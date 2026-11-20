using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>The small, release-bundled PS2 objective library. A saved project owns its own copy.</summary>
public static class Ps2ObjectiveAssets
{
    private static readonly Lazy<JsonObject> s_contract = new(() =>
    {
        using Stream stream = typeof(Ps2ObjectiveAssets).Assembly.GetManifestResourceStream("Ps2ObjectiveBundleManifest")!;
        return JsonNode.Parse(stream)!.AsObject();
    });
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProjectAssets> s_verified = new(StringComparer.OrdinalIgnoreCase);

    public static bool Used(MapProject project) => project.Actors.Any(a => a.Props.GetBool("bPS2Objective"));

    public static string? BundleRoot
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("OSE_PS2_OBJECTIVES");
            if (configured == "none") return null;
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, Path.GetDirectoryName(Environment.ProcessPath) ?? "" })
            {
                for (DirectoryInfo? folder = new(start); folder is not null; folder = folder.Parent)
                    foreach (string relative in new[] { "Data/Gameplay/Ps2Objectives", "local/ps2-objectives-bundle" })
                    {
                        string candidate = Path.Combine(folder.FullName, relative);
                        if (File.Exists(Path.Combine(candidate, "bundle.json"))) return candidate;
                    }
            }
            return null;
        }
    }

    /// <summary>Verify every bundled byte against the source-controlled contract before using it.</summary>
    public static ProjectAssets? Bundled => BundleRoot is { } folder ? s_verified.GetOrAdd(folder, Verify) : null;

    private static ProjectAssets Verify(string folder)
    {
        foreach (var (relative, record) in s_contract.Value["files"]!.AsObject())
        {
            string file = Path.Combine(folder, relative);
            if (!File.Exists(file) || new FileInfo(file).Length != record!["size"]!.GetValue<long>() ||
                !Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))).Equals(record["sha256"]!.GetValue<string>(), StringComparison.Ordinal))
                throw new InvalidDataException("The bundled PS2 objective asset is missing or changed: " + relative);
        }
        return ProjectAssets.For(folder);
    }

    /// <summary>Copy on first save. Save As already copies assets; user edits to an existing project stay intact.</summary>
    public static void EnsureProject(MapProject project, string projectFolder)
    {
        if (!Used(project)) return;
        ProjectAssets target = ProjectAssets.For(projectFolder);
        if (target.Manifest["ps2Objectives"] is JsonObject) return;
        ProjectAssets source = Bundled ?? throw new InvalidDataException("PS2 objective assets are unavailable. Set OSE_PS2_OBJECTIVES to a library converted from your own PS2 game.");
        JsonObject manifest = target.Edit();
        foreach (string section in new[] { "materials", "meshes", "skeletalMeshes", "textures", "sounds" })
            if (manifest[section] is JsonObject existing)
                foreach (var (asset, entry) in source.Manifest[section]!.AsObject())
                    if (existing[asset] is { } occupied && !JsonNode.DeepEquals(occupied, entry))
                        throw new InvalidDataException("A different project asset already uses " + asset + "; its record and files were preserved.");
        foreach (var (relative, _) in s_contract.Value["files"]!.AsObject())
        {
            if (relative == "assets/assets.json") continue;
            string destination = Path.Combine(projectFolder, relative);
            string original = Path.Combine(BundleRoot!, relative);
            if (File.Exists(destination) && !File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(original)))
                throw new InvalidDataException("A different project asset already occupies " + relative + "; it was preserved.");
        }
        foreach (var (relative, _) in s_contract.Value["files"]!.AsObject())
        {
            if (relative == "assets/assets.json") continue;
            string destination = Path.Combine(projectFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(Path.Combine(BundleRoot!, relative), destination);
        }
        foreach (string section in new[] { "materials", "meshes", "skeletalMeshes", "textures", "sounds" })
        {
            JsonObject destination = (manifest[section] ??= new JsonObject()).AsObject();
            foreach (var (asset, entry) in source.Manifest[section]!.AsObject())
                if (!destination.ContainsKey(asset)) destination[asset] = entry!.DeepClone();
        }
        manifest["ps2Objectives"] = source.Manifest["ps2Objectives"]!.DeepClone();
        ProjectAssets.Save(projectFolder, manifest);
    }
}
