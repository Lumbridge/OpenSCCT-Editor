using OpenScct.Content.Collision;
using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// The camera the strategic map and the briefing plan are drawn through (<c>LevelInfo.SnapshotCamera</c>): the game
/// projects a point with the camera's axes and <c>tan(FOV/2)</c>, 0..1 across and down the picture.
/// </summary>
public sealed record SnapshotCamera(Vec3 Position, Vec3 Forward, Vec3 Right, Vec3 Up, int Fov, double TargetDistance)
{
    /// <summary>The picture point of <paramref name="p"/>, as OpenSCCT's strategic map computes it, or null behind the camera.</summary>
    public (double U, double V)? Project(Vec3 p)
    {
        Vec3 d = p - Position;
        double depth = Vec3.Dot(d, Forward);
        if (depth < 1)
        {
            return null;
        }

        double half = depth * Math.Tan(Fov * Math.PI / 360.0);
        return (0.5 + 0.5 * Vec3.Dot(d, Right) / half, 0.5 - 0.5 * Vec3.Dot(d, Up) / half);
    }

    /// <summary>The LevelInfo property. The stored rows hold the camera's axes in their columns (forward, right, up).</summary>
    public SvJsonObject ToProp()
    {
        var o = new SvJsonObject();
        o.Add("FOV", SvJsonNode.Integer(Fov));
        o.Add("Position", PropValues.Vector(Position));
        o.Add("RotationRow0", PropValues.Vector(new Vec3(Forward.X, Right.X, Up.X)));
        o.Add("RotationRow1", PropValues.Vector(new Vec3(Forward.Y, Right.Y, Up.Y)));
        o.Add("RotationRow2", PropValues.Vector(new Vec3(Forward.Z, Right.Z, Up.Z)));
        o.Add("TargetDistance", SvJsonNode.Float((float)TargetDistance));
        return o;
    }

    /// <summary>Reads the LevelInfo property, or null when it is missing or degenerate.</summary>
    public static SnapshotCamera? FromProp(SvJsonNode? node)
    {
        if (node is not SvJsonObject o || !PropValues.TryVector(o["Position"], out Vec3 pos) || !PropValues.TryVector(o["RotationRow0"], out Vec3 r0)
            || !PropValues.TryVector(o["RotationRow1"], out Vec3 r1) || !PropValues.TryVector(o["RotationRow2"], out Vec3 r2))
        {
            return null;
        }

        int fov = (int)PropValues.Num(o["FOV"]);
        var forward = new Vec3(r0.X, r1.X, r2.X);
        return fov is > 0 and < 180 && forward.LengthSquared > 0.5
            ? new SnapshotCamera(pos, forward, new Vec3(r0.Y, r1.Y, r2.Y), new Vec3(r0.Z, r1.Z, r2.Z), fov, PropValues.Num(o["TargetDistance"]))
            : null;
    }
}

/// <summary>The strategic map of one compile: floor heights, the snapshot camera and the walkable and wall triangles
/// the pictures are drawn from.</summary>
public sealed class StrategicMap
{
    internal StrategicMap(IReadOnlyList<double> floors, SnapshotCamera? camera, bool generated, List<Tri> walkable, List<Tri> walls)
    {
        Floors = floors;
        Camera = camera;
        Generated = generated;
        Walkable = walkable;
        Walls = walls;
    }

    /// <summary>Each floor's <c>FloorZ</c>, rising (floor 0's value is never read by the game).</summary>
    public IReadOnlyList<double> Floors { get; }

    public SnapshotCamera? Camera { get; }

    /// <summary>True when this compile writes the floors, the camera and the floor pictures; false when the LevelInfo
    /// already had them (an imported map), which are then kept.</summary>
    public bool Generated { get; }

    internal List<Tri> Walkable { get; }

    internal List<Tri> Walls { get; }

    /// <summary>The floor a height is on, as the game picks it: the highest floor whose FloorZ is at or below it.</summary>
    public int FloorAt(double z)
    {
        int floor = 0;
        for (int i = 1; i < Floors.Count; i++)
        {
            if (z >= Floors[i])
            {
                floor = i;
            }
        }

        return floor;
    }

    internal readonly record struct Tri(Vec3 A, Vec3 B, Vec3 C)
    {
        public Vec3 Centroid => (A + B + C) / 3;
    }
}

/// <summary>
/// The strategic map and the briefing plan, drawn on the CPU from the compiled collision so <c>ose build</c> makes
/// them anywhere: floors found from the heights of the walkable surfaces, the stock camera orientation (looking
/// straight down, +X right, +Y down the picture) framed on the gameplay area, and pictures in the stock palette.
/// </summary>
public static class StrategicMapBuild
{
    public const string PictureFolder = "ui/strategicmap/";
    public const int PictureSize = 1024;

    /// <summary>Walkable: the game walks floors whose normal Z is at least 0.7.</summary>
    private const double WalkableNormalZ = 0.7;

    /// <summary>A new floor starts where at least this much height separates two large walkable areas.</summary>
    private const double FloorGap = 200;

    /// <summary>Heights are grouped in bins of this size; a bin with less than <see cref="MinFloorShare"/> of the walkable
    /// area (tables, stairs, ramps) never starts a floor.</summary>
    private const double Bin = 16;

    private const double MinFloorShare = 0.03;

    private const int MaxFloors = 8;

    /// <summary>FloorZ sits this far under a floor's lowest walkable surface, so a body standing on it is on that floor.</summary>
    private const double FloorBelowSurface = 32;

    /// <summary>Walls are drawn where they cross the height of a body's chest above each floor.</summary>
    private const double WallCut = FloorBelowSurface + 100;

    /// <summary>A narrow field of view, so every floor is drawn at nearly the same scale.</summary>
    private const int CameraFov = 15;

    // The stock floor pictures' palette: background, floors below, this floor, walls.
    private static readonly byte[] s_background = [24, 34, 18, 255], s_below = [88, 110, 80, 255], s_floor = [132, 160, 120, 255], s_wall = [214, 226, 204, 255];

    /// <summary>
    /// Works out the floors and the camera after the collision is built: from the identity's floor list, else the
    /// LevelInfo's own <c>MapFloors</c> and <c>SnapshotCamera</c> when it has them, else from the walkable surfaces.
    /// Sets <see cref="CompileContext.StrategicMap"/>; an empty floor list means no strategic map.
    /// </summary>
    public static void Plan(CompileContext ctx)
    {
        List<double>? chosen = ctx.Project.Identity.Floors;
        if (chosen is { Count: 0 } || !ctx.Files.TryGetValue(CollisionReader.RelativePath, out byte[]? bytes))
        {
            return;
        }

        (List<StrategicMap.Tri> walkable, List<StrategicMap.Tri> walls) = Triangles(ctx, CollisionReader.Read(bytes, strict: false));
        if (chosen is null && ctx.Project.LevelInfo["MapFloors"] is SvJsonArray kept)
        {
            List<double> floors = kept.OfType<SvJsonObject>().Select(f => PropValues.Num(f["FloorZ"])).ToList();
            ctx.StrategicMap = new StrategicMap(floors, SnapshotCamera.FromProp(ctx.Project.LevelInfo["SnapshotCamera"]), false, walkable, walls);
            return;
        }

        if (walkable.Count == 0)
        {
            ctx.Warn("identity.floors", "no walkable surface found: the map gets no strategic map");
            return;
        }

        (double minX, double minY, double maxX, double maxY) = Frame(ctx, walkable);
        walkable.RemoveAll(t => t.Centroid.X < minX || t.Centroid.X > maxX || t.Centroid.Y < minY || t.Centroid.Y > maxY);
        List<double> heights = chosen is null ? FindFloors(walkable) : chosen.Order().ToList();
        if (heights.Count == 0)
        {
            ctx.Warn("identity.floors", "no floor found in the gameplay area: the map gets no strategic map");
            return;
        }

        double top = walkable.Count > 0 ? walkable.Max(t => t.Centroid.Z) : heights[^1] + FloorBelowSurface;
        double half = Math.Max(maxX - minX, maxY - minY) / 2;
        double distance = half / Math.Tan(CameraFov * Math.PI / 360.0);
        var camera = new SnapshotCamera(new Vec3((minX + maxX) / 2, (minY + maxY) / 2, top + distance), new Vec3(0, 0, -1), Vec3.UnitX, new Vec3(0, -1, 0),
            CameraFov, distance);
        ctx.StrategicMap = new StrategicMap(heights, camera, true, walkable, walls);
        ctx.Count("strategicmap.floors", heights.Count);
    }

    /// <summary>The LevelInfo properties a generated strategic map adds (<c>MapFloors</c>, <c>SnapshotCamera</c>).</summary>
    public static void AddLevelInfoProps(CompileContext ctx, PropSet props)
    {
        if (ctx.StrategicMap is not { Generated: true } map)
        {
            return;
        }

        props.Set("MapFloors", new SvJsonArray(map.Floors.Select(z =>
        {
            var f = new SvJsonObject();
            f.Add("FloorZ", SvJsonNode.Float((float)z));
            return (SvJsonNode)f;
        })));
        if (map.Camera is not null)
        {
            props.Set("SnapshotCamera", map.Camera.ToProp());
        }
    }

    /// <summary>Writes <c>ui/strategicmap/floor&lt;n&gt;.png</c> for a generated strategic map, replacing any a baked map brought.</summary>
    public static void WritePictures(CompileContext ctx)
    {
        if (ctx.StrategicMap is not { Generated: true, Camera: not null } map)
        {
            return;
        }

        foreach (string stale in ctx.Files.Keys.Where(k => k.StartsWith(PictureFolder, StringComparison.Ordinal)).ToList())
        {
            ctx.Files.Remove(stale);
        }

        for (int i = 0; i < map.Floors.Count; i++)
        {
            ctx.Progress($"strategic map floor {i}");
            ctx.Files[$"{PictureFolder}floor{i}.png"] = Draw(map, i).EncodePng();
        }
    }

    /// <summary>
    /// Draws one floor (lower floors darker, walls crossing its chest height light) or, with no floor, the briefing plan,
    /// through the snapshot camera at <see cref="PictureSize"/>. The plan stacks the floors from the bottom up, each
    /// with its own walls and a lighter shade than the one under it, so an upper storey covers what lies beneath it
    /// instead of every floor's walls crossing in one colour. Drawn at twice the size and averaged down, so edges are
    /// smooth.
    /// </summary>
    public static RgbaImage Draw(StrategicMap map, int? floor)
    {
        SnapshotCamera camera = map.Camera ?? throw new InvalidOperationException("the strategic map has no camera");
        const int size = PictureSize * 2;
        var canvas = new RgbaImage(size, size);
        for (int i = 0; i < canvas.Pixels.Length; i += 4)
        {
            s_background.CopyTo(canvas.Pixels, i);
        }

        ILookup<int, StrategicMap.Tri> byFloor = map.Walkable.ToLookup(t => map.FloorAt(t.Centroid.Z));
        int count = map.Floors.Count;
        int last = floor ?? count - 1;
        for (int f = 0; f <= last; f++)
        {
            // One floor: the floors under it dark, its own light. The plan: every floor a step lighter than the one below.
            byte[] fill = floor is { } shown ? (f == shown ? s_floor : s_below) : Shade(s_below, s_floor, count == 1 ? 1 : (double)f / (count - 1));
            foreach (StrategicMap.Tri t in byFloor[f].OrderBy(t => t.Centroid.Z))
            {
                FillTriangle(canvas, camera, t, fill);
            }

            if (floor is null || f == floor)
            {
                byte[] wall = floor is null ? Shade(s_below, s_wall, count == 1 ? 1 : 0.5 + 0.5 * f / (count - 1)) : s_wall;
                double z = map.Floors[f] + WallCut;
                foreach (StrategicMap.Tri t in map.Walls)
                {
                    if (Cut(t, z) is { } segment)
                    {
                        DrawLine(canvas, camera, segment.From, segment.To, wall);
                    }
                }
            }
        }

        return canvas.FitWithin(PictureSize);
    }

    /// <summary>A colour <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static byte[] Shade(byte[] from, byte[] to, double t) =>
        [.. from.Zip(to, (a, b) => (byte)Math.Round(a + (b - a) * t))];

    /// <summary>Walkable and wall triangles in world space from the colliders that block players and stay put (the world,
    /// brushes and static meshes; movers, glass and gameplay objects move or break).</summary>
    private static (List<StrategicMap.Tri> Walkable, List<StrategicMap.Tri> Walls) Triangles(CompileContext ctx, CollisionData data)
    {
        var walkable = new List<StrategicMap.Tri>();
        var walls = new List<StrategicMap.Tri>();
        foreach (Collider c in data.Colliders)
        {
            if (c.Kind is not (ColliderKind.WorldBsp or ColliderKind.Brush or ColliderKind.MeshPoly)
                || (c.Flags & ColliderFlags.BlockPlayers) == 0 || (c.Flags & ColliderFlags.StartsDisabled) != 0)
            {
                continue;
            }

            ReadOnlySpan<FVector> verts = data.Vertices.AsSpan();
            foreach (CollisionTriangle t in data.TrianglesOf(c))
            {
                Vec3 a = ToWorld(c, verts[(int)t.I0]), b = ToWorld(c, verts[(int)t.I1]), d = ToWorld(c, verts[(int)t.I2]);
                Vec3 n = Vec3.Cross(b - a, d - a).Normalized();
                // Sky rooms are scenery, even when directly above the playable area. The XY flood below cannot
                // distinguish them. Sample just in front of the surface, on the empty side of a BSP boundary.
                if (ctx.InSkyZone(((a + b + d) / 3) + (n * 2)))
                {
                    continue;
                }

                if (n.Z >= WalkableNormalZ)
                {
                    walkable.Add(new StrategicMap.Tri(a, b, d));
                }
                else if (Math.Abs(n.Z) < 0.3)
                {
                    walls.Add(new StrategicMap.Tri(a, b, d));
                }
            }
        }

        return (walkable, walls);
    }

    /// <summary>A collider vertex in world space (the actor transform: pre-pivot, scale, rotation, location).</summary>
    private static Vec3 ToWorld(in Collider c, FVector v)
    {
        var p = new Vec3(v.X, v.Y, v.Z);
        var rot = new Rot(c.Rotation.Pitch, c.Rotation.Yaw, c.Rotation.Roll);
        return rot.Rotate((p - new Vec3(c.PrePivot.X, c.PrePivot.Y, c.PrePivot.Z)).Scale(new Vec3(c.Scale.X, c.Scale.Y, c.Scale.Z)))
            + new Vec3(c.Location.X, c.Location.Y, c.Location.Z);
    }

    /// <summary>
    /// The picture area: the walkable surfaces reachable from the player starts (a flood over a coarse grid of the
    /// walkable area seen from above, crossing gaps up to two cells), so skyboxes, backdrops and far-off cinematics stay
    /// out, plus a margin. Without starts the navigation points seed it, else every gameplay actor, else everything.
    /// </summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) Frame(CompileContext ctx, List<StrategicMap.Tri> walkable)
    {
        const double cell = 256;
        const int gap = 2;
        static (long X, long Y) CellOf(double x, double y) => ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));
        var occupied = new HashSet<(long X, long Y)>();
        foreach (StrategicMap.Tri t in walkable)
        {
            (long x0, long y0) = CellOf(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)), Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)));
            (long x1, long y1) = CellOf(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)), Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
            for (long x = x0; x <= x1; x++)
            {
                for (long y = y0; y <= y1; y++)
                {
                    occupied.Add((x, y));
                }
            }
        }

        List<Vec3> seeds = ctx.Gameplay.Where(a => ctx.Rules.IsA(a, "Engine.PlayerStart")).Select(a => a.Location).ToList();
        if (seeds.Count == 0)
        {
            seeds = ctx.Gameplay.Where(a => ctx.Rules.IsA(a, "Engine.NavigationPoint")).Select(a => a.Location).ToList();
        }

        if (seeds.Count == 0)
        {
            seeds = ctx.Gameplay.Select(a => a.Location).ToList();
        }

        var reached = new HashSet<(long X, long Y)>();
        var queue = new Queue<(long X, long Y)>();
        foreach (Vec3 seed in seeds)
        {
            (long sx, long sy) = CellOf(seed.X, seed.Y);
            // A start stands near its floor, maybe just off the walkable cells: begin from the nearest ones around it.
            for (long dx = -gap; dx <= gap; dx++)
            {
                for (long dy = -gap; dy <= gap; dy++)
                {
                    if (occupied.Contains((sx + dx, sy + dy)) && reached.Add((sx + dx, sy + dy)))
                    {
                        queue.Enqueue((sx + dx, sy + dy));
                    }
                }
            }
        }

        if (reached.Count == 0)
        {
            reached = occupied;
        }

        while (queue.TryDequeue(out (long X, long Y) c))
        {
            for (long dx = -gap; dx <= gap; dx++)
            {
                for (long dy = -gap; dy <= gap; dy++)
                {
                    if (occupied.Contains((c.X + dx, c.Y + dy)) && reached.Add((c.X + dx, c.Y + dy)))
                    {
                        queue.Enqueue((c.X + dx, c.Y + dy));
                    }
                }
            }
        }

        double minX = reached.Min(c => c.X) * cell, maxX = (reached.Max(c => c.X) + 1) * cell;
        double minY = reached.Min(c => c.Y) * cell, maxY = (reached.Max(c => c.Y) + 1) * cell;
        double margin = Math.Max(cell / 2, Math.Max(maxX - minX, maxY - minY) * 0.03);
        return (minX - margin, minY - margin, maxX + margin, maxY + margin);
    }

    /// <summary>Floors from the walkable area by height: bins holding a fair share of the area, grouped where the gap
    /// between them is at least <see cref="FloorGap"/>.</summary>
    private static List<double> FindFloors(List<StrategicMap.Tri> walkable)
    {
        var area = new SortedDictionary<long, double>();
        double total = 0;
        foreach (StrategicMap.Tri t in walkable)
        {
            double a = Vec3.Cross(t.B - t.A, t.C - t.A).Length / 2;
            long bin = (long)Math.Floor(t.Centroid.Z / Bin);
            area[bin] = area.GetValueOrDefault(bin) + a;
            total += a;
        }

        var floors = new List<double>();
        double last = double.NegativeInfinity;
        foreach ((long bin, double a) in area)
        {
            double z = bin * Bin;
            if (a < total * MinFloorShare)
            {
                continue;
            }

            if (z - last >= FloorGap)
            {
                floors.Add(z - FloorBelowSurface);
            }

            last = z;
        }

        return floors.Count > MaxFloors ? floors.Take(MaxFloors).ToList() : floors;
    }

    /// <summary>Where a wall triangle crosses height <paramref name="z"/>, or null.</summary>
    private static (Vec3 From, Vec3 To)? Cut(StrategicMap.Tri t, double z)
    {
        Span<Vec3> hits = stackalloc Vec3[3];
        int n = 0;
        Vec3[] v = [t.A, t.B, t.C];
        for (int i = 0; i < 3 && n < 2; i++)
        {
            Vec3 p = v[i], q = v[(i + 1) % 3];
            if ((p.Z - z) * (q.Z - z) < 0)
            {
                hits[n++] = Vec3.Lerp(p, q, (z - p.Z) / (q.Z - p.Z));
            }
        }

        return n == 2 ? (hits[0], hits[1]) : null;
    }

    private static void FillTriangle(RgbaImage canvas, SnapshotCamera camera, StrategicMap.Tri t, byte[] colour)
    {
        if (Pixel(canvas, camera, t.A) is not { } a || Pixel(canvas, camera, t.B) is not { } b || Pixel(canvas, camera, t.C) is not { } c)
        {
            return;
        }

        double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(area) < 1e-9)
        {
            return;
        }

        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int x1 = Math.Min(canvas.Width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int y1 = Math.Min(canvas.Height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        double sign = Math.Sign(area);
        for (int y = y0; y <= y1; y++)
        {
            double py = y + 0.5;
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5;
                double w0 = ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) * sign;
                double w1 = ((c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X)) * sign;
                double w2 = ((a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X)) * sign;
                if (w0 >= 0 && w1 >= 0 && w2 >= 0)
                {
                    colour.CopyTo(canvas.Pixels, (y * canvas.Width + x) * 4);
                }
            }
        }
    }

    /// <summary>A line two canvas pixels wide (one in the finished picture).</summary>
    private static void DrawLine(RgbaImage canvas, SnapshotCamera camera, Vec3 from, Vec3 to, byte[] colour)
    {
        if (Pixel(canvas, camera, from) is not { } a || Pixel(canvas, camera, to) is not { } b)
        {
            return;
        }

        // A wall far outside the picture would only cost steps.
        double limit = canvas.Width * 4.0;
        if (Math.Abs(a.X) > limit || Math.Abs(a.Y) > limit || Math.Abs(b.X) > limit || Math.Abs(b.Y) > limit)
        {
            return;
        }

        double length = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        int steps = Math.Max(1, (int)Math.Ceiling(length));
        for (int i = 0; i <= steps; i++)
        {
            double x = a.X + (b.X - a.X) * i / steps, y = a.Y + (b.Y - a.Y) * i / steps;
            for (int dy = -1; dy <= 0; dy++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    int px = (int)Math.Round(x) + dx, py = (int)Math.Round(y) + dy;
                    if (px >= 0 && py >= 0 && px < canvas.Width && py < canvas.Height)
                    {
                        colour.CopyTo(canvas.Pixels, (py * canvas.Width + px) * 4);
                    }
                }
            }
        }
    }

    private static (double X, double Y)? Pixel(RgbaImage canvas, SnapshotCamera camera, Vec3 p) =>
        camera.Project(p) is { } uv ? (uv.U * canvas.Width, uv.V * canvas.Height) : null;
}
