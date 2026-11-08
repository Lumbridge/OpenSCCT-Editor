using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>A stop-focused mover workflow. Persistent fields keep focus while editing long paths.</summary>
[Panel("Mover", DockSide.Right, Order = 6)]
public partial class MoverPanel : EditorPanel
{
    private VBoxContainer _empty = null!, _body = null!;
    private Label _title = null!, _summary = null!, _help = null!, _stateInfo = null!, _problems = null!, _triggers = null!, _previewInfo = null!;
    private AxisFields _position = null!, _rotation = null!;
    private SpinBox _stop = null!, _legTime = null!;
    private OptionButton _state = null!, _preset = null!, _easing = null!;
    private MoverTimelineView _timeline = null!;
    private readonly Dictionary<string, SpinBox> _times = new();
    private readonly List<(Button Button, Func<MoverSetup, bool> Enabled)> _buttons = new();
    private CheckBox _glide = null!;
    private Button _convert = null!;
    private DeferredAction _update = null!;
    private string? _id;
    private bool _filling;
    private int _selected;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        var root = PanelUi.Scroll(this);
        _empty = new VBoxContainer(); root.AddChild(_empty);
        PanelUi.Title(_empty, "Make something move");
        PanelUi.Caption(_empty, "Start with a ready-to-use door or platform, or turn a selected static mesh or solid brush into a mover.");
        _convert = PanelUi.Primary(_empty, "Make selection move", "Keep the selected object's appearance and add editable motion", () => Context.Run("gameplay.mover.fromSelection"));
        foreach (var t in Enum.GetValues<MoverTemplate>())
        {
            var template = t;
            PanelUi.Button(_empty, "Create " + MoverAuthoring.Title(t).ToLowerInvariant(), "Creates geometry, collision and a working two-stop motion", () => Context.Run("gameplay.mover.create." + template.ToString().ToLowerInvariant()));
        }
        PanelUi.Caption(_empty, "You can also use Brush > Add Mover to use the builder brush's shape.");
        _body = new VBoxContainer(); root.AddChild(_body);
        _title = PanelUi.Title(_body, ""); _summary = PanelUi.Caption(_body, "");
        _problems = PanelUi.Caption(_body, "");

        PanelUi.Header(_body, "Edit a stop");
        var pick = PanelUi.Row(_body, "Stop", "Stops are numbered from 1 in this panel");
        Button previous = PanelUi.IconButton(pick, "arrow-left", "Previous stop", () => Pick(_selected - 1));
        _stop = PanelUi.Spin(pick, 1, 2, 1, 1, "Choose a stop to place in the views");
        _stop.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _stop.ValueChanged += n => { if (!_filling) Pick((int)n - 1); };
        Button next = PanelUi.IconButton(pick, "arrow-right", "Next stop", () => Pick(_selected + 1));
        _buttons.Add((previous, m => _selected > 0)); _buttons.Add((next, m => _selected < m.Keys.Count - 1));
        _help = PanelUi.Caption(_body, "");
        var editing = PanelUi.Toolbar(_body);
        PanelUi.Button(editing, "Edit in view", "Show this stop, then move or rotate the mover in a view", () => Pick(_selected));
        PanelUi.Button(editing, "Move whole mover", "Return to the start pose. Moving it then moves the whole path", () => Context.Run("gameplay.mover.showStart"));
        _position = AxisFields.Create(["X", "Y", "Z"]);
        PanelUi.Row(_body, "Position", "Offset from the mover's base, in map units", 84).AddChild(_position);
        _position.Edited += (axis, value, final) =>
        {
            if (!final || _filling || Setup() is not { } m) return;
            var key = m.Keys[_selected];
            Edit(() => MoverEdits.SetKey(Context.Project, _id!, _selected, key with { Offset = axis switch
            { 0 => key.Offset with { X = value }, 1 => key.Offset with { Y = value }, _ => key.Offset with { Z = value } } }));
        };
        _rotation = AxisFields.Create(["Pitch", "Yaw", "Roll"]);
        PanelUi.Row(_body, "Rotation °", "Rotation relative to the base, in degrees", 84).AddChild(_rotation);
        _rotation.Edited += (axis, value, final) =>
        {
            if (!final || _filling || Setup() is not { } m) return;
            var key = m.Keys[_selected]; int units = (int)Math.Round(Math.Clamp(value, -1000000, 1000000) * 65536 / 360);
            Edit(() => MoverEdits.SetKey(Context.Project, _id!, _selected, key with { Turn = axis switch
            { 0 => key.Turn with { Pitch = units }, 1 => key.Turn with { Yaw = units }, _ => key.Turn with { Roll = units } } }));
        };
        var legRow = PanelUi.Row(_body, "Travel time", "Seconds between the previous stop and this stop, in either direction", 84);
        _legTime = PanelUi.Spin(legRow, MoverTiming.ShortestLeg, 3600, .01, 1, "This leg's travel time"); _legTime.Suffix = "s";
        _legTime.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _legTime.ValueChanged += t => { if (!_filling && _selected > 0) Edit(() => MoverEdits.SetLegTime(Context.Project, _id!, _selected, t)); };
        PanelUi.IconButton(legRow, "reset", "Use the default travel time", () => { if (_selected > 0) Edit(() => MoverEdits.SetLegTime(Context.Project, _id!, _selected, null)); });
        _easing = PanelUi.Options(PanelUi.Row(_body, "At this stop", "How it accelerates or slows down here", 84), "Easing at this stop");
        foreach (string label in new[] { "Use default", "Smooth arrival / departure", "Keep speed" }) _easing.AddItem(label);
        _easing.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _easing.ItemSelected += i => { if (!_filling) Edit(() => MoverEdits.SetKeySmooth(Context.Project, _id!, _selected, i switch { 1 => true, 2 => false, _ => null })); };
        var add = PanelUi.Toolbar(_body);
        Button append = PanelUi.Button(add, "Extend path", "Continue the last segment with one more stop", () =>
        {
            if (Setup() is not { } m) return; int index = m.Keys.Count;
            EditAndPick(() => MoverEdits.AddKey(Context.Project, _id!), index);
        }); _buttons.Add((append, m => m.Keys.Count < MoverSetup.MaxKeys));
        Button duplicate = PanelUi.Button(add, "Duplicate stop", "Insert a copy immediately after this stop", () =>
        { int index = _selected + 1; EditAndPick(() => MoverEdits.InsertKey(Context.Project, _id!, _selected), index); });
        _buttons.Add((duplicate, m => m.Keys.Count < MoverSetup.MaxKeys));
        Button here = PanelUi.Button(add, "Add at view centre", "Insert a stop where the active view looks", () =>
        { int index = _selected + 1; EditAndPick(() => MoverEdits.InsertKey(Context.Project, _id!, _selected, Context.PlacementPoint?.Invoke()), index); });
        _buttons.Add((here, m => m.Keys.Count < MoverSetup.MaxKeys));
        var order = PanelUi.Toolbar(_body);
        Button earlier = PanelUi.Button(order, "Earlier", "Move this stop earlier in the path", () => Reorder(-1));
        Button later = PanelUi.Button(order, "Later", "Move this stop later in the path", () => Reorder(1));
        _buttons.Add((earlier, m => _selected > 0)); _buttons.Add((later, m => _selected < m.Keys.Count - 1));
        PanelUi.Button(order, "Start here", "The round starts with the mover at this stop", () => Edit(() => MoverEdits.SetStartKey(Context.Project, _id!, _selected)));
        Button delete = PanelUi.Button(order, "Delete stop", "Remove this stop; undo restores it", () => Edit(() => MoverEdits.DeleteKey(Context.Project, _id!, _selected)));
        _buttons.Add((delete, m => m.Keys.Count > 1));

        PanelUi.Header(_body, "Preview motion");
        var preview = PanelUi.Toolbar(_body);
        PanelUi.Button(preview, "Start", "Preview the start of one activation", () => Context.Run("gameplay.mover.previewClosed"));
        PanelUi.Button(preview, "Finish", "Preview the last stop", () => Context.Run("gameplay.mover.previewOpen"));
        PanelUi.Button(preview, "Play / pause", "Preview motion without changing your map", () => Context.Run("gameplay.mover.play"));
        PanelUi.Button(preview, "Stop", "Return to editing", () => Context.Run("gameplay.mover.stopPreview"));
        _timeline = new MoverTimelineView { CustomMinimumSize = new Vector2(0, EditorTheme.TimelineHeight) };
        _timeline.Seek += t => { if (_id is null) return; MoverPreview.Playing = false; MoverPreview.Show(_id, t); };
        _timeline.LegDragged += (key, time) => Edit(() => MoverEdits.SetLegTime(Context.Project, _id!, key, time));
        _body.AddChild(_timeline); _previewInfo = PanelUi.Caption(_body, "");


        PanelUi.Header(_body, "How it works");
        _state = PanelUi.Options(_body, "What activates the mover and what it does afterwards");
        foreach (var state in MoverSetup.States) _state.AddItem(state.Title);
        _state.ItemSelected += i => { if (!_filling) Edit(() => MoverEdits.SetState(Context.Project, _id!, MoverSetup.States[(int)i].Name)); };
        _stateInfo = PanelUi.Caption(_body, "");
        TimeField(_body, "MoveTime", "Default travel", "Time between stops unless a leg has its own time");
        TimeField(_body, "StayOpenTime", "Wait before return", "Hold time for timed behaviour");
        _glide = new CheckBox { Text = "Ease in and out by default" }; _body.AddChild(_glide);
        _glide.Toggled += on => { if (!_filling) Edit(() => MoverEdits.SetEased(Context.Project, _id!, on)); };
        PanelUi.Button(_body, "Add a connected switch", "Place a switch at the view centre and wire it to this mover in one undo step", () =>
        {
            if (Setup() is { } m) Edit(() => MoverAuthoring.AddSwitch(Context.Project, _id!, Context.PlacementPoint?.Invoke() ?? m.BasePos + new Vec3(128, 0, 0)));
        });
        var advanced = new VBoxContainer { Visible = false };
        PanelUi.Toggle(_body, "More options", "Delays, motion presets and wiring details", false, on => advanced.Visible = on);
        _body.AddChild(advanced);
        TimeField(advanced, "DelayTime", "Before opening", "Delay after activation");
        TimeField(advanced, "DelayTimeLastKey", "Before closing", "Delay before the return movement");
        TimeField(advanced, "OtherTime", "Pound hold", "Hold open time for repeated opening and closing");
        PanelUi.Header(advanced, "Replace motion with a preset");
        PanelUi.Caption(advanced, "Replaces this mover's stops and timing. Its appearance stays; Undo restores the old motion.");
        _preset = PanelUi.Options(advanced, "Starting motion");
        foreach (var t in Enum.GetValues<MoverTemplate>()) _preset.AddItem(MoverAuthoring.Title(t));
        PanelUi.Button(advanced, "Replace motion", "Replace the path with the selected two-stop motion", () =>
            Edit(() => MoverAuthoring.ApplyTemplate(Context.Project, _id!, (MoverTemplate)_preset.Selected)));
        PanelUi.Header(advanced, "Wiring");
        PanelUi.Button(advanced, "Selected actors trigger this", "Connect other selected actors to this mover", WireSelected);
        _triggers = PanelUi.Caption(advanced, "");
        Context.ProjectChanged += _ => _update.Queue(); Context.SelectionChanged += _update.Queue;
        MoverPreview.Changed += ShowPreview;
    }

    private void TimeField(Control parent, string property, string title, string tip)
    {
        var field = PanelUi.Spin(PanelUi.Row(parent, title, tip), 0, 3600, .01, 0, tip);
        field.Suffix = "s"; field.SizeFlagsHorizontal = SizeFlags.ExpandFill; field.AllowGreater = true;
        field.ValueChanged += value => { if (!_filling) Edit(() => MoverEdits.SetTime(Context.Project, _id!, property, value, $"mover.time.{_id}.{property}")); };
        _times[property] = field;
    }
    private MoverSetup? Setup() => _id is { } id && Context.Project.FindActor(id) is { } a ? MoverSetup.Read(a) : null;
    public override void Refresh() => _update.Queue();
    public override void _Process(double delta) => MoverPreview.Advance(delta);
    private void Pick(int stop)
    {
        if (Setup() is not { } m) return;
        _selected = Math.Clamp(stop, 0, m.Keys.Count - 1); MoverPreview.Stop();
        Edit(() => MoverEdits.EditKey(Context.Project, _id!, _selected)); _update.Queue();
    }
    private void EditAndPick(Func<IEditCommand> edit, int stop)
    {
        if (_id is null) return;
        try
        {
            IEditCommand command = edit();
            var scratch = OpenScct.Editor.Core.Session.ProjectSnapshot.Take(Context.Project);
            command.Do(scratch);
            var show = MoverEdits.EditKey(scratch, _id, stop);
            Context.Execute(new CompositeCommand(command.Name, [command, show]));
            _selected = stop; MoverPreview.Stop(); _update.Queue();
        }
        catch (ArgumentException ex) { Context.SetStatus(ex.Message); }
    }
    private void Reorder(int by)
    {
        int target = _selected + by;
        EditAndPick(() => MoverEdits.MoveKey(Context.Project, _id!, _selected, by), target);
    }
    private void Rebuild()
    {
        Actor? actor = WorkbenchPart.Selected(Context, MoverBase.IsMover).FirstOrDefault();
        _empty.Visible = actor is null; _body.Visible = actor is not null;
        _convert.Disabled = Context.Selection.Count != 1 || Context.Selection.Primary is not { } selected || !MoverAuthoring.CanConvert(Context.Project, selected);
        if (actor is null) { _id = null; MoverPreview.Stop(); return; }
        if (_id != actor.Id) { _selected = MoverEdits.ShownKey(actor) ?? MoverSetup.Read(actor).StartKey; _id = actor.Id; }
        if (MoverEdits.ShownKey(actor) is { } shown) _selected = shown;
        var m = MoverSetup.Read(actor); _selected = Math.Clamp(_selected, 0, m.Keys.Count - 1);
        _filling = true;
        try
        {
            _title.Text = m.Id;
            _summary.Text = $"{m.Keys.Count} stops · starts at stop {m.StartKey + 1}";
            var problems = m.Problems();
            if (m.Keys.Count > MoverSetup.OriginalMaxKeys) problems.Add("OpenSCCT path. Original-game export supports eight stops; this path will be kept intact for OpenSCCT.");
            if (actor.Shape is null && PropValues.AssetPath(actor.Props["StaticMesh"] ?? OpenScct.Editor.Core.Gameplay.ClassDefaults.Catalog.Default(actor.Class, "StaticMesh")) is null)
                problems.Add("This mover needs an appearance. Assign a StaticMesh in the Inspector, or create a door or platform above.");
            _problems.Visible = problems.Count > 0; _problems.Text = string.Join("\n", problems);
            _stop.MaxValue = m.Keys.Count; _stop.SetValueNoSignal(_selected + 1);
            _help.Text = MoverEdits.ShownKey(actor) == _selected
                ? $"Editing stop {_selected + 1}. Move or rotate it in a view to place this stop."
                : $"Stop {_selected + 1}{(_selected == m.StartKey ? " (round start)" : "")}. Choose Edit in view to place it; Move whole mover moves the entire path.";
            var key = m.Keys[_selected]; _position.SetValues([key.Offset.X, key.Offset.Y, key.Offset.Z]);
            _rotation.SetValues([key.Turn.Pitch * 360.0 / 65536, key.Turn.Yaw * 360.0 / 65536, key.Turn.Roll * 360.0 / 65536]);
            var own = m.Legs is { } legs && _selected < legs.Keys.Count ? legs.Keys[_selected] : default;
            _legTime.Editable = _selected > 0; _legTime.SetValueNoSignal(own.Time ?? m.MoveTime);
            _easing.Selected = own.Smooth switch { true => 1, false => 2, _ => 0 };
            _glide.SetPressedNoSignal(m.Eased);
            _state.Selected = MoverSetup.States.ToList().FindIndex(s => s.Name.Equals(m.State, StringComparison.OrdinalIgnoreCase));
            _stateInfo.Text = m.StateInfo?.Description ?? m.State;
            foreach (var (name, field) in _times) field.SetValueNoSignal(name switch
            { "MoveTime" => m.MoveTime, "StayOpenTime" => m.StayOpenTime, "DelayTime" => m.DelayTime, "DelayTimeLastKey" => m.DelayTimeLastKey, _ => m.OtherTime });
            foreach (var (button, enabled) in _buttons) button.Disabled = !enabled(m);
            _timeline.Timeline = MoverTimeline.Build(m);
            var triggers = MoverTriggers.Of(Context.Project, GameplayState.Instance.Graph, actor);
            _triggers.Text = string.Join("\n", triggers.Senders.Select(l => $"{l.From} ({l.Via})").Concat(triggers.Locks.Select(l => $"{l.From} locks this mover")));
            if (_triggers.Text.Length == 0) _triggers.Text = "No event connections. Bump and stand behaviours can activate without wiring.";
        }
        finally { _filling = false; }
        if (MoverPreview.Id == _id) MoverPreview.Show(_id!, MoverPreview.Time);
        ShowPreview();
    }
    private void Edit(Func<IEditCommand> action)
    {
        if (_id is null) return;
        try { Context.Execute(action()); } catch (ArgumentException ex) { Context.SetStatus(ex.Message); }
    }
    private void WireSelected()
    {
        if (_id is null) return;
        var senders = WorkbenchPart.Selected(Context, a => a.Id != _id).Select(a => a.Id).ToList();
        var commands = senders.Select(s => Wiring.Link(Context.Project, s, _id)).OfType<ReplaceObjectsCommand>().Where(c => !c.IsEmpty).ToList();
        if (commands.Count == 0) { Context.SetStatus("Select a switch or trigger and this mover, then connect them"); return; }
        Context.Execute(new CompositeCommand("Connect mover", commands));
    }
    private void ShowPreview()
    {
        bool mine = _id is not null && MoverPreview.Id == _id;
        _timeline.Cursor = mine ? MoverPreview.Time : null;
        _previewInfo.Text = mine ? string.Create(CultureInfo.InvariantCulture, $"{MoverPreview.Time:0.00} s {(MoverPreview.Playing ? "· playing" : "· paused")}")
            : "Click to scrub. Drag a travel segment's end to change its time.";
    }
}

/// <summary>One mover activation drawn as a bar per step (legs bright, waits dim) with the preview cursor; clicking
/// or dragging seeks, and dragging the end of a leg between neighbouring keys gives that leg its own time (past the
/// right edge to lengthen the last leg).</summary>
public partial class MoverTimelineView : Control
{
    /// <summary>How near a leg's end (in pixels) a press grabs it.</summary>
    private const float Grab = 4;

    /// <summary>How far (in pixels) a grabbed leg end moves before it is a drag; a shorter press is a click that seeks.</summary>
    private const float DragStart = 3;
    private static readonly StyleBoxFlat s_track = EditorTheme.Box(EditorTheme.TimelineTrack, EditorTheme.RadiusS);

    private MoverTimeline? _timeline;
    private double? _cursor;
    private MoverStep? _dragged;
    private double _dragEnd;
    private float _pressX;
    private bool _dragMoved;

    public event Action<double>? Seek;

    /// <summary>A leg's end was dragged: the key it arrives at (the later of its two) and its new time in seconds.</summary>
    public event Action<int, double>? LegDragged;

    public MoverTimeline? Timeline
    {
        get => _timeline;
        set
        {
            _timeline = value;
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

    public override void _Draw()
    {
        // A tick row with the start and end times, then the track: legs on an accent wash with an accent top edge,
        // waits in the hold colour, each labelled when it fits.
        const float barTop = 16;
        float barHeight = Size.Y - barTop;
        DrawStyleBox(s_track, new Rect2(0, barTop, Size.X, barHeight));
        if (_timeline is not { Duration: > 0 } t)
        {
            return;
        }

        Font font = GetThemeDefaultFont();
        float textY = barTop + (barHeight + font.GetAscent(EditorTheme.FontSizeCaption) - font.GetDescent(EditorTheme.FontSizeCaption)) / 2;
        foreach (MoverStep s in t.Steps)
        {
            float x0 = X(s.Start), x1 = X(s.End);
            var bar = new Rect2(x0, barTop + 1, Math.Max(1, x1 - x0 - 1), barHeight - 2);
            DrawRect(bar, s.IsWait ? EditorTheme.TimelineHold : EditorTheme.TimelineSegment);
            if (!s.IsWait)
            {
                DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X, 1)), EditorTheme.Accent);
            }

            string text = s.IsWait ? s.Label : $"{s.From + 1} to {s.To + 1}";
            if (font.GetStringSize(text, fontSize: EditorTheme.FontSizeCaption).X < x1 - x0 - 8)
            {
                DrawString(font, new Vector2(x0 + 4, textY), text, fontSize: EditorTheme.FontSizeCaption,
                    modulate: s.IsWait ? EditorTheme.TextDim : EditorTheme.Text);
            }
        }

        float tickY = font.GetAscent(EditorTheme.FontSizeMicro);
        DrawString(font, new Vector2(0, tickY), "0 s", fontSize: EditorTheme.FontSizeMicro, modulate: EditorTheme.TextDim);
        string end = t.Duration.ToString("0.##", CultureInfo.InvariantCulture) + " s";
        DrawString(font, new Vector2(Size.X - font.GetStringSize(end, fontSize: EditorTheme.FontSizeMicro).X, tickY), end,
            fontSize: EditorTheme.FontSizeMicro, modulate: EditorTheme.TextDim);
        if (_cursor is { } c)
        {
            DrawLine(new Vector2(X(c), barTop - 3), new Vector2(X(c), Size.Y), EditorTheme.Playhead, 2);
        }

        if (_dragged is { } d && _dragMoved)
        {
            float x = Math.Min(X(_dragEnd), Size.X - 1);
            // The dragged leg end and the leg's new length, in the accent (the playhead keeps its own colour).
            DrawLine(new Vector2(x, barTop), new Vector2(x, barTop + barHeight), EditorTheme.Accent, 2);
            string seconds = (_dragEnd - d.Start).ToString("0.##", CultureInfo.InvariantCulture) + " s";
            float width = font.GetStringSize(seconds, fontSize: EditorTheme.FontSizeMicro).X;
            DrawString(font, new Vector2(Math.Min(x + 3, Size.X - width - 2), tickY), seconds, fontSize: EditorTheme.FontSizeMicro,
                modulate: EditorTheme.TextBright);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (_timeline is not { Duration: > 0 } t || e is not InputEventMouse mouse)
        {
            return;
        }

        double unclamped = mouse.Position.X / Math.Max(1, Size.X) * t.Duration;
        double at = Math.Clamp(unclamped, 0, t.Duration);
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                // A press on the end of a leg between neighbouring keys may start a drag of that end; anywhere else seeks.
                _dragged = LegEndAt(t, mouse.Position.X);
                _dragEnd = _dragged?.End ?? 0;
                _pressX = mouse.Position.X;
                _dragMoved = false;
                if (_dragged is null)
                {
                    Seek?.Invoke(at);
                }

                QueueRedraw();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragged is { } leg:
                _dragged = null;
                double seconds = Math.Round(_dragEnd - leg.Start, 2);
                if (!_dragMoved)
                {
                    // Not dragged: a click on the leg end, which seeks like any other.
                    Seek?.Invoke(at);
                }
                else if (Math.Abs(seconds - (leg.End - leg.Start)) >= 0.005)
                {
                    LegDragged?.Invoke(Math.Max(leg.From, leg.To), seconds);
                }

                QueueRedraw();
                break;
            case InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Left) != 0:
                if (_dragged is { } dragged)
                {
                    _dragMoved |= Math.Abs(mouse.Position.X - _pressX) >= DragStart;
                    if (_dragMoved)
                    {
                        // Not clamped to the timeline, so the last leg can grow past its end.
                        _dragEnd = Math.Clamp(unclamped, dragged.Start + MoverTiming.ShortestLeg, dragged.Start + MoverTiming.LongestLeg);
                        QueueRedraw();
                    }
                }
                else
                {
                    Seek?.Invoke(at);
                }

                break;
            case InputEventMouseMotion:
                MouseDefaultCursorShape = LegEndAt(t, mouse.Position.X) is null ? CursorShape.Arrow : CursorShape.Hsize;
                return;
            default:
                return;
        }

        AcceptEvent();
    }

    /// <summary>The leg between neighbouring keys whose end is under <paramref name="x"/>, or null.</summary>
    private MoverStep? LegEndAt(MoverTimeline t, float x) =>
        t.Steps.FirstOrDefault(s => !s.IsWait && Math.Abs(s.From - s.To) == 1 && Math.Abs(X(s.End) - x) <= Grab);

    private float X(double seconds) => (float)(seconds / _timeline!.Duration * Size.X);
}
