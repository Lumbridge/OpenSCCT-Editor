using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>Which side of a plane a polygon lies on.</summary>
public enum PlaneSide
{
    Front,
    Back,
    On,
    Split,
}

/// <summary>A plane <c>n . p = D</c> with a unit normal (UE2 FPlane); the signed distance is <c>n . p - D</c>.</summary>
public readonly record struct Plane3(Vec3 N, double D)
{
    /// <summary>Plane side tolerance in Unreal units (the stock editor's THRESH_POINT_ON_PLANE is 0.1; maps sit on whole units).</summary>
    public const double Epsilon = 0.01;

    public double Dist(Vec3 p) => N.X * p.X + N.Y * p.Y + N.Z * p.Z - D;

    public Plane3 Flipped() => new(-N, -D);

    /// <summary>The same plane, either orientation, within the tolerances.</summary>
    public bool Coincident(Plane3 o) =>
        (Vec3.Dot(N, o.N) > 1 - 1e-6 && Math.Abs(D - o.D) < Epsilon) ||
        (Vec3.Dot(N, o.N) < -(1 - 1e-6) && Math.Abs(D + o.D) < Epsilon);

    /// <summary>The plane of a polygon from its Newell normal, through the vertex average. Normals that are axis
    /// aligned within 1e-9 are made exact, so box planes stay exact. Returns false for a degenerate polygon.</summary>
    public static bool TryFromPolygon(IReadOnlyList<Vec3> verts, out Plane3 plane)
    {
        plane = default;
        if (verts.Count < 3)
        {
            return false;
        }

        double x = 0, y = 0, z = 0;
        Vec3 c = Vec3.Zero;
        for (int i = 0; i < verts.Count; i++)
        {
            Vec3 a = verts[i], b = verts[(i + 1) % verts.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
            c += a;
        }

        var n = new Vec3(x, y, z);
        double len = n.Length;
        if (len < 1e-9)
        {
            return false;
        }

        n = Exact(n / len);
        c /= verts.Count;
        plane = new Plane3(n, Vec3.Dot(n, c));
        return true;
    }

    private static Vec3 Exact(Vec3 n) => new(Snap(n.X), Snap(n.Y), Snap(n.Z));

    private static double Snap(double c) => Math.Abs(c) < 1e-9 ? 0 : c > 1 - 1e-9 ? 1 : c < -1 + 1e-9 ? -1 : c;
}

/// <summary>An axis-aligned box.</summary>
public readonly record struct Aabb(Vec3 Min, Vec3 Max)
{
    public static readonly Aabb Empty = new(new Vec3(double.MaxValue, double.MaxValue, double.MaxValue),
        new Vec3(double.MinValue, double.MinValue, double.MinValue));

    public bool IsEmpty => Min.X > Max.X;

    public Aabb Add(Vec3 p) => new(Vec3.Min(Min, p), Vec3.Max(Max, p));

    public Aabb Union(Aabb o) => new(Vec3.Min(Min, o.Min), Vec3.Max(Max, o.Max));

    public Aabb Expand(double by) => new(Min - new Vec3(by, by, by), Max + new Vec3(by, by, by));

    public bool Overlaps(Aabb o, double slop = Plane3.Epsilon) =>
        Min.X <= o.Max.X + slop && Max.X >= o.Min.X - slop &&
        Min.Y <= o.Max.Y + slop && Max.Y >= o.Min.Y - slop &&
        Min.Z <= o.Max.Z + slop && Max.Z >= o.Min.Z - slop;

    public bool Contains(Vec3 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;

    public Vec3 Center => (Min + Max) / 2;

    public static Aabb Of(IEnumerable<Vec3> points)
    {
        Aabb box = Empty;
        foreach (Vec3 p in points)
        {
            box = box.Add(p);
        }

        return box;
    }
}
