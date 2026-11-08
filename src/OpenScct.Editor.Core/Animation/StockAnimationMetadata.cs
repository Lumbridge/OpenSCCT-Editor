using System.Numerics;
using System.Text.Json.Nodes;
using OpenScct.Animation;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Game.Movement;
using OpenScct.Game.SBase;

namespace OpenScct.Editor.Core.Animation;

internal static class StockAnimationMetadata
{
    public static void Apply(AnimationProject project, GltfAnimationImport gltf, string source)
    {
        string? metadata = gltf.Json["extras"]?["svmap"]?["animation"]?.GetValue<string>();
        if (metadata is null || !metadata.EndsWith(".anim.json", StringComparison.OrdinalIgnoreCase)) return;
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, metadata));
        if (!path.StartsWith(Path.GetDirectoryName(Path.GetFullPath(source))! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
        JsonObject? sequences = JsonNode.Parse(File.ReadAllBytes(path))?["sequences"]?.AsObject();
        if (sequences is null) return;
        bool spy = Path.GetFileName(path).Equals("PRO.anim.json", StringComparison.OrdinalIgnoreCase), merc = Path.GetFileName(path).Equals("Def.anim.json", StringComparison.OrdinalIgnoreCase);
        PawnAnimationSource? pawnSource = spy || merc ? PawnAnimationSource.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(path))!) : null;
        SBasePawn pawn = spy ? new SPawnAttaque() : new SPAWNDEFENSE();
        foreach (AnimationClip clip in project.Clips)
        {
            if (sequences[clip.Name] is not JsonObject sequence) continue;
            float rate = sequence["rate"]?.GetValue<float>() ?? 30; int frames = sequence["frames"]?.GetValue<int>() ?? 1;
            if (rate <= 0 || frames < 1) continue;
            clip.FramesPerSecond = rate; clip.Duration = MathF.Max(1 / rate, (frames - 1) / rate);
            if (pawnSource is null || !pawnSource.TryGetClip(pawn, new Name(clip.Name), out PawnClip data)) continue;
            foreach (PawnClipNotify notify in data.Notifies)
            {
                AnimationEvent? e = notify.Notify switch
                {
                    UAnimNotify_Trigger trigger => new() { Kind = AnimationEventKind.Trigger, Target = trigger.EventName.ToString() },
                    UAnimNotify_Sound sound => new() { Kind = AnimationEventKind.Sound, Value = sound.Sound.Path ?? "" },
                    UAnimNotify_StopSound sound => new() { Kind = AnimationEventKind.StopSound, Value = sound.Sound.Path ?? "" },
                    UAnimNotify_Script script => new() { Kind = AnimationEventKind.ScriptNotify, Value = script.NotifyName.ToString() },
                    _ => null,
                };
                if (e is null) continue;
                e.Time = notify.Time * data.Frames / data.Fps; e.SourceNormalizedTime = notify.Time; clip.Events.Add(e);
            }
            if (data.RootTrack is not { } root) continue;
            var translation = new BoneTrack { Bone = "RootMotion", Channel = TrackChannel.Translation };
            var rotation = new BoneTrack { Bone = "RootMotion", Channel = TrackChannel.Rotation };
            for (int i = 0; i < root.KeyFrames.Length; i++)
            {
                float time = root.KeyFrames[i] / data.Fps;
                translation.Keys.Add(new() { Time = time, Value = ClipEvaluator.V(StudioSpace.FromUe(root.Positions[i])) });
                rotation.Keys.Add(new() { Time = time, Value = ClipEvaluator.V(StudioSpace.FromUe(new FRotator(0, root.Yaw is { } yaw && i < yaw.Length ? (int)yaw[i] : 0, 0))) });
            }
            if (translation.Keys.Count > 0)
            {
                var motion = new AnimationClip { Duration = MathF.Max(clip.Duration, translation.Keys[^1].Time), Tracks = [translation, rotation] };
                if (motion.Duration > clip.Duration) AnimationEdits.Trim(motion, 0, clip.Duration);
                clip.RootTracks = motion.Tracks;
            }
        }
    }
}
