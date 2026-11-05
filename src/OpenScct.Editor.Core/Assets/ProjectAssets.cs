using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// A project's own assets: <c>&lt;map&gt;.osmap/assets/assets.json</c> (format <c>osmap.assets/1</c>) lists the
/// map-local materials, meshes, textures and sounds; their files sit next to it (<c>meshes/</c>, <c>textures/</c>,
/// <c>sounds/</c>). Paths are in the <c>MyLevel</c> package, as UE2 names a map's own objects.
/// </summary>
public sealed class ProjectAssets
{
    public const string Folder = "assets";
    public const string ManifestFile = "assets.json";
    public const string Format = "osmap.assets/1";

    private static readonly ConcurrentDictionary<string, ProjectAssets> s_cache = new(StringComparer.OrdinalIgnoreCase);

    private ProjectAssets(string root, JsonObject manifest)
    {
        Root = root;
        Manifest = manifest;
    }

    /// <summary>The <c>assets</c> folder (texture and mesh paths in the manifest are relative to it).</summary>
    public string Root { get; }

    /// <summary>The manifest as read (a copy is edited and written by <see cref="Save"/>).</summary>
    public JsonObject Manifest { get; }

    public JsonObject Materials => Section("materials");

    public JsonObject Meshes => Section("meshes");

    public JsonObject Textures => Section("textures");

    public JsonObject Sounds => Section("sounds");

    /// <summary>The assets of a project folder, read once per manifest version; an empty set when it has none.</summary>
    public static ProjectAssets For(string projectFolder)
    {
        string root = Path.Combine(Path.GetFullPath(projectFolder), Folder);
        string file = Path.Combine(root, ManifestFile);
        var info = new FileInfo(file);
        string key = info.Exists ? $"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : file + "|none";
        return s_cache.GetOrAdd(key, _ => new ProjectAssets(root, Read(file)));
    }

    private static JsonObject Read(string file)
    {
        if (!File.Exists(file))
        {
            return new JsonObject { ["format"] = Format };
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(file)) is not JsonObject o)
            {
                return new JsonObject { ["format"] = Format };
            }

            // Parsed objects fill themselves in on first read; do it now, so threads sharing this copy only read.
            JsonTree.FillIn(o);
            return o;
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new InvalidDataException($"{file}: {e.Message}", e);
        }
    }

    private JsonObject Section(string key) => Manifest[key] as JsonObject ?? new JsonObject();

    public LibraryMaterial? FindMaterial(string name)
    {
        if (Find(Materials, name) is not (string path, JsonObject entry) || entry["flags"] is not JsonObject flags)
        {
            return null;
        }

        return new LibraryMaterial(Root, AssetPaths.LocalPackage, path, flags, entry["gameplay"] as JsonObject,
            Int(entry["uSize"], 256), Int(entry["vSize"], 256));
    }

    public LibraryMesh? FindMesh(string asset)
    {
        if (Find(Meshes, asset) is not (string path, JsonObject entry) || entry["file"]?.GetValue<string>() is not { } file)
        {
            return null;
        }

        int? collision = entry["collisionMesh"] is JsonValue c && c.TryGetValue(out int ci) ? ci : null;
        return new LibraryMesh(Root, AssetPaths.LocalPackage, FilePath(file), path, path, Int(entry["mesh"], 0), collision);
    }

    /// <summary>The absolute path of a file the manifest names (relative to <see cref="Root"/>, forward slashes).</summary>
    public string FilePath(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    /// <summary>An entry by full path, else by Package.Name, ignoring case.</summary>
    private static (string Path, JsonObject Entry)? Find(JsonObject section, string name)
    {
        foreach ((string key, JsonNode? value) in section)
        {
            if (value is JsonObject o && key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return (key, o);
            }
        }

        foreach ((string key, JsonNode? value) in section)
        {
            if (value is JsonObject o && AssetPaths.Short(key).Equals(AssetPaths.Short(name), StringComparison.OrdinalIgnoreCase))
            {
                return (key, o);
            }
        }

        return null;
    }

    private static int Int(JsonNode? n, int fallback) => n is JsonValue v && v.TryGetValue(out int i) ? i : fallback;

    /// <summary>A copy of the manifest to edit; <see cref="Save"/> writes it.</summary>
    public JsonObject Edit() => Manifest.DeepClone().AsObject();

    /// <summary>Copies a project's <c>assets</c> folder to another project folder (Save As), so its map-local meshes and
    /// materials still resolve there. Files already in the target are replaced; nothing happens when there is no
    /// assets folder or both folders are the same.</summary>
    public static void CopyFolder(string fromProject, string toProject)
    {
        string from = Path.Combine(Path.GetFullPath(fromProject), Folder);
        string to = Path.Combine(Path.GetFullPath(toProject), Folder);
        if (!Directory.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Writes an edited manifest (canonical JSON, through a temporary file).</summary>
    public static void Save(string projectFolder, JsonObject manifest)
    {
        string root = Path.Combine(Path.GetFullPath(projectFolder), Folder);
        Directory.CreateDirectory(root);
        manifest["format"] = Format;
        foreach (string section in new[] { "materials", "meshes", "textures", "sounds" })
        {
            manifest[section] ??= new JsonObject();
        }

        string file = Path.Combine(root, ManifestFile);
        byte[] bytes = CanonicalJson.ToFileBytes(SvJson.Parse(manifest.ToJsonString(), file));
        string tmp = file + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, file, overwrite: true);
    }
}
