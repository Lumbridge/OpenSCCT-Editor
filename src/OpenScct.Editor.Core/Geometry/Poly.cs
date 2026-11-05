using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>Which side of a plane a polygon lies on.</summary>
public enum PlaneSide
{
    Front,
    Back,
    Coplanar,
    Spanning,
}

/// <summary>The pieces of a polygon cut by a plane (null where there is no piece).</summary>
public readonly record struct PolySplit(PlaneSide Side, List<Vec3>? Front, List<Vec3>? Back);

/// <summary>Polygon helpers over vertex loops (planar, convex unless stated). Normals follow Newell, so a loop that
/// runs counter-clockwise seen from the front has its normal towards the viewer.</summary>
public static class Poly
{
    /// <summary>Default distance tolerance in Unreal units.</summary>
    public const double Epsilon = 0.01;

    /// <summary>The Newell vector: normal times twice the area.</summary>
    public static Vec3 Newell(IReadOnlyList<Vec3> v)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < v.Count; i++)
        {
            Vec3 a = v[i], b = v[(i + 1) % v.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec3(x, y, z);
    }

    public static Vec3 Normal(IReadOnlyList<Vec3> v) => Newell(v).Normalized();

    public static double Area(IReadOnlyList<Vec3> v) => Newell(v).Length / 2;

    public static Vec3 Average(IReadOnlyList<Vec3> v)
    {
        Vec3 sum = Vec3.Zero;
        foreach (Vec3 p in v)
        {
            sum += p;
        }

        return v.Count == 0 ? Vec3.Zero : sum / v.Count;
    }

    /// <summary>The largest distance of a vertex from the polygon's Newell plane (0 for a flat polygon).</summary>
    public static double PlanarError(IReadOnlyList<Vec3> v)
    {
        if (Plane3.FromPolygon(v) is not { } plane)
        {
            return 0;
        }

        return v.Max(p => Math.Abs(plane.Distance(p)));
    }

    /// <summary>True when every corner turns the same way about <paramref name="normal"/> (collinear corners pass).</summary>
    public static bool IsConvex(IReadOnlyList<Vec3> v, Vec3 normal, double tolerance = 1e-6)
    {
        int n = v.Count;
        if (n < 3)
        {
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            Vec3 a = v[i], b = v[(i + 1) % n], c = v[(i + 2) % n];
            Vec3 e1 = b - a, e2 = c - b;
            double scale = Math.Max(1e-12, e1.Length * e2.Length);
            if (Vec3.Dot(Vec3.Cross(e1, e2), normal) / scale < -tolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Splits a polygon by a plane. Vertices within <paramref name="tolerance"/> count as on the plane.</summary>
    public static PolySplit Split(IReadOnlyList<Vec3> v, Plane3 plane, double tolerance = Epsilon)
    {
        int n = v.Count;
        var d = new double[n];
        bool front = false, back = false;
        for (int i = 0; i < n; i++)
        {
            d[i] = plane.Distance(v[i]);
            front |= d[i] > tolerance;
            back |= d[i] < -tolerance;
        }

        if (!front && !back)
        {
            return new PolySplit(PlaneSide.Coplanar, null, null);
        }

        if (!back)
        {
            return new PolySplit(PlaneSide.Front, v.ToList(), null);
        }

        if (!front)
        {
            return new PolySplit(PlaneSide.Back, null, v.ToList());
        }

        var f = new List<Vec3>(n + 1);
        var b = new List<Vec3>(n + 1);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Vec3 p = v[i];
            int si = Math.Abs(d[i]) <= tolerance ? 0 : Math.Sign(d[i]);
            int sj = Math.Abs(d[j]) <= tolerance ? 0 : Math.Sign(d[j]);
            if (si >= 0)
            {
                f.Add(p);
            }

            if (si <= 0)
            {
                b.Add(p);
            }

            if (si * sj < 0)
            {
                Vec3 x = Vec3.Lerp(p, v[j], d[i] / (d[i] - d[j]));
                f.Add(x);
                b.Add(x);
            }
        }

        return new PolySplit(PlaneSide.Spanning, Clean(f), Clean(b));
    }

    /// <summary>Drops repeated and collinear vertices; returns null when fewer than three remain.</summary>
    public static List<Vec3>? Clean(List<Vec3> v, double tolerance = 1e-4)
    {
        var o = new List<Vec3>(v.Count);
        foreach (Vec3 p in v)
        {
            if (o.Count == 0 || (o[^1] - p).Length > tolerance)
            {
                o.Add(p);
            }
        }

        while (o.Count > 1 && (o[0] - o[^1]).Length <= tolerance)
        {
            o.RemoveAt(o.Count - 1);
        }

        bool removed = true;
        while (removed && o.Count >= 3)
        {
            removed = false;
            for (int i = 0; i < o.Count; i++)
            {
                Vec3 a = o[(i + o.Count - 1) % o.Count], b = o[i], c = o[(i + 1) % o.Count];
                double ab = (b - a).Length;
                if (ab > 0 && Vec3.Cross(b - a, c - b).Length / ab <= tolerance)
                {
                    o.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        return o.Count >= 3 ? o : null;
    }

    /// <summary>Two unit axes in the plane of <paramref name="normal"/>, with <c>u x v = normal</c>.</summary>
    public static (Vec3 U, Vec3 V) Basis(Vec3 normal)
    {
        Vec3 n = normal.Normalized();
        Vec3 helper = Math.Abs(n.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX;
        Vec3 u = Vec3.Cross(helper, n).Normalized();
        Vec3 v = Vec3.Cross(n, u);
        return (u, v);
    }

    /// <summary>The convex hull of points lying in a plane, wound counter-clockwise about <paramref name="normal"/>
    /// (so its Newell normal is <paramref name="normal"/>); null when fewer than three distinct corners remain.</summary>
    public static List<Vec3>? Hull2D(IEnumerable<Vec3> points, Vec3 normal, double tolerance = 1e-4)
    {
        (Vec3 u, Vec3 v) = Basis(normal);
        var pts = points
            .Select(p => (P: p, X: Vec3.Dot(p, u), Y: Vec3.Dot(p, v)))
            .OrderBy(t => t.X).ThenBy(t => t.Y)
            .ToList();
        if (pts.Count < 3)
        {
            return null;
        }

        // Andrew's monotone chain, counter-clockwise in (u, v), which is counter-clockwise about the normal.
        static double Cross((Vec3 P, double X, double Y) o, (Vec3 P, double X, double Y) a, (Vec3 P, double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new List<(Vec3 P, double X, double Y)>(pts.Count * 2);
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= tolerance)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= tolerance)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return Clean(hull.Select(h => h.P).ToList(), tolerance);
    }

    /// <summary>Rounds a point for stable output: 1/10000 of a unit, no negative zero.</summary>
    public static Vec3 Tidy(Vec3 p) => new(Tidy(p.X), Tidy(p.Y), Tidy(p.Z));

    public static double Tidy(double x) => Round(x, 4);

    /// <summary>Rounds a direction (texture axes are small numbers) to 1e-6.</summary>
    public static Vec3 Tidy6(Vec3 p) => new(Round(p.X, 6), Round(p.Y, 6), Round(p.Z, 6));

    private static double Round(double x, int digits)
    {
        double r = Math.Round(x, digits);
        return r == 0 ? 0 : r;
    }
}
