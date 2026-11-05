using OpenScct.Core;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>
/// The light a compiled map's lightmaps hold on its world surfaces, read at a point: what the game draws on the wall a
/// merc sees behind a spy. The charts come from render/world.glb as the lightmapper reads a converted map
/// (<see cref="ConvertedWorld"/>), the texels from the atlases, squeezed ones read at half their place.
/// </summary>
public sealed class LightmapSurfaces
{
    /// <summary>The grid cell the charts are filed by, uu across.</summary>
    private const float Cell = 512f;

    /// <summary>How far off a chart's plane a point may lie and still be on it (collision and render faces differ a little).</summary>
    private const double PlaneSlack = 4;

    private readonly List<LightmapChart> _charts;
    private readonly Dictionary<(int, int), List<int>> _cells = [];
    private readonly (byte[] Rgba, int Width, int Height, bool Squeezed)?[] _atlases;

    private LightmapSurfaces(List<LightmapChart> charts, (byte[] Rgba, int Width, int Height, bool Squeezed)?[] atlases)
    {
        _charts = charts;
        _atlases = atlases;
        for (int c = 0; c < charts.Count; c++)
        {
            if (charts[c].Atlas < 0 || atlases[charts[c].Atlas] is null)
            {
                continue;
            }

            Aabb box = charts[c].Bounds;
            for (int x = CellOf(box.Min.X); x <= CellOf(box.Max.X); x++)
            {
                for (int y = CellOf(box.Min.Y); y <= CellOf(box.Max.Y); y++)
                {
                    if (!_cells.TryGetValue((x, y), out List<int>? list))
                    {
                        _cells[(x, y)] = list = [];
                    }

                    list.Add(c);
                }
            }
        }
    }

    /// <summary>
    /// Reads the lightmaps of the compiled map in <paramref name="folder"/>, keeping the atlases
    /// <paramref name="trusted"/> passes (by their URI under render/); null when the map has no lightmapped world.
    /// </summary>
    public static LightmapSurfaces? Load(string folder, Func<string, bool> trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        string render = Path.Combine(folder, "render");
        if (!File.Exists(Path.Combine(render, "world.glb")) || !File.Exists(Path.Combine(render, "lighting.json")))
        {
            return null;
        }

        ConvertedWorld world = ConvertedWorld.Load(folder, castsShadow: null);
        var atlases = new (byte[] Rgba, int Width, int Height, bool Squeezed)?[world.Atlases.Count];
        for (int a = 0; a < atlases.Length; a++)
        {
            string path = Path.Combine(render, world.Atlases[a].Uri);
            if (trusted(world.Atlases[a].Uri) && File.Exists(path) && Png.Decode(File.ReadAllBytes(path)) is { } image)
            {
                atlases[a] = (image.Rgba, image.Width, image.Height, world.Compressed[a]);
            }
        }

        return world.Charts.Count == 0 || atlases.All(a => a is null) ? null : new LightmapSurfaces(world.Charts, atlases);
    }

    /// <summary>
    /// The light the lightmap holds at <paramref name="p"/> on a surface facing <paramref name="normal"/> (luminance in
    /// stored units, 0 to 1, as a texel holds it), or null when no kept lightmap covers the point (a static mesh, a
    /// mover, an unlit surface).
    /// </summary>
    public float? At(FVector p, FVector normal)
    {
        if (!_cells.TryGetValue((CellOf(p.X), CellOf(p.Y)), out List<int>? list))
        {
            return null;
        }

        var at = new Vec3(p.X, p.Y, p.Z);
        var n = new Vec3(normal.X, normal.Y, normal.Z);
        foreach (int c in list)
        {
            LightmapChart chart = _charts[c];
            double facing = Vec3.Dot(n, chart.Normal);
            if (!chart.Bounds.Expand(PlaneSlack).Contains(at) || (chart.TwoSided ? Math.Abs(facing) : facing) < 0.7 ||
                Math.Abs(Vec3.Dot(at - chart.Origin, chart.Normal)) > PlaneSlack)
            {
                continue;
            }

            TexelPoint t = chart.TexelOf(at);
            if (chart.Pieces.All(piece => !piece.Contains(t)))
            {
                continue;
            }

            (byte[] rgba, int width, int height, bool squeezed) = _atlases[chart.Atlas]!.Value;
            int shift = squeezed ? 1 : 0;
            int x = Math.Clamp((int)Math.Floor(chart.X + t.X) >> shift, 0, width - 1);
            int y = Math.Clamp((int)Math.Floor(chart.Y + t.Y) >> shift, 0, height - 1);
            int i = ((y * width) + x) * 4;
            return (float)(((0.2126 * rgba[i]) + (0.7152 * rgba[i + 1]) + (0.0722 * rgba[i + 2])) / 255);
        }

        return null;
    }

    private static int CellOf(double v) => (int)Math.Floor(v / Cell);
}
