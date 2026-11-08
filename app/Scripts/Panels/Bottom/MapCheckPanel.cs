using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Map Check: the findings of the last check or build with severity icons, severity chips with counts and a text
/// filter. Clicking a finding selects its objects; double-clicking frames them (or jumps to its location). "Run"
/// checks the map for OpenSCCT or for export to the original game; "Deep" also compiles it for the deep rules (in the
/// background). "Fix" applies the selected finding's fix as one undo step.
/// </summary>
[Panel("Map Check", DockSide.Bottom, Order = 0)]
public partial class MapCheckPanel : EditorPanel
{
    /// <summary>The code column's narrowest width (it widens to the longest code shown).</summary>
    private const int CodeColumnMin = 210;

    private readonly Dictionary<TreeItem, Finding> _findings = new();
    private readonly bool[] _show = [true, true, true];
    private readonly SeverityChip[] _toggles = new SeverityChip[3];
    private Tree _tree = null!;
    private OptionButton _target = null!;
    private Button _run = null!;
    private Button _fix = null!;
    private Label _summary = null!;
    private Control _empty = null!;
    private Control _clean = null!;
    private string[] _words = [];

    protected override void Build()
    {
        // Groups keep their parts together when a narrow dock wraps the toolbar: the run controls, then the filters.
        HFlowContainer bar = PanelUi.Toolbar(this);
        HBoxContainer run = Group(bar);
        _run = PanelUi.Primary(run, "Run check", "Check the map now", RunCheck);
        PanelUi.Button(run, "Deep check",
            "Also compile the map and run the deep rules (zone leaks, actors in the void, floating meshes, mesh collision caps, reachability)",
            () => Context.Run("analysis.check"));
        // The rules are a setting, not a command: set apart from the two checks.
        bar.AddChild(new VSeparator());
        HBoxContainer rules = Group(bar);
        PanelUi.Label(rules, "Rules", EditorTheme.TextDim);
        _target = PanelUi.Options(rules, "Which game's limits the check applies");
        _target.AddItem("OpenSCCT", (int)TargetGame.OpenScct);
        _target.AddItem("Original export", (int)TargetGame.Original);
        _target.CustomMinimumSize = new Vector2(EditorTheme.SearchMinWidth + EditorTheme.SpaceXl, 0);
        _target.Select(0);
        _fix = PanelUi.Button(bar, "Apply fix", "Apply the selected finding's fix (one undo step)", ApplyFix);
        _fix.Disabled = true;
        HBoxContainer chips = Group(bar);
        foreach (Severity s in new[] { Severity.Error, Severity.Warning, Severity.Info })
        {
            int index = (int)s;
            _toggles[index] = PanelUi.SeverityToggle(chips, s, () => Context.Findings.Count(f => f.Severity == s), true, on =>
            {
                _show[index] = on;
                Rebuild();
            });
        }

        HBoxContainer end = Group(bar);
        end.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        LineEdit filter = PanelUi.Search(end, "Filter\u2026", t =>
        {
            _words = TextMatch.Words(t);
            Rebuild();
        }, "Filter the findings by code, message or object");
        filter.CustomMinimumSize = new Vector2(EditorTheme.SearchMinWidth / 2, 0);
        PanelUi.IconButton(end, "copy", "Copy findings: the shown findings as text", CopyShown);
        // The status after a divider, so the copy button does not read as part of it.
        end.AddChild(new VSeparator());
        _summary = PanelUi.Caption(end, "", wrap: false);
        PanelMarks.Status(_summary);

        _tree = new Tree
        {
            Name = "Tree",
            Columns = 3,
            HideRoot = true,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _tree.SetColumnTitle(0, "Code");
        _tree.SetColumnTitle(1, "Finding");
        _tree.SetColumnTitle(2, "Objects");
        _tree.SetColumnExpand(0, false);
        _tree.SetColumnCustomMinimumWidth(0, CodeColumnMin);
        _tree.SetColumnExpandRatio(1, 3);
        _tree.ItemSelected += SelectObjects;
        _tree.ItemActivated += FocusObjects;
        PanelUi.DimColumn(_tree, 0);
        AddChild(_tree);
        for (int c = 0; c < _tree.Columns; c++)
        {
            _tree.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _tree.SetColumnClipContent(c, true);
        }

        // Short enough for the bottom dock's default height; Run check is the toolbar's primary button.
        _empty = PanelUi.EmptyState(_tree, "search", "No check run yet", "Run check to look for problems.");
        _clean = PanelUi.EmptyState(_tree, "success", "No problems found", "The last check found nothing to fix.");

        Context.FindingsChanged += Rebuild;
        Context.AddAction(new EditorAction("mapcheck.run", "Run Map Check", "Build", RunCheck)
        {
            IsEnabled = () => EditorServices.Checker is not null,
            Order = 50,
            Description = "Check the map with the rules chosen in the Map Check panel",
        });
    }

    public override void Refresh()
    {
        _run.Disabled = EditorServices.Checker is null;
        _run.TooltipText = EditorServices.Checker is null ? "No map checker is loaded" : "Check the map now";
        Rebuild();
    }

    private void RunCheck()
    {
        if (EditorServices.Checker is not { } checker)
        {
            Context.Log("Map Check is not available: no checker is loaded.", Severity.Warning);
            return;
        }

        var options = new CheckOptions { Target = (TargetGame)_target.GetSelectedId(), ProjectFolder = Context.Session.Folder };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<Finding> findings = checker.Check(Context.Project, options);
        Context.SetFindings(findings);
        int errors = findings.Count(f => f.Severity == Severity.Error);
        int warnings = findings.Count(f => f.Severity == Severity.Warning);
        Context.Log($"Map Check ({options.Target}): {errors} errors, {warnings} warnings, {findings.Count - errors - warnings} notes in {watch.ElapsedMilliseconds} ms.",
            errors > 0 ? Severity.Error : warnings > 0 ? Severity.Warning : Severity.Info);
    }

    private void Rebuild()
    {
        _tree.Clear();
        _findings.Clear();
        TreeItem root = _tree.CreateItem();
        MapProject p = Context.Project;
        IReadOnlyList<Finding> all = Context.Findings;
        int shown = 0;
        float widestCode = 0;
        foreach (Finding f in all.OrderBy(f => f.Severity).ThenBy(f => f.Code, StringComparer.Ordinal))
        {
            if (!_show[(int)f.Severity] || (_words.Length > 0 && !TextMatch.AllWords(_words, $"{f.Code} {f.Message} {string.Join(' ', f.Objects.Select(o => o.Id))}")))
            {
                continue;
            }

            TreeItem item = _tree.CreateItem(root);
            item.SetIcon(0, PanelIcons.Severity(f.Severity));
            item.SetText(0, f.Code);
            item.SetText(1, PanelUi.Sentence(f.Message));
            if (OpenScct.Editor.Core.Interop.Sdc.SdcExportReport.RequiresAction(f))
                item.SetCustomFont(1, EditorTheme.UiFontSemibold);
            // Messages are the point of the list: in a narrow bottom dock they wrap rather than lose their end.
            item.SetAutowrapMode(1, TextServer.AutowrapMode.WordSmart);
            item.SetTooltipText(1, f.Fix is { } fix ? $"{f.Message}\nFix: {fix.Label}" : f.Message);
            item.SetText(2, string.Join(", ", f.Objects.Take(6).Select(o => o.Id)) + (f.Objects.Count > 6 ? $" +{f.Objects.Count - 6}" : ""));
            item.SetTooltipText(2, string.Join("\n", f.Objects.Select(o => ObjectInfo.Describe(p, o))));
            // The icon carries the severity; the code is an identifier, dim and fixed-width.
            item.SetCustomColor(0, EditorTheme.TextDim);
            item.SetCustomFont(0, EditorTheme.MonoFont);
            item.SetCustomFontSize(0, EditorTheme.FontSizeSmall);
            item.SetTooltipText(0, $"{PanelUi.SeverityNoun(f.Severity)}: {f.Code}");
            widestCode = Math.Max(widestCode, EditorTheme.MonoFont.GetStringSize(f.Code, fontSize: EditorTheme.FontSizeSmall).X);
            if (f.Objects.Count > 0 && !f.Objects.Any(p.Exists))
            {
                item.SetCustomColor(2, EditorTheme.TextDim);
            }

            _findings[item] = f;
            shown++;
        }

        // The code column fits the longest code shown (the codes are what a reader looks up), within reason: the
        // finding text keeps most of a narrow dock.
        float codeColumn = widestCode + EditorTheme.IconSize + 3 * EditorTheme.SpaceS;
        _tree.SetColumnCustomMinimumWidth(0, (int)Math.Clamp(codeColumn, CodeColumnMin, Math.Max(CodeColumnMin, _tree.Size.X * 0.3f)));

        foreach (SeverityChip chip in _toggles)
        {
            chip.Refresh();
        }

        _empty.Visible = !Context.FindingsChecked;
        _clean.Visible = Context.FindingsChecked && all.Count == 0;
        _tree.ColumnTitlesVisible = all.Count > 0;
        _summary.Text = !Context.FindingsChecked ? "Not checked yet"
            : all.Count == 0 ? "No findings"
            : shown == all.Count ? PanelUi.Count(all.Count, "finding") : $"{shown} of {PanelUi.Count(all.Count, "finding")} shown";
        _summary.TooltipText = _summary.Text;
    }

    /// <summary>A row of toolbar controls that wraps as one piece.</summary>
    private static HBoxContainer Group(Control bar)
    {
        var group = new HBoxContainer();
        group.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        bar.AddChild(group);
        return group;
    }

    private Finding? Current() => _tree.GetSelected() is { } item ? _findings.GetValueOrDefault(item) : null;

    private void ApplyFix()
    {
        if (Current() is not { Fix: { } fix } f)
        {
            return;
        }

        if (!fix.IsCurrent(Context.Project))
        {
            Context.Log($"Not fixed: {string.Join(", ", fix.Was.Select(w => w.Id))} moved since the check worked out \"{fix.Label}\". Run the check again.",
                Severity.Warning);
            return;
        }

        ChangeResult r = ChangeApplier.Apply(Context.Session, fix.Changes, author: "map check");
        if (!r.Ok)
        {
            Context.Log($"Could not fix {f.Code}: {ProposalBook.ErrorText(r.Json["error"] as SvJsonObject)}", Severity.Error);
            return;
        }

        Context.RemoveFinding(f);
        Context.Log($"Fixed {f.Code}: {fix.Label}");
    }

    private void SelectObjects()
    {
        _fix.Disabled = Current()?.Fix is null;
        if (Current() is { } f)
        {
            var existing = f.Objects.Where(Context.Project.Exists).ToList();
            if (existing.Count > 0)
            {
                Context.Selection.Set(existing);
            }

            // The finding as a sentence; its severity, code and objects are in its row.
            Context.SetStatus(f.Message);
        }
    }

    private void FocusObjects()
    {
        if (Current() is not { } f)
        {
            return;
        }

        var existing = f.Objects.Where(Context.Project.Exists).ToList();
        if (existing.Count > 0)
        {
            Context.Focus(existing);
        }
        else if (f.Location is { } at)
        {
            Context.JumpTo(at);
        }
    }

    private void CopyShown()
    {
        string text = string.Join('\n', _findings.Values.Select(f => f.ToString()));
        PanelUi.Copy(text);
        Context.SetStatus($"Copied {_findings.Count} findings.");
    }
}
