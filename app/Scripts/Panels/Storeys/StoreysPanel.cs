using System.Globalization;
using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>
/// The map's storeys: the height ranges the storey filter steps through (PgUp, PgDn). Click one to show it. Storeys
/// found from the floors can be made the map's own, and then renamed, moved, added and removed here (names and heights
/// edit inline). Duplicate Above and Below copy everything on the selected storey to a new one on top of the highest
/// or under the lowest. Edits go through the <c>workspace</c> and <c>storey.duplicate</c> change ops, one undo step
/// each.
/// </summary>
[Panel("Storeys", DockSide.Left, Order = 4)]
public partial class StoreysPanel : EditorPanel
{
    private Tree _tree = null!;
    private Label _source = null!;
    private Button _own = null!;
    private Button _add = null!;
    private Button _remove = null!;
    private Control _empty = null!;
    private DeferredAction _rebuild = null!;

    private bool Own => Context.Project.Workspace.Storeys.Count > 0;

    protected override void Build()
    {
        _rebuild = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        PanelUi.Button(bar, "Show all", "Show every storey", () => StoreyFilter.Show(-1));
        // One button for both ways: the map's own storeys, or the ones found from its floors.
        _own = PanelUi.Button(bar, "Make own", "", () =>
        {
            if (Own)
            {
                SetRanges("Use detected storeys", []);
            }
            else
            {
                SetRanges("Make storeys the map's own", StoreyFilter.List.Select(AsRange));
            }
        });
        _add = PanelUi.IconButton(bar, "plus", "Add a storey above the highest one", AddAbove);
        _remove = PanelUi.IconButton(bar, "trash", "Remove the selected storey (the objects on it stay)", () =>
        {
            if (Selected() is { } i)
            {
                SetRanges($"Remove storey {StoreyFilter.List[i].Name}", StoreyFilter.List.Select(AsRange).Where((_, k) => k != i));
            }
        }, danger: true);
        HFlowContainer copy = PanelUi.Toolbar(this);
        PanelUi.Button(copy, "Duplicate above", "Copy everything on the selected storey to a new storey on top of the highest one",
            () => Duplicate(below: false));
        PanelUi.Button(copy, "Duplicate below", "Copy everything on the selected storey to a new storey under the lowest one",
            () => Duplicate(below: true));

        _source = PanelUi.Caption(this, "");
        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120),
        };
        PanelUi.Columns(_tree, ("Name", 0, true), ("Bottom", 64, false), ("Top", 64, false));
        _empty = PanelUi.EmptyState(_tree, "storeys", "No storeys yet", "Storeys are found from the map's floors: add some floor brushes.");

        _tree.ItemSelected += () =>
        {
            if (Selected() is { } i && i != StoreyFilter.Active)
            {
                StoreyFilter.Show(i);
            }
        };
        _tree.ItemEdited += Edited;
        AddChild(_tree);

        StoreyFilter.Changed += _rebuild.Queue;
        Context.ProjectChanged += _ => _rebuild.Queue();
    }

    public override void _ExitTree() => StoreyFilter.Changed -= _rebuild.Queue;

    public override void Refresh() => _rebuild.Queue();

    private int? Selected() => _tree.GetSelected() is { } item ? item.GetMetadata(0).AsInt32() : null;

    private void Rebuild()
    {
        IReadOnlyList<Storey> list = StoreyFilter.List;
        bool own = Own;
        _empty.Visible = list.Count == 0;
        _tree.ColumnTitlesVisible = list.Count > 0;
        _source.Visible = list.Count > 0;
        _source.Text = own
            ? "The map's own storeys. Click one to show it; double-click a name or height to change it."
            : "Found from the floors. Click one to show it; Make own to rename or move them.";
        _own.Text = own ? "Use detected" : "Make own";
        _own.TooltipText = own
            ? "Forget the map's own storeys and find them from the floors again"
            : "Keep the detected storeys as the map's own, so they can be renamed and moved";
        _own.Disabled = !own && list.Count == 0;
        _add.Disabled = list.Count == 0;
        _remove.Disabled = !own;

        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        for (int i = 0; i < list.Count; i++)
        {
            Storey s = list[i];
            TreeItem item = _tree.CreateItem(root);
            item.SetText(0, s.Name);
            item.SetText(1, Height(s.Bottom));
            item.SetText(2, Height(s.Top));
            item.SetTextAlignment(1, HorizontalAlignment.Right);
            item.SetTextAlignment(2, HorizontalAlignment.Right);
            item.SetMetadata(0, i);
            item.SetTooltipText(0, s.Floor is double f ? $"{s.Name}: floor at {Height(f)}" : s.Name);
            for (int column = 0; column < 3; column++)
            {
                // The open top of the highest detected storey has no height to type.
                item.SetEditable(column, own && (column < 2 || double.IsFinite(s.Top)));
            }

            if (i == StoreyFilter.Active)
            {
                item.Select(0);
            }
        }
    }

    /// <summary>A name or height typed into the list: the whole list goes back with that one change.</summary>
    private void Edited()
    {
        if (_tree.GetEdited() is not { } item)
        {
            return;
        }

        int index = item.GetMetadata(0).AsInt32();
        int column = _tree.GetEditedColumn();
        string text = item.GetText(column).Trim();
        List<StoreyRow> ranges = StoreyFilter.List.Select(AsRange).ToList();
        StoreyRow range = ranges[index];
        if (column == 0)
        {
            if (text.Length == 0)
            {
                _rebuild.Queue();
                return;
            }

            ranges[index] = range with { Name = text };
        }
        else if (PanelUi.TryNum(text, out double v))
        {
            ranges[index] = column == 1 ? range with { Bottom = v } : range with { Top = v };
        }
        else
        {
            Context.SetStatus($"'{text}' is not a height.");
            _rebuild.Queue();
            return;
        }

        SetRanges($"Edit storey {StoreyFilter.List[index].Name}", ranges);
    }

    private void AddAbove()
    {
        IReadOnlyList<Storey> list = StoreyFilter.List;
        if (list.Count == 0)
        {
            return;
        }

        Storey top = list[^1];
        double height = Storeys.StackHeight(list, list.Count - 1, Storeys.ContentTop(Context.Project, top));
        double bottom = top.Bottom + height;
        // The open top of a detected storey closes where the new one starts.
        List<StoreyRow> ranges = list.Select(AsRange).ToList();
        ranges[^1] = ranges[^1] with { Top = bottom };
        ranges.Add(new StoreyRow(Storeys.NewName(ranges.Select(r => r.Name).ToList()), bottom, bottom + height));
        SetRanges("Add storey", ranges);
    }

    private void Duplicate(bool below)
    {
        int? index = Selected() ?? (StoreyFilter.Active >= 0 ? StoreyFilter.Active : null);
        if (index is not { } i)
        {
            Context.SetStatus("Pick the storey to copy.");
            return;
        }

        SvJsonObject op = ApiJson.Obj(("op", ApiJson.Str("storey.duplicate")), ("storey", ApiJson.Str(StoreyFilter.List[i].Name)));
        if (below)
        {
            op.Add("below", ApiJson.Bool(true));
        }

        Apply($"Duplicate {StoreyFilter.List[i].Name} {(below ? "below" : "above")}", op);
    }

    /// <summary>One storey as the map's own; a detected open top is made tall enough for what stands on it.</summary>
    private StoreyRow AsRange(Storey s, int index)
    {
        double top = double.IsFinite(s.Top)
            ? s.Top
            : s.Bottom + Storeys.StackHeight(StoreyFilter.List, index, Storeys.ContentTop(Context.Project, s));
        return new StoreyRow(s.Name, s.Bottom, top);
    }

    private void SetRanges(string label, IEnumerable<StoreyRow> ranges) =>
        Apply(label, ApiJson.Obj(("op", ApiJson.Str("workspace")), ("storeys", ApiJson.Arr(ranges.Select(r => (SvJsonNode)ApiJson.Obj(
            ("name", ApiJson.Str(r.Name)), ("bottom", ApiJson.Num(r.Bottom)), ("top", ApiJson.Num(r.Top))))))));

    private void Apply(string label, SvJsonObject op)
    {
        SvJsonObject doc = ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)), ("label", ApiJson.Str(label)), ("ops", ApiJson.Arr([op])));
        ChangeResult result = ChangeApplier.Apply(Context.Session, doc);
        if (!result.Ok)
        {
            string error = ProposalBook.ErrorText(result.Json["error"] as SvJsonObject);
            Context.Log($"{label} failed: {error}", Severity.Error);
            Context.SetStatus($"{label} failed: {error}");
            return;
        }

        foreach (string warning in result.Warnings)
        {
            Context.Log($"{label}: {warning}", Severity.Warning);
        }

        Context.SetStatus(label);
    }

    /// <summary>A storey as the panel edits it before it goes to the <c>workspace</c> op.</summary>
    private sealed record StoreyRow(string Name, double Bottom, double Top);

    private static string Height(double z) => double.IsFinite(z) ? z.ToString("0.#", CultureInfo.InvariantCulture).Replace('-', '−') : "open";
}
