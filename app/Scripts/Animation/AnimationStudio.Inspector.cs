using Godot;
using OpenScctEditor.Panels;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace OpenScctEditor.Animation;

public partial class AnimationStudio
{
    private string _page = "Pose";
    private Action? _refreshPoseFields;
    private void BuildInspector()
    {
        _refreshPoseFields = null;
        Choice(_inspector, "Inspector", ["Pose", "Clip", "Layers", "Rig", "Scene", "Bindings", "Retarget"], _page, value => { _page = value; Refresh(); });
        switch (_page)
        {
            case "Pose": PoseInspector(); break;
            case "Clip": ClipInspector(); break;
            case "Layers": LayersInspector(); break;
            case "Rig": RigInspector(); break;
            case "Scene": SceneInspector(); break;
            case "Bindings": BindingInspector(); break;
            case "Retarget": RetargetInspector(); break;
        }
    }
    private void PoseInspector()
    {
        if (State.Skeleton is not { } skeleton) { PanelUi.Caption(_inspector, "Import a skeleton to begin posing."); return; }
        int bone = Math.Clamp(State.Bone, 0, skeleton.Bones.Count - 1); State.Bone = bone;
        PanelUi.Header(_inspector, skeleton.Bones[bone].Name);
        RigInstance? instance = State.Project.Scene.FirstOrDefault(r => r.Id == State.RigId);
        System.Numerics.Matrix4x4 stage = skeleton.DisplayTransform.Matrix * (instance?.Transform.Matrix ?? System.Numerics.Matrix4x4.Identity);
        (BoneTransform[] Pose, BoneTransform Transform, System.Numerics.Matrix4x4 Parent, NVector3 Units) SamplePose()
        {
            BoneTransform[] current = State.Pose();
            var world = ClipEvaluator.World(skeleton, current);
            var parent = (skeleton.Bones[bone].Parent < 0 ? System.Numerics.Matrix4x4.Identity : world[skeleton.Bones[bone].Parent]) * stage;
            NVector3 units = State.WorldSpace ? NVector3.One : new(
                MathF.Max(.000001f, NVector3.TransformNormal(NVector3.UnitX, parent).Length()),
                MathF.Max(.000001f, NVector3.TransformNormal(NVector3.UnitY, parent).Length()),
                MathF.Max(.000001f, NVector3.TransformNormal(NVector3.UnitZ, parent).Length()));
            return (current, State.WorldSpace ? BoneTransform.FromMatrix(world[bone] * stage) : current[bone], parent, units);
        }
        static Vector3 Angles(BoneTransform value) => new Quaternion(value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W).GetEuler() * (180 / MathF.PI);
        var sample = SamplePose(); BoneTransform transform = sample.Transform; NVector3 positionUnits = sample.Units;
        Vector3 euler = Angles(transform);
        void Change(string kind, int axis, float value)
        {
            var current = SamplePose(); BoneTransform next = current.Transform;
            NVector3 Update(NVector3 vector) => axis switch { 0 => vector with { X = value }, 1 => vector with { Y = value }, _ => vector with { Z = value } };
            if (kind == "Position") next = next with { Position = SetComponent(next.Position, axis, value / Component(current.Units, axis)) };
            else if (kind == "Scale") next = next with { Scale = Update(next.Scale) };
            else
            {
                Vector3 angles = new Quaternion(next.Rotation.X, next.Rotation.Y, next.Rotation.Z, next.Rotation.W).GetEuler() * (180 / MathF.PI);
                if (axis == 0) angles.X = value; else if (axis == 1) angles.Y = value; else angles.Z = value;
                Quaternion q = Quaternion.FromEuler(angles * (MathF.PI / 180)); next = next with { Rotation = new(q.X, q.Y, q.Z, q.W) };
            }
            if (State.WorldSpace)
            {
                if (!System.Numerics.Matrix4x4.Invert(current.Parent, out var inverse)) return;
                next = BoneTransform.FromMatrix(next.Matrix * inverse);
            }
            State.SetPose(bone, next, true);
        }
        var fields = new List<(string Kind, int Axis, SpinBox Field)>();
        var typing = new HashSet<SpinBox>();
        foreach (string kind in new[] { "Position", "Rotation", "Scale" })
        {
            PanelUi.Caption(_inspector, kind + (kind == "Position" ? " (m)" : kind == "Rotation" ? " (degrees)" : ""));
            for (int axis = 0; axis < 3; axis++)
            {
                int index = axis; string property = kind;
                float value = kind == "Position" ? Component(transform.Position * positionUnits, axis) : kind == "Scale" ? Component(transform.Scale, axis) : axis == 0 ? euler.X : axis == 1 ? euler.Y : euler.Z;
                SpinBox field = Number(_inspector, "XYZ"[axis].ToString(), value, kind == "Scale" ? .000001f : -100000, 100000, kind == "Rotation" ? 1 : kind == "Scale" ? MathF.Max(.000001f, MathF.Abs(value) * .05f) : .01f, v => Change(property, index, v));
                field.Name = "Pose_" + kind + "_" + "XYZ"[axis];
                field.TooltipText = kind + " " + "XYZ"[axis] + " · arrow step: " + (kind == "Position" ? "1 cm" : kind == "Rotation" ? "1 degree" : "5%");
                fields.Add((kind, axis, field));
                field.GetLineEdit().TextChanged += _ => { if (!_refreshing) typing.Add(field); };
                field.GetLineEdit().TextSubmitted += _ => typing.Remove(field);
                field.GetLineEdit().FocusExited += () => typing.Remove(field);
            }
        }
        _refreshPoseFields = () =>
        {
            if (State.Skeleton?.Id != skeleton.Id || State.Bone != bone) return;
            var current = SamplePose(); Vector3 angles = Angles(current.Transform);
            foreach (var (kind, axis, field) in fields)
            {
                if (typing.Contains(field)) continue;
                float value = kind == "Position" ? Component(current.Transform.Position * current.Units, axis)
                    : kind == "Scale" ? Component(current.Transform.Scale, axis) : axis == 0 ? angles.X : axis == 1 ? angles.Y : angles.Z;
                if (kind == "Scale") field.CustomArrowStep = MathF.Max(.000001f, MathF.Abs(value) * .05f);
                field.SetValueNoSignal(value);
            }
        };
        PanelUi.Button(_inspector, "Key selected bone", "Insert position, rotation and scale keys", () => State.KeyPose([bone]));
        PanelUi.Button(_inspector, "Reset selected bone", "Restore the bone's reference pose", () => State.SetPose(bone, skeleton.Bones[bone].Rest, true));
        PanelUi.Button(_inspector, "Save pose", "Save the current whole-body pose", () => Edit("Save pose", p => p.Poses.Add(new() { Name = "Pose " + (p.Poses.Count + 1), Skeleton = skeleton.Id, Bones = State.Pose().ToList() })));
        foreach (PosePreset preset in State.Project.Poses.Where(p => p.Skeleton == skeleton.Id))
        {
            PosePreset selected = preset;
            PanelUi.Button(_inspector, "Apply " + preset.Name, "Apply this saved pose", () =>
            {
                State.PoseOverrides.Clear(); for (int i = 0; i < selected.Bones.Count; i++) State.PoseOverrides[i] = selected.Bones[i];
                if (State.AutoKey) State.KeyPose(); else State.Notify();
            });
        }
        PanelUi.Button(_inspector, "Mirror pose", "Mirror using the rig's bone-pair map", () =>
        {
            ControlRig? rig = State.Project.ControlRigs.FirstOrDefault(r => r.Skeleton == skeleton.Id);
            BoneTransform[] mirrored = AnimationEdits.Mirror(skeleton, State.Pose(), rig?.MirrorBones ?? new Dictionary<string, string>());
            for (int i = 0; i < mirrored.Length; i++) State.PoseOverrides[i] = mirrored[i];
            if (State.AutoKey) State.KeyPose(); else State.Notify();
        });
    }
    private void ClipInspector()
    {
        if (State.CurrentClip is not { } clip) { PanelUi.Caption(_inspector, "Create or select a clip."); return; }
        Text(_inspector, "Name", clip.Name, value => EditClip("Rename clip", c => c.Name = value));
        Number(_inspector, "Duration (s)", clip.Duration, .001f, 3600, .01f, value => EditClip("Retime clip", c => AnimationEdits.Retime(c, value)));
        Number(_inspector, "Frames / second", clip.FramesPerSecond, 1, 240, 1, value => EditClip("Set frame rate", c => c.FramesPerSecond = value));
        Choice(_inspector, "Root motion bone", State.Skeleton!.Bones.Select(b => b.Name).ToArray(), clip.RootMotionBone ?? State.Skeleton.Bones[0].Name,
            value => EditClip("Root motion bone", c => c.RootMotionBone = value));
        Number(_inspector, "Timeline FPS", State.Project.FramesPerSecond, 1, 240, 1, value => Edit("Timeline frame rate", p => p.FramesPerSecond = value));
        Number(_inspector, "Export samples / s", _exportRate, 1, 480, 1, value => _exportRate = value);
        PanelUi.Toggle(_inspector, "Loop clip", "Runtime clip loop flag", clip.Loop, value => EditClip("Set clip loop", c => c.Loop = value));
        Number(_inspector, "Range start", State.RangeStart, 0, clip.Duration, .01f, value => State.RangeStart = MathF.Min(value, State.RangeEnd));
        Number(_inspector, "Range end", State.RangeEnd, 0, clip.Duration, .01f, value => State.RangeEnd = MathF.Max(value, State.RangeStart));
        PanelUi.Button(_inspector, "Trim to range", "Keep the selected range and move its first frame to zero", () => EditClip("Trim animation", c => AnimationEdits.Trim(c, State.RangeStart, MathF.Min(c.Duration, State.RangeEnd))));
        PanelUi.Button(_inspector, "Split at playhead", "Create a second clip from the remaining animation", () => Edit("Split clip", p => p.Clips.Add(AnimationEdits.Split(p.Clips.Single(c => c.Id == clip.Id), State.Time))));
        PanelUi.Button(_inspector, "Reverse", "Reverse keys, tangents and events", () => EditClip("Reverse animation", AnimationEdits.Reverse));
        PanelUi.Button(_inspector, "Close loop", "Match end keys to the first pose", () => EditClip("Close loop", AnimationEdits.CloseLoop));
        PanelUi.Button(_inspector, "Reimport base tracks", "Reload source motion while preserving authored layers, events and duration", () => Run(() => State.Session.Reimport(clip.Id)));
        PanelUi.Button(_inspector, "Bake copy", "Bake layers and constraints at 60 fps", () => Edit("Bake animation", p =>
        {
            var result = AnimationEdits.BakeScene(p, clip); result.Id = Guid.NewGuid().ToString("N"); result.Name += " baked"; p.Clips.Add(result);
        }));
        PanelUi.Header(_inspector, "Events");
        foreach (AnimationEvent e in clip.Events.ToArray())
        {
            string id = e.Id;
            HFlowContainer row = PanelUi.Toolbar(_inspector);
            PanelUi.Button(row, $"{e.Time:0.###} · {e.Kind} · {e.Target}", "Go to event", () => State.Seek(e.Time));
            PanelUi.IconButton(row, "delete", "Remove event", () => EditClip("Remove event", c => c.Events.RemoveAll(x => x.Id == id)));
        }
        var kind = new OptionButton(); foreach (string name in Enum.GetNames<AnimationEventKind>()) kind.AddItem(name); _inspector.AddChild(kind);
        var target = new LineEdit { PlaceholderText = "Target / event name" }; _inspector.AddChild(target);
        var data = new LineEdit { PlaceholderText = "Sound / notify / attachment value" }; _inspector.AddChild(data);
        PanelUi.Button(_inspector, "Add event at playhead", "Add a typed animation event", () => EditClip("Add event", c => c.Events.Add(new() { Time = State.Time, Kind = (AnimationEventKind)kind.Selected, Target = target.Text, Value = data.Text })));
        PanelUi.Caption(_inspector, "Scrubbing never fires gameplay events.");
    }
    private void LayersInspector()
    {
        if (State.CurrentClip is not { } clip) return;
        Choice(_inspector, "Edit layer", new[] { "Base tracks" }.Concat(clip.Layers.Select(l => l.Name)).ToArray(), State.Layer < 0 ? "Base tracks" : clip.Layers[State.Layer].Name,
            value => { State.Layer = value == "Base tracks" ? -1 : State.CurrentClip!.Layers.FindIndex(l => l.Name == value); State.Notify(); });
        PanelUi.Button(_inspector, "+ Override layer", "Create a masked override layer", () => EditClip("Add animation layer", c => c.Layers.Add(new() { Name = "Layer " + (c.Layers.Count + 1) })));
        PanelUi.Button(_inspector, "+ Additive layer", "Create a layer relative to the rest pose", () => EditClip("Add additive layer", c => c.Layers.Add(new() { Name = "Additive " + (c.Layers.Count + 1), Additive = true })));
        for (int i = 0; i < clip.Layers.Count; i++)
        {
            int index = i; AnimationLayer layer = clip.Layers[i];
            PanelUi.Header(_inspector, layer.Name);
            Text(_inspector, "Name", layer.Name, value => EditClip("Rename layer", c => c.Layers[index].Name = value));
            PanelUi.Toggle(_inspector, "Enabled", "Include this layer", layer.Enabled, value => EditClip("Enable layer", c => c.Layers[index].Enabled = value));
            PanelUi.Toggle(_inspector, "Additive", "Apply relative to the rest pose", layer.Additive, value => EditClip("Layer blend mode", c => c.Layers[index].Additive = value));
            Number(_inspector, "Weight", layer.Weight, 0, 1, .05f, value => EditClip("Layer weight", c => c.Layers[index].Weight = value));
            Text(_inspector, "Mask bones", string.Join(",", layer.Mask), value => EditClip("Layer bone mask", c => c.Layers[index].Mask = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()));
            PanelUi.Button(_inspector, "Key weight", "Animate this weight at the playhead", () => EditClip("Key layer weight", c =>
            {
                List<AnimationKey> keys = c.Layers[index].WeightKeys; keys.RemoveAll(k => k.Time == State.Time); keys.Add(new() { Time = State.Time, Value = new(layer.Weight, 0, 0, 0) }); keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            }));
        }
    }
    private void RigInspector()
    {
        if (State.Skeleton is not { } skeleton || State.CurrentClip is not { } clip) return;
        PanelUi.Caption(_inspector, "Constraints solve in rig space; exports bake their results.");
        Choice(_inspector, "New constraint", Enum.GetNames<ConstraintKind>(), "TwoBoneIk", value => EditClip("Add rig constraint", c => c.Constraints.Add(new()
        { Name = value + " " + skeleton.Bones[State.Bone].Name, Kind = Enum.Parse<ConstraintKind>(value), Bone = skeleton.Bones[State.Bone].Name, Root = skeleton.Bones[0].Name, Target = ClipEvaluator.World(skeleton, State.Pose())[State.Bone].Translation })));
        for (int i = 0; i < clip.Constraints.Count; i++)
        {
            int index = i; RigConstraint constraint = clip.Constraints[i];
            PanelUi.Header(_inspector, constraint.Name);
            Choice(_inspector, "Bone", skeleton.Bones.Select(b => b.Name).ToArray(), constraint.Bone, value => EditClip("Constraint bone", c => c.Constraints[index].Bone = value));
            Choice(_inspector, "Chain root", skeleton.Bones.Select(b => b.Name).ToArray(), constraint.Root, value => EditClip("Constraint root", c => c.Constraints[index].Root = value));
            Number(_inspector, "Weight", constraint.Weight, 0, 1, .05f, value => EditClip("Constraint weight", c => c.Constraints[index].Weight = value));
            Text(_inspector, "Target rig id", constraint.TargetRig ?? "", value => EditClip("Constraint target rig", c => c.Constraints[index].TargetRig = value.Length == 0 ? null : value));
            Text(_inspector, "Target bone", constraint.TargetBone ?? "", value => EditClip("Constraint target bone", c => c.Constraints[index].TargetBone = value.Length == 0 ? null : value));
            for (int axis = 0; axis < 3; axis++)
            {
                int component = axis;
                Number(_inspector, "Target " + "XYZ"[axis], Component(constraint.Target, axis), -1000, 1000, .01f, value => EditClip("Move rig target", c => c.Constraints[index].Target = SetComponent(c.Constraints[index].Target, component, value)));
                Number(_inspector, "Pole " + "XYZ"[axis], Component(constraint.Pole, axis), -1000, 1000, .01f, value => EditClip("Move pole target", c => c.Constraints[index].Pole = SetComponent(c.Constraints[index].Pole, component, value)));
            }
            PanelUi.Button(_inspector, "Remove constraint", "Remove this control", () => EditClip("Remove constraint", c => c.Constraints.RemoveAt(index)));
        }
        PanelUi.Button(_inspector, "Create humanoid controls", "Create hand/foot IK controls using matching stock bone names", () => Edit("Create control rig", p =>
        {
            var rig = new ControlRig { Skeleton = skeleton.Id, Name = skeleton.Name + " controls" };
            foreach (BoneDefinition bone in skeleton.Bones)
            {
                string? opposite = bone.Name.StartsWith("L", StringComparison.OrdinalIgnoreCase) ? "R" + bone.Name[1..] : bone.Name.StartsWith("R", StringComparison.OrdinalIgnoreCase) ? "L" + bone.Name[1..] : null;
                if (opposite is not null && skeleton.Bones.Any(b => b.Name.Equals(opposite, StringComparison.OrdinalIgnoreCase))) rig.MirrorBones[bone.Name] = skeleton.Bones.First(b => b.Name.Equals(opposite, StringComparison.OrdinalIgnoreCase)).Name;
                if (!bone.Name.Contains("hand", StringComparison.OrdinalIgnoreCase) && !bone.Name.Contains("foot", StringComparison.OrdinalIgnoreCase)) continue;
                var control = new RigConstraint { Name = bone.Name + " IK", Kind = ConstraintKind.TwoBoneIk, Bone = bone.Name, Target = ClipEvaluator.World(skeleton, State.Pose())[skeleton.Bones.IndexOf(bone)].Translation };
                rig.Controls.Add(control); p.Clips.Single(c => c.Id == clip.Id).Constraints.Add(AnimationJson.Clone(control));
            }
            p.ControlRigs.Add(rig);
        }));
    }
    private void SceneInspector()
    {
        foreach (RigInstance instance in State.Project.Scene)
        {
            string id = instance.Id;
            PanelUi.Header(_inspector, instance.Name);
            PanelUi.Caption(_inspector, id);
            PanelUi.Button(_inspector, "Select rig", "Pose this rig", () => State.SelectRig(id));
            Number(_inspector, "Start offset", instance.Start, -3600, 3600, .01f, value => Edit("Rig start offset", p => p.Scene.Single(r => r.Id == id).Start = value));
            for (int axis = 0; axis < 3; axis++)
            {
                int component = axis;
                Number(_inspector, "Position " + "XYZ"[axis], Component(instance.Transform.Position, axis), -10000, 10000, .01f, value => Edit("Move scene rig", p =>
                { RigInstance rig = p.Scene.Single(r => r.Id == id); rig.Transform = rig.Transform with { Position = SetComponent(rig.Transform.Position, component, value) }; }));
            }
            Choice(_inspector, "Clip", State.Project.Clips.Where(c => c.Skeleton == instance.Skeleton).Select(c => c.Name).ToArray(), State.Project.Clips.FirstOrDefault(c => c.Id == instance.Clip)?.Name ?? "",
                value => Edit("Scene animation", p => p.Scene.Single(r => r.Id == id).Clip = p.Clips.First(c => c.Name == value && c.Skeleton == instance.Skeleton).Id));
            PanelUi.Button(_inspector, "Duplicate rig", "Add another independently posed instance", () => Edit("Duplicate rig instance", p =>
            { RigInstance copy = AnimationJson.Clone(instance); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " copy"; copy.Transform = copy.Transform with { Position = copy.Transform.Position + NVector3.UnitX }; p.Scene.Add(copy); }));
            PanelUi.Button(_inspector, "Remove rig", "Remove this scene instance and its scene links. Keeps clips and models; Undo restores it.", () => Run(() => State.Session.RemoveRig(id)));
        }
        if (State.Skeleton is { } skeleton)
            PanelUi.Button(_inspector, "Add rig", "Add the selected skeleton and clip to the scene", () => Edit("Add scene rig", p => p.Scene.Add(new() { Name = skeleton.Name, Skeleton = skeleton.Id, Clip = State.ClipId })));
        PanelUi.Button(_inspector, "Create paired interaction", "Create a synchronized two-rig sequence", () => Edit("Paired interaction sequence", p =>
        {
            var sequence = new AnimationSequence { Name = "Paired interaction", Duration = p.Clips.Select(c => c.Duration).DefaultIfEmpty(1).Max() };
            foreach (RigInstance instance in p.Scene.Take(2))
                if (p.Clips.FirstOrDefault(c => c.Id == instance.Clip) is { } clip)
                    sequence.Tracks.Add(new() { Name = instance.Name, Kind = SequenceTrackKind.Skeletal, Target = instance.Id, Segments = [new() { Clip = clip.Id, Duration = clip.Duration }] });
            p.Sequences.Add(sequence);
        }));
    }
    private void BindingInspector()
    {
        PanelUi.Caption(_inspector, "Bind to a mesh (Package.Mesh) and an existing animation slot, or to actor:ActorId.");
        var target = new LineEdit { PlaceholderText = "SPerso.ATT_01 or actor:ActorId" }; _inspector.AddChild(target);
        var slot = new LineEdit { Text = State.CurrentClip?.Name ?? "", PlaceholderText = "Animation slot / trigger tag" }; _inspector.AddChild(slot);
        var mode = new OptionButton(); mode.AddItem("Visual: preserve gameplay"); mode.AddItem("Gameplay: authored timing"); _inspector.AddChild(mode);
        PanelUi.Button(_inspector, "Bind selected clip", "Create or replace this project binding", () => Edit("Bind animation", p =>
        {
            if (State.CurrentClip is null) throw new InvalidOperationException("Select a clip first.");
            p.Bindings.RemoveAll(b => b.Target == target.Text && b.Slot == slot.Text);
            p.Bindings.Add(new() { Target = target.Text, Slot = slot.Text, Clip = State.ClipId, Mode = (ReplacementMode)mode.Selected });
        }));
        foreach (AnimationBinding binding in State.Project.Bindings.ToArray())
        {
            PanelUi.Caption(_inspector, $"{binding.Target} / {binding.Slot} · {binding.Mode}");
            PanelUi.Button(_inspector, "Remove binding", "Remove this explicit replacement", () => Edit("Remove binding", p => p.Bindings.RemoveAll(b => b.Target == binding.Target && b.Slot == binding.Slot)));
        }
    }
    private void RetargetInspector()
    {
        if (State.CurrentClip is not { } clip || State.Skeleton is not { } source) return;
        PanelUi.Caption(_inspector, "Create a profile, then edit its bone mapping. Matching names are filled automatically.");
        foreach (SkeletonDefinition target in State.Project.Skeletons.Where(s => s.Id != source.Id))
            PanelUi.Button(_inspector, "Profile → " + target.Name, "Create a reusable rest-pose corrected retarget profile", () => Edit("Create retarget profile", p => p.RetargetProfiles.Add(new()
            {
                Name = source.Name + " → " + target.Name, SourceSkeleton = source.Id, TargetSkeleton = target.Id,
                Bones = source.Bones.Where(b => target.Bones.Any(t => t.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(b => b.Name, b => target.Bones.First(t => t.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)).Name, StringComparer.Ordinal),
            })));
        for (int i = 0; i < State.Project.RetargetProfiles.Count; i++)
        {
            int index = i; RetargetProfile profile = State.Project.RetargetProfiles[i];
            if (profile.SourceSkeleton != source.Id) continue;
            PanelUi.Header(_inspector, profile.Name);
            Number(_inspector, "Root scale", profile.RootScale, .001f, 100, .01f, value => Edit("Retarget scale", p => p.RetargetProfiles[index].RootScale = value));
            foreach (BoneDefinition bone in source.Bones)
            {
                string name = bone.Name;
                Text(_inspector, name, profile.Bones.GetValueOrDefault(name, ""), value => Edit("Retarget bone map", p =>
                { if (value.Length == 0) p.RetargetProfiles[index].Bones.Remove(name); else p.RetargetProfiles[index].Bones[name] = value; }));
            }
            PanelUi.Button(_inspector, "Retarget selected clip", "Create a new animation on the target skeleton", () => Edit("Retarget animation", p => p.Clips.Add(AnimationEdits.Retarget(p, clip, profile))));
        }
    }
    private void Edit(string name, Action<AnimationProject> edit) => Run(() => State.Session.Edit(name, edit));
    private void EditClip(string name, Action<OpenScct.Animation.AnimationClip> edit)
    { if (State.ClipId is { } id) Run(() => State.Session.EditClip(name, id, edit)); }
    private SpinBox Number(Control parent, string label, float value, float min, float max, float step, Action<float> change)
    {
        HBoxContainer row = PanelUi.Row(parent, label, label);
        SpinBox spin = PanelUi.Spin(row, min, max, .000001, Math.Clamp(value, min, max), label);
        spin.CustomArrowStep = step;
        spin.UpdateOnTextChanged = false;
        spin.ValueChanged += v => { if (!_refreshing) change((float)v); };
        return spin;
    }
    private void Text(Control parent, string label, string value, Action<string> change)
    {
        HBoxContainer row = PanelUi.Row(parent, label, label);
        var field = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(field);
        field.TextSubmitted += text => { if (text != value) change(text); };
        field.FocusExited += () => { if (field.Text != value && !_refreshing) change(field.Text); };
    }
    private void Choice(Control parent, string label, string[] values, string selected, Action<string> change)
    {
        HBoxContainer row = PanelUi.Row(parent, label, label);
        var options = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(options);
        foreach (string value in values) options.AddItem(value);
        options.Selected = Math.Max(0, Array.IndexOf(values, selected));
        options.ItemSelected += index => { if (!_refreshing && index >= 0 && index < values.Length) change(values[index]); };
    }
    private static float Component(NVector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    private static NVector3 SetComponent(NVector3 v, int axis, float value) => axis switch { 0 => v with { X = value }, 1 => v with { Y = value }, _ => v with { Z = value } };
    private void AttachMap() => Run(() =>
    {
        if (EditorContext.Instance.Session.Folder is not { } folder) throw new InvalidOperationException("Save the map before attaching animation assets.");
        string destination = Path.Combine(folder, "assets", "animations", State.Project.Id + ".osanim");
        var copy = new AnimationSession(AnimationJson.Clone(State.Project));
        KeepPlacedGraphs(copy.Project, destination);
        if (State.Session.Folder is { } source)
        {
            foreach (SkeletonDefinition skeleton in copy.Project.Skeletons)
                if (skeleton.Model is { } model && !Path.IsPathRooted(model)) skeleton.Model = Path.Combine(source, model);
            AnimationImport.RewriteAudio(copy.Project, value => Path.GetExtension(value).ToLowerInvariant() is ".wav" or ".ogg" && !Path.IsPathRooted(value) ? Path.Combine(source, value) : value);
        }
        copy.Save(destination);
        _status.Text = "Attached to map: " + destination;
    });
    private void TestGame() => TestGame(false);
    private void TestGame(bool play) => Run(() =>
    {
        string folder = Path.Combine(ProjectSettings.GlobalizePath("user://animation-tests"), State.Project.Id);
        AnimationPackBuild.Write(State.Project, State.Session.Folder, folder);
        var args = new List<string>(); string exe;
        if (!OS.HasFeature("template") && EditorServices.OpenScctRoot is { } source && File.Exists(Path.Combine(source, "game", "project.godot")))
        {
            exe = OS.GetExecutablePath(); args.AddRange(["--path", Path.Combine(source, "game")]);
        }
        else
        {
            exe = GameLauncher.FindGame() ?? throw new InvalidOperationException("Set the OpenSCCT executable in editor settings first.");
            AnimationRuntimeProbe.Require(exe);
        }
        if (play)
        {
            string maps = Path.Combine(folder, "practice-maps");
            AnimationPracticeMap.Build(maps);
            args.AddRange(["--", "--svm-maps=" + maps, "--svm-play=TestRange", "--svm-test-round", "--svm-animation-pack=" + folder, "--side=" + (_firstPerson.Profile?.Role == FirstPersonRole.Merc ? "merc" : "spy"), "--bots=0"]);
            if (EditorServices.ContentRoot is { } content) args.Add("--svm-content=" + content);
        }
        else
        {
            args.AddRange(["--", "--svm-animation-preview=" + folder]);
            if (_firstPerson.Profile is { } fp) args.Add("--svm-animation-fp=" + fp.Id);
        }
        GameLauncher.Play(exe, args);
        _status.Text = "Launched animation test with a private content overlay.";
    });
    private void PlaceMapProp() => Run(() =>
    {
        EditorContext context = EditorContext.Instance;
        string folder = context.Session.Folder ?? throw new InvalidOperationException("Save the map before placing animation assets.");
        string clip = State.CurrentClip?.Id ?? throw new InvalidOperationException("Select a clip to place in the map.");
        AnimationProject copy = AnimationJson.Clone(State.Project);
        string destination = Path.Combine(folder, "assets", "animations", copy.Id + ".osanim");
        KeepPlacedGraphs(copy, destination);
        var command = AnimationMapPlacement.Prepare(context.Project, copy, clip, OpenScctEditor.Viewports.ViewportLayout.Instance?.CursorWorld ?? OpenScct.Editor.Core.Model.Vec3.Zero);
        if (State.Session.Folder is { } source)
        {
            foreach (SkeletonDefinition skeleton in copy.Skeletons)
                if (skeleton.Model is { } model && !Path.IsPathRooted(model)) skeleton.Model = Path.Combine(source, model);
            AnimationImport.RewriteAudio(copy, value => Path.GetExtension(value).ToLowerInvariant() is ".wav" or ".ogg" && !Path.IsPathRooted(value) ? Path.Combine(source, value) : value);
        }
        new AnimationSession(copy).Save(destination);
        context.Execute(command); context.Selection.Set(command.Added);
        _status.Text = "Placed animated prop in the Map workspace. Build and Play Map to test it.";
    });
    private static void KeepPlacedGraphs(AnimationProject copy, string destination)
    {
        if (!File.Exists(Path.Combine(destination, "animation.json"))) return;
        AnimationProject previous = AnimationSession.Open(destination).Project;
        copy.Graphs.AddRange(previous.Graphs.Where(g => copy.Graphs.All(current => current.Id != g.Id)));
        copy.Bindings.AddRange(previous.Bindings.Where(b => copy.Bindings.All(current => current.Target != b.Target || current.Slot != b.Slot)));
    }
}
