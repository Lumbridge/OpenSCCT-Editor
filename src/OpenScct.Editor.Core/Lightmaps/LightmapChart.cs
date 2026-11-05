using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>A point in a chart's texel space: texel (i, j) covers [i, i + 1) x [j, j + 1).</summary>
public readonly record struct TexelPoint(double X, double Y)
{
    public static TexelPoint operator +(TexelPoint a, TexelPoint b) => new(a.X + b.X, a.Y + b.Y);

    public static TexelPoint operator -(TexelPoint a, TexelPoint b) => new(a.X - b.X, a.Y - b.Y);

    public static TexelPoint operator *(TexelPoint a, double s) => new(a.X * s, a.Y * s);
}

/// <summary>One convex piece of surface in a chart: its corners in texel space, its zone, and what it came from (the
/// compiler's polygon, so the output mesh can be written from the same pieces).</summary>
public sealed class ChartPiece
{
    public ChartPiece(TexelPoint[] corners, int zone, object? source = null)
    {
        Corners = corners;
        Zone = zone;
        Source = source;
    }

    public TexelPoint[] Corners { get; }

    public int Zone { get; }

    public object? Source { get; }

    /// <summary>Whether a point lies inside (or on) the piece; corners may wind either way.</summary>
    public bool Contains(TexelPoint p)
    {
        int n = Corners.Length;
        int sign = 0;
        for (int i = 0; i < n; i++)
        {
            TexelPoint a = Corners[i], b = Corners[(i + 1) % n];
            double cross = ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
            if (Math.Abs(cross) < 1e-9)
            {
                continue;
            }

            int s = cross > 0 ? 1 : -1;
            if (sign != 0 && s != sign)
            {
                return false;
            }

            sign = s;
        }

        return true;
    }

    /// <summary>The nearest point of the piece to <paramref name="p"/> (p itself when inside).</summary>
    public TexelPoint Nearest(TexelPoint p)
    {
        if (Contains(p))
        {
            return p;
        }

        TexelPoint best = Corners[0];
        double bestD = double.MaxValue;
        int n = Corners.Length;
        for (int i = 0; i < n; i++)
        {
            TexelPoint a = Corners[i], b = Corners[(i + 1) % n];
            TexelPoint ab = b - a;
            double len2 = (ab.X * ab.X) + (ab.Y * ab.Y);
            double t = len2 < 1e-12 ? 0 : Math.Clamp((((p.X - a.X) * ab.X) + ((p.Y - a.Y) * ab.Y)) / len2, 0, 1);
            TexelPoint q = a + (ab * t);
            double d = ((q.X - p.X) * (q.X - p.X)) + ((q.Y - p.Y) * (q.Y - p.Y));
            if (d < bestD)
            {
                bestD = d;
                best = q;
            }
        }

        return best;
    }

    public TexelPoint Centre()
    {
        double x = 0, y = 0;
        foreach (TexelPoint c in Corners)
        {
            x += c.X;
            y += c.Y;
        }

        return new TexelPoint(x / Corners.Length, y / Corners.Length);
    }
}

/// <summary>
/// A lightmap chart: a planar patch of world surface with its own texel grid. Texel (x, y) of the chart sits at
/// <c>Origin + x StepU + y StepV</c> in the world, and at (<see cref="X"/> + x, <see cref="Y"/> + y) in atlas
/// <see cref="Atlas"/> once packed.
/// </summary>
public sealed class LightmapChart
{
    public required Vec3 Origin { get; init; }

    /// <summary>World step of one texel along the chart's x.</summary>
    public required Vec3 StepU { get; init; }

    /// <summary>World step of one texel along the chart's y.</summary>
    public required Vec3 StepV { get; init; }

    /// <summary>The surface normal (the side the chart is lit from; two-sided charts are lit from both).</summary>
    public required Vec3 Normal { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public bool TwoSided { get; init; }

    public bool SpecialLit { get; init; }

    public required List<ChartPiece> Pieces { get; init; }

    /// <summary>The atlas it was packed into, or -1.</summary>
    public int Atlas { get; set; } = -1;

    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>World bounds of the chart's pieces.</summary>
    public Aabb Bounds { get; private set; } = Aabb.Empty;

    /// <summary>A key of the chart's geometry and texel grid (not its atlas place): cached shadows stay valid while it
    /// holds.</summary>
    public ulong Key { get; private set; }

    /// <summary>World size of a texel (its longer side), uu.</summary>
    public double TexelSize => Math.Max(StepU.Length, StepV.Length);

    public Vec3 World(TexelPoint t) => Origin + (StepU * t.X) + (StepV * t.Y);

    /// <summary>The texel-space point of a world point on the chart's plane.</summary>
    public TexelPoint TexelOf(Vec3 p)
    {
        // Solve p - Origin = x StepU + y StepV in the plane (the steps need not be at right angles).
        Vec3 d = p - Origin;
        double uu = Vec3.Dot(StepU, StepU), uv = Vec3.Dot(StepU, StepV), vv = Vec3.Dot(StepV, StepV);
        double du = Vec3.Dot(d, StepU), dv = Vec3.Dot(d, StepV);
        double det = (uu * vv) - (uv * uv);
        return det == 0 ? default : new TexelPoint(((du * vv) - (dv * uv)) / det, ((dv * uu) - (du * uv)) / det);
    }

    /// <summary>Works out <see cref="Bounds"/> and <see cref="Key"/>; call once the pieces are final.</summary>
    public LightmapChart Seal()
    {
        Aabb box = Aabb.Empty;
        var h = new Hash64();
        h.Add(Origin);
        h.Add(StepU);
        h.Add(StepV);
        h.Add(Normal);
        h.Add(Width);
        h.Add(Height);
        h.Add(TwoSided);
        h.Add(SpecialLit);
        foreach (ChartPiece p in Pieces)
        {
            h.Add(p.Zone);
            foreach (TexelPoint c in p.Corners)
            {
                h.Add(c.X);
                h.Add(c.Y);
                box = box.Add(World(c));
            }
        }

        Bounds = box.Expand(1);
        Key = h.Value;
        return this;
    }
}
