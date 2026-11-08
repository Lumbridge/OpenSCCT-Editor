using System.Numerics;
using OpenScct.Animation;

namespace OpenScct.Editor.Core.Animation;

/// <summary>Independent animation document, command history and recovery. UI and CLI share every operation.</summary>
public sealed class AnimationSession
{
    private sealed record Change(string Name, byte[] Before, byte[] After, string? Clip, long BeforeRevision, long AfterRevision,
        Action? UndoPreview = null, Action? RedoPreview = null)
    {
        public Action? BeforePreview { get; init; }
        public Action? AfterPreview { get; set; }
    }
    private readonly List<Change> _history = [];
    private int _cursor;
    private long _revision, _savedRevision, _nextRevision;
    public AnimationProject Project { get; private set; }
    public string? Folder { get; private set; }
    public bool Dirty => _revision != _savedRevision;
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _history.Count;
    public event Action? Changed;
    public Func<Action>? CapturePreviewState { get; set; }
    public AnimationSession(AnimationProject? project = null)
    {
        Project = project ?? new();
    }
    public static AnimationSession Open(string folder)
    {
        string root = Directory.Exists(folder) ? Path.GetFullPath(folder) : Path.GetDirectoryName(Path.GetFullPath(folder))!;
        string manifest = Path.Combine(root, "animation.json");
        // The previous complete document is recoverable if a process stopped during a save.
        if (!File.Exists(manifest) && File.Exists(manifest + ".previous")) manifest += ".previous";
        var session = new AnimationSession(AnimationJson.Read(File.ReadAllBytes(manifest))) { Folder = root };
        return session;
    }
    public void Edit(string name, Action<AnimationProject> edit)
    {
        Action? preview = CapturePreviewState?.Invoke();
        byte[] before = AnimationJson.Write(Project);
        try { edit(Project); AnimationJson.Validate(Project); }
        catch { Project = AnimationJson.Read(before); throw; }
        byte[] after = AnimationJson.Write(Project);
        if (before.AsSpan().SequenceEqual(after)) return;
        _history.RemoveRange(_cursor, _history.Count - _cursor);
        var change = new Change(name, before, after, null, _revision, ++_nextRevision) { BeforePreview = preview };
        _history.Add(change); _revision = _nextRevision; _cursor++;
        Changed?.Invoke();
        change.AfterPreview = CapturePreviewState?.Invoke();
    }
    public void EditClip(string name, string id, Action<AnimationClip> edit, Action? restoreBeforePreview = null)
    {
        Action? preview = restoreBeforePreview ?? CapturePreviewState?.Invoke();
        AnimationClip clip = Project.Clips.Single(c => c.Id == id);
        byte[] before = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(clip, AnimationJson.Options);
        try { edit(clip); AnimationJson.Validate(Project); }
        catch { Project.Clips[Project.Clips.FindIndex(c => c.Id == id)] = System.Text.Json.JsonSerializer.Deserialize<AnimationClip>(before, AnimationJson.Options)!; throw; }
        byte[] after = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(clip, AnimationJson.Options);
        if (before.AsSpan().SequenceEqual(after)) return;
        _history.RemoveRange(_cursor, _history.Count - _cursor);
        var change = new Change(name, before, after, id, _revision, ++_nextRevision) { BeforePreview = preview };
        _history.Add(change); _revision = _nextRevision; _cursor++; Changed?.Invoke();
        change.AfterPreview = CapturePreviewState?.Invoke();
    }
    private void Restore(Change change, bool after)
    {
        if (change.UndoPreview is { } undo)
        {
            (after ? change.RedoPreview! : undo)();
            return;
        }
        byte[] bytes = after ? change.After : change.Before;
        if (change.Clip is { } id) Project.Clips[Project.Clips.FindIndex(c => c.Id == id)] = System.Text.Json.JsonSerializer.Deserialize<AnimationClip>(bytes, AnimationJson.Options)!;
        else Project = AnimationJson.Read(bytes);
        _revision = after ? change.AfterRevision : change.BeforeRevision;
    }
    /// <summary>Temporary authoring poses share undo ordering with document edits without changing saved clip keys.</summary>
    public void RecordPreviewEdit(string name, Action undo, Action redo)
    {
        ArgumentNullException.ThrowIfNull(undo); ArgumentNullException.ThrowIfNull(redo);
        _history.RemoveRange(_cursor, _history.Count - _cursor);
        _history.Add(new(name, [], [], null, _revision, _revision, undo, redo)); _cursor++;
    }
    public void Undo()
    {
        if (!CanUndo) return;
        Change change = _history[--_cursor]; Restore(change, false);
        if (change.UndoPreview is null) { Changed?.Invoke(); change.BeforePreview?.Invoke(); }
    }
    public void Redo()
    {
        if (!CanRedo) return;
        Change change = _history[_cursor++]; Restore(change, true);
        if (change.RedoPreview is null) { Changed?.Invoke(); change.AfterPreview?.Invoke(); }
    }
    public void Save(string? folder = null)
    {
        string root = Path.GetFullPath(folder ?? Folder ?? throw new InvalidOperationException("Choose an .osanim folder first."));
        AnimationJson.Validate(Project);
        Directory.CreateDirectory(root);
        // Save As copies the document's complete asset tree before changing its owning folder.
        if (Folder is { } previous && !string.Equals(root, previous, StringComparison.OrdinalIgnoreCase))
        {
            string assets = Path.Combine(previous, "assets");
            if (Directory.Exists(assets))
                foreach (string file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
                {
                    string destination = Path.Combine(root, Path.GetRelativePath(previous, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
                }
        }
        AnimationProject portable = AnimationJson.Clone(Project);
        AnimationImport.CopyDependencies(portable, Folder ?? ".", root);
        AtomicWrite(Path.Combine(root, "animation.json"), AnimationJson.Write(portable));
        Project = portable; Folder = root; _savedRevision = _revision; Changed?.Invoke();
    }
    public void Autosave(string recoveryFolder)
    {
        Directory.CreateDirectory(recoveryFolder);
        AnimationProject copy = AnimationJson.Clone(Project);
        if (Folder is { } root)
        {
            foreach (SkeletonDefinition skeleton in copy.Skeletons)
                if (skeleton.Model is { } model && !Path.IsPathRooted(model)) skeleton.Model = Path.Combine(root, model);
            AnimationImport.RewriteAudio(copy, value => Path.GetExtension(value).ToLowerInvariant() is ".wav" or ".ogg" && !Path.IsPathRooted(value) ? Path.Combine(root, value) : value);
        }
        AtomicWrite(Path.Combine(recoveryFolder, Project.Id + ".json"), AnimationJson.Write(copy));
    }
    public static AnimationSession Recover(string path) => new(AnimationJson.Read(File.ReadAllBytes(path))) { _savedRevision = -1 };
    public static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = path + ".pending";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".previous", true);
        else File.Move(temporary, path);
    }
    public void Import(string path, string? targetSkeleton = null)
    {
        AnimationProject imported = AnimationImport.Read(path);
        if (Folder is { } root) AnimationImport.CopyDependencies(imported, Path.GetDirectoryName(Path.GetFullPath(path))!, root);
        Edit("Import animation", p =>
        {
            if (p.Skeletons.Count == 0) p.Name = imported.Name;
            if (imported.Skeletons.Count == 1 && imported.Skeletons[0].Model is null)
            {
                SkeletonDefinition source = imported.Skeletons[0];
                SkeletonDefinition? target = p.Skeletons.FirstOrDefault(s => (targetSkeleton is null || s.Id == targetSkeleton) && source.Bones.All(b =>
                {
                    BoneDefinition? match = s.Bones.FirstOrDefault(t => t.Name == b.Name);
                    return match is not null && (b.Parent < 0 || match.Parent >= 0 && s.Bones[match.Parent].Name == source.Bones[b.Parent].Name);
                }));
                if (target is not null)
                {
                    foreach (AnimationClip clip in imported.Clips) clip.Skeleton = target.Id;
                    imported.Skeletons.Clear(); imported.Scene.Clear();
                }
            }
            p.Skeletons.AddRange(imported.Skeletons); p.Clips.AddRange(imported.Clips);
            p.Scene.AddRange(imported.Scene); p.ControlRigs.AddRange(imported.ControlRigs);
        });
    }
    public string NewClip(string skeletonId, string name = "New clip")
    {
        string id = Guid.NewGuid().ToString("N");
        Edit("New animation", p => p.Clips.Add(new() { Id = id, Name = name, Skeleton = skeletonId, FramesPerSecond = p.FramesPerSecond }));
        return id;
    }
    public void RemoveRig(string id) => Edit("Remove scene rig", p =>
    {
        p.Scene.RemoveAll(r => r.Id == id);
        foreach (AnimationClip clip in p.Clips) clip.Constraints.RemoveAll(c => c.TargetRig == id);
        foreach (ControlRig rig in p.ControlRigs) rig.Controls.RemoveAll(c => c.TargetRig == id);
        foreach (AnimationSequence sequence in p.Sequences) sequence.Tracks.RemoveAll(t => t.Target == id);
        var profiles = p.FirstPersonProfiles.Where(f => f.MainRig == id || f.ArmsRig == id).Select(f => f.Id).ToHashSet();
        p.FirstPersonProfiles.RemoveAll(f => profiles.Contains(f.Id));
    });
    public void Reimport(string clipId)
    {
        AnimationClip clip = Project.Clips.Single(c => c.Id == clipId);
        string source = clip.Source ?? throw new InvalidOperationException("This clip was authored natively and has no import source.");
        AnimationProject imported = AnimationImport.Read(source);
        AnimationClip replacement = imported.Clips.FirstOrDefault(c => c.Name == (clip.SourceClip ?? clip.Name)) ?? throw new InvalidDataException("The source no longer contains this clip.");
        AnimationEdits.Retime(replacement, clip.Duration);
        EditClip("Reimport source tracks", clipId, target => { target.Tracks = replacement.Tracks; target.FramesPerSecond = replacement.FramesPerSecond; });
    }
    public void SetKey(string clipId, string bone, TrackChannel channel, float time, Vector4 value, int layer = -1)
    {
        EditClip("Set key", clipId, clip =>
        {
            List<BoneTrack> tracks = layer < 0 ? clip.Tracks : clip.Layers[layer].Tracks;
            BoneTrack? track = tracks.FirstOrDefault(t => t.Bone == bone && t.Channel == channel);
            if (track is null) { track = new() { Bone = bone, Channel = channel }; tracks.Add(track); }
            track.Keys.RemoveAll(k => MathF.Abs(k.Time - time) < .00001f);
            track.Keys.Add(new() { Time = time, Value = channel == TrackChannel.Rotation ? ClipEvaluator.V(ClipEvaluator.SafeRotation(value)) : value });
            track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            clip.Duration = MathF.Max(clip.Duration, time);
        });
    }
}
