using System.Collections.Concurrent;
using OpenScct.Content;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>Sparse, cached light paths and indirect-light transport. The game recombines colours, without tracing rays.</summary>
public sealed class WindowBakeCapture
{
    private readonly IReadOnlyList<LightmapChart> charts;
    private readonly float[][] baseline;
    private readonly List<WindowLightTransfer.Term>?[][] terms;
    private readonly int[] offsets;
    private readonly bool faithful;
    private float[][] direct = [];
    private float[][] final = [];
    private readonly bool[] affected;
    private readonly List<(int Target, int[] Sources)> directFill = [];
    private readonly List<(int Target, int[] Sources)> finalFill = [];
    private readonly ConcurrentBag<WindowLightTransfer.Bounce> bounces = [];

    public WindowBakeCapture(IReadOnlyList<LightmapChart> charts, bool faithful)
    {
        this.charts = charts; this.faithful = faithful;
        offsets = new int[charts.Count + 1]; baseline = new float[charts.Count][]; terms = new List<WindowLightTransfer.Term>?[charts.Count][];
        for (int c = 0; c < charts.Count; c++)
        {
            int n = charts[c].Width * charts[c].Height;
            offsets[c + 1] = offsets[c] + n; baseline[c] = new float[n * 3]; terms[c] = new List<WindowLightTransfer.Term>?[n];
        }
        affected = new bool[offsets[^1]];
    }
    private static float[] Rgb(Vec3 v) => [(float)v.X, (float)v.Y, (float)v.Z];
    public void Ambient(int c, int i, Vec3 value) => Rgb(value).CopyTo(baseline[c], i * 3);
    public void Light(int c, int i, int[] panes, Vec3 coefficient)
    {
        if (panes.Length > 0)
        {
            (terms[c][i] ??= []).Add(new() { Panes = panes, Colour = Rgb(coefficient) });
            affected[offsets[c] + i] = true;
        }
        else
        {
            float[] rgb = Rgb(coefficient);
            for (int k = 0; k < 3; k++) baseline[c][i * 3 + k] += faithful
                ? (float)(Math.Floor(Math.Min(255, rgb[k])) / 255) : rgb[k];
        }
    }
    public void PrepareDirect(float[][] texels, ChartSamples[] samples, bool[]?[] buried)
    {
        direct = texels.Select(t => (float[])t.Clone()).ToArray();
        FillDependencies(samples, buried, affected, directFill);
    }
    internal void Bounce(int targetChart, int targetPixel, int sourceChart, int sourcePixel, Vec3 gain, Vec3 ambient)
    {
        int source = offsets[sourceChart] + sourcePixel;
        if (!affected[source]) return;
        bounces.Add(new() { Target = offsets[targetChart] + targetPixel, Source = source, Gain = Rgb(gain), Ambient = Rgb(ambient) });
    }
    public void Finish(float[][] texels, ChartSamples[] samples, bool[]?[] buried)
    {
        final = texels;
        foreach (var b in bounces) affected[b.Target] = true;
        FillDependencies(samples, buried, affected, finalFill);
    }
    private void FillDependencies(ChartSamples[] samples, bool[]?[] buried, bool[] sensitivity, List<(int, int[])> output)
    {
        for (int c = 0; c < charts.Count; c++)
        {
            int w = charts[c].Width, h = charts[c].Height, offset = offsets[c];
            bool[] filled = Enumerable.Range(0, w * h).Select(i => samples[c].Piece[i] >= 0 && buried[c]?[i] != true).ToArray();
            for (int pass = 0; pass < w + h; pass++)
            {
                var next = new List<(int Target, int[] Sources)>();
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int i = y * w + x; if (filled[i]) continue;
                    var sources = new List<int>();
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h && filled[ny * w + nx]) sources.Add(offset + ny * w + nx);
                    }
                    if (sources.Count > 0) next.Add((offset + i, sources.ToArray()));
                }
                if (next.Count == 0) break;
                foreach (var f in next)
                {
                    filled[f.Target - offset] = true;
                    if (f.Sources.Any(i => sensitivity[i])) { sensitivity[f.Target] = true; output.Add(f); }
                }
            }
        }
    }
    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];
    public WindowLightTransfer.Batch Export(IReadOnlyList<string> atlasUris, IReadOnlyList<(int Width, int Height)> sizes, IReadOnlyList<bool>? compressed = null)
    {
        var batch = new WindowLightTransfer.Batch { Faithful = faithful };
        foreach (string uri in atlasUris) batch.Resources.Add(new() { Name = uri });
        var remap = new Dictionary<int, int>();
        for (int c = 0; c < charts.Count; c++)
        {
            LightmapChart chart = charts[c];
            bool squeeze = compressed?[chart.Atlas] == true;
            for (int i = 0; i < chart.Width * chart.Height; i++)
            {
                int index = offsets[c] + i; if (!affected[index]) continue;
                remap[index] = batch.Samples.Count;
                int x = chart.X + i % chart.Width, y = chart.Y + i / chart.Width;
                var sample = new WindowLightTransfer.Sample { Base = baseline[c].AsSpan(i * 3, 3).ToArray(),
                    Direct = direct[c].AsSpan(i * 3, 3).ToArray(), Initial = final[c].AsSpan(i * 3, 3).ToArray(), Terms = terms[c][i] ?? [] };
                sample.Destinations.Add(new() { Resource = chart.Atlas, Element = squeeze ? ((y >> 1) * (sizes[chart.Atlas].Width >> 1) + (x >> 1)) : y * sizes[chart.Atlas].Width + x,
                    Weight = squeeze ? .25f : 1, Dither = faithful ? 0 : (Bayer[((y & 3) * 4) + (x & 3)] + .5f) / 16 - .5f });
                batch.Samples.Add(sample);
            }
        }
        int Map(int index) => remap.GetValueOrDefault(index, -1);
        foreach (var f in directFill) batch.DirectFill.Add(new() { Target = Map(f.Target), Sources = f.Sources.Select(Map).ToArray() });
        foreach (var f in finalFill) batch.FinalFill.Add(new() { Target = Map(f.Target), Sources = f.Sources.Select(Map).ToArray() });
        foreach (var b in bounces.OrderBy(b => b.Target).ThenBy(b => b.Source)) batch.Bounces.Add(new() { Source = Map(b.Source), Target = Map(b.Target), Gain = b.Gain, Ambient = b.Ambient });
        return batch;
    }
}
