using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>Numbers of one bake, for build stats and the progress line.</summary>
public sealed record BakeStats(int Charts, long Texels, int Lights, int Jobs, int CachedJobs, long Rays, long Milliseconds);

/// <summary>
/// The lightmapper: lights every chart of a <see cref="BakeScene"/> into stored-unit texels (0..1 = byte 0..255; the
/// game shows twice that).
/// <para>Per texel: the zone's ambient plus, for each baked light that reaches the chart, <c>Colour x f x vis</c>
/// (<see cref="BakeLight"/>). <c>vis</c> is ray traced on the <see cref="TriangleBvh"/>. Quality passes shoot one ray per
/// texel and one per texel corner, then supersample (with a light of some size, for soft edges) the texels a shadow
/// edge's penumbra can reach and those whose corners catch an occluder the centre missed; faithful passes keep a hard
/// bit per texel centre (with the original's depth precision), its 3x3 filter, and round each light to bytes before
/// the saturating add (GetTextureData, 0x111b1d10).</para>
/// <para>Shadows are cached per light and chart, keyed by the light's shape (not its colour), the occluders its rays
/// can meet and the chart's geometry: recolouring a light re-adds cached shadows, moving one traces only the charts it
/// touches, and editing geometry retraces only the lights that reach the edit. Each kind of pass keeps its own
/// entries (<c>slot</c>), and <see cref="Save"/> and <see cref="Load"/> carry them from one build to the next. Work
/// runs on every core.</para>
/// </summary>
public sealed class LightmapBaker
{
    /// <summary>How far shadow rays start off the surface, uu.</summary>
    public const double Bias = 0.5;

    /// <summary>How close above a texel the back of a surface has to be for the texel to count as buried in solid
    /// (a floor running on under a kerb, a mesh sunk into the ground), uu.</summary>
    public const double BuryDepth = 32;

    /// <summary>The widest penumbra band, in texels, that soft shadows supersample on each side of a hard edge.</summary>
    private const int MaxPenumbra = 24;

    /// <summary>Changes whenever shadow masks would come out differently, so an older cache file is not read.</summary>
    private const int CacheVersion = 7;

    private static readonly byte[] s_cacheMagic = "OSLMSHAD"u8.ToArray();

    private readonly ConcurrentDictionary<(ulong Quality, ulong Light, ulong Chart), byte[]?> _shadows = new();
    private readonly ConcurrentDictionary<(ulong Chart, bool Clamp), ChartSamples> _samples = new();
    private readonly ConcurrentDictionary<(ulong Chart, bool Clamp, ulong Occluders), bool[]?> _buried = new();
    private readonly Dictionary<string, Used> _used = new(StringComparer.Ordinal);
    private readonly object _usedLock = new();

    /// <summary>The cache entries one slot's last bake used; anything no slot uses is dropped.</summary>
    private sealed class Used
    {
        public HashSet<(ulong, ulong, ulong)> Shadows { get; } = new();

        public HashSet<(ulong, bool)> Samples { get; } = new();

        public HashSet<(ulong, bool, ulong)> Buried { get; } = new();
    }

    public BakeStats? LastStats { get; private set; }

    /// <summary>Drops every cached shadow and sample layout.</summary>
    public void Clear()
    {
        lock (_usedLock)
        {
            _shadows.Clear();
            _samples.Clear();
            _buried.Clear();
            _used.Clear();
        }
    }

    /// <summary>Bakes every chart and packs them into atlases of <paramref name="atlasSize"/>. Passes of different
    /// kinds (the preview's coarse and full passes) keep their caches apart by <paramref name="slot"/>.</summary>
    public LightmapResult Bake(BakeScene scene, BakeQuality q, int atlasSize, CancellationToken cancel = default, string slot = "") =>
        Bake(scene, q, atlasSize, null, slot, cancel);

    /// <summary>Bakes charts that already have their atlas place (a converted map's own layout) into atlases of the
    /// given sizes.</summary>
    public LightmapResult BakeInPlace(BakeScene scene, BakeQuality q, IReadOnlyList<(int Width, int Height)> atlases, CancellationToken cancel = default,
        string slot = "") =>
        Bake(scene, q, 0, atlases, slot, cancel);

    private LightmapResult Bake(BakeScene scene, BakeQuality q, int atlasSize, IReadOnlyList<(int Width, int Height)>? placed, string slot,
        CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<LightmapChart> charts = scene.Charts;
        List<BakeLight> lights = scene.Lights.Where(l => l.Baked).ToList();
        bool clamp = !q.Faithful;
        var used = new Used();
        var samples = new ChartSamples[charts.Count];
        Parallel.For(0, charts.Count, new ParallelOptions { CancellationToken = cancel },
            i => samples[i] = _samples.GetOrAdd((charts[i].Key, clamp), _ => ChartSamples.Of(charts[i], clamp)));
        used.Samples.UnionWith(charts.Select(c => (c.Key, clamp)));

        // Texels buried in solid are never seen: they take no light and are filled from their neighbours instead, so
        // their darkness does not bleed out along every wall foot. A faithful bake lights them as the original did.
        var buried = new bool[]?[charts.Count];
        var chartKeys = charts.Select(c => c.Key).ToArray();
        if (q.Shadows && !q.Faithful)
        {
            var nearKeys = new ulong[charts.Count];
            Parallel.For(0, charts.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                LightmapChart c = charts[i];
                ulong near = nearKeys[i] = scene.Occluders.KeyOf(c.Bounds.Expand(BuryDepth));
                buried[i] = _buried.GetOrAdd((c.Key, clamp, near), _ => Buried(c, samples[i], scene.Occluders));
                // A chart's shadows also depend on which of its texels are buried.
                chartKeys[i] ^= near * 0x9E3779B97F4A7C15UL;
            });
            used.Buried.UnionWith(charts.Select((c, i) => (c.Key, clamp, nearKeys[i])));
        }

        ulong quality = QualityKey(q);
        var lightKeys = new ulong[lights.Count];
        Parallel.For(0, lights.Count, i => lightKeys[i] = LightKey(lights[i], scene.Occluders));
        var jobs = new List<(int Light, int Chart)>();
        var perChart = new List<int>[charts.Count];
        for (int c = 0; c < charts.Count; c++)
        {
            perChart[c] = new List<int>();
            for (int l = 0; l < lights.Count; l++)
            {
                if (Touches(lights[l], charts[c]) && (scene.Admits?.Invoke(charts[c], lights[l]) ?? true))
                {
                    perChart[c].Add(l);
                    jobs.Add((l, c));
                }
            }
        }

        // Shadows, biggest charts first so the last cores are not left with a giant.
        var shadows = new byte[]?[jobs.Count];
        long rays = 0;
        int cached = 0;
        if (q.Shadows)
        {
            var keys = new (ulong, ulong, ulong)[jobs.Count];
            int[] order = Enumerable.Range(0, jobs.Count).OrderByDescending(j => charts[jobs[j].Chart].Width * charts[jobs[j].Chart].Height).ToArray();
            Parallel.ForEach(order, new ParallelOptions { CancellationToken = cancel }, j =>
            {
                (int l, int c) = jobs[j];
                var key = keys[j] = (quality, lightKeys[l], JobChartKey(lights[l], charts[c], chartKeys[c], scene.Occluders, q));
                if (_shadows.TryGetValue(key, out byte[]? vis))
                {
                    Interlocked.Increment(ref cached);
                }
                else
                {
                    long n = 0;
                    vis = Shadow(lights[l], charts[c], samples[c], buried[c], scene.Occluders, q, ref n);
                    Interlocked.Add(ref rays, n);
                    _shadows[key] = vis;
                }

                shadows[j] = vis;
            });
            used.Shadows.UnionWith(keys);
        }

        Keep(slot, used);
        var jobIndex = new Dictionary<(int, int), int>(jobs.Count);
        for (int j = 0; j < jobs.Count; j++)
        {
            jobIndex[jobs[j]] = j;
        }

        var texels = new float[charts.Count][];
        var directions = scene.Directional ? new float[charts.Count][] : null;
        var windowResponse = scene.CaptureWindows && scene.TransmissionPath is not null && q.Shadows ? new WindowBakeCapture(charts, q.Faithful) : null;
        Parallel.For(0, charts.Count, new ParallelOptions { CancellationToken = cancel }, c =>
        {
            texels[c] = Compose(scene, charts[c], samples[c], buried[c], perChart[c].Select(l => (lights[l], q.Shadows ? shadows[jobIndex[(l, c)]] : null)), q, windowResponse, c);
            Dilate(texels[c], samples[c], buried[c], charts[c].Width, charts[c].Height);
            if (directions is not null)
            {
                var chart = charts[c]; var data = new float[chart.Width * chart.Height * 4];
                for (int i = 0; i < samples[c].Piece.Length; i++)
                {
                    if (samples[c].Piece[i] < 0 || buried[c]?[i] == true) continue;
                    Vec3 position = chart.World(samples[c].Point[i]), vector = Vec3.Zero; double energy = 0;
                    foreach (int l in perChart[c])
                    {
                        BakeLight light = lights[l]; byte[]? visibility = q.Shadows ? shadows[jobIndex[(l, c)]] : null;
                        double weight = light.Surface(position, chart.Normal, chart.TwoSided) * (visibility is null ? 1 : visibility[i] / 255.0) * (light.Colour.X + light.Colour.Y + light.Colour.Z);
                        if (light.OwnZoneOnly && light.Zone != chart.Pieces[samples[c].Piece[i]].Zone) continue;
                        vector += (light.IsSun ? -light.Direction : light.Location - position).Normalized() * weight; energy += weight;
                    }
                    Vec3 direction = vector.Normalized(); data[i * 4] = (float)direction.X; data[i * 4 + 1] = (float)direction.Y; data[i * 4 + 2] = (float)direction.Z;
                    data[i * 4 + 3] = (float)(energy > 1e-9 ? Math.Min(1, vector.Length / energy) : 0);
                }
                directions[c] = data;
            }
        });

        windowResponse?.PrepareDirect(texels, samples, buried);
        if (q.Bounce > 0 && !q.Faithful && scene.ChartAt is not null)
        {
            rays += Bounce.Add(scene, samples, buried, texels, q, cancel, windowResponse);
            Parallel.For(0, charts.Count, c => Dilate(texels[c], samples[c], buried[c], charts[c].Width, charts[c].Height));
        }

        List<(int Width, int Height)> sizes = placed?.ToList() ?? AtlasPacker.Pack(charts, atlasSize);
        LastStats = new BakeStats(charts.Count, samples.Sum(s => (long)s.Covered), lights.Count, jobs.Count, cached, rays, clock.ElapsedMilliseconds);
        windowResponse?.Finish(texels, samples, buried);
        return new LightmapResult(charts, texels, sizes, q.Faithful, LastStats) { WindowResponse = windowResponse, Directions = directions };
    }

    /// <summary>Records what a slot's bake used and drops every entry no slot uses any more, so the caches follow the
    /// map instead of growing over a long session.</summary>
    private void Keep(string slot, Used used)
    {
        lock (_usedLock)
        {
            _used[slot] = used;
            foreach (var key in _shadows.Keys.Where(k => !_used.Values.Any(u => u.Shadows.Contains(k))).ToList())
            {
                _shadows.TryRemove(key, out _);
            }

            foreach (var key in _samples.Keys.Where(k => !_used.Values.Any(u => u.Samples.Contains(k))).ToList())
            {
                _samples.TryRemove(key, out _);
            }

            foreach (var key in _buried.Keys.Where(k => !_used.Values.Any(u => u.Buried.Contains(k))).ToList())
            {
                _buried.TryRemove(key, out _);
            }
        }
    }

    // ---- cache file -------------------------------------------------------------------------------------------------

    /// <summary>Writes the shadow masks every slot's last bake used (compressed), for the next build of the same map.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        using (var file = File.Create(temp))
        {
            file.Write(s_cacheMagic);
            file.Write(BitConverter.GetBytes(CacheVersion));
            using var zip = new DeflateStream(file, CompressionLevel.Fastest);
            using var w = new BinaryWriter(zip);
            lock (_usedLock)
            {
                w.Write(_used.Count);
                foreach ((string slot, Used used) in _used)
                {
                    var entries = used.Shadows.Where(_shadows.ContainsKey).ToList();
                    w.Write(slot);
                    w.Write(entries.Count);
                    foreach ((ulong quality, ulong light, ulong chart) in entries)
                    {
                        byte[]? vis = _shadows[(quality, light, chart)];
                        w.Write(quality);
                        w.Write(light);
                        w.Write(chart);
                        w.Write(vis?.Length ?? -1);
                        if (vis is not null)
                        {
                            w.Write(vis);
                        }
                    }
                }
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads shadow masks <see cref="Save"/> wrote; a missing, older or damaged file is ignored.</summary>
    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using FileStream file = File.OpenRead(path);
            var head = new byte[s_cacheMagic.Length + 4];
            if (file.Read(head) != head.Length || !head.AsSpan(0, s_cacheMagic.Length).SequenceEqual(s_cacheMagic) ||
                BitConverter.ToInt32(head, s_cacheMagic.Length) != CacheVersion)
            {
                return;
            }

            using var zip = new DeflateStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(zip);
            int slots = r.ReadInt32();
            lock (_usedLock)
            {
                for (int s = 0; s < slots; s++)
                {
                    string slot = r.ReadString();
                    var used = new Used();
                    int count = r.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var key = (r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64());
                        int length = r.ReadInt32();
                        _shadows[key] = length < 0 ? null : r.ReadBytes(length);
                        used.Shadows.Add(key);
                    }

                    if (!_used.ContainsKey(slot))
                    {
                        _used[slot] = used;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
        {
            // A damaged cache only costs a full bake.
        }
    }

    // ---- keys -------------------------------------------------------------------------------------------------------

    private static ulong QualityKey(BakeQuality q)
    {
        var h = new Hash64();
        h.Add(q.Faithful);
        h.Add(q.Samples);
        h.Add(q.LightSize);
        h.Add(q.SunSize);
        return h.Value;
    }

    /// <summary>The light's shape plus, for point and spot lights, the occluders inside its reach. Suns and cylinder
    /// lights reach the whole map, so their occluders are keyed per chart instead (<see cref="JobChartKey"/>).</summary>
    private static ulong LightKey(BakeLight l, TriangleBvh occluders)
    {
        var h = new Hash64();
        h.Add(l.ShapeKey());
        h.Add(l.IsSun || l.IsCylinder ? 0UL : occluders.KeyOf(new Aabb(l.Location, l.Location).Expand(l.Radius)));
        return h.Value;
    }

    /// <summary>The chart's key for one light: for suns and cylinder lights, with the occluders the chart's shadow rays
    /// can meet, so an edit elsewhere in the map leaves the chart's sunlight cached.</summary>
    private static ulong JobChartKey(BakeLight l, LightmapChart c, ulong chartKey, TriangleBvh occluders, BakeQuality q)
    {
        if (!l.IsSun && !l.IsCylinder)
        {
            return chartKey;
        }

        var h = new Hash64();
        h.Add(chartKey);
        h.Add(occluders.KeyOf(RayBounds(l, c, occluders.Bounds, q)));
        return h.Value;
    }

    /// <summary>A box holding every shadow ray from the chart to the light: to a cylinder light's centre, or towards the
    /// sun until the rays leave the occluders' bounds (widened by the sun's size).</summary>
    private static Aabb RayBounds(BakeLight l, LightmapChart c, Aabb world, BakeQuality q)
    {
        Aabb box = c.Bounds;
        if (!l.IsSun)
        {
            return box.Add(l.Location).Expand(q.LightSize + 1);
        }

        Vec3 d = (-l.Direction).Normalized();
        double length = BakeLight.SunTraceLength;
        for (int axis = 0; axis < 3; axis++)
        {
            double step = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            if (Math.Abs(step) < 1e-9)
            {
                continue;
            }

            // The distance after which the moved box is past the world on this axis.
            double lo = axis == 0 ? box.Min.X : axis == 1 ? box.Min.Y : box.Min.Z, hi = axis == 0 ? box.Max.X : axis == 1 ? box.Max.Y : box.Max.Z;
            double wlo = axis == 0 ? world.Min.X : axis == 1 ? world.Min.Y : world.Min.Z, whi = axis == 0 ? world.Max.X : axis == 1 ? world.Max.Y : world.Max.Z;
            length = Math.Min(length, Math.Max(0, step > 0 ? (whi - lo) / step : (wlo - hi) / step));
        }

        Aabb end = new(box.Min + (d * length), box.Max + (d * length));
        return box.Union(end).Expand((length * Math.Tan(q.SunSize * Math.PI / 360)) + 1);
    }

    /// <summary>Whether the light can add anything to the chart: in reach, on the lit side, in its zone, and of the same
    /// special-lit kind.</summary>
    private static bool Touches(BakeLight l, LightmapChart c)
    {
        if (l.SpecialLit != c.SpecialLit || !l.Reaches(c.Bounds) || (l.OwnZoneOnly && c.Pieces.All(p => p.Zone != l.Zone)))
        {
            return false;
        }

        if (c.TwoSided)
        {
            return true;
        }

        if (l.IsSun)
        {
            return Vec3.Dot(c.Normal, l.Direction) < 0;
        }

        bool usesCosine = l.Effect is 0 or OpenScct.Game.Lighting.ActorLightRules.LE_StaticSpot or OpenScct.Game.Lighting.ActorLightRules.LE_Spotlight
            or OpenScct.Game.Lighting.ActorLightRules.LE_PointAmbient || l.UnknownEffect;
        return !usesCosine || Vec3.Dot(c.Normal, l.Location - c.Origin) > 0;
    }

    // ---- shadows ----------------------------------------------------------------------------------------------------

    /// <summary>Texels whose sample has the back of a surface just above it (inside a brush or a mesh), or null when
    /// none has. Two-sided charts have no inside.</summary>
    private static bool[]? Buried(LightmapChart c, ChartSamples s, TriangleBvh bvh)
    {
        if (c.TwoSided)
        {
            return null;
        }

        var buried = new bool[s.Piece.Length];
        bool any = false;
        Vector3 n = F(c.Normal);
        for (int i = 0; i < buried.Length; i++)
        {
            if (s.Piece[i] >= 0 && bvh.Closest(F(c.World(s.Point[i]) + (c.Normal * Bias)), n, (float)BuryDepth) is { Front: false })
            {
                buried[i] = any = true;
            }
        }

        return any ? buried : null;
    }

    /// <summary>Visibility 0..255 per texel of the chart, or null when every texel the light reaches is fully lit.</summary>
    private static byte[]? Shadow(BakeLight l, LightmapChart c, ChartSamples s, bool[]? buried, TriangleBvh bvh, BakeQuality q, ref long rays)
    {
        int w = c.Width, h = c.Height;
        var vis = new byte[w * h];
        var reached = new bool[w * h];
        // Per shadowed texel, how far (in texels) the penumbra of what blocks it can spread.
        var spread = new float[w * h];
        bool soft = !q.Faithful && (l.IsSun ? q.SunSize > 0 : q.LightSize > 0);
        double sunSpread = Math.Tan(q.SunSize * Math.PI / 360);
        bool anyShadow = false;
        for (int i = 0; i < vis.Length; i++)
        {
            int piece = s.Piece[i];
            if (piece < 0 || (buried?[i] == true && !q.Faithful) || (l.OwnZoneOnly && c.Pieces[piece].Zone != l.Zone))
            {
                continue;
            }

            Vec3 p = c.World(s.Point[i]);
            if (l.Surface(p, c.Normal, c.TwoSided) <= 0)
            {
                continue;
            }

            reached[i] = true;
            rays++;
            double blocker = Blocker(l, p, c.Normal, c.TwoSided, l.Location, -l.Direction, bvh, q.Faithful, farthest: soft);
            if (blocker < 0)
            {
                vis[i] = 255;
                continue;
            }

            anyShadow = true;
            if (soft)
            {
                // The farthest blocker gives a conservative band and keeps the result independent of BVH traversal
                // order (the shadow cache keys geometry, not tree layout). A blocker t from the surface and L - t
                // from a light of radius R casts a penumbra R t / (L - t) wide; the sun's is t tan(half its angle).
                double width = l.IsSun ? blocker * sunSpread : q.LightSize * blocker / Math.Max(1, (l.Location - p).Length - blocker);
                spread[i] = (float)Math.Min(MaxPenumbra, width / c.TexelSize);
            }
        }

        if (q.Faithful)
        {
            return anyShadow ? Filter3(vis, reached, w, h) : null;
        }

        bool[]? thin = q.Samples > 1 ? ThinOccluders(l, c, s, vis, reached, bvh, ref rays) : null;
        if (!anyShadow && thin is null)
        {
            return null;
        }

        if (q.Samples > 1)
        {
            bool[] band = Band(vis, reached, spread, w, h);
            for (int i = 0; i < vis.Length; i++)
            {
                if (band[i] || thin?[i] == true)
                {
                    vis[i] = Supersample(l, c, s, i, bvh, q, ref rays);
                }
            }
        }

        return vis.Where((v, i) => reached[i]).All(v => v == 255) ? null : vis;
    }

    /// <summary>
    /// Lit texels with a corner that does not see the light: something thinner than a texel (a wire, a grille's bar)
    /// that the centre rays slipped past, so the edge band never finds it. A corner ray is shared by the texels round
    /// it, so this costs about one ray a texel. Null when every corner sees the light.
    /// </summary>
    private static bool[]? ThinOccluders(BakeLight l, LightmapChart c, ChartSamples s, byte[] vis, bool[] reached, TriangleBvh bvh, ref long rays)
    {
        int w = c.Width, h = c.Height;
        // Per corner: 0 not traced yet, 1 sees the light, -1 does not.
        var corners = new sbyte[(w + 1) * (h + 1)];
        bool[]? thin = null;
        for (int i = 0; i < vis.Length; i++)
        {
            if (!reached[i] || vis[i] == 0)
            {
                continue;
            }

            int x = i % w, y = i / w;
            ChartPiece piece = c.Pieces[s.Piece[i]];
            for (int k = 0; k < 4; k++)
            {
                int cx = x + (k & 1), cy = y + (k >> 1), ci = (cy * (w + 1)) + cx;
                if (corners[ci] == 0)
                {
                    var at = new TexelPoint(cx, cy);
                    if (!piece.Contains(at))
                    {
                        TexelPoint near = piece.Nearest(at);
                        at = near + ((piece.Centre() - near) * 0.01);
                    }

                    rays++;
                    corners[ci] = Lit(l, c.World(at), c.Normal, c.TwoSided, l.Location, -l.Direction, bvh) ? (sbyte)1 : (sbyte)-1;
                }

                if (corners[ci] < 0)
                {
                    (thin ??= new bool[vis.Length])[i] = true;
                }
            }
        }

        return thin;
    }

    /// <summary>The texels to supersample: around every shadowed texel on a hard edge, as far as its penumbra can reach
    /// (one texel for hard light).</summary>
    private static bool[] Band(byte[] hard, bool[] reached, float[] spread, int w, int h)
    {
        var band = new bool[hard.Length];
        for (int i = 0; i < hard.Length; i++)
        {
            if (!reached[i] || hard[i] != 0 || !NextToLit(hard, reached, w, h, i))
            {
                continue;
            }

            int x = i % w, y = i / w, r = 1 + (int)Math.Ceiling(spread[i]);
            for (int ny = Math.Max(0, y - r); ny <= Math.Min(h - 1, y + r); ny++)
            {
                for (int nx = Math.Max(0, x - r); nx <= Math.Min(w - 1, x + r); nx++)
                {
                    int j = (ny * w) + nx;
                    band[j] |= reached[j];
                }
            }
        }

        return band;
    }

    private static bool NextToLit(byte[] hard, bool[] reached, int w, int h, int i)
    {
        int x = i % w, y = i / w;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && reached[(ny * w) + nx] && hard[(ny * w) + nx] != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The share of a texel's area and of the light's disc that sees each other: an n x n grid over the texel,
    /// each sample aimed at its own point of the light (a scrambled Hammersley set, so neighbours do not band).</summary>
    private static byte Supersample(BakeLight l, LightmapChart c, ChartSamples s, int i, TriangleBvh bvh, BakeQuality q, ref long rays)
    {
        int n = q.Samples, w = c.Width;
        ChartPiece piece = c.Pieces[s.Piece[i]];
        double x0 = i % w, y0 = i / w;
        uint seed = (uint)(c.Key ^ (c.Key >> 32)) ^ ((uint)i * 2654435761u);
        double r1 = (seed & 0xFFFF) / 65536.0, r2 = (seed >> 16) / 65536.0;
        int total = n * n, lit = 0;
        for (int k = 0; k < total; k++)
        {
            var sub = new TexelPoint(x0 + (((k % n) + 0.5) / n), y0 + (((k / n) + 0.5) / n));
            if (!piece.Contains(sub))
            {
                TexelPoint near = piece.Nearest(sub);
                sub = near + ((piece.Centre() - near) * 0.01);
            }

            Vec3 p = c.World(sub);
            double u = Frac((k + 0.5) / total + r1), v = Frac(RadicalInverse((uint)k) + r2);
            Vec3 target = l.Location, sunDir = -l.Direction;
            if (l.IsSun)
            {
                sunDir = Jitter(sunDir, Math.Tan(q.SunSize * Math.PI / 360), u, v);
            }
            else if (q.LightSize > 0)
            {
                target = l.Location + Disc(l.Location - p, q.LightSize, u, v);
            }

            rays++;
            if (Lit(l, p, c.Normal, c.TwoSided, target, sunDir, bvh))
            {
                lit++;
            }
        }

        return (byte)Math.Round(255.0 * lit / total);
    }

    /// <summary>True when nothing blocks the way from the surface point to the light (point and spot lights: to
    /// <paramref name="target"/>; suns: along <paramref name="sunDir"/>, through sky).</summary>
    internal static bool Lit(BakeLight l, Vec3 p, Vec3 n, bool twoSided, Vec3 target, Vec3 sunDir, TriangleBvh bvh) =>
        Blocker(l, p, n, twoSided, target, sunDir, bvh) < 0;

    /// <summary>How far from the surface point something blocks the way to the light, or -1 when nothing does. With
    /// <paramref name="depthSlack"/> (faithful bakes) whatever lies within the original's depth precision of the
    /// surface lets the light through (<see cref="FaithfulDepthSlack"/>).</summary>
    private static double Blocker(BakeLight l, Vec3 p, Vec3 n, bool twoSided, Vec3 target, Vec3 sunDir, TriangleBvh bvh,
        bool depthSlack = false, bool farthest = false)
    {
        Vec3 toLight = l.IsSun ? sunDir : target - p;
        Vec3 side = twoSided && Vec3.Dot(n, toLight) < 0 ? -n : n;
        Vec3 origin = p + (side * Bias);
        if (depthSlack && !l.IsSun)
        {
            origin += (target - origin).Normalized() * (FaithfulDepthSlack * (target - p).LengthSquared);
        }

        if (l.IsSun)
        {
            return farthest
                ? bvh.FarthestBlocker(F(origin), F(sunDir.Normalized()), (float)BakeLight.SunTraceLength, sun: true)
                : bvh.Blocker(F(origin), F(sunDir.Normalized()), (float)BakeLight.SunTraceLength, sun: true);
        }

        Vec3 d = target - origin;
        double len = d.Length;
        return len < 1e-3 ? -1 : farthest
            ? bvh.FarthestBlocker(F(origin), F(d / len), (float)(len - 0.5), sun: false)
            : bvh.Blocker(F(origin), F(d / len), (float)(len - 0.5), sun: false);
    }

    /// <summary>
    /// How near the surface, per uu squared of distance to the light, an occluder can lie and still not shadow a
    /// faithful texel. The original drew its shadow bits as a depth test from the light (FPointLightMapSceneNode,
    /// 0x1119d090), so anything nearer the surface than its depth buffer could tell apart let the light through. The
    /// buffer's format is not decoded: the value was fitted to the stock lightmaps (a sweep from 5e-6 to 3e-5 on SteeE,
    /// FactD and MissD; SteeE's roads running on under raised kerbs, and floors under meshes laid on them, keep their
    /// light there). It is what a 16-bit buffer with a 1 uu near plane would give (steps of d squared / 65536 at
    /// distance d; this is 0.8 of a step). Measured against the stock atlases, it adds no light leaks at any distance
    /// (single-light texels by distance to the light, up to 4000 uu). Suns were drawn orthographically and get no slack.
    /// </summary>
    public const double FaithfulDepthSlack = 1.2e-5;

    /// <summary>What a fully lit texel's filtered shadow byte comes to in faithful mode: the three row sums of
    /// <see cref="Filter3"/> are each rounded down (70 + 114 + 70).</summary>
    public const byte FaithfulLit = 254;

    /// <summary>The original's shadow unpack (FUN_111afee0, tables at 0x11aaf038 built by the FLightingTables
    /// constructor at 0x111af1d0): the 3x3 kernel 24 40 24 / 40 64 40 / 24 40 24 over 320, applied to the hard bits,
    /// with each kernel row's share of 255 rounded down on its own before the three are added, and the edges of the
    /// bitmap repeated. A texel with every neighbour lit stores 254, not 255.</summary>
    internal static byte[] Filter3(byte[] bits, bool[] reached, int w, int h)
    {
        var result = new byte[bits.Length];
        ReadOnlySpan<int> outer = [24, 40, 24];
        ReadOnlySpan<int> middle = [40, 64, 40];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                if (!reached[i])
                {
                    continue;
                }

                int value = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    ReadOnlySpan<int> k = dy == 0 ? middle : outer;
                    int ny = Math.Clamp(y + dy, 0, h - 1), sum = 0;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int j = (ny * w) + Math.Clamp(x + dx, 0, w - 1);
                        // Texels the light does not reach count as lit, as the light's-eye render saw nothing there.
                        if (!reached[j] || bits[j] != 0)
                        {
                            sum += k[dx + 1];
                        }
                    }

                    value += 255 * sum / 320;
                }

                result[i] = (byte)value;
            }
        }

        return result;
    }

    // ---- composition ------------------------------------------------------------------------------------------------

    private static float[] Compose(BakeScene scene, LightmapChart c, ChartSamples s, bool[]? buried, IEnumerable<(BakeLight Light, byte[]? Vis)> lights,
        BakeQuality q, WindowBakeCapture? capture, int chartIndex)
    {
        int count = c.Width * c.Height;
        var rgb = new float[count * 3];
        var world = new Vec3[count];
        for (int i = 0; i < count; i++)
        {
            if (s.Piece[i] < 0 || buried?[i] == true)
            {
                continue;
            }

            world[i] = c.World(s.Point[i]);
            Vec3 a = scene.Ambient(c.Pieces[s.Piece[i]].Zone);
            capture?.Ambient(chartIndex, i, a);
            rgb[3 * i] = (float)a.X;
            rgb[(3 * i) + 1] = (float)a.Y;
            rgb[(3 * i) + 2] = (float)a.Z;
        }

        foreach ((BakeLight l, byte[]? vis) in lights)
        {
            Vec3 colour = l.Colour;
            for (int i = 0; i < count; i++)
            {
                int piece = s.Piece[i];
                if (piece < 0 || buried?[i] == true || (vis is not null && vis[i] == 0) || (l.OwnZoneOnly && c.Pieces[piece].Zone != l.Zone))
                {
                    continue;
                }

                // Faithful: every light went through the shadow unpack, so even an unshadowed texel stores 254 of 255.
                double f = l.Surface(world[i], c.Normal, c.TwoSided) * (vis is not null ? vis[i] : q.Faithful ? FaithfulLit : 255) / 255.0;
                if (f <= 0)
                {
                    continue;
                }

                WindowLightPath? path = q.Shadows ? scene.TransmissionPath?.Invoke(l, world[i]) : null;
                Vec3 transmitted = colour.Scale(path?.Colour ?? (q.Shadows ? scene.Transmission?.Invoke(l, world[i]) ?? Vec3.One : Vec3.One));
                capture?.Light(chartIndex, i, path?.Panes ?? [], colour * (q.Faithful ? Math.Floor(Math.Min(255, 255 * f)) : f));
                if (q.Faithful)
                {
                    // One byte of term per light, through the light's colour, added with saturation.
                    double term = Math.Floor(Math.Min(255, 255 * f));
                    rgb[3 * i] = (float)Math.Min(1, rgb[3 * i] + (Math.Floor(Math.Min(255, term * transmitted.X)) / 255));
                    rgb[(3 * i) + 1] = (float)Math.Min(1, rgb[(3 * i) + 1] + (Math.Floor(Math.Min(255, term * transmitted.Y)) / 255));
                    rgb[(3 * i) + 2] = (float)Math.Min(1, rgb[(3 * i) + 2] + (Math.Floor(Math.Min(255, term * transmitted.Z)) / 255));
                }
                else
                {
                    rgb[3 * i] += (float)(f * transmitted.X);
                    rgb[(3 * i) + 1] += (float)(f * transmitted.Y);
                    rgb[(3 * i) + 2] += (float)(f * transmitted.Z);
                }
            }
        }

        return rgb;
    }

    /// <summary>Fills texels no surface covers (borders, gaps between pieces) and buried texels from their neighbours,
    /// so bilinear sampling at an edge never reads black; stays inside the chart, so charts never bleed into each
    /// other.</summary>
    internal static void Dilate(float[] rgb, ChartSamples s, bool[]? buried, int w, int h)
    {
        var filled = new bool[w * h];
        for (int i = 0; i < filled.Length; i++)
        {
            filled[i] = s.Piece[i] >= 0 && buried?[i] != true;
        }

        var next = new List<(int Index, float R, float G, float B)>();
        for (int pass = 0; pass < w + h; pass++)
        {
            next.Clear();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    if (filled[i])
                    {
                        continue;
                    }

                    float r = 0, g = 0, b = 0;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h || !filled[(ny * w) + nx])
                            {
                                continue;
                            }

                            int j = (ny * w) + nx;
                            (r, g, b, n) = (r + rgb[3 * j], g + rgb[(3 * j) + 1], b + rgb[(3 * j) + 2], n + 1);
                        }
                    }

                    if (n > 0)
                    {
                        next.Add((i, r / n, g / n, b / n));
                    }
                }
            }

            if (next.Count == 0)
            {
                return;
            }

            foreach ((int i, float r, float g, float b) in next)
            {
                (rgb[3 * i], rgb[(3 * i) + 1], rgb[(3 * i) + 2], filled[i]) = (r, g, b, true);
            }
        }
    }

    // ---- sampling helpers -------------------------------------------------------------------------------------------

    public static Vector3 F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    internal static double Frac(double x) => x - Math.Floor(x);

    internal static double RadicalInverse(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10;
    }

    /// <summary>A point on a disc of <paramref name="radius"/> facing <paramref name="axis"/> (concentric mapping of
    /// u, v in 0..1).</summary>
    private static Vec3 Disc(Vec3 axis, double radius, double u, double v)
    {
        (Vec3 a, Vec3 b) = Perpendiculars(axis);
        double x = (2 * u) - 1, y = (2 * v) - 1, r, phi;
        if (x == 0 && y == 0)
        {
            return Vec3.Zero;
        }

        if (Math.Abs(x) > Math.Abs(y))
        {
            (r, phi) = (x, Math.PI / 4 * (y / x));
        }
        else
        {
            (r, phi) = (y, (Math.PI / 2) - (Math.PI / 4 * (x / y)));
        }

        return ((a * Math.Cos(phi)) + (b * Math.Sin(phi))) * (r * radius);
    }

    /// <summary>A direction tilted from <paramref name="dir"/> by up to atan(<paramref name="spread"/>).</summary>
    private static Vec3 Jitter(Vec3 dir, double spread, double u, double v) => (dir.Normalized() + Disc(dir, spread, u, v)).Normalized();

    internal static (Vec3 A, Vec3 B) Perpendiculars(Vec3 axis)
    {
        Vec3 n = axis.Normalized();
        Vec3 a = Math.Abs(n.Z) < 0.9 ? Vec3.Cross(n, Vec3.UnitZ).Normalized() : Vec3.Cross(n, Vec3.UnitX).Normalized();
        return (a, Vec3.Cross(n, a));
    }
}
