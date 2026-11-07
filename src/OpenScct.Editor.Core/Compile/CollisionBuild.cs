using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using OpenScct.Content;
using OpenScct.Content.Collision;
using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// <c>sim/collision.bin</c> (SVMap 1.1, with the CSRC TFLX CYLX chunks):
/// <list type="bullet">
/// <item>collider 0, the world BSP: the solid-leaf tree of the CSG (semisolid faces included, as UE2 builds them),
/// every colliding node polygon as a fan of triangles in the node's range, NotSolid and portal splitters as non-CSG
/// nodes without triangles, leaf zones on the nodes; in baked mode the source map's world collider instead, with the
/// authored brushes as one extra brush collider;</item>
/// <item>volumes (gameplay actors with a brush shape) as brush colliders with a hull when convex; movers with a shape
/// as mover colliders;</item>
/// <item>static mesh actors: the mesh's per-polygon and simple collision sets as the converter wrote them (the baked
/// source map's, else those of the converted map that ships the mesh), each set with the traces that use it; a mesh no
/// converted map has collision for collides by its render triangles; gameplay objects also by their triggered and
/// damaged meshes, starting disabled;</item>
/// <item>conveyors' built looks as per-polygon colliders without an actor;</item>
/// <item>actor cylinders and the interaction cylinders of gameplay objects;</item>
/// <item>zones: zone 0 the LevelInfo, then the world's zones.</item>
/// </list>
/// </summary>
public sealed partial class CollisionBuild
{
    public const ColliderFlags WorldFlags = ColliderFlags.CollideActors | ColliderFlags.BlockActors | ColliderFlags.BlockPlayers |
        ColliderFlags.BlockZeroExtent | ColliderFlags.BlockNonZeroExtent | ColliderFlags.BlockCamera;

    private const ushort NotCsg = 0x01;

    /// <summary>Material gameplay props → TFLG flag bits (collision.md).</summary>
    private static readonly (string Prop, CollisionMaterialFlags Flag)[] s_materialFlags =
    [
        ("bClimbable", CollisionMaterialFlags.Climbable),
        ("bNoSplitJump", CollisionMaterialFlags.NoSplitJump),
        ("bNoStickyCam", CollisionMaterialFlags.NoStickyCam),
    ];

    /// <summary>Material gameplay props of the TFLX materialBits, bit 0 first.</summary>
    private static readonly string[] s_materialBits = ["bHideInEMFVision", "bHideInThermalVision", "bEchelonLightThrough"];
    private const int SetWorld = 0, SetBrush = 1, SetPerPoly = 2, SetSimple = 3;

    /// <summary>CSRC traceUse: zero-extent and box traces both use the set.</summary>
    private const ushort TraceBoth = 3;

    private readonly CompileContext _ctx;
    private readonly List<string> _strings = new();
    private readonly Dictionary<string, uint> _stringIndex = new(StringComparer.Ordinal);
    private readonly List<FVector> _verts = new();
    private readonly List<CollisionTriangle> _tris = new();
    private readonly List<CollisionMaterial> _materials = new();
    private readonly List<(uint PolyFlags, ushort Bits, byte Occlusion)> _materialExt = new();
    private readonly Dictionary<string, ushort> _materialIndex = new(StringComparer.Ordinal);
    private readonly List<CollisionPlane> _planes = new();
    private readonly List<CollisionHull> _hulls = new();
    private readonly List<CollisionBspNode> _bsp = new();
    private readonly List<Collider> _colliders = new();
    private readonly List<(uint Source, uint Asset, byte Set, ushort Variants, ushort TraceUse)> _sources = new();
    private readonly List<CollisionCylinder> _cylinders = new();
    private readonly List<(byte Kind, uint Class)> _cylinderExt = new();
    private readonly List<CollisionZone> _zones = new();
    private readonly Dictionary<string, (List<Vec3> Verts, List<int> Indices, List<string?> Materials)?> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MeshSet>?> _meshSets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CollisionData?> _libraryCollision = new(StringComparer.OrdinalIgnoreCase);

    private CollisionBuild(CompileContext ctx) => _ctx = ctx;

    public static void Write(CompileContext ctx)
    {
        var b = new CollisionBuild(ctx);
        try
        {
            b.Build();
            CollisionData data = b.Data();
            ctx.Files[CollisionReader.RelativePath] = b.WithExtensions(CollisionWriter.Write(data));
            ctx.Count("collision.vertices", data.Vertices.Length);
            ctx.Count("collision.triangles", data.Triangles.Length);
            ctx.Count("collision.bspNodes", data.BspNodes.Length);
            ctx.Count("collision.colliders", data.Colliders.Length);
            ctx.Count("collision.hulls", data.Hulls.Length);
            ctx.Count("collision.cylinders", data.Cylinders.Length);
            ctx.Count("collision.zones", data.Zones.Length);
            ctx.Count("collision.materials", data.Materials.Length);
        }
        catch (SvMapFormatException e)
        {
            ctx.Error("collision.invalid", e.Message);
        }
    }

    private void Build()
    {
        Material(null, 0, CollisionMaterialFlags.None);
        var collided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_ctx.Baked is { KeepCollision: true } baked)
        {
            CopyBaked(baked, collided);
            AddAuthoredBrushes();
        }
        else if (!_ctx.World.Csg.WorldSolid)
        {
            // Only the baked render is kept and nothing rebuilds the collision: the added brushes are all it has.
            AddCollider(ColliderKind.WorldBsp, WorldFlags, null, 0, 0, 0, 0, Vec3.Zero, Rot.Zero, Vec3.One, Vec3.Zero, Aabb.Empty,
                $"{_ctx.MapId}.World", null, SetWorld);
            AddAuthoredBrushes();
        }
        else
        {
            AddWorldBsp();
        }

        foreach (Actor a in _ctx.Gameplay)
        {
            if (collided.Contains(a.Id))
            {
                continue;
            }

            if (ConveyorDesign.Is(a))
            {
                AddConveyor(a);
            }
            else if (a.Shape is not null)
            {
                AddShapeActor(a);
            }
            else if (IsMeshPrimitive(a))
            {
                if (!AddMeshActor(a, a.Id) && _ctx.Rules.Flag(a, "bCollideActors"))
                {
                    _ctx.Warn("collision.mesh-missing",
                        $"actor {a.Id}: {_ctx.NothingShips($"its static mesh {MeshPath(a)}")}, so it collides as its cylinder", ObjectRef.Actor(a.Id));
                    AddCylinder(a);
                }
            }
            else if (_ctx.Rules.Flag(a, "bCollideActors"))
            {
                AddCylinder(a);
            }
        }

        foreach (Actor a in _ctx.Render)
        {
            if (!collided.Contains(a.Id) && IsMeshPrimitive(a))
            {
                AddMeshActor(a, null);
            }
        }

        foreach (Actor a in _ctx.Gameplay)
        {
            AddInteraction(a);
        }

        if (_zones.Count == 0)
        {
            AddZones();
        }
    }

    // ---- shared tables ------------------------------------------------------------------------------------------

    private uint String(string? s)
    {
        if (s is null)
        {
            return CollisionData.None;
        }

        if (!_stringIndex.TryGetValue(s, out uint i))
        {
            i = (uint)_strings.Count;
            _strings.Add(s);
            _stringIndex[s] = i;
        }

        return i;
    }

    /// <summary>A world surface's material; semisolid ones keep that flag, as the converter writes them.</summary>
    private ushort SurfaceMaterial(Surface s) =>
        Material(s.Material, s.Flags, s.Kind == SurfaceKind.Semisolid ? CollisionMaterialFlags.Semisolid : CollisionMaterialFlags.None);

    /// <summary>The collision material of a surface: its surface type, climb / split jump / sticky camera bits, vision
    /// bits and occlusion from the material library, its semisolid and invisible bits from the polygon flags. A
    /// material without a surface type of its own takes <paramref name="surface"/>.</summary>
    private ushort Material(string? name, int polyFlags, CollisionMaterialFlags extra, ESurfaceType surface = ESurfaceType.SURFACE_Generic)
    {
        JsonObject? info = name is null ? null : _ctx.Library.FindMaterial(name)?.Gameplay;
        byte own = (byte)Int(info?["SurfaceType"]);
        byte surfaceType = own != 0 ? own : (byte)surface;
        byte occlusion = (byte)Int(info?["OcclusionType"]);
        CollisionMaterialFlags flags = extra;
        foreach ((string prop, CollisionMaterialFlags flag) in s_materialFlags)
        {
            if (Int(info?[prop]) != 0)
            {
                flags |= flag;
            }
        }

        if ((polyFlags & PolyFlags.Invisible) != 0)
        {
            flags |= CollisionMaterialFlags.Invisible;
        }

        ushort bits = 0;
        for (int i = 0; i < s_materialBits.Length; i++)
        {
            if (Int(info?[s_materialBits[i]]) != 0)
            {
                bits |= (ushort)(1 << i);
            }
        }

        return Material(name, surfaceType, flags, (uint)polyFlags, bits, occlusion);
    }

    private ushort Material(string? name, byte surface, CollisionMaterialFlags flags, uint polyFlags, ushort bits, byte occlusion)
    {
        string key = $"{name}|{surface}|{(int)flags}|{polyFlags}|{bits}|{occlusion}";
        if (!_materialIndex.TryGetValue(key, out ushort index))
        {
            index = (ushort)_materials.Count;
            _materials.Add(new CollisionMaterial(surface, flags, String(name)));
            _materialExt.Add((polyFlags, bits, occlusion));
            _materialIndex[key] = index;
        }

        return index;
    }

    private static int Int(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : v.TryGetValue(out bool b) && b ? 1 : 0
        : 0;

    private uint Vertex(Vec3 v)
    {
        _verts.Add(F(v));
        return (uint)(_verts.Count - 1);
    }

    /// <summary>A vertex adder for one collider: equal float positions share one VERT record.</summary>
    private Func<Vec3, uint> SharedVertices()
    {
        var index = new Dictionary<FVector, uint>();
        return v =>
        {
            FVector f = F(v);
            if (!index.TryGetValue(f, out uint i))
            {
                i = Vertex(v);
                index[f] = i;
            }

            return i;
        };
    }

    private static FVector F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static FRotator R(Rot r) => new(r.Pitch, r.Yaw, r.Roll);

    private void AddCollider(ColliderKind kind, ColliderFlags flags, string? actor, uint vertStart, uint triStart, uint bspStart,
        uint hullStart, Vec3 location, Rot rotation, Vec3 scale, Vec3 prePivot, Aabb world, string source, string? asset, byte set,
        ushort variants = 0, ushort traceUse = TraceBoth)
    {
        if (world.IsEmpty)
        {
            world = new Aabb(location, location);
        }

        _colliders.Add(new Collider(kind, flags, String(actor), CollisionData.None, vertStart, (uint)_verts.Count - vertStart, triStart,
            (uint)_tris.Count - triStart, bspStart, (uint)_bsp.Count - bspStart, hullStart, (uint)_hulls.Count - hullStart, F(location),
            R(rotation), F(scale), F(prePivot), F(world.Min), F(world.Max)));
        _sources.Add((String(source), String(asset), set, variants, traceUse));
    }

    // ---- world --------------------------------------------------------------------------------------------------

    private void AddWorldBsp()
    {
        WorldModel w = _ctx.World;
        if (w.Tree.Root is BspNode root)
        {
            Flatten(root, SharedVertices(), (0, 0));
        }

        Aabb bounds = Aabb.Of(_verts.Select(v => new Vec3(v.X, v.Y, v.Z)));
        AddCollider(ColliderKind.WorldBsp, WorldFlags, null, 0, 0, 0, 0, Vec3.Zero, Rot.Zero, Vec3.One, Vec3.Zero, bounds,
            $"{_ctx.MapId}.World", null, SetWorld);
    }

    /// <summary>
    /// Writes the tree depth first, front before back; returns the node's index. The runtime, like the engine's
    /// NumVertices test, counts a node as CSG only when it has triangles, so a CSG node without any of its own (a leaf
    /// fix, or a polygon too thin to triangulate) shares the range of the nearest ancestor that has some (for a leaf
    /// fix usually its parent, whose plane it repeats) rather than adding triangles that would collide.
    /// </summary>
    private int Flatten(BspNode n, Func<Vec3, uint> vertex, (uint Start, uint Count) ancestorTris)
    {
        int index = _bsp.Count;
        _bsp.Add(default);
        uint triStart = (uint)_tris.Count;
        if (n.Csg)
        {
            foreach (Poly p in n.Polys)
            {
                AddFan(p, vertex, SurfaceMaterial(p.Surface), 0);
            }
        }

        uint triCount = (uint)_tris.Count - triStart;
        if (n.Csg && triCount == 0)
        {
            (triStart, triCount) = ancestorTris;
        }

        (uint, uint) below = triCount > 0 ? (triStart, triCount) : ancestorTris;
        int front = n.Front is BspNode f ? Flatten(f, vertex, below) : -1;
        int back = n.Back is BspNode b ? Flatten(b, vertex, below) : -1;
        byte zoneFront = n.Front is BspLeaf lf ? (byte)Math.Clamp(lf.Zone, 0, 255) : (byte)0;
        byte zoneBack = n.Back is BspLeaf lb ? (byte)Math.Clamp(lb.Zone, 0, 255) : (byte)0;
        _bsp[index] = new CollisionBspNode(new CollisionPlane(F(n.Plane.N), (float)n.Plane.D), front, back, -1, -1, triStart, triCount,
            n.Csg && triCount > 0 ? (ushort)0 : NotCsg, zoneFront, zoneBack);
        return index;
    }

    private void AddFan(Poly p, Func<Vec3, uint> vertex, ushort material, ushort collider)
    {
        (List<(int, int, int)> tris, Vec3? centre) = MeshPrep.Triangulate(p);
        var idx = new uint[p.Verts.Length + 1];
        for (int i = 0; i < p.Verts.Length; i++)
        {
            idx[i] = vertex(p.Verts[i]);
        }

        if (centre is { } c)
        {
            idx[p.Verts.Length] = vertex(c);
        }

        foreach ((int a, int b, int cc) in tris)
        {
            if (idx[a] != idx[b] && idx[b] != idx[cc] && idx[a] != idx[cc])
            {
                _tris.Add(new CollisionTriangle(idx[a], idx[b], idx[cc], collider, material));
            }
        }
    }

    /// <summary>Baked mode: the authored brushes' solid surfaces (CSG over an empty outside) as one brush collider.</summary>
    private void AddAuthoredBrushes()
    {
        List<Poly> solid = _ctx.World.Solid;
        if (solid.Count == 0)
        {
            return;
        }

        if (_ctx.World.Csg.Brushes.Any(b => b.Oper == CsgOper.Subtract && b.ShapesSpace))
        {
            _ctx.Warn("baked.subtract", "subtract brushes cannot carve the baked world; they only cut the authored add brushes");
        }

        uint vertStart = (uint)_verts.Count, triStart = (uint)_tris.Count;
        ushort collider = (ushort)_colliders.Count;
        Func<Vec3, uint> vertex = SharedVertices();
        foreach (Poly p in solid)
        {
            AddFan(p, vertex, SurfaceMaterial(p.Surface), collider);
        }

        AddCollider(ColliderKind.Brush, WorldFlags, null, vertStart, triStart, (uint)_bsp.Count, (uint)_hulls.Count, Vec3.Zero, Rot.Zero,
            Vec3.One, Vec3.Zero, Aabb.Of(solid.SelectMany(p => p.Verts)), $"{_ctx.MapId}.AuthoredBrushes", null, SetBrush);
    }

    private void AddHull(IReadOnlyList<Plane3> planes, List<Vec3>? corners, ushort material)
    {
        uint planeStart = (uint)_planes.Count;
        foreach (Plane3 p in planes)
        {
            _planes.Add(new CollisionPlane(F(p.N), (float)p.D));
        }

        uint cornerStart = (uint)_verts.Count;
        Aabb box = Aabb.Empty;
        if (corners is not null)
        {
            foreach (FVector v in corners.Select(F).Distinct())
            {
                _verts.Add(v);
                box = box.Add(new Vec3(v.X, v.Y, v.Z));
            }
        }

        if (box.IsEmpty)
        {
            box = new Aabb(Vec3.Zero, Vec3.Zero);
        }

        _hulls.Add(new CollisionHull(planeStart, (uint)planes.Count, corners is null ? 0 : cornerStart,
            corners is null ? 0 : (uint)_verts.Count - cornerStart, material, F(box.Min), F(box.Max)));
    }

    // ---- actors -------------------------------------------------------------------------------------------------

    /// <summary>A volume, blocking volume or brush mover: its shape in local space with the actor's transform.</summary>
    private void AddShapeActor(Actor a)
    {
        BrushShape shape = a.Shape!;
        var faces = new List<(Vec3[] Verts, Plane3 Plane, BrushFace Face)>();
        foreach (BrushFace f in shape.Faces)
        {
            if (Poly.Clean(f.Vertices) is { } v && Plane3.TryFromPolygon(v, out Plane3 plane))
            {
                faces.Add((v, plane, f));
            }
        }

        if (faces.Count < 4)
        {
            _ctx.Warn("collision.shape", $"actor {a.Id} has no closed brush shape; it does not collide", ObjectRef.Actor(a.Id));
            return;
        }

        bool mover = _ctx.Rules.IsA(a, "Engine.Mover");
        uint vertStart = (uint)_verts.Count, triStart = (uint)_tris.Count, hullStart = (uint)_hulls.Count;
        ushort collider = (ushort)_colliders.Count;
        Func<Vec3, uint> local = SharedVertices();
        foreach ((Vec3[] verts, Plane3 plane, BrushFace face) in faces)
        {
            ushort material = Material(face.Material, face.Flags, CollisionMaterialFlags.None);
            var poly = new Poly(verts, plane, new Surface { BrushId = a.Id, FaceIndex = 0 });
            AddFan(poly, local, material, collider);
        }

        List<Vec3> all = faces.SelectMany(f => f.Verts).ToList();
        bool convex = faces.All(f => all.All(p => f.Plane.Dist(p) <= 0.05));
        if (convex)
        {
            var planes = new List<Plane3>();
            foreach ((_, Plane3 plane, _) in faces)
            {
                if (!planes.Any(q => q.Coincident(plane)))
                {
                    planes.Add(plane);
                }
            }

            AddHull(planes, all, Material(null, 0, CollisionMaterialFlags.None));
        }
        else
        {
            _ctx.Info("collision.concave", $"actor {a.Id}: the shape is concave, so it collides by its triangles only", ObjectRef.Actor(a.Id));
        }

        Aabb world = Aabb.Of(all.Select(p => _ctx.Rules.LocalToWorld(a, p)));
        AddCollider(mover ? ColliderKind.Mover : ColliderKind.Brush, _ctx.Rules.FlagsOf(a), a.Id, vertStart, triStart, (uint)_bsp.Count,
            hullStart, a.Location, a.Rotation, _ctx.Rules.Scale(a), _ctx.Rules.PrePivot(a), world, a.Id, $"{_ctx.MapId}.{a.Id}Brush", SetBrush);
    }

    private string? MeshPath(Actor a) =>
        PropValues.AssetPath(_ctx.Rules.Value(a, "StaticMesh")) is { } path ? path : null;

    /// <summary>The converter's rule: DT_StaticMesh, a StaticMesh, no brush, no cylinder collision, bCollideActors.</summary>
    private bool IsMeshPrimitive(Actor a) =>
        a.Shape is null && MeshPath(a) is not null && (int)_ctx.Rules.Number(a, "DrawType") == 8 &&
        !_ctx.Rules.Flag(a, "bUseCylinderCollision") && _ctx.Rules.Flag(a, "bCollideActors");

    /// <summary>
    /// The static mesh colliders of an actor, for each mesh it can show (<see cref="MeshVariants"/>): the collision sets
    /// a converted map holds for the mesh (per-polygon, then simple, each with the traces that use it, as the converter
    /// writes them), else one collider of the library mesh's triangles. A mesh the initial StaticMesh does not use starts
    /// disabled; the runtime enables it when the object swaps to it. False when no converted map ships any of them.
    /// </summary>
    private bool AddMeshActor(Actor a, string? actorId)
    {
        ColliderKind kind = _ctx.Rules.IsA(a, "Engine.Mover") ? ColliderKind.Mover
            : _ctx.Rules.IsA(a, "SBase.SVitre") ? ColliderKind.Glass
            : _ctx.Rules.IsA(a, "SBase.SBasicGamePlayObject") && !_ctx.Rules.Flag(a, "bWorldGeometry") ? ColliderKind.Gpo
            : ColliderKind.MeshPoly;
        bool added = false;
        foreach ((ushort variants, string path) in MeshVariants(a))
        {
            ColliderFlags flags = _ctx.Rules.FlagsOf(a) | ((variants & 1) == 0 ? ColliderFlags.StartsDisabled : ColliderFlags.None);
            // A fitted window needs the frame's real hole after shattering, even if its imported hull filled it.
            if (_ctx.WindowFrameSources.Contains(a.Id) && AddLibraryMesh(a, actorId, path, kind, flags, variants, useRenderMesh: true))
            { added = true; continue; }
            if (ConvertedSets(path) is { } sets)
            {
                foreach (MeshSet set in sets)
                {
                    AddConvertedSet(a, actorId, path, kind == ColliderKind.MeshPoly && set.Set == SetSimple ? ColliderKind.MeshHull : kind, set,
                        flags, variants);
                }

                added = true;
            }
            else
            {
                added |= AddLibraryMesh(a, actorId, path, kind, flags, variants);
            }
        }

        return added;
    }

    /// <summary>
    /// The meshes an actor can show, each with its CSRC variant bits, as the converter gathers them: the initial
    /// StaticMesh (bit 0), then for gameplay objects the TriggeredMesh (bit 1) and the StaticMesh of each of the first 14
    /// DamagedMeshes stages that keeps collision (bit 2 + stage). A mesh used by several gets one entry with their bits.
    /// </summary>
    private List<(ushort Variants, string Path)> MeshVariants(Actor a)
    {
        var meshes = new List<(ushort Variants, string Path)>();
        void Add(int bit, string? path)
        {
            if (path is null)
            {
                return;
            }

            int i = meshes.FindIndex(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                meshes.Add(((ushort)bit, path));
            }
            else
            {
                meshes[i] = ((ushort)(meshes[i].Variants | bit), meshes[i].Path);
            }
        }

        Add(1, MeshPath(a));
        if (_ctx.Rules.IsA(a, "SBase.SGamePlayObject"))
        {
            Add(2, PropValues.AssetPath(_ctx.Rules.Value(a, "TriggeredMesh")));
            if (_ctx.Rules.Value(a, "DamagedMeshes") is SvJsonArray stages)
            {
                for (int i = 0; i < Math.Min(stages.Count, 14); i++)
                {
                    if (stages[i] is SvJsonObject stage && stage["bDisableCollision"]?.Kind != SvJsonKind.True)
                    {
                        Add(4 << i, PropValues.AssetPath(stage["StaticMesh"]));
                    }
                }
            }
        }

        return meshes;
    }

    /// <summary>A mesh no converted map has collision for: one collider of the library mesh's (collision) triangles.
    /// False when the library has no such mesh.</summary>
    private bool AddLibraryMesh(Actor a, string? actorId, string path, ColliderKind kind, ColliderFlags flags, ushort variants, bool useRenderMesh = false)
    {
        string cacheKey = useRenderMesh ? path + "|window-frame" : path;
        if (!_meshCache.TryGetValue(cacheKey, out var mesh))
        {
            // A gameplay actor's class mesh (a camera's, a laser trigger's) is in no converted map's library, only in
            // the gameplay pack; its render triangles are the per-polygon set the converter writes for stock devices.
            mesh = (_ctx.Library.FindMesh(path) ?? _ctx.Library.Gameplay.FindMesh(path)) is { } lib && File.Exists(lib.GlbPath)
                ? _ctx.Library.Glb(lib.GlbPath).ReadUeTriangles(useRenderMesh ? lib.Mesh : lib.CollisionMesh ?? lib.Mesh)
                : null;
            _meshCache[cacheKey] = mesh;
        }

        if (mesh is not { } m || m.Indices.Count == 0)
        {
            return false;
        }

        uint vertStart = (uint)_verts.Count, triStart = (uint)_tris.Count;
        ushort collider = (ushort)_colliders.Count;
        foreach (Vec3 v in m.Verts)
        {
            Vertex(v);
        }

        for (int t = 0; t + 2 < m.Indices.Count; t += 3)
        {
            ushort material = Material(m.Materials[t / 3], 0, CollisionMaterialFlags.None);
            uint i0 = vertStart + (uint)m.Indices[t], i1 = vertStart + (uint)m.Indices[t + 1], i2 = vertStart + (uint)m.Indices[t + 2];
            if (i0 != i1 && i1 != i2 && i0 != i2)
            {
                _tris.Add(new CollisionTriangle(i0, i1, i2, collider, material));
            }
        }

        Aabb world = Aabb.Of(m.Verts.Select(p => _ctx.Rules.LocalToWorld(a, p)));
        AddCollider(kind, flags, actorId, vertStart, triStart, (uint)_bsp.Count, (uint)_hulls.Count, a.Location, a.Rotation, _ctx.Rules.Scale(a),
            _ctx.Rules.PrePivot(a), world, a.Id, path, SetPerPoly, variants);
        return true;
    }

    /// <summary>One collision set of a static mesh in a converted map's collision: its collider (mesh-space triangles),
    /// CSRC set and traceUse.</summary>
    private sealed record MeshSet(CollisionData Data, Collider Collider, byte Set, ushort TraceUse);

    /// <summary>The per-polygon and simple sets of a mesh in the baked source map's collision, else in that of the
    /// converted map that ships the mesh; null when neither has a collider of the mesh.</summary>
    private List<MeshSet>? ConvertedSets(string path)
    {
        if (_meshSets.TryGetValue(path, out List<MeshSet>? cached))
        {
            return cached;
        }

        List<MeshSet>? sets = _ctx.Baked is { } baked ? FindSets(baked.Collision, path) : null;
        if (sets is null && _ctx.Library.FindMesh(path) is { } lib && _ctx.Library.MapFolder(lib.Origin) is { } folder)
        {
            if (!_libraryCollision.TryGetValue(folder, out CollisionData? data))
            {
                string file = Path.Combine(folder, CollisionReader.RelativePath);
                try
                {
                    data = File.Exists(file) ? CollisionReader.Load(file, strict: false) : null;
                }
                catch (Exception e) when (e is IOException or SvMapFormatException)
                {
                    data = null;
                }

                _libraryCollision[folder] = data;
            }

            sets = data is null ? null : FindSets(data, path);
        }

        _meshSets[path] = sets;
        return sets;
    }

    /// <summary>The first per-polygon and the first simple collider of the mesh (CSRC asset), per-polygon first, as
    /// the converter orders them. Every collider of one mesh holds the same mesh-space set, whichever variants use it.</summary>
    private static List<MeshSet>? FindSets(CollisionData data, string path)
    {
        if (BakedSource.Chunk(data, "CSRC", data.Colliders.Length, 16) is not { } csrc)
        {
            return null;
        }

        MeshSet? perPoly = null, simple = null;
        for (int i = 0; i < data.Colliders.Length && (perPoly is null || simple is null); i++)
        {
            ReadOnlySpan<byte> row = csrc.AsSpan(i * 16, 16);
            byte set = row[8];
            if ((set != SetPerPoly && set != SetSimple) ||
                !string.Equals(data.GetString(BinaryPrimitives.ReadUInt32LittleEndian(row[4..])), path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var found = new MeshSet(data, data.Colliders[i], set, BinaryPrimitives.ReadUInt16LittleEndian(row[12..]));
            if (set == SetPerPoly)
            {
                perPoly ??= found;
            }
            else
            {
                simple ??= found;
            }
        }

        List<MeshSet> sets = new[] { perPoly, simple }.OfType<MeshSet>().ToList();
        return sets.Count > 0 ? sets : null;
    }

    /// <summary>A converted mesh set for an actor: its vertices, triangles and their materials, with the actor's
    /// transform.</summary>
    private void AddConvertedSet(Actor a, string? actorId, string path, ColliderKind kind, MeshSet set, ColliderFlags flags, ushort variants)
    {
        CollisionData src = set.Data;
        Collider c = set.Collider;
        byte[]? tflx = BakedSource.Chunk(src, "TFLX", src.Materials.Length, 8);
        uint vertStart = (uint)_verts.Count, triStart = (uint)_tris.Count;
        ushort collider = (ushort)_colliders.Count;
        _verts.AddRange(src.Vertices.AsSpan((int)c.VertStart, (int)c.VertCount));
        var materials = new Dictionary<ushort, ushort>();
        foreach (CollisionTriangle t in src.TrianglesOf(c))
        {
            if (!materials.TryGetValue(t.Material, out ushort material))
            {
                CollisionMaterial m = src.Materials[t.Material];
                int row = t.Material * 8;
                material = tflx is null
                    ? Material(src.GetString(m.Name), m.SurfaceType, m.Flags, 0, 0, 0)
                    : Material(src.GetString(m.Name), m.SurfaceType, m.Flags, BinaryPrimitives.ReadUInt32LittleEndian(tflx.AsSpan(row)),
                        BinaryPrimitives.ReadUInt16LittleEndian(tflx.AsSpan(row + 4)), tflx[row + 6]);
                materials[t.Material] = material;
            }

            _tris.Add(new CollisionTriangle(vertStart + t.I0 - c.VertStart, vertStart + t.I1 - c.VertStart, vertStart + t.I2 - c.VertStart,
                collider, material));
        }

        Aabb world = Aabb.Of(Enumerable.Range((int)vertStart, (int)c.VertCount)
            .Select(i => _ctx.Rules.LocalToWorld(a, new Vec3(_verts[i].X, _verts[i].Y, _verts[i].Z))));
        AddCollider(kind, flags, actorId, vertStart, triStart, (uint)_bsp.Count, (uint)_hulls.Count, a.Location, a.Rotation,
            _ctx.Rules.Scale(a), _ctx.Rules.PrePivot(a), world, a.Id, path, set.Set, variants, set.TraceUse);
    }

    private void AddCylinder(Actor a)
    {
        _cylinders.Add(new CollisionCylinder(String(a.Id), _ctx.Rules.FlagsOf(a), F(a.Location), (float)_ctx.Rules.Number(a, "CollisionRadius"),
            (float)_ctx.Rules.Number(a, "CollisionHeight")));
        _cylinderExt.Add((0, CollisionData.None));
    }

    /// <summary>The SInteractObject a gameplay object with an InteractionClass spawns, as a CYLX interaction cylinder.</summary>
    private void AddInteraction(Actor a)
    {
        string? cls = _ctx.Rules.Value(a, "InteractionClass") switch
        {
            SvJsonObject o when o["$class"] is SvJsonString s => s.Value,
            SvJsonObject o when o["$asset"] is SvJsonString s => s.Value,
            SvJsonString s when s.Value.Length > 0 && s.Value != "None" => s.Value,
            _ => null,
        };
        if (cls is null || !_ctx.Rules.IsA(a, "SBase.SBasicGamePlayObject"))
        {
            return;
        }

        double radius = _ctx.Rules.Number(a, "fInteractionRadius", -1);
        double height = _ctx.Rules.Number(a, "fInteractionHeight", -1);
        if (radius < 0)
        {
            radius = _ctx.Rules.Number(a, "CollisionRadius") + 150;
        }

        if (height < 0)
        {
            height = _ctx.Rules.Number(a, "CollisionHeight") + 150;
        }

        _cylinders.Add(new CollisionCylinder(String(a.Id), ColliderFlags.CollideActors, F(a.Location), (float)radius, (float)height));
        _cylinderExt.Add((1, String(cls)));
    }

    // ---- zones --------------------------------------------------------------------------------------------------

    private void AddZones()
    {
        ZoneResult zones = _ctx.World.Zones;
        Aabb world = _ctx.World.Bounds.IsEmpty ? new Aabb(Vec3.Zero, Vec3.Zero) : _ctx.World.Bounds;
        _zones.Add(new CollisionZone(String(_ctx.LevelInfoId), CollisionZoneFlags.None, F(world.Min), F(world.Max)));
        foreach (ZoneEntry z in zones.Zones.Where(z => z.Index > 0).OrderBy(z => z.Index))
        {
            Aabb b = z.Bounds.IsEmpty ? new Aabb(Vec3.Zero, Vec3.Zero) : z.Bounds;
            _zones.Add(new CollisionZone(String(z.ActorId), z.Sky ? CollisionZoneFlags.SkyZone : CollisionZoneFlags.None, F(b.Min), F(b.Max)));
        }
    }

    // ---- baked world --------------------------------------------------------------------------------------------

    /// <summary>
    /// Copies the source map's collision: every table, with each actor's colliders following the project. A collider
    /// whose actor is kept gets the actor's transform; one whose actor is gone, or that no longer fits it (a volume
    /// with an edited shape, a static mesh given another mesh or no longer colliding), is made inert and the actor is
    /// built again from the project. Cylinders are rebuilt from the project.
    /// </summary>
    private void CopyBaked(BakedSource baked, HashSet<string> collided)
    {
        CollisionData src = baked.Collision;
        _strings.Clear();
        _stringIndex.Clear();
        foreach (string s in src.Strings)
        {
            _stringIndex.TryAdd(s, (uint)_strings.Count);
            _strings.Add(s);
        }

        _materials.Clear();
        _materialExt.Clear();
        _materialIndex.Clear();
        byte[]? tflx = baked.Chunk("TFLX", src.Materials.Length, 8);
        for (int i = 0; i < src.Materials.Length; i++)
        {
            _materials.Add(src.Materials[i]);
            _materialExt.Add(tflx is null
                ? (0u, (ushort)0, (byte)0)
                : (BinaryPrimitives.ReadUInt32LittleEndian(tflx.AsSpan(i * 8)), BinaryPrimitives.ReadUInt16LittleEndian(tflx.AsSpan((i * 8) + 4)),
                    tflx[(i * 8) + 6]));
        }

        _verts.AddRange(src.Vertices);
        _tris.AddRange(src.Triangles);
        _planes.AddRange(src.Planes);
        _hulls.AddRange(src.Hulls);
        _bsp.AddRange(src.BspNodes);
        byte[]? csrc = baked.Chunk("CSRC", src.Colliders.Length, 16);
        string?[] sources = BakedSource.ColliderSources(src);
        var gameplay = _ctx.Gameplay.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var render = _ctx.Render.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < src.Colliders.Length; i++)
        {
            Collider c = src.Colliders[i];
            (uint Source, uint Asset, byte Set, ushort Variants, ushort TraceUse) source = csrc is null
                ? (c.Actor, CollisionData.None, (byte)(c.Kind == ColliderKind.WorldBsp ? SetWorld : c.Kind == ColliderKind.Brush ? SetBrush : SetPerPoly), (ushort)0,
                    TraceBoth)
                : (BinaryPrimitives.ReadUInt32LittleEndian(csrc.AsSpan(i * 16)), BinaryPrimitives.ReadUInt32LittleEndian(csrc.AsSpan((i * 16) + 4)),
                    csrc[(i * 16) + 8], BinaryPrimitives.ReadUInt16LittleEndian(csrc.AsSpan((i * 16) + 10)),
                    BinaryPrimitives.ReadUInt16LittleEndian(csrc.AsSpan((i * 16) + 12)));

            // A gameplay actor's collider names it in COLL; a static mesh actor's (render only) only in CSRC.
            string? owner = src.GetString(c.Actor);
            string? meshActor = owner is null && sources[i] is { } id && baked.MeshActors.Contains(id) ? id : null;
            if (owner is not null || meshActor is not null)
            {
                Actor? a = owner is not null ? gameplay.GetValueOrDefault(owner) : render.GetValueOrDefault(meshActor!);
                bool fits = a is not null && !_ctx.WindowFrameSources.Contains(a.Id) && (owner is not null
                    ? c.Kind != ColliderKind.Brush || a.Shape is null || SameShape(src, c, a.Shape)
                    : IsMeshPrimitive(a) && string.Equals(MeshPath(a), src.GetString(source.Asset), StringComparison.OrdinalIgnoreCase));
                if (fits)
                {
                    collided.Add(a!.Id);
                    c = Moved(src, c, a);
                }
                else
                {
                    // Gone, reshaped, given another mesh or no longer colliding: built again from the project if at all.
                    c = c with { Actor = CollisionData.None, Flags = ColliderFlags.None };
                    if (a is not null)
                    {
                        stale.Add(a.Id);
                    }
                }
            }

            _colliders.Add(c);
            _sources.Add(source);
        }

        collided.ExceptWith(stale);
        _zones.AddRange(src.Zones.Select(z => src.GetString(z.Actor) is { } id && !_ctx.GameplayIds.Contains(id) && id != _ctx.LevelInfoId
            ? z with { Actor = CollisionData.None }
            : z));
        if (_zones.Count == 0)
        {
            _zones.Add(new CollisionZone(String(_ctx.LevelInfoId), CollisionZoneFlags.None, default, default));
        }
    }

    /// <summary>True when a brush collider still fits the shape (both in the actor's local space): every corner of the
    /// shape is a corner of the collider, and every corner of the collider lies on a face plane of the shape (it may
    /// keep points on an edge that the shape leaves out).</summary>
    private static bool SameShape(CollisionData src, Collider c, BrushShape shape)
    {
        var corners = new HashSet<uint>();
        foreach (CollisionTriangle t in src.TrianglesOf(c))
        {
            corners.UnionWith([t.I0, t.I1, t.I2]);
        }

        List<Vec3> baked = corners.Select(i => new Vec3(src.Vertices[(int)i].X, src.Vertices[(int)i].Y, src.Vertices[(int)i].Z)).ToList();
        List<Vec3> own = shape.Faces.SelectMany(f => f.Vertices).ToList();
        var planes = new List<Plane3>();
        foreach (BrushFace f in shape.Faces)
        {
            if (Plane3.TryFromPolygon(f.Vertices, out Plane3 plane))
            {
                planes.Add(plane);
            }
        }

        const double Tolerance = 0.1;
        return own.All(p => baked.Any(q => (p - q).Length < Tolerance)) && baked.All(q => planes.Any(pl => Math.Abs(pl.Dist(q)) < Tolerance));
    }

    /// <summary>A source collider placed at the project actor's transform (bounds recomputed from its vertices).</summary>
    private Collider Moved(CollisionData src, Collider c, Actor a)
    {
        var location = F(a.Location);
        FRotator rotation = R(a.Rotation);
        var scale = F(_ctx.Rules.Scale(a));
        var prePivot = F(_ctx.Rules.PrePivot(a));
        static bool Same(FVector p, FVector q, float tolerance) =>
            Math.Abs(p.X - q.X) <= tolerance && Math.Abs(p.Y - q.Y) <= tolerance && Math.Abs(p.Z - q.Z) <= tolerance;

        // Float noise in the project's copy of the transform keeps the source collider as it is.
        if (Same(c.Location, location, 0.001f) && c.Rotation == rotation && Same(c.Scale, scale, 1e-5f) && Same(c.PrePivot, prePivot, 0.001f))
        {
            return c;
        }

        Aabb world = Aabb.Empty;
        foreach (FVector v in src.VerticesOf(c))
        {
            world = world.Add(_ctx.Rules.LocalToWorld(a, new Vec3(v.X, v.Y, v.Z)));
        }

        if (world.IsEmpty)
        {
            world = new Aabb(a.Location, a.Location);
        }

        return c with { Location = location, Rotation = rotation, Scale = scale, PrePivot = prePivot, AabbMin = F(world.Min), AabbMax = F(world.Max) };
    }

    // ---- output -------------------------------------------------------------------------------------------------

    private CollisionData Data() => new()
    {
        Strings = [.. _strings],
        Vertices = [.. _verts],
        Triangles = [.. _tris],
        Materials = [.. _materials],
        Planes = [.. _planes],
        Hulls = [.. _hulls],
        BspNodes = [.. _bsp],
        Colliders = [.. _colliders],
        Cylinders = [.. _cylinders],
        Zones = [.. _zones],
        UnknownChunks = ImmutableArray<CollisionUnknownChunk>.Empty,
    };

    /// <summary>Appends the 1.1 chunks CSRC, TFLX and CYLX (parallel to COLL, TFLG and CYLS) to a 1.0 file.</summary>
    private byte[] WithExtensions(byte[] baseFile)
    {
        var csrc = new byte[_sources.Count * 16];
        for (int i = 0; i < _sources.Count; i++)
        {
            Span<byte> r = csrc.AsSpan(i * 16);
            BinaryPrimitives.WriteUInt32LittleEndian(r, _sources[i].Source);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], _sources[i].Asset);
            r[8] = _sources[i].Set;
            BinaryPrimitives.WriteUInt16LittleEndian(r[10..], _sources[i].Variants);
            BinaryPrimitives.WriteUInt16LittleEndian(r[12..], _sources[i].TraceUse);
        }

        var tflx = new byte[_materialExt.Count * 8];
        for (int i = 0; i < _materialExt.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tflx.AsSpan(i * 8), _materialExt[i].PolyFlags);
            BinaryPrimitives.WriteUInt16LittleEndian(tflx.AsSpan((i * 8) + 4), _materialExt[i].Bits);
            tflx[(i * 8) + 6] = _materialExt[i].Occlusion;
        }

        var cylx = new byte[_cylinderExt.Count * 8];
        for (int i = 0; i < _cylinderExt.Count; i++)
        {
            cylx[i * 8] = _cylinderExt[i].Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(cylx.AsSpan((i * 8) + 4), _cylinderExt[i].Class);
        }

        using var s = new MemoryStream();
        s.Write(baseFile);
        int chunks = 0;
        Span<byte> header = stackalloc byte[16];
        foreach ((string fourcc, byte[] payload, int count) in new[] { ("CSRC", csrc, _sources.Count), ("TFLX", tflx, _materialExt.Count), ("CYLX", cylx, _cylinderExt.Count) })
        {
            header.Clear();
            System.Text.Encoding.ASCII.GetBytes(fourcc, header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)count);
            s.Write(header);
            s.Write(payload);
            while (s.Length % 16 != 0)
            {
                s.WriteByte(0);
            }

            chunks++;
        }

        byte[] result = s.ToArray();
        // collision.md: the header's minor version is 1 when the 1.1 chunks are present.
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), 1);
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(8)) + (uint)chunks;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), total);
        return result;
    }
}
