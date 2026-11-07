using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// A skeletal mesh converted by OpenSCCT (<c>chars/&lt;Package&gt;/&lt;Mesh&gt;.glb</c>): the glb, the glTF meshes its
/// nodes draw, and the UE2 mesh's RotOrigin and scale the game places it with (the glb's <c>svmap</c> extras).
/// </summary>
public sealed record SkeletalMeshFile(string GlbPath, string Asset, IReadOnlyList<int> Meshes, Rot RotOrigin, Vec3 MeshScale);

/// <summary>
/// The skeletal meshes the views can draw in their reference pose: SAnimatedMesh props and DT_Mesh fish (a mesh the
/// game animates). Looked up as OpenSCCT's SkeletalModel finds them: the shared copy
/// (<c>&lt;content&gt;/shared/chars</c>), else a converted map's own (<c>svmap/&lt;map&gt;/render/chars</c>, maps in
/// name order). Indexed once per content folder.
/// </summary>
public sealed class SkeletalLibrary
{
    private static readonly ConcurrentDictionary<string, SkeletalLibrary> s_libraries = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _contentRoot;
    private readonly Lazy<Dictionary<string, string>> _files;
    private readonly ConcurrentDictionary<string, SkeletalMeshFile?> _read = new(StringComparer.OrdinalIgnoreCase);

    private SkeletalLibrary(string? contentRoot)
    {
        _contentRoot = contentRoot;
        _files = new Lazy<Dictionary<string, string>>(Index);
    }

    public static SkeletalLibrary Empty { get; } = new(null);

    public static SkeletalLibrary For(string? contentRoot) =>
        contentRoot is null || !Directory.Exists(contentRoot) ? Empty : s_libraries.GetOrAdd(Path.GetFullPath(contentRoot), r => new SkeletalLibrary(r));

    internal static void Invalidate(string contentRoot) => s_libraries.TryRemove(Path.GetFullPath(contentRoot), out _);

    /// <summary>Whether a converted copy of <paramref name="asset"/> exists, without reading it (the file list only).</summary>
    public bool Has(string asset) => _files.Value.ContainsKey(AssetPaths.Short(asset));

    /// <summary>The skeletal mesh <paramref name="asset"/> (Package.Mesh, ignoring case), or null when no converted
    /// copy exists or its glb draws nothing. The first call per file parses its glTF JSON (megabytes for a pawn), so
    /// the views make it on a worker; later calls are cached.</summary>
    public SkeletalMeshFile? FindMesh(string asset) =>
        _files.Value.TryGetValue(AssetPaths.Short(asset), out string? glb) ? _read.GetOrAdd(glb, path => Read(path, asset)) : null;

    private Dictionary<string, string> Index()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_contentRoot is null)
        {
            return index;
        }

        var roots = new List<string> { Path.Combine(_contentRoot, "shared", "chars") };
        string svmap = Path.Combine(_contentRoot, "svmap");
        if (Directory.Exists(svmap))
        {
            roots.AddRange(Directory.GetDirectories(svmap).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "render", "chars")));
        }

        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string package in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                foreach (string glb in Directory.GetFiles(package, "*.glb").Order(StringComparer.Ordinal))
                {
                    index.TryAdd($"{Path.GetFileName(package)}.{Path.GetFileNameWithoutExtension(glb)}", glb);
                }
            }
        }

        return index;
    }

    private static SkeletalMeshFile? Read(string glb, string asset)
    {
        if (GlbCache.TryReadJson(glb) is not { } json || json["nodes"] is not JsonArray nodes)
        {
            return null;
        }

        var meshes = nodes.Select(n => n?["mesh"] is JsonValue m ? m.GetValue<int>() : -1).Where(m => m >= 0).Distinct().ToList();
        if (meshes.Count == 0)
        {
            return null;
        }

        JsonNode? extras = json["extras"]?["svmap"];
        Rot rotOrigin = extras?["rotOrigin"] is JsonArray { Count: >= 3 } r
            ? new Rot((int)r[0]!.GetValue<double>(), (int)r[1]!.GetValue<double>(), (int)r[2]!.GetValue<double>())
            : default;
        Vec3 scale = extras?["meshScale"] is JsonArray { Count: >= 3 } s
            ? new Vec3(s[0]!.GetValue<double>(), s[1]!.GetValue<double>(), s[2]!.GetValue<double>())
            : Vec3.One;
        return new SkeletalMeshFile(glb, asset, meshes, rotOrigin, scale);
    }
}
