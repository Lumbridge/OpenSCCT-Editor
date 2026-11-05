using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// A finished bake: each chart's texels in stored units and where the packer put it. Atlases come out as RGBA8 at
/// half intensity (byte = 255 x stored, the game multiplies by 2), as converted maps store theirs; quality bakes are
/// dithered so dark gradients, where the spy hides, do not band.
/// </summary>
public sealed class LightmapResult
{
    // A 4x4 Bayer matrix: an ordered dither is the same on every run, so builds stay byte-identical.
    private static readonly int[] s_bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    public LightmapResult(IReadOnlyList<LightmapChart> charts, float[][] texels, List<(int Width, int Height)> atlases, bool faithful, BakeStats stats)
    {
        Charts = charts;
        Texels = texels;
        Atlases = atlases;
        Faithful = faithful;
        Stats = stats;
    }

    public IReadOnlyList<LightmapChart> Charts { get; }

    /// <summary>Per chart, RGB per texel in stored units (row-major, chart width x height).</summary>
    public float[][] Texels { get; }

    public List<(int Width, int Height)> Atlases { get; }

    public bool Faithful { get; }

    public BakeStats Stats { get; }

    /// <summary>The atlas UV (0..1) of a point given in a chart's texel space.</summary>
    public (float U, float V) Uv(LightmapChart chart, TexelPoint t)
    {
        (int w, int h) = Atlases[chart.Atlas];
        return ((float)((chart.X + t.X) / w), (float)((chart.Y + t.Y) / h));
    }

    /// <summary>The atlas UV of a world point on a chart.</summary>
    public (float U, float V) Uv(LightmapChart chart, Vec3 world) => Uv(chart, chart.TexelOf(world));

    /// <summary>An atlas as RGBA8 bytes.</summary>
    public byte[] Rgba(int atlas)
    {
        (int w, int h) = Atlases[atlas];
        var rgba = new byte[w * h * 4];
        for (int i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255;
        }

        for (int c = 0; c < Charts.Count; c++)
        {
            LightmapChart chart = Charts[c];
            if (chart.Atlas != atlas)
            {
                continue;
            }

            float[] t = Texels[c];
            for (int y = 0; y < chart.Height; y++)
            {
                for (int x = 0; x < chart.Width; x++)
                {
                    int ax = chart.X + x, ay = chart.Y + y, src = 3 * ((y * chart.Width) + x), dst = 4 * ((ay * w) + ax);
                    double dither = Faithful ? 0 : ((s_bayer[((ay & 3) * 4) + (ax & 3)] + 0.5) / 16.0) - 0.5;
                    for (int k = 0; k < 3; k++)
                    {
                        rgba[dst + k] = (byte)Math.Clamp(Math.Round((t[src + k] * 255.0) + dither), 0, 255);
                    }
                }
            }
        }

        return rgba;
    }

    public byte[] Png(int atlas) => Compile.Output.Png.EncodeRgba(Atlases[atlas].Width, Atlases[atlas].Height, Rgba(atlas));
}
