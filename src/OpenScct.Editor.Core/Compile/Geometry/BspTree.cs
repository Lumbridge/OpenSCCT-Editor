using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>A node or a leaf of a <see cref="BspTree"/>.</summary>
public abstract class BspElement
{
    public BspNode? Parent { get; internal set; }
}

/// <summary>A leaf: a convex region of space, empty or solid, in one zone.</summary>
public sealed class BspLeaf : BspElement
{
    public BspLeaf(bool empty) => Empty = empty;

    public bool Empty { get; }

    /// <summary>The zone index (0 = the LevelInfo's default zone).</summary>
    public int Zone { get; set; }

    /// <summary>Position in <see cref="BspTree.Leaves"/>.</summary>
    public int Index { get; internal set; }
}

/// <summary>A node: a splitting plane, the polygons on it, and the two sides.</summary>
public sealed class BspNode : BspElement
{
    public BspNode(Plane3 plane, bool csg)
    {
        Plane = plane;
        Csg = csg;
    }

    public Plane3 Plane { get; }

    /// <summary>Collision polygons lying on the plane and facing its way.</summary>
    public List<Poly> Polys { get; } = new();

    /// <summary>A CSG node makes its front empty and its back solid (UModel::LineCheck); other nodes (non-solid
    /// surfaces, zone portal splits) pass the state on.</summary>
    public bool Csg { get; }

    public BspElement Front { get; internal set; } = null!;

    public BspElement Back { get; internal set; } = null!;

    /// <summary>A portal sheet split (a plane with no polygon, NotCsg).</summary>
    public bool IsPortalSplit { get; init; }

    /// <summary>The piece of the portal sheet a portal split was made for (the original's world model keeps it as the
    /// node's polygon, on a portal surface).</summary>
    public Poly? PortalPiece { get; init; }
}

/// <summary>
/// A solid-leaf BSP over the collision polygons of the CSG result, the form the original world model's collision
/// has (spec/svmap/1.1/collision.md BSPN): every node is a polygon's plane, and a point's state is the side of the
/// last CSG plane crossed on the way down (front empty, back solid), starting solid at the root, as UModel::LineCheck
/// walks it. A generalisation of OpenSCCT's tools/testmap/bsp.py to any convex polygons. Deterministic.
/// </summary>
public sealed class BspTree
{
    private BspTree(BspElement root, bool rootEmpty)
    {
        Root = root;
        RootEmpty = rootEmpty;
        Reindex();
    }

    public BspElement Root { get; private set; }

    /// <summary>The state above the root (false: the UE2 world starts solid).</summary>
    public bool RootEmpty { get; }

    public IReadOnlyList<BspLeaf> Leaves { get; private set; } = [];

    public int NodeCount { get; private set; }

    /// <summary>Builds the tree. <paramref name="csgPolys"/> make CSG nodes; <paramref name="passPolys"/> (non-solid
    /// surfaces of solid brushes) only split, after every CSG polygon of their region is placed.</summary>
    public static BspTree Build(IReadOnlyList<Poly> csgPolys, IReadOnlyList<Poly>? passPolys = null, bool rootEmpty = false)
    {
        var items = new List<(Poly Poly, bool Csg)>(csgPolys.Count + (passPolys?.Count ?? 0));
        items.AddRange(csgPolys.Select(p => (p, true)));
        if (passPolys is not null)
        {
            items.AddRange(passPolys.Select(p => (p, false)));
        }

        BspElement root = BuildRec(items, rootEmpty);
        return new BspTree(root, rootEmpty);
    }

    private static BspElement BuildRec(List<(Poly Poly, bool Csg)> items, bool inherited)
    {
        if (items.Count == 0)
        {
            return new BspLeaf(inherited);
        }

        (Poly splitter, bool csg) = Pick(items);
        var node = new BspNode(splitter.Plane, csg);
        var front = new List<(Poly, bool)>();
        var back = new List<(Poly, bool)>();
        foreach ((Poly q, bool qcsg) in items)
        {
            // A polygon carrying the node's own plane (the splitter, its pieces, or a face of exactly that plane) is on
            // it whatever its corners say: a non-planar face lies partly in front of its own plane, and placed by its
            // corners it would go in front of itself again and again. A parallel face within UE2's coplanar band is on
            // it too (see OnPlane); any other face is placed by its corners, or it would skip the splits that place
            // its far corners.
            switch (q.Plane == node.Plane || q.Plane == node.Plane.Flipped() || Coplanar(q, node.Plane) ? PlaneSide.On : q.Classify(node.Plane))
            {
                case PlaneSide.On:
                    if (Vec3.Dot(q.Plane.N, node.Plane.N) > 0 && qcsg == csg)
                    {
                        if (csg)
                        {
                            node.Polys.Add(q);
                        }
                    }
                    else
                    {
                        // Behind, carried onto the node's plane, as UE2 keeps a coplanar face on the node: with its
                        // own plane a nearly coplanar face (of a brush a hair from this one) would cut a sliver out of
                        // the space behind, which the engine reads as a solid sheet that stops bullets.
                        back.Add((OnPlane(q, node.Plane), qcsg));
                    }

                    break;
                case PlaneSide.Front:
                    front.Add((q, qcsg));
                    break;
                case PlaneSide.Back:
                    back.Add((q, qcsg));
                    break;
                default:
                    (Poly? f, Poly? b) = q.Split(node.Plane);
                    if (f is not null)
                    {
                        front.Add((f, qcsg));
                    }

                    if (b is not null)
                    {
                        back.Add((b, qcsg));
                    }

                    break;
            }
        }

        node.Front = BuildRec(front, csg || inherited);
        node.Back = BuildRec(back, !csg && inherited);
        node.Front.Parent = node;
        node.Back.Parent = node;
        return node;
    }

    /// <summary>UE2's band for a polygon on a BSP plane (THRESH_SPLIT_POLY_WITH_PLANE, as bspBuild splits).</summary>
    private const double CoplanarBand = 0.25;

    /// <summary>A polygon parallel to the plane (either way) with every corner within <see cref="CoplanarBand"/> of
    /// it: two brush faces a hair apart, which UE2 puts on one node.</summary>
    private static bool Coplanar(Poly q, Plane3 plane)
    {
        if (Math.Abs(Vec3.Dot(q.Plane.N, plane.N)) < 1 - 1e-6)
        {
            return false;
        }

        foreach (Vec3 v in q.Verts)
        {
            if (Math.Abs(plane.Dist(v)) > CoplanarBand)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The polygon with <paramref name="plane"/> (turned its way) in place of its own nearly coplanar one.</summary>
    private static Poly OnPlane(Poly q, Plane3 plane)
    {
        Plane3 turned = Vec3.Dot(q.Plane.N, plane.N) > 0 ? plane : plane.Flipped();
        return q.Plane == turned ? q : new Poly(q.Verts, turned, q.Surface);
    }

    /// <summary>The splitter: CSG polygons first; among a sample of them, the plane that splits fewest polygons,
    /// larger polygons first on ties, then the earliest (deterministic).</summary>
    private static (Poly, bool) Pick(List<(Poly Poly, bool Csg)> items)
    {
        bool anyCsg = items.Exists(i => i.Csg);
        List<(Poly Poly, bool Csg)> pool = anyCsg && items.Exists(i => !i.Csg) ? items.FindAll(i => i.Csg) : items;
        int step = Math.Max(1, pool.Count / 48);
        (Poly, bool) best = pool[0];
        (int Splits, double Area) bestKey = (int.MaxValue, 0);
        for (int s = 0; s < pool.Count; s += step)
        {
            Plane3 plane = pool[s].Poly.Plane;
            int splits = 0;
            foreach ((Poly q, bool _) in items)
            {
                if (q.Classify(plane) == PlaneSide.Split)
                {
                    splits++;
                    if (splits > bestKey.Splits)
                    {
                        // Worse than the best already: stop counting (a tie must be counted in full).
                        break;
                    }
                }
            }

            double area = Math.Round(pool[s].Poly.Area(), 3);
            if (splits < bestKey.Splits || (splits == bestKey.Splits && area > bestKey.Area))
            {
                best = pool[s];
                bestKey = (splits, area);
            }
        }

        return best;
    }

    /// <summary>Numbers the leaves and counts the nodes, depth first, front before back.</summary>
    public void Reindex()
    {
        var leaves = new List<BspLeaf>();
        int nodes = 0;
        var stack = new Stack<BspElement>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            BspElement e = stack.Pop();
            if (e is BspNode n)
            {
                nodes++;
                stack.Push(n.Back);
                stack.Push(n.Front);
            }
            else
            {
                var leaf = (BspLeaf)e;
                leaf.Index = leaves.Count;
                leaves.Add(leaf);
            }
        }

        Leaves = leaves;
        NodeCount = nodes;
    }

    /// <summary>The leaf holding a point (front when the distance is not negative, as the engine walks it).</summary>
    public BspLeaf LeafAt(Vec3 p)
    {
        BspElement e = Root;
        while (e is BspNode n)
        {
            e = n.Plane.Dist(p) >= 0 ? n.Front : n.Back;
        }

        return (BspLeaf)e;
    }

    public bool IsEmpty(Vec3 p) => LeafAt(p).Empty;

    /// <summary>True when the segment passes through solid space anywhere (for light shadows and sight checks).</summary>
    public bool SegmentHitsSolid(Vec3 a, Vec3 b) => HitsSolid(Root, a, b, 0);

    private static bool HitsSolid(BspElement e, Vec3 a, Vec3 b, int depth)
    {
        while (true)
        {
            if (e is BspLeaf leaf)
            {
                return !leaf.Empty;
            }

            var n = (BspNode)e;
            double da = n.Plane.Dist(a), db = n.Plane.Dist(b);
            if (da >= -Plane3.Epsilon && db >= -Plane3.Epsilon)
            {
                e = n.Front;
                continue;
            }

            if (da < Plane3.Epsilon && db < Plane3.Epsilon)
            {
                e = n.Back;
                continue;
            }

            Vec3 mid = a + (b - a) * (da / (da - db));
            BspElement near = da >= 0 ? n.Front : n.Back, far = da >= 0 ? n.Back : n.Front;
            if (depth < 512 && HitsSolid(near, a, mid, depth + 1))
            {
                return true;
            }

            e = far;
            a = mid;
        }
    }

    /// <summary>Replaces <paramref name="leaf"/> with <paramref name="replacement"/> (portal splits).</summary>
    internal void Replace(BspLeaf leaf, BspElement replacement)
    {
        BspNode? parent = leaf.Parent;
        replacement.Parent = parent;
        if (parent is null)
        {
            Root = replacement;
        }
        else if (ReferenceEquals(parent.Front, leaf))
        {
            parent.Front = replacement;
        }
        else
        {
            parent.Back = replacement;
        }
    }

    /// <summary>The convex pieces of the solid leaves as outward plane sets (a concave brush's decomposition when the
    /// tree is built from its faces with <c>rootEmpty</c> true).</summary>
    public List<Plane3[]> SolidRegions()
    {
        var result = new List<Plane3[]>();
        var path = new List<Plane3>();
        Collect(Root, path, result);
        return result;
    }

    private static void Collect(BspElement e, List<Plane3> path, List<Plane3[]> result)
    {
        if (e is BspLeaf leaf)
        {
            if (!leaf.Empty)
            {
                result.Add(path.ToArray());
            }

            return;
        }

        var n = (BspNode)e;
        // In front of the plane: the region is behind the flipped plane.
        path.Add(n.Plane.Flipped());
        Collect(n.Front, path, result);
        path[^1] = n.Plane;
        Collect(n.Back, path, result);
        path.RemoveAt(path.Count - 1);
    }

    /// <summary>The half-spaces bounding a leaf (planes pointing out of it), the node each comes from, and whether
    /// the leaf lies in front of that node.</summary>
    public static List<(Plane3 Plane, BspNode Node, bool LeafInFront)> BoundsOf(BspLeaf leaf)
    {
        var list = new List<(Plane3, BspNode, bool)>();
        BspElement child = leaf;
        for (BspNode? n = leaf.Parent; n is not null; child = n, n = n.Parent)
        {
            bool front = ReferenceEquals(n.Front, child);
            list.Add((front ? n.Plane.Flipped() : n.Plane, n, front));
        }

        return list;
    }
}
