using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// One bounce of indirect light, as a separate layer on top of the direct bake. The original has none, and Versus
/// stealth leans on that: corners that only see indirect light are where the spy hides. So it is off unless the map
/// asks for it (<see cref="LightingSettings.Bounce"/>), and it never touches pawn lighting.
/// <para>Each texel gathers <see cref="BakeQuality.BounceSamples"/> cosine-weighted rays; a ray that meets the front of
/// a lightmapped surface brings back the light the lights put there (its texel less the zone's ambient fill) times
/// its material's mean colour. Static meshes and unlit surfaces block bounce rays but reflect nothing. The layer is
/// <c>gain x mean(albedo x direct)</c> in stored units.</para>
/// </summary>
internal static class Bounce
{
    private const float Reach = 65536;
    private const double DefaultAlbedo = 0.5;
    private const int Run = 256;

    public static long Add(BakeScene scene, ChartSamples[] samples, bool[]?[] buried, float[][] texels, BakeQuality q, CancellationToken cancel)
    {
        IReadOnlyList<LightmapChart> charts = scene.Charts;
        float[][] direct = texels.Select(t => (float[])t.Clone()).ToArray();
        int count = q.BounceSamples;
        long rays = 0;
        // Work in runs of texels rather than whole charts, so a map of a few big charts still keeps every core busy.
        var runs = new List<(int Chart, int Start)>();
        for (int c = 0; c < charts.Count; c++)
        {
            for (int start = 0; start < samples[c].Piece.Length; start += Run)
            {
                runs.Add((c, start));
            }
        }

        Parallel.ForEach(runs, new ParallelOptions { CancellationToken = cancel }, run =>
        {
            int c = run.Chart;
            LightmapChart chart = charts[c];
            ChartSamples s = samples[c];
            (Vec3 ta, Vec3 tb) = LightmapBaker.Perpendiculars(chart.Normal);
            long local = 0;
            for (int i = run.Start; i < Math.Min(s.Piece.Length, run.Start + Run); i++)
            {
                if (s.Piece[i] < 0 || buried[c]?[i] == true)
                {
                    continue;
                }

                Vec3 origin = chart.World(s.Point[i]) + (chart.Normal * LightmapBaker.Bias);
                uint seed = (uint)i * 2654435761u ^ (uint)chart.Key;
                double r1 = (seed & 0xFFFF) / 65536.0, r2 = (seed >> 16) / 65536.0;
                double r = 0, g = 0, b = 0;
                for (int k = 0; k < count; k++)
                {
                    // Cosine-weighted hemisphere direction (Malley's method on a scrambled Hammersley point).
                    double u = LightmapBaker.Frac(((k + 0.5) / count) + r1), v = LightmapBaker.Frac(LightmapBaker.RadicalInverse((uint)k) + r2);
                    double radius = Math.Sqrt(u), phi = 2 * Math.PI * v;
                    Vec3 dir = (ta * (radius * Math.Cos(phi))) + (tb * (radius * Math.Sin(phi))) + (chart.Normal * Math.Sqrt(Math.Max(0, 1 - u)));
                    local++;
                    if (scene.Occluders.Closest(LightmapBaker.F(origin), LightmapBaker.F(dir), Reach) is not { Front: true } hit)
                    {
                        continue;
                    }

                    Vec3 light = LightAt(scene, direct, scene.Occluders.SourceOf(hit.Triangle), origin + (dir * hit.Distance), out Vec3 albedo);
                    r += albedo.X * light.X;
                    g += albedo.Y * light.Y;
                    b += albedo.Z * light.Z;
                }

                double k2 = q.Bounce / count;
                texels[c][3 * i] += (float)(r * k2);
                texels[c][(3 * i) + 1] += (float)(g * k2);
                texels[c][(3 * i) + 2] += (float)(b * k2);
            }

            Interlocked.Add(ref rays, local);
        });

        return rays;
    }

    /// <summary>The light the lights put where a bounce ray landed, and the colour that surface reflects.</summary>
    private static Vec3 LightAt(BakeScene scene, float[][] direct, int source, Vec3 at, out Vec3 albedo)
    {
        albedo = new Vec3(DefaultAlbedo, DefaultAlbedo, DefaultAlbedo);
        if (source < 0 || scene.ChartAt?.Invoke(source, at) is not (int ci, int pi))
        {
            return Vec3.Zero;
        }

        LightmapChart chart = scene.Charts[ci];
        ChartPiece piece = chart.Pieces[pi];
        TexelPoint t = chart.TexelOf(at);
        int x = Math.Clamp((int)t.X, 0, chart.Width - 1), y = Math.Clamp((int)t.Y, 0, chart.Height - 1), i = 3 * ((y * chart.Width) + x);
        albedo = scene.Albedo?.Invoke(chart, piece) ?? albedo;
        Vec3 fill = scene.Ambient(piece.Zone);
        return new Vec3(Math.Max(0, direct[ci][i] - fill.X), Math.Max(0, direct[ci][i + 1] - fill.Y), Math.Max(0, direct[ci][i + 2] - fill.Z));
    }
}
