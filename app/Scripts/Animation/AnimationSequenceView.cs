using Godot;
using OpenScctEditor.Panels;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Animation;

public partial class AnimationSequenceView : VBoxContainer
{
    private readonly StudioState _state;
    private string? _id;
    private int _track;
    private OptionButton _sequences = null!;
    private SequenceCanvas _canvas = null!;
    private VBoxContainer _details = null!;
    private bool _updating;
    public AnimationSequenceView(StudioState state) => _state = state;
    private AnimationSequence? Sequence => _state.Project.Sequences.FirstOrDefault(s => s.Id == _id);
    public override void _Ready()
    {
        HFlowContainer tools = PanelUi.Toolbar(this);
        _sequences = new OptionButton(); tools.AddChild(_sequences);
        _sequences.ItemSelected += i => { if (!_updating) { _id = _sequences.GetItemMetadata((int)i).AsString(); _track = 0; Refresh(); } };
        PanelUi.Button(tools, "+ Sequence", "Create a new sequence", () =>
        {
            var sequence = new AnimationSequence(); _id = sequence.Id; _state.Session.Edit("Create animation sequence", p => p.Sequences.Add(sequence));
        });
        foreach (SequenceTrackKind kind in Enum.GetValues<SequenceTrackKind>())
            PanelUi.Button(tools, "+ " + kind, "Add a sequence track", () => Edit("Add sequence track", s => s.Tracks.Add(new() { Name = kind.ToString(), Kind = kind, Target = _state.RigId ?? "" })));
        PanelUi.Button(tools, "Preview sequence", "Preview the sequence in the isolated stage", () => { _state.SequenceId = _id; _state.GraphPlayer = null; _state.RangeStart = 0; _state.RangeEnd = Sequence?.Duration ?? 1; _state.Seek(0); _state.Playing = true; });
        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; AddChild(split);
        _canvas = new SequenceCanvas(this) { SizeFlagsHorizontal = SizeFlags.ExpandFill }; split.AddChild(_canvas);
        _details = PanelUi.Scroll(split); PanelUi.ScrollOf(_details).CustomMinimumSize = new(220, 0);
        _state.Changed += Refresh; _state.TimeChanged += Redraw;
        TreeExiting += () => { _state.Changed -= Refresh; _state.TimeChanged -= Redraw; };
        Refresh();
    }
    private void Redraw() => _canvas.QueueRedraw();
    private void Refresh()
    {
        if (!IsNodeReady()) return;
        if (Sequence is null) _id = _state.Project.Sequences.FirstOrDefault()?.Id;
        _updating = true; _sequences.Clear();
        foreach (AnimationSequence sequence in _state.Project.Sequences) { _sequences.AddItem(sequence.Name); int index = _sequences.ItemCount - 1; _sequences.SetItemMetadata(index, sequence.Id); if (sequence.Id == _id) _sequences.Selected = index; }
        foreach (Node child in _details.GetChildren()) { _details.RemoveChild(child); child.QueueFree(); }
        if (Sequence is { } selected)
        {
            PanelUi.Caption(_details, "Duration (s)"); SpinBox duration = PanelUi.Spin(_details, .01, 3600, .1, selected.Duration, "Sequence duration");
            duration.ValueChanged += v => Edit("Sequence duration", s => s.Duration = (float)v);
            if (selected.Tracks.Count > 0)
            {
                _track = Math.Clamp(_track, 0, selected.Tracks.Count - 1); SequenceTrack track = selected.Tracks[_track];
                PanelUi.Header(_details, track.Name);
                var name = new LineEdit { Text = track.Name, PlaceholderText = "Track name" }; _details.AddChild(name);
                name.TextSubmitted += value => Edit("Rename sequence track", s => s.Tracks[_track].Name = value);
                var target = new LineEdit { Text = track.Target, PlaceholderText = "Rig id / actor:ActorId / camera id" }; _details.AddChild(target);
                target.TextSubmitted += value => Edit("Sequence target", s => s.Tracks[_track].Target = value);
                PanelUi.Button(_details, "Insert selected clip", "Insert at the playhead", () =>
                {
                    if (_state.CurrentClip is not { } clip) return;
                    Edit("Insert sequence clip", s => s.Tracks[_track].Segments.Add(new() { Start = _state.Time, Duration = MathF.Min(clip.Duration, s.Duration - _state.Time), Clip = clip.Id }));
                });
                if (track.Kind is SequenceTrackKind.Transform or SequenceTrackKind.Camera)
                {
                    var temporary = new OpenScct.Animation.AnimationClip { Tracks = track.TransformTracks, Duration = selected.Duration };
                    var definition = new SkeletonDefinition { Bones = [new() { Name = "Transform" }] };
                    BoneTransform initial = ClipEvaluator.Evaluate(definition, temporary, _state.Time)[0];
                    PanelUi.Caption(_details, "Position (metres)");
                    SpinBox px = PanelUi.Spin(_details, -100000, 100000, .01, initial.Position.X, "Position X");
                    SpinBox py = PanelUi.Spin(_details, -100000, 100000, .01, initial.Position.Y, "Position Y");
                    SpinBox pz = PanelUi.Spin(_details, -100000, 100000, .01, initial.Position.Z, "Position Z");
                    Vector3 angles = new Quaternion(initial.Rotation.X, initial.Rotation.Y, initial.Rotation.Z, initial.Rotation.W).GetEuler() * (180 / MathF.PI);
                    PanelUi.Caption(_details, "Rotation (degrees)");
                    SpinBox rx = PanelUi.Spin(_details, -3600, 3600, 1, angles.X, "Rotation X");
                    SpinBox ry = PanelUi.Spin(_details, -3600, 3600, 1, angles.Y, "Rotation Y");
                    SpinBox rz = PanelUi.Spin(_details, -3600, 3600, 1, angles.Z, "Rotation Z");
                    PanelUi.Button(_details, "Key transform", "Insert position and rotation keys at the playhead", () => Edit("Key sequence transform", s =>
                    {
                        Quaternion rotation = Quaternion.FromEuler(new Vector3((float)rx.Value, (float)ry.Value, (float)rz.Value) * (MathF.PI / 180));
                        var clip = new OpenScct.Animation.AnimationClip { Tracks = s.Tracks[_track].TransformTracks };
                        AnimationEdits.AddPoseKeys(clip, "Transform", new(new((float)px.Value, (float)py.Value, (float)pz.Value), new(rotation.X, rotation.Y, rotation.Z, rotation.W), initial.Scale), _state.Time);
                    }));
                    PanelUi.Button(_details, "Key selected pose transform", "Key the selected bone's transform on this track", () => Edit("Key sequence transform", s =>
                    {
                        BoneTransform[] pose = _state.Pose(); if (pose.Length == 0) return;
                        var clip = new OpenScct.Animation.AnimationClip { Tracks = s.Tracks[_track].TransformTracks };
                        AnimationEdits.AddPoseKeys(clip, "Transform", pose[Math.Clamp(_state.Bone, 0, pose.Length - 1)], _state.Time);
                    }));
                    if (track.Kind == SequenceTrackKind.Camera)
                    {
                        SpinBox fov = PanelUi.Spin(_details, 1, 179, 1, 60, "Camera field of view");
                        PanelUi.Button(_details, "Key field of view", "Key camera FOV at the playhead", () => Edit("Key camera field of view", s =>
                        {
                            List<AnimationKey> keys = s.Tracks[_track].FieldOfView; keys.RemoveAll(k => k.Time == _state.Time); keys.Add(new() { Time = _state.Time, Value = new((float)fov.Value, 0, 0, 0) }); keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                        }));
                    }
                }
                var asset = new LineEdit { PlaceholderText = "Sound path / event tag" }; _details.AddChild(asset);
                if (track.Kind == SequenceTrackKind.Audio)
                    PanelUi.Button(_details, "Choose audio file…", "Copy WAV or Ogg Vorbis into the animation project", () =>
                        Dialogs.OpenFile("Sequence sound", ["*.wav,*.ogg;Audio"], path =>
                        {
                            if (_state.Session.Folder is not { } folder) { TooltipText = "Save the animation project before importing audio."; return; }
                            string destination = Path.Combine(folder, "assets", "audio", Path.GetFileName(path)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            if (!Path.GetFullPath(path).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) File.Copy(path, destination, true);
                            asset.Text = Path.GetRelativePath(folder, destination).Replace('\\', '/');
                        }));
                PanelUi.Button(_details, track.Kind == SequenceTrackKind.Audio ? "Insert sound" : "Insert trigger event", "Insert at playhead", () => Edit("Insert sequence event", s =>
                {
                    if (track.Kind == SequenceTrackKind.Audio) s.Tracks[_track].Segments.Add(new() { Start = _state.Time, Duration = MathF.Min(1, s.Duration - _state.Time), Asset = asset.Text });
                    else s.Tracks[_track].Events.Add(new() { Time = _state.Time, Kind = AnimationEventKind.Trigger, Target = asset.Text });
                }));
                for (int i = 0; i < track.Segments.Count; i++)
                {
                    int index = i; SequenceSegment segment = track.Segments[i];
                    PanelUi.Caption(_details, "Segment " + (i + 1));
                    SpinBox start = PanelUi.Spin(_details, 0, selected.Duration, .01, segment.Start, "Start (s)"); start.ValueChanged += v => Edit("Move sequence segment", s => s.Tracks[_track].Segments[index].Start = (float)v);
                    SpinBox length = PanelUi.Spin(_details, .001, selected.Duration, .01, segment.Duration, "Duration (s)"); length.ValueChanged += v => Edit("Resize sequence segment", s => s.Tracks[_track].Segments[index].Duration = (float)v);
                    SpinBox rate = PanelUi.Spin(_details, .01, 10, .05, segment.Rate, "Playback rate"); rate.ValueChanged += v => Edit("Sequence clip rate", s => s.Tracks[_track].Segments[index].Rate = (float)v);
                    SpinBox blend = PanelUi.Spin(_details, 0, selected.Duration, .01, segment.BlendIn, "Blend in (s)"); blend.ValueChanged += v => Edit("Sequence blend in", s => s.Tracks[_track].Segments[index].BlendIn = (float)v);
                    PanelUi.Button(_details, "Remove segment", "Delete this sequence segment", () => Edit("Remove sequence segment", s => s.Tracks[_track].Segments.RemoveAt(index)));
                }
            }
            var tag = new LineEdit { PlaceholderText = "Map trigger tag" }; _details.AddChild(tag);
            PanelUi.Button(_details, "Bind to map trigger", "SMagicEvent can trigger this sequence by tag", () => _state.Session.Edit("Bind sequence trigger", p => p.Bindings.Add(new() { Target = "sequence:" + tag.Text, Slot = tag.Text, Sequence = selected.Id, Mode = ReplacementMode.Gameplay })));
        }
        _updating = false; _canvas.QueueRedraw();
    }
    private void Edit(string name, Action<AnimationSequence> edit)
    {
        if (_id is not { } id) return;
        try { _state.Session.Edit(name, p => edit(p.Sequences.Single(s => s.Id == id))); }
        catch (InvalidDataException e) { TooltipText = e.Message; }
    }
    private partial class SequenceCanvas : Control
    {
        private readonly AnimationSequenceView _owner;
        public SequenceCanvas(AnimationSequenceView owner) { _owner = owner; ClipContents = true; MouseFilter = MouseFilterEnum.Stop; }
        public override void _Draw()
        {
            DrawRect(new(Vector2.Zero, Size), EditorTheme.Field);
            if (_owner.Sequence is not { } sequence) return;
            float scale = MathF.Max(1, Size.X - 150) / sequence.Duration;
            Font font = GetThemeDefaultFont();
            for (int i = 0; i < sequence.Tracks.Count; i++)
            {
                SequenceTrack track = sequence.Tracks[i]; float y = 24 + i * 30;
                if (_owner._track == i) DrawRect(new(0, y - 16, Size.X, 30), EditorTheme.Selected);
                DrawString(font, new(6, y + 4), track.Name, width: 140, fontSize: EditorTheme.FontSizeCaption, modulate: EditorTheme.Text);
                foreach (SequenceSegment segment in track.Segments)
                {
                    var rect = new Rect2(150 + segment.Start * scale, y - 12, segment.Duration * scale, 24);
                    DrawRect(rect, EditorTheme.TimelineSegment);
                    DrawString(font, rect.Position + new Vector2(4, 16), _owner._state.Project.Clips.FirstOrDefault(c => c.Id == segment.Clip)?.Name ?? segment.Asset ?? "Segment", width: MathF.Max(0, rect.Size.X - 8), fontSize: EditorTheme.FontSizeCaption, modulate: EditorTheme.Text);
                }
                foreach (AnimationEvent e in track.Events) DrawCircle(new(150 + e.Time * scale, y), 4, EditorTheme.TimelineEvent);
            }
            float x = 150 + _owner._state.Time * scale; DrawLine(new(x, 0), new(x, Size.Y), EditorTheme.Playhead, 2);
        }
        public override void _GuiInput(InputEvent e)
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse || _owner.Sequence is not { } sequence) return;
            _owner._track = Math.Clamp((int)((mouse.Position.Y - 8) / 30), 0, Math.Max(0, sequence.Tracks.Count - 1));
            _owner._state.SequenceId = sequence.Id;
            _owner._state.Seek(Math.Clamp((mouse.Position.X - 150) / MathF.Max(1, Size.X - 150) * sequence.Duration, 0, sequence.Duration));
            _owner.Refresh(); AcceptEvent();
        }
    }
}
