using System.Numerics;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// Writes a level's GE partition (SDC-FORMAT.md section 5; scctformats <c>ge.py</c>): version 4, the box of the
/// first two points of every GE, the GE records, the index list and a k-d tree over it. The tree splits the longest
/// side of a node's box at the median GE until a leaf holds at most <see cref="LeafSize"/> GEs; a GE that crosses a
/// split goes to both sides (the index list allows repeats), so every GE is found from every half-space it touches,
/// as the editor's own trees are (left is the low side).
/// </summary>
public static class SdcGePartition
{
    public const int PartitionVersion = 4;
    public const int LeafSize = 8;

    private const int TreeEmpty = 1, TreeLeaf = 16;

    /// <summary>The kind code of a GE kind name (POLE 1 ... ZL 64).</summary>
    public static int Code(string kind) => SdcGe.Kinds.First(k => k.Value.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>The project's GEs as records, in project order.</summary>
    public static List<SdcGe> Records(IEnumerable<Ge> ges) =>
        ges.Select(g => new SdcGe(Code(g.Kind), g.Flags, g.Platform, g.Mask & 0xFF,
            g.Points.Take(SdcGe.Kinds[Code(g.Kind)].Points).Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z)).ToArray())).ToList();

    /// <summary>True when two GE lists hold the same records in the same order (then a stored partition still fits).</summary>
    public static bool Same(IReadOnlyList<SdcGe> a, IReadOnlyList<SdcGe> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Code == p.Second.Code && p.First.Flags == p.Second.Flags
            && p.First.Platform == p.Second.Platform && p.First.Id == p.Second.Id && p.First.Points.SequenceEqual(p.Second.Points));

    public static void Write(UWriter w, IReadOnlyList<SdcGe> ges)
    {
        w.I32(PartitionVersion);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (SdcGe g in ges)
        {
            foreach (Vector3 p in g.Points.Take(2))
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        if (ges.Count == 0)
        {
            min = max = Vector3.Zero;
        }

        w.Vector(min);
        w.Vector(max);
        w.Compact(ges.Count);
        foreach (SdcGe g in ges)
        {
            w.I32(g.Code);
            w.I32(g.Flags);
            w.I32(g.Platform);
            w.U8((byte)g.Id);
            foreach (Vector3 p in g.Points)
            {
                w.Vector(p);
            }
        }

        var indices = new List<int>();
        var tree = new List<(int Type, float Split, int First, int End)>();
        Build(ges, Enumerable.Range(0, ges.Count).ToList(), indices, tree, 0);
        w.Compact(indices.Count);
        indices.ForEach(w.I32);
        foreach ((int type, float split, int first, int end) in tree)
        {
            w.I32(type);
            if (type == TreeLeaf)
            {
                w.I32(first);
                w.I32(end);
            }
            else if (type != TreeEmpty)
            {
                w.F32(split);
            }
        }
    }

    /// <summary>Appends the pre-order nodes of the subtree over <paramref name="items"/>.</summary>
    private static void Build(IReadOnlyList<SdcGe> ges, List<int> items, List<int> indices, List<(int, float, int, int)> tree, int depth)
    {
        if (items.Count == 0)
        {
            tree.Add((TreeEmpty, 0, 0, 0));
            return;
        }

        if (items.Count <= LeafSize || depth >= 24)
        {
            tree.Add((TreeLeaf, 0, indices.Count, indices.Count + items.Count));
            indices.AddRange(items);
            return;
        }

        // The longest side of the items' box; the median of their centres splits it.
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (int i in items)
        {
            foreach (Vector3 p in ges[i].Points)
            {
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
        }

        Vector3 size = hi - lo;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        List<float> centres = items.Select(i => Centre(ges[i], axis)).OrderBy(c => c).ToList();
        float split = centres[centres.Count / 2];
        var left = items.Where(i => ges[i].Points.Min(p => Axis(p, axis)) <= split).ToList();
        var right = items.Where(i => ges[i].Points.Max(p => Axis(p, axis)) >= split).ToList();
        if (left.Count == items.Count && right.Count == items.Count)
        {
            // Every GE crosses the split: no split separates them.
            tree.Add((TreeLeaf, 0, indices.Count, indices.Count + items.Count));
            indices.AddRange(items);
            return;
        }

        tree.Add((2 << axis, split, 0, 0));
        Build(ges, left, indices, tree, depth + 1);
        Build(ges, right, indices, tree, depth + 1);
    }

    private static float Centre(SdcGe g, int axis) => g.Points.Average(p => Axis(p, axis));

    private static float Axis(Vector3 p, int axis) => axis switch
    {
        0 => p.X,
        1 => p.Y,
        _ => p.Z,
    };
}
