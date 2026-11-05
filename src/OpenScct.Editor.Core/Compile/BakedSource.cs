using System.Buffers.Binary;
using OpenScct.Content.Collision;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// The SVMap folder a baked project carries its world from (a converted map imported for editing): its collision
/// and render files are kept instead of rebuilding them from brushes the project does not have.
/// </summary>
public sealed class BakedSource
{
    private BakedSource(string folder, string map, BakedWorld settings, CollisionData collision)
    {
        Folder = folder;
        Map = map;
        KeepRender = settings.KeepRender;
        KeepCollision = settings.KeepCollision;
        Collision = collision;
    }

    public string Folder { get; }

    public string Map { get; }

    public bool KeepRender { get; }

    public bool KeepCollision { get; }

    public CollisionData Collision { get; }

    /// <summary>Finds the source map (a folder path, a relative one read against the project folder first, or a map id in
    /// the content library) and reads its collision.</summary>
    public static BakedSource? Open(CompileContext ctx, BakedWorld settings)
    {
        string? folder = FolderOf(settings.SourceMap, ctx.Library, ctx.Options.ProjectFolder);
        if (folder is null)
        {
            ctx.Error("baked.missing", $"the baked world's source map '{settings.SourceMap}' was not found (a map folder or a map in {ctx.ContentRoot ?? "no content folder"})");
            return null;
        }

        try
        {
            CollisionData collision = CollisionReader.Load(Path.Combine(folder, CollisionReader.RelativePath), strict: false);
            return new BakedSource(folder, Path.GetFileName(folder), settings, collision);
        }
        catch (Exception e) when (e is IOException or OpenScct.Content.SvMapFormatException)
        {
            ctx.Error("baked.collision", $"the baked world's collision could not be read: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The folder of a baked world's source map: <paramref name="sourceMap"/> itself when it is an SVMap folder (a
    /// relative path is read against <paramref name="projectFolder"/> first when one is given), else the map of that id
    /// in <paramref name="library"/>, else null.
    /// </summary>
    public static string? FolderOf(string sourceMap, Assets.AssetResolver library, string? projectFolder = null)
    {
        if (sourceMap.Length == 0)
        {
            return null;
        }

        static bool IsMap(string dir) => Directory.Exists(dir) && File.Exists(Path.Combine(dir, "manifest.json"));
        if (projectFolder is not null && !Path.IsPathRooted(sourceMap))
        {
            string near = Path.Combine(projectFolder, sourceMap);
            if (IsMap(near))
            {
                return Path.GetFullPath(near);
            }
        }

        return IsMap(sourceMap) ? Path.GetFullPath(sourceMap) : library.MapFolder(sourceMap);
    }

    /// <summary>The payload of a 1.1 chunk parallel to a table of <paramref name="count"/> rows, or null.</summary>
    public byte[]? Chunk(string fourcc, int count, int stride) => Chunk(Collision, fourcc, count, stride);

    public static byte[]? Chunk(CollisionData data, string fourcc, int count, int stride) =>
        data.UnknownChunks.FirstOrDefault(c => c.FourCC == fourcc && c.Count == (uint)count && c.Payload.Length >= count * stride)?.Payload;

    /// <summary>The source object of each collider (CSRC: the actor id, also for render-only actors whose COLL actor is
    /// none), or nulls when the map has no CSRC chunk.</summary>
    public static string?[] ColliderSources(CollisionData data)
    {
        var sources = new string?[data.Colliders.Length];
        if (Chunk(data, "CSRC", sources.Length, 16) is { } csrc)
        {
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = data.GetString(BinaryPrimitives.ReadUInt32LittleEndian(csrc.AsSpan(i * 16)));
            }
        }

        return sources;
    }

    /// <summary>Ids of the source's render instances that an import turns into StaticMeshActors.</summary>
    public IReadOnlySet<string> MeshActors => _meshActors ??= ReadMeshActors();

    /// <summary>Ids of every actor the source map has: gameplay.json, the instance table, and the lighting, fx and
    /// audio records. An actor not among them was added in the editor.</summary>
    public IReadOnlySet<string> Actors => _actors ??= ReadActors();

    private HashSet<string>? _meshActors;
    private HashSet<string>? _actors;

    private HashSet<string> ReadActors()
    {
        SvJsonObject? Read(string relative) =>
            File.Exists(Path.Combine(Folder, relative)) ? SvJson.Parse(File.ReadAllBytes(Path.Combine(Folder, relative)), relative) as SvJsonObject : null;
        static IEnumerable<string> Ids(SvJsonNode? records, string key) =>
            (records as SvJsonArray ?? []).OfType<SvJsonObject>().Select(r => r[key]).OfType<SvJsonString>().Select(s => s.Value);

        var ids = new HashSet<string>(MeshActors, StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(Ids(Read("sim/gameplay.json")?["actors"], "id"));
        ids.UnionWith(Ids(Read("render/lighting.json")?["lights"], "name"));
        ids.UnionWith(Ids(Read("audio/audio.json")?["actors"], "id"));
        if (Read("render/fx.json")?["emitters"] is SvJsonObject emitters)
        {
            ids.UnionWith(emitters.Select(kv => kv.Key));
        }

        return ids;
    }

    private HashSet<string> ReadMeshActors()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (GlbJson.Read(Path.Combine(Folder, "render", "world.glb")) is SvJsonObject g && g["extras"] is SvJsonObject extras &&
            extras["svmap"] is SvJsonObject svmap && svmap["instances"] is SvJsonArray instances)
        {
            foreach (SvJsonObject i in instances.OfType<SvJsonObject>())
            {
                if (i["actor"] is SvJsonString id && SvMapImport.IsMeshActor(i))
                {
                    ids.Add(id.Value);
                }
            }
        }

        // A gameplay actor's mesh is drawn by its gameplay object, not by a StaticMeshActor of the import.
        string gameplay = Path.Combine(Folder, "sim", "gameplay.json");
        if (File.Exists(gameplay) && SvJson.Parse(File.ReadAllBytes(gameplay), "sim/gameplay.json") is SvJsonObject doc &&
            doc["actors"] is SvJsonArray actors)
        {
            ids.ExceptWith(actors.OfType<SvJsonObject>().Select(a => a["id"]).OfType<SvJsonString>().Select(s => s.Value));
        }

        return ids;
    }

    /// <summary>The source's zone actors by zone index.</summary>
    public IEnumerable<string?> ZoneActors => Collision.Zones.Select(z => Collision.GetString(z.Actor));

    /// <summary>The zone of a point by the source's world BSP (UModel::PointRegion), else by zone bounds.</summary>
    public int ZoneAt(Vec3 p) => ZoneAt(Collision, p);

    /// <summary>The zone of a point by a map's collision data: its world BSP, else its zone bounds.</summary>
    public static int ZoneAt(CollisionData collision, Vec3 p)
    {
        var at = new OpenScct.Core.FVector((float)p.X, (float)p.Y, (float)p.Z);
        Collider? world = collision.Colliders.Cast<Collider?>().FirstOrDefault(c => c!.Value.Kind == ColliderKind.WorldBsp && c.Value.BspCount > 0);
        if (world is { } w)
        {
            int node = (int)w.BspStart, parent = -1;
            bool front = false;
            for (int guard = 0; node >= 0 && guard <= collision.BspNodes.Length; guard++)
            {
                parent = node;
                CollisionBspNode n = collision.BspNodes[node];
                front = n.Plane.PlaneDot(at) >= 0f;
                node = front ? n.Front : n.Back;
            }

            int zone = parent < 0 ? 0 : front ? collision.BspNodes[parent].ZoneFront : collision.BspNodes[parent].ZoneBack;
            return zone < collision.Zones.Length ? zone : 0;
        }

        for (int i = 1; i < collision.Zones.Length; i++)
        {
            if (collision.Zones[i].Contains(at))
            {
                return i;
            }
        }

        return 0;
    }
}
