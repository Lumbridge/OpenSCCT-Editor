using Godot;
using OpenScct.Animation;
using OpenScctGame.Render.Characters;
using OpenScctEditor.Shell;
using NVector3 = System.Numerics.Vector3;

namespace OpenScctEditor.Animation;

/// <summary>Isolated animation stage. It never mutates the map or imported scene resources.</summary>
public partial class AnimationStage : SubViewportContainer
{
    private StudioState _state = null!;
    private Node3D _world = null!;
    private Camera3D _camera = null!;
    private MeshInstance3D _lines = null!;
    private MeshInstance3D _floor = null!;
    private MeshInstance3D _grid = null!;
    private float _ground;
    private string? _sceneKey;
    private readonly List<(int Axis, Vector2 A, Vector2 B)> _handles = [];
    private readonly Dictionary<int, Vector2> _tips = [];
    private Vector2 _dragDirection;
    private Vector3 _gizmoOrigin, _moveOrigin, _moveAxis, _moveGrab;
    private readonly Vector3[] _gizmoAxes = new Vector3[3];
    private Plane _movePlane;
    private System.Numerics.Matrix4x4 _moveParentInverse;
    private BoneTransform _beforeLocal;
    private readonly Dictionary<string, (Node3D Root, Skeleton3D? Skeleton, string Signature, bool EyeSpace)> _models = [];
    private readonly Dictionary<int, Vector2> _pick = [];
    private readonly StandardMaterial3D _lineMaterial = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true };
    private float _yaw = .65f, _pitch = .15f, _distance = 3;
    private Vector3 _target = new(0, .9f, 0);
    private bool _orbit, _pan, _drag;
    private System.Numerics.Matrix4x4 _dragParent = System.Numerics.Matrix4x4.Identity;
    private Vector2 _dragStart;
    private BoneTransform _before;
    private int _axis;
    internal IReadOnlyDictionary<int, Vector2> BonePoints => _pick;
    internal IReadOnlyList<(int Axis, Vector2 A, Vector2 B)> Handles => _handles;
    internal float GroundHeight => _ground;
    internal Transform3D ViewTransform => _camera.GlobalTransform;
    internal bool Empty => _models.Count == 0 && _pick.Count == 0 && _handles.Count == 0 && _lines.Mesh is null;
    public bool SequenceCamera { get; set; }
    public string? FirstPersonProfileId { get; set; }
    public string FirstPersonActionName { get; set; } = "Idle";
    public bool FirstPersonEye { get; set; }
    private Transform3D? _savedCamera;
    private float _savedFov;
    public void Bind(StudioState state)
    {
        _state = state; state.Changed += Sync; state.TimeChanged += Pose;
        TreeExiting += () => { state.Changed -= Sync; state.TimeChanged -= Pose; };
    }
    internal void RefreshView() => Sync();
    public override void _Ready()
    {
        Stretch = true; ClipContents = true; MouseFilter = MouseFilterEnum.Stop; FocusMode = FocusModeEnum.All;
        TooltipText = "Middle drag: orbit · Shift+middle: pan · Wheel: zoom · Click a bone to select · Drag a selected axis to pose";
        var viewport = new SubViewport { OwnWorld3D = true, GuiDisableInput = true, RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible };
        AddChild(viewport);
        _world = new Node3D(); viewport.AddChild(_world);
        _camera = new Camera3D { Current = true, Near = .01f, Far = 1000, Fov = 50,
            Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = EditorTheme.Background,
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .65f } };
        viewport.AddChild(_camera);
        _world.AddChild(new DirectionalLight3D { RotationDegrees = new(-35, -30, 0), LightEnergy = 1.2f });
        _floor = new MeshInstance3D { Mesh = new PlaneMesh { Size = new(20, 20) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = EditorTheme.Field, Roughness = .9f } };
        _world.AddChild(_floor);
        _grid = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _world.AddChild(_grid);
        _lines = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _world.AddChild(_lines);
        Aim(); Sync();
    }
    public void Frame()
    {
        FirstPersonEye = false;
        if (FirstPersonProfileId is null && Bounds() is { } bounds)
        {
            _target = bounds.GetCenter();
            float halfFov = Mathf.DegToRad(_camera.Fov * .5f);
            if (Size.X < Size.Y) halfFov = MathF.Atan(MathF.Tan(halfFov) * Size.X / MathF.Max(1, Size.Y));
            _distance = MathF.Max(.5f, (bounds.Size.Length() * .5f + .15f) / MathF.Sin(halfFov));
        }
        else { _target = new(.25f, -.1f, 0); _distance = .9f; }
        Aim();
    }
    private Aabb? Bounds()
    {
        Vector3? minimum = null, maximum = null;
        foreach ((string id, var model) in _models)
        {
            RigInstance? instance = _state.Project.Scene.FirstOrDefault(r => r.Id == id);
            SkeletonDefinition? definition = _state.Project.Skeletons.FirstOrDefault(s => s.Id == instance?.Skeleton);
            if (definition is null) continue;
            var clip = _state.Project.Clips.FirstOrDefault(c => c.Id == (id == _state.RigId ? _state.ClipId : instance?.Clip));
            foreach (var bone in ClipEvaluator.World(definition, ClipEvaluator.Evaluate(definition, clip, 0)))
            {
                Vector3 point = model.Root.Transform * Vec(bone.Translation);
                minimum = minimum?.Min(point) ?? point; maximum = maximum?.Max(point) ?? point;
            }
        }
        return minimum is { } min && maximum is { } max ? new Aabb(min, max - min) : null;
    }
    public void FirstPerson()
    {
        if (!IsNodeReady()) return;
        ApplyFirstPersonCamera(); Pose();
    }
    private void ApplyFirstPersonCamera()
    {
        bool eyeAsset = _state.RigId is { } id && _models.TryGetValue(id, out var model) && model.EyeSpace;
        if (FirstPersonProfileId is not null || eyeAsset)
        {
            FirstPersonEye = true; _camera.Position = Vector3.Zero; _camera.LookAt(Vector3.Right, Vector3.Up); _camera.Near = .005f;
            _camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
            _camera.Fov = FirstPersonFraming.VerticalFov(_state.Project.FirstPersonProfiles.FirstOrDefault(p => p.Id == FirstPersonProfileId)?.FieldOfView ?? 70);
            return;
        }
        FirstPersonEye = false;
        _target = new(0, 1.6f, -.5f); _camera.Position = new(0, 1.6f, .05f); _camera.LookAt(_target);
    }
    private void Aim()
    {
        _camera.Position = _target + new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch)) * _distance;
        _camera.LookAt(_target); if (_state is not null) Pose();
    }
    private void Sync()
    {
        if (!IsNodeReady() || _state is null) return;
        var instances = _state.Project.Scene.ToList();
        if (_state.Project.FirstPersonProfiles.FirstOrDefault(p => p.Id == FirstPersonProfileId) is { } profile)
            instances = instances.Where(i => i.Id == profile.MainRig || i.Id == profile.ArmsRig).ToList();
        foreach (string old in _models.Keys.Where(id => !instances.Any(i => i.Id == id)).ToArray()) { _models[old].Root.GetParent().RemoveChild(_models[old].Root); _models[old].Root.QueueFree(); _models.Remove(old); }
        foreach (RigInstance instance in instances)
        {
            SkeletonDefinition definition = _state.Project.Skeletons.Single(s => s.Id == instance.Skeleton);
            string? model = definition.Model;
            string signature = definition.Id + ":" + model;
            if (!_models.TryGetValue(instance.Id, out var entry) || entry.Signature != signature)
            {
                entry.Root?.QueueFree();
                var root = new Node3D { Name = "Rig_" + instance.Id }; _world.AddChild(root);
                Skeleton3D? rig = null; bool eyeSpace = false;
                string? path = model is null ? null : Path.IsPathRooted(model) ? model : Path.Combine(_state.Session.Folder ?? ".", model);
                if (path is not null && File.Exists(path))
                {
                    var document = new GltfDocument(); var data = new GltfState { CreateAnimations = false };
                    if (document.AppendFromFile(path, data) == Error.Ok && document.GenerateScene(data) is Node3D scene)
                    {
                        root.AddChild(scene); rig = Descendants<Skeleton3D>(scene).FirstOrDefault();
                        if (data.Json.TryGetValue("extras", out var extras) && extras.VariantType == Variant.Type.Dictionary &&
                            extras.AsGodotDictionary().TryGetValue("svmap", out var svmap) && svmap.VariantType == Variant.Type.Dictionary &&
                            svmap.AsGodotDictionary().TryGetValue("mesh", out var asset) && asset.VariantType == Variant.Type.String)
                        {
                            string mesh = asset.AsString();
                            eyeSpace = mesh.StartsWith("SWeaponFirst.", StringComparison.OrdinalIgnoreCase) || mesh.EndsWith("_first", StringComparison.OrdinalIgnoreCase) || mesh.EndsWith("_fp", StringComparison.OrdinalIgnoreCase);
                        }
                        foreach (AnimationPlayer player in Descendants<AnimationPlayer>(scene)) { player.GetParent().RemoveChild(player); player.QueueFree(); }
                    }
                }
                _models[instance.Id] = (root, rig, signature, eyeSpace);
            }
            _models[instance.Id].Root.Transform = ToGodot(instance.Transform) * ToGodot(definition.DisplayTransform);
        }
        string key = _state.Project.Id + ":" + string.Join(',', instances.Select(i => i.Id)) + ":" + FirstPersonProfileId;
        if (_sceneKey != key)
        {
            _sceneKey = key; _drag = false;
            _ground = (Bounds()?.Position.Y ?? 0) - .02f;
            _floor.Position = new(0, _ground, 0);
            var grid = new ImmediateMesh();
            grid.SurfaceBegin(Mesh.PrimitiveType.Lines, new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = EditorTheme.Divider });
            for (int i = -10; i <= 10; i++)
            {
                grid.SurfaceAddVertex(new(i, _ground + .001f, -10)); grid.SurfaceAddVertex(new(i, _ground + .001f, 10));
                grid.SurfaceAddVertex(new(-10, _ground + .001f, i)); grid.SurfaceAddVertex(new(10, _ground + .001f, i));
            }
            grid.SurfaceEnd(); Mesh? old = _grid.Mesh; _grid.Mesh = grid; old?.Dispose();
            bool eye = FirstPersonEye && FirstPersonProfileId is not null;
            Frame();
            if (eye) FirstPerson();
        }
        Pose();
    }
    private void Pose()
    {
        if (!IsNodeReady() || _state is null) return;
        _floor.Visible = FirstPersonProfileId is null && !FirstPersonEye; _grid.Visible = _floor.Visible;
        _pick.Clear(); _handles.Clear(); _tips.Clear();
        if (_state.Skeleton is null || _models.Count == 0)
        {
            Mesh? old = _lines.Mesh; _lines.Mesh = null; old?.Dispose(); QueueRedraw(); return;
        }
        var mesh = new ImmediateMesh(); mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _lineMaterial); int vertices = 0;
        void Line(Vector3 a, Vector3 b, Color color)
        {
            mesh.SurfaceSetColor(color); mesh.SurfaceAddVertex(a); mesh.SurfaceSetColor(color); mesh.SurfaceAddVertex(b); vertices += 2;
        }
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (FirstPersonEye) ApplyFirstPersonCamera();
        if (_state.Project.FirstPersonProfiles.FirstOrDefault(p => p.Id == FirstPersonProfileId) is { } profile)
        {
            FirstPersonAction? action = profile.Actions.FirstOrDefault(a => a.Name == FirstPersonActionName);
            if (action?.Clip is { } mainClip) overrides[profile.MainRig] = mainClip;
            if (action?.ArmsClip is { } armsClip && profile.ArmsRig is { } armsRig) overrides[armsRig] = armsClip;
        }
        if (_state.RigId is { } selectedRig && _state.ClipId is { } selectedClip) overrides[selectedRig] = selectedClip;
        Dictionary<string, BoneTransform[]> poses = RigSolver.Scene(_state.Project, _state.Time, overrides);
        SequenceSample? sequence = _state.Project.Sequences.FirstOrDefault(s => s.Id == _state.SequenceId) is { } sourceSequence ? SequencePlayer.Evaluate(_state.Project, sourceSequence, _state.Time) : null;
        if (SequenceCamera && sequence?.Transforms.FirstOrDefault(t => t.Target is "camera:all" or "camera:local") is { } camera)
        {
            if (_savedCamera is null) { _savedCamera = _camera.Transform; _savedFov = _camera.Fov; }
            _camera.Transform = ToGodot(camera.Transform); _camera.Fov = camera.FieldOfView ?? _savedFov;
        }
        else if (_savedCamera is { } saved) { _camera.Transform = saved; _camera.Fov = _savedFov; _savedCamera = null; }
        foreach ((string id, var model) in _models)
        {
            RigInstance? instance = _state.Project.Scene.FirstOrDefault(i => i.Id == id);
            SkeletonDefinition skeleton = instance is null ? _state.Skeleton : _state.Project.Skeletons.Single(s => s.Id == instance.Skeleton);
            bool selected = id == _state.RigId || (_state.RigId is null && skeleton.Id == _state.Skeleton.Id);
            BoneTransform[] pose = sequence?.Poses.FirstOrDefault(p => p.Target == id)?.Bones ?? (selected && _state.PoseOverrides.Count > 0 || selected && _state.GraphPlayer is not null
                ? _state.Pose() : poses.GetValueOrDefault(id) ?? (selected ? _state.Pose() : ClipEvaluator.Evaluate(skeleton, null, 0)));
            if (sequence?.Transforms.FirstOrDefault(t => t.Target == id) is { } transform) model.Root.Transform = ToGodot(transform.Transform) * ToGodot(skeleton.DisplayTransform);
            if (model.Skeleton is not null) StudioPoseBridge.Apply(model.Skeleton, skeleton, pose);
            System.Numerics.Matrix4x4[] world = ClipEvaluator.World(skeleton, pose);
            for (int bone = 0; bone < pose.Length; bone++)
            {
                Vector3 position = model.Root.Transform * Vec(world[bone].Translation);
                if (selected && !_camera.IsPositionBehind(position)) _pick[bone] = _camera.UnprojectPosition(position);
                int parent = skeleton.Bones[bone].Parent;
                if ((_state.ShowBones || model.Skeleton is null) && parent >= 0)
                    Line(model.Root.Transform * Vec(world[parent].Translation), position, selected && bone == _state.Bone ? EditorTheme.Accent : EditorTheme.TextDim);
                if (selected && bone == _state.Bone)
                {
                    _gizmoOrigin = position;
                    Vector3[] axes = [Vector3.Right, Vector3.Up, Vector3.Back];
                    Basis basis = _state.WorldSpace ? Basis.Identity : (model.Root.Basis * ToGodot(BoneTransform.FromMatrix(world[bone])).Basis).Orthonormalized();
                    float length = MathF.Max(.05f, _camera.GlobalPosition.DistanceTo(position) * .09f);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        _gizmoAxes[axis] = (basis * axes[axis]).Normalized();
                        Color color = axis switch { 0 => EditorTheme.AxisX, 1 => EditorTheme.AxisY, _ => EditorTheme.AxisZ };
                        void Handle(Vector3 a, Vector3 b)
                        {
                            Line(a, b, color);
                            if (!_camera.IsPositionBehind(a) && !_camera.IsPositionBehind(b)) _handles.Add((axis, _camera.UnprojectPosition(a), _camera.UnprojectPosition(b)));
                        }
                        if (_state.TransformTool == "rotate")
                        {
                            Vector3 u = basis * axes[(axis + 1) % 3], v = basis * axes[(axis + 2) % 3];
                            for (int step = 0; step < 64; step++)
                            {
                                float a = MathF.Tau * step / 64, b = MathF.Tau * (step + 1) / 64;
                                Handle(position + (u * MathF.Cos(a) + v * MathF.Sin(a)) * length, position + (u * MathF.Cos(b) + v * MathF.Sin(b)) * length);
                            }
                        }
                        else
                        {
                            Vector3 end = position + basis * axes[axis] * length;
                            Handle(position + basis * axes[axis] * length * .18f, end);
                            if (!_camera.IsPositionBehind(end)) _tips[axis] = _camera.UnprojectPosition(end);
                        }
                    }
                }
            }
            if (selected && _state.Ghosts && _state.CurrentClip is { } clip)
                foreach (float offset in new[] { -.12f, .12f })
                {
                    var ghost = ClipEvaluator.World(skeleton, ClipEvaluator.Evaluate(skeleton, clip, _state.Time + offset));
                    for (int b = 0; b < ghost.Length; b++) if (skeleton.Bones[b].Parent >= 0)
                        Line(model.Root.Transform * Vec(ghost[skeleton.Bones[b].Parent].Translation), model.Root.Transform * Vec(ghost[b].Translation), new Color(EditorTheme.Accent, .25f));
                }
            if (selected && _state.Trails && _state.CurrentClip is { } trailClip)
            {
                Vector3? previous = null;
                int bone = Math.Clamp(_state.Bone, 0, skeleton.Bones.Count - 1);
                for (int frame = 0; frame <= 60; frame++)
                {
                    var sample = ClipEvaluator.World(skeleton, ClipEvaluator.Evaluate(skeleton, trailClip, trailClip.Duration * frame / 60));
                    Vector3 point = model.Root.Transform * Vec(sample[bone].Translation);
                    if (previous is { } last) Line(last, point, EditorTheme.Warning); previous = point;
                }
            }
        }
        if (vertices > 0) mesh.SurfaceEnd();
        Mesh? previousMesh = _lines.Mesh; _lines.Mesh = vertices > 0 ? mesh : null; previousMesh?.Dispose();
        if (vertices == 0) mesh.Dispose();
        QueueRedraw();
    }
    public (BoneTransform[] Pose, BoneTransform Mount) CreateSpyGrip(string rigId)
    {
        if (!_models.TryGetValue(rigId, out var model) || model.Skeleton is null) throw new InvalidDataException("The Spy first-person arm skeleton did not load.");
        RigInstance instance = _state.Project.Scene.Single(i => i.Id == rigId);
        SkeletonDefinition definition = _state.Project.Skeletons.Single(s => s.Id == instance.Skeleton);
        var adapter = new FirstPersonAdapter(_state.Project, definition, model.Skeleton);
        if (!OpenScctGame.Views.SpyFirstPersonRig.TryPose(adapter, Transform3D.Identity, OpenScctGame.Views.SpyFirstPersonRig.Ready, out Transform3D mount, out _))
            throw new InvalidDataException("The Spy arm rig is missing its two-handed grip bones.");
        BoneTransform[] pose = definition.Bones.Select(b =>
        {
            int index = model.Skeleton.FindBone(b.Name); return index < 0 ? b.Rest : FromGodot(model.Skeleton.GetBonePose(index));
        }).ToArray();
        return (pose, FromGodot(mount));
    }
    private sealed class FirstPersonAdapter(AnimationProject project, SkeletonDefinition definition, Skeleton3D skeleton) : OpenScctGame.Views.IFirstPersonRigModel
    {
        public Skeleton3D Skeleton => skeleton;
        public bool SampleClip(string name, float frame)
        {
            var clip = project.Clips.FirstOrDefault(c => c.Skeleton == definition.Id && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (clip is null) return false;
            StudioPoseBridge.Apply(skeleton, definition, ClipEvaluator.Evaluate(definition, clip, frame * clip.Duration)); return true;
        }
        public Transform3D BonePose(int bone)
        {
            Transform3D pose = skeleton.GetBonePose(bone);
            for (int parent = skeleton.GetBoneParent(bone); parent >= 0; parent = skeleton.GetBoneParent(parent)) pose = skeleton.GetBonePose(parent) * pose;
            return pose;
        }
    }
    private static BoneTransform FromGodot(Transform3D transform)
    {
        Quaternion q = transform.Basis.GetRotationQuaternion(); Vector3 scale = transform.Basis.Scale;
        return new(new(transform.Origin.X, transform.Origin.Y, transform.Origin.Z), new(q.X, q.Y, q.Z, q.W), new(scale.X, scale.Y, scale.Z));
    }
    public override void _Draw()
    {
        if (_state is null) return;
        if (_state.ShowBones)
            foreach ((int bone, Vector2 point) in _pick)
            {
                DrawCircle(point, bone == _state.Bone ? 6 : 3, bone == _state.Bone ? EditorTheme.Accent : EditorTheme.TextDim);
                if (bone == _state.Bone) DrawArc(point, 9, 0, MathF.Tau, 24, EditorTheme.TextBright, 2);
            }
        foreach ((int axis, Vector2 point) in _tips)
        {
            Color color = axis switch { 0 => EditorTheme.AxisX, 1 => EditorTheme.AxisY, _ => EditorTheme.AxisZ };
            if (_state.TransformTool == "scale") DrawRect(new Rect2(point - new Vector2(4, 4), new(8, 8)), color);
            else if (_pick.TryGetValue(_state.Bone, out Vector2 origin))
            {
                Vector2 direction = (point - origin).Normalized(), normal = direction.Orthogonal();
                DrawColoredPolygon([point, point - direction * 11 + normal * 4, point - direction * 11 - normal * 4], color);
            }
        }
        if (!FirstPersonEye) return;
        Vector2 centre = Size * .5f;
        DrawLine(centre + new Vector2(-6, 0), centre + new Vector2(6, 0), EditorTheme.TextDim);
        DrawLine(centre + new Vector2(0, -6), centre + new Vector2(0, 6), EditorTheme.TextDim);
    }
    public override void _GuiInput(InputEvent e)
    {
        if (_state is null) return;
        if (e is InputEventMouseButton mouse)
        {
            if (mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && mouse.Pressed) { _distance = Math.Clamp(_distance * (mouse.ButtonIndex == MouseButton.WheelUp ? .9f : 1.1f), .05f, 200); Aim(); AcceptEvent(); }
            if (mouse.ButtonIndex == MouseButton.Middle) { _orbit = mouse.Pressed && !mouse.ShiftPressed; _pan = mouse.Pressed && mouse.ShiftPressed; AcceptEvent(); }
            if (mouse.ButtonIndex == MouseButton.Left)
            {
                if (mouse.Pressed)
                {
                    GrabFocus();
                    _dragStart = mouse.Position;
                    var nearest = _pick.OrderBy(p => p.Value.DistanceSquaredTo(mouse.Position)).FirstOrDefault();
                    var handle = _handles.OrderBy(h => Geometry2D.GetClosestPointToSegment(mouse.Position, h.A, h.B).DistanceSquaredTo(mouse.Position)).FirstOrDefault();
                    float handleDistance = _handles.Count == 0 ? float.PositiveInfinity : Geometry2D.GetClosestPointToSegment(mouse.Position, handle.A, handle.B).DistanceTo(mouse.Position);
                    float boneDistance = _pick.Count == 0 ? float.PositiveInfinity : nearest.Value.DistanceTo(mouse.Position);
                    bool selectBone = nearest.Key != _state.Bone && boneDistance < 9 && boneDistance < handleDistance;
                    if (!selectBone && handleDistance < 9)
                    {
                        _axis = handle.Axis;
                        _dragDirection = (handle.B - handle.A).Normalized();
                        BoneTransform[] pose = _state.Pose();
                        if (_state.Bone < pose.Length && _state.Skeleton is { } definition)
                        {
                            int parent = definition.Bones[_state.Bone].Parent;
                            _dragParent = parent < 0 ? System.Numerics.Matrix4x4.Identity : ClipEvaluator.World(definition, pose)[parent];
                            RigInstance? instance = _state.Project.Scene.FirstOrDefault(r => r.Id == _state.RigId);
                            _dragParent *= definition.DisplayTransform.Matrix * (instance?.Transform.Matrix ?? System.Numerics.Matrix4x4.Identity);
                            _beforeLocal = pose[_state.Bone];
                            _before = _state.WorldSpace ? BoneTransform.FromMatrix(_beforeLocal.Matrix * _dragParent) : _beforeLocal;
                            _drag = _state.TransformTool != "move" || BeginMove(mouse.Position);
                            if (_drag) _state.Playing = false;
                        }
                    }
                    if (!_drag)
                    {
                        if (_pick.Count > 0 && nearest.Value.DistanceTo(mouse.Position) < 18) _state.SelectBone(nearest.Key);
                    }
                }
                else if (_drag)
                {
                    _drag = false; BoneTransform[] pose = _state.Pose(); if (_state.Bone < pose.Length) _state.SetPose(_state.Bone, pose[_state.Bone], true);
                }
                AcceptEvent();
            }
        }
        if (e is InputEventMouseMotion motion)
        {
            if (_orbit) { FirstPersonEye = false; _yaw -= motion.Relative.X * .008f; _pitch = Math.Clamp(_pitch + motion.Relative.Y * .008f, -1.4f, 1.4f); Aim(); }
            if (_pan) { _target += (-_camera.GlobalBasis.X * motion.Relative.X + _camera.GlobalBasis.Y * motion.Relative.Y) * _distance * .0015f; Aim(); }
            if (_drag)
            {
                if (_state.TransformTool == "move")
                {
                    if (_movePlane.IntersectsRay(_camera.ProjectRayOrigin(motion.Position), _camera.ProjectRayNormal(motion.Position)) is { } point)
                    {
                        float distance = (point - _moveGrab).Dot(_moveAxis);
                        if (motion.ShiftPressed) distance = MathF.Round(distance * 100) / 100;
                        Vector3 world = _moveOrigin + _moveAxis * distance;
                        NVector3 local = NVector3.Transform(new(world.X, world.Y, world.Z), _moveParentInverse);
                        _state.SetPose(_state.Bone, Mathf.IsZeroApprox(distance) ? _beforeLocal : _beforeLocal with { Position = local }, false);
                    }
                    return;
                }
                float delta = (motion.Position - _dragStart).Dot(_dragDirection) * .01f;
                if (motion.ShiftPressed) delta = MathF.Round(delta * 10) / 10;
                NVector3 axis = _axis switch { 0 => NVector3.UnitX, 1 => NVector3.UnitY, _ => NVector3.UnitZ };
                BoneTransform value = _state.TransformTool switch
                {
                    "scale" => _before with { Scale = _before.Scale * NVector3.Max(new(.001f), NVector3.One + axis * delta) },
                    _ => _before with { Rotation = System.Numerics.Quaternion.Normalize(_state.WorldSpace ? System.Numerics.Quaternion.CreateFromAxisAngle(axis, delta) * _before.Rotation : _before.Rotation * System.Numerics.Quaternion.CreateFromAxisAngle(axis, delta)) },
                };
                if (_state.WorldSpace && System.Numerics.Matrix4x4.Invert(_dragParent, out var inverse)) value = BoneTransform.FromMatrix(value.Matrix * inverse);
                _state.SetPose(_state.Bone, value, false);
            }
        }
    }
    private bool BeginMove(Vector2 mouse)
    {
        _moveOrigin = _gizmoOrigin; _moveAxis = _gizmoAxes[_axis];
        Vector3 ray = _camera.ProjectRayNormal(mouse);
        // The plane contains the chosen axis and faces the view as closely as that axis allows.
        Vector3 normal = ray - _moveAxis * ray.Dot(_moveAxis);
        if (normal.LengthSquared() < .0001f || !System.Numerics.Matrix4x4.Invert(_dragParent, out _moveParentInverse)) return false;
        _movePlane = new Plane(normal.Normalized(), _moveOrigin);
        if (_movePlane.IntersectsRay(_camera.ProjectRayOrigin(mouse), ray) is not { } point) return false;
        _moveGrab = point;
        return true;
    }
    public static Vector3 Vec(NVector3 v) => new(v.X, v.Y, v.Z);
    public static Transform3D ToGodot(BoneTransform value) => new(new Basis(new Quaternion(value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W)).Scaled(Vec(value.Scale)), Vec(value.Position));
    private static IEnumerable<T> Descendants<T>(Node root) where T : Node
    {
        if (root is T found) yield return found;
        foreach (Node child in root.GetChildren()) foreach (T item in Descendants<T>(child)) yield return item;
    }
}
