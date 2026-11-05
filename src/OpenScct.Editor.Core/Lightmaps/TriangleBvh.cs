using System.Numerics;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>What an occluder triangle does to rays.</summary>
[Flags]
public enum OccluderFlags : byte
{
    None = 0,

    /// <summary>Sky: FakeBackdrop surfaces and the sky zone let sunlight through.</summary>
    Sky = 2,
}

/// <summary>One triangle of the shadow scene (UE space), with what it came from (<see cref="Source"/> is the
/// caller's own index: a chart piece, a mesh instance). A masked triangle carries its texture's
/// <see cref="Mask"/> and the texture coordinates of its corners.</summary>
public readonly record struct OccluderTriangle(Vec3 A, Vec3 B, Vec3 C, OccluderFlags Flags, int Source)
{
    public AlphaMask? Mask { get; init; }

    public Vector2 UvA { get; init; }

    public Vector2 UvB { get; init; }

    public Vector2 UvC { get; init; }
}

/// <summary>A ray hit: the triangle (in the order given to <see cref="TriangleBvh.Build"/>), the distance and whether the
/// ray met the triangle's front.</summary>
public readonly record struct BvhHit(int Triangle, float Distance, bool Front);

/// <summary>
/// The editor's own bounding volume hierarchy over shadow-casting triangles (binned SAH, up to four triangles a leaf).
/// Any-hit queries answer shadow rays; closest-hit queries serve the bounce pass. Thread-safe for queries.
/// <para>Every triangle blocks a shadow ray from either side, one-sided or not: the original's light's-eye render did
/// not cull back faces (measured: culling them left 5-10% of single-light texels on MissD and RiveD lit where the
/// original is in shadow, mostly behind open meshes and brushes seen from inside). Facing still matters to
/// <see cref="Closest"/>, which tells buried texels and bounce hits apart by it.</para>
/// </summary>
public sealed class TriangleBvh
{
    private const int LeafSize = 4;
    private const int Bins = 12;

    private readonly Node[] _nodes;
    private readonly Vector3[] _v0;
    private readonly Vector3[] _e1;
    private readonly Vector3[] _e2;
    private readonly Vector3[] _n;
    private readonly OccluderFlags[] _flags;
    private readonly AlphaMask?[] _mask;
    private readonly Vector2[] _uv;
    private readonly int[] _order;
    private readonly ulong[] _hash;
    private readonly Vector3[] _min;
    private readonly Vector3[] _max;

    /// <summary>Traversal stack entries a query needs: the tree's depth plus the sibling pushed at each level.</summary>
    private readonly int _stackSize;

    private struct Node
    {
        public Vector3 Min;
        public Vector3 Max;

        /// <summary>A leaf's first triangle, or an inner node's left child (the right child follows it).</summary>
        public int Start;

        /// <summary>Triangles in a leaf; 0 for an inner node.</summary>
        public int Count;
    }

    private TriangleBvh(int count, Node[] nodes)
    {
        Count = count;
        _nodes = nodes;
        _stackSize = MaxDepth(nodes) + 2;
        _v0 = new Vector3[count];
        _e1 = new Vector3[count];
        _e2 = new Vector3[count];
        _n = new Vector3[count];
        _flags = new OccluderFlags[count];
        _mask = new AlphaMask?[count];
        _uv = new Vector2[3 * count];
        _order = new int[count];
        _hash = new ulong[count];
        _min = new Vector3[count];
        _max = new Vector3[count];
    }

    public int Count { get; }

    /// <summary>An order-independent key of every triangle (changes when any occluder moves).</summary>
    public ulong Key { get; private set; }

    /// <summary>The caller's index of the triangle a hit reports.</summary>
    public int SourceOf(int triangle) => _order[triangle];

    /// <summary>Bounds of every triangle (empty when there are none).</summary>
    public Aabb Bounds => Count == 0 ? Aabb.Empty
        : new Aabb(new Vec3(_nodes[0].Min.X, _nodes[0].Min.Y, _nodes[0].Min.Z), new Vec3(_nodes[0].Max.X, _nodes[0].Max.Y, _nodes[0].Max.Z));

    private static int MaxDepth(Node[] nodes)
    {
        int deepest = 0;
        var pending = new Stack<(int Node, int Depth)>();
        pending.Push((0, 0));
        while (pending.Count > 0)
        {
            (int at, int depth) = pending.Pop();
            deepest = Math.Max(deepest, depth);
            // Inner nodes hold no triangles and point past the root; an empty tree's root is a leaf.
            if (nodes[at].Count == 0 && nodes[at].Start > 0)
            {
                pending.Push((nodes[at].Start, depth + 1));
                pending.Push((nodes[at].Start + 1, depth + 1));
            }
        }

        return deepest;
    }

    public static TriangleBvh Build(IReadOnlyList<OccluderTriangle> triangles)
    {
        int n = triangles.Count;
        var centres = new Vector3[n];
        var mins = new Vector3[n];
        var maxs = new Vector3[n];
        var index = new int[n];
        for (int i = 0; i < n; i++)
        {
            OccluderTriangle t = triangles[i];
            Vector3 a = F(t.A), b = F(t.B), c = F(t.C);
            mins[i] = Vector3.Min(a, Vector3.Min(b, c));
            maxs[i] = Vector3.Max(a, Vector3.Max(b, c));
            centres[i] = (mins[i] + maxs[i]) * 0.5f;
            index[i] = i;
        }

        var nodes = new List<Node>(Math.Max(1, 2 * n / LeafSize));
        nodes.Add(default);
        if (n > 0)
        {
            Split(nodes, 0, index, 0, n, centres, mins, maxs);
        }
        else
        {
            nodes[0] = new Node { Min = new Vector3(1), Max = new Vector3(-1) };
        }

        var bvh = new TriangleBvh(n, nodes.ToArray());
        ulong key = 0;
        for (int k = 0; k < n; k++)
        {
            int i = index[k];
            OccluderTriangle t = triangles[i];
            Vector3 a = F(t.A), b = F(t.B), c = F(t.C);
            bvh._v0[k] = a;
            bvh._e1[k] = b - a;
            bvh._e2[k] = c - a;
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float len = normal.Length();
            bvh._n[k] = len > 0 ? normal / len : Vector3.Zero;
            bvh._flags[k] = t.Flags;
            bvh._mask[k] = t.Mask;
            (bvh._uv[3 * k], bvh._uv[(3 * k) + 1], bvh._uv[(3 * k) + 2]) = (t.UvA, t.UvB, t.UvC);
            bvh._order[k] = t.Source;
            bvh._min[k] = mins[i];
            bvh._max[k] = maxs[i];
            var h = new Hash64();
            h.Add(t.A);
            h.Add(t.B);
            h.Add(t.C);
            h.Add((int)t.Flags);
            if (t.Mask is not null)
            {
                h.Add(t.Mask.Key);
                foreach (Vector2 uv in (ReadOnlySpan<Vector2>)[t.UvA, t.UvB, t.UvC])
                {
                    h.Add(uv.X);
                    h.Add(uv.Y);
                }
            }
            bvh._hash[k] = h.Value;
            key += h.Value;
        }

        bvh.Key = key;
        return bvh;
    }

    private static Vector3 F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static void Split(List<Node> nodes, int at, int[] index, int start, int count, Vector3[] centres, Vector3[] mins, Vector3[] maxs)
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue), clo = new(float.MaxValue), chi = new(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            lo = Vector3.Min(lo, mins[index[i]]);
            hi = Vector3.Max(hi, maxs[index[i]]);
            clo = Vector3.Min(clo, centres[index[i]]);
            chi = Vector3.Max(chi, centres[index[i]]);
        }

        Vector3 extent = chi - clo;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        float span = axis == 0 ? extent.X : axis == 1 ? extent.Y : extent.Z;
        if (count <= LeafSize || span <= 1e-4f)
        {
            nodes[at] = new Node { Min = lo, Max = hi, Start = start, Count = count };
            if (count > LeafSize)
            {
                // All centres coincide: split the list in half so leaves stay small.
                SplitHalves(nodes, at, index, start, count, centres, mins, maxs, lo, hi);
            }

            return;
        }

        // Binned SAH along the widest centre axis.
        Span<int> binCount = stackalloc int[Bins];
        var binLo = new Vector3[Bins];
        var binHi = new Vector3[Bins];
        for (int b = 0; b < Bins; b++)
        {
            binLo[b] = new Vector3(float.MaxValue);
            binHi[b] = new Vector3(float.MinValue);
        }

        float origin = axis == 0 ? clo.X : axis == 1 ? clo.Y : clo.Z;
        float scale = Bins / span * 0.9999f;
        for (int i = start; i < start + count; i++)
        {
            int t = index[i];
            int b = Math.Min(Bins - 1, (int)((Axis(centres[t], axis) - origin) * scale));
            binCount[b]++;
            binLo[b] = Vector3.Min(binLo[b], mins[t]);
            binHi[b] = Vector3.Max(binHi[b], maxs[t]);
        }

        Span<float> rightCost = stackalloc float[Bins];
        Vector3 rlo = new(float.MaxValue), rhi = new(float.MinValue);
        int rcount = 0;
        for (int b = Bins - 1; b > 0; b--)
        {
            rlo = Vector3.Min(rlo, binLo[b]);
            rhi = Vector3.Max(rhi, binHi[b]);
            rcount += binCount[b];
            rightCost[b] = rcount == 0 ? 0 : Area(rlo, rhi) * rcount;
        }

        Vector3 llo = new(float.MaxValue), lhi = new(float.MinValue);
        int lcount = 0, best = -1;
        float bestCost = float.MaxValue;
        for (int b = 0; b < Bins - 1; b++)
        {
            llo = Vector3.Min(llo, binLo[b]);
            lhi = Vector3.Max(lhi, binHi[b]);
            lcount += binCount[b];
            if (lcount == 0 || lcount == count)
            {
                continue;
            }

            float cost = (Area(llo, lhi) * lcount) + rightCost[b + 1];
            if (cost < bestCost)
            {
                bestCost = cost;
                best = b;
            }
        }

        if (best < 0)
        {
            nodes[at] = new Node { Min = lo, Max = hi, Start = start, Count = count };
            SplitHalves(nodes, at, index, start, count, centres, mins, maxs, lo, hi);
            return;
        }

        int mid = start;
        for (int i = start; i < start + count; i++)
        {
            int t = index[i];
            if (Math.Min(Bins - 1, (int)((Axis(centres[t], axis) - origin) * scale)) <= best)
            {
                (index[i], index[mid]) = (index[mid], index[i]);
                mid++;
            }
        }

        Children(nodes, at, index, start, mid - start, count - (mid - start), centres, mins, maxs, lo, hi);
    }

    private static void SplitHalves(List<Node> nodes, int at, int[] index, int start, int count, Vector3[] centres, Vector3[] mins,
        Vector3[] maxs, Vector3 lo, Vector3 hi) =>
        Children(nodes, at, index, start, count / 2, count - (count / 2), centres, mins, maxs, lo, hi);

    private static void Children(List<Node> nodes, int at, int[] index, int start, int left, int right, Vector3[] centres, Vector3[] mins,
        Vector3[] maxs, Vector3 lo, Vector3 hi)
    {
        int first = nodes.Count;
        nodes.Add(default);
        nodes.Add(default);
        nodes[at] = new Node { Min = lo, Max = hi, Start = first, Count = 0 };
        Split(nodes, first, index, start, left, centres, mins, maxs);
        Split(nodes, first + 1, index, start + left, right, centres, mins, maxs);
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static float Area(Vector3 lo, Vector3 hi)
    {
        Vector3 d = Vector3.Max(hi - lo, Vector3.Zero);
        return (d.X * d.Y) + (d.Y * d.Z) + (d.Z * d.X);
    }

    /// <summary>
    /// True when something blocks the segment from <paramref name="origin"/> along unit <paramref name="dir"/> for
    /// <paramref name="length"/> uu (the light is at its end). Sun rays (<paramref name="sun"/>) pass sky triangles.
    /// </summary>
    public bool Occluded(Vector3 origin, Vector3 dir, float length, bool sun) => Blocker(origin, dir, length, sun) >= 0;

    /// <summary>How far along the segment a blocking triangle is (the first one found, not always the nearest), or -1
    /// when nothing blocks it; <see cref="Occluded"/> with the distance.</summary>
    public float Blocker(Vector3 origin, Vector3 dir, float length, bool sun)
    {
        if (Count == 0)
        {
            return -1;
        }

        Vector3 inv = Inverse(dir);
        Span<int> stack = _stackSize <= 256 ? stackalloc int[256] : new int[_stackSize];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly Node node = ref _nodes[stack[--sp]];
            if (!HitsBox(node.Min, node.Max, origin, inv, length))
            {
                continue;
            }

            if (node.Count == 0)
            {
                stack[sp++] = node.Start;
                stack[sp++] = node.Start + 1;
                continue;
            }

            for (int t = node.Start; t < node.Start + node.Count; t++)
            {
                OccluderFlags flags = _flags[t];
                if (sun && (flags & OccluderFlags.Sky) != 0)
                {
                    continue;
                }

                if (Intersect(t, origin, dir, length, out float at, out Vector2 bary) && Solid(t, bary))
                {
                    return at;
                }
            }
        }

        return -1;
    }

    /// <summary>The nearest triangle along the ray within <paramref name="length"/>, either side, or null.</summary>
    public BvhHit? Closest(Vector3 origin, Vector3 dir, float length)
    {
        if (Count == 0)
        {
            return null;
        }

        Vector3 inv = Inverse(dir);
        Span<int> stack = _stackSize <= 256 ? stackalloc int[256] : new int[_stackSize];
        int sp = 0;
        stack[sp++] = 0;
        int best = -1;
        float bestT = length;
        while (sp > 0)
        {
            ref readonly Node node = ref _nodes[stack[--sp]];
            if (!HitsBox(node.Min, node.Max, origin, inv, bestT))
            {
                continue;
            }

            if (node.Count == 0)
            {
                stack[sp++] = node.Start;
                stack[sp++] = node.Start + 1;
                continue;
            }

            for (int t = node.Start; t < node.Start + node.Count; t++)
            {
                if (Intersect(t, origin, dir, bestT, out float at, out Vector2 bary) && Solid(t, bary))
                {
                    bestT = at;
                    best = t;
                }
            }
        }

        return best < 0 ? null : new BvhHit(best, bestT, Vector3.Dot(_n[best], dir) < 0);
    }

    /// <summary>An order-independent key of the triangles whose bounds touch <paramref name="box"/>: a light whose
    /// reach holds the same key casts the same shadows.</summary>
    public ulong KeyOf(Aabb box)
    {
        if (Count == 0)
        {
            return 0;
        }

        Vector3 lo = F(box.Min), hi = F(box.Max);
        ulong key = 0;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            ref readonly Node node = ref _nodes[stack.Pop()];
            if (!Overlaps(node.Min, node.Max, lo, hi))
            {
                continue;
            }

            if (node.Count == 0)
            {
                stack.Push(node.Start);
                stack.Push(node.Start + 1);
                continue;
            }

            for (int t = node.Start; t < node.Start + node.Count; t++)
            {
                if (Overlaps(_min[t], _max[t], lo, hi))
                {
                    key += _hash[t];
                }
            }
        }

        return key;
    }

    private static bool Overlaps(Vector3 aLo, Vector3 aHi, Vector3 bLo, Vector3 bHi) =>
        aLo.X <= bHi.X && aHi.X >= bLo.X && aLo.Y <= bHi.Y && aHi.Y >= bLo.Y && aLo.Z <= bHi.Z && aHi.Z >= bLo.Z;

    private static Vector3 Inverse(Vector3 d) => new(
        1f / (MathF.Abs(d.X) < 1e-12f ? 1e-12f : d.X),
        1f / (MathF.Abs(d.Y) < 1e-12f ? 1e-12f : d.Y),
        1f / (MathF.Abs(d.Z) < 1e-12f ? 1e-12f : d.Z));

    private static bool HitsBox(Vector3 lo, Vector3 hi, Vector3 origin, Vector3 inv, float length)
    {
        Vector3 t0 = (lo - origin) * inv, t1 = (hi - origin) * inv;
        Vector3 near = Vector3.Min(t0, t1), far = Vector3.Max(t0, t1);
        float enter = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0f));
        float exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, length));
        return enter <= exit;
    }

    /// <summary>False where a masked triangle's texture has a hole under the hit (barycentric <paramref name="bary"/>).</summary>
    private bool Solid(int t, Vector2 bary)
    {
        AlphaMask? mask = _mask[t];
        if (mask is null)
        {
            return true;
        }

        Vector2 uv = (_uv[3 * t] * (1f - bary.X - bary.Y)) + (_uv[(3 * t) + 1] * bary.X) + (_uv[(3 * t) + 2] * bary.Y);
        return mask.Blocks(uv);
    }

    // Moller-Trumbore, both faces.
    private bool Intersect(int t, Vector3 origin, Vector3 dir, float length, out float at, out Vector2 bary)
    {
        at = 0;
        bary = default;
        Vector3 e1 = _e1[t], e2 = _e2[t];
        Vector3 p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
        {
            return false;
        }

        float invDet = 1f / det;
        Vector3 s = origin - _v0[t];
        float u = Vector3.Dot(s, p) * invDet;
        if (u < 0f || u > 1f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(dir, q) * invDet;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        at = Vector3.Dot(e2, q) * invDet;
        bary = new Vector2(u, v);
        return at > 1e-3f && at < length;
    }
}
