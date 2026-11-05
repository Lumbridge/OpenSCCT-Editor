using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// The compiled world geometry of a project: the CSG result sorted by what each polygon does (drawn, collides,
/// splits zones), the solid-leaf BSP over the colliding polygons, and the zones.
/// </summary>
public sealed class WorldModel
{
    private WorldModel(CsgResult csg) => Csg = csg;

    public CsgResult Csg { get; }

    /// <summary>Polygons drawn in the world (not Invisible, not portals), facing the empty side.</summary>
    public List<Poly> Visible { get; } = new();

    /// <summary>Solid and semisolid boundary polygons that collide: the BSP's CSG nodes and the world collider's
    /// triangles.</summary>
    public List<Poly> Solid { get; } = new();

    /// <summary>Solid boundary polygons flagged NotSolid: split the BSP but pass traces through.</summary>
    public List<Poly> PassThrough { get; } = new();

    /// <summary>Zone portal sheets.</summary>
    public List<Poly> Portals { get; } = new();

    public BspTree Tree { get; private set; } = null!;

    public ZoneResult Zones { get; private set; } = null!;

    /// <summary>Bounds of every output polygon.</summary>
    public Aabb Bounds { get; private set; } = Aabb.Empty;

    /// <summary>Problems of single brushes, by brush id.</summary>
    public List<(string BrushId, string Problem)> BrushProblems { get; } = new();

    /// <summary>Builds the world of the brushes, and its zones numbered by <paramref name="numbering"/>; with none, a
    /// world only to be drawn, without zones (a map that keeps one baked layer has its zones in the other world).</summary>
    public static WorldModel Build(IReadOnlyList<Brush> brushes, IReadOnlyList<ZoneSeed> seeds, bool worldSolid, ZoneNumbering? numbering, CsgWorld? cache = null)
    {
        CsgResult csg = (cache ?? new CsgWorld()).Build(brushes, worldSolid);
        var w = new WorldModel(csg);
        foreach (CsgBrush b in csg.Brushes)
        {
            foreach (string problem in b.Problems)
            {
                w.BrushProblems.Add((b.Id, problem));
            }
        }

        foreach (Poly p in csg.Polys)
        {
            w.Bounds = w.Bounds.Union(p.Bounds());
            Surface s = p.Surface;
            if (s.Kind == SurfaceKind.Portal)
            {
                w.Portals.Add(p);
                continue;
            }

            if (!s.Invisible)
            {
                w.Visible.Add(p);
            }

            // Semisolid faces go into the BSP like solid ones, as UE2 builds them (they only do not cut other brushes).
            if (s.Kind is SurfaceKind.Solid or SurfaceKind.Semisolid)
            {
                (s.Collides ? w.Solid : w.PassThrough).Add(p);
            }
        }

        w.Tree = BspTree.Build(w.Solid, w.PassThrough, rootEmpty: !worldSolid);
        w.FixedLeaves = FixLeaves(w.Tree, csg, w.Bounds);
        w.Zones = numbering is null ? ZoneResult.None() : ZoneBuilder.Build(w.Tree, w.Portals, seeds, w.Bounds, numbering);
        return w;
    }

    /// <summary>Leaves whose state the brushes contradicted (see <see cref="FixLeaves"/>).</summary>
    public int FixedLeaves { get; private set; }

    /// <summary>Margin a leaf's inner point keeps from the leaf's planes and the brushes' faces to be judged.</summary>
    private const double JudgeMargin = 0.05;

    /// <summary>
    /// Gives every leaf the state the brushes give its inside. The engine reads a point's state from the last CSG plane
    /// crossed on the way down (UModel::LineCheck), which misreads a leaf whose last plane belongs to a polygon that is
    /// not on the leaf's boundary: solid space reached only through the front of a far plane, or the other way round.
    /// Such a leaf gets one more CSG node without polygons, on a plane it lies wholly behind (solid) or in front of
    /// (empty), so the engine's rule reads its true state. A leaf too thin to judge, or whose inner point lies on a
    /// brush face, is left as built. Returns the number of leaves fixed.
    /// </summary>
    private static int FixLeaves(BspTree tree, CsgResult csg, Aabb bounds)
    {
        if (tree.Root is not BspNode)
        {
            return 0;
        }

        List<CsgBrush> space = csg.Brushes.Where(b => b.ShapesSpace).OrderBy(b => b.Order).ToList();
        List<CsgBrush> semisolids = csg.Brushes.Where(b => b.Kind == SurfaceKind.Semisolid && !b.IsSheet).ToList();
        Aabb box = bounds.IsEmpty ? new Aabb(new Vec3(-1024, -1024, -1024), new Vec3(1024, 1024, 1024)) : bounds.Expand(512);
        IReadOnlyList<BspLeaf> leaves = tree.Leaves;
        var wrong = new bool[leaves.Count];
        Parallel.For(0, leaves.Count, i =>
        {
            BspLeaf leaf = leaves[i];
            List<Vec3> corners = ZoneBuilder.CellCorners(leaf, box);
            if (corners.Count < 4)
            {
                return;
            }

            Vec3 inner = corners.Aggregate(Vec3.Zero, (s, c) => s + c) / corners.Count;
            if (BspTree.BoundsOf(leaf).Any(b => b.Plane.Dist(inner) > -JudgeMargin))
            {
                return;
            }

            wrong[i] = StateAt(space, semisolids, inner, csg.WorldSolid) is bool solid && solid == leaf.Empty;
        });

        int fixedLeaves = 0;
        for (int i = 0; i < leaves.Count; i++)
        {
            if (!wrong[i])
            {
                continue;
            }

            BspLeaf leaf = leaves[i];
            BspNode parent = leaf.Parent!;
            bool leafInFront = ReferenceEquals(parent.Front, leaf);
            bool solid = leaf.Empty;
            // The parent's plane bounds the leaf: turned so the leaf is behind it for solid, in front for empty.
            Plane3 plane = leafInFront == solid ? parent.Plane.Flipped() : parent.Plane;
            var node = new BspNode(plane, csg: true)
            {
                Front = new BspLeaf(empty: true),
                Back = new BspLeaf(empty: false),
                Parent = parent,
            };
            node.Front.Parent = node;
            node.Back.Parent = node;
            if (leafInFront)
            {
                parent.Front = node;
            }
            else
            {
                parent.Back = node;
            }

            fixedLeaves++;
        }

        if (fixedLeaves > 0)
        {
            tree.Reindex();
        }

        return fixedLeaves;
    }

    /// <summary>The state the brushes give a point: solid inside a semisolid brush (its faces are in the tree whatever
    /// comes after it), else the operation of the last brush holding it, else the world's; null when the brush that
    /// decides it has a face through the point.</summary>
    private static bool? StateAt(List<CsgBrush> space, List<CsgBrush> semisolids, Vec3 p, bool worldSolid)
    {
        bool solid = worldSolid;
        bool onFace = false;
        foreach (CsgBrush b in space)
        {
            switch (Inside(b, p))
            {
                case true:
                    solid = b.Oper == CsgOper.Add;
                    onFace = false;
                    break;
                case null:
                    onFace = true;
                    break;
            }
        }

        foreach (CsgBrush b in semisolids)
        {
            switch (Inside(b, p))
            {
                case true:
                    return true;
                case null:
                    onFace = true;
                    break;
            }
        }

        return onFace ? null : solid;
    }

    /// <summary>True inside the brush's volume, false outside, null within <see cref="JudgeMargin"/> of its faces.</summary>
    private static bool? Inside(CsgBrush b, Vec3 p)
    {
        if (!b.Bounds.Expand(JudgeMargin).Contains(p))
        {
            return false;
        }

        double nearest = double.MaxValue;
        foreach (Plane3[] piece in b.Pieces)
        {
            nearest = Math.Min(nearest, piece.Max(q => q.Dist(p)));
        }

        return nearest < -JudgeMargin ? true : nearest <= JudgeMargin ? null : false;
    }

    /// <summary>The zone index of a point (0 when it is in no zone or in solid).</summary>
    public int ZoneAt(Vec3 p) => Tree.LeafAt(p).Zone;
}
