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

/// <summary>
/// The mover workbench for the selected mover: its keys (offsets from the base, at most 8 as the game's arrays hold),
/// each placeable in the views (Show draws the mover at the key, then moving or turning it places that key), the key
/// the round starts at, timing, glide and state, each key's own leg time and easing (OpenSCCT; drag a leg's end on the
/// timeline too), one activation drawn as a timeline with the time each key is reached and warnings where the game moves
/// it differently from the key list, a preview that moves the mover in the views
/// (closed, open, or playing), and what triggers or locks it.
/// </summary>
[Panel("Mover", DockSide.Workbench, Order = 6)]
public partial class MoverPanel : EditorPanel
{
    private static readonly (string Prop, string Name, string Tip)[] s_times =
    [
        ("MoveTime", "Move time", "Seconds for each leg between two keys (the game uses one time for every leg)"),
        ("StayOpenTime", "Stay open", "Seconds held open before closing (timed states); seconds held closed in the close-timed and pound states"),
        ("DelayTime", "Delay", "Seconds between the trigger and the first leg of opening"),
        ("DelayTimeLastKey", "Close delay", "Seconds before it starts closing (and before the first leg of the states that start open)"),
        ("OtherTime", "Pound open", "Seconds TriggerPound stays open before closing again"),
    ];

    /// <summary>The longest time a timing field takes, in seconds.</summary>
    private const double MaxSeconds = 3600;

    /// <summary>The step of the timing fields, in seconds.</summary>
    private const double TimeStep = 0.01;

    private Control _empty = null!;
    private Control _scroll = null!;
    private Label _title = null!;
    private Label _summary = null!;
    private VBoxContainer _body = null!;
    private PanelContainer _shownBox = null!;
    private Label _shown = null!;
    private Button _backToStart = null!;
    private VBoxContainer _keys = null!;
    private VBoxContainer _legs = null!;
    private readonly List<LegFields> _legRows = new();
    private string? _legsFor;
    private readonly Dictionary<string, SpinBox> _timeFields = new();
    private CheckBox _eased = null!;
    private OptionButton _state = null!;
    private Label _stateInfo = null!;
    private MoverTimelineView _timeline = null!;
    private PanelContainer _problemsBox = null!;
    private Label _problems = null!;
    private Label _previewInfo = null!;
    private Label _triggers = null!;
    private DeferredAction _update = null!;
    private string? _id;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        _empty = PanelUi.EmptyState(this, "type-mover", "No mover selected", "Select a mover (a door, lift or shutter) to edit its keys and timing.");
        VBoxContainer box = PanelUi.Scroll(this);
        _scroll = PanelUi.ScrollOf(box);
        _title = PanelUi.Title(box, "");
        _summary = PanelUi.Caption(box, "");
        _body = new VBoxContainer();
        box.AddChild(_body);

        PanelUi.Header(_body, "Keys");
        (_shownBox, _shown) = WorkbenchUi.Callout(_body, Severity.Info);
        HFlowContainer back = PanelUi.Toolbar(_body);
        _backToStart = PanelUi.Button(back, "Back to where it starts", "Draw the mover at its start key again, so moving it moves the whole mover",
            () => Context.Run("gameplay.mover.showStart"));
        _keys = new VBoxContainer();
        _keys.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        _body.AddChild(_keys);
        HFlowContainer keyBar = PanelUi.Toolbar(_body);
        PanelUi.Button(keyBar, "Add key", "One leg further along the last leg", () => Context.Run("gameplay.mover.addKey")).Icon = Icons.Get("plus", EditorTheme.Text);
        PanelUi.Button(keyBar, "Add at view centre", "A key where the view looks", () => Context.Run("gameplay.mover.addKeyHere"));

        PanelUi.Header(_body, "Timing");
        foreach ((string prop, string name, string tip) in s_times)
        {
            HBoxContainer row = PanelUi.Row(_body, name, tip);
            SpinBox field = PanelUi.Spin(row, 0, MaxSeconds, TimeStep, 0, tip);
            field.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            field.Suffix = "s";
            // The arrows stop at MaxSeconds; a longer stored time still shows as it is.
            field.AllowGreater = true;
            field.ValueChanged += v => SetTime(prop, v);
            _timeFields[prop] = field;
        }

        _eased = new CheckBox { Text = "Ease in and out", TooltipText = "GlideByTime; off: constant speed (MoveByTime)" };
        _eased.Toggled += on => Edit(() => MoverEdits.SetEased(Context.Project, _id!, on));
        _body.AddChild(_eased);
        HBoxContainer stateRow = PanelUi.Row(_body, "State", "InitialState: how triggers and touches move it");
        _state = PanelUi.Options(stateRow, "InitialState: how triggers and touches move it");
        _state.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (MoverState s in MoverSetup.States)
        {
            _state.AddItem(s.Title);
        }

        _state.ItemSelected += i => Edit(() => MoverEdits.SetState(Context.Project, _id!, MoverSetup.States[(int)i].Name));
        _stateInfo = PanelUi.Caption(_body, "");

        PanelUi.Header(_body, "Each key's timing (OpenSCCT)");
        PanelUi.Caption(_body, "A leg's own time and the easing at a key; a dimmed time is the move time above, Default the glide above. " +
            "The original game runs every leg in the move time, so an export to it drops these.");
        _legs = new VBoxContainer();
        _body.AddChild(_legs);

        PanelUi.Header(_body, "One activation");
        _timeline = new MoverTimelineView { CustomMinimumSize = new Vector2(0, EditorTheme.TimelineHeight) };
        _timeline.Seek += t =>
        {
            MoverPreview.Playing = false;
            MoverPreview.Show(_id!, t);
        };
        _timeline.LegDragged += (key, seconds) => Edit(() => MoverEdits.SetLegTime(Context.Project, _id!, key, seconds));
        _body.AddChild(_timeline);
        (_problemsBox, _problems) = WorkbenchUi.Callout(_body, Severity.Warning);
        var preview = new HBoxContainer();
        _body.AddChild(preview);
        PanelUi.Segmented(preview, [("Closed", "Draw it where an activation starts"), ("Open", "Draw it at its open key")], -1,
            i => Context.Run(i == 0 ? "gameplay.mover.previewClosed" : "gameplay.mover.previewOpen"));
        WorkbenchUi.Spacer(preview);
        PanelUi.IconButton(preview, "play", "Play or pause the activation in the views, again and again", () => Context.Run("gameplay.mover.play"));
        PanelUi.IconButton(preview, "stop", "Stop the preview: draw it where the map has it", () => Context.Run("gameplay.mover.stopPreview"));
        _previewInfo = PanelUi.Caption(_body, "");

        PanelUi.Header(_body, "Triggered and locked by");
        _triggers = PanelUi.Label(_body, "");
        _triggers.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        HFlowContainer wire = PanelUi.Toolbar(_body);
        PanelUi.Button(wire, "Selected actors trigger it", "The other selected actors fire this mover (their Event = its Tag)", WireSelected);

        Context.ProjectChanged += _ => _update.Queue();
        Context.SelectionChanged += _update.Queue;
        MoverPreview.Changed += ShowPreview;
    }

    public override void Refresh() => _update.Queue();

    public override void _Process(double delta) => MoverPreview.Advance(delta);

    private void Rebuild()
    {
        Actor? mover = WorkbenchPart.Selected(Context, MoverBase.IsMover).FirstOrDefault();
        _id = mover?.Id;
        _scroll.Visible = mover is not null;
        _empty.Visible = mover is null;
        if (mover is null)
        {
            if (MoverPreview.Id is not null)
            {
                MoverPreview.Stop();
            }

            return;
        }

        MoverSetup m = MoverSetup.Read(mover);
        MoverTimeline timeline = MoverTimeline.Build(m);
        IReadOnlyList<double?> reached = timeline.KeyTimes();
        int? shown = MoverEdits.ShownKey(mover);
        _title.Text = _title.TooltipText = mover.Id;
        _summary.Text = $"Tag {m.Tag} · {m.Keys.Count} of {MoverSetup.MaxKeys} keys · starts at key {m.StartKey} · last key {m.OpenKey}";
        _shownBox.Visible = _backToStart.Visible = shown is not null;
        _shown.Text = shown is { } k
            ? $"Drawn at key {k}: moving or turning it in the views places key {k}. The round still starts it at key {m.StartKey}."
            : "";
        foreach (Node child in _keys.GetChildren())
        {
            child.QueueFree();
        }

        for (int i = 0; i < m.Keys.Count; i++)
        {
            KeyRow(m, i, reached[i], shown ?? m.StartKey);
        }

        // The leg rows stay while the mover and its key count do, so a number box keeps its focus and a held arrow
        // keeps stepping; their values refresh below.
        if (_legsFor != m.Id || _legRows.Count != m.Keys.Count)
        {
            foreach (Node child in _legs.GetChildren())
            {
                child.QueueFree();
            }

            _legRows.Clear();
            _legsFor = m.Id;
            for (int i = 0; i < m.Keys.Count; i++)
            {
                _legRows.Add(LegRow(i));
            }
        }

        for (int i = 0; i < m.Keys.Count; i++)
        {
            ShowLeg(m, i);
        }

        foreach ((string prop, _, _) in s_times)
        {
            double value = prop switch
            {
                "MoveTime" => m.MoveTime,
                "StayOpenTime" => m.StayOpenTime,
                "DelayTime" => m.DelayTime,
                "DelayTimeLastKey" => m.DelayTimeLastKey,
                _ => m.OtherTime,
            };
            _timeFields[prop].SetValueNoSignal(value);
        }

        _eased.SetPressedNoSignal(m.Eased);
        int state = MoverSetup.States.ToList().FindIndex(s => string.Equals(s.Name, m.State, StringComparison.OrdinalIgnoreCase));
        _state.Selected = state;
        _stateInfo.Text = m.StateInfo?.Description ?? $"{m.State}: not a state the editor knows";
        _timeline.Timeline = timeline;
        List<string> problems = m.Problems();
        _problemsBox.Visible = problems.Count > 0;
        _problems.Text = string.Join("\n", problems);
        MoverTriggers triggers = MoverTriggers.Of(Context.Project, GameplayState.Instance.Graph, mover);
        IEnumerable<string> lines = triggers.Senders.Select(l => $"{l.From} ({l.Via})")
            .Concat(triggers.Locks.Select(l => $"{l.From} locks it ({l.Via}): it stops answering, it does not move"))
            .Concat(triggers.Overrides.Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.Sender}: {o.State}, move time {o.MoveTime:0.##} s (SpecialBehaviors)")));
        bool touched = m.State.StartsWith("Bump", StringComparison.OrdinalIgnoreCase) || m.State.StartsWith("Stand", StringComparison.OrdinalIgnoreCase);
        string wired = string.Join("\n", lines);
        _triggers.Text = wired.Length > 0 ? wired : "Nothing triggers it." + (touched ? " It opens when touched." : "");
        _triggers.ThemeTypeVariation = wired.Length > 0 ? "" : "Dim";
        if (string.Equals(MoverPreview.Id, mover.Id, StringComparison.OrdinalIgnoreCase))
        {
            MoverPreview.Show(mover.Id, MoverPreview.Time);
        }
        else if (MoverPreview.Id is not null)
        {
            MoverPreview.Stop();
        }

        ShowPreview();
    }

    private void KeyRow(MoverSetup m, int i, double? reached, int drawnAt)
    {
        var key = new VBoxContainer();
        key.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        _keys.AddChild(key);
        var head = new HBoxContainer();
        key.AddChild(head);
        head.AddChild(new Label { Text = $"Key {i}", ThemeTypeVariation = "GroupLabel" });
        string roles = string.Join(" · ", new[] { i == m.StartKey ? "start" : null, i == m.OpenKey ? "last" : null }.OfType<string>());
        WorkbenchUi.Value(head, roles, "Caption");
        Label at = PanelUi.Caption(head, reached is { } r ? string.Create(CultureInfo.InvariantCulture, $"{r:0.##} s") : PanelUi.Mixed, wrap: false);
        at.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        // Short: it keeps its width, the roles before it give way.
        at.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        at.CustomMinimumSize = Vector2.Zero;
        at.TooltipText = "When one activation first reaches this key (\u2014: it never does)";
        HBoxContainer actions = WorkbenchUi.Actions(head);
        WorkbenchUi.IconToggle(actions, "eye", "Draw the mover at this key; then move or turn it in the views to place the key", i == drawnAt,
            () => Edit(() => MoverEdits.ShowKey(Context.Project, _id!, i)));
        WorkbenchUi.IconToggle(actions, "pin", "The round starts with the mover at this key (KeyNum)", i == m.StartKey,
            () => Edit(() => MoverEdits.SetStartKey(Context.Project, _id!, i)));
        PanelUi.IconButton(actions, "arrow-up", "Earlier", () => Edit(() => MoverEdits.MoveKey(Context.Project, _id!, i, -1))).Disabled = i == 0;
        PanelUi.IconButton(actions, "arrow-down", "Later", () => Edit(() => MoverEdits.MoveKey(Context.Project, _id!, i, 1))).Disabled = i == m.Keys.Count - 1;
        PanelUi.IconButton(actions, "trash", "Delete this key", () => Edit(() => MoverEdits.DeleteKey(Context.Project, _id!, i)), danger: true)
            .Disabled = m.Keys.Count == 1;

        // The key's offset and turn as the Inspector shows a location and rotation: one box per axis, the axis
        // letters in the views' colours.
        MoverKey value = m.Keys[i];
        AxisFields offset = AxisFields.Create(["X", "Y", "Z"]);
        PanelUi.Row(key, "Offset", "Offset from the base in units", EditorTheme.LabelColumnNarrow).AddChild(offset);
        offset.SetValues([value.Offset.X, value.Offset.Y, value.Offset.Z]);
        offset.Edited += (axis, v, final) =>
        {
            if (final)
            {
                Apply(value with { Offset = axis switch
                {
                    0 => value.Offset with { X = v },
                    1 => value.Offset with { Y = v },
                    _ => value.Offset with { Z = v },
                } });
            }
        };
        AxisFields turn = AxisFields.Create(["Pitch", "Yaw", "Roll"]);
        PanelUi.Row(key, "Turn", "Turn from the base: pitch, yaw, roll in rotator units (65536 = a full turn)", EditorTheme.LabelColumnNarrow)
            .AddChild(turn);
        turn.SetValues([value.Turn.Pitch, value.Turn.Yaw, value.Turn.Roll]);
        turn.Edited += (axis, v, final) =>
        {
            if (final)
            {
                Apply(value with { Turn = axis switch
                {
                    0 => value.Turn with { Pitch = (int)Math.Round(v) },
                    1 => value.Turn with { Yaw = (int)Math.Round(v) },
                    _ => value.Turn with { Roll = (int)Math.Round(v) },
                } });
            }
        };

        void Apply(MoverKey next)
        {
            if (next != value)
            {
                Edit(() => MoverEdits.SetKey(Context.Project, _id!, i, next));
            }
        }
    }

    /// <summary>Key <paramref name="i"/>'s own timing: the leg into it from the key before and its easing. The leg time
    /// shows the mover's move time, dimmed, until the leg has its own (reset goes back to the move time); key 0 has no
    /// leg. <see cref="ShowLeg"/> fills the values.</summary>
    private LegFields LegRow(int i)
    {
        HBoxContainer row = PanelUi.Row(_legs, $"Key {i}", i == 0 ? "Key 0 has no leg before it" : $"The leg between key {i - 1} and key {i}, either way",
            EditorTheme.LabelColumnNarrow);
        // One column for the time and its reset in every row, so the easing drop-downs line up.
        var cell = new HBoxContainer { CustomMinimumSize = new Vector2(EditorTheme.SpinMinWidth + EditorTheme.SpaceXs + EditorTheme.IconButtonSize, 0) };
        cell.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        row.AddChild(cell);
        SpinBox? time = null;
        Button? reset = null;
        if (i == 0)
        {
            Label none = PanelUi.Caption(cell, "No leg", wrap: false);
            none.TooltipText = "Key 0 has no leg before it";
        }
        else
        {
            // The box steps in hundredths from its minimum (Range rounds to min + n * step), so it starts at one step:
            // a valid leg time (ShortestLeg is less) on the same grid as the other timing fields.
            time = PanelUi.Spin(cell, Math.Max(MoverTiming.ShortestLeg, TimeStep), MoverTiming.LongestLeg, TimeStep, TimeStep,
                $"Seconds of the leg from key {i - 1}");
            time.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            time.Suffix = "s";
            time.ValueChanged += v =>
            {
                // Against the stored time, compared as stored (float), so a value left as shown is no edit. Steps of
                // the box in a row make one undo step.
                if (_id is { } id && (float)v != StoredLeg(id, i))
                {
                    Edit(() => MoverEdits.SetLegTime(Context.Project, id, i, v, $"mover.leg.{id}.{i}"));
                }
            };
            reset = PanelUi.IconButton(cell, "reset", "Back to the mover's move time", () =>
            {
                if (_id is { } id && StoredLeg(id, i) is not null)
                {
                    Edit(() => MoverEdits.SetLegTime(Context.Project, id, i, null));
                }
            });
        }

        OptionButton easing = PanelUi.Options(row, "How the mover moves at this key");
        easing.FocusMode = FocusModeEnum.None;
        easing.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        easing.AddItem("Default");
        easing.AddItem("Smooth");
        easing.AddItem("At speed");
        easing.ItemSelected += index =>
        {
            if (_id is { } id)
            {
                Edit(() => MoverEdits.SetKeySmooth(Context.Project, id, i, index switch { 1 => true, 2 => false, _ => null }));
            }
        };
        return new LegFields(time, reset, easing);
    }

    /// <summary>Fills leg row <paramref name="i"/> from the mover as it is now.</summary>
    private void ShowLeg(MoverSetup m, int i)
    {
        (SpinBox? time, Button? reset, OptionButton easing) = _legRows[i];
        OpenScct.Core.Engine.MoverKeyTiming own = m.Legs is { } legs && i < legs.Keys.Count ? legs.Keys[i] : default;
        if (time is not null && reset is not null)
        {
            bool stored = own.Time is not null;
            time.SetValueNoSignal(own.Time ?? m.MoveTime);
            LineEdit text = time.GetLineEdit();
            if (stored)
            {
                text.RemoveThemeColorOverride("font_color");
            }
            else
            {
                text.AddThemeColorOverride("font_color", EditorTheme.TextDim);
            }

            time.TooltipText = $"Seconds of the leg from key {i - 1}" + (stored ? "" : ": the mover's move time until the leg has its own");
            // As in the Inspector: the reset keeps its place but shows only when there is something to reset.
            reset.Modulate = stored ? Colors.White : Colors.Transparent;
            reset.Disabled = !stored;
            reset.MouseFilter = stored ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        }

        // Short items: the row leaves the drop-down little room in a narrow dock; the tooltip says the rest.
        easing.Selected = own.Smooth switch { null => 0, true => 1, false => 2 };
        easing.TooltipText = own.Smooth switch
        {
            null => $"Default: the mover's glide ({(m.Eased ? "smooth" : "at speed")}, Ease in and out above)",
            true => "Smooth: eases to a stop at this key and away from it",
            false => "At speed: moves through this key without slowing",
        } + "\nHow the mover moves at this key";
    }

    /// <summary>Key <paramref name="key"/>'s own leg time as stored now, or null for the move time.</summary>
    private float? StoredLeg(string id, int key) =>
        Context.Project.FindActor(id)?.Legs is { } now && key < now.Keys.Count ? now.Keys[key].Time : null;

    /// <summary>A leg row's fields: the time and its reset (none for key 0) and the easing.</summary>
    private readonly record struct LegFields(SpinBox? Time, Button? Reset, OptionButton Easing);

    private void SetTime(string prop, double seconds)
    {
        if (_id is null || Context.Project.FindActor(_id) is not { } a)
        {
            return;
        }

        if (Math.Abs(seconds - ClassDefaults.Number(a, prop, 0)) > 1e-6)
        {
            // Steps of the box in a row make one undo step.
            string id = _id;
            Edit(() => MoverEdits.SetTime(Context.Project, id, prop, seconds, $"mover.time.{id}.{prop}"));
        }
    }

    private void Edit(Func<IEditCommand> edit)
    {
        if (_id is null)
        {
            return;
        }

        try
        {
            Context.Execute(edit());
        }
        catch (ArgumentException ex)
        {
            Context.SetStatus(ex.Message);
        }
    }

    private void WireSelected()
    {
        if (_id is null)
        {
            return;
        }

        List<string> senders = WorkbenchPart.Selected(Context, a => !string.Equals(a.Id, _id, StringComparison.OrdinalIgnoreCase)).Select(a => a.Id).ToList();
        var parts = senders.Select(s => Wiring.Link(Context.Project, s, _id)).OfType<ReplaceObjectsCommand>().Where(c => !c.IsEmpty).ToList();
        if (parts.Count == 0)
        {
            Context.SetStatus("Select the actors that should trigger the mover too");
            return;
        }

        Context.Execute(new CompositeCommand($"{senders.Count} actors trigger {_id}", parts));
        Context.SetStatus($"{string.Join(", ", senders)} now trigger {_id}");
    }

    private void ShowPreview()
    {
        bool mine = _id is not null && string.Equals(MoverPreview.Id, _id, StringComparison.OrdinalIgnoreCase);
        _timeline.Cursor = mine ? MoverPreview.Time : null;
        _previewInfo.Text = mine
            ? string.Create(CultureInfo.InvariantCulture, $"Previewing {MoverPreview.Time:0.00} s into an activation{(MoverPreview.Playing ? " (playing)" : "")}. The map is not changed.")
            : "Click the timeline to see the mover at that moment in the views.";
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

            string text = s.IsWait ? s.Label : $"{s.From} → {s.To}";
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
