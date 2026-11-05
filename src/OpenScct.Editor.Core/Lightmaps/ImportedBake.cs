using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>How far a bake is from a converted map's own atlases: mean absolute byte error and mean signed error
/// (bake minus atlas) over every texel the original's lightmaps cover, all three channels.</summary>
public readonly record struct BakeError(double Mae, double Bias, long Texels)
{
    public override string ToString() => $"MAE {Mae:0.00} (bias {Bias:+0.00;-0.00}) over {Texels} texels";
}

/// <summary>
/// A converted map's baked lightmaps (the cooked atlases, byte for byte) and the editor's own faithful bake of the
/// same charts (<see cref="ConvertedWorld"/>, <see cref="LightmapBaker"/>), so the two can be compared texel by texel:
/// what the light recovery fits against and how it checks its result. The bake is the one a relight of the imported
/// map runs, with the same lights, shadow scene and zone ambient.
/// </summary>
public sealed class ImportedBake
{
    private readonly LightmapBaker _baker = new();
    private readonly List<(int Width, int Height)> _bakeSizes;

    private ImportedBake(ConvertedWorld world, Func<int, Vec3> ambient)
    {
        World = world;
        Ambient = ambient;
        Occluders = TriangleBvh.Build(world.Occluders);
        _bakeSizes = world.Atlases.Select((_, a) => world.BakeSize(a)).ToList();
        foreach ((string uri, int w, int h) in world.Atlases)
        {
            string file = Path.Combine(world.Folder, "render", uri);
            Atlases.Add(File.Exists(file) && Png.Decode(File.ReadAllBytes(file)) is { } image && image.Width == w && image.Height == h
                ? image.Rgba
                : new byte[w * h * 4]);
        }

        Covered = Atlases.Select(a => new bool[a.Length / 4]).ToArray();
        Sampling = new ChartSamples[world.Charts.Count];
        Parallel.For(0, world.Charts.Count, c => Sampling[c] = ChartSamples.Of(world.Charts[c], clamp: true));
        for (int c = 0; c < world.Charts.Count; c++)
        {
            LightmapChart chart = world.Charts[c];
            ChartSamples s = Sampling[c];
            for (int i = 0; i < s.Piece.Length; i++)
            {
                if (s.Inside[i])
                {
                    Covered[chart.Atlas][StoredIndex(chart, i)] = true;
                }
            }
        }
    }

    public ConvertedWorld World { get; }

    /// <summary>The shadow scene: the source map's BSP and its shadow-casting static meshes.</summary>
    public TriangleBvh Occluders { get; }

    /// <summary>A zone's lightmap ambient fill, stored units.</summary>
    public Func<int, Vec3> Ambient { get; }

    /// <summary>The converted map's atlases as stored (RGBA8; the squeezed ones at their stored size).</summary>
    public List<byte[]> Atlases { get; } = new();

    /// <summary>Per atlas, the stored texels whose centre lies on a lit surface.</summary>
    public bool[][] Covered { get; }

    /// <summary>Per chart, where each texel samples its surface (clamped, as quality bakes sample).</summary>
    public ChartSamples[] Sampling { get; }

    /// <param name="castsShadow">Which static mesh instances (by actor id) cast shadows into the lightmaps.</param>
    /// <param name="outsideZones">The ambient of zones lighting.json does not list (the LevelInfo's).</param>
    public static ImportedBake Load(string folder, Func<string, bool>? castsShadow, Vec3 outsideZones)
    {
        ConvertedWorld world = ConvertedWorld.Load(folder, castsShadow);
        Dictionary<int, Vec3> zones = world.ZoneAmbient();
        return new ImportedBake(world, z => zones.TryGetValue(z, out Vec3 a) ? a : outsideZones);
    }

    /// <summary>The stored texel (index into the stored atlas) a chart texel lands on.</summary>
    public int StoredIndex(LightmapChart chart, int texel)
    {
        int shift = World.Compressed[chart.Atlas] ? 1 : 0;
        int x = (chart.X + (texel % chart.Width)) >> shift, y = (chart.Y + (texel / chart.Width)) >> shift;
        return (y * World.Atlases[chart.Atlas].Width) + x;
    }

    /// <summary>A faithful bake of <paramref name="lights"/> in the original's layout, as stored atlases (squeezed where
    /// the original squeezed). Texels outside the charts keep the converted map's bytes.</summary>
    public List<byte[]> Bake(IReadOnlyList<BakeLight> lights, CancellationToken cancel = default)
    {
        var scene = new BakeScene { Charts = World.Charts, Lights = lights, Occluders = Occluders, Ambient = Ambient };
        LightmapResult result = _baker.BakeInPlace(scene, new BakeQuality { Faithful = true }, _bakeSizes, cancel);
        var stored = new List<byte[]>();
        for (int a = 0; a < World.Atlases.Count; a++)
        {
            byte[] rgba = World.Compressed[a] ? ConvertedWorld.Compress(result.Rgba(a), _bakeSizes[a].Width, _bakeSizes[a].Height) : result.Rgba(a);
            stored.Add(rgba);
        }

        return stored;
    }

    /// <summary>The error of a bake against the converted map's atlases over the covered texels.</summary>
    public BakeError Compare(IReadOnlyList<byte[]> baked)
    {
        double abs = 0, signed = 0;
        long n = 0;
        for (int a = 0; a < Atlases.Count; a++)
        {
            byte[] want = Atlases[a], got = baked[a];
            bool[] covered = Covered[a];
            for (int t = 0; t < covered.Length; t++)
            {
                if (!covered[t])
                {
                    continue;
                }

                for (int k = 0; k < 3; k++)
                {
                    int d = got[(4 * t) + k] - want[(4 * t) + k];
                    abs += Math.Abs(d);
                    signed += d;
                }

                n++;
            }
        }

        return n == 0 ? default : new BakeError(abs / (3.0 * n), signed / (3.0 * n), n);
    }

    /// <summary>
    /// One sample per covered stored texel, thinned to one per <paramref name="thin"/> uu cell per surface orientation
    /// (0: every texel): where it is, its surface, its zone and the atlas texel it reads.
    /// </summary>
    public BakeSamples Samples(double thin)
    {
        var seen = new HashSet<(int Atlas, int Texel)>();
        var cells = new HashSet<(long, long, long, int, int, int)>();
        var s = new BakeSamples();
        for (int c = 0; c < World.Charts.Count; c++)
        {
            LightmapChart chart = World.Charts[c];
            ChartSamples cs = Sampling[c];
            for (int i = 0; i < cs.Piece.Length; i++)
            {
                if (!cs.Inside[i] || !seen.Add((chart.Atlas, StoredIndex(chart, i))))
                {
                    continue;
                }

                Vec3 p = chart.World(cs.Point[i]);
                if (thin > 0)
                {
                    Vec3 n = chart.Normal;
                    var key = ((long)Math.Floor(p.X / thin), (long)Math.Floor(p.Y / thin), (long)Math.Floor(p.Z / thin),
                        (int)Math.Round(n.X * 4), (int)Math.Round(n.Y * 4), (int)Math.Round(n.Z * 4));
                    if (!cells.Add(key))
                    {
                        continue;
                    }
                }

                s.Add(p, chart.Normal, chart.TwoSided, chart.Pieces[cs.Piece[i]].Zone, chart.Atlas, StoredIndex(chart, i));
            }
        }

        return s;
    }

    /// <summary>A stored atlas texel's RGB bytes.</summary>
    public static (int R, int G, int B) Texel(IReadOnlyList<byte[]> atlases, int atlas, int texel) =>
        (atlases[atlas][4 * texel], atlases[atlas][(4 * texel) + 1], atlases[atlas][(4 * texel) + 2]);
}

/// <summary>Lightmap texels as world samples (<see cref="ImportedBake.Samples"/>).</summary>
public sealed class BakeSamples
{
    private readonly List<Vec3> _p = new(), _n = new();
    private readonly List<bool> _twoSided = new();
    private readonly List<int> _zone = new(), _atlas = new(), _texel = new();

    public int Count => _p.Count;

    public IReadOnlyList<Vec3> Position => _p;

    public IReadOnlyList<Vec3> Normal => _n;

    public IReadOnlyList<bool> TwoSided => _twoSided;

    public IReadOnlyList<int> Zone => _zone;

    public IReadOnlyList<int> Atlas => _atlas;

    /// <summary>The stored texel index in its atlas.</summary>
    public IReadOnlyList<int> Texel => _texel;

    internal void Add(Vec3 p, Vec3 n, bool twoSided, int zone, int atlas, int texel)
    {
        _p.Add(p);
        _n.Add(n);
        _twoSided.Add(twoSided);
        _zone.Add(zone);
        _atlas.Add(atlas);
        _texel.Add(texel);
    }

    /// <summary>Per sample, RGB bytes of the given stored atlases (3 floats a sample).</summary>
    public float[] Read(IReadOnlyList<byte[]> atlases)
    {
        var rgb = new float[Count * 3];
        for (int i = 0; i < Count; i++)
        {
            (int r, int g, int b) = ImportedBake.Texel(atlases, _atlas[i], _texel[i]);
            (rgb[3 * i], rgb[(3 * i) + 1], rgb[(3 * i) + 2]) = (r, g, b);
        }

        return rgb;
    }
}
