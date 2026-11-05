using System.Text.Json.Nodes;
using OpenScct.Content.Collision;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// A converted map's world as the lightmapper sees it, keeping the original's lightmap layout: one chart per original
/// lightmap (triangles of one atlas that share a texel-to-world mapping and touch), the BSP and static mesh instances
/// as occluders, and zones from its collision. Relighting an imported map rebakes these charts in place, so its
/// world.glb stays as it is and only the atlas images change.
/// <para>A 256 atlas is one the original squeezed: CompressLightmaps (0x1119edf0) blits each 512 atlas and, with an
/// RGBA8 lightmap format, sums each 2x2 block, scales by 0.25 and stores 256x256. Its charts are laid out at 512, as the
/// original baked them (<see cref="BakeSize"/>), and the bake is squeezed the same way (<see cref="Compress"/>).</para>
/// </summary>
public sealed class ConvertedWorld
{
    private ConvertedWorld(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>Charts placed where the original put them (Atlas, X, Y set).</summary>
    public List<LightmapChart> Charts { get; } = new();

    /// <summary>The atlases: their URI under render/ (as lighting.json lists them) and size.</summary>
    public List<(string Uri, int Width, int Height)> Atlases { get; } = new();

    /// <summary>Per atlas, whether the original squeezed it from twice its size.</summary>
    public List<bool> Compressed { get; } = new();

    public List<OccluderTriangle> Occluders { get; } = new();

    /// <summary>Occluder triangles from static mesh instances (a Source of <see cref="MeshSource"/> and up).</summary>
    public int MeshTriangles { get; private set; }

    public CollisionData? Collision { get; private set; }

    /// <summary>The source map's placed static meshes by actor id (<see cref="SourceInstance"/>).</summary>
    public Dictionary<string, SourceInstance> Instances { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a source instance drawing <paramref name="instanceMesh"/> (a library node, <c>#vc</c> variants
    /// included) shows the static mesh <paramref name="path"/>: a build keeps such an instance as it is.</summary>
    public static bool SameMesh(string? instanceMesh, string path) =>
        instanceMesh is null || path.Equals(instanceMesh.Split('#')[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>Occluder sources at and above this are mesh triangles; below it, chart indices.</summary>
    public const int MeshSource = 1 << 30;

    public int ZoneAt(Vec3 p) => Collision is null ? 0 : BakedSource.ZoneAt(Collision, p);

    /// <summary>The size an atlas's charts are laid out and baked at: twice its stored size when the original squeezed it.</summary>
    public (int Width, int Height) BakeSize(int atlas) =>
        Compressed[atlas] ? (Atlases[atlas].Width * 2, Atlases[atlas].Height * 2) : (Atlases[atlas].Width, Atlases[atlas].Height);

    /// <summary>The original's squeeze of an RGBA8 atlas: each 2x2 block summed, times 0.25, truncated.</summary>
    public static byte[] Compress(byte[] rgba, int width, int height)
    {
        int w = width / 2, h = height / 2;
        var result = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int a = (((2 * y) * width) + (2 * x)) * 4, b = (((2 * y) + 1) * width + (2 * x)) * 4;
                    int sum = rgba[a + k] + rgba[a + 4 + k] + rgba[b + k] + rgba[b + 4 + k];
                    result[(((y * w) + x) * 4) + k] = (byte)(sum * 0.25f);
                }
            }
        }

        return result;
    }

    /// <summary>Reads the map folder's render/world.glb, lighting.json, materials.json and sim/collision.bin.</summary>
    /// <param name="castsShadow">Which static mesh instances (by actor id) cast shadows; null: none do.</param>
    public static ConvertedWorld Load(string folder, Func<string, bool>? castsShadow)
    {
        var w = new ConvertedWorld(folder);
        string render = Path.Combine(folder, "render");
        string collision = Path.Combine(folder, CollisionReader.RelativePath);
        if (File.Exists(collision))
        {
            w.Collision = CollisionReader.Load(collision, strict: false);
        }

        JsonObject lighting = JsonNode.Parse(File.ReadAllBytes(Path.Combine(render, "lighting.json")))!.AsObject();
        var atlasIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonNode? uri in lighting["lightmaps"] as JsonArray ?? [])
        {
            string u = uri!.GetValue<string>();
            (int Width, int Height) size = Png.SizeOf(Path.Combine(render, u)) ?? (0, 0);
            atlasIndex[u] = w.Atlases.Count;
            w.Atlases.Add((u, size.Width, size.Height));
            w.Compressed.Add(size is (256, 256));
        }

        var materials = new MaterialInfo(render);
        Glb glb = Glb.Load(File.ReadAllBytes(Path.Combine(render, "world.glb")));
        JsonArray gltfMaterials = glb.Array("materials");
        var triangles = new List<(int Atlas, Vec3[] P, TexelPoint[] T, Vec3 N, bool TwoSided)>();
        foreach (JsonNode? node in glb.Array("nodes"))
        {
            if (node?["extras"]?["svmap"] is not JsonObject sv || sv["role"]?.GetValue<string>() != "bsp" || node["mesh"] is not JsonValue mesh)
            {
                continue;
            }

            bool sky = sv["sky"]?.GetValue<bool>() == true;
            JsonArray prims = glb.Array("meshes")[mesh.GetValue<int>()]!["primitives"]!.AsArray();
            List<Glb.UePrimitive> ue = glb.ReadUePrimitives(mesh.GetValue<int>());
            for (int pi = 0; pi < prims.Count && pi < ue.Count; pi++)
            {
                JsonObject prim = prims[pi]!.AsObject();
                Glb.UePrimitive up = ue[pi];
                bool twoSided = materials.TwoSided(up.Material);
                OccluderFlags flags = sky ? OccluderFlags.Sky : OccluderFlags.None;
                AlphaMask? mask = materials.Mask(up.Material);
                OccluderTriangle Occluder(int a, int b, int c) => Masked(new OccluderTriangle(up.Positions[a], up.Positions[b], up.Positions[c], flags, -1),
                    mask, up.Uvs, a, b, c);
                string? lightmap = prim["material"] is JsonValue mv ? gltfMaterials[mv.GetValue<int>()]?["extras"]?["scct_lightmap"]?.GetValue<string>() : null;
                float[]? uv2 = !sky && lightmap is not null && atlasIndex.ContainsKey(lightmap) && prim["attributes"]?["TEXCOORD_1"] is JsonValue t1
                    ? glb.ReadFloats(t1.GetValue<int>(), 2)
                    : null;
                for (int t = 0; t + 2 < up.Indices.Length; t += 3)
                {
                    int a = up.Indices[t], b = up.Indices[t + 1], c = up.Indices[t + 2];
                    Vec3 n = (up.Normals[a] + up.Normals[b] + up.Normals[c]).Normalized();
                    // Wind each triangle so its front is the side its normals face (the side light reaches).
                    if (Vec3.Dot(Vec3.Cross(up.Positions[b] - up.Positions[a], up.Positions[c] - up.Positions[a]), n) < 0)
                    {
                        (b, c) = (c, b);
                    }

                    if (uv2 is null)
                    {
                        w.Occluders.Add(Occluder(a, b, c));
                        continue;
                    }

                    int atlas = atlasIndex[lightmap!];
                    (int aw, int ah) = w.BakeSize(atlas);
                    TexelPoint T(int i) => new(uv2[2 * i] * aw, uv2[(2 * i) + 1] * ah);
                    triangles.Add((atlas, [up.Positions[a], up.Positions[b], up.Positions[c]], [T(a), T(b), T(c)], n, twoSided));
                    w.Occluders.Add(Occluder(a, b, c));
                }
            }
        }

        w.BuildCharts(triangles);
        w.ReadInstances(glb);
        if (castsShadow is not null)
        {
            w.AddMeshes(glb, materials, castsShadow);
        }

        return w;
    }

    /// <summary>Groups lightmapped triangles into the original's lightmaps: the same atlas and texel-to-world map, and
    /// touching texel rects.</summary>
    private void BuildCharts(List<(int Atlas, Vec3[] P, TexelPoint[] T, Vec3 N, bool TwoSided)> triangles)
    {
        var groups = new Dictionary<(int, long, long, long, long, long, long, long, long, long, long), List<int>>();
        var maps = new (Vec3 O, Vec3 SU, Vec3 SV)[triangles.Count];
        for (int i = 0; i < triangles.Count; i++)
        {
            (int atlas, Vec3[] p, TexelPoint[] t, Vec3 _, bool _) = triangles[i];
            // World = O + x SU + y SV, solved from the triangle's corners.
            double x1 = t[1].X - t[0].X, y1 = t[1].Y - t[0].Y, x2 = t[2].X - t[0].X, y2 = t[2].Y - t[0].Y;
            double det = (x1 * y2) - (x2 * y1);
            if (Math.Abs(det) < 1e-9)
            {
                continue;
            }

            Vec3 e1 = p[1] - p[0], e2 = p[2] - p[0];
            Vec3 su = ((e1 * y2) - (e2 * y1)) / det, sv = ((e2 * x1) - (e1 * x2)) / det;
            Vec3 o = p[0] - (su * t[0].X) - (sv * t[0].Y);
            maps[i] = (o, su, sv);
            static long Q(double v, double step) => (long)Math.Round(v / step);
            // The texel steps and the plane (normal and distance) name one original lightmap.
            Vec3 n = triangles[i].N;
            var key = (atlas, Q(su.X, 0.05), Q(su.Y, 0.05), Q(su.Z, 0.05), Q(sv.X, 0.05), Q(sv.Y, 0.05), Q(sv.Z, 0.05),
                Q(n.X, 0.01), Q(n.Y, 0.01), Q(n.Z, 0.01), Q(Vec3.Dot(n, p[0]), 1));
            if (!groups.TryGetValue(key, out List<int>? g))
            {
                groups[key] = g = new List<int>();
            }

            g.Add(i);
        }

        foreach (List<int> group in groups.OrderBy(g => g.Key).Select(g => g.Value))
        {
            foreach (List<int> part in TouchingRects(group, triangles))
            {
                AddChart(part, triangles, maps[part[0]]);
            }
        }
    }

    private static List<List<int>> TouchingRects(List<int> group, List<(int Atlas, Vec3[] P, TexelPoint[] T, Vec3 N, bool TwoSided)> triangles)
    {
        var rects = group.Select(i => Rect(triangles[i].T)).ToList();
        var parent = Enumerable.Range(0, group.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        int[] order = Enumerable.Range(0, group.Count).OrderBy(i => rects[i].X0).ToArray();
        for (int a = 0; a < order.Length; a++)
        {
            for (int b = a + 1; b < order.Length && rects[order[b]].X0 <= rects[order[a]].X1 + 1; b++)
            {
                if (rects[order[b]].Y0 <= rects[order[a]].Y1 + 1 && rects[order[b]].Y1 >= rects[order[a]].Y0 - 1)
                {
                    parent[Find(order[a])] = Find(order[b]);
                }
            }
        }

        return Enumerable.Range(0, group.Count).GroupBy(Find).OrderBy(g => g.Key).Select(g => g.Select(i => group[i]).ToList()).ToList();
    }

    private static (double X0, double Y0, double X1, double Y1) Rect(TexelPoint[] t) =>
        (t.Min(p => p.X), t.Min(p => p.Y), t.Max(p => p.X), t.Max(p => p.Y));

    private void AddChart(List<int> part, List<(int Atlas, Vec3[] P, TexelPoint[] T, Vec3 N, bool TwoSided)> triangles, (Vec3 O, Vec3 SU, Vec3 SV) map)
    {
        int atlas = triangles[part[0]].Atlas;
        (int aw, int ah) = BakeSize(atlas);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        Vec3 normal = Vec3.Zero;
        foreach (int i in part)
        {
            (double a, double b, double c, double d) = Rect(triangles[i].T);
            (x0, y0, x1, y1) = (Math.Min(x0, a), Math.Min(y0, b), Math.Max(x1, c), Math.Max(y1, d));
            normal += triangles[i].N;
        }

        // The rect plus one texel of the original's border, inside the atlas.
        int ox = Math.Max(0, (int)Math.Floor(x0) - 1), oy = Math.Max(0, (int)Math.Floor(y0) - 1);
        int ex = Math.Min(aw, (int)Math.Ceiling(x1) + 1), ey = Math.Min(ah, (int)Math.Ceiling(y1) + 1);
        var offset = new TexelPoint(ox, oy);
        var pieces = part.Select(i =>
        {
            Vec3 centre = (triangles[i].P[0] + triangles[i].P[1] + triangles[i].P[2]) / 3;
            return new ChartPiece(triangles[i].T.Select(t => t - offset).ToArray(), ZoneAt(centre + (triangles[i].N * 2)));
        }).ToList();
        var chart = new LightmapChart
        {
            Origin = map.O + (map.SU * ox) + (map.SV * oy),
            StepU = map.SU,
            StepV = map.SV,
            Normal = normal.Normalized(),
            Width = Math.Max(1, ex - ox),
            Height = Math.Max(1, ey - oy),
            TwoSided = triangles[part[0]].TwoSided,
            Pieces = pieces,
            Atlas = atlas,
            X = ox,
            Y = oy,
        };
        Charts.Add(chart.Seal());
    }

    /// <summary>Reads each static mesh instance's mesh and the baked vertex light its <c>#vc</c> variant carries.</summary>
    private void ReadInstances(Glb glb)
    {
        var library = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonNode? node in glb.Array("nodes"))
        {
            if (node?["extras"]?["svmap"]?["role"]?.GetValue<string>() == "libraryMesh" && node["name"]?.GetValue<string>() is { } name &&
                node["mesh"] is JsonValue m)
            {
                library[name] = m.GetValue<int>();
            }
        }

        foreach (JsonNode? inst in glb.Json["extras"]?["svmap"]?["instances"] as JsonArray ?? [])
        {
            if (inst is not JsonObject o || o["actor"]?.GetValue<string>() is not { } actor)
            {
                continue;
            }

            string? mesh = o["mesh"]?.GetValue<string>();
            Dictionary<int, byte[]>? light = null;
            if (o["vertexLit"]?.GetValue<bool>() == true && mesh is not null && library.TryGetValue(mesh, out int index))
            {
                light = glb.ReadUePrimitives(index).Where(p => p.ColorAccessor >= 0).ToDictionary(p => p.Index, p => glb.ReadColors(p.ColorAccessor));
            }

            Instances[actor] = new SourceInstance(mesh, light);
        }
    }

    /// <summary>Static mesh instances (not gameplay objects, not hidden) that cast shadows, as occluders placed as the game
    /// places them.</summary>
    private void AddMeshes(Glb glb, MaterialInfo materials, Func<string, bool> castsShadow)
    {
        var library = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonNode? node in glb.Array("nodes"))
        {
            if (node?["extras"]?["svmap"]?["role"]?.GetValue<string>() == "libraryMesh" && node["name"]?.GetValue<string>() is { } name &&
                node["mesh"] is JsonValue m)
            {
                library[name] = m.GetValue<int>();
            }
        }

        var meshes = new Dictionary<int, List<Glb.UePrimitive>>();
        foreach (JsonNode? inst in glb.Json["extras"]?["svmap"]?["instances"] as JsonArray ?? [])
        {
            if (inst is not JsonObject o || o["hidden"]?.GetValue<bool>() == true || o["gameplay"]?.GetValue<bool>() == true ||
                o["kind"]?.GetValue<string>() != "staticmesh" || o["mesh"]?.GetValue<string>() is not { } meshName ||
                !library.TryGetValue(meshName, out int mesh) || !castsShadow(o["actor"]?.GetValue<string>() ?? ""))
            {
                continue;
            }

            if (!meshes.TryGetValue(mesh, out List<Glb.UePrimitive>? prims))
            {
                meshes[mesh] = prims = glb.ReadUePrimitives(mesh);
            }

            Vec3 location = V(o["location"]), scale = o["scale"] is null ? Vec3.One : V(o["scale"]), pivot = V(o["prePivot"]);
            var rot = new Rot(I(o["rotation"]?["Pitch"]), I(o["rotation"]?["Yaw"]), I(o["rotation"]?["Roll"]));
            bool mirrored = scale.X * scale.Y * scale.Z < 0;
            foreach (Glb.UePrimitive p in prims)
            {
                AlphaMask? mask = materials.Mask(p.Material);
                Vec3 W(int i) => rot.Rotate((p.Positions[i] - pivot).Scale(scale)) + location;
                for (int t = 0; t + 2 < p.Indices.Length; t += 3)
                {
                    int a = p.Indices[t], b = p.Indices[t + 1], c = p.Indices[t + 2];
                    if (mirrored)
                    {
                        (b, c) = (c, b);
                    }

                    Occluders.Add(Masked(new OccluderTriangle(W(a), W(b), W(c), OccluderFlags.None, MeshSource + MeshTriangles), mask, p.Uvs, a, b, c));
                    MeshTriangles++;
                }
            }
        }
    }

    private static OccluderTriangle Masked(OccluderTriangle t, AlphaMask? mask, float[]? uvs, int a, int b, int c)
    {
        if (mask is null || uvs is null)
        {
            return t;
        }

        System.Numerics.Vector2 Uv(int i) => new(uvs[2 * i], uvs[(2 * i) + 1]);
        return t with { Mask = mask, UvA = Uv(a), UvB = Uv(b), UvC = Uv(c) };
    }

    /// <summary>materials.json's flags, and the alpha masks of its masked materials (read once each).</summary>
    private sealed class MaterialInfo(string render)
    {
        private readonly JsonObject _materials = JsonNode.Parse(File.ReadAllBytes(Path.Combine(render, "materials.json")))!["materials"]!.AsObject();
        private readonly Dictionary<string, AlphaMask?> _masks = new(StringComparer.Ordinal);

        public bool TwoSided(string? name) => Flag(name, "two_sided");

        public AlphaMask? Mask(string? name)
        {
            if (name is null || !Flag(name, "masked") || _materials[name]?["flags"]?["texture"]?.GetValue<string>() is not { } texture)
            {
                return null;
            }

            if (!_masks.TryGetValue(texture, out AlphaMask? mask))
            {
                string path = Path.Combine(render, texture);
                var image = File.Exists(path) ? Png.Decode(File.ReadAllBytes(path)) : null;
                _masks[texture] = mask = image is { } im ? AlphaMask.FromRgba(im.Width, im.Height, im.Rgba) : null;
            }

            return mask;
        }

        private bool Flag(string? name, string flag) => name is not null && _materials[name]?["flags"]?[flag]?.GetValue<bool>() == true;
    }

    private static Vec3 V(JsonNode? a) => a is JsonArray { Count: >= 3 } v
        ? new Vec3(v[0]!.GetValue<double>(), v[1]!.GetValue<double>(), v[2]!.GetValue<double>())
        : Vec3.Zero;

    private static int I(JsonNode? n) => n is null ? 0 : (int)n.GetValue<double>();

    /// <summary>Zone ambient (stored units) by zone index, from lighting.json's zone records (AmbientHue, AmbientSaturation,
    /// AmbientBrightness as stored, else their defaults).</summary>
    public Dictionary<int, Vec3> ZoneAmbient()
    {
        var result = new Dictionary<int, Vec3>();
        JsonObject lighting = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Folder, "render", "lighting.json")))!.AsObject();
        foreach (JsonNode? z in lighting["zones"] as JsonArray ?? [])
        {
            if (z?["zoneIndex"] is not JsonValue index)
            {
                continue;
            }

            JsonNode? props = z["props"];
            int P(string key, int fallback) => props?[key] is JsonValue v ? (int)v.GetValue<double>() : fallback;
            result[index.GetValue<int>()] = LightColour.LightmapAmbient(P("AmbientHue", 0), P("AmbientSaturation", 255), P("AmbientBrightness", 0));
        }

        return result;
    }
}

/// <summary>A source map's static mesh instance: the library node it draws (null for none) and, when it is vertex lit,
/// its baked vertex light per glb primitive (RGBA8, stored units, as <see cref="WorldLighting.VertexLight"/> gives it).</summary>
public sealed record SourceInstance(string? Mesh, IReadOnlyDictionary<int, byte[]>? VertexLight);
