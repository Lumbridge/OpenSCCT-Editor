using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// The class-default static meshes of gameplay actors (security cameras and their wall mounts, laser triggers, motion
/// sensors, mines, sticky cams, window panes, extinguishers...) from OpenSCCT's shared render pack:
/// <c>&lt;content&gt;/shared/render/gameplay/gameplay.glb</c>, with <c>render.json</c>'s gameplay mesh table naming the
/// glTF node of each mesh. A converted map's world.glb carries only the meshes its actors store, so a device that
/// draws its class's mesh is found only here, as the game finds it (OpenSCCT's GameplayObjects). Indexed once per
/// content folder.
/// </summary>
public sealed class GameplayPack
{
    /// <summary>The <see cref="LibraryMesh.Origin"/> of the pack's meshes.</summary>
    public const string Origin = "gameplay";

    private static readonly ConcurrentDictionary<string, GameplayPack> s_packs = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _renderRoot;
    private readonly Lazy<Dictionary<string, LibraryMesh>> _meshes;

    private GameplayPack(string? renderRoot)
    {
        _renderRoot = renderRoot;
        _meshes = new Lazy<Dictionary<string, LibraryMesh>>(Index);
    }

    public static GameplayPack Empty { get; } = new(null);

    /// <summary>The pack of a content folder (<c>shared/render</c>), or the empty pack when it was not built.</summary>
    public static GameplayPack For(string? contentRoot)
    {
        string? render = contentRoot is null ? null : Path.Combine(contentRoot, "shared", "render");
        return render is null || !File.Exists(Path.Combine(render, "render.json"))
            ? Empty
            : s_packs.GetOrAdd(Path.GetFullPath(render), r => new GameplayPack(r));
    }

    internal static void Invalidate(string contentRoot) =>
        s_packs.TryRemove(Path.GetFullPath(Path.Combine(contentRoot, "shared", "render")), out _);

    /// <summary>A mesh by its full path (Package.Group.Name), else by Package.Name, ignoring case.</summary>
    public LibraryMesh? FindMesh(string asset)
    {
        Dictionary<string, LibraryMesh> index = _meshes.Value;
        return index.GetValueOrDefault(asset) ?? index.GetValueOrDefault(AssetPaths.Short(asset));
    }

    private Dictionary<string, LibraryMesh> Index()
    {
        var index = new Dictionary<string, LibraryMesh>(StringComparer.OrdinalIgnoreCase);
        if (_renderRoot is null)
        {
            return index;
        }

        string folder = Path.Combine(_renderRoot, "gameplay");
        string glb = Path.Combine(folder, "gameplay.glb");
        JsonObject? table;
        try
        {
            table = JsonNode.Parse(File.ReadAllBytes(Path.Combine(_renderRoot, "render.json"))) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return index;
        }

        if (table?["gameplayMeshes"] is not JsonObject meshes || GlbCache.TryReadJson(glb)?["nodes"] is not JsonArray nodes)
        {
            return index;
        }

        var meshOfNode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonNode? n in nodes)
        {
            if (n?["name"] is JsonValue name && n["mesh"] is JsonValue mesh)
            {
                meshOfNode.TryAdd(name.GetValue<string>(), mesh.GetValue<int>());
            }
        }

        foreach ((string asset, JsonNode? node) in meshes)
        {
            if (node is JsonValue v && v.TryGetValue(out string? name) && meshOfNode.TryGetValue(name, out int mesh))
            {
                var m = new LibraryMesh(folder, Origin, glb, asset, name, mesh);
                index.TryAdd(asset, m);
                index.TryAdd(AssetPaths.Short(asset), m);
            }
        }

        return index;
    }
}
