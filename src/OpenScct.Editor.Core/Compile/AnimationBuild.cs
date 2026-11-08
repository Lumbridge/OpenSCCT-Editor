using OpenScct.Animation;
using OpenScct.Content;
using OpenScct.Editor.Core.Animation;

namespace OpenScct.Editor.Core.Compile;

internal static class AnimationBuild
{
    public static void Write(CompileContext context)
    {
        if (context.Options.ProjectFolder is not { } root) return;
        string assets = Path.Combine(root, "assets", "animations");
        if (!Directory.Exists(assets)) return;
        var projects = new List<AnimationProject>();
        var digests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var bindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Directory.EnumerateDirectories(assets, "*.osanim").Order(StringComparer.Ordinal))
        {
            AnimationSession source = AnimationSession.Open(folder);
            foreach (AnimationBinding binding in source.Project.Bindings)
                if (!bindings.Add(binding.Target + "/" + binding.Slot)) throw new InvalidDataException("Conflicting map animation binding: " + binding.Target + "/" + binding.Slot);
            SortedDictionary<string, byte[]> files = AnimationPackBuild.Build(source.Project, folder);
            string temporaryManifest = System.Text.Encoding.UTF8.GetString(files[AnimationBundle.RelativePath]);
            AnimationBundle bundle = System.Text.Json.JsonSerializer.Deserialize<AnimationBundle>(temporaryManifest, AnimationJson.Options)!;
            projects.Add(bundle.Project);
            foreach (var file in files.Where(f => f.Key != AnimationBundle.RelativePath))
            {
                if (!context.Files.TryAdd(file.Key, file.Value) && !context.Files[file.Key].AsSpan().SequenceEqual(file.Value)) throw new InvalidDataException("Conflicting animation asset: " + file.Key);
                digests[file.Key] = SvMapHash.Sha256Hex(file.Value);
            }
        }
        if (projects.Count == 0) return;
        var combined = new AnimationProject();
        foreach (AnimationProject project in projects)
        {
            combined.Skeletons.AddRange(project.Skeletons); combined.Clips.AddRange(project.Clips); combined.Graphs.AddRange(project.Graphs);
            combined.Sequences.AddRange(project.Sequences); combined.Bindings.AddRange(project.Bindings); combined.Scene.AddRange(project.Scene);
            combined.FirstPersonProfiles.AddRange(project.FirstPersonProfiles);
        }
        AnimationJson.Validate(combined);
        context.Files[AnimationBundle.RelativePath] = new AnimationBundle { Project = combined, Assets = digests }.ToBytes();
        context.Info("animation.compiled", $"{combined.Clips.Count} clips, {combined.Graphs.Count} graphs, {combined.Sequences.Count} sequences");
    }
}
