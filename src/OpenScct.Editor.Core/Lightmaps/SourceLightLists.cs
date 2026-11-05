using System.Numerics;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Lighting;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// Which lights reach each lightmap of an imported map, the way the original bake chose them, read from the source
/// .sdc. The original never lights a surface with a light its BSP leaves do not list, so a texel past a wall (a
/// lightmap's border ring, which the game's filtering reads) takes no light from the room behind it.
/// <para>Decoded from the stock editor (UModel::Illuminate, 0x111a0d4b on): CalculateNodeLights (0x1119f880) walks the
/// tree from the root. A node's front list is the run of its front leaf (CalculateLeafLights, 0x1119f510), or with a
/// front child, every light listed under that child; the back list likewise. Each node on the node's plane
/// (iPlane chain) takes, through CalculateSurfaceLights (0x1119f5d0), the lights of the list on the side it faces
/// (both lists when its surf is two-sided, PF_TwoSided 0x100) that pass these tests, into its surf's list: the same
/// bSpecialLit as the surf (PF_SpecialLit 0x100000); a sun lit from the side it faces when its zone is the node's zone
/// on that side or the light is in no zone (back side: two-sided surfs only); any other light within its WorldLightRadius
/// of the node's bounding sphere and, unless two-sided, in front of the node's plane. Each lightmap bakes the lights of
/// its surf's list.</para>
/// <para>What a file keeps of this: the leaf runs (UModel Leaves and Lights) hold only the lights the game lights at
/// run time, because the save rebuilds them without the lights cooking drops (TestLights, 0x1101dcb7); the static
/// lights' places in the runs the bake used are lost. An editor copy also stores each lightmap's own list (FLightMap),
/// static lights included. So a light still as the file saved it (place, radius, effect, direction, bSpecialLit, and
/// the properties that decide whether it bakes) takes the stored lists of an editor copy; any other light listed in the
/// runs of the leaf it stands in takes the lists worked out from the runs as above (a cooked copy's lights, or a moved
/// run-time light). A light neither covers (new, edited, moved, or switched to baking) is flooded through the leaves of
/// the map's world rebuilt from its own brushes, as an authored map's lights are (<see cref="LeafLightLists"/>), and each
/// surf takes the lights listed in the leaves just in front of its polygons; only without that world (a project with no
/// baked brushes) is such a light not limited: it reaches every surface in its radius.</para>
/// </summary>
public sealed class SourceLightLists
{
    private const uint TwoSided = 0x100;
    private const uint SpecialLit = 0x100000;

    /// <summary>The properties that decide where a light is listed, and whether it bakes at all (a light that did not
    /// bake when the file was saved is in none of its stored lists).</summary>
    private static readonly string[] s_listedBy =
        ["LightRadius", "LightEffect", "bSpecialLit", "LightType", "bDynamicLight", "bApplyToStaticLighting", "bMovable", "bStatic"];

    /// <summary>How far in front of a polygon its surf's leaves are looked for, uu.</summary>
    private const double LeafProbe = 1;

    private readonly UModel _model;
    private readonly string?[] _lightNames;
    private readonly Dictionary<int, string> _refNames;
    private readonly Dictionary<LightmapChart, int[]> _chartSurfs;
    private readonly Dictionary<string, SvJsonObject> _saved;

    private SourceLightLists(UModel model, string?[] lightNames, Dictionary<int, string> refNames, Dictionary<LightmapChart, int[]> chartSurfs,
        Dictionary<string, SvJsonObject> saved, SdcLayout layout)
    {
        _model = model;
        _lightNames = lightNames;
        _refNames = refNames;
        _chartSurfs = chartSurfs;
        _saved = saved;
        Layout = layout;
    }

    public SdcLayout Layout { get; }

    /// <summary>The source lightmaps (charts) matched to the surfs of the source BSP.</summary>
    public int MatchedCharts => _chartSurfs.Count;

    /// <summary>Whether the file stores each lightmap's own list (an editor copy).</summary>
    public bool HasStoredLists => _model.LightMapList.Count > 0;

    /// <summary>Reads the world model of <paramref name="sdc"/> and its lights, and matches its surfs to
    /// <paramref name="world"/>'s charts. Throws when the package cannot be read.</summary>
    public static SourceLightLists Read(string sdc, ConvertedWorld world)
    {
        UPackage pkg = UPackage.Load(sdc);
        ULevel level = ULevelReader.Read(pkg, ULevelReader.Find(pkg));
        if (level.WorldModel <= 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(sdc)} has no world model");
        }

        SdcLayout layout = SdcLayoutDetector.Detect(pkg);
        UModel model = new UModelReader(layout).Read(pkg, pkg.Export(level.WorldModel), UModelDetail.Lighting);
        string?[] names = model.LightList.Select(r => r == 0 ? null : pkg.ObjectName(r)).ToArray();
        var refNames = new Dictionary<int, string>();
        foreach (int r in model.LightList.Concat(model.LightMapList.SelectMany(lm => lm.Lights)).Where(r => r != 0))
        {
            refNames.TryAdd(r, pkg.ObjectName(r));
        }

        // The saved properties of every actor, read by their tags (Location and Rotation need no class data), so a light
        // can be told unchanged, a light no list holds included.
        var saved = new Dictionary<string, SvJsonObject>(StringComparer.OrdinalIgnoreCase);
        var reader = new UPropertyReader(null, (_, _) => null);
        foreach (UExport e in pkg.Exports.Where(e => (e.Flags & UExport.HasStack) != 0 && e.Size > 0))
        {
            try
            {
                UReader data = pkg.Reader(e);
                UPropertyReader.SkipStateFrame(data, e.Flags);
                saved.TryAdd(e.Name, reader.ReadObject(data, null, e.Name));
            }
            catch (InvalidDataException)
            {
                // Unreadable by tags alone: the actor is not taken as unchanged.
            }
        }

        return new SourceLightLists(model, names, refNames, MatchCharts(model, world.Charts), saved, layout);
    }

    /// <summary>Each chart's surfs: a chart holds the surf of every node whose polygon centre lies on it.</summary>
    private static Dictionary<LightmapChart, int[]> MatchCharts(UModel model, IReadOnlyList<LightmapChart> charts)
    {
        const double Cell = 512;
        var grid = new Dictionary<(long, long, long), List<LightmapChart>>();
        foreach (LightmapChart c in charts)
        {
            Aabb b = c.Bounds;
            for (long x = (long)Math.Floor(b.Min.X / Cell); x <= (long)Math.Floor(b.Max.X / Cell); x++)
            {
                for (long y = (long)Math.Floor(b.Min.Y / Cell); y <= (long)Math.Floor(b.Max.Y / Cell); y++)
                {
                    for (long z = (long)Math.Floor(b.Min.Z / Cell); z <= (long)Math.Floor(b.Max.Z / Cell); z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out List<LightmapChart>? list))
                        {
                            grid[(x, y, z)] = list = new List<LightmapChart>();
                        }

                        list.Add(c);
                    }
                }
            }
        }

        var surfs = new Dictionary<LightmapChart, HashSet<int>>(ReferenceEqualityComparer.Instance);
        foreach (FBspNode node in model.NodeList)
        {
            if (node.Surf < 0 || node.Surf >= model.Surfs.Count || node.NumVertices < 3 || node.VertPool + node.NumVertices > model.VertPoints.Count)
            {
                continue;
            }

            Vec3 sum = Vec3.Zero;
            for (int i = 0; i < node.NumVertices; i++)
            {
                Vector3 p = model.PointList[model.VertPoints[node.VertPool + i]];
                sum += new Vec3(p.X, p.Y, p.Z);
            }

            Vec3 centre = sum * (1.0 / node.NumVertices), normal = new(node.Plane.X, node.Plane.Y, node.Plane.Z);
            var key = ((long)Math.Floor(centre.X / Cell), (long)Math.Floor(centre.Y / Cell), (long)Math.Floor(centre.Z / Cell));
            if (!grid.TryGetValue(key, out List<LightmapChart>? near))
            {
                continue;
            }

            foreach (LightmapChart c in near)
            {
                if (Vec3.Dot(c.Normal, normal) < 0.99 || Math.Abs(Vec3.Dot(centre - c.Origin, c.Normal)) > 1 || !c.Bounds.Contains(centre))
                {
                    continue;
                }

                TexelPoint t = c.TexelOf(centre);
                if (c.Pieces.Any(piece => piece.Contains(t)))
                {
                    if (!surfs.TryGetValue(c, out HashSet<int>? set))
                    {
                        surfs[c] = set = new HashSet<int>();
                    }

                    set.Add(node.Surf);
                }
            }
        }

        var result = new Dictionary<LightmapChart, int[]>(ReferenceEqualityComparer.Instance);
        foreach ((LightmapChart chart, HashSet<int> set) in surfs)
        {
            result[chart] = set.Order().ToArray();
        }

        return result;
    }

    /// <summary>The leaf a point is in, or -1 in solid: down the nodes from the root, front when the point is past the
    /// plane, as the stock editor finds a light's leaf (FEditorVisibility::ActorVisibility, 0x1101b050).</summary>
    public int LeafAt(Vec3 p)
    {
        IReadOnlyList<FBspNode> nodes = _model.NodeList;
        if (nodes.Count == 0)
        {
            return -1;
        }

        int i = 0, leaf = -1;
        while (i >= 0)
        {
            FBspNode n = nodes[i];
            bool front = (n.Plane.X * p.X) + (n.Plane.Y * p.Y) + (n.Plane.Z * p.Z) - n.Plane.W > 0;
            leaf = front ? n.FrontLeaf : n.BackLeaf;
            i = front ? n.Front : n.Back;
        }

        return leaf;
    }

    /// <summary>The names in a leaf's run.</summary>
    public IEnumerable<string> LeafLights(int leaf)
    {
        if (leaf < 0 || leaf >= _model.LeafList.Count || _model.LeafList[leaf].Permeating < 0)
        {
            yield break;
        }

        for (int i = _model.LeafList[leaf].Permeating; i < _lightNames.Length && _lightNames[i] is { } name; i++)
        {
            yield return name;
        }
    }

    /// <summary>
    /// The light lists for a bake with <paramref name="lights"/>: which of them each matched chart takes, by the rules
    /// in the class summary. <paramref name="wholeWorld"/> gives the leaf lists of the map's world rebuilt from its
    /// brushes; it is asked for only when some baked light needs it.
    /// </summary>
    public LightLists For(IReadOnlyList<BakeLight> lights, Func<LeafLightLists?>? wholeWorld = null)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lights.Count; i++)
        {
            index.TryAdd(lights[i].Id, i);
        }

        int words = (lights.Count + 63) / 64;
        IReadOnlyList<(int Zone, int Permeating)> leaves = _model.LeafList;
        var leafBits = new ulong[leaves.Count][];
        for (int leaf = 0; leaf < leaves.Count; leaf++)
        {
            var bits = new ulong[words];
            foreach (string name in LeafLights(leaf))
            {
                if (index.TryGetValue(name, out int l))
                {
                    bits[l >> 6] |= 1UL << (l & 63);
                }
            }

            leafBits[leaf] = bits;
        }

        var held = new LightHold[lights.Count];
        var zones = new int[lights.Count];
        for (int l = 0; l < lights.Count; l++)
        {
            int leaf = LeafAt(lights[l].Location);
            zones[l] = leaf >= 0 ? leaves[leaf].Zone : 0;
            held[l] = HasStoredLists && Unchanged(lights[l]) ? LightHold.Stored
                : leaf >= 0 && (leafBits[leaf][l >> 6] & (1UL << (l & 63))) != 0 ? LightHold.Runs
                : LightHold.Free;
        }

        LeafLightLists? flood = null;
        if (wholeWorld is not null && Enumerable.Range(0, lights.Count).Any(l => lights[l].Baked && held[l] == LightHold.Free))
        {
            flood = wholeWorld();
        }

        ulong[]?[]? floodSurfs = null;
        if (flood is not null)
        {
            for (int l = 0; l < lights.Count; l++)
            {
                if (held[l] == LightHold.Free)
                {
                    held[l] = LightHold.Flood;
                }
            }

            floodSurfs = FloodSurfs(flood, lights, zones, words);
        }

        var surfBits = new ulong[_model.Surfs.Count][];
        if (_model.NodeList.Count > 0)
        {
            NodeLights(0, lights, leafBits, zones, surfBits, words);
        }

        var storedBits = new ulong[_model.Surfs.Count][];
        foreach (FLightMapRecord lm in _model.LightMapList)
        {
            if (lm.Surf < 0 || lm.Surf >= storedBits.Length)
            {
                continue;
            }

            ulong[] bits = storedBits[lm.Surf] ??= new ulong[words];
            foreach (int r in lm.Lights)
            {
                if (_refNames.TryGetValue(r, out string? name) && index.TryGetValue(name, out int l))
                {
                    bits[l >> 6] |= 1UL << (l & 63);
                }
            }
        }

        var charts = new Dictionary<LightmapChart, ChartLists>(ReferenceEqualityComparer.Instance);
        foreach ((LightmapChart chart, int[] surfs) in _chartSurfs)
        {
            var runs = new ulong[words];
            var stored = new ulong[words];
            ulong[]? flooded = floodSurfs is null ? null : new ulong[words];
            foreach (int s in surfs)
            {
                if (surfBits[s] is { } sb)
                {
                    Or(runs, sb);
                }

                if (storedBits[s] is { } st)
                {
                    Or(stored, st);
                }

                if (flooded is not null && floodSurfs![s] is { } fs)
                {
                    Or(flooded, fs);
                }
            }

            charts[chart] = new ChartLists(runs, stored, flooded);
        }

        return new LightLists(lights, index, held, charts, surfBits, flood, floodSurfs);
    }

    /// <summary>
    /// Each matched surf's lights by the whole-world leaf lists: for each of its nodes, the lights listed in the empty
    /// leaves just in front of the centre and the inner corners of its polygon (behind it too when two-sided) that pass
    /// the node's CalculateSurfaceLights tests. A node whose probes all land in solid space (the rebuilt world rounds it
    /// away) takes every light that passes those tests.
    /// </summary>
    private ulong[]?[] FloodSurfs(LeafLightLists flood, IReadOnlyList<BakeLight> lights, int[] zones, int words)
    {
        var nodesOf = new Dictionary<int, List<int>>();
        for (int i = 0; i < _model.NodeList.Count; i++)
        {
            int surf = _model.NodeList[i].Surf;
            if (surf >= 0)
            {
                if (!nodesOf.TryGetValue(surf, out List<int>? list))
                {
                    nodesOf[surf] = list = new List<int>();
                }

                list.Add(i);
            }
        }

        var result = new ulong[]?[_model.Surfs.Count];
        foreach (int surf in _chartSurfs.Values.SelectMany(s => s).Distinct())
        {
            uint flags = _model.Surfs[surf].PolyFlags;
            bool twoSided = (flags & TwoSided) != 0, specialLit = (flags & SpecialLit) != 0;
            var bits = new ulong[words];
            foreach (int n in nodesOf.GetValueOrDefault(surf) ?? [])
            {
                FBspNode node = _model.NodeList[n];
                var normal = new Vec3(node.Plane.X, node.Plane.Y, node.Plane.Z);
                var leaves = new HashSet<int>();
                foreach (Vec3 probe in Probes(node, twoSided))
                {
                    int leaf = flood.EmptyLeafAt(probe);
                    if (leaf >= 0)
                    {
                        leaves.Add(leaf);
                    }
                }

                for (int l = 0; l < lights.Count; l++)
                {
                    BakeLight light = lights[l];
                    if (light.SpecialLit == specialLit && (leaves.Count == 0 || leaves.Any(leaf => flood.Lists(leaf, light))) &&
                        Takes(node, light, zones[l], normal, twoSided))
                    {
                        bits[l >> 6] |= 1UL << (l & 63);
                    }
                }
            }

            result[surf] = bits;
        }

        return result;
    }

    /// <summary>Points just in front of a node's polygon: its centre and its corners pulled a quarter of the way in.</summary>
    private IEnumerable<Vec3> Probes(FBspNode node, bool bothSides)
    {
        if (node.NumVertices < 3 || node.VertPool + node.NumVertices > _model.VertPoints.Count)
        {
            yield break;
        }

        var corners = new Vec3[node.NumVertices];
        Vec3 centre = Vec3.Zero;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 p = _model.PointList[_model.VertPoints[node.VertPool + i]];
            corners[i] = new Vec3(p.X, p.Y, p.Z);
            centre += corners[i];
        }

        centre *= 1.0 / corners.Length;
        Vec3 off = new Vec3(node.Plane.X, node.Plane.Y, node.Plane.Z) * LeafProbe;
        foreach (Vec3 at in corners.Select(c => c + ((centre - c) * 0.25)).Prepend(centre))
        {
            yield return at + off;
            if (bothSides)
            {
                yield return at - off;
            }
        }
    }

    /// <summary>True when a light still stands, reaches and shines as the file saved it, so the file's lists hold for it.</summary>
    private bool Unchanged(BakeLight light)
    {
        if (light.Actor is not { } a || !_saved.TryGetValue(light.Id, out SvJsonObject? saved))
        {
            return false;
        }

        Vec3 at = PropValues.TryVector(saved["Location"], out Vec3 v) ? v : Vec3.Zero;
        Rot rotation = PropValues.TryRotator(saved["Rotation"], out Rot r) ? r : Rot.Zero;
        if ((a.Location - at).Length > 0.01 || (light.IsSun && a.Rotation != rotation))
        {
            return false;
        }

        return s_listedBy.All(key => Same(a.Props[key], saved[key]));
    }

    /// <summary>Two property values as stored (absent in both: the class default in both).</summary>
    private static bool Same(SvJsonNode? mine, SvJsonNode? theirs) => (mine, theirs) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (SvJsonNumber, SvJsonNumber) => Math.Abs(PropValues.Num(mine) - PropValues.Num(theirs)) <= 1e-4,
        _ => mine.Kind == theirs.Kind && mine.ToString() == theirs.ToString(),
    };

    /// <summary>
    /// CalculateNodeLights over the whole tree, without recursion (deep trees): each node's front and back lists are
    /// built from its children's, its plane's nodes take their surfs' lights from them, and the union goes up.
    /// </summary>
    private void NodeLights(int root, IReadOnlyList<BakeLight> lights, ulong[][] leafBits, int[] zones, ulong[]?[] surfBits, int words)
    {
        IReadOnlyList<FBspNode> nodes = _model.NodeList;
        var below = new ulong[nodes.Count][]; // every light listed under a node, once it is done
        var stack = new Stack<(int Node, bool Expanded)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            (int i, bool expanded) = stack.Pop();
            FBspNode n = nodes[i];
            if (!expanded)
            {
                stack.Push((i, true));
                if (n.FrontLeaf < 0 && n.Front >= 0)
                {
                    stack.Push((n.Front, false));
                }

                if (n.BackLeaf < 0 && n.Back >= 0)
                {
                    stack.Push((n.Back, false));
                }

                continue;
            }

            ulong[] front = Side(n.FrontLeaf, n.Front), back = Side(n.BackLeaf, n.Back);
            for (int k = i; k >= 0 && k < nodes.Count; k = nodes[k].Coplanar)
            {
                FBspNode c = nodes[k];
                if (c.Surf < 0 || c.Surf >= _model.Surfs.Count)
                {
                    continue;
                }

                bool twoSided = (_model.Surfs[c.Surf].PolyFlags & TwoSided) != 0;
                bool same = c.Plane.X == n.Plane.X && c.Plane.Y == n.Plane.Y && c.Plane.Z == n.Plane.Z;
                SurfaceLights(c, same ? front : back, lights, zones, surfBits, words);
                if (twoSided)
                {
                    SurfaceLights(c, same ? back : front, lights, zones, surfBits, words);
                }
            }

            var union = new ulong[words];
            Or(union, front);
            Or(union, back);
            below[i] = union;
            if (n.FrontLeaf < 0 && n.Front >= 0)
            {
                below[n.Front] = null!;
            }

            if (n.BackLeaf < 0 && n.Back >= 0)
            {
                below[n.Back] = null!;
            }

            ulong[] Side(int leaf, int child) =>
                leaf >= 0 && leaf < leafBits.Length ? leafBits[leaf] : child >= 0 && below[child] is { } b ? b : new ulong[words];
        }
    }

    /// <summary>CalculateSurfaceLights: the lights of <paramref name="list"/> that node <paramref name="c"/>'s surf takes.</summary>
    private void SurfaceLights(FBspNode c, ulong[] list, IReadOnlyList<BakeLight> lights, int[] zones, ulong[]?[] surfBits, int words)
    {
        uint flags = _model.Surfs[c.Surf].PolyFlags;
        bool twoSided = (flags & TwoSided) != 0, specialLit = (flags & SpecialLit) != 0;
        var normal = new Vec3(c.Plane.X, c.Plane.Y, c.Plane.Z);
        for (int w = 0; w < list.Length; w++)
        {
            for (ulong bits = list[w]; bits != 0; bits &= bits - 1)
            {
                int l = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                BakeLight light = lights[l];
                if (light.SpecialLit != specialLit || !Takes(c, light, zones[l], normal, twoSided))
                {
                    continue;
                }

                ulong[] s = surfBits[c.Surf] ??= new ulong[words];
                s[w] |= 1UL << (l & 63);
            }
        }
    }

    private static bool Takes(FBspNode c, BakeLight light, int zone, Vec3 normal, bool twoSided)
    {
        if (light.Effect == ActorLightRules.LE_Sunlight)
        {
            double facing = -Vec3.Dot(light.Direction, normal);
            if ((zone == 0 || c.FrontZone == zone) && facing > 0)
            {
                return true;
            }

            return (zone == 0 || c.BackZone == zone) && facing < 0 && twoSided;
        }

        Vec3 centre = new(c.Sphere.X, c.Sphere.Y, c.Sphere.Z);
        double reach = light.Radius + c.Sphere.W;
        if ((light.Location - centre).LengthSquared >= reach * reach)
        {
            return false;
        }

        return twoSided || Vec3.Dot(normal, light.Location) - c.Plane.W > 0;
    }

    private static void Or(ulong[] into, ulong[] from)
    {
        for (int i = 0; i < into.Length; i++)
        {
            into[i] |= from[i];
        }
    }

    /// <summary>
    /// A check of the decode on an editor copy: how its stored lightmap lists compare with the lists worked out from the
    /// leaf runs, over the baked lights the runs hold. Returns the lightmaps compared, those that agree, and the
    /// light-lightmap pairs only the file has and only ours have. Null for a cooked copy (it stores no lightmap lists).
    /// </summary>
    public (int Lightmaps, int Agree, int OnlyStored, int OnlyOurs)? CompareWithStored(LightLists lists)
    {
        if (!HasStoredLists)
        {
            return null;
        }

        var inRuns = new HashSet<string>(_lightNames.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        int agree = 0, onlyStored = 0, onlyOurs = 0, compared = 0;
        foreach (FLightMapRecord lm in _model.LightMapList)
        {
            if (lm.Surf < 0 || lm.Surf >= _model.Surfs.Count)
            {
                continue;
            }

            compared++;
            var stored = new HashSet<int>();
            foreach (int r in lm.Lights)
            {
                if (_refNames.TryGetValue(r, out string? name) && inRuns.Contains(name) && lists.Index(name) is int l && lists.Lights[l].Baked)
                {
                    stored.Add(l);
                }
            }

            HashSet<int> ours = lists.OfSurf(lm.Surf).Where(l => lists.Lights[l].Baked).ToHashSet();
            onlyStored += stored.Count(l => !ours.Contains(l));
            onlyOurs += ours.Count(l => !stored.Contains(l));
            if (stored.SetEquals(ours))
            {
                agree++;
            }
        }

        return (compared, agree, onlyStored, onlyOurs);
    }

    /// <summary>
    /// A check of the whole-world flood on an editor copy: how its stored lightmap lists compare with the lists the
    /// flood gives, over the baked lights the stored lists hold (those unchanged since the save). Null without a flood.
    /// </summary>
    public (int Lightmaps, int Agree, int OnlyStored, int OnlyOurs)? CompareFloodWithStored(LightLists lists)
    {
        if (!HasStoredLists || lists.FloodSurfs is not { } flood)
        {
            return null;
        }

        int agree = 0, onlyStored = 0, onlyOurs = 0, compared = 0;
        foreach (FLightMapRecord lm in _model.LightMapList)
        {
            if (lm.Surf < 0 || lm.Surf >= flood.Length || flood[lm.Surf] is not { } bits)
            {
                continue;
            }

            compared++;
            var stored = new HashSet<int>();
            foreach (int r in lm.Lights)
            {
                if (_refNames.TryGetValue(r, out string? name) && lists.Index(name) is int l && lists.IsStored(l))
                {
                    stored.Add(l);
                }
            }

            HashSet<int> ours = Enumerable.Range(0, lists.Lights.Count)
                .Where(l => lists.IsStored(l) && (bits[l >> 6] & (1UL << (l & 63))) != 0).ToHashSet();
            onlyStored += stored.Count(l => !ours.Contains(l));
            onlyOurs += ours.Count(l => !stored.Contains(l));
            if (stored.SetEquals(ours))
            {
                agree++;
            }
        }

        return (compared, agree, onlyStored, onlyOurs);
    }
}

/// <summary>A chart's lists by each way of holding a light (<see cref="ChartLists.Flood"/> only when some light needs it).</summary>
internal sealed record ChartLists(ulong[] Runs, ulong[] Stored, ulong[]? Flood);

/// <summary>How a light is held to the lists of a bake.</summary>
internal enum LightHold
{
    /// <summary>Not held: it may light any surface in reach.</summary>
    Free,

    /// <summary>By the lists worked out from the leaf runs.</summary>
    Runs,

    /// <summary>By the lightmap lists an editor copy stores.</summary>
    Stored,

    /// <summary>By the leaf lists of the map's world rebuilt from its brushes.</summary>
    Flood,
}

/// <summary>The light lists of one bake (<see cref="SourceLightLists.For"/>).</summary>
public sealed class LightLists
{
    private readonly Dictionary<string, int> _index;
    private readonly LightHold[] _held;
    private readonly Dictionary<LightmapChart, ChartLists> _charts;
    private readonly ulong[]?[] _surfs;
    private readonly Dictionary<BakeLight, int> _byLight;

    internal LightLists(IReadOnlyList<BakeLight> lights, Dictionary<string, int> index, LightHold[] held,
        Dictionary<LightmapChart, ChartLists> charts, ulong[]?[] surfs, LeafLightLists? wholeWorld, ulong[]?[]? floodSurfs)
    {
        Lights = lights;
        _index = index;
        _held = held;
        _charts = charts;
        _surfs = surfs;
        WholeWorld = wholeWorld;
        FloodSurfs = floodSurfs;
        _byLight = new Dictionary<BakeLight, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < lights.Count; i++)
        {
            _byLight.TryAdd(lights[i], i);
        }
    }

    public IReadOnlyList<BakeLight> Lights { get; }

    /// <summary>Baked lights held to the stored lightmap lists of an editor copy (unchanged since the save).</summary>
    public int Stored => Count(LightHold.Stored);

    /// <summary>Baked lights held to the lists worked out from the leaf runs.</summary>
    public int Runs => Count(LightHold.Runs);

    /// <summary>Baked lights held to the leaf lists of the world rebuilt from the map's brushes.</summary>
    public int Flood => Count(LightHold.Flood);

    /// <summary>Baked lights no list holds.</summary>
    public int Free => Count(LightHold.Free);

    /// <summary>The baked lights no list holds.</summary>
    public IEnumerable<BakeLight> FreeLights =>
        Enumerable.Range(0, Lights.Count).Where(i => Lights[i].Baked && _held[i] == LightHold.Free).Select(i => Lights[i]);

    /// <summary>The leaf lists of the world rebuilt from the map's brushes, when some light needed them.</summary>
    public LeafLightLists? WholeWorld { get; }

    /// <summary>The baked lights held to <see cref="WholeWorld"/>.</summary>
    public IEnumerable<BakeLight> Flooded =>
        Enumerable.Range(0, Lights.Count).Where(i => Lights[i].Baked && _held[i] == LightHold.Flood).Select(i => Lights[i]);

    /// <summary>Each surf's lights by <see cref="WholeWorld"/> (light indices as bits), for the surfs a chart holds.</summary>
    internal ulong[]?[]? FloodSurfs { get; }

    /// <summary>Whether light <paramref name="l"/> bakes and is held to the stored lists.</summary>
    internal bool IsStored(int l) => Lights[l].Baked && _held[l] == LightHold.Stored;

    private int Count(LightHold hold) => Enumerable.Range(0, Lights.Count).Count(i => Lights[i].Baked && _held[i] == hold);

    public int? Index(string id) => _index.TryGetValue(id, out int i) ? i : null;

    /// <summary>The light indices a surf's list holds, as worked out from the leaf runs.</summary>
    public IEnumerable<int> OfSurf(int surf)
    {
        if (surf < 0 || surf >= _surfs.Length || _surfs[surf] is not { } bits)
        {
            yield break;
        }

        for (int w = 0; w < bits.Length; w++)
        {
            for (ulong b = bits[w]; b != 0; b &= b - 1)
            {
                yield return (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(b);
            }
        }
    }

    /// <summary>Whether a light may light a chart: always for a chart with no list or a light no list holds.</summary>
    public bool Admits(LightmapChart chart, BakeLight light)
    {
        if (!_byLight.TryGetValue(light, out int l) || _held[l] == LightHold.Free || !_charts.TryGetValue(chart, out ChartLists? lists))
        {
            return true;
        }

        ulong[]? list = _held[l] switch
        {
            LightHold.Stored => lists.Stored,
            LightHold.Runs => lists.Runs,
            _ => lists.Flood,
        };
        return list is null || (list[l >> 6] & (1UL << (l & 63))) != 0;
    }
}
