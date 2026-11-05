using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Snap;

/// <summary>How a moving face sits against the neighbour face it stuck to: on the same plane and overlapping it
/// (touching: a corridor's end on a room's wall, a crate on a floor), or on the same plane beside it (in line: tops
/// level with a nearby box, a wall flush with the run next to it). Purely geometric: add and subtract brushes alike.</summary>
public enum SnapRelation
{
    Touching,
    InLine,
}

/// <summary>What a neighbour face belongs to, best first when two faces tie.</summary>
public enum SnapSource
{
    Brush,
    Mesh,
    Actor,
    Baked,
}

/// <summary>
/// A face a snap can use: a neighbour's or the moving shape's, in world space. Faces whose normal lies within half a
/// degree of a world axis (and whose corners share that axis's coordinate) are axis faces and carry that coordinate
/// exactly in <see cref="Coord"/>; the rest are oblique (<see cref="Axis"/> -1) and use their plane.
/// </summary>
public sealed record SnapFace(ObjectRef? Owner, int Face, SnapSource Source, Vec3 Normal, double PlaneD,
    IReadOnlyList<Vec3> Polygon, Vec3 Min, Vec3 Max, int Axis, double Coord, bool Approximate)
{
    /// <summary>The cosine of half a degree: normals closer than that are parallel.</summary>
    public const double ParallelCos = 0.99996192306;

    /// <summary>The face's area in square units.</summary>
    public double Area { get; init; }

    /// <summary>The average of the corners.</summary>
    public Vec3 Center { get; init; }

    public bool IsAxis => Axis >= 0;

    /// <summary>The name the readouts give the owner: the object's id, or "baked world".</summary>
    public string Name => Owner?.Id ?? "baked world";

    /// <summary>"+X face", "−Z face", or "face" for an oblique one.</summary>
    public string Side => Axis < 0 ? "face" : (Normal[Axis] >= 0 ? "+" : "−") + "XYZ"[Axis] + " face";

    /// <summary>A face from its world polygon, or null when the polygon is degenerate (fewer than 3 corners, no area).
    /// <paramref name="normal"/> overrides the polygon's own normal (box faces of a mirrored mesh).</summary>
    public static SnapFace? Of(ObjectRef? owner, int face, SnapSource source, IReadOnlyList<Vec3> polygon, bool approximate = false,
        Vec3? normal = null)
    {
        if (polygon.Count < 3)
        {
            return null;
        }

        Vec3 newell = Poly.Newell(polygon);
        double area = newell.Length / 2;
        if (!(area >= 1e-6))
        {
            // No area, or NaN corners.
            return null;
        }

        Vec3 n = (normal ?? newell).Normalized();
        Vec3 center = Poly.Average(polygon);
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (Vec3 v in polygon)
        {
            min = Vec3.Min(min, v);
            max = Vec3.Max(max, v);
        }

        int axis = -1;
        double coord = 0;
        for (int a = 0; a < 3; a++)
        {
            if (Math.Abs(n[a]) >= ParallelCos && max[a] - min[a] <= 0.01)
            {
                axis = a;
                coord = Tidy(min[a] == max[a] ? min[a] : (min[a] + max[a]) / 2);
                n = Vec3.Zero.With(a, Math.Sign(n[a]));
                break;
            }
        }

        double d = axis >= 0 ? n[axis] * coord : Vec3.Dot(n, center);
        return new SnapFace(owner, face, source, n, d, polygon, min, max, axis, coord, approximate) { Area = area, Center = center };
    }

    /// <summary>This face moved by <paramref name="by"/>.</summary>
    public SnapFace Offset(Vec3 by)
    {
        if (by == Vec3.Zero)
        {
            return this;
        }

        return this with
        {
            Polygon = Polygon.Select(v => v + by).ToArray(),
            Min = Min + by,
            Max = Max + by,
            Coord = Axis >= 0 ? Coord + by[Axis] : Coord,
            PlaneD = PlaneD + Vec3.Dot(Normal, by),
            Center = Center + by,
        };
    }

    /// <summary>A coordinate read back from float or rotated data, cleaned of rounding noise (127.99999999 is 128).</summary>
    public static double Tidy(double v)
    {
        double r = Math.Round(v, 4);
        return Math.Abs(v - r) < 1e-6 ? r + 0.0 : v;
    }
}

/// <summary>A box with its own axes (unit <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/>; half sizes along them):
/// a mesh's box, an actor's collision box.</summary>
public readonly record struct OrientedBox(Vec3 Center, Vec3 X, Vec3 Y, Vec3 Z, Vec3 Half)
{
    /// <summary>A world-aligned box.</summary>
    public static OrientedBox Aligned(Vec3 min, Vec3 max) =>
        new((min + max) / 2, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, (max - min) / 2);

    /// <summary>A local box carried into the world by an affine transform (rotation, scale, offset).</summary>
    public static OrientedBox FromLocal(Vec3 min, Vec3 max, Func<Vec3, Vec3> toWorld)
    {
        Vec3 mid = (min + max) / 2, half = (max - min) / 2;
        Vec3 c = toWorld(mid);
        Vec3 ax = toWorld(mid + new Vec3(Math.Max(half.X, 0.5), 0, 0)) - c;
        Vec3 ay = toWorld(mid + new Vec3(0, Math.Max(half.Y, 0.5), 0)) - c;
        Vec3 az = toWorld(mid + new Vec3(0, 0, Math.Max(half.Z, 0.5))) - c;
        double sx = ax.Length / Math.Max(half.X, 0.5), sy = ay.Length / Math.Max(half.Y, 0.5), sz = az.Length / Math.Max(half.Z, 0.5);
        if (ax.LengthSquared < 1e-12 || ay.LengthSquared < 1e-12 || az.LengthSquared < 1e-12)
        {
            return Aligned(c - half, c + half);
        }

        return new OrientedBox(c, Clean(ax.Normalized()), Clean(ay.Normalized()), Clean(az.Normalized()),
            new Vec3(half.X * sx, half.Y * sy, half.Z * sz));
    }

    public Vec3 Axis(int i) => i switch { 0 => X, 1 => Y, _ => Z };

    /// <summary>The 8 corners.</summary>
    public Vec3[] Corners()
    {
        var c = new Vec3[8];
        for (int i = 0; i < 8; i++)
        {
            c[i] = Center + X * ((i & 1) != 0 ? Half.X : -Half.X) + Y * ((i & 2) != 0 ? Half.Y : -Half.Y) + Z * ((i & 4) != 0 ? Half.Z : -Half.Z);
        }

        return c;
    }

    /// <summary>The world-aligned bounds.</summary>
    public (Vec3 Min, Vec3 Max) Bounds()
    {
        Vec3 e = new(Extent(0), Extent(1), Extent(2));
        return (Center - e, Center + e);
    }

    /// <summary>The six faces (index 0..5: −X, +X, −Y, +Y, −Z, +Z of the box's own axes).</summary>
    public IReadOnlyList<SnapFace> Faces(ObjectRef? owner, SnapSource source, bool approximate)
    {
        var faces = new List<SnapFace>(6);
        double[] h = [Half.X, Half.Y, Half.Z];
        for (int i = 0; i < 3; i++)
        {
            Vec3 a = Axis(i), u = Axis((i + 1) % 3), v = Axis((i + 2) % 3);
            double hu = h[(i + 1) % 3], hv = h[(i + 2) % 3];
            for (int s = -1; s <= 1; s += 2)
            {
                Vec3 f = Center + a * (s * h[i]);
                Vec3[] poly = [f - u * hu - v * hv, f + u * hu - v * hv, f + u * hu + v * hv, f - u * hu + v * hv];
                if (s < 0)
                {
                    Array.Reverse(poly);
                }

                Vec3[] tidy = poly.Select(p => new Vec3(SnapFace.Tidy(p.X), SnapFace.Tidy(p.Y), SnapFace.Tidy(p.Z))).ToArray();
                if (SnapFace.Of(owner, i * 2 + (s > 0 ? 1 : 0), source, tidy, approximate, a * s) is { } face)
                {
                    faces.Add(face);
                }
            }
        }

        return faces;
    }

    private double Extent(int axis) => Math.Abs(X[axis]) * Half.X + Math.Abs(Y[axis]) * Half.Y + Math.Abs(Z[axis]) * Half.Z;

    /// <summary>An axis with rounding noise taken off its components (a 90° turn gives 6e-17, not 0).</summary>
    private static Vec3 Clean(Vec3 v) => new(Near(v.X), Near(v.Y), Near(v.Z));

    private static double Near(double x) => Math.Abs(x) < 1e-12 ? 0 : Math.Abs(Math.Abs(x) - 1) < 1e-12 ? Math.Sign(x) : x;
}

/// <summary>An actor's box for snapping, where it came from, and whether it is only an estimate (a mesh not loaded yet,
/// sized from the asset index or the class).</summary>
public readonly record struct SnapBox(OrientedBox Box, SnapSource Source, bool Approximate);

/// <summary>
/// The selection as it was when a drag started: its bounds, the faces that can stick (a single brush's own faces, a
/// mesh's box, or the combined box of several objects) and the directions a turn can line up (its largest faces'
/// normals, its axes).
/// </summary>
public sealed record MovingShape(Vec3 Min, Vec3 Max, IReadOnlyList<SnapFace> Faces, IReadOnlyList<Vec3> Directions)
{
    public Vec3 Center => (Min + Max) / 2;

    public Vec3 Size => Max - Min;

    /// <summary>The six faces of a world box.</summary>
    public static MovingShape Box(Vec3 min, Vec3 max, ObjectRef? owner = null) =>
        new(min, max, OrientedBox.Aligned(min, max).Faces(owner, SnapSource.Brush, false), [Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ]);
}

/// <summary>
/// How the drag's view measures distance at the moving shape: pixels per world unit along each world axis, world
/// units per pixel, the snap distance in pixels, the depth axis of a 2D view (null in perspective) and the pixels per
/// unit along any direction (for oblique and extrude gaps). Scripts use <see cref="World"/>, where a pixel is a unit.
/// <see cref="InLine"/> says whether faces beside the moving one (on its plane, not touching it) count too.
/// </summary>
public sealed record SnapView(double ThresholdPx, Vec3 PixelsPerUnit, double UnitsPerPixel, int? DepthAxis, Func<Vec3, double> PixelsPerUnitAlong)
{
    /// <summary>True when in-line matches count (touching ones always do).</summary>
    public bool InLine { get; init; } = true;

    /// <summary>A view with no screen: the distance is <paramref name="within"/> world units.</summary>
    public static SnapView World(double within) => new(within, Vec3.One, 1, null, _ => 1);

    /// <summary>The snap distance in world units.</summary>
    public double WorldThreshold => ThresholdPx * UnitsPerPixel;
}

/// <summary>One stick: the axis it fixed (-1 for an oblique plane or an extrude), how the faces sit, the neighbour face,
/// the moving face (null for a baked hit's own box) and the gap it closed (units, signed).</summary>
public sealed record SnapMatch(int Axis, SnapRelation Relation, SnapFace Target, SnapFace? Moving, double Gap)
{
    /// <summary>True when <paramref name="other"/> sticks the same faces (the hysteresis keeps it).</summary>
    public bool SameAs(SnapMatch? other)
    {
        if (other is null || other.Axis != Axis || other.Target.Source != Target.Source || other.Target.Owner != Target.Owner)
        {
            return false;
        }

        bool samePlane = Target.Axis >= 0 && other.Target.Axis == Target.Axis
            ? Math.Abs(Target.Coord - other.Target.Coord) < (Target.Source == SnapSource.Baked ? 0.5 : 1e-6)
            : Math.Abs(Vec3.Dot(Target.Normal, other.Target.Normal)) >= SnapFace.ParallelCos
              && Math.Abs(Target.PlaneD * Math.Sign(Vec3.Dot(Target.Normal, other.Target.Normal)) - other.Target.PlaneD) < 1e-6;
        if (!samePlane)
        {
            return false;
        }

        return (Moving, other.Moving) switch
        {
            (null, null) => true,
            ({ } a, { } b) => a.Face == b.Face && a.Owner == b.Owner && Math.Abs(Vec3.Dot(a.Normal, b.Normal) - 1) < 1e-9,
            _ => false,
        };
    }
}

/// <summary>A move's result: the offset (exact on the stuck axes, the raw offset elsewhere), which axes stuck and how.</summary>
public sealed record MoveSnap(Vec3 Offset, bool StuckX, bool StuckY, bool StuckZ, IReadOnlyList<SnapMatch> Matches)
{
    public static MoveSnap None { get; } = new(Vec3.Zero, false, false, false, []);

    public bool Any => StuckX || StuckY || StuckZ;

    public bool Stuck(int axis) => axis switch { 0 => StuckX, 1 => StuckY, _ => StuckZ };
}

/// <summary>A scale factor or an extrude distance that puts a face on a neighbour's plane.</summary>
public sealed record ValueSnap(double Value, SnapMatch Match);

/// <summary>A turn (rotator units) that lines a direction of the selection up with a neighbour's face.
/// <see cref="Direction"/> is the neighbour's direction (for the guide); <see cref="Exact"/> marks a turn read from the
/// neighbour's own rotation.</summary>
public sealed record TurnSnap(int Units, SnapMatch Match, Vec3 Direction, bool Exact);

/// <summary>A box swept through the baked world: how far it went (0..1), the surface normal and where the box stopped.
/// <see cref="Exact"/> marks a time that is the contact itself (not a box sweep's stop a hair short of it).</summary>
public readonly record struct SweepHit(double Time, Vec3 Normal, Vec3 Location, bool Exact = false);

/// <summary>The baked world (an imported map's collision), swept as boxes.</summary>
public interface ISnapSweeper
{
    /// <summary>The first hit of a box (half size <paramref name="extent"/>) moving from <paramref name="from"/> to
    /// <paramref name="to"/>; null when it is clear or starts inside solid.</summary>
    SweepHit? Sweep(Vec3 from, Vec3 to, Vec3 extent);

    /// <summary>True when the box at <paramref name="at"/> overlaps the world.</summary>
    bool Overlaps(Vec3 at, Vec3 extent);
}
