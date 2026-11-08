using Godot;
using OpenScctEditor.Panels;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;
using OpenScctEditor.Shell;
using NVector4 = System.Numerics.Vector4;

namespace OpenScctEditor.Animation;

public partial class AnimationTimeline : VBoxContainer
{
    private readonly StudioState _state;
    private readonly bool _curves;
    private TimelineCanvas _canvas = null!;
    private readonly HashSet<(int Track, float Time)> _selected = [];
    private List<(string Bone, TrackChannel Channel, AnimationKey Key)> _clipboard = [];
    private int _component;
    private int _channel;
    private bool _rootTracks;
    private string? _selectionContext;
    private Button _smoothBetween = null!;
    public bool HasSelectedKeys => _selected.Count > 0;
    public void SelectKeys(bool all)
    {
        _selected.Clear();
        if (all) for (int i = 0; i < Tracks.Count; i++) if (TrackVisible(Tracks[i])) foreach (AnimationKey key in Tracks[i].Keys) _selected.Add((i, key.Time));
        UpdateSelectionTools();
        _canvas.QueueRedraw();
    }
    public AnimationTimeline(StudioState state, bool curves = false) { _state = state; _curves = curves; }
    internal List<BoneTrack> Tracks => _state.CurrentClip is not { } clip ? [] : _rootTracks ? clip.RootTracks : _state.Layer < 0 ? clip.Tracks : clip.Layers[_state.Layer].Tracks;
    private bool TrackVisible(BoneTrack track) => !_curves || (_channel == 0 || (int)track.Channel == _channel - 1)
        && (_rootTracks || _state.Skeleton is not { } skeleton || _state.Bone < 0 || _state.Bone >= skeleton.Bones.Count || track.Bone == skeleton.Bones[_state.Bone].Name);
    internal IReadOnlyDictionary<(int Track, float Time), Vector2> KeyPoints => _canvas.Points;
    internal Rect2 PlotRect => _canvas.PlotRect;
    internal int SelectedKeyCount => _selected.Count;
    public void FrameKeys() => _canvas.FrameKeys();
    public override void _Ready()
    {
        HFlowContainer tools = PanelUi.Toolbar(this);
        PanelUi.Toggle(tools, "Root motion", "Edit the separate gameplay root tracks", false, value => { _rootTracks = value; Redraw(); });
        PanelUi.Button(tools, "Key root", "Add root-motion transform keys at the playhead", () =>
        {
            if (_state.CurrentClip is not { } clip) return;
            _state.Session.EditClip("Key root motion", clip.Id, edited =>
            {
                var target = new OpenScct.Animation.AnimationClip { Tracks = edited.RootTracks };
                OpenScct.Editor.Core.Animation.AnimationEdits.AddPoseKeys(target, "RootMotion", ClipEvaluator.RootMotion(edited, _state.Time), _state.Time);
            });
            _rootTracks = true; _canvas.QueueRedraw();
        });
        PanelUi.Button(tools, "Copy", "Copy selected keys", Copy); PanelUi.Button(tools, "Paste", "Paste at playhead", Paste);
        PanelUi.Button(tools, "Delete", "Delete selected keys", Delete);
        foreach (KeyInterpolation interpolation in Enum.GetValues<KeyInterpolation>())
            PanelUi.Button(tools, interpolation.ToString(), "Set selected key interpolation", () => Edit("Key interpolation", (key, _) => key.Interpolation = interpolation));
        _smoothBetween = PanelUi.Button(tools, "Smooth between", "", SmoothBetween);
        _smoothBetween.Name = "AnimationSmoothBetween";
        UpdateSelectionTools();
        if (_curves)
        {
            PanelUi.Button(tools, "Frame keys", "Fit selected keys, or all visible keys when none are selected", FrameKeys);
            var channel = new OptionButton { Name = "CurveChannel" }; foreach (string name in new[] { "All channels", "Position", "Rotation", "Scale" }) channel.AddItem(name); tools.AddChild(channel);
            channel.ItemSelected += i => { _channel = (int)i; Redraw(); };
            var component = new OptionButton(); foreach (string axis in new[] { "X", "Y", "Z", "W" }) component.AddItem(axis); tools.AddChild(component);
            component.Name = "CurveComponent";
            component.ItemSelected += i => { _component = (int)i; Redraw(); };
            var incoming = new HBoxContainer(); tools.AddChild(incoming);
            PanelUi.Caption(incoming, "In", false); SpinBox input = PanelUi.Spin(incoming, -10000, 10000, .1, 0, "Incoming tangent", 70);
            input.ValueChanged += v => Edit("Incoming key tangent", (key, _) => key.InTangent = Set(key.InTangent, (float)v));
            var outgoing = new HBoxContainer(); tools.AddChild(outgoing);
            PanelUi.Caption(outgoing, "Out", false); SpinBox output = PanelUi.Spin(outgoing, -10000, 10000, .1, 0, "Outgoing tangent", 70);
            output.ValueChanged += v => Edit("Outgoing key tangent", (key, _) => key.OutTangent = Set(key.OutTangent, (float)v));
        }
        _canvas = new TimelineCanvas(this) { Name = _curves ? "AnimationCurves" : "AnimationDopeSheet", SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 120) };
        AddChild(_canvas);
        _state.Changed += Redraw; _state.TimeChanged += Redraw;
        TreeExiting += () => { _state.Changed -= Redraw; _state.TimeChanged -= Redraw; };
    }
    private void Redraw()
    {
        string context = _state.ClipId + "/" + _state.Layer + "/" + _rootTracks + (_curves ? "/" + _state.Bone + "/" + _component + "/" + _channel : "");
        if (_selectionContext != context) { _selectionContext = context; _selected.Clear(); _canvas.RequestFit(); }
        _canvas.CheckKeyCount();
        UpdateSelectionTools();
        _canvas.QueueRedraw();
    }
    private (int Track, float Start, float End)[] SelectedRanges()
    {
        List<BoneTrack> tracks = Tracks;
        return _selected.Where(k => k.Track >= 0 && k.Track < tracks.Count && TrackVisible(tracks[k.Track]))
            .GroupBy(k => k.Track).Select(g =>
            {
                var selected = g.Select(k => k.Time).ToHashSet();
                float[] times = tracks[g.Key].Keys.Where(k => selected.Contains(k.Time)).Select(k => k.Time).ToArray();
                return (Track: g.Key, Times: times);
            }).Where(g => g.Times.Length >= 2).Select(g => (g.Track, g.Times.Min(), g.Times.Max())).ToArray();
    }
    private void UpdateSelectionTools()
    {
        if (_smoothBetween is null) return;
        _smoothBetween.Disabled = SelectedRanges().Length == 0;
        _smoothBetween.TooltipText = _smoothBetween.Disabled ? "Select two endpoint keys on the same track (Ctrl-click the second)."
            : "Replace keys between the first and last selected key on each track with eased motion on all axes. Endpoint poses and outside motion are preserved. Undo restores the original keys.";
    }
    private void SmoothBetween()
    {
        var ranges = SelectedRanges();
        if (ranges.Length == 0 || _state.CurrentClip is not { } clip) return;
        _state.Playing = false;
        _state.Session.EditClip("Smooth between animation keys", clip.Id, _ =>
        {
            foreach (var (track, start, end) in ranges) AnimationEdits.SmoothBetween(Tracks[track], start, end, clip.FramesPerSecond);
        });
        _selected.Clear();
        foreach (var (track, start, end) in ranges) { _selected.Add((track, start)); _selected.Add((track, end)); }
        UpdateSelectionTools(); _canvas.RequestFit();
    }
    private float Get(NVector4 v) => _component switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };
    private NVector4 Set(NVector4 v, float value) => _component switch { 0 => v with { X = value }, 1 => v with { Y = value }, 2 => v with { Z = value }, _ => v with { W = value } };
    private void Edit(string name, Action<AnimationKey, BoneTrack> edit)
    {
        if (_selected.Count == 0) return;
        _state.Session.EditClip(name, _state.ClipId!, _ =>
        {
            List<BoneTrack> tracks = Tracks;
            foreach (var (index, time) in _selected)
                if (index >= 0 && index < tracks.Count && tracks[index].Keys.FirstOrDefault(k => k.Time == time) is { } key) edit(key, tracks[index]);
        });
    }
    public void Copy()
    {
        _clipboard.Clear();
        foreach (var (index, time) in _selected.OrderBy(x => x.Time))
            if (index >= 0 && index < Tracks.Count && Tracks[index].Keys.FirstOrDefault(k => k.Time == time) is { } key)
                _clipboard.Add((Tracks[index].Bone, Tracks[index].Channel, AnimationJson.Clone(key)));
    }
    public void Paste()
    {
        if (_clipboard.Count == 0 || _state.CurrentClip is null) return;
        float origin = _clipboard.Min(k => k.Key.Time);
        _state.Session.EditClip("Paste animation keys", _state.ClipId!, _ =>
        {
            foreach (var (bone, channel, copied) in _clipboard)
            {
                BoneTrack? track = Tracks.FirstOrDefault(t => t.Bone == bone && t.Channel == channel);
                if (track is null) { track = new() { Bone = bone, Channel = channel }; Tracks.Add(track); }
                AnimationKey key = AnimationJson.Clone(copied); key.Time = Math.Clamp(_state.Time + key.Time - origin, 0, _state.CurrentClip!.Duration);
                track.Keys.RemoveAll(k => MathF.Abs(k.Time - key.Time) < .00001f); track.Keys.Add(key); track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
        });
    }
    public void Delete()
    {
        if (_selected.Count == 0 || _state.CurrentClip is null) return;
        _state.Session.EditClip("Delete animation keys", _state.ClipId!, _ =>
        {
            foreach (var (index, time) in _selected) if (index >= 0 && index < Tracks.Count) Tracks[index].Keys.RemoveAll(k => k.Time == time);
        });
        _selected.Clear();
        UpdateSelectionTools();
    }
    private partial class TimelineCanvas : Control
    {
        private readonly AnimationTimeline _owner;
        private float _zoom = 100, _scroll, _vertical, _delta, _valueDelta;
        private bool _drag, _scrub;
        private bool _fit = true;
        private int _keyCount = -1;
        private float _valueCenter, _valueScale = 45;
        private Vector2 _start;
        private readonly Dictionary<(int Track, float Time), Vector2> _points = [];
        internal IReadOnlyDictionary<(int Track, float Time), Vector2> Points => _points;
        internal Rect2 PlotRect => new(Labels, Ruler, MathF.Max(1, Size.X - Labels), MathF.Max(1, Size.Y - Ruler));
        private const float Labels = 160, Ruler = 24, Row = 24;
        public TimelineCanvas(AnimationTimeline owner)
        {
            _owner = owner; MouseFilter = MouseFilterEnum.Stop; ClipContents = true;
            TooltipText = "Click a key to select · Ctrl-click adds keys · Ctrl+A selects visible keys · Drag to move · Ctrl-wheel zooms time · Wheel pans values · Alt-wheel zooms values";
            Resized += RequestFit;
        }
        internal void RequestFit() { if (_owner._curves) _fit = true; QueueRedraw(); }
        internal void CheckKeyCount()
        {
            int count = _owner.Tracks.Where(_owner.TrackVisible).Sum(t => t.Keys.Count);
            if (count != _keyCount) { _keyCount = count; RequestFit(); }
        }
        internal void FrameKeys()
        {
            float start = 0, end = _owner._state.Duration;
            var selected = _owner._selected.Where(k => k.Track < _owner.Tracks.Count && _owner.TrackVisible(_owner.Tracks[k.Track])).ToArray();
            if (selected.Length > 0) { start = selected.Min(k => k.Time); end = selected.Max(k => k.Time); }
            if (end - start < .001f) { start = MathF.Max(0, start - .5f); end = start + 1; }
            _zoom = MathF.Max(1, PlotRect.Size.X - 24) / MathF.Max(.001f, end - start);
            _scroll = start * _zoom - 12;
            float low = float.PositiveInfinity, high = float.NegativeInfinity;
            void Include(float value) { if (float.IsFinite(value)) { low = MathF.Min(low, value); high = MathF.Max(high, value); } }
            foreach (BoneTrack track in _owner.Tracks.Where(_owner.TrackVisible))
            {
                foreach (AnimationKey key in track.Keys.Where(k => k.Time >= start && k.Time <= end)) Include(_owner.Get(key.Value));
                // Include interpolation extrema as well as keys, including cubic overshoot.
                for (int n = 0; n <= 512 && track.Keys.Count > 0; n++) Include(_owner.Get(ClipEvaluator.Sample(track.Keys, start + (end - start) * n / 512, track.Channel == TrackChannel.Rotation)));
            }
            if (!float.IsFinite(low)) { low = -1; high = 1; }
            float span = MathF.Max(high - low, MathF.Max(.01f, MathF.Abs(low) * .1f));
            _valueCenter = (low + high) * .5f;
            _valueScale = MathF.Max(1, PlotRect.Size.Y - 32) / span;
            _fit = false; QueueRedraw();
        }
        private float X(float time) => Labels + time * _zoom - _scroll;
        private float Time(float x) => Math.Clamp((x - Labels + _scroll) / _zoom, 0, _owner._state.Duration);
        private float Y(int row, float value) => _owner._curves ? Ruler + PlotRect.Size.Y * .5f - (value - _valueCenter) * _valueScale : Ruler + row * Row + Row * .5f - _vertical;
        private void CurveLine(Vector2 a, Vector2 b, Color color)
        {
            Vector2 delta = b - a; float enter = 0, exit = 1;
            bool Edge(float p, float q)
            {
                if (MathF.Abs(p) < .000001f) return q >= 0;
                float ratio = q / p;
                if (p < 0) enter = MathF.Max(enter, ratio); else exit = MathF.Min(exit, ratio);
                return enter <= exit;
            }
            if (Edge(-delta.X, a.X - Labels) && Edge(delta.X, Size.X - a.X) && Edge(-delta.Y, a.Y - Ruler) && Edge(delta.Y, Size.Y - a.Y))
                DrawLine(a + delta * enter, a + delta * exit, color, 1.5f, true);
        }
        public override void _Draw()
        {
            if (_fit && _owner._curves) FrameKeys();
            DrawRect(new(Vector2.Zero, Size), EditorTheme.Field); _points.Clear();
            Font font = GetThemeDefaultFont(); int fontSize = EditorTheme.FontSizeCaption;
            DrawRect(new(0, 0, Size.X, Ruler), EditorTheme.Bar);
            float roughStep = 105 / _zoom;
            float magnitude = MathF.Pow(10, MathF.Floor(MathF.Log10(MathF.Max(.0001f, roughStep))));
            float step = magnitude * (roughStep / magnitude <= 2 ? 2 : roughStep / magnitude <= 5 ? 5 : 10);
            for (float t = 0; t <= _owner._state.Duration; t += step)
            {
                float x = X(t); if (x < Labels || x > Size.X) continue;
                DrawLine(new(x, Ruler), new(x, Size.Y), EditorTheme.Divider);
                DrawString(font, new(x + 3, 16), $"{t * _owner._state.Project.FramesPerSecond:0}f · {t:0.##}s", fontSize: fontSize, modulate: EditorTheme.TextDim);
            }
            if (_owner._curves)
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = Ruler + 16 + (PlotRect.Size.Y - 32) * i / 4;
                    float value = _valueCenter + (Ruler + PlotRect.Size.Y * .5f - y) / _valueScale;
                    DrawLine(new(Labels, y), new(Size.X, y), EditorTheme.Divider);
                    DrawString(font, new(5, y + 4), value.ToString("G5", System.Globalization.CultureInfo.InvariantCulture), width: Labels - 12, fontSize: fontSize, modulate: EditorTheme.TextDim);
                }
            }
            List<BoneTrack> tracks = _owner.Tracks;
            for (int index = 0; index < tracks.Count; index++)
            {
                BoneTrack track = tracks[index];
                if (!_owner.TrackVisible(track)) continue;
                float rowY = Y(index, 0);
                Color color = track.Channel switch { TrackChannel.Translation => EditorTheme.AxisX, TrackChannel.Rotation => EditorTheme.AxisY, _ => EditorTheme.AxisZ };
                if (!_owner._curves)
                {
                    DrawLine(new(0, rowY + Row * .5f), new(Size.X, rowY + Row * .5f), EditorTheme.Divider);
                    DrawString(font, new(6, rowY + 4), track.Bone + " · " + track.Channel.ToString()[0], width: Labels - 10, fontSize: fontSize, modulate: EditorTheme.Text);
                }
                else
                {
                    Vector2? last = null;
                    int count = Math.Min(2000, Math.Max(2, (int)(_owner._state.Duration * _zoom / 3)));
                    for (int n = 0; n <= count; n++)
                    {
                        float time = _owner._state.Duration * n / count;
                        Vector2 point = new(X(time), Y(index, _owner.Get(ClipEvaluator.Sample(track.Keys, time, track.Channel == TrackChannel.Rotation))));
                        if (last is { } previous) CurveLine(previous, point, color); last = point;
                    }
                }
                foreach (AnimationKey key in track.Keys)
                {
                    bool selected = _owner._selected.Contains((index, key.Time));
                    Vector2 position = new(X(key.Time + (selected ? _delta : 0)), Y(index, _owner.Get(key.Value) + (selected ? _valueDelta : 0)));
                    if (position.X < Labels || position.X > Size.X || position.Y < Ruler || position.Y > Size.Y) continue;
                    _points[(index, key.Time)] = position;
                    DrawCircle(position, selected ? 5.5f : 4, EditorTheme.Field);
                    DrawArc(position, selected ? 5.5f : 4, 0, MathF.Tau, 12, selected ? EditorTheme.Accent : color, selected ? 2.5f : 1.5f, true);
                }
            }
            if (_owner._curves)
            {
                DrawRect(new(0, 0, Labels, Ruler), EditorTheme.Bar);
                DrawString(font, new(6, 16), $"{_owner.Tracks.Where(_owner.TrackVisible).Sum(t => t.Keys.Count)} keys · {_owner._selected.Count} selected", fontSize: fontSize, modulate: EditorTheme.Text);
            }
            float playhead = X(_owner._state.Time);
            if (playhead >= Labels && playhead <= Size.X) DrawLine(new(playhead, 0), new(playhead, Size.Y), EditorTheme.Playhead, 2);
            if (!tracks.Any(t => _owner.TrackVisible(t) && t.Keys.Count > 0)) DrawString(font, new(Labels + 16, 58), "No keys here. Pose this bone and press I.", fontSize: fontSize, modulate: EditorTheme.TextDim);
        }
        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton mouse)
            {
                if (mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && mouse.Pressed)
                {
                    float sign = mouse.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                    if (mouse.CtrlPressed) _zoom = Math.Clamp(_zoom * (sign > 0 ? 1.2f : .8f), 10, 2000);
                    else if (mouse.ShiftPressed) _scroll -= sign * 60;
                    else if (_owner._curves && mouse.AltPressed) _valueScale = Math.Clamp(_valueScale * (sign > 0 ? 1.2f : .8f), .000001f, 1000000);
                    else if (_owner._curves) _valueCenter += sign * Row * 2 / _valueScale;
                    else _vertical = MathF.Max(0, _vertical - sign * Row * 2);
                    QueueRedraw(); AcceptEvent(); return;
                }
                if (mouse.ButtonIndex != MouseButton.Left) return;
                if (mouse.Pressed)
                {
                    var nearest = _points.OrderBy(p => p.Value.DistanceSquaredTo(mouse.Position)).FirstOrDefault();
                    if (mouse.Position.Y >= Ruler && _points.Count > 0 && nearest.Value.DistanceTo(mouse.Position) < 9)
                    {
                        if (!mouse.CtrlPressed && !mouse.ShiftPressed && !_owner._selected.Contains(nearest.Key)) _owner._selected.Clear();
                        if (mouse.CtrlPressed && _owner._selected.Contains(nearest.Key)) _owner._selected.Remove(nearest.Key); else _owner._selected.Add(nearest.Key);
                        _drag = true; _start = mouse.Position;
                    }
                    else
                    {
                        if (!mouse.ShiftPressed) _owner._selected.Clear();
                        _scrub = true; _owner._state.Playing = false;
                        _owner._state.Seek(Time(mouse.Position.X));
                    }
                }
                else if (_drag)
                {
                    _drag = false;
                    if (_delta != 0 || _valueDelta != 0)
                    {
                        float delta = _delta, value = _valueDelta; _delta = _valueDelta = 0;
                        try
                        {
                            _owner._state.Session.EditClip("Move animation keys", _owner._state.ClipId!, _ =>
                            {
                                var keys = _owner._selected.Where(k => k.Track < _owner.Tracks.Count)
                                    .Select(k => (Track: _owner.Tracks[k.Track], Key: _owner.Tracks[k.Track].Keys.FirstOrDefault(key => key.Time == k.Time), Time: k.Time)).ToArray();
                                foreach (var (track, key, time) in keys)
                                    if (key is not null)
                                    {
                                        key.Time = Math.Clamp(time + delta, 0, _owner._state.Duration);
                                        key.Value = _owner.Set(key.Value, _owner.Get(key.Value) + value);
                                        if (track.Channel == TrackChannel.Rotation) key.Value = ClipEvaluator.V(ClipEvaluator.SafeRotation(key.Value));
                                    }
                                foreach (BoneTrack track in _owner.Tracks) track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                            });
                            _owner._selected.Clear();
                        }
                        catch (InvalidDataException) { /* Colliding keys are rejected as one undoable transaction. */ }
                    }
                }
                if (!mouse.Pressed) _scrub = false;
                _owner.UpdateSelectionTools(); QueueRedraw(); AcceptEvent();
            }
            if (e is InputEventMouseMotion scrub && _scrub)
            {
                _owner._state.Seek(Time(scrub.Position.X)); AcceptEvent();
            }
            if (e is InputEventMouseMotion motion && _drag)
            {
                _delta = (motion.Position.X - _start.X) / _zoom;
                if (!motion.AltPressed) _delta = MathF.Round(_delta * _owner._state.Project.FramesPerSecond) / _owner._state.Project.FramesPerSecond;
                _valueDelta = _owner._curves ? -(motion.Position.Y - _start.Y) / _valueScale : 0;
                QueueRedraw(); AcceptEvent();
            }
        }
    }
}
