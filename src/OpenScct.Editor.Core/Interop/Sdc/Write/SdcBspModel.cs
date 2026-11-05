using System.Numerics;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>What one surface becomes in a model: its object references and, for the world, its texture size.</summary>
internal sealed record SdcSurfaceRefs(int Material, int Actor, int BrushPoly, FPoly? EditorPoly, double USize, double VSize);

/// <summary>How one node polygon is drawn: its lightmap (an FLightMap index and the atlas UV of a world point), or
/// none when it is unlit.</summary>
internal sealed record SdcPolyLight(int LightMap, int Atlas, Func<Vec3, (float U, float V)> Uv);

/// <summary>
/// Turns a solid-leaf <see cref="BspTree"/> into the nodes, surfaces, vertices and leaves of a UModel (SDC-FORMAT.md
/// section 8), the way the stock BSP holds them: each node one polygon, the further polygons on its plane chained to it
/// through iPlane, NF_NotCsg on nodes that only split, a surface per brush face, empty leaves numbered for the zones.
/// The world model adds render sections; brush models (volumes, movers) have none.
/// </summary>
internal sealed class SdcBspModel
{
    private const byte NotCsg = 0x01;
    private const uint PolyInvisible = 0x00000001;
    private const uint PolyPortal = 0x04000000;

    private readonly UModelData _m = new() { NumSharedSides = 4 };
    private readonly Dictionary<Vector3, int> _points = new();
    private readonly Dictionary<Vector3, int> _vectors = new();
    private readonly Dictionary<Surface, int> _surfs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BspLeaf, int> _leaves = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(int Material, uint Flags, int Atlas), int> _sections = new();
    private readonly Func<Surface, SdcSurfaceRefs> _refs;
    private readonly IReadOnlyDictionary<BspNode, List<Poly>> _extra;
    private readonly Func<Poly, SdcPolyLight?>? _light;
    private readonly Func<Poly, bool> _drawn;
    private readonly bool _zoned;
    private readonly List<List<float>> _sectionVertices = new();
    private int _blankSurf = -1;

    private SdcBspModel(Func<Surface, SdcSurfaceRefs> refs, IReadOnlyDictionary<BspNode, List<Poly>>? extra, Func<Poly, bool>? drawn,
        Func<Poly, SdcPolyLight?>? light, bool zoned)
    {
        _zoned = zoned;
        _refs = refs;
        _extra = extra ?? new Dictionary<BspNode, List<Poly>>();
        _drawn = drawn ?? (_ => false);
        _light = light;
    }

    /// <summary>
    /// The model of <paramref name="tree"/>. <paramref name="extra"/> are polygons on non-CSG nodes (surfaces that do
    /// not collide); <paramref name="drawn"/> says which polygons go into the render sections, <paramref name="light"/>
    /// their lightmap. <paramref name="zoned"/> (the world) numbers the empty leaves and gives the nodes the zones of
    /// the tree's leaves; a brush model has neither.
    /// </summary>
    public static UModelData Build(BspTree tree, Func<Surface, SdcSurfaceRefs> refs, IReadOnlyDictionary<BspNode, List<Poly>>? extra = null,
        Func<Poly, bool>? drawn = null, Func<Poly, SdcPolyLight?>? light = null, bool zoned = false)
    {
        var b = new SdcBspModel(refs, extra, drawn, light, zoned);
        if (tree.Root is BspNode root)
        {
            b.Emit(root);
        }

        b.Finish(tree);
        return b._m;
    }

    private int Emit(BspNode node)
    {
        List<Poly> polys = node.Polys.Concat(_extra.GetValueOrDefault(node) ?? []).Where(p => p.Verts.Length >= 3).ToList();
        if (polys.Count == 0 && node.PortalPiece is { Verts.Length: >= 3 } piece)
        {
            // A zone portal: the sheet's piece on a portal surface, as the stock world holds it (the engine renders
            // through portals by those polygons).
            polys.Add(piece);
        }

        int head = AddNode(node, polys.Count > 0 ? polys[0] : null);
        int last = head;
        foreach (Poly p in polys.Skip(1))
        {
            int next = AddNode(node, p);
            _m.Nodes[last].PlaneLink = next;
            last = next;
        }

        UBspNode h = _m.Nodes[head];
        (h.Back, h.Leaf0, h.Zone0) = Side(node.Back);
        (h.Front, h.Leaf1, h.Zone1) = Side(node.Front);
        h.ZoneMask = _zoned ? Mask(node) : 0;
        foreach (UBspNode coplanar in Chain(head).Skip(1))
        {
            (coplanar.Leaf0, coplanar.Leaf1, coplanar.Zone0, coplanar.Zone1, coplanar.ZoneMask) = (h.Leaf0, h.Leaf1, h.Zone0, h.Zone1, h.ZoneMask);
        }

        return head;
    }

    private IEnumerable<UBspNode> Chain(int head)
    {
        for (int i = head; i >= 0; i = _m.Nodes[i].PlaneLink)
        {
            yield return _m.Nodes[i];
        }
    }

    /// <summary>A child: a node index, or -1 with the leaf (empty leaves only) and its zone.</summary>
    private (int Child, int Leaf, byte Zone) Side(BspElement e)
    {
        if (e is BspNode n)
        {
            return (Emit(n), -1, _zoned ? (byte)Math.Clamp(ZoneOf(n), 0, 63) : (byte)0);
        }

        var leaf = (BspLeaf)e;
        if (!leaf.Empty || !_zoned)
        {
            return (-1, -1, 0);
        }

        if (!_leaves.TryGetValue(leaf, out int index))
        {
            _leaves[leaf] = index = _leaves.Count;
        }

        return (-1, index, (byte)Math.Clamp(leaf.Zone, 0, 63));
    }

    /// <summary>The zone of a subtree's first empty leaf (the stock nodes name one even where the side is a node).</summary>
    private static int ZoneOf(BspElement e) => e switch
    {
        BspLeaf { Empty: true } l => l.Zone,
        BspNode n => ZoneOfFirst(n),
        _ => 0,
    };

    private static int ZoneOfFirst(BspNode n)
    {
        int front = ZoneOf(n.Front);
        return front != 0 ? front : ZoneOf(n.Back);
    }

    private static ulong Mask(BspElement e) => e switch
    {
        BspLeaf { Empty: true } l => 1UL << Math.Clamp(l.Zone, 0, 63),
        BspNode n => Mask(n.Front) | Mask(n.Back),
        _ => 0,
    };

    private int AddNode(BspNode node, Poly? poly)
    {
        var n = new UBspNode
        {
            Plane = new Vector4(F(node.Plane.N), (float)node.Plane.D),
            NodeFlags = node.Csg ? (byte)0 : NotCsg,
            VertPool = _m.Verts.Count,
            FirstVertex = 0,
        };
        int index = _m.Nodes.Count;
        _m.Nodes.Add(n);
        if (poly is null)
        {
            n.Surf = BlankSurf(node.Plane);
            return index;
        }

        n.Surf = Surf(poly);
        n.NumVertices = (byte)Math.Min(poly.Verts.Length, 255);
        foreach (Vec3 v in poly.Verts.Take(n.NumVertices))
        {
            _m.Verts.Add((Point(F(v)), -1));
        }

        (Vector3 min, Vector3 max) = (F(poly.Verts[0]), F(poly.Verts[0]));
        foreach (Vec3 v in poly.Verts)
        {
            (min, max) = (Vector3.Min(min, F(v)), Vector3.Max(max, F(v)));
        }

        Vector3 centre = (min + max) / 2;
        n.ExclusiveSphereBound = new Vector4(centre, poly.Verts.Max(v => Vector3.Distance(centre, F(v))));
        if (_drawn(poly))
        {
            Draw(n, poly);
        }

        return index;
    }

    /// <summary>Adds the node's polygon to the render section of its material, flags and lightmap texture: a fan of
    /// 40-byte vertices (position, normal, texture UV, lightmap UV). Texture UVs are shifted by whole repeats to sit
    /// near zero, as the stock sections hold them.</summary>
    private void Draw(UBspNode n, Poly poly)
    {
        FBspSurf s = _m.Surfs[n.Surf];
        SdcSurfaceRefs refs = _refs(poly.Surface);
        SdcPolyLight? light = _light?.Invoke(poly);
        int atlas = light?.Atlas ?? -1;
        if (!_sections.TryGetValue((s.Material, s.PolyFlags, atlas), out int index))
        {
            _sections[(s.Material, s.PolyFlags, atlas)] = index = _m.Sections.Count;
            _m.Sections.Add(new UBspSection { Material = s.Material, PolyFlags = s.PolyFlags, LightMapTexture = atlas });
            _sectionVertices.Add(new List<float>());
        }

        UBspSection section = _m.Sections[index];
        Surface surface = poly.Surface;
        Vec3 origin = PannedBase(surface);
        double[] u = poly.Verts.Select(v => Vec3.Dot(v - origin, surface.TextureU) / refs.USize).ToArray();
        double[] v = poly.Verts.Select(p => Vec3.Dot(p - origin, surface.TextureV) / refs.VSize).ToArray();
        double du = Math.Round(u.Average()), dv = Math.Round(v.Average());
        List<float> w = _sectionVertices[index];
        for (int i = 0; i < n.NumVertices; i++)
        {
            Vec3 p = poly.Verts[i], normal = poly.Plane.N;
            (float lu, float lv) = light?.Uv(p) ?? (0, 0);
            w.AddRange([(float)p.X, (float)p.Y, (float)p.Z, (float)normal.X, (float)normal.Y, (float)normal.Z, (float)(u[i] - du), (float)(v[i] - dv), lu, lv]);
        }

        n.Section = index;
        n.FirstVertex = section.VertexCount;
        n.LightMap = light?.LightMap ?? -1;
        section.VertexCount += n.NumVertices;
        section.NumNodes++;
        section.Revision = section.NumNodes;
    }

    private int Surf(Poly poly)
    {
        Surface s = poly.Surface;
        if (_surfs.TryGetValue(s, out int index))
        {
            return index;
        }

        SdcSurfaceRefs refs = _refs(s);
        float scale = (float)(s.LightMapScale > 0 ? s.LightMapScale : 32);
        uint flags = unchecked((uint)s.Flags) | (s.Kind == SurfaceKind.Portal ? PolyPortal | PolyInvisible : 0);
        _m.Surfs.Add(new FBspSurf(refs.Material, flags, Point(F(PannedBase(s))), Vector(F(poly.Plane.N)), Vector(F(s.TextureU)),
            Vector(F(s.TextureV)), new Vector4(F(poly.Plane.N), (float)poly.Plane.D), scale, scale, refs.EditorPoly, refs.Actor, refs.BrushPoly));
        _surfs[s] = index = _m.Surfs.Count - 1;
        return index;
    }

    /// <summary>The one surface of the nodes that hold no polygon (zone portal and leaf-fixing splits): invisible.</summary>
    private int BlankSurf(Plane3 plane)
    {
        if (_blankSurf < 0)
        {
            _m.Surfs.Add(new FBspSurf(0, PolyInvisible, Point(Vector3.Zero), Vector(F(plane.N)), Vector(Vector3.UnitX), Vector(Vector3.UnitY),
                new Vector4(F(plane.N), (float)plane.D), 32, 32, null, 0, -1));
            _blankSurf = _m.Surfs.Count - 1;
        }

        return _blankSurf;
    }

    /// <summary>The texture origin with the pans folded in (FPoly has no pans: UE2 folds them into Base).</summary>
    public static Vec3 PannedBase(Surface s) => Geometry.TextureOps.FoldPans(s.TextureBase, s.TextureU, s.TextureV, s.PanU, s.PanV);

    private int Point(Vector3 p)
    {
        if (!_points.TryGetValue(p, out int i))
        {
            _points[p] = i = _m.Points.Count;
            _m.Points.Add(p);
        }

        return i;
    }

    private int Vector(Vector3 v)
    {
        if (!_vectors.TryGetValue(v, out int i))
        {
            _vectors[v] = i = _m.Vectors.Count;
            _m.Vectors.Add(v);
        }

        return i;
    }

    /// <summary>Leaves (each with an empty light list: one None entry both its indices point at), the root state,
    /// bounds and collision hulls.</summary>
    private void Finish(BspTree tree)
    {
        foreach ((BspLeaf leaf, int index) in _leaves.OrderBy(kv => kv.Value))
        {
            _m.Leaves.Add((Math.Clamp(leaf.Zone, 0, 63), index, index));
            _m.Lights.Add(0);
        }

        for (int i = 0; i < _m.Sections.Count; i++)
        {
            _m.Sections[i].Vertices = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_sectionVertices[i].ToArray().AsSpan()).ToArray();
        }

        _m.RootOutside = tree.RootEmpty;
        _m.Linked = false;
        SdcBspBounds.Build(_m);
        if (_m.Points.Count > 0)
        {
            Vector3 min = _m.Points.Aggregate(Vector3.Min), max = _m.Points.Aggregate(Vector3.Max);
            Vector3 centre = (min + max) / 2;
            (_m.BoxMin, _m.BoxMax, _m.BoxValid) = (min, max, true);
            _m.Sphere = new Vector4(centre, Vector3.Distance(min, max) / 2);
        }
    }

    internal static Vector3 F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
