using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>A lit world polygon handed to the chart builder, with its zone.</summary>
public readonly record struct LitPoly(Poly Poly, int Zone);

/// <summary>
/// Cuts lit world polygons into lightmap charts.
/// <para>Quality mode merges coplanar, touching polygons with the same lightmap scale and lighting flags into one chart
/// (whatever brush or material they came from), so light runs on across poly and material edges with no seam, but never
/// lets a chart span a narrow gap (<see cref="Components"/>); a polygon larger than an atlas is cut into tiles. Faithful
/// mode lays out one lightmap per brush face as
/// <c>LightMapLayout::AddSurface</c> (0x111a0410) does: size <c>((extent / scale) + 9) &amp; ~7</c> per axis, the scale
/// doubled until both fit in 256, a one-texel border, axes from the face's texture axes.</para>
/// </summary>
public static class ChartBuilder
{
    /// <summary>The original editor's cap on a lightmap side.</summary>
    public const int FaithfulMaxSize = 256;

    /// <summary>Coplanar polygons closer than this many texels that do not touch go to different charts: enough for the
    /// sampling reach, the dilation that fills the gap and the bilinear footprint on each side.</summary>
    private const double SeparateGap = 4;

    /// <summary>Polygons this close (uu) touch: CSG pieces of one floor share edges to well within it.</summary>
    private const double TouchDistance = 0.25;

    /// <summary>A chart wider than this many texels takes no polygon that would leave it less than
    /// <see cref="MinFill"/> covered, so the connected floors of a whole map do not become one mostly empty chart.</summary>
    private const int SparseSide = 64;

    private const double MinFill = 0.5;

    public static List<LightmapChart> Build(IEnumerable<LitPoly> polys, LightingSettings settings, double densityScale = 1)
    {
        List<LitPoly> list = polys.Where(p => p.Poly.Verts.Length >= 3).ToList();
        return settings.Mode == LightmapMode.Faithful
            ? Faithful(list)
            : Quality(list, settings.Density * densityScale, settings.AtlasSize);
    }

    // ---- quality ----------------------------------------------------------------------------------------------------

    private static List<LightmapChart> Quality(List<LitPoly> polys, double density, int atlasSize)
    {
        var groups = new Dictionary<(long, long, long, long, double, bool, bool), List<LitPoly>>();
        foreach (LitPoly lp in polys)
        {
            Plane3 pl = lp.Poly.Plane;
            Surface s = lp.Poly.Surface;
            var key = ((long)Math.Round(pl.N.X * 4096), (long)Math.Round(pl.N.Y * 4096), (long)Math.Round(pl.N.Z * 4096),
                (long)Math.Round(pl.D * 8), s.LightMapScale, TwoSided(s), SpecialLit(s));
            if (!groups.TryGetValue(key, out List<LitPoly>? g))
            {
                groups[key] = g = new List<LitPoly>();
            }

            g.Add(lp);
        }

        var charts = new List<LightmapChart>();
        foreach (List<LitPoly> group in groups.OrderBy(g => g.Key).Select(g => g.Value))
        {
            Surface s = group[0].Poly.Surface;
            Vec3 n = group[0].Poly.Plane.N;
            double d = group[0].Poly.Plane.D;
            (Vec3 u, Vec3 v) = Basis(n);
            double texel = Math.Clamp(s.LightMapScale / density, 1, 4096);
            foreach (List<LitPoly> component in Components(group, u, v, texel, atlasSize))
            {
                charts.AddRange(Charts(component, n, d, u, v, texel, TwoSided(s), SpecialLit(s), atlasSize));
            }
        }

        return charts;
    }

    /// <summary>The chart axes of a plane: walls run along the floor and up; floors and ceilings along X and Y.</summary>
    private static (Vec3 U, Vec3 V) Basis(Vec3 n)
    {
        Vec3 u = Math.Abs(n.Z) > 0.7071 ? (Vec3.UnitX - (n * n.X)).Normalized() : Vec3.Cross(Vec3.UnitZ, n).Normalized();
        return (u, Vec3.Cross(n, u));
    }

    /// <summary>
    /// Polygons of one plane grouped into charts. Only touching polygons (a shared edge or corner) join, and two polygons
    /// with a narrow gap between them (a floor on each side of a wall) never share a chart even when the rest of the
    /// floor connects them: texels over the gap would sample, or be filled from, the floor on the far side, and bilinear
    /// filtering would carry that light under the wall. A chart also stops growing where it would be mostly empty
    /// texels, and at an atlas side.
    /// </summary>
    private static List<List<LitPoly>> Components(List<LitPoly> group, Vec3 u, Vec3 v, double texel, int atlasSize)
    {
        int n = group.Count;
        var shape = new (double X, double Y)[n][];
        var box = new (double U0, double V0, double U1, double V1)[n];
        var area = new double[n];
        for (int i = 0; i < n; i++)
        {
            shape[i] = group[i].Poly.Verts.Select(p => (Vec3.Dot(p, u), Vec3.Dot(p, v))).ToArray();
            box[i] = (shape[i].Min(p => p.X), shape[i].Min(p => p.Y), shape[i].Max(p => p.X), shape[i].Max(p => p.Y));
            area[i] = Area(shape[i]);
        }

        double gap = SeparateGap * texel;
        var touching = new List<int>[n];
        var apart = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            (touching[i], apart[i]) = (new List<int>(), new List<int>());
        }

        int[] order = Enumerable.Range(0, n).OrderBy(i => box[i].U0).ToArray();
        for (int a = 0; a < n; a++)
        {
            int i = order[a];
            for (int b = a + 1; b < n && box[order[b]].U0 <= box[i].U1 + gap; b++)
            {
                int j = order[b];
                if (box[j].V0 > box[i].V1 + gap || box[j].V1 < box[i].V0 - gap)
                {
                    continue;
                }

                double d = Distance(shape[i], shape[j]);
                if (d <= TouchDistance)
                {
                    touching[i].Add(j);
                    touching[j].Add(i);
                }
                else if (d < gap)
                {
                    apart[i].Add(j);
                    apart[j].Add(i);
                }
            }
        }

        // Each chart grows from the first free polygon over touching ones, in input order, so builds repeat.
        var chartOf = new int[n];
        Array.Fill(chartOf, -1);
        var result = new List<List<LitPoly>>();
        double maxSide = (atlasSize - 2) * texel, sparseSide = SparseSide * texel;
        for (int seed = 0; seed < n; seed++)
        {
            if (chartOf[seed] >= 0)
            {
                continue;
            }

            int id = result.Count;
            var members = new List<LitPoly> { group[seed] };
            chartOf[seed] = id;
            (double U0, double V0, double U1, double V1) bounds = box[seed];
            double covered = area[seed];
            var queue = new Queue<int>(touching[seed]);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                if (chartOf[c] >= 0 || apart[c].Exists(k => chartOf[k] == id))
                {
                    continue;
                }

                (double U0, double V0, double U1, double V1) grown = (Math.Min(bounds.U0, box[c].U0), Math.Min(bounds.V0, box[c].V0),
                    Math.Max(bounds.U1, box[c].U1), Math.Max(bounds.V1, box[c].V1));
                double w = grown.U1 - grown.U0, h = grown.V1 - grown.V0;
                if (w > maxSide || h > maxSide || (Math.Max(w, h) > sparseSide && covered + area[c] < MinFill * w * h))
                {
                    continue;
                }

                chartOf[c] = id;
                members.Add(group[c]);
                bounds = grown;
                covered += area[c];
                foreach (int next in touching[c])
                {
                    queue.Enqueue(next);
                }
            }

            result.Add(members);
        }

        return result;
    }

    private static double Area((double X, double Y)[] p)
    {
        double sum = 0;
        for (int i = 0; i < p.Length; i++)
        {
            (double x0, double y0) = p[i];
            (double x1, double y1) = p[(i + 1) % p.Length];
            sum += (x0 * y1) - (x1 * y0);
        }

        return Math.Abs(sum) / 2;
    }

    /// <summary>The distance between two convex polygons of one plane (0 when they touch or overlap).</summary>
    private static double Distance((double X, double Y)[] a, (double X, double Y)[] b)
    {
        if (Inside(a[0], b) || Inside(b[0], a))
        {
            return 0;
        }

        double best = double.MaxValue;
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = 0; j < b.Length; j++)
            {
                best = Math.Min(best, SegmentDistance(a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length]));
            }
        }

        return best;
    }

    private static bool Inside((double X, double Y) p, (double X, double Y)[] poly)
    {
        int sign = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            (double X, double Y) a = poly[i], b = poly[(i + 1) % poly.Length];
            double cross = ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
            int s = Math.Abs(cross) < 1e-9 ? 0 : Math.Sign(cross);
            if (s != 0 && sign != 0 && s != sign)
            {
                return false;
            }

            sign = s != 0 ? s : sign;
        }

        return true;
    }

    private static double SegmentDistance((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        double d1 = Cross(a, b, c), d2 = Cross(a, b, d), d3 = Cross(c, d, a), d4 = Cross(c, d, b);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return 0;
        }

        return Math.Min(Math.Min(PointSegment(c, a, b), PointSegment(d, a, b)), Math.Min(PointSegment(a, c, d), PointSegment(b, c, d)));
    }

    private static double Cross((double X, double Y) o, (double X, double Y) p, (double X, double Y) q) =>
        ((p.X - o.X) * (q.Y - o.Y)) - ((p.Y - o.Y) * (q.X - o.X));

    private static double PointSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = (dx * dx) + (dy * dy);
        double t = len2 < 1e-12 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2, 0, 1);
        double ex = a.X + (t * dx) - p.X, ey = a.Y + (t * dy) - p.Y;
        return Math.Sqrt((ex * ex) + (ey * ey));
    }

    /// <summary>One chart per tile of at most an atlas side (with its border), so no chart is ever squeezed.</summary>
    private static IEnumerable<LightmapChart> Charts(List<LitPoly> polys, Vec3 n, double d, Vec3 u, Vec3 v, double texel, bool twoSided,
        bool specialLit, int atlasSize)
    {
        double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
        foreach (Vec3 p in polys.SelectMany(p => p.Poly.Verts))
        {
            (u0, u1) = (Math.Min(u0, Vec3.Dot(p, u)), Math.Max(u1, Vec3.Dot(p, u)));
            (v0, v1) = (Math.Min(v0, Vec3.Dot(p, v)), Math.Max(v1, Vec3.Dot(p, v)));
        }

        // Origins on the plane's own texel grid, so charts that meet at an edge sample it at the same points.
        (u0, v0) = (Math.Floor(u0 / texel) * texel, Math.Floor(v0 / texel) * texel);
        double tile = (atlasSize - 2) * texel;
        int tilesU = Math.Max(1, (int)Math.Ceiling((u1 - u0) / tile - 1e-9));
        int tilesV = Math.Max(1, (int)Math.Ceiling((v1 - v0) / tile - 1e-9));
        for (int tu = 0; tu < tilesU; tu++)
        {
            for (int tv = 0; tv < tilesV; tv++)
            {
                double lo = u0 + (tu * tile), lov = v0 + (tv * tile);
                double hi = tu == tilesU - 1 ? u1 : lo + tile, hiv = tv == tilesV - 1 ? v1 : lov + tile;
                var pieces = new List<(Poly Poly, int Zone)>();
                foreach (LitPoly lp in polys)
                {
                    Poly? p = lp.Poly;
                    if (tilesU > 1)
                    {
                        p = Clip(p, u, lo, hi);
                    }

                    if (p is not null && tilesV > 1)
                    {
                        p = Clip(p, v, lov, hiv);
                    }

                    if (p is not null)
                    {
                        pieces.Add((p, lp.Zone));
                    }
                }

                if (pieces.Count == 0)
                {
                    continue;
                }

                // Texel 0 and the last texel are border: the surface spans [1, size - 1).
                Vec3 origin = (n * d) + (u * (lo - texel)) + (v * (lov - texel));
                int w = (int)Math.Ceiling(((hi - lo) / texel) - 1e-6) + 2, h = (int)Math.Ceiling(((hiv - lov) / texel) - 1e-6) + 2;
                yield return Make(origin, u * texel, v * texel, n, Math.Max(3, w), Math.Max(3, h), twoSided, specialLit, pieces);
            }
        }
    }

    private static Poly? Clip(Poly p, Vec3 axis, double lo, double hi)
    {
        Poly? kept = p;
        if (kept.Verts.Any(x => Vec3.Dot(x, axis) < lo - 1e-6))
        {
            kept = kept.Split(new Plane3(axis, lo)).Front;
        }

        if (kept is not null && kept.Verts.Any(x => Vec3.Dot(x, axis) > hi + 1e-6))
        {
            kept = kept.Split(new Plane3(axis, hi)).Back;
        }

        return kept;
    }

    private static LightmapChart Make(Vec3 origin, Vec3 stepU, Vec3 stepV, Vec3 n, int w, int h, bool twoSided, bool specialLit,
        List<(Poly Poly, int Zone)> polys)
    {
        var chart = new LightmapChart
        {
            Origin = origin,
            StepU = stepU,
            StepV = stepV,
            Normal = n,
            Width = w,
            Height = h,
            TwoSided = twoSided,
            SpecialLit = specialLit,
            Pieces = new List<ChartPiece>(),
        };
        foreach ((Poly poly, int zone) in polys)
        {
            chart.Pieces.Add(new ChartPiece(poly.Verts.Select(chart.TexelOf).ToArray(), zone, poly));
        }

        return chart.Seal();
    }

    // ---- faithful ---------------------------------------------------------------------------------------------------

    private static List<LightmapChart> Faithful(List<LitPoly> polys)
    {
        var charts = new List<LightmapChart>();
        foreach (IGrouping<(string, int), LitPoly> face in polys.GroupBy(p => (p.Poly.Surface.BrushId, p.Poly.Surface.FaceIndex))
            .OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2))
        {
            // CSG pieces of a face share its plane; a face split across planes (never in practice) keeps the first's.
            Poly first = face.First().Poly;
            Surface s = first.Surface;
            Vec3 n = first.Plane.N;
            Vec3 u = s.TextureU - (n * Vec3.Dot(n, s.TextureU));
            u = u.LengthSquared < 1e-12 ? Basis(n).U : u.Normalized();
            Vec3 v = Vec3.Cross(n, u);
            double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
            foreach (Vec3 p in face.SelectMany(p => p.Poly.Verts))
            {
                (u0, u1) = (Math.Min(u0, Vec3.Dot(p, u)), Math.Max(u1, Vec3.Dot(p, u)));
                (v0, v1) = (Math.Min(v0, Vec3.Dot(p, v)), Math.Max(v1, Vec3.Dot(p, v)));
            }

            double scale = s.LightMapScale > 0 ? s.LightMapScale : 32;
            int w, h;
            while (true)
            {
                w = ((int)((u1 - u0) / scale) + 9) & ~7;
                h = ((int)((v1 - v0) / scale) + 9) & ~7;
                if (w <= FaithfulMaxSize && h <= FaithfulMaxSize)
                {
                    break;
                }

                scale *= 2;
            }

            double su = u1 - u0 > 1e-6 ? (u1 - u0) / (w - 2) : scale, sv = v1 - v0 > 1e-6 ? (v1 - v0) / (h - 2) : scale;
            Vec3 origin = (n * first.Plane.D) + (u * (u0 - su)) + (v * (v0 - sv));
            charts.Add(Make(origin, u * su, v * sv, n, w, h, TwoSided(s), SpecialLit(s), face.Select(p => (p.Poly, p.Zone)).ToList()));
        }

        return charts;
    }

    private static bool TwoSided(Surface s) => (s.Flags & PolyFlags.TwoSided) != 0;

    private static bool SpecialLit(Surface s) => (s.Flags & PolyFlags.SpecialLit) != 0;
}
