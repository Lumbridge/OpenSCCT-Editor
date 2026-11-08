using System.Numerics;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;

internal static class Examples
{
    public static void Write(string output, string content)
    {
        void Save(string name, AnimationProject project)
        {
            string folder = Path.Combine(output, name + ".osanim");
            new AnimationSession(project).Save(folder);
            AnimationPackBuild.Write(project, folder, Path.Combine(output, name + "-overlay"));
        }
        AnimationProject Stock(string category, string asset, string slot)
        {
            AnimationProject project = AnimationImport.Read(Path.Combine(content, "shared", "chars", category, asset + ".glb"));
            AnimationClip clip = AnimationEdits.Duplicate(project.Clips.First(c => c.Name == slot)); clip.Name = "Studio " + slot;
            project.Clips = [clip]; project.Scene[0].Clip = clip.Id;
            project.Bindings.Add(new() { Target = category + "." + asset, Slot = slot, Clip = clip.Id });
            return project;
        }
        var spy = Stock("SPerso", "ATT_01", "WaitStAlFd0"); spy.Name = "New Spy greeting";
        string hand = spy.Skeletons[0].Bones.First(b => b.Name.Contains("Hand", StringComparison.OrdinalIgnoreCase)).Name;
        var rest = spy.Skeletons[0].Bones.First(b => b.Name == hand).Rest;
        spy.Clips[0].Layers.Add(new() { Name = "Hand gesture", Additive = true, Mask = [hand], Tracks = [new() { Bone = hand, Channel = TrackChannel.Rotation,
            Keys = [new() { Time = 0, Value = ClipEvaluator.V(rest.Rotation) }, new() { Time = spy.Clips[0].Duration * .5f, Value = ClipEvaluator.V(Quaternion.Normalize(rest.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f))) }, new() { Time = spy.Clips[0].Duration, Value = ClipEvaluator.V(rest.Rotation) }] }] });
        Save("spy-greeting", spy);
        var weapon = Stock("SWeaponFirst", "1DEF_01", "FirstReloStFd2"); weapon.Name = "Merc rifle reload replacement";
        weapon.FirstPersonProfiles.Add(new() { Role = FirstPersonRole.Merc, Weapon = "Rifle", MainRig = weapon.Scene[0].Id, MainAsset = "SWeaponFirst.1DEF_01", Enabled = true,
            Actions = [new() { Name = "Reload", Slot = "FirstReloStFd2", Clip = weapon.Clips[0].Id }] });
        Save("merc-reload", weapon);
        var skeleton = new SkeletonDefinition { Name = "Original example rig", Bones = [new() { Name = "Root" }, new() { Name = "Hand", Parent = 0, Rest = new(Vector3.UnitY, Quaternion.Identity, Vector3.One) }] };
        var clip = new AnimationClip { Name = "Wave", Skeleton = skeleton.Id, Duration = 2, Loop = true };
        AnimationEdits.AddPoseKeys(clip, "Hand", skeleton.Bones[1].Rest, 0);
        AnimationEdits.AddPoseKeys(clip, "Hand", skeleton.Bones[1].Rest with { Position = new(.5f, 1, 0) }, 1);
        AnimationEdits.AddPoseKeys(clip, "Hand", skeleton.Bones[1].Rest, 2);
        var a = new RigInstance { Name = "Leader", Skeleton = skeleton.Id, Clip = clip.Id };
        var b = new RigInstance { Name = "Partner", Skeleton = skeleton.Id, Transform = new(new(1, 0, 0), Quaternion.Identity, Vector3.One) };
        var pair = new AnimationProject { Name = "Paired hand pin", Skeletons = [skeleton], Clips = [clip], Scene = [a, b] };
        clip.Constraints.Add(new() { Kind = ConstraintKind.Pin, Bone = "Hand", TargetRig = b.Id, TargetBone = "Hand" });
        pair.Sequences.Add(new() { Name = "Interaction", Duration = 2, Tracks = [new() { Kind = SequenceTrackKind.Skeletal, Target = a.Id, Segments = [new() { Clip = clip.Id, Duration = 2 }] }] });
        Save("paired-interaction", pair);
        var prop = AnimationImport.Read(SyntheticFixtures.ActorX(output, false)); prop.Name = "Animated map prop";
        var propClip = new AnimationClip { Name = "Prop motion", Skeleton = prop.Skeletons[0].Id, Duration = 2, Loop = true };
        AnimationEdits.AddPoseKeys(propClip, prop.Skeletons[0].Bones[0].Name, BoneTransform.Identity, 0);
        AnimationEdits.AddPoseKeys(propClip, prop.Skeletons[0].Bones[0].Name, BoneTransform.Identity with { Position = Vector3.UnitY }, 1);
        AnimationEdits.AddPoseKeys(propClip, prop.Skeletons[0].Bones[0].Name, BoneTransform.Identity, 2);
        prop.Clips.Add(propClip); prop.Scene[0].Clip = propClip.Id;
        _ = AnimationMapPlacement.Prepare(new(), prop, propClip.Id, default);
        Save("animated-prop", prop);
        var camera = new AnimationProject { Name = "Camera and object sequence" };
        camera.Sequences.Add(new() { Name = "Move and reveal", Duration = 2, Tracks = [
            new() { Name = "Object", Kind = SequenceTrackKind.Transform, Target = "actor:AnimatedProp", TransformTracks = [new() { Bone = "Transform", Channel = TrackChannel.Translation, Keys = [new() { Time = 0 }, new() { Time = 2, Value = new(2, 0, 0, 0) }] }] },
            new() { Name = "Explicit camera", Kind = SequenceTrackKind.Camera, Target = "camera:local", TransformTracks = [new() { Bone = "Transform", Channel = TrackChannel.Translation, Keys = [new() { Time = 0, Value = new(0, 2, 4, 0) }, new() { Time = 2, Value = new(2, 2, 4, 0) }] }], FieldOfView = [new() { Time = 0, Value = new(65, 0, 0, 0) }] },
            new() { Name = "Completion", Kind = SequenceTrackKind.Event, Events = [new() { Time = 2, Kind = AnimationEventKind.Trigger, Target = "SequenceDone" }] }] });
        camera.Bindings.Add(new() { Target = "sequence:reveal", Slot = "Reveal", Sequence = camera.Sequences[0].Id, Mode = ReplacementMode.Gameplay });
        Save("camera-object", camera);
    }
}
