using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Lighting;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// The light lists of an authored map's BSP leaves, made the way the stock editor makes them before it bakes, and the
/// lights each lightmap may take from them: a surface takes only the lights listed in the empty leaves it borders
/// (on its front, and on its back too when two-sided), so light never reaches a surface through a wall, not even the
/// border texels of its lightmap that lie past the wall's foot.
/// <para>FEditorVisibility::ActorVisibility (0x1101b050) lists a light in the leaf it stands in, then floods it from
/// leaf to leaf through the leaf portals: through each portal of the leaf that faces away from the light and lies
/// within its radius, into the leaf behind it (not straight back to the leaf it came from), with the portal clipped to
/// the light's view through the portal it came in by (the planes through the light and each edge of that portal, while
/// the clipped portal has fewer than 14 corners); a portal clipped away stops the flood there. A sun (LightEffect 19)
/// is listed in every leaf of its own zone instead. A light standing in solid space is listed nowhere, so the original
/// lights nothing with it, a sun included (the solid test comes first, 0x1101b0f9). Zone portals do not stop the flood
/// (bAffectOwnZoneOnly does, elsewhere). The stock editor keeps no set of leaves already reached: a leaf reached again by
/// another path floods on through its own clipped view, so this walk does the same, up to a guard.</para>
/// <para>The stock bake then gives each surface the lights of the leaves under its BSP node's two sides
/// (CalculateNodeLights, 0x1119f880; see <see cref="SourceLightLists"/>), a superset that depends on the shape of its
/// tree. This map's tree is the editor's own, so a surface takes the lights of the leaves it borders instead, which is
/// what that superset is there to cover.</para>
/// </summary>
public sealed class LeafLightLists
{
    /// <summary>The stock editor stops clipping a portal once it has this many corners (FPoly holds 16).</summary>
    private const int MaxClipCorners = 14;

    /// <summary>A guard against a flood that keeps going round: leaf steps per light, and portals deep.</summary>
    private const int MaxSteps = 200_000;

    private const int MaxDepth = 256;

    private readonly BspTree _tree;
    private readonly ulong[][] _leafBits;
    private readonly Dictionary<BakeLight, int> _index = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<Poly, ulong[]> _faces = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<LightmapChart, ulong[]> _charts = new();
    private readonly int _words;

    private LeafLightLists(BspTree tree, int words)
    {
        _tree = tree;
        _words = words;
        _leafBits = new ulong[tree.Leaves.Count][];
        for (int i = 0; i < _leafBits.Length; i++)
        {
            _leafBits[i] = new ulong[words];
        }
    }

    /// <summary>The baked lights that stand in solid space, which light nothing.</summary>
    public List<BakeLight> InSolid { get; } = new();

    /// <summary>The baked lights whose flood the guard stopped before it was done, so they may miss leaves.</summary>
    public List<BakeLight> Cut { get; } = new();

    /// <summary>Leaf entries made (a light listed in a leaf), summed over the lights.</summary>
    public long Listed { get; private set; }

    /// <summary>The most leaf steps one light's flood took.</summary>
    public int MostSteps { get; private set; }

    /// <summary>Floods every baked light of <paramref name="lights"/> through <paramref name="world"/>'s leaves.</summary>
    public static LeafLightLists Build(WorldModel world, IReadOnlyList<BakeLight> lights)
    {
        List<BakeLight> baked = lights.Where(l => l.Baked).ToList();
        var lists = new LeafLightLists(world.Tree, (baked.Count + 63) / 64);
        for (int i = 0; i < baked.Count; i++)
        {
            lists._index[baked[i]] = i;
        }

        var portals = new List<LeafPortal>[world.Tree.Leaves.Count];
        foreach (LeafPortal p in world.Zones.LeafPortals)
        {
            (portals[p.From] ??= new List<LeafPortal>()).Add(p);
        }

        var perLight = new (List<int> Leaves, int Steps, bool Cut)[baked.Count];
        Parallel.For(0, baked.Count, i => perLight[i] = Flood(world.Tree, portals, baked[i]));
        for (int i = 0; i < baked.Count; i++)
        {
            (List<int> leaves, int steps, bool cut) = perLight[i];
            if (leaves.Count == 0)
            {
                lists.InSolid.Add(baked[i]);
            }

            if (cut)
            {
                lists.Cut.Add(baked[i]);
            }

            foreach (int leaf in leaves)
            {
                lists._leafBits[leaf][i >> 6] |= 1UL << (i & 63);
            }

            lists.Listed += leaves.Count;
            lists.MostSteps = Math.Max(lists.MostSteps, steps);
        }

        return lists;
    }

    /// <summary>The empty leaf holding <paramref name="p"/>, or -1 in solid space.</summary>
    public int EmptyLeafAt(Vec3 p)
    {
        BspLeaf leaf = _tree.LeafAt(p);
        return leaf.Empty ? leaf.Index : -1;
    }

    /// <summary>Whether <paramref name="leaf"/>'s list holds <paramref name="light"/> (never a light these lists do not cover).</summary>
    public bool Lists(int leaf, BakeLight light) =>
        _index.TryGetValue(light, out int l) && (_leafBits[leaf][l >> 6] & (1UL << (l & 63))) != 0;

    /// <summary>The leaves one light is listed in, the leaf steps its flood took, and whether the guard stopped it.</summary>
    private static (List<int> Leaves, int Steps, bool Cut) Flood(BspTree tree, List<LeafPortal>?[] portals, BakeLight light)
    {
        var listed = new HashSet<int>();
        // The solid test comes before the sun's (ActorVisibility returns at once for a light in solid space).
        BspLeaf start = tree.LeafAt(light.Location);
        if (!start.Empty)
        {
            return ([], 0, false);
        }

        if (light.Effect == ActorLightRules.LE_Sunlight)
        {
            foreach (BspLeaf leaf in tree.Leaves)
            {
                if (leaf.Empty && leaf.Zone == start.Zone)
                {
                    listed.Add(leaf.Index);
                }
            }

            return (listed.ToList(), 0, false);
        }

        int steps = 0;
        bool cut = false;
        Visit(start.Index, -1, null, 0);
        return (listed.ToList(), steps, cut);

        void Visit(int leaf, int from, Vec3[]? through, int depth)
        {
            listed.Add(leaf);
            if (portals[leaf] is not { } outs)
            {
                return;
            }

            if (++steps > MaxSteps || depth > MaxDepth)
            {
                cut = true;
                return;
            }

            foreach (LeafPortal p in outs)
            {
                double d = p.Piece.Plane.Dist(light.Location);
                // The light must be behind the portal (on this leaf's side) and within its radius of it.
                if (d >= 0 || d <= -light.Radius || p.To == from)
                {
                    continue;
                }

                Vec3[]? seen = p.Piece.Verts;
                if (through is not null)
                {
                    seen = Clip(seen, through, light.Location);
                }

                if (seen is not null)
                {
                    Visit(p.To, leaf, seen, depth + 1);
                }
            }
        }
    }

    /// <summary>The part of <paramref name="portal"/> the light at <paramref name="eye"/> sees through
    /// <paramref name="window"/>, or null when it sees none of it.</summary>
    private static Vec3[]? Clip(Vec3[] portal, Vec3[] window, Vec3 eye)
    {
        Vec3 centre = Vec3.Zero;
        foreach (Vec3 v in window)
        {
            centre += v;
        }

        centre /= window.Length;
        Vec3[]? poly = portal;
        for (int i = 0, j = window.Length - 1; i < window.Length && poly is not null && poly.Length < MaxClipCorners; j = i++)
        {
            Vec3 n = Vec3.Cross(window[i] - eye, window[j] - eye);
            if (n.LengthSquared < 1e-12)
            {
                continue; // the light lies on this edge's line
            }

            var plane = new Plane3(n.Normalized(), Vec3.Dot(n.Normalized(), eye));
            if (plane.Dist(centre) < 0)
            {
                plane = plane.Flipped();
            }

            poly = Poly.ClassifyPoints(poly, plane) switch
            {
                PlaneSide.Back => null,
                PlaneSide.Split => Poly.SplitPoints(poly, plane).Front,
                _ => poly,
            };
        }

        return poly;
    }

    /// <summary>Whether <paramref name="light"/> may light <paramref name="chart"/>: a light these lists do not cover
    /// (not baked here) always may; a chart of world faces takes the lights of the leaves its faces border.</summary>
    public bool Admits(LightmapChart chart, BakeLight light)
    {
        if (!_index.TryGetValue(light, out int l))
        {
            return true;
        }

        ulong[] bits = _charts.GetValue(chart, Of);
        return (bits[l >> 6] & (1UL << (l & 63))) != 0;
    }

    private ulong[] Of(LightmapChart chart)
    {
        var bits = new ulong[_words];
        foreach (ChartPiece piece in chart.Pieces)
        {
            if (piece.Source is not Poly face)
            {
                // Not a world face: no list holds it back.
                Array.Fill(bits, ulong.MaxValue);
                return bits;
            }

            ulong[] fb = _faces.GetOrAdd(face, f => FaceBits(f, chart.TwoSided));
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] |= fb[i];
            }
        }

        return bits;
    }

    private ulong[] FaceBits(Poly face, bool twoSided)
    {
        var bits = new ulong[_words];
        List<BspLeaf> leaves = ZoneBuilder.LeavesBeside(_tree, face, face.Plane.N).ToList();
        if (twoSided)
        {
            leaves.AddRange(ZoneBuilder.LeavesBeside(_tree, face, -face.Plane.N));
        }

        if (leaves.Count == 0)
        {
            // A face with solid on its lit side (a sliver the tree rounds away): no leaf holds its lights back.
            Array.Fill(bits, ulong.MaxValue);
            return bits;
        }

        foreach (BspLeaf leaf in leaves)
        {
            ulong[] lb = _leafBits[leaf.Index];
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] |= lb[i];
            }
        }

        return bits;
    }
}
