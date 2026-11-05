using System.Buffers.Binary;
using System.Text.Json;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports;

/// <summary>
/// Draws the baked world of an imported map (<see cref="MapProject.Baked"/>) from its SVMap <c>render/world.glb</c>,
/// in the unlit and lit views: the BSP (sky excluded) with its textures (Render/BakedSurfaces; flat colours when they
/// cannot be read) and the static meshes placed by <c>extras.svmap.instances</c>, in per-material flat colours, all cut
/// to the storey shown (<see cref="Render.StoreyClip"/>).
/// Instances whose actor is an object of the project are left to the project's own drawing. The glb is read on a
/// worker thread.
/// </summary>
public sealed partial class BakedWorldNode : Node3D
{
    private int _generation;

    /// <summary>The textured BSP sections drawn now (the surface tool picks baked surfaces from them), or null.</summary>
    public IReadOnlyList<OpenScct.Editor.Core.Assets.MeshSection>? Sections { get; private set; }

    /// <summary>Loads the baked world of <paramref name="p"/> (or clears it). <paramref name="projectFolder"/>
    /// resolves a relative source map path.</summary>
    public void Load(MapProject p, string? projectFolder)
    {
        int generation = ++_generation;
        Sections = null;
        foreach (Node c in GetChildren())
        {
            c.QueueFree();
        }

        if (p.Baked is not { SourceMap.Length: > 0, KeepRender: true } baked)
        {
            return;
        }

        string folder = Path.IsPathRooted(baked.SourceMap) || projectFolder is null
            ? baked.SourceMap
            : Path.GetFullPath(Path.Combine(projectFolder, baked.SourceMap));
        string glb = Path.Combine(folder, "render", "world.glb");
        if (!File.Exists(glb))
        {
            Core.EditorContext.Instance.Log($"Baked world: {glb} not found", Severity.Warning);
            return;
        }

        var projectActors = new HashSet<string>(p.Actors.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        BackgroundJobs.Start(_ =>
        {
            try
            {
                BakedData data = Read(glb, projectActors);
                BackgroundJobs.Post(() => Show(generation, data));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or KeyNotFoundException
                or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                BackgroundJobs.Post(() => Core.EditorContext.Instance.Log($"Baked world: could not read {glb}: {ex.Message}", Severity.Warning));
            }
        });
    }

    private void Show(int generation, BakedData data)
    {
        if (generation != _generation)
        {
            return;
        }

        // Textured when the BSP's sections read (Render/BakedSurfaces), else flat colours.
        bool textured = data.TexturedBsp is { } sections && Render.BakedSurfaces.Show(this, sections);
        Sections = data.TexturedBsp;
        if (!textured && data.Bsp.Build() is { } bsp)
        {
            WorldScene.AddMesh(this, bsp, Render.StoreyClip.Of(SceneStyle.Surface(lit: false, twoSided: true)), VisualLayers.Unlit)!.Name = "BspUnlit";
            WorldScene.AddMesh(this, bsp, Render.StoreyClip.Of(SceneStyle.Surface(lit: true, twoSided: true)), VisualLayers.Lit)!.Name = "BspLit";
        }

        int instances = 0;
        foreach ((int meshIndex, List<Transform3D> placed) in data.Instances)
        {
            if (data.Library[meshIndex].Build() is not { } mesh)
            {
                continue;
            }

            var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = placed.Count };
            for (int i = 0; i < placed.Count; i++)
            {
                multi.SetInstanceTransform(i, placed[i]);
            }

            foreach ((bool lit, uint layer) in new[] { (false, VisualLayers.Unlit), (true, VisualLayers.Lit) })
            {
                AddChild(new MultiMeshInstance3D
                {
                    Multimesh = multi,
                    Layers = layer,
                    MaterialOverride = Render.StoreyClip.Of(SceneStyle.Surface(lit, twoSided: true)),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }

            instances += placed.Count;
        }

        int bspTriangles = data.TexturedBsp?.Sum(t => t.Indices.Length / 3) ?? data.Bsp.Count;
        Core.EditorContext.Instance.Log($"Baked world: {bspTriangles} BSP triangles{(data.TexturedBsp is null ? "" : " (textured)")}, {instances} static mesh instances " +
            $"of {data.Instances.Count} meshes ({data.SkippedForActors} left to project actors)");
    }

    /// <summary>What a world.glb holds for the editor: BSP triangles, library meshes and where they are placed.</summary>
    private sealed record BakedData(TriangleList Bsp, Dictionary<int, TriangleList> Library,
        Dictionary<int, List<Transform3D>> Instances, int SkippedForActors)
    {
        /// <summary>The BSP as textured sections, or null (then <see cref="Bsp"/> holds it in flat colours).</summary>
        public List<OpenScct.Editor.Core.Assets.MeshSection>? TexturedBsp { get; init; }
    }

    /// <summary>Reads the BSP and the placed static meshes of a world.glb.</summary>
    private static BakedData Read(string path, HashSet<string> projectActors)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(file) != 0x46546C67)
        {
            throw new InvalidDataException("not a glb file");
        }

        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        using JsonDocument doc = JsonDocument.Parse(file.AsMemory(20, jsonLength));
        var glb = new Glb(file, 20 + jsonLength + 8, doc.RootElement);

        var bsp = new TriangleList();
        var bspMeshes = new List<int>();
        var libraryByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement node in glb.Root.GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("mesh", out JsonElement meshIndex) || Svmap(node) is not { } svmap)
            {
                continue;
            }

            string role = svmap.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? "" : "";
            if (role == "bsp" && !(svmap.TryGetProperty("sky", out JsonElement sky) && sky.ValueKind == JsonValueKind.True))
            {
                bspMeshes.Add(meshIndex.GetInt32());
            }
            else if (role == "libraryMesh" && node.TryGetProperty("name", out JsonElement name) && name.GetString() is { } n)
            {
                libraryByName[n] = meshIndex.GetInt32();
            }
        }

        List<OpenScct.Editor.Core.Assets.MeshSection>? textured = Render.BakedSurfaces.Read(path, bspMeshes);
        if (textured is null)
        {
            foreach (int mesh in bspMeshes)
            {
                glb.AppendMesh(mesh, bsp);
            }
        }

        var library = new Dictionary<int, TriangleList>();
        var instances = new Dictionary<int, List<Transform3D>>();
        int skipped = 0;
        if (Svmap(glb.Root) is { } top && top.TryGetProperty("instances", out JsonElement list))
        {
            foreach (JsonElement inst in list.EnumerateArray())
            {
                bool hidden = inst.TryGetProperty("hidden", out JsonElement h) && h.ValueKind == JsonValueKind.True;
                if (hidden || inst.TryGetProperty("projectorOf", out _)
                    || !inst.TryGetProperty("mesh", out JsonElement m) || m.GetString() is not { } meshName
                    || !libraryByName.TryGetValue(meshName, out int mesh))
                {
                    continue;
                }

                if (inst.TryGetProperty("actor", out JsonElement actor) && actor.GetString() is { } id && projectActors.Contains(id))
                {
                    skipped++;
                    continue;
                }

                if (!library.ContainsKey(mesh))
                {
                    var tris = new TriangleList();
                    glb.AppendMesh(mesh, tris);
                    library[mesh] = tris;
                }

                if (!instances.TryGetValue(mesh, out List<Transform3D>? placed))
                {
                    instances[mesh] = placed = new List<Transform3D>();
                }

                placed.Add(InstanceTransform(inst));
            }
        }

        return new BakedData(bsp, library, instances, skipped) { TexturedBsp = textured };
    }

    /// <summary>The UE2 actor transform of an instance in Godot space, as OpenSCCT's UeSpace builds it.</summary>
    private static Transform3D InstanceTransform(JsonElement inst)
    {
        Vec3 Vec(string key, Vec3 fallback) =>
            inst.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 3
                ? new Vec3(v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble())
                : fallback;
        int Units(string key) =>
            inst.TryGetProperty("rotation", out JsonElement r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out JsonElement x)
                ? x.GetInt32()
                : 0;

        var rotation = new Rot(Units("Pitch"), Units("Yaw"), Units("Roll"));
        Basis basis = Space.RotationToGodot(rotation) * Basis.FromScale(Space.DirToGodot(Vec("scale", Vec3.One)));
        Vector3 origin = Space.ToGodot(Vec("location", Vec3.Zero)) - basis * Space.ToGodot(Vec("prePivot", Vec3.Zero));
        return new Transform3D(basis, origin);
    }

    private static JsonElement? Svmap(JsonElement e) =>
        e.TryGetProperty("extras", out JsonElement extras) && extras.TryGetProperty("svmap", out JsonElement svmap) ? svmap : null;

    /// <summary>Triangles collected from glTF primitives, wound for Godot and coloured by material.</summary>
    private sealed class TriangleList
    {
        public List<Vector3> V { get; } = new();

        public List<Vector3> N { get; } = new();

        public List<Color> C { get; } = new();

        public int Count => V.Count / 3;

        public ArrayMesh? Build()
        {
            if (V.Count == 0)
            {
                return null;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = V.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = N.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = C.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }

    /// <summary>A parsed glb (its JSON and binary chunk), read for just what the editor draws.</summary>
    private sealed class Glb(byte[] file, int binStart, JsonElement root)
    {
        public JsonElement Root { get; } = root;

        /// <summary>Adds every triangle primitive of a mesh to <paramref name="into"/>.</summary>
        public void AppendMesh(int meshIndex, TriangleList into)
        {
            JsonElement accessors = Root.GetProperty("accessors");
            JsonElement views = Root.GetProperty("bufferViews");
            JsonElement materials = Root.TryGetProperty("materials", out JsonElement m) ? m : default;
            foreach (JsonElement prim in Root.GetProperty("meshes")[meshIndex].GetProperty("primitives").EnumerateArray())
            {
                if (prim.TryGetProperty("mode", out JsonElement mode) && mode.GetInt32() != 4)
                {
                    continue;
                }

                JsonElement attrs = prim.GetProperty("attributes");
                Vector3[] pos = ReadVec3(file, binStart, accessors, views, attrs.GetProperty("POSITION").GetInt32());
                Vector3[]? nor = attrs.TryGetProperty("NORMAL", out JsonElement na) ? ReadVec3(file, binStart, accessors, views, na.GetInt32()) : null;
                int[] idx = prim.TryGetProperty("indices", out JsonElement ia)
                    ? ReadIndices(file, binStart, accessors, views, ia.GetInt32())
                    : Enumerable.Range(0, pos.Length).ToArray();
                string? matName = prim.TryGetProperty("material", out JsonElement mi) && materials.ValueKind == JsonValueKind.Array
                    && materials[mi.GetInt32()].TryGetProperty("name", out JsonElement mn) ? mn.GetString() : null;
                if (matName is not null && matName.IndexOf("__", StringComparison.Ordinal) is var cut and > 0)
                {
                    matName = matName[..cut];
                }

                Color color = SceneStyle.MaterialColor(matName);
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    // glTF fronts are counter-clockwise, Godot's clockwise: emit (a, c, b).
                    foreach (int k in new[] { idx[i], idx[i + 2], idx[i + 1] })
                    {
                        into.V.Add(pos[k]);
                        into.N.Add(nor?[k] ?? Vector3.Up);
                        into.C.Add(color);
                    }
                }
            }
        }
    }

    private static (int Offset, int Count, int Stride, int Type) View(int binStart, JsonElement accessors, JsonElement views, int accessor, int elementSize)
    {
        JsonElement a = accessors[accessor];
        JsonElement bv = views[a.GetProperty("bufferView").GetInt32()];
        int offset = binStart + (bv.TryGetProperty("byteOffset", out JsonElement o) ? o.GetInt32() : 0)
            + (a.TryGetProperty("byteOffset", out JsonElement ao) ? ao.GetInt32() : 0);
        int stride = bv.TryGetProperty("byteStride", out JsonElement s) ? s.GetInt32() : elementSize;
        return (offset, a.GetProperty("count").GetInt32(), stride, a.GetProperty("componentType").GetInt32());
    }

    private static Vector3[] ReadVec3(byte[] file, int binStart, JsonElement accessors, JsonElement views, int accessor)
    {
        (int offset, int count, int stride, int type) = View(binStart, accessors, views, accessor, 12);
        if (type != 5126)
        {
            throw new InvalidDataException("VEC3 accessor is not float");
        }

        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = file.AsSpan(offset + i * stride, 12);
            result[i] = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(e), BinaryPrimitives.ReadSingleLittleEndian(e[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(e[8..]));
        }

        return result;
    }

    private static int[] ReadIndices(byte[] file, int binStart, JsonElement accessors, JsonElement views, int accessor)
    {
        JsonElement a = accessors[accessor];
        int size = a.GetProperty("componentType").GetInt32() switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("bad index type") };
        (int offset, int count, int stride, _) = View(binStart, accessors, views, accessor, size);
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + i * stride;
            result[i] = size switch
            {
                1 => file[at],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at)),
                _ => (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)),
            };
        }

        return result;
    }
}
