using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>What a brush face is in the CSG, from its brush's and its own PolyFlags (UE2 rules).</summary>
public enum SurfaceKind
{
    /// <summary>Part of the solid boundary: collides, splits the BSP, drawn unless Invisible.</summary>
    Solid,

    /// <summary>Of a semisolid brush: collides and goes into the BSP, but never cuts other brushes.</summary>
    Semisolid,

    /// <summary>Drawn only: non-solid brushes and faces, sheets.</summary>
    NonSolid,

    /// <summary>A zone portal sheet: neither drawn nor solid; separates zones.</summary>
    Portal,
}

/// <summary>The surface a polygon belongs to: one face of one brush, with its material, flags and texture mapping
/// in world space. Every CSG fragment of the face shares it.</summary>
public sealed class Surface
{
    public bool Displaced { get; init; }
    public required string BrushId { get; init; }

    public required int FaceIndex { get; init; }

    public string? Material { get; init; }

    /// <summary>The face's PolyFlags merged with its brush's.</summary>
    public int Flags { get; init; }

    public SurfaceKind Kind { get; init; }

    public Vec3 TextureBase { get; init; }

    public Vec3 TextureU { get; init; }

    public Vec3 TextureV { get; init; }

    public int PanU { get; init; }

    public int PanV { get; init; }

    public double LightMapScale { get; init; } = 32;

    public bool Invisible => (Flags & PolyFlags.Invisible) != 0 || Kind == SurfaceKind.Portal;

    /// <summary>Collides: solid and semisolid surfaces that are not flagged NotSolid.</summary>
    public bool Collides => Kind is SurfaceKind.Solid or SurfaceKind.Semisolid && (Flags & PolyFlags.NotSolid) == 0;
}

/// <summary>A convex planar polygon with its plane and surface. Vertices are wound so their Newell normal is the
/// plane normal (the UE2 front face).</summary>
public sealed class Poly
{
    public Poly(Vec3[] verts, Plane3 plane, Surface surface)
    {
        Verts = verts;
        Plane = plane;
        Surface = surface;
    }

    public Vec3[] Verts { get; }

    public Plane3 Plane { get; }

    public Surface Surface { get; }

    public Poly WithVerts(Vec3[] verts) => new(verts, Plane, Surface);

    /// <summary>The polygon facing the other way.</summary>
    public Poly Reversed()
    {
        var v = (Vec3[])Verts.Clone();
        Array.Reverse(v);
        return new Poly(v, Plane.Flipped(), Surface);
    }

    public Vec3 Centroid()
    {
        Vec3 c = Vec3.Zero;
        foreach (Vec3 v in Verts)
        {
            c += v;
        }

        return c / Verts.Length;
    }

    public double Area()
    {
        Vec3 total = Vec3.Zero;
        Vec3 v0 = Verts[0];
        for (int i = 1; i < Verts.Length - 1; i++)
        {
            total += Vec3.Cross(Verts[i] - v0, Verts[i + 1] - v0);
        }

        return total.Length / 2;
    }

    public Aabb Bounds() => Aabb.Of(Verts);

    public PlaneSide Classify(Plane3 plane) => ClassifyPoints(Verts, plane);

    public static PlaneSide ClassifyPoints(IReadOnlyList<Vec3> verts, Plane3 plane)
    {
        bool front = false, back = false;
        foreach (Vec3 v in verts)
        {
            double d = plane.Dist(v);
            if (d > Plane3.Epsilon)
            {
                front = true;
            }
            else if (d < -Plane3.Epsilon)
            {
                back = true;
            }
        }

        return front && back ? PlaneSide.Split : front ? PlaneSide.Front : back ? PlaneSide.Back : PlaneSide.On;
    }

    /// <summary>The parts in front of and behind a plane the polygon crosses (Sutherland-Hodgman). Null for a part
    /// that is empty or degenerate.</summary>
    public (Poly? Front, Poly? Back) Split(Plane3 plane)
    {
        (Vec3[]? f, Vec3[]? b) = SplitPoints(Verts, plane);
        return (f is null ? null : WithVerts(f), b is null ? null : WithVerts(b));
    }

    public static (Vec3[]? Front, Vec3[]? Back) SplitPoints(Vec3[] verts, Plane3 plane)
    {
        int n = verts.Length;
        var f = new List<Vec3>(n + 2);
        var b = new List<Vec3>(n + 2);
        Span<double> dist = n <= 64 ? stackalloc double[n] : new double[n];
        for (int i = 0; i < n; i++)
        {
            dist[i] = plane.Dist(verts[i]);
        }

        for (int i = 0; i < n; i++)
        {
            Vec3 a = verts[i], c = verts[(i + 1) % n];
            double da = dist[i], dc = dist[(i + 1) % n];
            if (da >= -Plane3.Epsilon)
            {
                f.Add(a);
            }

            if (da <= Plane3.Epsilon)
            {
                b.Add(a);
            }

            if ((da > Plane3.Epsilon && dc < -Plane3.Epsilon) || (da < -Plane3.Epsilon && dc > Plane3.Epsilon))
            {
                Vec3 p = SnapSplit(a + (c - a) * (da / (da - dc)));
                f.Add(p);
                b.Add(p);
            }
        }

        return (Clean(f), Clean(b));
    }

    /// <summary>Split points go on a 1/512 uu grid, so two polygons cut by one plane along one edge share them exactly.</summary>
    private static Vec3 SnapSplit(Vec3 p) =>
        new(Math.Round(p.X * 512.0) / 512.0, Math.Round(p.Y * 512.0) / 512.0, Math.Round(p.Z * 512.0) / 512.0);

    /// <summary>Drops repeated points; null when fewer than three remain or the area is negligible.</summary>
    public static Vec3[]? Clean(List<Vec3> vs)
    {
        var clean = new List<Vec3>(vs.Count);
        foreach (Vec3 v in vs)
        {
            if (clean.Count == 0 || !Near(v, clean[^1]))
            {
                clean.Add(v);
            }
        }

        while (clean.Count > 1 && Near(clean[0], clean[^1]))
        {
            clean.RemoveAt(clean.Count - 1);
        }

        if (clean.Count < 3)
        {
            return null;
        }

        Vec3 total = Vec3.Zero;
        for (int i = 1; i < clean.Count - 1; i++)
        {
            total += Vec3.Cross(clean[i] - clean[0], clean[i + 1] - clean[0]);
        }

        return total.Length / 2 > 1e-3 ? clean.ToArray() : null;
    }

    private static bool Near(Vec3 a, Vec3 b) => Math.Abs(a.X - b.X) <= 1e-6 && Math.Abs(a.Y - b.Y) <= 1e-6 && Math.Abs(a.Z - b.Z) <= 1e-6;

    /// <summary>A square of half size <paramref name="half"/> on a plane, wound so its normal is the plane's.</summary>
    public static Vec3[] HugeQuad(Plane3 plane, Vec3 near, double half)
    {
        Vec3 n = plane.N;
        Vec3 up = Math.Abs(n.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX;
        Vec3 u = Vec3.Cross(up, n).Normalized();
        Vec3 v = Vec3.Cross(n, u);
        Vec3 c = near - n * plane.Dist(near);
        // u x v = n, so (c - u - v), (c + u - v), (c + u + v), (c - u + v) is counter-clockwise about n: its Newell
        // normal is n.
        return [c - u * half - v * half, c + u * half - v * half, c + u * half + v * half, c - u * half + v * half];
    }
}
