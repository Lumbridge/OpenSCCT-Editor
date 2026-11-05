using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>What is wrong (or right) with a brush shape. Indexes are face indexes.</summary>
public sealed record ShapeReport(
    int Faces,
    int Vertices,
    double Volume,
    bool IsSheet,
    bool Closed,
    bool Convex,
    bool Outward,
    double MaxPlanarError,
    IReadOnlyList<int> ZeroAreaFaces,
    IReadOnlyList<int> NonPlanarFaces,
    IReadOnlyList<int> NonConvexFaces,
    IReadOnlyList<int> TooFewVertices,
    int OpenEdges)
{
    /// <summary>A usable solid brush: closed, convex, planar, outward and without degenerate faces.</summary>
    public bool IsCleanSolid => !IsSheet && Closed && Convex && Outward && ZeroAreaFaces.Count == 0 && NonPlanarFaces.Count == 0
        && NonConvexFaces.Count == 0 && TooFewVertices.Count == 0;
}

/// <summary>Planarity, convexity, closure and orientation checks of brush shapes (local space).</summary>
public static class ShapeAnalysis
{
    /// <summary>Faces further than this from flat are non-planar.</summary>
    public const double PlanarTolerance = 0.05;

    /// <summary>Faces smaller than this (square units) have no area.</summary>
    public const double ZeroArea = 0.01;

    public static ShapeReport Analyze(BrushShape shape)
    {
        List<BrushFace> faces = shape.Faces;
        var zero = new List<int>();
        var nonPlanar = new List<int>();
        var nonConvex = new List<int>();
        var tooFew = new List<int>();
        double maxPlanar = 0;
        for (int i = 0; i < faces.Count; i++)
        {
            List<Vec3> v = faces[i].Vertices;
            if (v.Count < 3)
            {
                tooFew.Add(i);
                continue;
            }

            Vec3 newell = Poly.Newell(v);
            if (newell.Length / 2 < ZeroArea)
            {
                zero.Add(i);
                continue;
            }

            double err = Poly.PlanarError(v);
            maxPlanar = Math.Max(maxPlanar, err);
            if (err > PlanarTolerance)
            {
                nonPlanar.Add(i);
            }

            if (!Poly.IsConvex(v, newell.Normalized(), 1e-4))
            {
                nonConvex.Add(i);
            }
        }

        double volume = Volume(shape);
        bool sheet = IsSheetShape(shape, volume);
        int open = OpenEdgeCount(shape);
        bool convex = !sheet && IsConvexSolid(shape);
        return new ShapeReport(faces.Count, faces.Sum(f => f.Vertices.Count), volume, sheet, open == 0, convex, volume > 0,
            maxPlanar, zero, nonPlanar, nonConvex, tooFew, open);
    }

    /// <summary>Signed volume (positive when the faces are wound outwards).</summary>
    public static double Volume(BrushShape shape)
    {
        double sum = 0;
        foreach (BrushFace f in shape.Faces)
        {
            List<Vec3> v = f.Vertices;
            for (int i = 1; i + 1 < v.Count; i++)
            {
                sum += Vec3.Dot(v[0], Vec3.Cross(v[i], v[i + 1]));
            }
        }

        return sum / 6;
    }

    /// <summary>Sheets (single planes, cross sheets, zero-volume shapes) are not solids and skip solid checks.</summary>
    public static bool IsSheetShape(BrushShape shape, double? volume = null)
    {
        if (shape.Builder?.Kind is "sheet" or "volumetric")
        {
            return true;
        }

        if (shape.Faces.Count < 4)
        {
            return true;
        }

        (Vec3 min, Vec3 max) = shape.Bounds();
        Vec3 size = max - min;
        double scale = Math.Max(1, Math.Max(size.X, Math.Max(size.Y, size.Z)));
        return Math.Abs(volume ?? Volume(shape)) < 1e-6 * scale * scale * scale;
    }

    /// <summary>True when every vertex is behind (or on) every face plane.</summary>
    public static bool IsConvexSolid(BrushShape shape, double tolerance = 0.05)
    {
        var verts = shape.Faces.SelectMany(f => f.Vertices).ToList();
        foreach (BrushFace f in shape.Faces)
        {
            if (Plane3.FromPolygon(f.Vertices) is not { } plane)
            {
                continue;
            }

            foreach (Vec3 p in verts)
            {
                if (plane.Distance(p) > tolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Edges used once (or with the same direction twice): 0 for a closed, consistently wound shape. An edge
    /// with another face's corner on it (a T-junction, within 0.01 units) is split there first, so it pairs with the
    /// run of collinear edges facing it, as the faces of a brush cut into pieces by material meet.</summary>
    public static int OpenEdgeCount(BrushShape shape)
    {
        var corners = new Dictionary<Key, Vec3>();
        foreach (BrushFace f in shape.Faces)
        {
            foreach (Vec3 p in f.Vertices)
            {
                corners.TryAdd(Key.Of(p), p);
            }
        }

        var edges = new Dictionary<(Key A, Key B), int>();
        var on = new List<(double T, Key K)>();
        foreach (BrushFace f in shape.Faces)
        {
            List<Vec3> v = f.Vertices;
            for (int i = 0; i < v.Count; i++)
            {
                Vec3 pa = v[i], pb = v[(i + 1) % v.Count];
                Key a = Key.Of(pa), b = Key.Of(pb);
                if (a == b)
                {
                    continue;
                }

                Vec3 ab = pb - pa;
                double len2 = ab.LengthSquared;
                on.Clear();
                foreach ((Key k, Vec3 p) in corners)
                {
                    if (k == a || k == b)
                    {
                        continue;
                    }

                    double t = Vec3.Dot(p - pa, ab) / len2;
                    if (t > 0 && t < 1 && (pa + ab * t - p).LengthSquared <= TJunction * TJunction)
                    {
                        on.Add((t, k));
                    }
                }

                Key from = a;
                foreach ((double _, Key k) in on.OrderBy(x => x.T))
                {
                    edges[(from, k)] = edges.GetValueOrDefault((from, k)) + 1;
                    from = k;
                }

                edges[(from, b)] = edges.GetValueOrDefault((from, b)) + 1;
            }
        }

        int open = 0;
        foreach (((Key a, Key b), int count) in edges)
        {
            if (edges.GetValueOrDefault((b, a)) != count)
            {
                open++;
            }
        }

        return open;
    }

    /// <summary>How far off an edge a corner may lie and still split it (<see cref="OpenEdgeCount"/>).</summary>
    private const double TJunction = 0.01;

    /// <summary>A welded vertex key (1/100 of a unit).</summary>
    private readonly record struct Key(long X, long Y, long Z)
    {
        public static Key Of(Vec3 p) => new((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100), (long)Math.Round(p.Z * 100));
    }
}
