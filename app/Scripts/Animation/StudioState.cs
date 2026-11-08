using System.Numerics;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;

namespace OpenScctEditor.Animation;

public sealed class StudioState
{
    public AnimationSession Session { get; private set; } = new();
    public AnimationProject Project => Session.Project;
    public string? ClipId { get; set; }
    public string? RigId { get; set; }
    public int Bone { get; set; }
    public int Layer { get; set; } = -1;
    public float Time { get; private set; }
    public float RangeStart { get; set; }
    public float RangeEnd { get; set; } = 1;
    public float Speed { get; set; } = 1;
    public bool Playing { get; set; }
    public bool Loop { get; set; } = true;
    public bool AutoKey { get; set; } = true;
    public bool WorldSpace { get; set; }
    public bool ShowBones { get; set; } = true;
    public bool Ghosts { get; set; }
    public bool Trails { get; set; }
    public string TransformTool { get; set; } = "rotate";
    public GraphPlayer? GraphPlayer { get; set; }
    public string? SequenceId { get; set; }
    public Dictionary<int, BoneTransform> PoseOverrides { get; } = [];
    public event Action? Changed;
    public event Action? TimeChanged;
    public SkeletonDefinition? Skeleton => Project.Skeletons.FirstOrDefault(s => s.Id == CurrentClip?.Skeleton)
        ?? Project.Skeletons.FirstOrDefault(s => s.Id == Project.Scene.FirstOrDefault(i => i.Id == RigId)?.Skeleton) ?? Project.Skeletons.FirstOrDefault();
    public AnimationClip? CurrentClip => Project.Clips.FirstOrDefault(c => c.Id == ClipId);
    public float Duration => SequenceId is { } id ? Project.Sequences.FirstOrDefault(s => s.Id == id)?.Duration ?? 1 : CurrentClip?.Duration ?? 1;
    public StudioState() { Session.Changed += DocumentChanged; }
    public void Open(AnimationSession session)
    {
        Session.Changed -= DocumentChanged; Session = session; Session.Changed += DocumentChanged;
        ClipId = Project.Clips.FirstOrDefault()?.Id; RigId = Project.Scene.FirstOrDefault()?.Id;
        Bone = 0; Layer = -1; GraphPlayer = null; SequenceId = null; PoseOverrides.Clear();
        RangeStart = 0; RangeEnd = Duration; Time = 0; Playing = false; Notify();
    }
    private void DocumentChanged()
    {
        if (CurrentClip is null) ClipId = Project.Clips.FirstOrDefault()?.Id;
        if (Layer >= (CurrentClip?.Layers.Count ?? 0)) Layer = -1;
        GraphPlayer = null; PoseOverrides.Clear(); Notify();
    }
    public void SelectClip(string id)
    {
        ClipId = id; SequenceId = null; GraphPlayer = null; Layer = -1; PoseOverrides.Clear();
        RigId = Project.Scene.FirstOrDefault(i => i.Skeleton == CurrentClip?.Skeleton)?.Id;
        RangeStart = 0; RangeEnd = Duration; Time = 0; Notify();
    }
    public void Seek(float time) { Time = Math.Clamp(time, 0, Duration); PoseOverrides.Clear(); TimeChanged?.Invoke(); }
    public void Advance(float delta)
    {
        if (!Playing) return;
        GraphPlayer?.Advance(delta * MathF.Abs(Speed));
        float end = MathF.Min(Duration, RangeEnd), start = Math.Clamp(RangeStart, 0, end);
        float next = Time + delta * Speed;
        if (next > end || next < start)
        {
            if (Loop && end > start) next = start + ((next - start) % (end - start) + end - start) % (end - start);
            else { next = Math.Clamp(next, start, end); Playing = false; }
        }
        Seek(next);
    }
    public BoneTransform[] Pose()
    {
        if (Skeleton is not { } skeleton) return [];
        BoneTransform[] pose = GraphPlayer?.Evaluate() ?? ClipEvaluator.Evaluate(skeleton, CurrentClip, Time);
        foreach ((int bone, BoneTransform value) in PoseOverrides) if (bone >= 0 && bone < pose.Length) pose[bone] = value;
        return pose;
    }
    public void SetPose(int bone, BoneTransform value, bool commit)
    {
        if (Skeleton is not { } skeleton || bone < 0 || bone >= skeleton.Bones.Count) return;
        PoseOverrides[bone] = value; TimeChanged?.Invoke();
        if (commit && AutoKey) KeyPose([bone]);
    }
    public void KeyPose(IEnumerable<int>? bones = null)
    {
        if (CurrentClip is not { } clip || Skeleton is not { } skeleton) return;
        BoneTransform[] pose = Pose(); int[] selected = bones?.ToArray() ?? Enumerable.Range(0, pose.Length).ToArray();
        Session.EditClip("Key pose", clip.Id, edited =>
        {
            AnimationClip target = Layer < 0 ? edited : new AnimationClip { Tracks = edited.Layers[Layer].Tracks };
            foreach (int bone in selected) AnimationEdits.AddPoseKeys(target, skeleton.Bones[bone].Name, pose[bone], Time);
        });
    }
    public void Notify() { Changed?.Invoke(); TimeChanged?.Invoke(); }
}
