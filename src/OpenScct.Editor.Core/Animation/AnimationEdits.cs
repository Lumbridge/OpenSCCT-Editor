using System.Numerics;
using OpenScct.Animation;

namespace OpenScct.Editor.Core.Animation;

public static class AnimationEdits
{
    /// <summary>Replace a dense interval with an eased path while preserving endpoint poses and motion outside it.</summary>
    public static void SmoothBetween(BoneTrack track, float start, float end, float framesPerSecond)
    {
        if (!float.IsFinite(start) || !float.IsFinite(end) || end <= start || !float.IsFinite(framesPerSecond) || framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(end));
        AnimationKey first = track.Keys.FirstOrDefault(k => k.Time == start) ?? throw new ArgumentException("Select a start key on this track.");
        AnimationKey last = track.Keys.FirstOrDefault(k => k.Time == end) ?? throw new ArgumentException("Select an end key on the same track.");
        track.Keys.RemoveAll(k => k.Time > start && k.Time < end);
        if (track.Channel == TrackChannel.Rotation)
        {
            // Slerp takes the short arc even when endpoint quaternions have opposite signs. Sample eased time
            // without changing either endpoint's representation or the neighbouring outside segments.
            Quaternion a = ClipEvaluator.SafeRotation(first.Value), b = ClipEvaluator.SafeRotation(last.Value);
            int count = Math.Max(2, (int)MathF.Ceiling((end - start) * Math.Clamp(framesPerSecond, 30, 480)));
            first.Interpolation = KeyInterpolation.Linear;
            for (int i = 1; i < count; i++)
            {
                float t = (float)i / count, ease = t * t * (3 - 2 * t);
                track.Keys.Add(new() { Time = start + (end - start) * t, Value = ClipEvaluator.V(Quaternion.Normalize(Quaternion.Slerp(a, b, ease))) });
            }
        }
        else
        {
            first.Interpolation = KeyInterpolation.Cubic;
            first.OutTangent = Vector4.Zero; last.InTangent = Vector4.Zero;
        }
        track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
    }
    public static IEnumerable<List<AnimationKey>> KeySets(AnimationClip clip) => clip.Tracks.Concat(clip.RootTracks).Select(t => t.Keys)
        .Concat(clip.Layers.SelectMany(l => l.Tracks).Select(t => t.Keys)).Concat(clip.Layers.Select(l => l.WeightKeys)).Concat(clip.Constraints.Select(c => c.WeightKeys));
    public static AnimationClip Duplicate(AnimationClip clip)
    {
        AnimationClip copy = AnimationJson.Clone(clip); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " copy";
        foreach (AnimationEvent e in copy.Events) e.Id = Guid.NewGuid().ToString("N");
        return copy;
    }
    public static void Retime(AnimationClip clip, float duration)
    {
        if (!(duration > 0) || !float.IsFinite(duration)) throw new ArgumentOutOfRangeException(nameof(duration));
        float factor = duration / clip.Duration;
        foreach (List<AnimationKey> keys in KeySets(clip)) foreach (AnimationKey key in keys) { key.Time *= factor; key.InTangent /= factor; key.OutTangent /= factor; }
        foreach (AnimationEvent e in clip.Events) { e.Time = Math.Clamp(e.Time * factor, 0, duration); e.SourceNormalizedTime = null; }
        clip.Duration = duration;
    }
    public static void Reverse(AnimationClip clip)
    {
        foreach (List<AnimationKey> keys in KeySets(clip))
        {
            AnimationKey[] original = keys.Select(AnimationJson.Clone).ToArray();
            for (int i = 0; i < keys.Count; i++)
            {
                AnimationKey key = keys[i]; key.Time = clip.Duration - key.Time;
                (key.InTangent, key.OutTangent) = (-key.OutTangent, -key.InTangent);
                if (i > 0) key.Interpolation = original[i - 1].Interpolation;
            }
            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
        foreach (AnimationEvent e in clip.Events) { e.Time = Math.Clamp(clip.Duration - e.Time, 0, clip.Duration); e.SourceNormalizedTime = null; }
    }
    public static void Trim(AnimationClip clip, float start, float end)
    {
        if (!(start >= 0 && end > start && end <= clip.Duration)) throw new ArgumentOutOfRangeException(nameof(start));
        foreach (BoneTrack track in clip.Tracks.Concat(clip.RootTracks).Concat(clip.Layers.SelectMany(l => l.Tracks)))
            TrimKeys(track.Keys, start, end, track.Channel == TrackChannel.Rotation);
        foreach (List<AnimationKey> weights in clip.Layers.Select(l => l.WeightKeys).Concat(clip.Constraints.Select(c => c.WeightKeys))) TrimKeys(weights, start, end, false);
        clip.Events.RemoveAll(e => e.Time < start || e.Time > end);
        foreach (AnimationEvent e in clip.Events) { e.Time -= start; e.SourceNormalizedTime = null; }
        clip.Duration = end - start;
    }
    private static void TrimKeys(List<AnimationKey> keys, float start, float end, bool rotation)
    {
        if (keys.Count == 0) return;
        Vector4 first = ClipEvaluator.Sample(keys, start, rotation), last = ClipEvaluator.Sample(keys, end, rotation);
        Vector4 Derivative(float time)
        {
            int left = Math.Clamp(keys.FindLastIndex(k => k.Time <= time), 0, keys.Count - 1);
            if (left >= keys.Count - 1) left = Math.Max(0, left - 1);
            AnimationKey a = keys[left], b = keys[Math.Min(left + 1, keys.Count - 1)];
            float span = b.Time - a.Time; if (span <= 0 || a.Interpolation == KeyInterpolation.Step) return default;
            if (a.Interpolation == KeyInterpolation.Linear) return (b.Value - a.Value) / span;
            float t = Math.Clamp((time - a.Time) / span, 0, 1), t2 = t * t;
            return ((6 * t2 - 6 * t) * a.Value + (3 * t2 - 4 * t + 1) * span * a.OutTangent
                + (-6 * t2 + 6 * t) * b.Value + (3 * t2 - 2 * t) * span * b.InTangent) / span;
        }
        Vector4 firstDerivative = Derivative(start), lastDerivative = Derivative(end);
        KeyInterpolation interpolation = keys[Math.Clamp(keys.FindLastIndex(k => k.Time <= start), 0, keys.Count - 1)].Interpolation;
        keys.RemoveAll(k => k.Time <= start || k.Time >= end);
        foreach (AnimationKey k in keys) k.Time -= start;
        keys.Insert(0, new() { Time = 0, Value = first, InTangent = firstDerivative, OutTangent = firstDerivative, Interpolation = interpolation });
        keys.Add(new() { Time = end - start, Value = last, InTangent = lastDerivative, OutTangent = lastDerivative });
    }
    public static AnimationClip Split(AnimationClip clip, float time)
    {
        AnimationClip second = Duplicate(clip); second.Name = clip.Name + " end";
        Trim(second, time, clip.Duration); Trim(clip, 0, time); return second;
    }
    public static void CloseLoop(AnimationClip clip)
    {
        foreach (BoneTrack track in clip.Tracks.Concat(clip.Layers.SelectMany(l => l.Tracks)))
        {
            if (track.Keys.Count == 0) continue;
            Vector4 first = track.Keys[0].Value;
            track.Keys.RemoveAll(k => k.Time == clip.Duration);
            track.Keys.Add(new() { Time = clip.Duration, Value = first });
        }
        clip.Loop = true;
    }
    public static BoneTransform[] Mirror(SkeletonDefinition skeleton, IReadOnlyList<BoneTransform> pose, IReadOnlyDictionary<string, string> mirrors)
    {
        var output = new BoneTransform[pose.Count];
        Matrix4x4 reflect = Matrix4x4.CreateScale(-1, 1, 1);
        for (int i = 0; i < output.Length; i++)
        {
            string name = mirrors.GetValueOrDefault(skeleton.Bones[i].Name, skeleton.Bones[i].Name);
            int source = skeleton.Bones.FindIndex(b => b.Name == name);
            output[i] = BoneTransform.FromMatrix(reflect * pose[source < 0 ? i : source].Matrix * reflect);
        }
        return output;
    }
    public static AnimationClip Retarget(AnimationProject project, AnimationClip clip, RetargetProfile profile, float rate = 60)
    {
        SkeletonDefinition source = project.Skeletons.Single(s => s.Id == profile.SourceSkeleton);
        SkeletonDefinition target = project.Skeletons.Single(s => s.Id == profile.TargetSkeleton);
        if (clip.Skeleton != source.Id || rate <= 0) throw new InvalidDataException("Retarget source does not match the animation.");
        var result = new AnimationClip { Name = clip.Name + " retargeted", Skeleton = target.Id, Duration = clip.Duration, FramesPerSecond = rate, Loop = clip.Loop, Events = AnimationJson.Clone(clip.Events) };
        AnimationClip input = AnimationJson.Clone(clip); input.Loop = false;
        int count = Math.Max(1, (int)MathF.Ceiling(clip.Duration * rate));
        for (int frame = 0; frame <= count; frame++)
        {
            float time = clip.Duration * frame / count;
            BoneTransform[] pose = ClipEvaluator.Evaluate(source, input, MathF.Min(time, clip.Duration));
            foreach ((string from, string to) in profile.Bones.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                int a = source.Bones.FindIndex(b => b.Name == from), b = target.Bones.FindIndex(b => b.Name == to);
                if (a < 0 || b < 0) throw new InvalidDataException("Retarget profile references missing bone.");
                BoneTransform srcRest = source.Bones[a].Rest, dstRest = target.Bones[b].Rest;
                BoneTransform value = new(dstRest.Position + (pose[a].Position - srcRest.Position) * profile.RootScale,
                    Quaternion.Normalize(dstRest.Rotation * Quaternion.Inverse(srcRest.Rotation) * pose[a].Rotation), dstRest.Scale);
                AddPoseKeys(result, target.Bones[b].Name, value, time);
            }
        }
        return result;
    }
    public static AnimationClip Bake(SkeletonDefinition skeleton, AnimationClip clip, float rate = 60)
    {
        if (!float.IsFinite(rate) || rate <= 0 || rate > 480) throw new ArgumentOutOfRangeException(nameof(rate));
        AnimationClip output = AnimationJson.Clone(clip); output.Tracks.Clear(); output.Layers.Clear(); output.Constraints.Clear(); output.FramesPerSecond = rate;
        int count = Math.Max(1, (int)MathF.Ceiling(clip.Duration * rate));
        var source = AnimationJson.Clone(clip); source.Loop = false;
        for (int frame = 0; frame <= count; frame++)
        {
            float time = clip.Duration * frame / count;
            BoneTransform[] pose = ClipEvaluator.Evaluate(skeleton, source, time);
            for (int bone = 0; bone < pose.Length; bone++) AddPoseKeys(output, skeleton.Bones[bone].Name, pose[bone], time);
        }
        return output;
    }
    public static AnimationClip BakeScene(AnimationProject project, AnimationClip clip, float rate = 60)
    {
        if (!clip.Constraints.Any(c => c.TargetRig is { Length: > 0 })) return Bake(project.Skeletons.Single(s => s.Id == clip.Skeleton), clip, rate);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        var ownerIds = project.Scene.Where(i => i.Clip == clip.Id).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (FirstPersonProfile profile in project.FirstPersonProfiles)
            foreach (FirstPersonAction action in profile.Actions.Where(a => a.Clip == clip.Id || a.ArmsClip == clip.Id))
            {
                ownerIds.Add(action.Clip == clip.Id ? profile.MainRig : profile.ArmsRig ?? profile.MainRig);
                if (action.Clip is { } main) Set(profile.MainRig, main);
                if (action.ArmsClip is { } arms) Set(profile.ArmsRig ?? profile.MainRig, arms);
            }
        void Set(string rig, string id)
        {
            if (overrides.TryGetValue(rig, out string? prior) && prior != id) throw new InvalidDataException("Duplicate this clip before using different first-person partners with cross-rig constraints.");
            overrides[rig] = id;
        }
        RigInstance[] owners = project.Scene.Where(i => ownerIds.Contains(i.Id)).ToArray();
        if (owners.Length != 1) throw new InvalidDataException("A clip with cross-rig constraints needs exactly one owning scene instance. Duplicate the clip for independently constrained actors.");
        if (!float.IsFinite(rate) || rate <= 0 || rate > 480) throw new ArgumentOutOfRangeException(nameof(rate));
        AnimationProject source = AnimationJson.Clone(project); source.Clips.Single(c => c.Id == clip.Id).Loop = false;
        SkeletonDefinition skeleton = source.Skeletons.Single(s => s.Id == clip.Skeleton);
        AnimationClip output = AnimationJson.Clone(clip); output.Tracks.Clear(); output.Layers.Clear(); output.Constraints.Clear(); output.FramesPerSecond = rate;
        int count = Math.Max(1, (int)MathF.Ceiling(clip.Duration * rate));
        for (int frame = 0; frame <= count; frame++)
        {
            float time = clip.Duration * frame / count;
            overrides[owners[0].Id] = clip.Id;
            BoneTransform[] pose = RigSolver.Scene(source, time + owners[0].Start, overrides)[owners[0].Id];
            for (int bone = 0; bone < pose.Length; bone++) AddPoseKeys(output, skeleton.Bones[bone].Name, pose[bone], time);
        }
        return output;
    }
    public static void AddPoseKeys(AnimationClip clip, string bone, BoneTransform pose, float time)
    {
        foreach (TrackChannel channel in Enum.GetValues<TrackChannel>())
        {
            BoneTrack? track = clip.Tracks.FirstOrDefault(t => t.Bone == bone && t.Channel == channel);
            if (track is null) { track = new() { Bone = bone, Channel = channel }; clip.Tracks.Add(track); }
            track.Keys.RemoveAll(k => MathF.Abs(k.Time - time) < .000001f);
            track.Keys.Add(new() { Time = time, Value = channel switch { TrackChannel.Translation => ClipEvaluator.V(pose.Position), TrackChannel.Rotation => ClipEvaluator.V(pose.Rotation), _ => ClipEvaluator.V(pose.Scale) } });
            track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
    }
}
