using Godot;
using OpenScctEditor.Panels;
using OpenScct.Animation;
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
    private bool _rootTracks;
    private string? _selectionContext;
    public bool HasSelectedKeys => _selected.Count > 0;
    public void SelectKeys(bool all)
    {
        _selected.Clear();
        if (all) for (int i = 0; i < Tracks.Count; i++) foreach (AnimationKey key in Tracks[i].Keys) _selected.Add((i, key.Time));
        _canvas.QueueRedraw();
    }
    public AnimationTimeline(StudioState state, bool curves = false) { _state = state; _curves = curves; }
    internal List<BoneTrack> Tracks => _state.CurrentClip is not { } clip ? [] : _rootTracks ? clip.RootTracks : _state.Layer < 0 ? clip.Tracks : clip.Layers[_state.Layer].Tracks;
    public override void _Ready()
    {
        HFlowContainer tools = PanelUi.Toolbar(this);
        PanelUi.Toggle(tools, "Root motion", "Edit the separate gameplay root tracks", false, value => { _rootTracks = value; _selected.Clear(); _canvas.QueueRedraw(); });
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
        if (_curves)
        {
            var component = new OptionButton(); foreach (string axis in new[] { "X", "Y", "Z", "W" }) component.AddItem(axis); tools.AddChild(component);
            component.ItemSelected += i => { _component = (int)i; _canvas.QueueRedraw(); };
            PanelUi.Caption(tools, "In", false); SpinBox input = PanelUi.Spin(tools, -10000, 10000, .1, 0, "Incoming tangent", 70);
            input.ValueChanged += v => Edit("Incoming key tangent", (key, _) => key.InTangent = Set(key.InTangent, (float)v));
            PanelUi.Caption(tools, "Out", false); SpinBox output = PanelUi.Spin(tools, -10000, 10000, .1, 0, "Outgoing tangent", 70);
            output.ValueChanged += v => Edit("Outgoing key tangent", (key, _) => key.OutTangent = Set(key.OutTangent, (float)v));
        }
        _canvas = new TimelineCanvas(this) { Name = _curves ? "AnimationCurves" : "AnimationDopeSheet", SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 120) };
        AddChild(_canvas);
        _state.Changed += Redraw; _state.TimeChanged += Redraw;
        TreeExiting += () => { _state.Changed -= Redraw; _state.TimeChanged -= Redraw; };
    }
    private void Redraw()
    {
        string context = _state.ClipId + "/" + _state.Layer + "/" + _rootTracks;
        if (_selectionContext != context) { _selectionContext = context; _selected.Clear(); }
        _canvas.QueueRedraw();
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
    }
    private partial class TimelineCanvas : Control
    {
        private readonly AnimationTimeline _owner;
        private float _zoom = 100, _scroll, _vertical, _delta, _valueDelta;
        private bool _drag;
        private Vector2 _start;
        private readonly Dictionary<(int Track, float Time), Vector2> _points = [];
        private const float Labels = 160, Ruler = 24, Row = 24;
        public TimelineCanvas(AnimationTimeline owner) { _owner = owner; MouseFilter = MouseFilterEnum.Stop; ClipContents = true; }
        private float X(float time) => Labels + time * _zoom - _scroll;
        private float Time(float x) => Math.Clamp((x - Labels + _scroll) / _zoom, 0, _owner._state.Duration);
        private float Y(int row, float value) => _owner._curves ? Size.Y * .55f - value * 45 - _vertical : Ruler + row * Row + Row * .5f - _vertical;
        public override void _Draw()
        {
            DrawRect(new(Vector2.Zero, Size), EditorTheme.Field); _points.Clear();
            Font font = GetThemeDefaultFont(); int fontSize = EditorTheme.FontSizeCaption;
            DrawRect(new(0, 0, Size.X, Ruler), EditorTheme.Bar);
            float step = _zoom > 180 ? .1f : _zoom > 60 ? .5f : 1;
            for (float t = 0; t <= _owner._state.Duration; t += step)
            {
                float x = X(t); if (x < Labels || x > Size.X) continue;
                DrawLine(new(x, Ruler), new(x, Size.Y), EditorTheme.Divider);
                DrawString(font, new(x + 3, 16), t.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), fontSize: fontSize, modulate: EditorTheme.TextDim);
            }
            List<BoneTrack> tracks = _owner.Tracks;
            for (int index = 0; index < tracks.Count; index++)
            {
                BoneTrack track = tracks[index];
                if (_owner._curves && _owner._state.Skeleton is { } skeleton && _owner._state.Bone < skeleton.Bones.Count && track.Bone != skeleton.Bones[_owner._state.Bone].Name) continue;
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
                        if (last is { } previous) DrawLine(previous, point, color, 1.5f, true); last = point;
                    }
                }
                foreach (AnimationKey key in track.Keys)
                {
                    bool selected = _owner._selected.Contains((index, key.Time));
                    Vector2 position = new(X(key.Time + (selected ? _delta : 0)), Y(index, _owner.Get(key.Value) + (selected ? _valueDelta : 0)));
                    if (position.X < Labels || position.X > Size.X || position.Y < Ruler || position.Y > Size.Y) continue;
                    _points[(index, key.Time)] = position;
                    DrawCircle(position, selected ? 5 : 3.5f, selected ? EditorTheme.Accent : color);
                }
            }
            float playhead = X(_owner._state.Time); DrawLine(new(playhead, 0), new(playhead, Size.Y), EditorTheme.Playhead, 2);
            if (tracks.Count == 0) DrawString(font, new(Labels + 16, 58), "Pose a bone and press I to create keys", fontSize: fontSize, modulate: EditorTheme.TextDim);
        }
        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton mouse)
            {
                if (mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && mouse.Pressed)
                {
                    float sign = mouse.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                    if (mouse.CtrlPressed) _zoom = Math.Clamp(_zoom * (sign > 0 ? 1.2f : .8f), 10, 2000);
                    else if (mouse.ShiftPressed) _scroll = MathF.Max(0, _scroll - sign * 60);
                    else _vertical = MathF.Max(0, _vertical - sign * Row * 2);
                    QueueRedraw(); AcceptEvent(); return;
                }
                if (mouse.ButtonIndex != MouseButton.Left) return;
                if (mouse.Pressed)
                {
                    var nearest = _points.OrderBy(p => p.Value.DistanceSquaredTo(mouse.Position)).FirstOrDefault();
                    if (_points.Count > 0 && nearest.Value.DistanceTo(mouse.Position) < 9)
                    {
                        if (!mouse.CtrlPressed && !mouse.ShiftPressed && !_owner._selected.Contains(nearest.Key)) _owner._selected.Clear();
                        if (mouse.CtrlPressed && _owner._selected.Contains(nearest.Key)) _owner._selected.Remove(nearest.Key); else _owner._selected.Add(nearest.Key);
                        _drag = true; _start = mouse.Position;
                    }
                    else
                    {
                        if (!mouse.ShiftPressed) _owner._selected.Clear();
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
                QueueRedraw(); AcceptEvent();
            }
            if (e is InputEventMouseMotion motion && _drag)
            {
                _delta = (motion.Position.X - _start.X) / _zoom;
                if (!motion.AltPressed) _delta = MathF.Round(_delta * _owner._state.Project.FramesPerSecond) / _owner._state.Project.FramesPerSecond;
                _valueDelta = _owner._curves ? -(motion.Position.Y - _start.Y) / 45 : 0;
                QueueRedraw(); AcceptEvent();
            }
        }
    }
}
