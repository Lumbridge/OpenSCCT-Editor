namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// Where each texel of a chart samples the surface. Clamped sampling (quality mode) samples covered texels at their
/// centre and texels that only touch the surface at the nearest point on it, nudged inside, so no sample lands in the
/// wall next door; texels that touch nothing are left for dilation. Unclamped sampling (faithful mode) samples every
/// texel of the rect at its centre, as the original's light's-eye render did, including border texels that lie in
/// the neighbouring wall.
/// </summary>
public sealed class ChartSamples
{
    private const double Reach = 0.75;

    private ChartSamples(int count)
    {
        Piece = new int[count];
        Point = new TexelPoint[count];
        Array.Fill(Piece, -1);
    }

    /// <summary>The piece each texel samples (its zone), or -1 for a texel that touches no piece.</summary>
    public int[] Piece { get; }

    public TexelPoint[] Point { get; }

    /// <summary>Texels whose centre lies on the surface (the rest are edge texels or uncovered).</summary>
    public bool[] Inside { get; private set; } = [];

    public int Covered { get; private set; }

    public static ChartSamples Of(LightmapChart chart, bool clamp)
    {
        int w = chart.Width, h = chart.Height;
        var s = new ChartSamples(w * h) { Inside = new bool[w * h] };
        var best = new double[w * h];
        Array.Fill(best, double.MaxValue);
        for (int pi = 0; pi < chart.Pieces.Count; pi++)
        {
            ChartPiece piece = chart.Pieces[pi];
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (TexelPoint c in piece.Corners)
            {
                (x0, x1, y0, y1) = (Math.Min(x0, c.X), Math.Max(x1, c.X), Math.Min(y0, c.Y), Math.Max(y1, c.Y));
            }

            int ix0 = Math.Max(0, (int)Math.Floor(x0 - Reach)), ix1 = Math.Min(w - 1, (int)Math.Floor(x1 + Reach));
            int iy0 = Math.Max(0, (int)Math.Floor(y0 - Reach)), iy1 = Math.Min(h - 1, (int)Math.Floor(y1 + Reach));
            TexelPoint middle = piece.Centre();
            for (int y = iy0; y <= iy1; y++)
            {
                for (int x = ix0; x <= ix1; x++)
                {
                    int i = (y * w) + x;
                    if (s.Inside[i])
                    {
                        continue;
                    }

                    var centre = new TexelPoint(x + 0.5, y + 0.5);
                    if (piece.Contains(centre))
                    {
                        s.Inside[i] = true;
                        s.Piece[i] = pi;
                        s.Point[i] = centre;
                        continue;
                    }

                    TexelPoint near = piece.Nearest(centre);
                    double d = Math.Max(Math.Abs(near.X - centre.X), Math.Abs(near.Y - centre.Y));
                    if (d < Reach && d < best[i])
                    {
                        best[i] = d;
                        s.Piece[i] = pi;
                        // A hair inside, so the shadow ray leaves from the surface and not from its edge.
                        s.Point[i] = near + ((middle - near) * 0.01);
                    }
                }
            }
        }

        if (!clamp)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    s.Point[i] = new TexelPoint(x + 0.5, y + 0.5);
                    s.Piece[i] = s.Piece[i] >= 0 ? s.Piece[i] : NearestPiece(chart, s.Point[i]);
                }
            }
        }

        s.Covered = s.Piece.Count(p => p >= 0);
        return s;
    }

    private static int NearestPiece(LightmapChart chart, TexelPoint p)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < chart.Pieces.Count; i++)
        {
            TexelPoint q = chart.Pieces[i].Nearest(p);
            double d = ((q.X - p.X) * (q.X - p.X)) + ((q.Y - p.Y) * (q.Y - p.Y));
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }
}
