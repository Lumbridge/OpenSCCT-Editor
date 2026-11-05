using System.Collections.Concurrent;
using OpenScct.Content.Collision;
using OpenScct.Core;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Physics;
using OpenScct.Physics.Collision;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>Where a surface polygon comes from.</summary>
public enum SurfaceSource
{
    /// <summary>The CSG of the project's brushes.</summary>
    Brushes,

    /// <summary>A baked map's world BSP.</summary>
    WorldBsp,

    /// <summary>A baked map's static mesh collision.</summary>
    Mesh,
}

/// <summary>A world-space surface polygon, wound so <see cref="Normal"/> faces into open space.</summary>
public readonly record struct SurfacePoly(Vec3[] Verts, Vec3 Normal, SurfaceSource Source);

/// <summary>A line trace hit.</summary>
public readonly record struct TraceHit(Vec3 Point, Vec3 Normal, double Distance);

/// <summary>
/// The level geometry as the traversal tools see it: OpenSCCT's own collision world (the engine's line and box traces)
/// over the CSG of the project's brushes and, for imported maps, the baked <c>collision.bin</c>. Also keeps the surface
/// polygons the ledge detector walks. Build once per question; the baked collision is cached per file.
/// </summary>
public sealed class TraversalWorld
{
    /// <summary>The ledge clearance traces' flags (0x2DF) without pawns: level, movers, volumes and static meshes.</summary>
    public const ETraceFlags Flags = (ETraceFlags)0x2DE;

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, CollisionData Data, CollisionWorld World)?> s_baked =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(CollisionWorld World, CollisionData Data)> _worlds;

    private TraversalWorld(List<(CollisionWorld World, CollisionData Data)> worlds, List<SurfacePoly> surfaces)
    {
        _worlds = worlds;
        Surfaces = surfaces;
        double top = double.NegativeInfinity;
        foreach (SurfacePoly poly in surfaces)
        {
            foreach (Vec3 v in poly.Verts)
            {
                top = Math.Max(top, v.Z);
            }
        }

        Top = double.IsNegativeInfinity(top) ? null : top;
    }

    /// <summary>Every colliding surface: CSG polygons, the baked world BSP and the static meshes players collide with.</summary>
    public IReadOnlyList<SurfacePoly> Surfaces { get; }

    public bool HasGeometry => _worlds.Count > 0;

    /// <summary>The highest point of any colliding surface (worked out when the world is built), or null when there are
    /// none.</summary>
    public double? Top { get; }

    /// <summary>The world of a project. <paramref name="projectFolder"/> resolves a relative baked source path.</summary>
    public static TraversalWorld Build(MapProject project, string? projectFolder = null)
    {
        project = GeGeometry.ExpandValid(project);
        var worlds = new List<(CollisionWorld, CollisionData)>();
        var surfaces = new List<SurfacePoly>();
        if (project.Baked is { KeepCollision: true } baked && LoadBaked(baked.SourceMap, projectFolder) is { } b)
        {
            worlds.Add((b.World, b.Data));
            BakedSurfaces(b.Data, surfaces);
        }

        if (BakedContent.Collision(project).Brushes.Count > 0 && CsgWorldOf(project, surfaces) is { } csg)
        {
            worlds.Add(csg);
        }

        return new TraversalWorld(worlds, surfaces);
    }

    /// <summary>The first thing a line from <paramref name="from"/> to <paramref name="to"/> hits, at the surface itself
    /// (the engine's trace stops a little short of it: half a unit on level geometry, up to one on meshes).</summary>
    public TraceHit? Trace(Vec3 from, Vec3 to)
    {
        TraceHit? best = null;
        double length = (to - from).Length;
        if (length < 1e-6)
        {
            return null;
        }

        Vec3 dir = (to - from) / length;
        foreach ((CollisionWorld w, CollisionData data) in _worlds)
        {
            if (w.LineCheck(out FCheckResult hit, null, F(to), F(from), default, Flags))
            {
                double d = Math.Min(length, (V(hit.Location) - from).Length + PullBack(data, hit.Collider, length));
                Vec3 p = from + dir * d;
                if (best is null || d < best.Value.Distance)
                {
                    best = new TraceHit(p, V(hit.Normal), d);
                }
            }
        }

        return best;
    }

    /// <summary>True when a box of half size <paramref name="extent"/> swept from one point to the other hits something.</summary>
    public bool BoxBlocked(Vec3 from, Vec3 to, Vec3 extent) =>
        _worlds.Any(w => w.World.LineCheck(out _, null, F(to), F(from), F(extent), Flags));

    /// <summary>True when a box of half size <paramref name="extent"/> at <paramref name="at"/> overlaps something.</summary>
    public bool BoxOverlaps(Vec3 at, Vec3 extent) => _worlds.Any(w => w.World.PointCheck(out _, null, F(at), F(extent), Flags));

    /// <summary>How far short of the surface a line trace of <paramref name="length"/> stops on this collider
    /// (OpenSCCT's CollisionWorld pull-back rules for zero-extent traces).</summary>
    private static double PullBack(CollisionData data, int collider, double length)
    {
        if (collider < 0 || collider >= data.Colliders.Length)
        {
            return K.Pullback.Cylinder * length;
        }

        return data.Colliders[collider].Kind is ColliderKind.WorldBsp or ColliderKind.Brush
            ? K.Pullback.BspLine
            : Math.Min(K.Pullback.Mesh, K.Pullback.MeshCap * length);
    }

    /// <summary>How far the floor is below a point (searching down to <paramref name="max"/>), or null.</summary>
    public double? DropBelow(Vec3 p, double max = 8192) => Trace(p, p - Vec3.UnitZ * max)?.Distance;

    private static FVector F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static Vec3 V(FVector v) => new(v.X, v.Y, v.Z);

    /// <summary>The brushes' CSG as world triangles in a collision world of its own (null when nothing collides).</summary>
    private static (CollisionWorld, CollisionData)? CsgWorldOf(MapProject project, List<SurfacePoly> surfaces)
    {
        BrushLayer layer = BakedContent.Collision(project);
        CsgResult csg = new CsgWorld().Build(layer.Brushes, layer.WorldSolid);
        var builder = new CollisionDataBuilder();
        int triangles = 0;
        foreach (Poly p in csg.Polys)
        {
            if (!p.Surface.Collides || p.Verts.Length < 3)
            {
                continue;
            }

            Vec3 n = p.Plane.N;
            surfaces.Add(new SurfacePoly(p.Verts, n, SurfaceSource.Brushes));
            for (int i = 1; i + 1 < p.Verts.Length; i++)
            {
                Vec3 a = p.Verts[0], b = p.Verts[i], c = p.Verts[i + 1];
                if (Vec3.Dot(Vec3.Cross(b - a, c - a), n) < 0)
                {
                    (b, c) = (c, b);
                }

                builder.AddTriangle(F(a), F(b), F(c));
                triangles++;
            }
        }

        if (triangles == 0)
        {
            return null;
        }

        CollisionData data = builder.Build();
        return (CollisionWorld.Build(data), data);
    }

    /// <summary>Surfaces players stand on in a baked map: the world BSP's triangles and the static mesh colliders that
    /// block players (triangles, or hull faces rebuilt from their corners). Duplicate triangles are dropped.</summary>
    private static void BakedSurfaces(CollisionData data, List<SurfacePoly> surfaces)
    {
        var seen = new HashSet<(VertexKey, VertexKey, VertexKey)>();
        foreach (Collider c in data.Colliders)
        {
            if (c.Kind is not (ColliderKind.WorldBsp or ColliderKind.MeshPoly or ColliderKind.MeshHull) ||
                (c.Flags & ColliderFlags.BlockPlayers) == 0)
            {
                continue;
            }

            Func<FVector, Vec3> xf = Transform(c);
            SurfaceSource source = c.Kind == ColliderKind.WorldBsp ? SurfaceSource.WorldBsp : SurfaceSource.Mesh;
            if (c.HullCount > 0)
            {
                foreach (CollisionHull h in data.HullsOf(c))
                {
                    HullFaces(data, h, xf, surfaces);
                }

                continue;
            }

            foreach (CollisionTriangle t in data.TrianglesOf(c))
            {
                Vec3 a = xf(data.Vertices[(int)t.I0]), b = xf(data.Vertices[(int)t.I1]), d = xf(data.Vertices[(int)t.I2]);
                Vec3 n = Vec3.Cross(b - a, d - a);
                if (n.LengthSquared < 1e-6 || !seen.Add(TriangleKey(a, b, d)))
                {
                    continue;
                }

                surfaces.Add(new SurfacePoly([a, b, d], n.Normalized(), source));
            }
        }
    }

    /// <summary>A hull's faces: for each plane the corners on it, sorted around their centre.</summary>
    private static void HullFaces(CollisionData data, CollisionHull h, Func<FVector, Vec3> xf, List<SurfacePoly> surfaces)
    {
        ReadOnlySpan<FVector> corners = data.CornersOf(h);
        if (corners.Length < 4)
        {
            return;
        }

        var local = new List<(FVector Local, Vec3 World)>();
        foreach (FVector v in corners)
        {
            local.Add((v, xf(v)));
        }

        foreach (CollisionPlane plane in data.PlanesOf(h))
        {
            List<Vec3> on = local.Where(p => Math.Abs(plane.PlaneDot(p.Local)) < 0.5f).Select(p => p.World).ToList();
            if (on.Count < 3)
            {
                continue;
            }

            Vec3 centre = on.Aggregate(Vec3.Zero, (s, v) => s + v) / on.Count;
            Vec3 n = Vec3.Cross(on[1] - on[0], on[2] - on[0]).Normalized();
            Vec3 outward = xf(Add(corners[0], plane.Normal)) - xf(corners[0]);
            if (Vec3.Dot(n, outward) < 0)
            {
                n = -n;
            }

            Vec3 u = (on[0] - centre).Normalized(), w = Vec3.Cross(n, u);
            on.Sort((a, b) => Angle(a - centre, u, w).CompareTo(Angle(b - centre, u, w)));
            surfaces.Add(new SurfacePoly([.. on], n, SurfaceSource.Mesh));
        }
    }

    private static double Angle(Vec3 d, Vec3 u, Vec3 w) => Math.Atan2(Vec3.Dot(d, w), Vec3.Dot(d, u));

    private static FVector Add(FVector a, FVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>A collider's local-to-world transform: ((p - PrePivot) * Scale) rotated, plus Location.</summary>
    private static Func<FVector, Vec3> Transform(Collider c)
    {
        var rot = new Rot(c.Rotation.Pitch, c.Rotation.Yaw, c.Rotation.Roll);
        Vec3 loc = V(c.Location), scale = V(c.Scale), pivot = V(c.PrePivot);
        return c.HasIdentityTransform ? V : p => rot.Rotate((V(p) - pivot).Scale(scale)) + loc;
    }

    /// <summary>A triangle's corners quantised to a tenth of a unit, in a fixed order, so the same triangle stored
    /// twice (or wound the other way) gives the same key.</summary>
    private static (VertexKey, VertexKey, VertexKey) TriangleKey(Vec3 a, Vec3 b, Vec3 c)
    {
        VertexKey[] k = [VertexKey.Of(a), VertexKey.Of(b), VertexKey.Of(c)];
        Array.Sort(k);
        return (k[0], k[1], k[2]);
    }

    private readonly record struct VertexKey(long X, long Y, long Z) : IComparable<VertexKey>
    {
        public static VertexKey Of(Vec3 v) => new((long)Math.Round(v.X * 10), (long)Math.Round(v.Y * 10), (long)Math.Round(v.Z * 10));

        public int CompareTo(VertexKey o) => X != o.X ? X.CompareTo(o.X) : Y != o.Y ? Y.CompareTo(o.Y) : Z.CompareTo(o.Z);
    }

    private static (DateTime Stamp, CollisionData Data, CollisionWorld World)? LoadBaked(string sourceMap, string? projectFolder)
    {
        if (string.IsNullOrEmpty(sourceMap))
        {
            return null;
        }

        string folder = Path.IsPathRooted(sourceMap) || projectFolder is null ? sourceMap : Path.Combine(projectFolder, sourceMap);
        string file = Path.GetFullPath(Path.Combine(folder, "sim", "collision.bin"));
        if (!File.Exists(file))
        {
            return null;
        }

        DateTime stamp = File.GetLastWriteTimeUtc(file);
        if (s_baked.TryGetValue(file, out var cached) && cached is { } c && c.Stamp == stamp)
        {
            return c;
        }

        try
        {
            CollisionData data = CollisionReader.Load(file, strict: false);
            var entry = (stamp, data, CollisionWorld.Build(data));
            s_baked[file] = entry;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OpenScct.Content.SvMapFormatException)
        {
            return null;
        }
    }
}
