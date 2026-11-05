using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Help paths and zone tips: every hint cylinder with its text, the selected tip's spy and merc hints, size and flags,
/// and placing new tips with their info marker (shown only with in-game help, as in the stock maps; the map's own marker
/// mesh, else the stock help_path_INFO).
/// </summary>
[Panel("Tips", DockSide.Workbench, Order = 9)]
public partial class TipsPanel : EditorPanel
{
    private ItemList _list = null!;
    private Control _empty = null!;
    private Label _summary = null!;
    private VBoxContainer _edit = null!;
    private LineEdit _spy = null!, _merc = null!;
    private SpinBox _radius = null!, _height = null!, _delay = null!;
    private CheckBox _onlyWithHelp = null!, _stay = null!;
    private HBoxContainer _delayRow = null!;
    private DeferredAction _update = null!;
    private List<ZoneTip> _tips = [];
    private string? _id;
    private bool _updating;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        PanelUi.Button(bar, "Add tip with marker", "A hint cylinder with an info marker below it", () => Context.Run("gameplay.tip.add")).Icon =
            Icons.Get("plus", EditorTheme.Text);
        PanelUi.Button(bar, "Add tip", "A hint cylinder only", () => Context.Run("gameplay.tip.addOnly"));
        // The list and the selected tip's form share the height, the form the larger part (it scrolls); the list
        // keeps three rows at least.
        _list = new ItemList
        {
            CustomMinimumSize = new Vector2(0, 3 * EditorTheme.RowHeight + 2 * EditorTheme.SpaceM),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.4f,
        };
        _list.ItemSelected += i =>
        {
            if (!_updating && i < _tips.Count)
            {
                Context.Selection.Set([ObjectRef.Actor(_tips[(int)i].Id)]);
            }
        };
        _list.ItemActivated += _ => Context.Focus(Context.Selection.Items);
        AddChild(_list);
        _empty = PanelUi.EmptyState(_list, "type-tip", "No zone tips", "A zone tip shows a hint while a player stands in its cylinder. Add one at the view centre.");

        _edit = PanelUi.Scroll(this);
        PanelUi.ScrollOf(_edit).SizeFlagsStretchRatio = 0.6f;
        PanelUi.Header(_edit, "Selected tip");
        _spy = Text("Spy hint", "What a spy reads while standing in the cylinder");
        _merc = Text("Merc hint", "What a mercenary reads (the stock maps use the same text)");
        _radius = Number("Radius", "Cylinder radius in units", 1, 4096);
        _height = Number("Half height", "Cylinder half height in units", 1, 4096);
        _onlyWithHelp = Check("Only with in-game help", "Shown only when the player's in-game help option is on (Visit a Map)");
        _stay = Check("Stays after leaving", "Keep the hint on the HUD after the player walks out");
        _delayRow = PanelUi.Row(_edit, "End delay", "Seconds before the game ends once touched (SEndZoneTip)");
        _delay = PanelUi.Spin(_delayRow, 0, 600, 1, 0, "Seconds before the game ends once touched (SEndZoneTip)");
        _delay.Suffix = "s";
        _delay.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _delay.ValueChanged += v => Apply(() => HelpTips.Set(Context.Project, _id!, endGameDelay: (int)v));
        _summary = PanelUi.Caption(this, "", wrap: false);

        Context.ProjectChanged += _ => _update.Queue();
        Context.SelectionChanged += _update.Queue;
    }

    public override void Refresh() => _update.Queue();

    private LineEdit Text(string name, string tip)
    {
        HBoxContainer row = PanelUi.Row(_edit, name, tip);
        var e = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        e.TextSubmitted += _ => Apply(() => HelpTips.Set(Context.Project, _id!, spy: _spy.Text, merc: _merc.Text));
        row.AddChild(e);
        return e;
    }

    private SpinBox Number(string name, string tip, double min, double max)
    {
        HBoxContainer row = PanelUi.Row(_edit, name, tip);
        SpinBox s = PanelUi.Spin(row, min, max, 1, min, tip);
        s.Suffix = "u";
        s.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        s.ValueChanged += _ => Apply(() => HelpTips.Set(Context.Project, _id!, radius: _radius.Value, height: _height.Value));
        return s;
    }

    private CheckBox Check(string text, string tip)
    {
        var c = new CheckBox { Text = text, TooltipText = tip };
        c.Toggled += _ => Apply(() => HelpTips.Set(Context.Project, _id!, onlyWithHelp: _onlyWithHelp.ButtonPressed, stayPermanently: _stay.ButtonPressed));
        _edit.AddChild(c);
        return c;
    }

    private void Rebuild()
    {
        _updating = true;
        _tips = HelpTips.Tips(Context.Project);
        string mesh = HelpTips.DefaultMarkerMesh(Context.Project, () => AssetService.Instance.Catalog) ?? "none found: give one with ose tips add --mesh";
        _summary.Text = $"{PanelUi.Count(_tips.Count, "zone tip")} · {PanelUi.Count(HelpTips.Markers(Context.Project).Count, "help path marker")} · new markers use {mesh}";
        _summary.TooltipText = _summary.Text;
        _empty.Visible = _tips.Count == 0;
        string? selected = WorkbenchPart.Selected(Context, HelpTips.IsTip).FirstOrDefault()?.Id;
        _list.Clear();
        ZoneTip? current = null;
        foreach (ZoneTip t in _tips)
        {
            // A tip reads as its hint; its actor id is in the tooltip.
            string hint = t.Spy.Length > 0 ? t.Spy : t.Id;
            int i = _list.AddItem(t.EndsGame ? $"Ends game · {hint}" : hint, PanelIcons.Type("SZoneTips"));
            _list.SetItemTooltip(i, $"{t.Id}\n" + (t.Spy == t.Merc ? t.Spy : $"Spy: {t.Spy}\nMerc: {t.Merc}"));
            if (string.Equals(t.Id, selected, StringComparison.OrdinalIgnoreCase))
            {
                _list.Select(i);
                current = t;
            }
        }

        _id = current?.Id;
        PanelUi.ScrollOf(_edit).Visible = current is not null;
        if (current is { } c)
        {
            _spy.Text = c.Spy;
            _merc.Text = c.Merc;
            _radius.SetValueNoSignal(c.Radius);
            _height.SetValueNoSignal(c.Height);
            _onlyWithHelp.SetPressedNoSignal(c.OnlyWithHelp);
            _stay.SetPressedNoSignal(c.StayPermanently);
            _delayRow.Visible = c.EndsGame;
            _delay.SetValueNoSignal(c.EndGameDelay);
        }

        _updating = false;
    }

    private void Apply(Func<OpenScct.Editor.Core.Commands.ReplaceObjectsCommand> edit)
    {
        if (_updating || _id is null)
        {
            return;
        }

        try
        {
            if (edit() is { IsEmpty: false } cmd)
            {
                Context.Execute(cmd);
            }
        }
        catch (ArgumentException ex)
        {
            Context.SetStatus(ex.Message);
        }
    }
}
