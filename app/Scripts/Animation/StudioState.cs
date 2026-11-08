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
    public bool UpdatingPoseKeys { get; private set; }
    public bool WorldSpace { get; set; }
    public bool ShowBones { get; set; } = true;
    public bool Ghosts { get; set; }
    public bool Trails { get; set; }
    public string TransformTool { get; set; } = "rotate";
    public GraphPlayer? GraphPlayer { get; set; }
    public string? SequenceId { get; set; }
    public Dictionary<int, BoneTransform> PoseOverrides { get; } = [];
    private sealed record PoseSnapshot(string? Clip, string? Rig, int Bone, int Layer, float Time, float Start, float End,
        string? Sequence, GraphPlayer? Graph, Dictionary<int, BoneTransform> Overrides, BoneTransform[] Pose);
    private PoseSnapshot? _poseBefore;
    public event Action? Changed;
    public event Action? TimeChanged;
    public SkeletonDefinition? Skeleton => Project.Skeletons.FirstOrDefault(s => s.Id == CurrentClip?.Skeleton)
        ?? Project.Skeletons.FirstOrDefault(s => s.Id == Project.Scene.FirstOrDefault(i => i.Id == RigId)?.Skeleton) ?? Project.Skeletons.FirstOrDefault();
    public AnimationClip? CurrentClip => Project.Clips.FirstOrDefault(c => c.Id == ClipId);
    public float Duration => SequenceId is { } id ? Project.Sequences.FirstOrDefault(s => s.Id == id)?.Duration ?? 1 : CurrentClip?.Duration ?? 1;
    public StudioState() { Session.Changed += DocumentChanged; Session.CapturePreviewState = CapturePreview; }
    public void Open(AnimationSession session)
    {
        Session.Changed -= DocumentChanged;
        if (Session.CapturePreviewState == CapturePreview) Session.CapturePreviewState = null;
        Session = session; Session.Changed += DocumentChanged; Session.CapturePreviewState = CapturePreview;
        ClipId = Project.Clips.FirstOrDefault()?.Id; RigId = Project.Scene.FirstOrDefault()?.Id;
        Bone = 0; Layer = -1; GraphPlayer = null; SequenceId = null; PoseOverrides.Clear(); _poseBefore = null;
        RangeStart = 0; RangeEnd = Duration; Time = 0; Playing = false; Notify();
    }
    private void DocumentChanged()
    {
        if (CurrentClip is null) ClipId = Project.Clips.FirstOrDefault()?.Id;
        if (!Project.Scene.Any(r => r.Id == RigId))
        {
            RigInstance? rig = Project.Scene.FirstOrDefault(r => r.Skeleton == CurrentClip?.Skeleton) ?? Project.Scene.FirstOrDefault();
            RigId = rig?.Id;
            if (rig is not null && rig.Skeleton != CurrentClip?.Skeleton)
                ClipId = rig.Clip ?? Project.Clips.FirstOrDefault(c => c.Skeleton == rig.Skeleton)?.Id;
        }
        Bone = Math.Clamp(Bone, 0, Math.Max(0, (Skeleton?.Bones.Count ?? 1) - 1));
        if (Layer >= (CurrentClip?.Layers.Count ?? 0)) Layer = -1;
        GraphPlayer = null; PoseOverrides.Clear(); _poseBefore = null; Notify();
    }
    public void SelectClip(string id)
    {
        ClipId = id; SequenceId = null; GraphPlayer = null; Layer = -1; PoseOverrides.Clear(); _poseBefore = null;
        RigId = Project.Scene.FirstOrDefault(i => i.Skeleton == CurrentClip?.Skeleton)?.Id;
        RangeStart = 0; RangeEnd = Duration; Time = 0; Notify();
    }
    public void SelectRig(string id)
    {
        RigInstance rig = Project.Scene.Single(r => r.Id == id);
        RigId = id; ClipId = rig.Clip ?? Project.Clips.FirstOrDefault(c => c.Skeleton == rig.Skeleton)?.Id;
        Bone = 0; Layer = -1; SequenceId = null; GraphPlayer = null; PoseOverrides.Clear(); _poseBefore = null;
        RangeStart = 0; RangeEnd = Duration; Time = 0; Notify();
    }
    public void SelectBone(int bone) { Bone = bone; Notify(); }
    public void SelectTool(string tool) { TransformTool = tool; Notify(); }
    public void Seek(float time) { Time = Math.Clamp(time, 0, Duration); PoseOverrides.Clear(); _poseBefore = null; TimeChanged?.Invoke(); }
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
        Playing = false;
        _poseBefore ??= CapturePose();
        PoseOverrides[bone] = value; TimeChanged?.Invoke();
        if (!commit) return;
        PoseSnapshot before = _poseBefore; _poseBefore = null;
        if (AutoKey) { CommitPoseKeys([bone], before); return; }
        PoseSnapshot after = CapturePose();
        if (!before.Pose.SequenceEqual(after.Pose))
        {
            AnimationSession session = Session;
            Session.RecordPreviewEdit("Pose bone", () => { if (Session == session) RestorePose(before); }, () => { if (Session == session) RestorePose(after); });
        }
        TimeChanged?.Invoke();
    }
    private PoseSnapshot CapturePose() => new(ClipId, RigId, Bone, Layer, Time, RangeStart, RangeEnd, SequenceId, GraphPlayer, new(PoseOverrides), Pose());
    private Action CapturePreview() => RestoreSnapshotAction(CapturePose());
    private Action RestoreSnapshotAction(PoseSnapshot pose)
    {
        AnimationSession session = Session;
        return () => { if (Session == session) RestorePose(pose); };
    }
    private void RestorePose(PoseSnapshot pose)
    {
        ClipId = pose.Clip; RigId = pose.Rig; Bone = pose.Bone; Layer = pose.Layer; Time = pose.Time;
        RangeStart = pose.Start; RangeEnd = pose.End; SequenceId = pose.Sequence; GraphPlayer = pose.Graph;
        Playing = false; _poseBefore = null; PoseOverrides.Clear();
        foreach (var (bone, value) in pose.Overrides) PoseOverrides[bone] = value;
        Notify();
    }
    public void KeyPose(IEnumerable<int>? bones = null) => CommitPoseKeys(bones);
    private void CommitPoseKeys(IEnumerable<int>? bones, PoseSnapshot? before = null)
    {
        if (CurrentClip is not { } clip || Skeleton is not { } skeleton) return;
        BoneTransform[] pose = Pose(); int[] selected = bones?.ToArray() ?? Enumerable.Range(0, pose.Length).ToArray();
        UpdatingPoseKeys = true;
        try
        {
            Session.EditClip("Key pose", clip.Id, edited =>
            {
                AnimationClip target = Layer < 0 ? edited : new AnimationClip { Tracks = edited.Layers[Layer].Tracks };
                foreach (int bone in selected) AnimationEdits.AddPoseKeys(target, skeleton.Bones[bone].Name, pose[bone], Time);
            }, before is null ? null : RestoreSnapshotAction(before));
        }
        finally { UpdatingPoseKeys = false; }
    }
    public void Notify() { Changed?.Invoke(); TimeChanged?.Invoke(); }
}
