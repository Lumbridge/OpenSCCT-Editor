using Godot;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Import Report: what the import that made the open map did, in parts (what came in, cooked out, lifted, recovered or
/// rebuilt, dropped, warnings) instead of a run of log lines. It comes to the front after an import. Each part is a
/// group row with its count; warnings carry their severity icon. Clicking a line selects its objects; double-clicking
/// frames them. The project keeps the import's findings, so a map opened from disk shows the same report.
/// </summary>
[Panel("Import Report", DockSide.Bottom, Order = 6)]
public partial class ImportReportPanel : EditorPanel
{
    /// <summary>The objects column's width (a count such as "1,698 objects").</summary>
    private const int ObjectsColumn = 112;

    private readonly Dictionary<TreeItem, ImportReportLine> _lines = new();
    private Tree _tree = null!;
    private Label _summary = null!;
    private Button _copy = null!;
    private Control _empty = null!;
    private ImportReport? _report;
    private bool _kept;

    protected override void Build()
    {
        HFlowContainer bar = PanelUi.Toolbar(this);
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        bar.AddChild(row);
        _summary = PanelUi.Caption(row, "", wrap: false);
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _summary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _summary.ClipText = true;
        _summary.CustomMinimumSize = Vector2.Zero;
        _summary.MouseFilter = MouseFilterEnum.Pass;
        _copy = PanelUi.IconButton(row, "copy", "Copy report: the import report as text", () =>
        {
            if (_report is not null)
            {
                PanelUi.Copy(_report.ToText());
                Context.SetStatus("Copied the import report.");
            }
        });

        _tree = new Tree
        {
            Name = "Tree",
            Columns = 2,
            HideRoot = true,
            ColumnTitlesVisible = false,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _tree.SetColumnExpand(0, true);
        _tree.SetColumnClipContent(0, true);
        _tree.SetColumnExpand(1, false);
        _tree.SetColumnCustomMinimumWidth(1, ObjectsColumn);
        _tree.ItemSelected += SelectObjects;
        _tree.ItemActivated += FocusObjects;
        AddChild(_tree);
        _empty = PanelUi.EmptyState(_tree, "import", "No import report",
            "This map was made here. Importing a T3D, SVMap or .sdc map shows what came in.");
    }

    public override void Refresh()
    {
        MapProject p = Context.Project;
        bool fresh = Context.ImportReport is not null;
        _report = Context.ImportReport ?? (p.Source.Kind != "authored" ? ImportReport.Build(p, null) : null);
        _kept = Context.ImportReport is not null || p.Source.Findings is not null;
        Rebuild();
        if (fresh && GetParent() is TabContainer dock)
        {
            dock.CurrentTab = GetIndex();
        }
    }

    private void Rebuild()
    {
        _tree.Clear();
        _lines.Clear();
        TreeItem root = _tree.CreateItem();
        _empty.Visible = _report is null;
        _copy.Disabled = _report is null;
        if (_report is null)
        {
            _summary.Text = "Not imported";
            _summary.TooltipText = "";
            return;
        }

        int warnings = _report.Sections.Sum(s => s.Lines.Count(l => l.Severity != Severity.Info));
        string from = _report.File is { } file ? Path.GetFileName(file) : _report.Kind;
        _summary.Text = $"{_report.MapId} from {from}"
            + (warnings > 0 ? $" · {PanelUi.Count(warnings, "warning")}" : "")
            + (_kept ? "" : " · only what the project records");
        _summary.TooltipText = $"{_report.MapId} from {_report.File ?? _report.Kind}"
            + (_kept ? "" : "\nImported before the editor kept the import's findings: only what the project itself records.");
        foreach (ImportReportSection section in _report.Sections)
        {
            // A part is a group row: semibold heading text and its count, never the accent.
            TreeItem head = _tree.CreateItem(root);
            head.SetText(0, section.Title);
            head.SetTooltipText(0, section.Title);
            head.SetCustomFont(0, EditorTheme.UiFontSemibold);
            head.SetText(1, PanelUi.Int(section.Lines.Count));
            head.SetTextAlignment(1, HorizontalAlignment.Right);
            for (int c = 0; c < _tree.Columns; c++)
            {
                head.SetSelectable(c, false);
                head.SetCustomColor(c, c == 0 ? EditorTheme.TextHeading : EditorTheme.TextDim);
            }

            // Long parts (the cooked-out classes, the notes) start folded.
            head.Collapsed = section.Lines.Count > 12 || section.Key == ImportReport.Notes;
            foreach (ImportReportLine line in section.Lines)
            {
                TreeItem item = _tree.CreateItem(head);
                item.SetText(0, PanelUi.Sentence(line.Text));
                item.SetAutowrapMode(0, TextServer.AutowrapMode.WordSmart);
                item.SetTooltipText(0, line.Text);
                if (line.Severity != Severity.Info)
                {
                    // The icon carries the severity; the text stays body text.
                    item.SetIcon(0, PanelIcons.Severity(line.Severity));
                }

                if (line.Objects.Count > 0)
                {
                    item.SetText(1, PanelUi.Count(line.Objects.Count, "object"));
                    item.SetTextAlignment(1, HorizontalAlignment.Right);
                    item.SetCustomColor(1, EditorTheme.TextDim);
                    item.SetTooltipText(1, "Click to select, double‑click to frame:\n"
                        + string.Join(", ", line.Objects.Take(12).Select(o => o.Id)) + (line.Objects.Count > 12 ? ", …" : ""));
                }

                _lines[item] = line;
            }
        }
    }

    private List<ObjectRef> Existing() =>
        _tree.GetSelected() is { } item && _lines.TryGetValue(item, out ImportReportLine? line)
            ? line.Objects.Where(Context.Project.Exists).ToList()
            : [];

    private void SelectObjects()
    {
        List<ObjectRef> objects = Existing();
        if (objects.Count > 0)
        {
            Context.Selection.Set(objects);
            Context.SetStatus($"Selected {PanelUi.Count(objects.Count, "object")} from the import report.");
        }
    }

    private void FocusObjects()
    {
        List<ObjectRef> objects = Existing();
        if (objects.Count > 0)
        {
            Context.Focus(objects);
        }
    }
}
