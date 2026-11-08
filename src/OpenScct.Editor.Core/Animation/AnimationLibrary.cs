namespace OpenScct.Editor.Core.Animation;

public sealed record AnimationLibraryEntry(string Name, string Path, string Category);
public static class AnimationLibrary
{
    public static IEnumerable<AnimationLibraryEntry> Index(string? contentRoot)
    {
        if (contentRoot is null) yield break;
        string shared = Path.Combine(contentRoot, "shared", "chars");
        if (Directory.Exists(shared))
            foreach (string file in Directory.EnumerateFiles(shared, "*.glb", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                yield return new(Path.GetFileNameWithoutExtension(file), file, Path.GetFileName(Path.GetDirectoryName(file))!);
        string maps = Path.Combine(contentRoot, "svmap");
        if (!Directory.Exists(maps)) yield break;
        foreach (string map in Directory.EnumerateDirectories(maps).Order(StringComparer.Ordinal))
        {
            string chars = Path.Combine(map, "render", "chars");
            if (!Directory.Exists(chars)) continue;
            foreach (string file in Directory.EnumerateFiles(chars, "*.glb", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                yield return new(Path.GetFileNameWithoutExtension(file), file, Path.GetFileName(map));
        }
    }
}
