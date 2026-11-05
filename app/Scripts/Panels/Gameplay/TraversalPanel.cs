using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Traversal authoring: the ledge detector (settings, proposals to review and add), flushing and corner-joining
/// ledge grabs, the GE ID of selected GEs and the map's GE activation mask (which IDs are on when a round starts).
/// </summary>
[Panel("Traversal", DockSide.Workbench, Order = 12)]
public partial class TraversalPanel : EditorPanel
{
    private static readonly int[] s_ids = [0, 1, 2, 4, 8, 16, 32, 64, 128];

    private Tree _proposals = null!;
    private Label _detection = null!;
    private Label _counts = null!;
    private Label _selected = null!;
    private OptionButton _id = null!;
    private OptionButton _platform = null!;
    private CheckBox _meshes = null!;
    private Control _noProposals = null!;
    private readonly List<CheckBox> _mask = new();
    private DeferredAction _update = null!;
    private bool _updating;

    /// <summary>The proposal list's height with proposals in it, and while it shows its empty state.</summary>
    private const int ProposalsHeight = 180, EmptyProposalsHeight = 120;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        VBoxContainer box = PanelUi.Scroll(this);

        PanelUi.Header(box, "Ledge detection");
        Number(box, "Min drop", "Lower drops are steps the walk climbs", () => State.Options.MinDrop, v => State.Options = State.Options with { MinDrop = v });
        Number(box, "Min length", "Shorter lips are dropped unless they meet another at a corner", () => State.Options.MinLength,
            v => State.Options = State.Options with { MinLength = v });
        Number(box, "Climb headroom", "Room needed on top (0 accepts lips nobody can climb onto)", () => State.Options.ClimbHeadroom,
            v => State.Options = State.Options with { ClimbHeadroom = v });
        Number(box, "Probe step", "Distance between probes along an edge", () => State.Options.Step,
            v => State.Options = State.Options with { Step = Math.Max(1, v) });
        _meshes = new CheckBox { Text = "Static meshes too", TooltipText = "Detect on static meshes as well as brushes (baked maps)", ButtonPressed = State.Options.IncludeMeshes };
        _meshes.Toggled += on => State.Options = State.Options with { IncludeMeshes = on };
        box.AddChild(_meshes);

        HFlowContainer detect = PanelUi.Toolbar(box);
        PanelUi.Primary(detect, "Detect", "Find ledges in the whole map (in the selection's bounds when something is selected)",
            () => Context.Run("gameplay.ge.detect"));
        WorkbenchUi.Action(detect, "Add all", "Add an LG for every proposal", "gameplay.ge.apply", Context);
        WorkbenchUi.Guard(PanelUi.Button(detect, "Add chosen", "Add LGs for the proposals chosen in the list", AddChosen), () => _proposals.GetNextSelected(null) is not null);
        // Discard as a row action, so the three commands stay on one row in a narrow dock.
        WorkbenchUi.Guard(PanelUi.IconButton(detect, "trash", "Discard the proposals", () => Context.Run("gameplay.ge.discard"), danger: true),
            () => Context.FindAction("gameplay.ge.discard")?.IsEnabled() ?? false);
        _detection = PanelUi.Caption(box, "");
        _proposals = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Multi,
            CustomMinimumSize = new Vector2(0, ProposalsHeight),
        };
        PanelUi.Columns(_proposals, ("Ledge", 120, true), ("Length", 64, false), ("Drop", 56, false));
        _proposals.ItemActivated += () =>
        {
            if (Chosen().FirstOrDefault() is { } lip)
            {
                Context.JumpTo(lip.Mid);
            }
        };
        box.AddChild(_proposals);
        _noProposals = PanelUi.EmptyState(_proposals, "traversal", "No proposals", "Detect finds ledges a player can grab; review them here before adding.");

        PanelUi.Header(box, "Ledge grabs");
        HFlowContainer fix = PanelUi.Toolbar(box);
        WorkbenchUi.Action(fix, "Flush to edges", "Move the selected LGs (all, with nothing selected) onto their edges", "gameplay.ge.flush", Context);
        WorkbenchUi.Action(fix, "Join corners", "Move LG ends that nearly meet onto their corner", "gameplay.ge.join", Context);
        _counts = PanelUi.Caption(box, "");

        PanelUi.Header(box, "Selected GEs");
        HBoxContainer idRow = PanelUi.Row(box, "GE ID", "The ID that turns the selected GEs on and off (0: always on)");
        _id = PanelUi.Options(idRow, "The ID that turns the selected GEs on and off (0: always on)");
        _id.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (int id in s_ids)
        {
            _id.AddItem(id == 0 ? "0 (always on)" : id.ToString(CultureInfo.InvariantCulture), id);
        }

        WorkbenchUi.Guard(PanelUi.Button(idRow, "Set", "Give the selected GEs this ID",
            () => Context.Run("gameplay.ge.id." + _id.GetSelectedId().ToString(CultureInfo.InvariantCulture))), HasGes);
        HBoxContainer platformRow = PanelUi.Row(box, "Platform", "Every platform, PS2 only or Xbox only (kept for the original game)");
        _platform = PanelUi.Options(platformRow, "Every platform, PS2 only or Xbox only (kept for the original game)");
        _platform.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (string name in GeEdits.Platforms)
        {
            _platform.AddItem(name);
        }

        WorkbenchUi.Guard(PanelUi.Button(platformRow, "Set", "Give the selected GEs this platform",
            () => Context.Run("gameplay.ge.platform." + GeEdits.Platforms[_platform.Selected])), HasGes);
        _selected = PanelUi.Caption(box, "");

        Label mask = PanelUi.Header(box, "GE IDs on at round start");
        mask.TooltipText = "GEActivationMask: the IDs whose GEs are on when a round starts";
        mask.MouseFilter = MouseFilterEnum.Pass;
        var grid = new GridContainer { Columns = 4 };
        for (int bit = 0; bit < 8; bit++)
        {
            int value = 1 << bit;
            var check = new CheckBox { Text = value.ToString(CultureInfo.InvariantCulture) };
            check.Toggled += on =>
            {
                if (!_updating && on != ((GameplayPart.ActivationMask(Context.Project) & value) != 0))
                {
                    Context.Run("gameplay.ge.activation." + value.ToString(CultureInfo.InvariantCulture));
                }
            };
            grid.AddChild(check);
            _mask.Add(check);
        }

        box.AddChild(grid);

        Context.ProjectChanged += _ => _update.Queue();
        Context.SelectionChanged += _update.Queue;
        State.Changed += _update.Queue;
    }

    public override void Refresh() => _update.Queue();

    private static GameplayState State => GameplayState.Instance;

    private void Rebuild()
    {
        _updating = true;
        // Before a first run the proposal list's empty state says it all.
        _detection.Text = State.LastDetection;
        _detection.Visible = State.LastDetection.Length > 0;
        _proposals.Clear();
        _noProposals.Visible = State.Proposals.Count == 0;
        // Empty, the list is only as tall as its empty state, so the sections below follow it closely.
        _proposals.CustomMinimumSize = new Vector2(0, State.Proposals.Count == 0 ? EmptyProposalsHeight : ProposalsHeight);
        _proposals.ColumnTitlesVisible = State.Proposals.Count > 0;
        TreeItem root = _proposals.CreateItem();
        for (int i = 0; i < State.Proposals.Count; i++)
        {
            Lip lip = State.Proposals[i];
            TreeItem item = _proposals.CreateItem(root);
            item.SetText(0, string.Create(CultureInfo.InvariantCulture, $"({lip.Mid.X:0}, {lip.Mid.Y:0}, {lip.Mid.Z:0})"));
            item.SetText(1, lip.Length.ToString("0", CultureInfo.InvariantCulture));
            item.SetText(2, lip.Drop >= 8000 ? "Open" : lip.Drop.ToString("0", CultureInfo.InvariantCulture));
            item.SetTextAlignment(1, HorizontalAlignment.Right);
            item.SetTextAlignment(2, HorizontalAlignment.Right);
            item.SetMetadata(0, i);
        }

        MapProject p = Context.Project;
        _counts.Text = p.Ges.Count == 0 ? "No GEs." : string.Join(" · ", p.Ges.GroupBy(g => g.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
        List<Ge> selected = Context.Selection.Items.Where(r => r.Kind == ObjectKind.Ge).Select(r => p.FindGe(r.Id)).OfType<Ge>().ToList();
        _selected.Text = selected.Count == 0 ? "No GEs selected."
            : $"{selected.Count} selected · IDs {string.Join(", ", selected.Select(g => g.Mask).Distinct().Order())} · platforms " +
              string.Join(", ", selected.Select(g => g.Platform).Distinct().Order().Select(PlatformName));
        _meshes.SetPressedNoSignal(State.Options.IncludeMeshes);
        int mask = GameplayPart.ActivationMask(p);
        for (int bit = 0; bit < _mask.Count; bit++)
        {
            _mask[bit].ButtonPressed = (mask & (1 << bit)) != 0;
        }

        _updating = false;
    }

    private bool HasGes() => Context.Selection.Items.Any(r => r.Kind == ObjectKind.Ge);

    private static string PlatformName(int p) => p >= 0 && p < GeEdits.Platforms.Count ? GeEdits.Platforms[p] : p.ToString(CultureInfo.InvariantCulture);

    private List<Lip> Chosen()
    {
        var chosen = new List<Lip>();
        for (TreeItem? item = _proposals.GetNextSelected(null); item is not null; item = _proposals.GetNextSelected(item))
        {
            int i = item.GetMetadata(0).AsInt32();
            if (i < State.Proposals.Count)
            {
                chosen.Add(State.Proposals[i]);
            }
        }

        return chosen;
    }

    private void AddChosen()
    {
        List<Lip> chosen = Chosen();
        Context.SetStatus(chosen.Count == 0 ? "Choose proposals in the list first" : $"Added {State.Apply(chosen)} ledge grabs");
    }

    private void Number(Control parent, string name, string tip, Func<double> get, Action<double> set)
    {
        // The docks' label column, the field filling the rest, as in the Inspector.
        HBoxContainer row = PanelUi.Row(parent, name, tip);
        SpinBox spin = PanelUi.Spin(row, 0, 4096, 1, get(), tip);
        spin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        spin.Suffix = "u";
        spin.ValueChanged += v => set(v);
    }
}
