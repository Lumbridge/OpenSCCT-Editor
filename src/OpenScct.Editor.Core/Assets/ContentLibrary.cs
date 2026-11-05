using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Compile.Output;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// A material an authored map can use: its materials.json entry (texture paths relative to <see cref="RenderDir"/>),
/// its gameplay props and its texture size. <see cref="Origin"/> names where it comes from (a converted map's id,
/// or <c>MyLevel</c> for the project's own assets).
/// </summary>
public sealed record LibraryMaterial(string RenderDir, string Origin, string Source, JsonObject Flags, JsonObject? Gameplay, int USize, int VSize)
{
    /// <summary>The base texture's path relative to <see cref="RenderDir"/>, or null.</summary>
    public string? Texture => Flags["texture"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>
/// A static mesh an authored map can place: the glb that holds it, its library node and mesh, and an optional
/// collision mesh in the same glb (map-local meshes carry generated hulls; library meshes collide per polygon).
/// </summary>
public sealed record LibraryMesh(string RenderDir, string Origin, string GlbPath, string Asset, string NodeName, int Mesh, int? CollisionMesh = null);

/// <summary>
/// The converted maps under <c>&lt;content&gt;/svmap</c> as a library of materials, textures and static meshes, so an
/// authored map can use the stock game's surfaces. Indexed once per content folder; maps are searched in name order
/// and the first one that has an asset supplies it.
/// </summary>
public sealed class ContentLibrary
{
    private static readonly ConcurrentDictionary<string, ContentLibrary> s_libraries = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _svmapRoot;
    private readonly string? _onlyMap;
    private readonly Lazy<Dictionary<string, LibraryMaterial>> _materials;
    private readonly Lazy<Dictionary<string, LibraryMesh>> _meshes;

    private ContentLibrary(string? svmapRoot, string? onlyMap = null)
    {
        _svmapRoot = svmapRoot;
        _onlyMap = onlyMap;
        _materials = new Lazy<Dictionary<string, LibraryMaterial>>(IndexMaterials);
        _meshes = new Lazy<Dictionary<string, LibraryMesh>>(IndexMeshes);
    }

    public static ContentLibrary Empty { get; } = new(null);

    /// <summary>The <c>svmap</c> folder this library reads, or null for the empty library.</summary>
    public string? SvmapRoot => _svmapRoot;

    public static ContentLibrary For(string? contentRoot)
    {
        string? svmap = contentRoot is null ? null : Path.Combine(contentRoot, "svmap");
        return svmap is null || !Directory.Exists(svmap) ? Empty : s_libraries.GetOrAdd(Path.GetFullPath(svmap), p => new ContentLibrary(p));
    }

    /// <summary>A library of one converted map folder, wherever it is (a baked project's source map). Not cached: an
    /// import can replace the folder.</summary>
    public static ContentLibrary ForMap(string mapDir)
    {
        string full = Path.GetFullPath(mapDir);
        return new ContentLibrary(Path.GetDirectoryName(full), full);
    }

    /// <summary>The folder of a map in the library, or null.</summary>
    public string? MapFolder(string map)
    {
        if (_onlyMap is not null && !Path.GetFileName(_onlyMap).Equals(map, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? dir = _svmapRoot is null ? null : Path.Combine(_svmapRoot, map);
        return dir is not null && File.Exists(Path.Combine(dir, "manifest.json")) ? dir : null;
    }

    /// <summary>A material by its full path (Package.Group.Name), else by Package.Name, ignoring case.</summary>
    public LibraryMaterial? FindMaterial(string name)
    {
        Dictionary<string, LibraryMaterial> index = _materials.Value;
        return index.GetValueOrDefault(name) ?? index.GetValueOrDefault(AssetPaths.Short(name));
    }

    public LibraryMesh? FindMesh(string asset)
    {
        Dictionary<string, LibraryMesh> index = _meshes.Value;
        return index.GetValueOrDefault(asset) ?? index.GetValueOrDefault(AssetPaths.Short(asset));
    }

    /// <summary>Builds the material and mesh indexes now (safe on any thread), so a later lookup does not read them.</summary>
    public void Warm()
    {
        _ = _materials.Value;
        _ = _meshes.Value;
    }

    /// <summary>The library's map folders (those with a manifest), in name order.</summary>
    public IEnumerable<string> MapDirs() => _onlyMap is not null ? [_onlyMap]
        : _svmapRoot is null ? []
        : Directory.GetDirectories(_svmapRoot).Order(StringComparer.Ordinal).Where(d => File.Exists(Path.Combine(d, "manifest.json")));

    private Dictionary<string, LibraryMaterial> IndexMaterials()
    {
        var index = new Dictionary<string, LibraryMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in MapDirs())
        {
            foreach ((string key, LibraryMaterial m) in ReadMaterials(dir))
            {
                index.TryAdd(m.Source, m);
                index.TryAdd(AssetPaths.Short(m.Source), m);
                index.TryAdd(key, m);
            }
        }

        return index;
    }

    /// <summary>The textured materials of one converted map's <c>render/materials.json</c>, by table key.</summary>
    public static IEnumerable<(string Key, LibraryMaterial Material)> ReadMaterials(string mapDir)
    {
        string path = Path.Combine(mapDir, "render", "materials.json");
        if (!File.Exists(path))
        {
            yield break;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        }
        catch (System.Text.Json.JsonException)
        {
            yield break;
        }

        JsonObject? gameplay = root["gameplay"] as JsonObject;
        JsonObject? graph = root["graph"] as JsonObject;
        if (root["materials"] is not JsonObject materials)
        {
            yield break;
        }

        string render = Path.Combine(mapDir, "render");
        string origin = Path.GetFileName(mapDir);
        foreach ((string key, JsonNode? value) in materials)
        {
            if (value is not JsonObject entry || entry["flags"] is not JsonObject flags || flags["texture"] is null)
            {
                continue;
            }

            string source = entry["source"]?.GetValue<string>() ?? key;
            (int u, int v) = TextureSize(graph?[source]?["props"], render, flags["texture"]!.GetValue<string>());
            yield return (key, new LibraryMaterial(render, origin, source, flags, gameplay?[source] as JsonObject, u, v));
        }
    }

    private static (int U, int V) TextureSize(JsonNode? props, string renderDir, string texture)
    {
        if (Positive(props?["USize"]) is int u && Positive(props?["VSize"]) is int v)
        {
            return (u, v);
        }

        return Png.SizeOf(Path.Combine(renderDir, texture)) is { } size ? size : (GridTexture.UvSize, GridTexture.UvSize);
    }

    private static int? Positive(JsonNode? n) =>
        n is JsonValue v && (v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : 0) is var x && x > 0 ? x : null;

    private Dictionary<string, LibraryMesh> IndexMeshes()
    {
        var index = new Dictionary<string, LibraryMesh>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in MapDirs())
        {
            foreach (LibraryMesh m in ReadMeshes(dir))
            {
                index.TryAdd(m.Asset, m);
                index.TryAdd(AssetPaths.Short(m.Asset), m);
            }
        }

        return index;
    }

    /// <summary>The plain (not vertex-coloured) library meshes of one converted map's <c>render/world.glb</c>.</summary>
    public static IEnumerable<LibraryMesh> ReadMeshes(string mapDir)
    {
        string path = Path.Combine(mapDir, "render", "world.glb");
        JsonObject? json = GlbCache.TryReadJson(path);
        if (json?["nodes"] is not JsonArray nodes)
        {
            yield break;
        }

        string render = Path.Combine(mapDir, "render");
        string origin = Path.GetFileName(mapDir);
        foreach (JsonNode? n in nodes)
        {
            JsonNode? svmap = n?["extras"]?["svmap"];
            if (svmap?["role"]?.GetValue<string>() != "libraryMesh" || svmap["variant"]?.GetValue<string>() is not "" ||
                n!["mesh"] is not JsonValue mesh || svmap["asset"]?.GetValue<string>() is not { } asset)
            {
                continue;
            }

            yield return new LibraryMesh(render, origin, path, asset, n["name"]?.GetValue<string>() ?? asset, mesh.GetValue<int>());
        }
    }
}

/// <summary>
/// Loaded glb files, shared by the compiler, the catalogue and the viewport. Keyed by path, size and write time, so a
/// re-imported map-local mesh is read again. The converted maps' glbs add up to well over a gigabyte, so the least
/// recently used ones are dropped past <see cref="BudgetBytes"/> (a caller still holding one keeps it alive).
/// </summary>
public static class GlbCache
{
    /// <summary>How many bytes of glb files stay loaded.</summary>
    public const long BudgetBytes = 768L * 1024 * 1024;

    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, LinkedListNode<Slot>> s_slots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Slot> s_recent = new();
    private static long s_bytes;

    public static Glb Load(string path)
    {
        var info = new FileInfo(path);
        string key = $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        Lazy<Glb> glb;
        lock (s_lock)
        {
            if (s_slots.TryGetValue(key, out LinkedListNode<Slot>? node))
            {
                s_recent.Remove(node);
                s_recent.AddFirst(node);
                glb = node.Value.Glb;
            }
            else
            {
                glb = new Lazy<Glb>(() =>
                {
                    Glb loaded = Glb.Load(File.ReadAllBytes(path));
                    // Worker threads read meshes of one glb at once: fill its JSON in before sharing it.
                    JsonTree.FillIn(loaded.Json);
                    return loaded;
                });
                s_slots[key] = s_recent.AddFirst(new Slot(key, info.Length, glb));
                s_bytes += info.Length;
                while (s_bytes > BudgetBytes && s_recent.Last is { } last && last != s_recent.First)
                {
                    s_recent.RemoveLast();
                    s_slots.Remove(last.Value.Key);
                    s_bytes -= last.Value.Bytes;
                }
            }
        }

        return glb.Value;
    }

    /// <summary>Only the JSON chunk of a glb, or null when the file is missing or broken.</summary>
    public static JsonObject? TryReadJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Glb.ReadJson(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record Slot(string Key, long Bytes, Lazy<Glb> Glb);
}

/// <summary>JSON trees shared between threads.</summary>
internal static class JsonTree
{
    /// <summary>Parsed JSON objects and arrays build their children on first read, which is not safe on two threads
    /// at once; this reads the whole tree once, so later readers only read.</summary>
    public static void FillIn(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach ((string _, JsonNode? v) in o)
                {
                    FillIn(v);
                }

                break;
            case JsonArray a:
                foreach (JsonNode? v in a)
                {
                    FillIn(v);
                }

                break;
        }
    }
}

/// <summary>Asset path helpers (<c>Package.Group.Name</c>).</summary>
public static class AssetPaths
{
    /// <summary>The package of the project's own assets, as in UE2 (MyLevel).</summary>
    public const string LocalPackage = "MyLevel";

    /// <summary>Package.Name for a longer path (the compiler matches either form).</summary>
    public static string Short(string path)
    {
        string[] parts = path.Split('.');
        return parts.Length > 2 ? $"{parts[0]}.{parts[^1]}" : path;
    }

    public static (string Package, string Group, string Name) Split(string path)
    {
        string[] parts = path.Split('.');
        return parts.Length switch
        {
            1 => ("", "", parts[0]),
            2 => (parts[0], "", parts[1]),
            _ => (parts[0], string.Join('.', parts[1..^1]), parts[^1]),
        };
    }

    /// <summary>A name made of letters, digits and _ (asset names, file names).</summary>
    public static string Safe(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        return sb.Length == 0 ? "Asset" : sb.ToString();
    }
}
