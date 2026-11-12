using System.Numerics;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc.Recovery;

/// <summary>One BSP node as brush recovery walks it: its plane (unit normal, <c>n.p = d</c>, front positive), children
/// (-1 a leaf), surf, and whether it is a CSG node (one with polygons and NF_NotCsg clear).</summary>
public readonly record struct CookedNode(Vec3 Normal, double Distance, int Front, int Back, int Surf, bool Csg);

/// <summary>One surf of the world model as a brush face takes it: material path, flags, texture mapping in world space
/// (pans are in the base), both lightmap scales, and a portal or mirror's authored outline.</summary>
public sealed record CookedSurf(string? Material, int Flags, Vec3 TextureBase, Vec3 TextureU, Vec3 TextureV, double LightMapScale,
    double? LightMapScalePsx2, IReadOnlyList<Vec3>? Outline, Vec3 OutlineNormal);

/// <summary>One node polygon of the world model: its points (consecutive duplicates removed), outward normal, surf,
/// its node's plane, and whether it is on a CSG node (a wall of solid space) or a NotCsg node (a non-solid sheet, a
/// portal).</summary>
public sealed class CookedFace
{
    public CookedFace(int node, int surf, List<Vec3> points, Vec3 normal, bool structural, Vec3 planeNormal, double planeDistance)
    {
        Node = node;
        Surf = surf;
        Points = points;
        Normal = normal;
        Structural = structural;
        Distance = Vec3.Dot(normal, points[0]);
        Bounds = Aabb.Of(points);
        PlaneNormal = planeNormal;
        PlaneDistance = planeDistance;
    }

    /// <summary>The node's plane (<c>n.p = d</c>). The polygon's points can lie up to the BSP's coplanar band (0.25
    /// units) off it; the cells the recovery cuts lie on it exactly.</summary>
    public Vec3 PlaneNormal { get; }

    public double PlaneDistance { get; }

    public int Node { get; }

    public int Surf { get; }

    public List<Vec3> Points { get; }

    public Vec3 Normal { get; }

    public double Distance { get; }

    public bool Structural { get; }

    public Aabb Bounds { get; }

    public Vec3 Centre()
    {
        Vec3 sum = Vec3.Zero;
        foreach (Vec3 p in Points)
        {
            sum += p;
        }

        return sum / Points.Count;
    }
}

/// <summary>
/// The world model of a compiled map as brush recovery reads it (SDC-FORMAT.md section 8): the node tree with each
/// node's CSG state, every node polygon with its surf, the surfs' materials and texture mapping, RootOutside and the
/// bounds of the points. Read from the exact model decode (<see cref="UModelData"/>).
/// </summary>
public sealed class CookedBsp
{
    /// <summary>EPolyFlags bits of a zone portal and of a mirror (their surfs keep the authored FPoly).</summary>
    public const int PortalFlag = 0x04000000, MirrorFlag = 0x08000000;

    private CookedBsp(List<CookedNode> nodes, List<CookedSurf> surfs, List<CookedFace> faces, bool rootOutside, Aabb pointBounds, int skipped)
    {
        Nodes = nodes;
        Surfs = surfs;
        Faces = faces;
        RootOutside = rootOutside;
        PointBounds = pointBounds;
        SkippedPolygons = skipped;
    }

    public IReadOnlyList<CookedNode> Nodes { get; }

    public IReadOnlyList<CookedSurf> Surfs { get; }

    /// <summary>Every node polygon of three or more usable points, in node order.</summary>
    public IReadOnlyList<CookedFace> Faces { get; }

    /// <summary>The state above the root: false for a level whose world starts solid (every stock map).</summary>
    public bool RootOutside { get; }

    public Aabb PointBounds { get; }

    /// <summary>Node polygons left out: a vertex out of range or not finite, fewer than three distinct points, no area.</summary>
    public int SkippedPolygons { get; }

    /// <summary>Reads the world model of a map package. Throws <see cref="InvalidDataException"/> for a node tree that
    /// cannot be walked (a child out of range, a node reached twice, a plane with no normal).</summary>
    public static CookedBsp Read(UPackage pkg, UExport worldModel, SdcLayout layout) =>
        From(UModelData.Read(pkg, worldModel, layout), objref => objref == 0 ? null : pkg.PathOf(objref));

    /// <summary>A world model put together from parts another format keeps (an SVMap's collision and render files,
    /// <see cref="Svmap.SvMapBsp"/>). Throws <see cref="InvalidDataException"/> for a node tree that cannot be walked.</summary>
    public static CookedBsp FromParts(List<CookedNode> nodes, List<CookedSurf> surfs, List<CookedFace> faces, bool rootOutside, Aabb pointBounds,
        int skipped)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Front < -1 || nodes[i].Front >= nodes.Count || nodes[i].Back < -1 || nodes[i].Back >= nodes.Count)
            {
                throw new InvalidDataException($"BSP node {i} has a child out of range");
            }
        }

        CheckTree(nodes);
        return new CookedBsp(nodes, surfs, faces, rootOutside, pointBounds, skipped);
    }

    /// <summary>The front/back links must form a tree from node 0.</summary>
    private static void CheckTree(List<CookedNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        var seen = new bool[nodes.Count];
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (seen[i])
            {
                throw new InvalidDataException($"BSP node {i} is reached twice");
            }

            seen[i] = true;
            if (nodes[i].Front >= 0)
            {
                stack.Push(nodes[i].Front);
            }

            if (nodes[i].Back >= 0)
            {
                stack.Push(nodes[i].Back);
            }
        }
    }

    public static CookedBsp From(UModelData m, Func<int, string?> pathOf)
    {
        var nodes = new List<CookedNode>(m.Nodes.Count);
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            UBspNode n = m.Nodes[i];
            var normal = new Vec3(n.Plane.X, n.Plane.Y, n.Plane.Z);
            double length = normal.Length;
            if (!double.IsFinite(length) || length < 1e-12 || !float.IsFinite(n.Plane.W))
            {
                throw new InvalidDataException($"BSP node {i} has no usable plane");
            }

            if (n.Front < -1 || n.Front >= m.Nodes.Count || n.Back < -1 || n.Back >= m.Nodes.Count)
            {
                throw new InvalidDataException($"BSP node {i} has a child out of range");
            }

            // The engine keeps NodeFlags bits 0x1F on load; NF_NotCsg is 0x01. A node with no polygon is not a CSG node
            // (FBspNode::IsCsg).
            bool csg = n.NumVertices != 0 && (n.NodeFlags & 0x01) == 0;
            nodes.Add(new CookedNode(normal / length, n.Plane.W / length, n.Front, n.Back, n.Surf, csg));
        }

        CheckTree(nodes);

        var surfs = new List<CookedSurf>(m.Surfs.Count);
        foreach (FBspSurf s in m.Surfs)
        {
            Vec3 baseAt = s.Base >= 0 && s.Base < m.Points.Count ? V(m.Points[s.Base]) : Vec3.Zero;
            IReadOnlyList<Vec3>? outline = s.Poly is { Vertices.Length: >= 3 } poly ? poly.Vertices.Select(V).ToList() : null;
            surfs.Add(new CookedSurf(pathOf(s.Material), unchecked((int)s.PolyFlags), baseAt, VectorAt(m, s.TextureU), VectorAt(m, s.TextureV),
                s.LightMapScaleXbox, s.LightMapScalePsx2 == s.LightMapScaleXbox ? null : s.LightMapScalePsx2, outline,
                s.Poly is { } q ? V(q.Normal) : Vec3.Zero));
        }

        var faces = new List<CookedFace>();
        int skipped = 0;
        Aabb pointBounds = Aabb.Of(m.Points.Select(V));
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            UBspNode n = m.Nodes[i];
            if (n.NumVertices == 0)
            {
                continue; // a splitter with no polygon
            }

            if (n.Surf < 0 || n.Surf >= surfs.Count || n.NumVertices < 3 || n.VertPool < 0 || n.VertPool + n.NumVertices > m.Verts.Count)
            {
                skipped++;
                continue;
            }

            var points = new List<Vec3>(n.NumVertices);
            bool valid = true;
            for (int k = 0; k < n.NumVertices; k++)
            {
                int pi = m.Verts[n.VertPool + k].Vertex;
                if (pi < 0 || pi >= m.Points.Count || !float.IsFinite(m.Points[pi].X + m.Points[pi].Y + m.Points[pi].Z))
                {
                    valid = false;
                    break;
                }

                Vec3 p = V(m.Points[pi]);
                if (points.Count == 0 || (points[^1] - p).LengthSquared >= 1e-6)
                {
                    points.Add(p);
                }
            }

            if (points.Count > 1 && (points[0] - points[^1]).LengthSquared < 1e-6)
            {
                points.RemoveAt(points.Count - 1);
            }

            // Some cooked polygons fold back on themselves along an edge (z -448 -> -453 -> -450 -> -467): such a corner
            // is a spike with no area, and later steps read its backward edge as a wall facing the wrong way.
            points = Unfold(points);
            Vec3 newell = Newell(points);
            if (!valid || points.Count < 3 || newell.LengthSquared < 1e-24)
            {
                skipped++;
                continue;
            }

            Vec3 normal = newell.Normalized();
            // The cooked normal decides the facing; the winding follows it.
            Vec3 cooked = VectorAt(m, m.Surfs[n.Surf].Normal);
            if (cooked.LengthSquared > 1e-8 && double.IsFinite(cooked.X + cooked.Y + cooked.Z))
            {
                cooked = cooked.Normalized();
                if (Vec3.Dot(cooked, normal) < 0)
                {
                    points.Reverse();
                }

                normal = cooked;
            }

            faces.Add(new CookedFace(i, n.Surf, points, normal, (n.NodeFlags & 0x01) == 0, nodes[i].Normal, nodes[i].Distance));
        }

        return new CookedBsp(nodes, surfs, faces, m.RootOutside, pointBounds, skipped);
    }

    /// <summary>The polygon without corners that double back along one line (a spike out and back: its two edges point
    /// opposite ways within 1e-3 of a radian) or run straight on (within 1e-6), repeated until none is left; corners
    /// within 1e-3 units of the one before go too.</summary>
    public static List<Vec3> Unfold(List<Vec3> points)
    {
        var o = new List<Vec3>(points);
        bool changed = true;
        while (changed && o.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < o.Count && o.Count >= 3; i++)
            {
                Vec3 a = o[(i + o.Count - 1) % o.Count], b = o[i], c = o[(i + 1) % o.Count];
                Vec3 ab = b - a, bc = c - b;
                double lab = ab.Length, lbc = bc.Length;
                bool drop = lab < 1e-3;
                if (!drop && lbc >= 1e-3)
                {
                    double cross = Vec3.Cross(ab, bc).Length / (lab * lbc);
                    drop = Vec3.Dot(ab, bc) < 0 ? cross <= 1e-3 : cross <= 1e-6;
                }

                if (drop)
                {
                    o.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }

        return o;
    }

    private static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vec3 VectorAt(UModelData m, int i) => i >= 0 && i < m.Vectors.Count ? V(m.Vectors[i]) : Vec3.Zero;

    public static Vec3 Newell(IReadOnlyList<Vec3> points)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Vec3 a = points[i], b = points[(i + 1) % points.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec3(x, y, z);
    }

    /// <summary>True when a point is in empty space: the engine's walk (front when <c>n.p &gt;= d</c>), the state turning
    /// empty in front of a CSG node and staying as it was behind a non-CSG one.</summary>
    public bool IsOutside(Vec3 p)
    {
        bool outside = RootOutside;
        int i = Nodes.Count == 0 ? -1 : 0;
        while (i >= 0)
        {
            CookedNode n = Nodes[i];
            if (Vec3.Dot(n.Normal, p) >= n.Distance)
            {
                outside = outside || n.Csg;
                i = n.Front;
            }
            else
            {
                outside = outside && !n.Csg;
                i = n.Back;
            }
        }

        return outside;
    }
}
