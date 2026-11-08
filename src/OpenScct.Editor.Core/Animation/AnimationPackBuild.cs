using System.IO.Compression;
using OpenScct.Animation;
using OpenScct.Content;

namespace OpenScct.Editor.Core.Animation;

public static class AnimationPackBuild
{
    public static SortedDictionary<string, byte[]> Build(AnimationProject project, string? projectFolder, float bakeRate = 60)
    {
        AnimationJson.Validate(project);
        AnimationProject compiled = AnimationJson.Clone(project);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var bundle = new AnimationBundle { Project = compiled };
        string Sound(string value)
        {
            string path = Path.IsPathRooted(value) ? value : Path.Combine(projectFolder ?? ".", value);
            if (!File.Exists(path))
            {
                if (value.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Missing animation sound: " + value);
                return value;
            }
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".wav" or ".ogg")) throw new InvalidDataException("Animation audio supports WAV and Ogg Vorbis.");
            byte[] bytes = File.ReadAllBytes(path); string hash = SvMapHash.Sha256Hex(bytes);
            string relative = "audio/animations/" + hash + extension;
            files.TryAdd(relative, bytes); bundle.Assets[relative] = hash;
            return "osanim." + hash;
        }
        foreach (AnimationClip clip in compiled.Clips)
            foreach (AnimationEvent e in clip.Events)
                if (e.Kind is AnimationEventKind.Sound or AnimationEventKind.StopSound) e.Value = Sound(e.Value);
        foreach (AnimationSequence sequence in compiled.Sequences)
            foreach (SequenceTrack track in sequence.Tracks)
            {
                if (track.Kind == SequenceTrackKind.Audio)
                    foreach (SequenceSegment segment in track.Segments) segment.Asset = Sound(segment.Asset ?? "");
                foreach (AnimationEvent e in track.Events)
                    if (e.Kind is AnimationEventKind.Sound or AnimationEventKind.StopSound) e.Value = Sound(e.Value);
            }
        foreach (SkeletonDefinition skeleton in compiled.Skeletons)
        {
            string relative = "render/animations/" + skeleton.Id + ".glb";
            byte[] glb = AnimationExport.Glb(project, skeleton.Id, projectFolder, bakeRate);
            files.Add(relative, glb); bundle.Assets.Add(relative, SvMapHash.Sha256Hex(glb)); skeleton.Model = relative;
        }
        for (int i = 0; i < compiled.Clips.Count; i++)
        {
            AnimationClip clip = compiled.Clips[i];
            if (clip.Layers.Count > 0 || clip.Constraints.Count > 0)
            {
                AnimationClip baked = AnimationEdits.BakeScene(project, project.Clips[i], bakeRate);
                baked.Events = compiled.Clips[i].Events; compiled.Clips[i] = baked;
            }
            compiled.Clips[i].Source = null;
        }
        compiled.Poses.Clear(); compiled.ControlRigs.Clear(); compiled.RetargetProfiles.Clear();
        files.Add(AnimationBundle.RelativePath, bundle.ToBytes()); return files;
    }
    public static void Write(AnimationProject project, string? projectFolder, string output, float bakeRate = 60)
    {
        foreach ((string relative, byte[] bytes) in Build(project, projectFolder, bakeRate))
        {
            string path = Path.Combine(output, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        }
    }
    public static void Export(AnimationProject project, string? projectFolder, string output, float bakeRate = 60)
    {
        SortedDictionary<string, byte[]> files = Build(project, projectFolder, bakeRate);
        string temporary = output + ".pending";
        using (ZipArchive archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            foreach ((string path, byte[] bytes) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream stream = entry.Open(); stream.Write(bytes);
            }
        File.Move(temporary, output, true);
    }
    public static string Install(string archivePath, string destination) => AnimationPackStore.Install(archivePath, destination);
}
