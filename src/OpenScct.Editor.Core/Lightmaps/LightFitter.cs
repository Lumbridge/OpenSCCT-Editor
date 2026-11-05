using System.Diagnostics;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>Settings of <see cref="LightFitter"/>.</summary>
public sealed record LightFitSettings
{
    /// <summary>One sample per cell of this size per surface orientation, uu.</summary>
    public double Thin { get; init; } = 24;

    /// <summary>Smallest seed residual (bytes) worth a light; the fit raises it to the map's noise floor.</summary>
    public double StopResidual { get; init; } = 10;

    /// <summary>Smallest peak (bytes at full term) of an accepted light.</summary>
    public double MinPeak { get; init; } = 10;

    /// <summary>A seed that gives no light masks this radius around it, uu.</summary>
    public double FailureMask { get; init; } = 96;

    /// <summary>Weight of over-lit texels in a candidate's score (a missed shadow over-lights; it should not veto).</summary>
    public double OvershootWeight { get; init; } = 0.7;

    public int MaxFailures { get; init; } = 60;

    public int MaxLights { get; init; } = 1500;

    /// <summary>After this many lights a seed must reach twice the noise floor (most later lights fit model error).</summary>
    public int SoftLimit { get; init; } = 300;

    /// <summary>A wall-clock limit on the fit, or null (the default) for none. A limit makes the result depend on how
    /// fast the machine is; without one the fit ends on its own stop rules (noise floor, soft limit, failures).</summary>
    public TimeSpan? TimeLimit { get; init; }
}

/// <summary>A stand-in light the fit placed: a point light (LightEffect 0) with its reach and colour amplitude.</summary>
/// <param name="Amplitude">Bytes per channel at a full term (<c>255 x BakeLight.Colour</c>).</param>
/// <param name="Seed">The smoothed residual (bytes) at the texel it was seeded from.</param>
/// <param name="Support">The share of its predicted light the residual held when it was placed (0..1), or null when it
/// adds 24 bytes or more to no sample (too faint to say).</param>
public sealed record FittedLight(Vec3 Location, double Radius, Vec3 Amplitude, double Gain, double Seed, double? Support)
{
    public double Peak => Math.Max(Amplitude.X, Math.Max(Amplitude.Y, Amplitude.Z));
}

/// <summary>
/// Places stand-in point lights that explain what a cooked map's atlases hold beyond the lights it still has: the
/// light of the static lights the cook stripped. Greedy: seed at the brightest smoothed residual texel, try a light
/// on the seed's normal at six heights and eleven radii (shadows traced on the bake's own <see cref="TriangleBvh"/>,
/// colour by least squares in closed form), refine position, radius and colour by Levenberg-Marquardt with a soft L1
/// weight, subtract, repeat. The forward model is the editor's own (<see cref="BakeLight.Surface"/>, shadows by
/// <see cref="LightmapBaker.Lit"/>, the colour through <see cref="BakeLight.Colour"/>), so a relight with the fitted
/// lights reproduces what was fitted. Fitted lights restore the look, not the author's lights: on sixteen maps with a
/// known editor copy about two in five of them sit within 128 uu of a lost light.
/// </summary>
public sealed class LightFitter
{
    private static readonly double[] s_heights = [12, 24, 48, 96, 192, 384];

    /// <summary>LightRadius 4 to 110: reach 125 to 2775 uu.</summary>
    private static readonly double[] s_radii = new[] { 4, 7, 10, 14, 19, 25, 33, 45, 60, 80, 110 }.Select(r => 25.0 * (r + 1)).ToArray();

    private const int MaxCandidateSamples = 6000;
    private const double SoftL1Scale = 12;

    private readonly Vec3[] _p, _n;
    private readonly bool[] _two;
    private readonly float[] _res;
    private readonly bool[] _sat;
    private readonly TriangleBvh _bvh;
    private readonly LightFitSettings _settings;
    private readonly SampleGrid _grid;
    private readonly int[][] _neighbours;
    private readonly float[] _smooth;

    /// <param name="residual">Bytes per sample and channel the known lights leave unexplained (atlas minus bake).</param>
    /// <param name="saturated">Samples whose atlas texel is saturated: they only bound the sum from below.</param>
    public LightFitter(BakeSamples samples, float[] residual, bool[] saturated, TriangleBvh occluders, LightFitSettings settings)
    {
        _p = samples.Position.ToArray();
        _n = samples.Normal.ToArray();
        _two = samples.TwoSided.ToArray();
        _res = (float[])residual.Clone();
        _sat = saturated;
        _bvh = occluders;
        _settings = settings;
        for (int i = 0; i < _sat.Length; i++)
        {
            if (_sat[i])
            {
                for (int k = 0; k < 3; k++)
                {
                    _res[(3 * i) + k] = Math.Max(0, _res[(3 * i) + k]);
                }
            }
        }

        _grid = new SampleGrid(_p, 256);
        _neighbours = new int[_p.Length][];
        Parallel.For(0, _p.Length, i => _neighbours[i] = _grid.Nearest(_p, _p[i], 6, settings.Thin * 3));
        _smooth = new float[_p.Length];
        Parallel.For(0, _p.Length, Smooth);
    }

    /// <summary>The residual the fitted lights leave, bytes per sample and channel.</summary>
    public IReadOnlyList<float> Residual => _res;

    /// <summary>The noise floor (bytes) the run stopped at.</summary>
    public double Floor { get; private set; }

    /// <summary>
    /// The 95th percentile of the smoothed overshoot (the model brighter than the bake) on samples the known lights light
    /// by 24 bytes or more: model error (soft shadow edges, meshes the shadow scene lacks) the fit should not place
    /// lights on. Only the overshoot is counted, because a lost light near a known one only ever adds light and would
    /// otherwise read as noise (ClubD keeps one light among 634 lost ones). <see cref="MeasuredFloor"/> keeps the
    /// measured value; the floor returned is at least <see cref="LightFitSettings.StopResidual"/> (on most maps the
    /// overshoot is lower, so the 10 bytes bind) and at most 30.
    /// </summary>
    public double NoiseFloor(float[] knownLight)
    {
        var values = new List<float>();
        for (int i = 0; i < _p.Length; i++)
        {
            if ((knownLight[3 * i] + knownLight[(3 * i) + 1] + knownLight[(3 * i) + 2]) / 3 >= 24)
            {
                values.Add(Math.Max(0, -_smooth[i]));
            }
        }

        if (values.Count < 100)
        {
            MeasuredFloor = null;
            return _settings.StopResidual;
        }

        values.Sort();
        MeasuredFloor = values[(int)(values.Count * 0.95)];
        return Math.Clamp(MeasuredFloor.Value, _settings.StopResidual, 30);
    }

    /// <summary>The model error <see cref="NoiseFloor"/> measured, before its bounds (null when too few samples).</summary>
    public double? MeasuredFloor { get; private set; }

    /// <summary>
    /// How much of the residual is light the fit can explain rather than noise: the share of samples whose smoothed
    /// residual is above <paramref name="floor"/>, and the mean excess over it (bytes per sample). A lost light leaves a
    /// patch of samples well above the floor; model error leaves scattered ones just above it.
    /// </summary>
    public (double Share, double Excess) Structure(double floor)
    {
        int above = 0;
        double excess = 0;
        foreach (float v in _smooth)
        {
            if (v > floor)
            {
                above++;
                excess += v - floor;
            }
        }

        return _smooth.Length == 0 ? (0, 0) : ((double)above / _smooth.Length, excess / _smooth.Length);
    }

    /// <summary>Runs the greedy fit down to <paramref name="floor"/> bytes of smoothed residual.</summary>
    public List<FittedLight> Run(double floor, Action<string>? progress = null, CancellationToken cancel = default)
    {
        Floor = Math.Max(floor, _settings.StopResidual);
        double minPeak = Math.Max(_settings.MinPeak, Floor);
        var found = new List<FittedLight>();
        var mask = new bool[_p.Length];
        var clock = Stopwatch.StartNew();
        int failures = 0;
        while (found.Count < _settings.MaxLights && (_settings.TimeLimit is not { } limit || clock.Elapsed < limit))
        {
            cancel.ThrowIfCancellationRequested();
            int seed = -1;
            float best = float.MinValue;
            for (int i = 0; i < _smooth.Length; i++)
            {
                if (!mask[i] && _smooth[i] > best)
                {
                    (best, seed) = (_smooth[i], i);
                }
            }

            double stop = found.Count >= _settings.SoftLimit ? 2 * Floor : Floor;
            if (seed < 0 || best < stop)
            {
                break;
            }

            FittedLight? light = null;
            if (Candidate(seed) is { } c)
            {
                (Vec3 at, double radius) = Refine(seed, c.At, c.Radius, c.Amplitude);
                light = Apply(at, radius, best, minPeak);
            }

            if (light is null)
            {
                foreach (int i in _grid.Within(_p, _p[seed], _settings.FailureMask))
                {
                    mask[i] = true;
                }

                if (++failures > _settings.MaxFailures)
                {
                    break;
                }

                continue;
            }

            failures = 0;
            found.Add(light);
            if (found.Count % 25 == 0)
            {
                progress?.Invoke($"light recovery: {found.Count} stand-in lights, seed residual {best:0.0} bytes, {clock.Elapsed.TotalSeconds:0}s");
            }
        }

        return found;
    }

    // ---- the forward model --------------------------------------------------------------------------------------------

    /// <summary>A stand-in point light as the bake sees it (the term only: colour is fitted separately).</summary>
    private static BakeLight Point(Vec3 at, double radius) =>
        new() { Id = "", Location = at, LightRadius = (radius / 25) - 1, Brightness = 255 };

    private double Term(BakeLight l, int i) => l.Surface(_p[i], _n[i], _two[i]);

    private bool Lit(BakeLight l, int i) => LightmapBaker.Lit(l, _p[i], _n[i], _two[i], l.Location, -l.Direction, _bvh);

    // ---- one light ----------------------------------------------------------------------------------------------------

    private sealed record Start(Vec3 At, double Radius, Vec3 Amplitude);

    /// <summary>The best light on the seed's normal: six heights, eleven radii, colour in closed form.</summary>
    private Start? Candidate(int seed)
    {
        Vec3 p0 = _p[seed], n0 = _n[seed];
        Start? best = null;
        double bestGain = 0;
        var rng = new Random(seed);
        foreach (double h in s_heights)
        {
            Vec3 at = p0 + (n0 * h);
            BakeLight probe = Point(at, s_radii[^1]);
            // A light in solid space cannot see its own seed.
            if (!Lit(probe, seed))
            {
                continue;
            }

            int[] idx = Subsample(_grid.Within(_p, at, s_radii[^1]), MaxCandidateSamples, rng);
            if (idx.Length < 8)
            {
                continue;
            }

            var lit = new bool[idx.Length];
            Parallel.For(0, idx.Length, k => lit[k] = Lit(probe, idx[k]));
            int[] visible = idx.Where((_, k) => lit[k]).ToArray();
            foreach (double radius in s_radii)
            {
                BakeLight l = Point(at, radius);
                var g = new double[visible.Length];
                for (int k = 0; k < visible.Length; k++)
                {
                    g[k] = Term(l, visible[k]);
                }

                (double gain, Vec3 a) = Score(visible, g);
                if (gain > bestGain)
                {
                    (bestGain, best) = (gain, new Start(at, radius, a));
                }
            }
        }

        return best;
    }

    /// <summary>Least-squares colour for term <paramref name="g"/> over samples <paramref name="idx"/>, and how much it
    /// lowers the squared residual (over-lit texels weighted by <see cref="LightFitSettings.OvershootWeight"/>).</summary>
    private (double Gain, Vec3 Amplitude) Score(int[] idx, double[] g)
    {
        double gg = 0, gr = 0, gG = 0, gB = 0;
        for (int k = 0; k < idx.Length; k++)
        {
            if (g[k] <= 0)
            {
                continue;
            }

            int i = 3 * idx[k];
            gg += g[k] * g[k];
            gr += g[k] * _res[i];
            gG += g[k] * _res[i + 1];
            gB += g[k] * _res[i + 2];
        }

        if (gg <= 0)
        {
            return (0, default);
        }

        var a = new Vec3(Math.Max(0, gr / gg), Math.Max(0, gG / gg), Math.Max(0, gB / gg));
        double gain = 0, w = _settings.OvershootWeight;
        for (int k = 0; k < idx.Length; k++)
        {
            if (g[k] <= 0)
            {
                continue;
            }

            int i = 3 * idx[k];
            for (int c = 0; c < 3; c++)
            {
                double r = _res[i + c], e = r - (g[k] * a[c]);
                e = e < 0 ? e * w : e;
                gain += (r * r) - (e * e);
            }
        }

        return (gain, a);
    }

    /// <summary>
    /// Refines position and reach with the colour by Levenberg-Marquardt, iteratively reweighted for a soft L1 loss
    /// (scale 12 bytes), on up to 6,000 samples the starting light sees (visibility held fixed); position within 200 uu.
    /// </summary>
    private (Vec3 At, double Radius) Refine(int seed, Vec3 at0, double radius0, Vec3 a0)
    {
        BakeLight start = Point(at0, radius0);
        var rng = new Random(seed ^ 0x5bd1e995);
        int[] near = Subsample(_grid.Within(_p, at0, radius0 * 1.15), 8000, rng);
        var lit = new bool[near.Length];
        Parallel.For(0, near.Length, k => lit[k] = Lit(start, near[k]));
        int[] idx = Subsample(near.Where((_, k) => lit[k]).ToArray(), MaxCandidateSamples, rng);
        if (idx.Length < 12)
        {
            return (at0, radius0);
        }

        // x = (X, Y, Z, ln R, aR, aG, aB)
        double[] x = [at0.X, at0.Y, at0.Z, Math.Log(radius0), Math.Max(a0.X, 0.5), Math.Max(a0.Y, 0.5), Math.Max(a0.Z, 0.5)];
        double[] lo = [at0.X - 200, at0.Y - 200, at0.Z - 200, Math.Log(60), 0, 0, 0];
        double[] hi = [at0.X + 200, at0.Y + 200, at0.Z + 200, Math.Log(6000), 600, 600, 600];
        double[] steps = [2, 2, 2, 0.02];
        double lambda = 1e-2;
        double cost = Cost(idx, x);
        for (int iter = 0; iter < 30; iter++)
        {
            double[] g = Terms(idx, x);
            var dg = new double[4][];
            for (int j = 0; j < 4; j++)
            {
                double[] xp = (double[])x.Clone();
                xp[j] += steps[j];
                double[] gp = Terms(idx, xp);
                dg[j] = new double[idx.Length];
                for (int k = 0; k < idx.Length; k++)
                {
                    dg[j][k] = (gp[k] - g[k]) / steps[j];
                }
            }

            // Normal equations of the reweighted problem: (J'WJ) d = -J'We, J = de/dx.
            var a = new double[7, 7];
            var b = new double[7];
            var row = new double[7];
            for (int k = 0; k < idx.Length; k++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double e = _res[(3 * idx[k]) + c] - (g[k] * x[4 + c]);
                    double w = 1 / Math.Sqrt(1 + ((e / SoftL1Scale) * (e / SoftL1Scale)));
                    for (int j = 0; j < 4; j++)
                    {
                        row[j] = -x[4 + c] * dg[j][k];
                    }

                    row[4] = row[5] = row[6] = 0;
                    row[4 + c] = -g[k];
                    for (int p = 0; p < 7; p++)
                    {
                        if (row[p] == 0)
                        {
                            continue;
                        }

                        b[p] -= w * row[p] * e;
                        for (int q = 0; q < 7; q++)
                        {
                            a[p, q] += w * row[p] * row[q];
                        }
                    }
                }
            }

            bool improved = false;
            while (lambda < 1e8)
            {
                var damped = (double[,])a.Clone();
                for (int p = 0; p < 7; p++)
                {
                    damped[p, p] += lambda * Math.Max(a[p, p], 1e-9);
                }

                if (Solve(damped, b) is not { } d)
                {
                    lambda *= 4;
                    continue;
                }

                double[] trial = new double[7];
                for (int p = 0; p < 7; p++)
                {
                    trial[p] = Math.Clamp(x[p] + d[p], lo[p], hi[p]);
                }

                double trialCost = Cost(idx, trial);
                if (trialCost < cost)
                {
                    improved = cost - trialCost > 1e-4 * cost;
                    (x, cost) = (trial, trialCost);
                    lambda = Math.Max(lambda / 3, 1e-7);
                    break;
                }

                lambda *= 4;
            }

            if (!improved)
            {
                break;
            }
        }

        var at = new Vec3(x[0], x[1], x[2]);
        double radius = Math.Exp(x[3]);
        // A refined light that moved into solid space (it no longer sees its seed) keeps its start.
        return Lit(Point(at, radius), seed) ? (at, radius) : (at0, radius0);
    }

    private double[] Terms(int[] idx, double[] x)
    {
        BakeLight l = Point(new Vec3(x[0], x[1], x[2]), Math.Exp(x[3]));
        var g = new double[idx.Length];
        for (int k = 0; k < idx.Length; k++)
        {
            g[k] = Term(l, idx[k]);
        }

        return g;
    }

    /// <summary>The soft L1 cost of a parameter vector.</summary>
    private double Cost(int[] idx, double[] x)
    {
        double[] g = Terms(idx, x);
        double cost = 0;
        for (int k = 0; k < idx.Length; k++)
        {
            for (int c = 0; c < 3; c++)
            {
                double z = (_res[(3 * idx[k]) + c] - (g[k] * x[4 + c])) / SoftL1Scale;
                cost += Math.Sqrt(1 + (z * z)) - 1;
            }
        }

        return cost;
    }

    /// <summary>Traces the refined light's shadows on every sample it reaches, solves its colour again and subtracts
    /// it, when it lowers the residual and peaks at <paramref name="minPeak"/> bytes or more.</summary>
    private FittedLight? Apply(Vec3 at, double radius, double seedResidual, double minPeak)
    {
        BakeLight l = Point(at, radius);
        int[] idx = _grid.Within(_p, at, radius);
        var g = new double[idx.Length];
        Parallel.For(0, idx.Length, k => g[k] = Lit(l, idx[k]) ? Term(l, idx[k]) : 0);
        (double gain, Vec3 a) = Score(idx, g);
        if (gain <= 0 || Math.Max(a.X, Math.Max(a.Y, a.Z)) < minPeak)
        {
            return null;
        }

        double held = 0, predicted = 0;
        var touched = new List<int>();
        for (int k = 0; k < idx.Length; k++)
        {
            if (g[k] <= 0)
            {
                continue;
            }

            int i = 3 * idx[k];
            double pred = 0, res = 0;
            for (int c = 0; c < 3; c++)
            {
                double add = Math.Min(255, g[k] * a[c]);
                pred += add;
                res += _res[i + c];
                _res[i + c] -= (float)add;
            }

            if (pred >= 24)
            {
                held += Math.Clamp(res, 0, pred);
                predicted += pred;
            }

            touched.Add(idx[k]);
        }

        // Smoothed residual changes for the samples touched and their neighbours.
        var again = new HashSet<int>(touched);
        foreach (int i in touched)
        {
            again.UnionWith(_neighbours[i]);
        }

        Parallel.ForEach(again, Smooth);
        return new FittedLight(at, radius, a, gain, seedResidual, predicted > 0 ? held / predicted : null);
    }

    /// <summary>Median of the residual's mean over a sample and its nearest neighbours (one texel's noise is no seed).</summary>
    private void Smooth(int i)
    {
        int[] nb = _neighbours[i];
        Span<float> v = stackalloc float[nb.Length];
        for (int k = 0; k < nb.Length; k++)
        {
            int j = 3 * nb[k];
            v[k] = (_res[j] + _res[j + 1] + _res[j + 2]) / 3;
        }

        v.Sort();
        _smooth[i] = nb.Length == 0 ? 0 : (nb.Length % 2 == 1 ? v[nb.Length / 2] : (v[(nb.Length / 2) - 1] + v[nb.Length / 2]) / 2);
    }

    private static int[] Subsample(int[] idx, int max, Random rng)
    {
        if (idx.Length <= max)
        {
            return idx;
        }

        int[] copy = (int[])idx.Clone();
        for (int i = 0; i < max; i++)
        {
            int j = rng.Next(i, copy.Length);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy[..max];
    }

    /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                return null;
            }

            if (pivot != col)
            {
                for (int k = 0; k < n; k++)
                {
                    (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                }

                (v[col], v[pivot]) = (v[pivot], v[col]);
            }

            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                for (int k = col; k < n; k++)
                {
                    m[r, k] -= f * m[col, k];
                }

                v[r] -= f * v[col];
            }
        }

        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = v[r];
            for (int k = r + 1; k < n; k++)
            {
                s -= m[r, k] * x[k];
            }

            x[r] = s / m[r, r];
        }

        return x;
    }
}

/// <summary>A uniform grid over sample positions for radius and nearest-neighbour queries.</summary>
public sealed class SampleGrid
{
    private readonly double _cell;
    private readonly Dictionary<(int, int, int), int[]> _cells = new();

    public SampleGrid(IReadOnlyList<Vec3> points, double cell)
    {
        _cell = cell;
        var lists = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            (int, int, int) key = Key(points[i]);
            if (!lists.TryGetValue(key, out List<int>? list))
            {
                lists[key] = list = new List<int>();
            }

            list.Add(i);
        }

        foreach ((var key, List<int> list) in lists)
        {
            _cells[key] = list.ToArray();
        }
    }

    private (int, int, int) Key(Vec3 p) => ((int)Math.Floor(p.X / _cell), (int)Math.Floor(p.Y / _cell), (int)Math.Floor(p.Z / _cell));

    /// <summary>Points within <paramref name="radius"/> of <paramref name="centre"/>.</summary>
    public int[] Within(IReadOnlyList<Vec3> points, Vec3 centre, double radius)
    {
        var result = new List<int>();
        (int x0, int y0, int z0) = Key(centre - new Vec3(radius, radius, radius));
        (int x1, int y1, int z1) = Key(centre + new Vec3(radius, radius, radius));
        double r2 = radius * radius;
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > _cells.Count)
        {
            // A reach wider than the map: walk the cells there are.
            foreach (((int x, int y, int z), int[] list) in _cells)
            {
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1 && z >= z0 && z <= z1)
                {
                    Collect(list);
                }
            }

            return result.ToArray();
        }

        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (_cells.TryGetValue((x, y, z), out int[]? list))
                    {
                        Collect(list);
                    }
                }
            }
        }

        return result.ToArray();

        void Collect(int[] list)
        {
            foreach (int i in list)
            {
                if ((points[i] - centre).LengthSquared <= r2)
                {
                    result.Add(i);
                }
            }
        }
    }

    /// <summary>Up to <paramref name="k"/> nearest points within <paramref name="radius"/> (the point itself included).</summary>
    public int[] Nearest(IReadOnlyList<Vec3> points, Vec3 centre, int k, double radius) =>
        Within(points, centre, radius).OrderBy(i => (points[i] - centre).LengthSquared).Take(k).ToArray();
}
