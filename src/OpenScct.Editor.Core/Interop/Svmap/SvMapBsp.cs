using System.Buffers.Binary;
using System.Text.Json.Nodes;
using OpenScct.Content.Collision;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Svmap;

/// <summary>
/// The world BSP of a converted SVMap as brush recovery reads a compiled map's (<see cref="CookedBsp"/>), so a map that
/// came as an SVMap folder (a downloaded one) gets its brushes back as a cooked .sdc does:
/// <list type="bullet">
/// <item>the node tree and the polygons of the solid surfaces come from <c>sim/collision.bin</c>, which keeps the
/// original nodes in their order (<c>BSPN</c>) with each node's polygon as a fan of triangles, its material path and
/// its original poly flags (<c>TFLX</c>);</item>
/// <item>the texture mapping and the lightmap scale of a surface are solved from the triangle of
/// <c>render/world.glb</c> that lies on it (its texture coordinates and its place in the lightmap atlas);</item>
/// <item>non-solid sheets, which the collision leaves out, are the drawn triangles that lie on no solid surface with
/// empty space on both sides.</item>
/// </list>
/// An SVMap does not keep the surfs themselves, so surfaces are told apart by material, flags and mapping. It does not
/// keep zone portals either, but every node has the zones of the space on its two sides: a portal comes back as the
/// part of a non-solid splitter's plane with empty space of one zone in front and of another behind
/// (<see cref="Portals"/>). A map with no BSP nodes (a native map, built from its own brushes) has nothing to recover.
/// </summary>
public static class SvMapBsp
{
    /// <summary>How far a drawn triangle's centre may lie off a node plane and still be on its polygon (the BSP's
    /// coplanar band is 0.25 units).</summary>
    private const double PlaneBand = 0.3;

    /// <summary>How far either side of a drawn triangle is probed to tell a sheet from a wall.</summary>
    private const double SheetProbe = 0.3;

    /// <summary>A mapping fits a triangle when it gives its corners' texture coordinates within this many texels.</summary>
    private const double TexelFit = 0.5;

    /// <summary>The world BSP of the map in <paramref name="folder"/>, or null when its collision has no BSP nodes.
    /// <paramref name="notes"/> gets what could not be read back. Throws <see cref="InvalidDataException"/> for a node
    /// tree that cannot be walked.</summary>
    public static CookedBsp? Read(string folder, CollisionData data, List<string> notes)
    {
        int index = -1;
        for (int i = 0; i < data.Colliders.Length && index < 0; i++)
        {
            if (data.Colliders[i].Kind == ColliderKind.WorldBsp && data.Colliders[i].BspCount > 0)
            {
                index = i;
            }
        }

        if (index < 0)
        {
            return null;
        }

        Collider world = data.Colliders[index];
        if (!world.HasIdentityTransform)
        {
            throw new InvalidDataException("the world collider is not in world space");
        }

        List<CookedNode> nodes = Nodes(data, world);
        List<Polygon> polygons = Polygons(data, world, nodes, out int skipped);
        Aabb bounds = Aabb.Of(polygons.SelectMany(p => p.Points));
        // The tree alone answers solid or empty, which tells a drawn sheet from a wall.
        CookedBsp tree = CookedBsp.FromParts(nodes, [], [], rootOutside: false, bounds, skipped);

        List<Drawn> drawn = ReadDrawn(folder, notes);
        Match(polygons, drawn);

        var surfs = new SurfTable();
        var faces = new List<CookedFace>(polygons.Count);
        int unmapped = 0;
        foreach (Polygon p in polygons)
        {
            Drawn? d = p.Best;
            Mapping? mapping = d is null ? null : Mapping.Solve(d);
            if (mapping is null && (p.Flags & PolyFlags.Invisible) == 0)
            {
                unmapped++;
            }

            int surf = surfs.Get(p.Material ?? d?.Material, p.Flags, mapping, d);
            nodes[p.Node] = nodes[p.Node] with { Surf = surf };
            faces.Add(new CookedFace(p.Node, surf, p.Points, p.Normal, p.Structural, p.Normal, nodes[p.Node].Distance));
        }

        int sheets = 0, walls = 0;
        foreach (Drawn d in drawn)
        {
            if (d.Matched)
            {
                continue;
            }

            Vec3 c = d.Centre;
            if (!tree.IsOutside(c + (d.Normal * SheetProbe)) || !tree.IsOutside(c - (d.Normal * SheetProbe)))
            {
                walls++;
                continue;
            }

            List<Vec3> points = [d.A, d.B, d.C];
            if (Vec3.Dot(CookedBsp.Newell(points), d.Normal) < 0)
            {
                points.Reverse();
            }

            int flags = PolyFlags.NotSolid | (d.TwoSided ? PolyFlags.TwoSided : 0);
            int surf = surfs.Get(d.Material, flags, Mapping.Solve(d), d);
            faces.Add(new CookedFace(-1, surf, points, d.Normal, structural: false, d.Normal, Vec3.Dot(d.Normal, c)));
            bounds = bounds.Add(d.A).Add(d.B).Add(d.C);
            sheets++;
        }

        int portals = Portals(data, world, nodes, bounds, surfs, faces);
        if (portals > 0)
        {
            notes.Add($"{portals} zone portal pieces were rebuilt where the empty space of two zones meets (an SVMap keeps the zone of every "
                + "BSP leaf but not the outlines of the portals), so the recovered portals span their openings exactly");
        }
        else if (data.Zones.Length > 2)
        {
            notes.Add("no place was found where the empty space of two zones meets, so no zone portals were recovered: a world built "
                + "from the recovered brushes is one zone until portal sheets are added");
        }

        if (drawn.Count == 0)
        {
            notes.Add("the map's render/world.glb has no world surfaces, so the recovered faces have their materials but a default "
                + "texture alignment");
        }
        else if (unmapped > 0)
        {
            notes.Add($"{unmapped} of {polygons.Count} solid surfaces have no drawn triangle to take their texture alignment from: their "
                + "brush faces have the material with a default alignment");
        }

        if (sheets > 0)
        {
            notes.Add($"{sheets} drawn triangles on no solid surface, with empty space on both sides, came back as non-solid sheets");
        }

        if (walls > 0)
        {
            notes.Add($"{walls} drawn triangles lie on no solid surface of the same material and are not sheets (solid on one side): "
                + "they are in the baked render only");
        }

        return CookedBsp.FromParts(nodes, surfs.Surfs, faces, rootOutside: false, bounds, skipped);
    }

    // ---- collision: nodes and solid polygons -----------------------------------------------------------------------

    private static List<CookedNode> Nodes(CollisionData data, Collider world)
    {
        ReadOnlySpan<CollisionBspNode> source = data.BspNodesOf(world);
        int start = (int)world.BspStart;
        var nodes = new List<CookedNode>(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            CollisionBspNode n = source[i];
            var normal = new Vec3(n.Plane.Normal.X, n.Plane.Normal.Y, n.Plane.Normal.Z);
            double length = normal.Length;
            if (!double.IsFinite(length) || length < 1e-12 || !float.IsFinite(n.Plane.W))
            {
                throw new InvalidDataException($"BSP node {i} has no usable plane");
            }

            // Child indices are absolute in the file. A solid surface's node keeps its polygon (the collision leaves out
            // those of sheets and portals, which are not CSG nodes either); NF_NotCsg is 0x01.
            bool csg = n.TriCount != 0 && (n.NodeFlags & 0x01) == 0;
            nodes.Add(new CookedNode(normal / length, n.Plane.W / length, n.Front < 0 ? -1 : n.Front - start, n.Back < 0 ? -1 : n.Back - start, -1, csg));
        }

        return nodes;
    }

    /// <summary>A node's polygon: its outline, the node plane's normal, its material path and original poly flags, and
    /// the largest drawn triangle found on it.</summary>
    private sealed class Polygon(int node, List<Vec3> points, Vec3 normal, double distance, bool structural, string? material, int flags)
    {
        public int Node { get; } = node;

        public List<Vec3> Points { get; } = points;

        public Vec3 Normal { get; } = normal;

        public double Distance { get; } = distance;

        public bool Structural { get; } = structural;

        public string? Material { get; } = material;

        public int Flags { get; } = flags;

        public Aabb Bounds { get; } = Aabb.Of(points).Expand(0.5);

        public Drawn? Best { get; set; }
    }

    private static List<Polygon> Polygons(CollisionData data, Collider world, List<CookedNode> nodes, out int skipped)
    {
        ReadOnlySpan<CollisionBspNode> source = data.BspNodesOf(world);
        byte[]? extras = BakedSource.Chunk(data, "TFLX", data.Materials.Length, 8);
        var polygons = new List<Polygon>();
        skipped = 0;
        for (int i = 0; i < source.Length; i++)
        {
            CollisionBspNode n = source[i];
            if (n.TriCount == 0)
            {
                continue;
            }

            if ((ulong)n.TriStart + n.TriCount > (ulong)data.Triangles.Length)
            {
                skipped++;
                continue;
            }

            ReadOnlySpan<CollisionTriangle> triangles = data.Triangles.AsSpan().Slice((int)n.TriStart, (int)n.TriCount);
            List<Vec3>? points = Outline(data, triangles);
            if (points is not null)
            {
                points = CookedBsp.Unfold(points);
            }

            Vec3 newell = points is null ? Vec3.Zero : CookedBsp.Newell(points);
            if (points is null || points.Count < 3 || newell.LengthSquared < 1e-24)
            {
                skipped++;
                continue;
            }

            // The converter winds the fan so its front follows the node plane; the plane decides the facing.
            Vec3 normal = nodes[i].Normal;
            if (Vec3.Dot(newell, normal) < 0)
            {
                points.Reverse();
            }

            int material = triangles[0].Material;
            CollisionMaterial m = material < data.Materials.Length ? data.Materials[material] : default;
            int flags = extras is not null && material < data.Materials.Length
                ? unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(extras.AsSpan(material * 8)))
                : ((m.Flags & CollisionMaterialFlags.Invisible) != 0 ? PolyFlags.Invisible : 0)
                  | ((m.Flags & CollisionMaterialFlags.Semisolid) != 0 ? PolyFlags.Semisolid : 0);
            string? path = material < data.Materials.Length && m.Name != CollisionData.None && m.Name < (uint)data.Strings.Length
                ? data.Strings[(int)m.Name]
                : null;
            polygons.Add(new Polygon(i, points, normal, nodes[i].Distance, (n.NodeFlags & 0x01) == 0, string.IsNullOrEmpty(path) ? null : path, flags));
        }

        return polygons;
    }

    /// <summary>The outline of a node's triangles: the edges only one triangle has, followed round. Null when they do
    /// not close into one loop.</summary>
    private static List<Vec3>? Outline(CollisionData data, ReadOnlySpan<CollisionTriangle> triangles)
    {
        var edges = new List<(uint A, uint B)>(triangles.Length * 3);
        var all = new HashSet<(uint, uint)>();
        foreach (CollisionTriangle t in triangles)
        {
            if (Math.Max(t.I0, Math.Max(t.I1, t.I2)) >= (uint)data.Vertices.Length || t.I0 == t.I1 || t.I1 == t.I2 || t.I2 == t.I0)
            {
                continue;
            }

            foreach ((uint, uint) e in (ReadOnlySpan<(uint, uint)>)[(t.I0, t.I1), (t.I1, t.I2), (t.I2, t.I0)])
            {
                if (all.Add(e))
                {
                    edges.Add(e);
                }
            }
        }

        var next = new Dictionary<uint, uint>();
        uint? first = null;
        foreach ((uint a, uint b) in edges)
        {
            if (all.Contains((b, a)))
            {
                continue;
            }

            if (!next.TryAdd(a, b))
            {
                return null;
            }

            first ??= a;
        }

        if (first is not { } start || next.Count < 3)
        {
            return null;
        }

        var points = new List<Vec3>(next.Count);
        uint at = start;
        for (int i = 0; i < next.Count; i++)
        {
            OpenScct.Core.FVector v = data.Vertices[(int)at];
            var p = new Vec3(v.X, v.Y, v.Z);
            if (points.Count == 0 || (points[^1] - p).LengthSquared >= 1e-6)
            {
                points.Add(p);
            }

            if (!next.TryGetValue(at, out at))
            {
                return null;
            }
        }

        if (at != start)
        {
            return null;
        }

        if (points.Count > 1 && (points[0] - points[^1]).LengthSquared < 1e-6)
        {
            points.RemoveAt(points.Count - 1);
        }

        return points;
    }

    // ---- zone portals ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the zone portals: for every splitter with a non-solid node without a polygon on its plane (what the
    /// collision keeps of a portal), the plane inside the splitter's region is cut by the tree in front of it and behind
    /// it, and the pieces with empty space of two different zones on their sides are portal faces (one surf per
    /// splitter and pair of zones, so each comes back as a portal sheet of its own). Returns how many pieces were made.
    /// </summary>
    private static int Portals(CollisionData data, Collider world, List<CookedNode> nodes, Aabb bounds, SurfTable surfs, List<CookedFace> faces)
    {
        if (nodes.Count == 0 || bounds.IsEmpty)
        {
            return 0;
        }

        ReadOnlySpan<CollisionBspNode> source = data.BspNodesOf(world);
        int start = (int)world.BspStart;
        var zones = new (int Front, int Back)[nodes.Count];
        var candidate = new bool[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            zones[i] = (source[i].ZoneFront, source[i].ZoneBack);
            int member = i;
            for (int guard = 0; member >= 0 && member < nodes.Count && guard <= nodes.Count && !candidate[i]; guard++)
            {
                candidate[i] = source[member].TriCount == 0 && (source[member].NodeFlags & 0x01) != 0;
                member = source[member].Coplanar < 0 ? -1 : source[member].Coplanar - start;
            }
        }

        Aabb box = bounds.Expand(Sdc.Recovery.BrushRecovery.BoxMargin);
        double reach = (box.Max - box.Min).Length;
        int made = 0;
        var surfOf = new Dictionary<(int, int, int), int>();
        var stack = new Stack<(int Node, bool Outside, Clip? Clips)>();
        stack.Push((0, false, null));
        while (stack.Count > 0)
        {
            (int index, bool outside, Clip? clips) = stack.Pop();
            CookedNode n = nodes[index];
            if (n.Front >= 0)
            {
                stack.Push((n.Front, outside || n.Csg, new Clip(n.Normal, n.Distance, true, clips)));
            }

            if (n.Back >= 0)
            {
                stack.Push((n.Back, outside && !n.Csg, new Clip(n.Normal, n.Distance, false, clips)));
            }

            if (!candidate[index] || Section(n, box.Center, reach, clips) is not { } section)
            {
                continue;
            }

            var front = new List<Piece>();
            Filter(nodes, zones, section, n.Front, outside || n.Csg, zones[index].Front, n.Normal, 1, front);
            foreach (Piece f in front)
            {
                if (!f.Outside || f.Zone == 0 || f.Zone >= data.Zones.Length)
                {
                    continue;
                }

                var behind = new List<Piece>();
                Filter(nodes, zones, f.Points, n.Back, outside && !n.Csg, zones[index].Back, n.Normal, -1, behind);
                foreach (Piece b in behind)
                {
                    if (!b.Outside || b.Zone == 0 || b.Zone == f.Zone || b.Zone >= data.Zones.Length)
                    {
                        continue;
                    }

                    (int, int, int) key = (index, Math.Min(f.Zone, b.Zone), Math.Max(f.Zone, b.Zone));
                    if (!surfOf.TryGetValue(key, out int surf))
                    {
                        surfOf[key] = surf = surfs.Add(new CookedSurf(null, CookedBsp.PortalFlag | PolyFlags.Invisible | PolyFlags.NotSolid, Vec3.Zero,
                            Vec3.Zero, Vec3.Zero, BrushFace.DefaultLightMapScale, null, null, Vec3.Zero));
                    }

                    List<Vec3> points = b.Points;
                    if (Vec3.Dot(CookedBsp.Newell(points), n.Normal) < 0)
                    {
                        points.Reverse();
                    }

                    faces.Add(new CookedFace(index, surf, points, n.Normal, structural: false, n.Normal, n.Distance));
                    made++;
                }
            }
        }

        return made;
    }

    /// <summary>One half-space of a node's region: in front of (or behind) an ancestor's plane.</summary>
    private sealed record Clip(Vec3 Normal, double Distance, bool Front, Clip? Next);

    /// <summary>A piece of a splitter's plane in one leaf: whether the leaf is empty space, and its zone.</summary>
    private readonly record struct Piece(List<Vec3> Points, bool Outside, int Zone);

    /// <summary>The part of a node's plane inside the node's region (a square larger than the map, cut by its ancestors).</summary>
    private static List<Vec3>? Section(CookedNode n, Vec3 centre, double reach, Clip? clips)
    {
        Vec3 origin = centre - (n.Normal * (Vec3.Dot(n.Normal, centre) - n.Distance));
        Vec3 u = Vec3.Cross(Math.Abs(n.Normal.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX, n.Normal).Normalized();
        Vec3 v = Vec3.Cross(n.Normal, u);
        List<Vec3>? poly = [origin - (u * reach) - (v * reach), origin + (u * reach) - (v * reach), origin + (u * reach) + (v * reach), origin - (u * reach) + (v * reach)];
        for (Clip? c = clips; c is not null && poly is not null; c = c.Next)
        {
            (List<Vec3>? front, List<Vec3>? back) = PolygonOps.Split(poly, c.Normal, c.Distance);
            poly = Usable(c.Front ? front : back, n.Normal);
        }

        return poly;
    }

    private static List<Vec3>? Usable(List<Vec3>? poly, Vec3 normal) =>
        poly is { Count: >= 3 } && Math.Abs(PolygonOps.Area(poly, normal)) > 1e-3 ? poly : null;

    /// <summary>Cuts a polygon on a splitter's plane by the tree on one side of the splitter (<paramref name="side"/> 1
    /// in front, -1 behind), down to the leaves that touch the plane there.</summary>
    private static void Filter(List<CookedNode> nodes, (int Front, int Back)[] zones, List<Vec3> polygon, int child, bool outside, int zone, Vec3 normal,
        int side, List<Piece> leaves)
    {
        var stack = new Stack<(List<Vec3> Points, int Node, bool Outside, int Zone)>();
        stack.Push((polygon, child, outside, zone));
        while (stack.Count > 0)
        {
            (List<Vec3> points, int index, bool state, int at) = stack.Pop();
            if (index < 0)
            {
                leaves.Add(new Piece(points, state, at));
                continue;
            }

            CookedNode m = nodes[index];
            double most = 0;
            foreach (Vec3 p in points)
            {
                most = Math.Max(most, Math.Abs(Vec3.Dot(m.Normal, p) - m.Distance));
            }

            List<Vec3>? front, back;
            if (most < 0.01)
            {
                // A plane on the splitter's own: the space beside the polygon is all on one side of it.
                bool inFront = Vec3.Dot(m.Normal, normal) * side > 0;
                (front, back) = inFront ? (points, null) : ((List<Vec3>?)null, points);
            }
            else
            {
                (front, back) = PolygonOps.Split(points, m.Normal, m.Distance);
                front = Usable(front, normal);
                back = Usable(back, normal);
            }

            if (front is not null)
            {
                stack.Push((front, m.Front, state || m.Csg, zones[index].Front));
            }

            if (back is not null)
            {
                stack.Push((back, m.Back, state && !m.Csg, zones[index].Back));
            }
        }
    }

    // ---- render: drawn triangles -----------------------------------------------------------------------------------

    /// <summary>One triangle of the drawn world BSP: corners in UE space, the side it faces, its material path and size,
    /// its texture coordinates in texels and, when it is lightmapped, its place in the atlas in texels.</summary>
    private sealed class Drawn
    {
        public Vec3 A { get; init; }

        public Vec3 B { get; init; }

        public Vec3 C { get; init; }

        public Vec3 Normal { get; init; }

        public string? Material { get; init; }

        public bool TwoSided { get; init; }

        /// <summary>Texel coordinates of the three corners (x, y each), or null without texture coordinates.</summary>
        public double[]? Texels { get; init; }

        /// <summary>Lightmap texel coordinates of the three corners, or null when it is not lightmapped.</summary>
        public double[]? LightTexels { get; init; }

        public bool Matched { get; set; }

        public Vec3 Centre => (A + B + C) / 3;

        public double Area => Vec3.Cross(B - A, C - A).Length / 2;
    }

    private static List<Drawn> ReadDrawn(string folder, List<string> notes)
    {
        var drawn = new List<Drawn>();
        string render = Path.Combine(folder, "render");
        string path = Path.Combine(render, "world.glb");
        if (!File.Exists(path))
        {
            return drawn;
        }

        try
        {
            var materials = new Dictionary<string, LibraryMaterial>(StringComparer.Ordinal);
            foreach ((string key, LibraryMaterial material) in ContentLibrary.ReadMaterials(folder))
            {
                materials[key] = material;
            }

            JsonObject? table = File.Exists(Path.Combine(render, "materials.json"))
                ? JsonNode.Parse(File.ReadAllBytes(Path.Combine(render, "materials.json")))?["materials"] as JsonObject
                : null;
            Glb glb = Glb.Load(File.ReadAllBytes(path));
            JsonArray gltfMaterials = glb.Json["materials"] as JsonArray ?? [];
            var atlases = new Dictionary<string, (int Width, int Height)?>(StringComparer.Ordinal);
            foreach (JsonNode? node in glb.Array("nodes"))
            {
                if (node?["extras"]?["svmap"] is not JsonObject sv || sv["role"]?.GetValue<string>() != "bsp" || node["mesh"] is not JsonValue mesh)
                {
                    continue;
                }

                JsonArray prims = glb.Array("meshes")[mesh.GetValue<int>()]!["primitives"]!.AsArray();
                foreach (Glb.UePrimitive up in glb.ReadUePrimitives(mesh.GetValue<int>()))
                {
                    JsonObject prim = prims[up.Index]!.AsObject();
                    LibraryMaterial? material = up.Material is null ? null : materials.GetValueOrDefault(up.Material);
                    string? source = material?.Source ?? (up.Material is null ? null : table?[up.Material]?["source"]?.GetValue<string>());
                    bool twoSided = up.Material is not null && table?[up.Material]?["flags"]?["two_sided"]?.GetValue<bool>() == true;
                    double usize = Math.Max(1, material?.USize ?? 0), vsize = Math.Max(1, material?.VSize ?? 0);
                    float[]? uvs = material is null ? null : up.Uvs;

                    // The atlas a lightmapped primitive is in, at the size its charts were laid out (a 256 atlas is one
                    // the original squeezed from 512).
                    string? lightmap = prim["material"] is JsonValue mv ? gltfMaterials[mv.GetValue<int>()]?["extras"]?["scct_lightmap"]?.GetValue<string>() : null;
                    (int Width, int Height)? atlas = null;
                    if (lightmap is not null && !atlases.TryGetValue(lightmap, out atlas))
                    {
                        atlas = Png.SizeOf(Path.Combine(render, lightmap)) is { Width: > 0, Height: > 0 } size
                            ? size is (256, 256) ? (512, 512) : size
                            : null;
                        atlases[lightmap] = atlas;
                    }

                    float[]? uv2 = atlas is not null && prim["attributes"]?["TEXCOORD_1"] is JsonValue t1 ? glb.ReadFloats(t1.GetValue<int>(), 2) : null;
                    for (int t = 0; t + 2 < up.Indices.Length; t += 3)
                    {
                        int a = up.Indices[t], b = up.Indices[t + 1], c = up.Indices[t + 2];
                        Vec3 pa = up.Positions[a], pb = up.Positions[b], pc = up.Positions[c];
                        Vec3 cross = Vec3.Cross(pb - pa, pc - pa);
                        if (cross.Length < 1e-6)
                        {
                            continue;
                        }

                        // The side the triangle is seen from is the one its normals face.
                        Vec3 normal = up.Normals[a] + up.Normals[b] + up.Normals[c];
                        normal = normal.LengthSquared > 1e-8 ? normal.Normalized() : cross.Normalized();
                        drawn.Add(new Drawn
                        {
                            A = pa,
                            B = pb,
                            C = pc,
                            Normal = normal,
                            Material = source,
                            TwoSided = twoSided,
                            Texels = uvs is null ? null : [uvs[2 * a] * usize, uvs[(2 * a) + 1] * vsize, uvs[2 * b] * usize, uvs[(2 * b) + 1] * vsize, uvs[2 * c] * usize, uvs[(2 * c) + 1] * vsize],
                            LightTexels = uv2 is null || atlas is not { } size2
                                ? null
                                : [uv2[2 * a] * size2.Width, uv2[(2 * a) + 1] * size2.Height, uv2[2 * b] * size2.Width, uv2[(2 * b) + 1] * size2.Height,
                                   uv2[2 * c] * size2.Width, uv2[(2 * c) + 1] * size2.Height],
                        });
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or InvalidDataException
            or ArgumentException or NullReferenceException or KeyNotFoundException or FormatException)
        {
            // The brushes still come back from the collision alone, with their materials.
            drawn.Clear();
            notes.Add($"the map's render/world.glb could not be read ({ex.Message}), so texture alignment and sheets were not recovered");
        }

        return drawn;
    }

    /// <summary>Gives each drawn triangle the solid polygon it lies on (same facing, same material, its centre inside),
    /// and each polygon the largest triangle on it.</summary>
    private static void Match(List<Polygon> polygons, List<Drawn> drawn)
    {
        var byPlane = new Dictionary<(long, long, long, long), List<Polygon>>();
        foreach (Polygon p in polygons)
        {
            (long, long, long, long) key = PlaneKey(p.Normal, p.Distance);
            if (!byPlane.TryGetValue(key, out List<Polygon>? list))
            {
                byPlane[key] = list = [];
            }

            list.Add(p);
        }

        foreach (Drawn d in drawn)
        {
            Vec3 c = d.Centre;
            (long x, long y, long z, long w) = PlaneKey(d.Normal, Vec3.Dot(d.Normal, c));
            Polygon? found = null;
            for (long dx = -1; dx <= 1 && found is null; dx++)
            {
                for (long dy = -1; dy <= 1 && found is null; dy++)
                {
                    for (long dz = -1; dz <= 1 && found is null; dz++)
                    {
                        for (long dw = -1; dw <= 1 && found is null; dw++)
                        {
                            if (byPlane.TryGetValue((x + dx, y + dy, z + dz, w + dw), out List<Polygon>? list))
                            {
                                found = list.Find(p => On(p, d, c));
                            }
                        }
                    }
                }
            }

            if (found is null)
            {
                continue;
            }

            d.Matched = true;
            if (d.Texels is not null && (found.Best is null || d.Area > found.Best.Area))
            {
                found.Best = d;
            }
        }
    }

    private static (long, long, long, long) PlaneKey(Vec3 n, double d) =>
        ((long)Math.Round(n.X * 64), (long)Math.Round(n.Y * 64), (long)Math.Round(n.Z * 64), (long)Math.Floor(d / 4));

    private static bool On(Polygon p, Drawn d, Vec3 c)
    {
        if (Vec3.Dot(p.Normal, d.Normal) < 0.999 || Math.Abs(Vec3.Dot(p.Normal, c) - p.Distance) > PlaneBand || !p.Bounds.Contains(c)
            || (p.Material is not null && d.Material is not null && !p.Material.Equals(d.Material, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Inside a convex outline: on the same side of every edge (within a hundredth of a unit).
        bool left = false, right = false;
        for (int i = 0; i < p.Points.Count; i++)
        {
            Vec3 a = p.Points[i], e = p.Points[(i + 1) % p.Points.Count] - a;
            double length = e.Length;
            if (length < 1e-9)
            {
                continue;
            }

            double side = Vec3.Dot(Vec3.Cross(e, c - a), p.Normal) / length;
            left |= side > 0.01;
            right |= side < -0.01;
        }

        return !(left && right);
    }

    // ---- surfs -----------------------------------------------------------------------------------------------------

    /// <summary>A texture mapping as a surf has it (u = dot(p - Base, U), in texels) and a lightmap scale (0: unknown).</summary>
    private sealed record Mapping(Vec3 Base, Vec3 U, Vec3 V, double LightMapScale)
    {
        public static Mapping? Solve(Drawn d)
        {
            if (d.Texels is not { } t || Axes(d, t) is not { } axes)
            {
                return null;
            }

            // Float noise off the axes goes, then the base is where the first corner reads its own coordinates.
            Vec3 u = Tidy(axes.U), v = Tidy(axes.V);
            Vec3 m = Vec3.Cross(u, v);
            double du = Vec3.Dot(u, Vec3.Cross(v, m)), dv = Vec3.Dot(v, Vec3.Cross(m, u));
            if (Math.Abs(du) < 1e-12 || Math.Abs(dv) < 1e-12)
            {
                return null;
            }

            Vec3 baseAt = d.A - (Vec3.Cross(v, m) / du * t[0]) - (Vec3.Cross(m, u) / dv * t[1]);
            if (!double.IsFinite(baseAt.X + baseAt.Y + baseAt.Z))
            {
                return null;
            }

            double scale = 0;
            if (d.LightTexels is { } l && Steps(d, l) is { } steps)
            {
                scale = TidyScale(Math.Sqrt(steps.SU.Length * steps.SV.Length));
            }

            return new Mapping(baseAt, u, v, scale);
        }

        public bool Fits(Drawn d)
        {
            if (d.Texels is not { } t)
            {
                return false;
            }

            ReadOnlySpan<Vec3> corners = [d.A, d.B, d.C];
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(Vec3.Dot(corners[i] - Base, U) - t[2 * i]) > TexelFit || Math.Abs(Vec3.Dot(corners[i] - Base, V) - t[(2 * i) + 1]) > TexelFit)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>World units per texel along each texel axis (world = origin + x SU + y SV), from the corners.</summary>
        private static (Vec3 SU, Vec3 SV)? Steps(Drawn d, double[] t)
        {
            double x1 = t[2] - t[0], y1 = t[3] - t[1], x2 = t[4] - t[0], y2 = t[5] - t[1];
            double det = (x1 * y2) - (x2 * y1);
            if (Math.Abs(det) < 1e-9 || !double.IsFinite(det))
            {
                return null;
            }

            Vec3 e1 = d.B - d.A, e2 = d.C - d.A;
            return (((e1 * y2) - (e2 * y1)) / det, ((e2 * x1) - (e1 * x2)) / det);
        }

        /// <summary>The texel gradients in the triangle's plane: U.SU = 1, U.SV = 0 and the same for V.</summary>
        private static (Vec3 U, Vec3 V)? Axes(Drawn d, double[] t)
        {
            if (Steps(d, t) is not { } s)
            {
                return null;
            }

            Vec3 n = Vec3.Cross(s.SU, s.SV);
            double du = Vec3.Dot(s.SU, Vec3.Cross(s.SV, n)), dv = Vec3.Dot(s.SV, Vec3.Cross(n, s.SU));
            if (Math.Abs(du) < 1e-18 || Math.Abs(dv) < 1e-18)
            {
                return null;
            }

            Vec3 u = Vec3.Cross(s.SV, n) / du, v = Vec3.Cross(n, s.SU) / dv;
            double lu = u.Length, lv = v.Length;
            return double.IsFinite(lu + lv) && lu is > 1e-6 and < 1e4 && lv is > 1e-6 and < 1e4 ? (u, v) : null;
        }

        private static Vec3 Tidy(Vec3 v) => new(Tidy(v.X), Tidy(v.Y), Tidy(v.Z));

        /// <summary>A component a hair off a 64th is that 64th (0, 1, 0.5...: what an author's alignment gives); others
        /// keep five decimals.</summary>
        private static double Tidy(double c)
        {
            double r = Math.Round(c * 64) / 64;
            return Math.Abs(c - r) < 3e-4 ? r : Math.Round(c, 5);
        }

        /// <summary>The lightmap scale a baked texel step stands for, an estimate: the next power of two at or above it
        /// (within 2%). The original fits a surface's lightmap into a power-of-two size, so the baked step lies between
        /// half the authored scale and the scale itself, and authored scales are nearly all powers of two.</summary>
        private static double TidyScale(double s) =>
            !double.IsFinite(s) || s < 0.25 || s > 4096 ? 0 : Math.Pow(2, Math.Ceiling(Math.Log2(s / 1.02)));
    }

    /// <summary>The surfs made so far: one per material, flags and mapping (an SVMap does not keep the original's).</summary>
    private sealed class SurfTable
    {
        private readonly Dictionary<(string, int), List<(int Surf, Mapping? Mapping)>> _byLook = new();

        public List<CookedSurf> Surfs { get; } = new();

        public int Get(string? material, int flags, Mapping? mapping, Drawn? drawn)
        {
            (string, int) key = ((material ?? "").ToLowerInvariant(), flags);
            if (!_byLook.TryGetValue(key, out List<(int Surf, Mapping? Mapping)>? list))
            {
                _byLook[key] = list = [];
            }

            foreach ((int surf, Mapping? known) in list)
            {
                if (mapping is null ? known is null : known is not null && known.LightMapScale == mapping.LightMapScale && drawn is not null && known.Fits(drawn))
                {
                    return surf;
                }
            }

            double scale = mapping is { LightMapScale: > 0 } ? mapping.LightMapScale : BrushFace.DefaultLightMapScale;
            int made = Add(new CookedSurf(material, flags, mapping?.Base ?? Vec3.Zero, mapping?.U ?? Vec3.Zero, mapping?.V ?? Vec3.Zero, scale, null, null, Vec3.Zero));
            list.Add((made, mapping));
            return made;
        }

        /// <summary>A surf of its own, shared with nothing (a zone portal's).</summary>
        public int Add(CookedSurf surf)
        {
            Surfs.Add(surf);
            return Surfs.Count - 1;
        }
    }
}
