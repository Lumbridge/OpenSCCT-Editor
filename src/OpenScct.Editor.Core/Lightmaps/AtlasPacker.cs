namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// Packs charts into lightmap atlases with a skyline packer (lowest place first, then least wasted space), trying every
/// open atlas before starting a new one, so space is never stranded the way the original's packer flush left it
/// (0x111a02f0). Charts carry their own border texels, so they can sit side by side. Atlases are cropped to what they
/// use (multiples of 4).
/// </summary>
public static class AtlasPacker
{
    /// <summary>Places every chart (sets Atlas, X, Y) and returns each atlas's size.</summary>
    public static List<(int Width, int Height)> Pack(IReadOnlyList<LightmapChart> charts, int size)
    {
        var bins = new List<Skyline>();
        foreach (LightmapChart c in charts.OrderByDescending(c => c.Height).ThenByDescending(c => c.Width).ThenBy(c => c.Key))
        {
            if (c.Width > size || c.Height > size)
            {
                throw new InvalidOperationException($"a {c.Width}x{c.Height} chart does not fit a {size} atlas");
            }

            int placed = -1;
            (int X, int Y) at = default;
            for (int b = 0; b < bins.Count && placed < 0; b++)
            {
                if (bins[b].Find(c.Width, c.Height) is { } spot)
                {
                    placed = b;
                    at = spot;
                }
            }

            if (placed < 0)
            {
                bins.Add(new Skyline(size));
                placed = bins.Count - 1;
                at = bins[placed].Find(c.Width, c.Height)!.Value;
            }

            bins[placed].Place(at.X, at.Y, c.Width, c.Height);
            c.Atlas = placed;
            c.X = at.X;
            c.Y = at.Y;
        }

        return bins.Select(b => (RoundUp(b.UsedWidth), RoundUp(b.UsedHeight))).ToList();
    }

    private static int RoundUp(int v) => Math.Max(4, (v + 3) & ~3);

    private sealed class Skyline
    {
        private readonly int _size;
        private readonly List<(int X, int Y, int Width)> _segments;

        public Skyline(int size)
        {
            _size = size;
            _segments = [(0, 0, size)];
        }

        public int UsedWidth { get; private set; }

        public int UsedHeight { get; private set; }

        public (int X, int Y)? Find(int w, int h)
        {
            (int X, int Y)? best = null;
            int bestWaste = int.MaxValue;
            for (int i = 0; i < _segments.Count; i++)
            {
                int x = _segments[i].X;
                if (x + w > _size)
                {
                    break;
                }

                // The rect rests on the highest segment it spans.
                int y = 0, covered = 0, waste = 0;
                for (int j = i; j < _segments.Count && covered < w; j++)
                {
                    y = Math.Max(y, _segments[j].Y);
                    covered += _segments[j].Width;
                }

                if (y + h > _size)
                {
                    continue;
                }

                covered = 0;
                for (int j = i; j < _segments.Count && covered < w; j++)
                {
                    int span = Math.Min(_segments[j].Width, w - covered);
                    waste += span * (y - _segments[j].Y);
                    covered += span;
                }

                if (best is null || y < best.Value.Y || (y == best.Value.Y && waste < bestWaste))
                {
                    best = (x, y);
                    bestWaste = waste;
                }
            }

            return best;
        }

        public void Place(int x, int y, int w, int h)
        {
            var next = new List<(int X, int Y, int Width)>();
            foreach ((int sx, int sy, int sw) in _segments)
            {
                int end = sx + sw;
                if (end <= x || sx >= x + w)
                {
                    next.Add((sx, sy, sw));
                    continue;
                }

                if (sx < x)
                {
                    next.Add((sx, sy, x - sx));
                }

                if (end > x + w)
                {
                    next.Add((x + w, sy, end - (x + w)));
                }
            }

            next.Add((x, y + h, w));
            next.Sort((a, b) => a.X.CompareTo(b.X));
            _segments.Clear();
            foreach ((int X, int Y, int Width) s in next)
            {
                if (_segments.Count > 0 && _segments[^1].Y == s.Y)
                {
                    _segments[^1] = (_segments[^1].X, s.Y, _segments[^1].Width + s.Width);
                }
                else
                {
                    _segments.Add(s);
                }
            }

            UsedWidth = Math.Max(UsedWidth, x + w);
            UsedHeight = Math.Max(UsedHeight, y + h);
        }
    }
}
