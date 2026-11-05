using OpenScct.Content.Collision;
using OpenScct.Core;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Physics.Collision;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>A ray hit on the map's geometry.</summary>
public sealed record RayHit(Vec3 Point, Vec3 Normal, double Distance, string? BrushId, int Face, bool StartSolid)
{
    /// <summary>Where the hit came from: a brush id, or "baked" for an imported map's collision.</summary>
    public string Source => BrushId ?? "baked";
}

/// <summary>One brush as world-space planes (outward normals) for ray and point tests.</summary>
internal sealed class ConvexBrush
{
    private const double Eps = 0.01;

    public ConvexBrush(Brush brush)
    {
        Id = brush.Id;
        Adds = brush.Oper == CsgOper.Add;
        foreach (BrushFace f in brush.WorldFaces())
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            Vec3 n = f.Normal();
            if (n == Vec3.Zero)
            {
                continue;
            }

            // Average the vertices so a slightly non-planar face still gives a stable plane.
            Vec3 c = Vec3.Zero;
            foreach (Vec3 v in f.Vertices)
            {
                c += v;
            }

            c /= f.Vertices.Count;
            Planes.Add((n, Vec3.Dot(n, c)));
            foreach (Vec3 v in f.Vertices)
            {
                Min = Vec3.Min(Min, v);
                Max = Vec3.Max(Max, v);
            }
        }
    }

    public string Id { get; }

    /// <summary>True for an additive (solid) brush, false for a subtractive (carving) one.</summary>
    public bool Adds { get; }

    public List<(Vec3 N, double D)> Planes { get; } = new();

    public Vec3 Min { get; private set; } = new(double.MaxValue, double.MaxValue, double.MaxValue);

    public Vec3 Max { get; private set; } = new(double.MinValue, double.MinValue, double.MinValue);

    public bool IsValid => Planes.Count >= 4;

    public bool Contains(Vec3 p)
    {
        if (p.X < Min.X - Eps || p.Y < Min.Y - Eps || p.Z < Min.Z - Eps || p.X > Max.X + Eps || p.Y > Max.Y + Eps || p.Z > Max.Z + Eps)
        {
            return false;
        }

        foreach ((Vec3 n, double d) in Planes)
        {
            if (Vec3.Dot(n, p) - d > Eps)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Clips the ray <c>o + t*d</c> (t in [0, tMax]) against the brush (Cyrus-Beck): the entry and exit times
    /// and the planes they cross, or false when the ray misses.</summary>
    public bool Clip(Vec3 o, Vec3 d, double tMax, out double tIn, out int inPlane, out double tOut, out int outPlane)
    {
        tIn = double.NegativeInfinity;
        tOut = double.PositiveInfinity;
        inPlane = outPlane = -1;
        for (int i = 0; i < Planes.Count; i++)
        {
            (Vec3 n, double dist) = Planes[i];
            double denom = Vec3.Dot(n, d);
            double num = dist - Vec3.Dot(n, o);
            if (Math.Abs(denom) < 1e-12)
            {
                if (num < -Eps)
                {
                    return false; // parallel and outside
                }

                continue;
            }

            double t = num / denom;
            if (denom < 0)
            {
                if (t > tIn)
                {
                    tIn = t;
                    inPlane = i;
                }
            }
            else if (t < tOut)
            {
                tOut = t;
                outPlane = i;
            }
        }

        return tIn <= tOut && tOut >= 0 && tIn <= tMax;
    }
}

/// <summary>
/// The level geometry as the placement and query code sees it: the project's CSG brushes in order (UE2 rules: the
/// world starts solid, subtract carves, add fills, the last brush containing a point wins; non-solid brushes are
/// ignored) plus, for imported maps, the baked world's collision. Rays find the first empty-to-solid crossing.
/// </summary>
public sealed class WorldGeometry
{
    private static readonly Dictionary<string, CollisionWorld?> s_bakedCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_bakedLock = new();

    /// <summary>Held around every query on a cached baked collision world: the world keeps query state, and the same
    /// instance serves a worker's Map Check and the main thread's picking and neighbour snap.</summary>
    public static readonly object BakedQueryLock = new();

    private readonly List<ConvexBrush> _brushes;
    private readonly CollisionWorld? _baked;
    private readonly bool _startsSolid;

    public WorldGeometry(MapProject project, string? projectFolder, string? exclude = null)
    {
        BrushLayer layer = BakedContent.Collision(project);
        _brushes = layer.Brushes
            .Where(b => !b.IsNotSolid && !b.IsPortal && (b.PolyFlags & PolyFlags.AntiPortal) == 0)
            .Where(b => exclude is null || !string.Equals(b.Id, exclude, StringComparison.OrdinalIgnoreCase))
            .Select(b => new ConvexBrush(b))
            .Where(b => b.IsValid)
            .ToList();
        _baked = project.Baked is { KeepCollision: true } baked ? LoadBaked(baked.SourceMap, projectFolder) : null;

        // A map with baked collision is already carved, so its new brushes only add (and carve what they added). Without
        // it the brushes build the whole world, solid outside, as the compile builds it (a converted map with no brushes
        // has no world then, rather than a solid one).
        _startsSolid = layer.WorldSolid && (project.Baked is null || _brushes.Count > 0);
    }

    /// <summary>The number of brushes the rays hit (a kept baked world's own brushes are in it, not here).</summary>
    public int BrushCount => _brushes.Count;

    /// <summary>True when the map has any geometry to hit.</summary>
    public bool HasGeometry => _brushes.Count > 0 || _baked is not null;

    public bool HasBaked => _baked is not null;

    /// <summary>The imported map's baked collision these rays use, or null.</summary>
    public CollisionWorld? BakedWorld => _baked;

    /// <summary>A map's baked collision when it has already been loaded (by a query or <see cref="LoadBakedCollision"/>),
    /// else null: never reads the file, so a drag can ask on every step.</summary>
    public static CollisionWorld? CachedBakedCollision(MapProject project, string? projectFolder)
    {
        if (project.Baked is not { KeepCollision: true } baked || BakedFile(baked.SourceMap, projectFolder) is not { } file)
        {
            return null;
        }

        // A worker reading the file holds the lock: answer "not yet" rather than wait for it.
        if (!Monitor.TryEnter(s_bakedLock))
        {
            return null;
        }

        try
        {
            return s_bakedCache.GetValueOrDefault(file);
        }
        finally
        {
            Monitor.Exit(s_bakedLock);
        }
    }

    /// <summary>True when the map keeps a baked collision layer whose file has not been read yet.</summary>
    public static bool BakedCollisionPending(MapProject project, string? projectFolder)
    {
        if (project.Baked is not { KeepCollision: true } baked || BakedFile(baked.SourceMap, projectFolder) is not { } file)
        {
            return false;
        }

        if (!Monitor.TryEnter(s_bakedLock))
        {
            return true;
        }

        try
        {
            return !s_bakedCache.ContainsKey(file);
        }
        finally
        {
            Monitor.Exit(s_bakedLock);
        }
    }

    /// <summary>Reads (once) and returns a map's baked collision, or null when it has none. Safe on a worker thread.</summary>
    public static CollisionWorld? LoadBakedCollision(MapProject project, string? projectFolder) =>
        project.Baked is { KeepCollision: true } baked ? LoadBaked(baked.SourceMap, projectFolder) : null;

    /// <summary>True when the point is inside solid CSG (baked geometry is not classified).</summary>
    public bool IsSolid(Vec3 p)
    {
        bool solid = _startsSolid;
        foreach (ConvexBrush b in _brushes)
        {
            if (b.Contains(p))
            {
                solid = b.Adds;
            }
        }

        return solid;
    }

    /// <summary>The brushes containing a point, in CSG order.</summary>
    public IEnumerable<string> BrushesAt(Vec3 p) => _brushes.Where(b => b.Contains(p)).Select(b => b.Id);

    /// <summary>The first surface the ray from <paramref name="from"/> along <paramref name="dir"/> meets, within
    /// <paramref name="maxDistance"/>. A ray that starts in solid first leaves it, then reports the next surface.</summary>
    public RayHit? Raycast(Vec3 from, Vec3 dir, double maxDistance = 65536)
    {
        Vec3 d = dir.Normalized();
        if (d == Vec3.Zero)
        {
            return null;
        }

        RayHit? csg = CsgRay(from, d, maxDistance);
        RayHit? baked = BakedRay(from, d, csg?.Distance ?? maxDistance);
        return baked is not null && (csg is null || baked.Distance < csg.Distance) ? baked : csg;
    }

    private RayHit? CsgRay(Vec3 o, Vec3 d, double tMax)
    {
        // Solidity only changes where the ray crosses a brush boundary, so test each stretch between crossings. The
        // start is judged from just behind it, so a ray from a point on a floor (or a crate's top) hits it at 0.
        const double Behind = 0.05;
        var cuts = new List<(double T, ConvexBrush Brush, int Plane, bool Enter)>();
        foreach (ConvexBrush b in _brushes)
        {
            if (!b.Clip(o, d, tMax, out double tIn, out int pIn, out double tOut, out int pOut))
            {
                continue;
            }

            if (tIn >= -Behind && tIn <= tMax && pIn >= 0)
            {
                cuts.Add((Math.Max(tIn, 0), b, pIn, true));
            }

            if (tOut >= -Behind && tOut <= tMax && pOut >= 0)
            {
                cuts.Add((Math.Max(tOut, 0), b, pOut, false));
            }
        }

        bool startSolid = IsSolid(o - d * Behind);
        cuts.Sort((a, b) => a.T.CompareTo(b.T));
        bool solid = startSolid;
        bool leftSolid = !startSolid;
        for (int i = 0; i < cuts.Count;)
        {
            int j = i;
            while (j + 1 < cuts.Count && cuts[j + 1].T - cuts[i].T < 1e-6)
            {
                j++;
            }

            double t = cuts[i].T;
            double next = j + 1 < cuts.Count ? cuts[j + 1].T : Math.Min(tMax, t + 1);
            bool after = IsSolid(o + d * ((t + Math.Max(next, t + 1e-3)) / 2));
            if (!solid && after && leftSolid)
            {
                // The surface hit: entering solid (an add brush) or leaving a carved space (a subtract brush).
                (double _, ConvexBrush brush, int plane, bool _) = cuts.Skip(i).Take(j - i + 1)
                    .Select(c => (c.T, c.Brush, c.Plane, c.Enter))
                    .FirstOrDefault(c => c.Enter == c.Brush.Adds, (cuts[j].T, cuts[j].Brush, cuts[j].Plane, cuts[j].Enter));
                (Vec3 n, double _) = brush.Planes[plane];
                if (Vec3.Dot(n, d) > 0)
                {
                    n = -n; // the surface faces back at the ray
                }

                return new RayHit(o + d * t, n, t, brush.Id, plane, startSolid);
            }

            if (!after)
            {
                leftSolid = true;
            }

            solid = after;
            i = j + 1;
        }

        return null;
    }

    private RayHit? BakedRay(Vec3 o, Vec3 d, double tMax)
    {
        if (_baked is null)
        {
            return null;
        }

        Vec3 end = o + d * tMax;
        var start = new FVector((float)o.X, (float)o.Y, (float)o.Z);
        var stop = new FVector((float)end.X, (float)end.Y, (float)end.Z);
        FCheckResult hit;
        lock (BakedQueryLock)
        {
            if (!_baked.LineCheck(out hit, null, stop, start, new FVector(0, 0, 0), ETraceFlags.TRACE_World))
            {
                return null;
            }
        }

        var p = new Vec3(hit.Location.X, hit.Location.Y, hit.Location.Z);
        return new RayHit(p, new Vec3(hit.Normal.X, hit.Normal.Y, hit.Normal.Z), (p - o).Length, null, hit.Item, false);
    }

    private static string? BakedFile(string sourceMap, string? projectFolder)
    {
        if (string.IsNullOrEmpty(sourceMap))
        {
            return null;
        }

        string folder = Path.IsPathRooted(sourceMap) || projectFolder is null ? sourceMap : Path.Combine(projectFolder, sourceMap);
        return Path.GetFullPath(Path.Combine(folder, "sim", "collision.bin"));
    }

    private static CollisionWorld? LoadBaked(string sourceMap, string? projectFolder)
    {
        if (BakedFile(sourceMap, projectFolder) is not { } file)
        {
            return null;
        }

        lock (s_bakedLock)
        {
            if (s_bakedCache.TryGetValue(file, out CollisionWorld? cached))
            {
                return cached;
            }

            CollisionWorld? world = null;
            try
            {
                if (File.Exists(file))
                {
                    world = CollisionWorld.Build(CollisionReader.Load(file, strict: false));
                }
            }
            catch (Exception ex) when (ex is IOException or OpenScct.Content.SvMapFormatException or InvalidDataException)
            {
                world = null;
            }

            s_bakedCache[file] = world;
            return world;
        }
    }
}
