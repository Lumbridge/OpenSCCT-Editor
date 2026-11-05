using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// The SMagicEvent workbench (the stock event sequencer) for the selected SMagicEvent: its groups of actions on a
/// timeline (event, delay, type, which activation it answers; drag an action along the timeline to change its delay),
/// repeat and sequence, the StopActor (picked from the actors that fire it), and a preview that plays activations
/// through the game's logic, marks in the views what has fired and moves the movers it triggers.
/// </summary>
[Panel("Event Sequencer", DockSide.Bottom, Order = 4)]
public partial class MagicEventPanel : EditorPanel
{
    private static readonly string[] s_types = ["Trigger", "Untrigger", "Trigger then untrigger", "Untrigger then trigger"];

    /// <summary>The event a new action fires until it is given the Tag of what it should reach.</summary>
    private const string NewEvent = "NewEvent";

    /// <summary>The width of an action's delay field.</summary>
    private const int DelayWidth = 64;

    /// <summary>The longest delay an action's box takes, in seconds.</summary>
    private const double MaxDelay = 3600;

    private Label _title = null!;
    private Control _empty = null!;
    private Control _scroll = null!;
    private VBoxContainer _body = null!;
    private VBoxContainer _groups = null!;
    private LineEdit _stop = null!;
    private MenuButton _stopPick = null!;
    private LineEdit _activations = null!;
    private MagicTimelineView _timeline = null!;
    private Label _firings = null!;
    private DeferredAction _update = null!;
    private string? _id;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        WorkbenchUi.Action(bar, "New from selection", "A new SMagicEvent firing the selected actors", "gameplay.magic.add", Context).Icon =
            Icons.Get("plus", EditorTheme.Text);
        WorkbenchUi.Action(bar, "Copy timeline", "Copy the timeline as JSON in the RE+ exchange format", "gameplay.magic.copyJson", Context).Icon =
            Icons.Get("copy", EditorTheme.Text);
        WorkbenchUi.Action(bar, "Paste timeline", "Replace the timeline with one from the clipboard (JSON in the RE+ exchange format)", "gameplay.magic.pasteJson", Context).Icon =
            Icons.Get("paste", EditorTheme.Text);
        _title = PanelUi.Caption(bar, "", wrap: false);
        _empty = PanelUi.EmptyState(this, "type-event", "No event selected",
            "Select a magic event (SMagicEvent) to edit its timeline, or select the actors it should fire and press New from selection.");
        VBoxContainer box = PanelUi.Scroll(this);
        _scroll = PanelUi.ScrollOf(box);
        _body = new VBoxContainer();
        box.AddChild(_body);

        HBoxContainer stopRow = PanelUi.Row(_body, "Stop actor", "A trigger from this actor clears every action still waiting");
        _stop = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "Not set" };
        _stop.TextSubmitted += _ => SetStop();
        _stop.FocusExited += SetStop;
        stopRow.AddChild(_stop);
        // The StopActor is compared with the actor that triggers the event, so only its senders can be one.
        _stopPick = WorkbenchUi.DropButton(stopRow, "Senders", "Pick the stop actor from the actors that fire this event");
        _stopPick.AboutToPopup += FillStopPick;
        _stopPick.GetPopup().IndexPressed += i =>
        {
            _stop.Text = i == 0 ? "" : _stopPick.GetPopup().GetItemText((int)i);
            SetStop();
        };
        _groups = new VBoxContainer();
        _groups.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        _body.AddChild(_groups);
        HFlowContainer addGroup = PanelUi.Toolbar(_body);
        PanelUi.Button(addGroup, "Add group", "A group answers activations on its own: all its actions, or one per activation (sequence)", () =>
            Edit(g => g.Add(new MagicGroup([new MagicAction(NewEvent, 0)])))).Icon = Icons.Get("plus", EditorTheme.Text);

        PanelUi.Header(_body, "Preview");
        HBoxContainer playRow = PanelUi.Row(_body, "Activations", "Seconds of each trigger, comma separated; t:untrigger for an untrigger, t:stop for a trigger from the stop actor");
        _activations = new LineEdit { Text = "0", SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "0, 2.5, 4:untrigger, 6:stop" };
        _activations.TextSubmitted += _ => Play(false);
        playRow.AddChild(_activations);
        PanelUi.Segmented(playRow, [("Play", "Play the activations; the views mark what has fired"), ("Pause", "Pause the preview"), ("Stop", "End the preview")], -1, i =>
        {
            switch (i)
            {
                case 0:
                    Play(true);
                    break;
                case 1:
                    MagicPreview.Playing = false;
                    break;
                default:
                    Context.Run("gameplay.magic.stopPreview");
                    break;
            }
        });
        _timeline = new MagicTimelineView { CustomMinimumSize = new Vector2(0, EditorTheme.LaneTimelineHeight) };
        _timeline.Seek += t =>
        {
            if (MagicPreview.Id is null)
            {
                Play(false);
            }

            MagicPreview.Playing = false;
            MagicPreview.Seek(t);
        };
        _timeline.ActionMoved += (g, i, delay) => SetAction(g, i, x => x with { Delay = delay });
        _body.AddChild(_timeline);
        _firings = PanelUi.Caption(_body, "");

        Context.ProjectChanged += _ => _update.Queue();
        Context.SelectionChanged += _update.Queue;
        MagicPreview.Changed += ShowPreview;
    }

    public override void Refresh() => _update.Queue();

    public override void _Process(double delta) => MagicPreview.Advance(delta);

    private void Rebuild()
    {
        Actor? magic = WorkbenchPart.Selected(Context, MagicEvents.IsMagicEvent).FirstOrDefault();
        _id = magic?.Id;
        _scroll.Visible = magic is not null;
        _empty.Visible = magic is null;
        if (magic is null)
        {
            _title.Text = _title.TooltipText = "";
            return;
        }

        List<MagicGroup> groups = MagicEvents.Read(magic);
        _title.Text = _title.TooltipText = string.Create(CultureInfo.InvariantCulture,
            $"{magic.Id} · fired by Tag {magic.Tag} · {PanelUi.Count(groups.Count, "group")} · {PanelUi.Count(groups.Sum(g => g.Actions.Count), "action")} over {MagicEvents.Length(groups):0.##} s");
        _stop.Text = PropValues.ActorId(magic.Props["StopActor"]) ?? "";
        foreach (Node child in _groups.GetChildren())
        {
            child.QueueFree();
        }

        GameplayGraph graph = GameplayState.Instance.Graph;
        for (int g = 0; g < groups.Count; g++)
        {
            GroupRows(groups[g], g, graph);
        }

        _timeline.Groups = groups;
        if (MagicPreview.Id is { } previewed && !string.Equals(previewed, magic.Id, StringComparison.OrdinalIgnoreCase))
        {
            MagicPreview.Stop();
        }
        else if (MagicPreview.Id is not null)
        {
            Play(false);
        }

        ShowPreview();
    }

    private void GroupRows(MagicGroup group, int g, GameplayGraph graph)
    {
        var head = new HBoxContainer();
        _groups.AddChild(head);
        head.AddChild(new Label { Text = $"Group {g + 1}", ThemeTypeVariation = "GroupLabel", TooltipText = $"Group {g} in the game's lists" });
        head.AddChild(new VSeparator());
        head.AddChild(new Label { Text = "Repeat", ThemeTypeVariation = "Dim", TooltipText = "0: every time; n: n more cycles; -1: off", MouseFilter = MouseFilterEnum.Pass });
        SpinBox repeat = PanelUi.Spin(head, -1, 999, 1, group.Repeat, "0: every time; n: n more cycles; -1: off", DelayWidth);
        // Steps of the spin box in a row make one undo step.
        repeat.ValueChanged += v => Edit(list => list[g] = list[g] with { Repeat = (int)v }, $"magic.repeat.{_id}.{g}");
        var sequence = new CheckBox { Text = "Sequence", TooltipText = "One action per activation, in turn (sequence); off: every action each time", ButtonPressed = group.Sequence };
        sequence.Toggled += on => Edit(list => list[g] = list[g] with { Sequence = on });
        head.AddChild(sequence);
        WorkbenchUi.Spacer(head);
        PanelUi.Button(head, "Add action", "A copy of the last action one second later", () => Edit(list => list[g] = list[g] with
        {
            Actions = [.. list[g].Actions, list[g].Actions.Count > 0 ? list[g].Actions[^1] with { Delay = list[g].Actions[^1].Delay + 1 } : new MagicAction(NewEvent, 0)],
        })).Icon = Icons.Get("plus", EditorTheme.Text);
        PanelUi.IconButton(WorkbenchUi.Actions(head), "trash", "Delete this group and its actions", () => Edit(list => list.RemoveAt(g)), danger: true);

        if (group.Actions.Count > 0)
        {
            // Column titles over the action rows, in the rows' widths.
            var titles = new HBoxContainer();
            _groups.AddChild(titles);
            Titled(titles, "Fires (Tag)", "The Tag each action fires", 2, 0);
            Titled(titles, "Delay", "Seconds after the activation", 0, DelayWidth);
            Titled(titles, "Sends", "What it sends", 0, EditorTheme.PickerWidth);
            Titled(titles, "Answers", "Which activation it answers", 0, EditorTheme.PickerWidth);
            Titled(titles, "Reaches", "The actors whose Tag it names", 2, 0);
            titles.AddChild(new Control { CustomMinimumSize = new Vector2(EditorTheme.IconButtonSize, 0) });
        }

        for (int i = 0; i < group.Actions.Count; i++)
        {
            int index = i;
            MagicAction a = group.Actions[i];
            var row = new HBoxContainer();
            _groups.AddChild(row);
            var ev = new LineEdit { Text = a.Event, TooltipText = "The Tag it fires", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2 };
            ev.CustomMinimumSize = new Vector2(80, 0);
            void SetEvent(string text)
            {
                if (text.Trim() is { Length: > 0 } t && t != a.Event)
                {
                    SetAction(g, index, x => x with { Event = t });
                }
            }

            ev.TextSubmitted += SetEvent;
            ev.FocusExited += () => SetEvent(ev.Text);
            // The field and its picker are one combo box: one outline, the chevron joined on the right.
            var tag = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2 };
            tag.AddThemeConstantOverride("separation", 0);
            row.AddChild(tag);
            ev.ThemeTypeVariation = "ComboField";
            tag.AddChild(ev);
            TagPicker(tag, graph, SetEvent).ThemeTypeVariation = "ComboButton";
            // A number box like the group's Repeat: no stepper in this dense row, the unit inside the box.
            SpinBox delay = PanelUi.Spin(row, 0, MaxDelay, 0.01, a.Delay, "Seconds after the activation", DelayWidth);
            delay.Suffix = "s";
            delay.ValueChanged += d =>
            {
                if (Math.Abs(d - a.Delay) > 1e-9)
                {
                    // Steps of the box in a row make one undo step, as for Repeat.
                    SetAction(g, index, x => x with { Delay = d }, $"magic.delay.{_id}.{g}.{index}");
                }
            };
            TypePicker(row, (int)a.Type, "What it sends", t => SetAction(g, index, x => x with { Type = (MagicType)t }));
            TypePicker(row, (int)a.On, "Which activation it answers", t => SetAction(g, index, x => x with { On = (MagicType)t }));
            IReadOnlyList<string> reaches = MagicEvents.Receivers(graph, a.Event);
            Label to = WorkbenchUi.Value(row, reaches.Count == 0 ? "Reaches nothing" : WorkbenchUi.Join(reaches), reaches.Count == 0 ? "" : "Dim");
            to.SizeFlagsStretchRatio = 2;
            to.TooltipText = reaches.Count == 0 ? "No actor has this Tag" : string.Join(", ", reaches);
            if (reaches.Count == 0)
            {
                to.AddThemeColorOverride("font_color", EditorTheme.Warning);
            }

            PanelUi.IconButton(row, "trash", "Delete this action",
                () => Edit(list => list[g] = list[g] with { Actions = list[g].Actions.Where((_, k) => k != index).ToList() }), danger: true);
        }
    }

    /// <summary>A column title over the action rows: <paramref name="ratio"/> of the free width, or a fixed width.</summary>
    private static void Titled(HBoxContainer row, string text, string tooltip, float ratio, int width)
    {
        Label l = PanelUi.Caption(row, text, wrap: false);
        l.TooltipText = tooltip;
        if (ratio > 0)
        {
            l.SizeFlagsStretchRatio = ratio;
        }
        else
        {
            l.SizeFlagsHorizontal = SizeFlags.Fill;
            l.CustomMinimumSize = new Vector2(width, 0);
        }
    }

    /// <summary>A menu of the map's gameplay Tags (what an action can fire), filled when opened.</summary>
    private static MenuButton TagPicker(Control parent, GameplayGraph graph, Action<string> picked)
    {
        MenuButton menu = WorkbenchUi.DropButton(parent, "", "Pick the Tag of an actor in the map");
        PopupMenu popup = menu.GetPopup();
        menu.AboutToPopup += () =>
        {
            popup.Clear();
            foreach ((string tag, List<string> ids) in graph.TagIndex.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                popup.AddItem(ids.Count == 1 && string.Equals(ids[0], tag, StringComparison.OrdinalIgnoreCase) ? tag : $"{tag}  ({WorkbenchUi.Join(ids)})");
                popup.SetItemMetadata(popup.ItemCount - 1, tag);
            }
        };
        popup.IndexPressed += i => picked(popup.GetItemMetadata((int)i).AsString());
        return menu;
    }

    /// <summary>Fills the stop actor menu: none, then every actor that fires this SMagicEvent.</summary>
    private void FillStopPick()
    {
        PopupMenu popup = _stopPick.GetPopup();
        popup.Clear();
        popup.AddItem("None");
        if (_id is null)
        {
            return;
        }

        foreach (string sender in GameplayState.Instance.Graph.In(_id).Where(l => l.Kind == LinkKind.Fire).Select(l => l.From)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            popup.AddItem(sender);
        }
    }

    private static OptionButton TypePicker(Control parent, int selected, string tip, Action<int> picked)
    {
        OptionButton o = PanelUi.Options(parent, tip);
        o.CustomMinimumSize = new Vector2(EditorTheme.PickerWidth, 0);
        foreach (string t in s_types)
        {
            o.AddItem(t);
        }

        o.Selected = selected;
        o.ItemSelected += i => picked((int)i);
        return o;
    }

    private void SetAction(int g, int i, Func<MagicAction, MagicAction> change, string? mergeKey = null) =>
        Edit(list =>
        {
            var actions = list[g].Actions.ToList();
            actions[i] = change(actions[i]);
            list[g] = list[g] with { Actions = actions };
        }, mergeKey);

    private void Edit(Action<List<MagicGroup>> change, string? mergeKey = null)
    {
        if (_id is null || Context.Project.FindActor(_id) is not { } a)
        {
            return;
        }

        List<MagicGroup> groups = MagicEvents.Read(a);
        change(groups);
        try
        {
            Context.Execute(MagicEvents.SetGroups(Context.Project, _id, groups, mergeKey: mergeKey));
        }
        catch (ArgumentException ex)
        {
            Context.SetStatus(ex.Message);
        }
    }

    private void SetStop()
    {
        if (_id is null)
        {
            return;
        }

        string text = _stop.Text.Trim();
        string? stop = text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : text;
        if (Context.Project.FindActor(_id) is not { } a || string.Equals(PropValues.ActorId(a.Props["StopActor"]), stop, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Context.Execute(MagicEvents.SetStopActor(Context.Project, _id, stop));
        }
        catch (ArgumentException ex)
        {
            Context.SetStatus(ex.Message);
        }
    }

    private void Play(bool start)
    {
        if (_id is null)
        {
            return;
        }

        var activations = new List<MagicActivation>();
        foreach (string part in _activations.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] bits = part.Split(':');
            if (PanelUi.TryNum(bits[0], out double t))
            {
                string kind = bits.Length > 1 ? bits[1].ToLowerInvariant() : "";
                activations.Add(new MagicActivation(t, kind != "untrigger", kind == "stop"));
            }
        }

        double at = start ? 0 : MagicPreview.Time;
        MagicPreview.Show(_id, activations, at);
        if (start)
        {
            MagicPreview.Playing = true;
        }
    }

    private void ShowPreview()
    {
        bool mine = _id is not null && string.Equals(MagicPreview.Id, _id, StringComparison.OrdinalIgnoreCase);
        _timeline.Firings = mine ? MagicPreview.Firings : [];
        _timeline.Cursor = mine ? MagicPreview.Time : null;
        _timeline.Length = mine ? MagicPreview.Length : 0;
        if (!mine)
        {
            _firings.Text = "Play to see what fires when (actions wait in a queue checked every 0.05 s, as in the game).";
            return;
        }

        IEnumerable<string> done = MagicPreview.Firings.Where(f => f.Time <= MagicPreview.Time)
            .Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Time:0.00} s: {(f.Trigger ? "trigger" : "untrigger")} {f.Event}"));
        _firings.Text = string.Create(CultureInfo.InvariantCulture, $"At {MagicPreview.Time:0.00} s: ") + (string.Join("; ", done) is { Length: > 0 } s ? s : "nothing fired yet");
    }
}

/// <summary>An SMagicEvent's groups as lanes with each action at its delay (for one activation at 0) and, while
/// previewing, the firings and the preview cursor. Dragging an action changes its delay (in 0.05 s steps, the game's
/// queue step); clicking or dragging elsewhere seeks.</summary>
public partial class MagicTimelineView : Control
{
    private const float GrabRadius = 6;

    /// <summary>The height of the tick row above the lanes.</summary>
    private const float Top = 16;

    private static readonly StyleBoxFlat s_track = EditorTheme.Box(EditorTheme.TimelineTrack, EditorTheme.RadiusS);

    private IReadOnlyList<MagicGroup> _groups = [];
    private IReadOnlyList<MagicFiring> _firings = [];
    private double? _cursor;
    private double _length;
    private (int Group, int Action, double Delay)? _drag;

    public event Action<double>? Seek;

    /// <summary>An action was dragged to a new delay: group, action, seconds.</summary>
    public event Action<int, int, double>? ActionMoved;

    public IReadOnlyList<MagicGroup> Groups
    {
        get => _groups;
        set
        {
            _groups = value;
            QueueRedraw();
        }
    }

    public IReadOnlyList<MagicFiring> Firings
    {
        get => _firings;
        set
        {
            _firings = value;
            QueueRedraw();
        }
    }

    public double? Cursor
    {
        get => _cursor;
        set
        {
            _cursor = value;
            QueueRedraw();
        }
    }

    /// <summary>The previewed span in seconds (0: one activation's length).</summary>
    public double Length
    {
        get => _length;
        set
        {
            _length = value;
            QueueRedraw();
        }
    }

    private double Span => Math.Max(1, _length > 0 ? _length : MagicEvents.Length(_groups) * 1.1);

    public override void _Draw()
    {
        DrawStyleBox(s_track, new Rect2(0, Top, Size.X, Size.Y - Top));
        Font font = GetThemeDefaultFont();
        int lanes = Math.Max(1, _groups.Count);
        float laneHeight = (Size.Y - Top) / lanes;
        for (int g = 0; g < _groups.Count; g++)
        {
            float y = Top + g * laneHeight + laneHeight / 2;
            DrawLine(new Vector2(0, y), new Vector2(Size.X, y), EditorTheme.Divider);
            for (int i = 0; i < _groups[g].Actions.Count; i++)
            {
                MagicAction a = _groups[g].Actions[i];
                bool dragged = _drag is { } d && d.Group == g && d.Action == i;
                float x = X(dragged ? _drag!.Value.Delay : a.Delay);
                // A diamond per action, as the timeline-key icon draws it.
                float r = dragged ? 6 : 5;
                DrawColoredPolygon([new(x, y - r), new(x + r, y), new(x, y + r), new(x - r, y)], dragged ? EditorTheme.Playhead : EditorTheme.TimelineEvent);
                string text = dragged ? string.Create(CultureInfo.InvariantCulture, $"{a.Event} {_drag!.Value.Delay:0.00} s") : a.Event;
                DrawString(font, new Vector2(x + r + 3, y - 4), text, fontSize: EditorTheme.FontSizeCaption, modulate: dragged ? EditorTheme.Text : EditorTheme.TextDim);
            }
        }

        foreach (MagicFiring f in _firings)
        {
            float y = Top + f.Group * laneHeight + laneHeight / 2;
            Color col = _cursor is { } c && f.Time <= c ? EditorTheme.Playhead : new Color(EditorTheme.Playhead, 0.35f);
            DrawRect(new Rect2(X(f.Time) - 1, y - 7, 3, 14), col);
        }

        float tickY = font.GetAscent(EditorTheme.FontSizeMicro);
        DrawString(font, new Vector2(0, tickY), "0 s", fontSize: EditorTheme.FontSizeMicro, modulate: EditorTheme.TextDim);
        string end = Span.ToString("0.##", CultureInfo.InvariantCulture) + " s";
        DrawString(font, new Vector2(Size.X - font.GetStringSize(end, fontSize: EditorTheme.FontSizeMicro).X, tickY), end,
            fontSize: EditorTheme.FontSizeMicro, modulate: EditorTheme.TextDim);
        if (_cursor is { } cursor)
        {
            DrawLine(new Vector2(X(cursor), Top - 3), new Vector2(X(cursor), Size.Y), EditorTheme.Playhead, 2);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        bool press = e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true };
        bool release = e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false };
        bool drag = e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0;
        if (e is not InputEventMouse mouse)
        {
            return;
        }

        if (press && ActionAt(mouse.Position) is { } hit)
        {
            _drag = (hit.Group, hit.Action, _groups[hit.Group].Actions[hit.Action].Delay);
        }
        else if (drag && _drag is { } d)
        {
            _drag = d with { Delay = Math.Round(Seconds(mouse.Position.X) / MagicEvents.QueueStep) * MagicEvents.QueueStep };
            QueueRedraw();
        }
        else if (release && _drag is { } done)
        {
            _drag = null;
            if (Math.Abs(done.Delay - _groups[done.Group].Actions[done.Action].Delay) > 1e-9)
            {
                ActionMoved?.Invoke(done.Group, done.Action, done.Delay);
            }

            QueueRedraw();
        }
        else if (press || drag)
        {
            Seek?.Invoke(Seconds(mouse.Position.X));
        }
        else
        {
            return;
        }

        AcceptEvent();
    }

    /// <summary>The action drawn under <paramref name="at"/>, if any.</summary>
    private (int Group, int Action)? ActionAt(Vector2 at)
    {
        float laneHeight = (Size.Y - Top) / Math.Max(1, _groups.Count);
        for (int g = 0; g < _groups.Count; g++)
        {
            float y = Top + g * laneHeight + laneHeight / 2;
            for (int i = 0; i < _groups[g].Actions.Count; i++)
            {
                if (new Vector2(X(_groups[g].Actions[i].Delay), y).DistanceTo(at) <= GrabRadius)
                {
                    return (g, i);
                }
            }
        }

        return null;
    }

    private double Seconds(float x) => Math.Clamp(x / Math.Max(1, Size.X), 0, 1) * Span;

    private float X(double seconds) => (float)(seconds / Span * Size.X);
}
