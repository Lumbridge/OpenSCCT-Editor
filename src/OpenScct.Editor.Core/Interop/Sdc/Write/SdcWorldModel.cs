using System.Numerics;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using ShapeTransform = OpenScct.Editor.Core.Geometry.ShapeTransform;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>The object references the world model needs, resolved when the package is written.</summary>
internal sealed record SdcWorldRefs(Func<string?, int> Material, Func<string, int> Actor, Func<int> Model, Func<Brush, int, FPoly> EditorPoly)
{
    public Func<string?, (double U, double V)> TextureScale { get; init; } = _ => (1, 1);
}

/// <summary>
/// The world model of a map written without a source .sdc, built by the editor's own compiler: the CSG of the brushes,
/// the solid-leaf BSP and zones (<see cref="MapCompiler"/>), and lightmaps baked by <see cref="WorldLighting"/> laid out
/// one per brush face into 512 texel BGRA atlases, as the stock editor's lighting build makes them.
/// </summary>
internal static class SdcWorldModel
{
    private const int AtlasSize = 512;
    private const byte FormatBgra8 = 5;

    public static UModelData Build(MapProject p, string? contentRoot, List<Finding> findings, SdcLayout layout, SdcWorldRefs refs, string? projectFolder = null)
    {
        var ctx = new CompileContext(p, new CompileOptions { Target = TargetGame.Original, ContentRoot = contentRoot, ProjectFolder = projectFolder }, null);
        GameplayBuild.SortActors(ctx);
        MapCompiler.BuildWorld(ctx);
        // The world build's own findings; class checks are the export's lint (this context has no generated class data).
        findings.AddRange(ctx.Findings.Where(f => f.Severity != Severity.Info && f.Code != "gameplay.unknown-class"));
        WorldModel world = ctx.World;
        Dictionary<BspNode, List<Poly>> extra = InsertDrawnOnly(world);

        // Every polygon a node holds that is drawn: the colliding faces' BSP fragments and the drawn-only ones.
        var drawn = new HashSet<Poly>(ReferenceEqualityComparer.Instance);
        var visible = new HashSet<Surface>(world.Visible.Select(v => v.Surface), ReferenceEqualityComparer.Instance);
        foreach (Poly poly in Nodes(world.Tree.Root).SelectMany(n => n.Polys.Concat(extra.GetValueOrDefault(n) ?? [])))
        {
            if (visible.Contains(poly.Surface) && !poly.Surface.Invisible && poly.Verts.Length >= 3)
            {
                drawn.Add(poly);
            }
        }

        var looks = new Dictionary<(string?, int), SurfaceLook>();
        SurfaceLook Look(Surface s) => looks.TryGetValue((s.Material, s.Flags), out SurfaceLook? l) ? l
            : looks[(s.Material, s.Flags)] = SurfaceLook.Resolve(ctx.Library, s.Material, s.Flags);

        var lighting = new WorldLighting(ctx);
        lighting.AddSurfaces(world.Visible, poly => ctx.InSkyZone(poly.Centroid() + (poly.Plane.N * 2)));
        lighting.AddMeshOccluders();
        List<LitPoly> lit = drawn.Where(poly => !Look(poly.Surface).Unlit)
            .Select(poly => new LitPoly(poly, ctx.ZoneAt(poly.Centroid() + (poly.Plane.N * 2)))).ToList();
        LightingSettings layoutSettings = p.Lighting.Clone();
        layoutSettings.Mode = LightmapMode.Faithful;
        layoutSettings.AtlasSize = AtlasSize;
        // Each face takes the lights its leaves list, as the editor's own build of these brushes bakes it.
        LightmapResult? baked = lit.Count > 0 ? lighting.BakeSurfaces(lit, layoutSettings, lighting.LeafLists()) : null;
        var lights = new Dictionary<Poly, SdcPolyLight>(ReferenceEqualityComparer.Instance);
        for (int c = 0; c < (baked?.Charts.Count ?? 0); c++)
        {
            LightmapChart chart = baked!.Charts[c];
            foreach (ChartPiece piece in chart.Pieces)
            {
                if (piece.Source is Poly poly)
                {
                    lights[poly] = new SdcPolyLight(c, chart.Atlas, v => AtlasUv(chart, v));
                }
            }
        }

        var byId = p.Brushes.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
        var surfaceRefs = new Dictionary<Surface, SdcSurfaceRefs>(ReferenceEqualityComparer.Instance);
        SdcSurfaceRefs Refs(Surface s)
        {
            if (!surfaceRefs.TryGetValue(s, out SdcSurfaceRefs? r))
            {
                SurfaceLook look = Look(s);
                Brush? brush = byId.GetValueOrDefault(s.BrushId);
                // The cooked copy keeps a surface's source face only on portals and mirrors (SDC-FORMAT.md section 8).
                bool keepsPoly = layout == SdcLayout.Editor || s.Kind == SurfaceKind.Portal || (s.Flags & PolyFlags.Mirrored) != 0;
                r = new SdcSurfaceRefs(refs.Material(s.Material), brush is null ? 0 : refs.Actor(brush.Id), brush is null ? -1 : s.FaceIndex,
                    brush is null || !keepsPoly ? null : refs.EditorPoly(brush, s.FaceIndex), look.USize, look.VSize)
                { TextureUScale = refs.TextureScale(s.Material).U, TextureVScale = refs.TextureScale(s.Material).V };
                surfaceRefs[s] = r;
            }

            return r;
        }

        UModelData m = SdcBspModel.Build(world.Tree, Refs, extra, drawn.Contains, poly => lights.GetValueOrDefault(poly), zoned: true);
        AddZones(m, world.Zones, ctx, refs);
        if (baked is not null)
        {
            AddLightmaps(m, baked, layout, refs);
        }

        return m;
    }

    private static IEnumerable<BspNode> Nodes(BspElement e)
    {
        if (e is not BspNode n)
        {
            yield break;
        }

        yield return n;
        foreach (BspNode child in Nodes(n.Back).Concat(Nodes(n.Front)))
        {
            yield return child;
        }
    }

    /// <summary>
    /// Drawn surfaces that are not CSG faces of the tree (non-solid brushes, NotSolid faces: the tree only splits by
    /// them) go in as NotCsg nodes, as the stock BSP holds them: each pushed down the tree, cut by the planes it
    /// crosses, and set on a new node at the leaf it reaches, whose two sides are that leaf's space.
    /// </summary>
    private static Dictionary<BspNode, List<Poly>> InsertDrawnOnly(WorldModel world)
    {
        var extra = new Dictionary<BspNode, List<Poly>>(ReferenceEqualityComparer.Instance);
        var inTree = new HashSet<Surface>(world.Solid.Select(p => p.Surface), ReferenceEqualityComparer.Instance);
        foreach (Poly poly in world.Visible.Where(p => !inTree.Contains(p.Surface)))
        {
            Insert(world.Tree, world.Tree.Root, poly, extra);
        }

        world.Tree.Reindex();
        return extra;
    }

    private static void Insert(BspTree tree, BspElement e, Poly poly, Dictionary<BspNode, List<Poly>> extra)
    {
        if (e is BspLeaf leaf)
        {
            if (leaf.Parent is not { } parent)
            {
                return; // a world with no CSG faces at all: nothing to hang it on
            }

            var node = new BspNode(poly.Plane, csg: false) { Front = leaf, Back = new BspLeaf(leaf.Empty) { Zone = leaf.Zone }, Parent = parent };
            if (ReferenceEquals(parent.Front, leaf))
            {
                parent.Front = node;
            }
            else
            {
                parent.Back = node;
            }

            leaf.Parent = node;
            node.Back.Parent = node;
            extra[node] = [poly];
            return;
        }

        var n = (BspNode)e;
        switch (poly.Classify(n.Plane))
        {
            case PlaneSide.Front:
                Insert(tree, n.Front, poly, extra);
                break;
            case PlaneSide.Back:
                Insert(tree, n.Back, poly, extra);
                break;
            case PlaneSide.On:
                Insert(tree, Vec3.Dot(poly.Plane.N, n.Plane.N) > 0 ? n.Front : n.Back, poly, extra);
                break;
            default:
                (Poly? front, Poly? back) = poly.Split(n.Plane);
                if (front is not null)
                {
                    Insert(tree, n.Front, front, extra);
                }

                if (back is not null)
                {
                    Insert(tree, n.Back, back, extra);
                }

                break;
        }
    }

    /// <summary>The zone table: each zone's actor (none for zone 0, the LevelInfo's) and the zones it reaches through
    /// portals, itself included.</summary>
    private static void AddZones(UModelData m, ZoneResult zones, CompileContext ctx, SdcWorldRefs refs)
    {
        int n = Math.Min(zones.Zones.Count, 64);
        for (int i = 0; i < n; i++)
        {
            ulong reach = 0;
            for (int j = 0; j < n; j++)
            {
                if (i == j || zones.Distance[i, j] != 255)
                {
                    reach |= 1UL << j;
                }
            }

            string? actor = i < ctx.ZoneActors.Count && i > 0 ? ctx.ZoneActors[i] : null;
            m.Zones.Add((actor is null ? 0 : refs.Actor(actor), reach, 0));
        }
    }

    /// <summary>
    /// The baked lightmaps: an FLightMap per chart (editor copies only: atlas, surface, place and size, the
    /// world-to-texel matrix and the chart's axes; no per-light records, the stock editor's incremental relighting
    /// data) and the atlases as BGRA8 FLightMapTextures.
    /// </summary>
    private static void AddLightmaps(UModelData m, LightmapResult baked, SdcLayout layout, SdcWorldRefs refs)
    {
        int model = refs.Model();
        if (layout == SdcLayout.Editor)
        {
            var nodeOfChart = new Dictionary<int, UBspNode>();
            foreach (UBspNode n in m.Nodes.Where(n => n.LightMap >= 0))
            {
                nodeOfChart.TryAdd(n.LightMap, n);
            }

            for (int c = 0; c < baked.Charts.Count; c++)
            {
                LightmapChart chart = baked.Charts[c];
                UBspNode? node = nodeOfChart.GetValueOrDefault(c);
                m.LightMaps.Add(new ULightMapRecord
                {
                    // Atlas, surface, the zone the face looks into (ShipD's records hold its 23 zones there), place, size.
                    Header = [chart.Atlas, node?.Surf ?? 0, node?.Zone1 ?? 0, chart.X, chart.Y, chart.Width, chart.Height],
                    Matrix = WorldToTexel(chart),
                    Axes = Floats(chart.Origin, chart.StepU, chart.StepV),
                    Owner = model,
                });
            }
        }

        for (int a = 0; a < baked.Atlases.Count; a++)
        {
            // The baker trims an atlas to what it holds; the stock textures are whole 512 squares (charts keep their
            // places, so the UVs are taken against the full size).
            byte[] rgba = baked.Rgba(a);
            (int w, int h) = baked.Atlases[a];
            var bgra = new byte[AtlasSize * AtlasSize * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int from = ((y * w) + x) * 4, to = ((y * AtlasSize) + x) * 4;
                    (bgra[to], bgra[to + 1], bgra[to + 2], bgra[to + 3]) = (rgba[from + 2], rgba[from + 1], rgba[from], rgba[from + 3]);
                }
            }

            m.LightMapTextures.Add(new ULightMapTexture
            {
                Owner = model,
                LightMaps = Enumerable.Range(0, baked.Charts.Count).Where(c => baked.Charts[c].Atlas == a).ToList(),
                I32 = 1,
                Data = bgra,
                Format = FormatBgra8,
                Width = AtlasSize,
                Height = AtlasSize,
                Revision = 1,
            });
        }
    }

    private static (float U, float V) AtlasUv(LightmapChart chart, Vec3 world)
    {
        TexelPoint t = chart.TexelOf(world);
        return ((float)((chart.X + t.X) / AtlasSize), (float)((chart.Y + t.Y) / AtlasSize));
    }

    /// <summary>The FMatrix (column-major) taking a world point to the chart's texels, as the stock records hold it:
    /// texel = (P - Origin) . Step / |Step|^2 on each axis, the plane normal reversed as the third row.</summary>
    private static byte[] WorldToTexel(LightmapChart c)
    {
        Vec3 u = c.StepU / c.StepU.LengthSquared, v = c.StepV / c.StepV.LengthSquared;
        float[] cols =
        [
            (float)u.X, (float)v.X, (float)-c.Normal.X, 0,
            (float)u.Y, (float)v.Y, (float)-c.Normal.Y, 0,
            (float)u.Z, (float)v.Z, (float)-c.Normal.Z, 0,
            (float)-Vec3.Dot(c.Origin, u), (float)-Vec3.Dot(c.Origin, v), 0, 1,
        ];
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(cols.AsSpan()).ToArray();
    }

    private static byte[] Floats(params Vec3[] vs) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(vs.SelectMany(v => new[] { (float)v.X, (float)v.Y, (float)v.Z }).ToArray().AsSpan()).ToArray();
}

/// <summary>
/// The model of a volume or mover made by the writer: its faces in their own (Polys) space, as every stock volume and
/// mover model holds them (the tree's points equal the Polys' box whatever the actor's Location and PrePivot; the
/// engine moves a check into the actor's space), in a solid-leaf BSP, outside empty, so the engine's point and line
/// checks find its inside.
/// </summary>
internal static class SdcBrushBsp
{
    public static UModelData Build(IReadOnlyList<FPoly> polys, string owner)
    {
        var faces = new List<Poly>();
        for (int i = 0; i < polys.Count; i++)
        {
            FPoly f = polys[i];
            Vec3[] verts = f.Vertices.Select(v => new Vec3(v.X, v.Y, v.Z)).ToArray();

            if (Plane3.TryFromPolygon(verts, out Plane3 plane))
            {
                faces.Add(new Poly(verts, plane, new Surface { BrushId = owner, FaceIndex = i, Flags = unchecked((int)f.PolyFlags) }));
            }
        }

        BspTree tree = BspTree.Build(faces, rootEmpty: true);
        return SdcBspModel.Build(tree, _ => new SdcSurfaceRefs(0, 0, -1, null, 1, 1));
    }
}
