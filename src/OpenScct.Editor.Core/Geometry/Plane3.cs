using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>A plane <c>dot(Normal, p) = D</c> with a unit normal. Points with a positive distance are in front
/// (outside, for a brush face plane).</summary>
public readonly record struct Plane3(Vec3 Normal, double D)
{
    public static Plane3 FromPointNormal(Vec3 point, Vec3 normal)
    {
        Vec3 n = normal.Normalized();
        return new Plane3(n, Vec3.Dot(n, point));
    }

    /// <summary>The plane through three points, normal by the right-hand rule; null when they are collinear.</summary>
    public static Plane3? FromPoints(Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 n = Vec3.Cross(b - a, c - a);
        return n.LengthSquared < 1e-18 ? null : FromPointNormal(a, n);
    }

    /// <summary>The plane of a polygon: Newell normal through the vertex average; null for a degenerate polygon.</summary>
    public static Plane3? FromPolygon(IReadOnlyList<Vec3> vertices)
    {
        Vec3 n = Poly.Newell(vertices);
        return n.LengthSquared < 1e-18 ? null : FromPointNormal(Poly.Average(vertices), n);
    }

    /// <summary>Parses <c>nx,ny,nz,d</c> (the normal is normalised and <c>d</c> scaled with it).</summary>
    public static Plane3 Parse(string text)
    {
        double[] v = Parsing.Numbers(text, 4, "plane (nx,ny,nz,d)");
        var n = new Vec3(v[0], v[1], v[2]);
        double len = n.Length;
        if (len < 1e-12)
        {
            throw new ArgumentException("plane normal is zero");
        }

        return new Plane3(n / len, v[3] / len);
    }

    /// <summary>Signed distance: positive in front of the plane.</summary>
    public double Distance(Vec3 p) => Vec3.Dot(Normal, p) - D;

    public Plane3 Flipped => new(-Normal, -D);

    public Vec3 Project(Vec3 p) => p - Normal * Distance(p);

    /// <summary>The point where the segment a-b crosses the plane (a and b on opposite sides).</summary>
    public Vec3 Intersect(Vec3 a, Vec3 b)
    {
        double da = Distance(a), db = Distance(b);
        double t = da / (da - db);
        return Vec3.Lerp(a, b, t);
    }

    public bool NearlyEquals(Plane3 other, double normalTolerance = 1e-5, double distanceTolerance = 0.01) =>
        (Normal - other.Normal).Length <= normalTolerance && Math.Abs(D - other.D) <= distanceTolerance;

    /// <summary><c>nx,ny,nz,d</c> (what <see cref="Parse"/> reads). The record's own ToString would print
    /// <see cref="Flipped"/>, whose Flipped... recurses until the stack overflows.</summary>
    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{Normal.X:0.######},{Normal.Y:0.######},{Normal.Z:0.######},{D:0.####}");
}
