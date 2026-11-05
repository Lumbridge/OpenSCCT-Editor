using System.Numerics;

namespace OpenScct.Editor.Core.Assets.Import;

/// <summary>
/// 3D convex hulls for generated collision (incremental hull, double precision). Fronts are counter-clockwise seen
/// from outside, as glTF wants. A flat or nearly flat point set is thickened to <see cref="FlatThickness"/> first,
/// as the RE+ OBJ importer does (a sheet with no depth never collides).
/// </summary>
public static class ConvexHull
{
    /// <summary>Thickness given to flat parts, in the points' units (glTF metres: 2 Unreal units).</summary>
    public const double FlatThickness = 0.02;

    public sealed record Result(List<Vector3> Points, List<(int A, int B, int C)> Triangles);

    /// <summary>The hull of <paramref name="points"/>, or null when there are fewer than 3 distinct points.</summary>
    public static Result? Of(IReadOnlyCollection<Vector3> points)
    {
        List<Vector3d> pts = Distinct(points);
        if (pts.Count < 3)
        {
            return null;
        }

        double size = Size(pts);
        double eps = Math.Max(1e-9, size * 1e-7);
        if (Build(pts, eps) is { } hull)
        {
            return hull;
        }

        // Flat: push the points both ways along the plane normal and try again.
        Vector3d n = Normal(pts);
        if (n.Length() < 1e-12)
        {
            return null;
        }

        var thick = new List<Vector3d>(pts.Count * 2);
        foreach (Vector3d p in pts)
        {
            thick.Add(p + n * (FlatThickness / 2));
            thick.Add(p - n * (FlatThickness / 2));
        }

        return Build(thick, eps);
    }

    /// <summary>The 12 triangles of an axis-aligned box.</summary>
    public static Result Box(Vector3 min, Vector3 max)
    {
        var pts = new List<Vector3>();
        for (int i = 0; i < 8; i++)
        {
            pts.Add(new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z));
        }

        return Build(pts.Select(p => new Vector3d(p.X, p.Y, p.Z)).ToList(), 1e-9) ?? new Result(pts, []);
    }

    private static Result? Build(List<Vector3d> pts, double eps)
    {
        if (Initial(pts, eps) is not var (i0, i1, i2, i3))
        {
            return null;
        }

        var faces = new List<Face>();
        Vector3d centre = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) / 4;
        void AddOutward(int a, int b, int c)
        {
            var f = new Face(a, b, c, pts);
            faces.Add(f.Distance(centre) > 0 ? new Face(a, c, b, pts) : f);
        }

        AddOutward(i0, i1, i2);
        AddOutward(i0, i1, i3);
        AddOutward(i0, i2, i3);
        AddOutward(i1, i2, i3);
        for (int p = 0; p < pts.Count; p++)
        {
            if (p == i0 || p == i1 || p == i2 || p == i3)
            {
                continue;
            }

            var visible = faces.Where(f => f.Distance(pts[p]) > eps).ToList();
            if (visible.Count == 0)
            {
                continue;
            }

            var edges = new HashSet<(int, int)>();
            foreach (Face f in visible)
            {
                edges.Add((f.A, f.B));
                edges.Add((f.B, f.C));
                edges.Add((f.C, f.A));
            }

            faces.RemoveAll(f => visible.Contains(f));
            foreach ((int u, int v) in edges)
            {
                if (!edges.Contains((v, u)))
                {
                    faces.Add(new Face(u, v, p, pts));
                }
            }
        }

        // Keep only the corners the faces use.
        var map = new Dictionary<int, int>();
        var outPts = new List<Vector3>();
        int Map(int i)
        {
            if (!map.TryGetValue(i, out int j))
            {
                j = outPts.Count;
                map[i] = j;
                outPts.Add(new Vector3((float)pts[i].X, (float)pts[i].Y, (float)pts[i].Z));
            }

            return j;
        }

        var tris = faces.Select(f => (Map(f.A), Map(f.B), Map(f.C))).ToList();
        return new Result(outPts, tris);
    }

    /// <summary>Four points spanning a volume, or null when the set is flat.</summary>
    private static (int, int, int, int)? Initial(List<Vector3d> pts, double eps)
    {
        int a = 0, b = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].X < pts[a].X)
            {
                a = i;
            }

            if (pts[i].X > pts[b].X)
            {
                b = i;
            }
        }

        if (a == b || (pts[a] - pts[b]).Length() < eps)
        {
            b = Farthest(pts, i => (pts[i] - pts[a]).Length());
        }

        Vector3d ab = pts[b] - pts[a];
        int c = Farthest(pts, i => Vector3d.Cross(ab, pts[i] - pts[a]).Length());
        Vector3d n = Vector3d.Cross(ab, pts[c] - pts[a]);
        if (n.Length() < eps * eps)
        {
            return null;
        }

        n /= n.Length();
        int d = Farthest(pts, i => Math.Abs(Vector3d.Dot(n, pts[i] - pts[a])));
        return Math.Abs(Vector3d.Dot(n, pts[d] - pts[a])) <= eps * 10 ? null : (a, b, c, d);
    }

    private static int Farthest(List<Vector3d> pts, Func<int, double> metric)
    {
        int best = 0;
        double bestValue = double.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            double v = metric(i);
            if (v > bestValue)
            {
                (best, bestValue) = (i, v);
            }
        }

        return best;
    }

    /// <summary>The best-fit plane normal of a flat set (Newell over the points' order is not usable, so the largest
    /// cross product of spread pairs).</summary>
    private static Vector3d Normal(List<Vector3d> pts)
    {
        int a = 0;
        int b = Farthest(pts, i => (pts[i] - pts[a]).Length());
        Vector3d ab = pts[b] - pts[a];
        int c = Farthest(pts, i => Vector3d.Cross(ab, pts[i] - pts[a]).Length());
        Vector3d n = Vector3d.Cross(ab, pts[c] - pts[a]);
        return n.Length() > 0 ? n / n.Length() : n;
    }

    private static List<Vector3d> Distinct(IEnumerable<Vector3> points)
    {
        var seen = new HashSet<(long, long, long)>();
        var list = new List<Vector3d>();
        foreach (Vector3 p in points)
        {
            // Snap to a tenth of a millimetre so near-duplicates do not make sliver faces.
            if (seen.Add(((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4))))
            {
                list.Add(new Vector3d(p.X, p.Y, p.Z));
            }
        }

        return list;
    }

    private static double Size(List<Vector3d> pts)
    {
        double s = 0;
        foreach (Vector3d p in pts)
        {
            s = Math.Max(s, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))));
        }

        return Math.Max(s, 1e-6);
    }

    private sealed class Face
    {
        public Face(int a, int b, int c, List<Vector3d> pts)
        {
            (A, B, C) = (a, b, c);
            Vector3d n = Vector3d.Cross(pts[b] - pts[a], pts[c] - pts[a]);
            double len = n.Length();
            N = len > 0 ? n / len : n;
            D = Vector3d.Dot(N, pts[a]);
        }

        public int A { get; }

        public int B { get; }

        public int C { get; }

        private Vector3d N { get; }

        private double D { get; }

        public double Distance(Vector3d p) => Vector3d.Dot(N, p) - D;
    }

    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);

        public static Vector3d operator /(Vector3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);

        public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
    }
}
