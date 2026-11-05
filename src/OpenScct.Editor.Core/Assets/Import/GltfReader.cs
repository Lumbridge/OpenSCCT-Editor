using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;

namespace OpenScct.Editor.Core.Assets.Import;

/// <summary>
/// Reads a glTF 2.0 file (<c>.glb</c>, or <c>.gltf</c> with external or data-URI buffers) into a
/// <see cref="SourceMesh"/>: every triangle primitive of the default scene, with node transforms baked in, one part
/// per node. Positions, normals and first UVs must be floats; base colour images come from files or the binary chunk.
/// </summary>
public static class GltfReader
{
    public static SourceMesh Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        (JsonObject json, byte[]? bin) = Split(file);
        var doc = new Doc(json, LoadBuffers(json, bin, dir), dir);
        var mesh = new SourceMesh();
        JsonArray nodes = json["nodes"] as JsonArray ?? new JsonArray();
        IEnumerable<int> roots = json["scenes"]?[json["scene"]?.GetValue<int>() ?? 0]?["nodes"] is JsonArray r
            ? r.Select(n => n!.GetValue<int>())
            : Enumerable.Range(0, nodes.Count).Where(i => !nodes.Any(n => n?["children"] is JsonArray c && c.Any(x => x!.GetValue<int>() == i)));
        foreach (int root in roots)
        {
            Walk(doc, root, Matrix4x4.Identity, mesh);
        }

        mesh.Parts.RemoveAll(p => p.Triangles.Count == 0);
        return mesh;
    }

    private static void Walk(Doc doc, int index, Matrix4x4 parent, SourceMesh mesh)
    {
        JsonObject node = doc.Json["nodes"]![index]!.AsObject();
        Matrix4x4 world = Local(node) * parent;
        if (node["mesh"] is JsonValue mv)
        {
            JsonObject m = doc.Json["meshes"]![mv.GetValue<int>()]!.AsObject();
            string name = node["name"]?.GetValue<string>() ?? m["name"]?.GetValue<string>() ?? $"Node{index}";
            if (m["name"]?.GetValue<string>() is { } meshName && (meshName.StartsWith("UCX_", StringComparison.OrdinalIgnoreCase) ||
                meshName.StartsWith("MCD", StringComparison.OrdinalIgnoreCase)))
            {
                name = meshName;
            }
            else if (node["extras"]?["svmap"]?["role"]?.GetValue<string>() == "collision")
            {
                // A mesh this editor wrote: its collision node is a hull, as UCX_ parts are.
                name = "UCX_" + name;
            }

            var part = new SourcePart(name);
            foreach (JsonNode? p in m["primitives"]!.AsArray())
            {
                AddPrimitive(doc, p!.AsObject(), world, part, mesh);
            }

            mesh.Parts.Add(part);
        }

        if (node["children"] is JsonArray children)
        {
            foreach (JsonNode? c in children)
            {
                Walk(doc, c!.GetValue<int>(), world, mesh);
            }
        }
    }

    private static Matrix4x4 Local(JsonObject node)
    {
        if (node["matrix"] is JsonArray m && m.Count == 16)
        {
            float[] f = m.Select(x => x!.GetValue<float>()).ToArray();
            // glTF matrices are column-major; System.Numerics multiplies row vectors, so this is the same layout.
            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }

        Vector3 t = node["translation"] is JsonArray ta ? new Vector3(ta[0]!.GetValue<float>(), ta[1]!.GetValue<float>(), ta[2]!.GetValue<float>()) : Vector3.Zero;
        Quaternion r = node["rotation"] is JsonArray ra
            ? new Quaternion(ra[0]!.GetValue<float>(), ra[1]!.GetValue<float>(), ra[2]!.GetValue<float>(), ra[3]!.GetValue<float>())
            : Quaternion.Identity;
        Vector3 s = node["scale"] is JsonArray sa ? new Vector3(sa[0]!.GetValue<float>(), sa[1]!.GetValue<float>(), sa[2]!.GetValue<float>()) : Vector3.One;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    private static void AddPrimitive(Doc doc, JsonObject prim, Matrix4x4 world, SourcePart part, SourceMesh mesh)
    {
        if ((prim["mode"]?.GetValue<int>() ?? 4) != 4 || prim["attributes"]?["POSITION"] is not JsonValue pa)
        {
            mesh.Notes.Add($"{part.Name}: a primitive that is not a triangle list was skipped");
            return;
        }

        Vector3[] pos = doc.Vec3(pa.GetValue<int>());
        Vector3[]? nrm = prim["attributes"]!["NORMAL"] is JsonValue na ? doc.Vec3(na.GetValue<int>()) : null;
        Vector2[]? uv = prim["attributes"]!["TEXCOORD_0"] is JsonValue ua ? doc.Vec2(ua.GetValue<int>()) : null;
        int[] idx = prim["indices"] is JsonValue ia ? doc.Indices(ia.GetValue<int>()) : Enumerable.Range(0, pos.Length).ToArray();
        string? material = prim["material"] is JsonValue mi ? doc.Material(mi.GetValue<int>(), mesh) : null;
        bool mirrored = world.GetDeterminant() < 0;
        Matrix4x4 normalMatrix = Matrix4x4.Invert(world, out Matrix4x4 inv) ? Matrix4x4.Transpose(inv) : world;
        int baseIndex = part.Positions.Count;
        for (int i = 0; i < pos.Length; i++)
        {
            Vector3 n = nrm is null ? Vector3.UnitY : Vector3.TransformNormal(nrm[i], normalMatrix);
            part.AddVertex(Vector3.Transform(pos[i], world), n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY, uv?[i] ?? Vector2.Zero);
        }

        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            (int a, int b, int c) = (baseIndex + idx[t], baseIndex + idx[t + 1], baseIndex + idx[t + 2]);
            part.Triangles.Add(mirrored ? (a, c, b, material) : (a, b, c, material));
        }
    }

    private static (JsonObject Json, byte[]? Bin) Split(byte[] file)
    {
        if (file.Length >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(file) == 0x46546C67)
        {
            int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
            JsonObject json = JsonNode.Parse(file.AsSpan(20, jsonLength))!.AsObject();
            int at = 20 + jsonLength;
            byte[]? bin = null;
            if (at + 8 <= file.Length && BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4)) == 0x004E4942)
            {
                int binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
                bin = file.AsSpan(at + 8, binLength).ToArray();
            }

            return (json, bin);
        }

        return (JsonNode.Parse(file)!.AsObject(), null);
    }

    private static List<byte[]> LoadBuffers(JsonObject json, byte[]? bin, string dir)
    {
        var buffers = new List<byte[]>();
        foreach (JsonNode? b in json["buffers"] as JsonArray ?? new JsonArray())
        {
            string? uri = b?["uri"]?.GetValue<string>();
            buffers.Add(uri is null ? bin ?? throw new InvalidDataException("a buffer without uri needs the glb binary chunk")
                : DataUri(uri) ?? File.ReadAllBytes(Path.Combine(dir, Uri.UnescapeDataString(uri))));
        }

        return buffers;
    }

    private static byte[]? DataUri(string uri)
    {
        int comma = uri.IndexOf(',', StringComparison.Ordinal);
        return uri.StartsWith("data:", StringComparison.Ordinal) && comma > 0 && uri[..comma].EndsWith(";base64", StringComparison.Ordinal)
            ? Convert.FromBase64String(uri[(comma + 1)..])
            : null;
    }

    /// <summary>A parsed document with its buffers.</summary>
    private sealed class Doc(JsonObject json, List<byte[]> buffers, string dir)
    {
        private readonly Dictionary<int, string> _materials = new();

        public JsonObject Json { get; } = json;

        public Vector3[] Vec3(int accessor)
        {
            float[] f = Floats(accessor, 3);
            var r = new Vector3[f.Length / 3];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = new Vector3(f[3 * i], f[3 * i + 1], f[3 * i + 2]);
            }

            return r;
        }

        public Vector2[] Vec2(int accessor)
        {
            float[] f = Floats(accessor, 2);
            var r = new Vector2[f.Length / 2];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = new Vector2(f[2 * i], f[2 * i + 1]);
            }

            return r;
        }

        private (byte[] Data, int Offset, int Stride, int Count, int Type) View(int accessor, int elementSize)
        {
            JsonObject acc = Json["accessors"]![accessor]!.AsObject();
            int count = acc["count"]!.GetValue<int>();
            int type = acc["componentType"]!.GetValue<int>();
            if (acc["bufferView"] is not JsonValue bvi)
            {
                throw new InvalidDataException($"accessor {accessor} has no buffer view (sparse accessors are not read)");
            }

            JsonObject view = Json["bufferViews"]![bvi.GetValue<int>()]!.AsObject();
            byte[] data = buffers[view["buffer"]!.GetValue<int>()];
            int offset = (view["byteOffset"]?.GetValue<int>() ?? 0) + (acc["byteOffset"]?.GetValue<int>() ?? 0);
            int stride = view["byteStride"]?.GetValue<int>() ?? elementSize;
            return (data, offset, stride, count, type);
        }

        private float[] Floats(int accessor, int components)
        {
            (byte[] data, int offset, int stride, int count, int type) = View(accessor, 4 * components);
            if (type != 5126)
            {
                throw new InvalidDataException($"accessor {accessor} is not float (component type {type}); re-export with float attributes");
            }

            var r = new float[count * components];
            for (int i = 0; i < count; i++)
            {
                for (int k = 0; k < components; k++)
                {
                    r[i * components + k] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + i * stride + 4 * k));
                }
            }

            return r;
        }

        public int[] Indices(int accessor)
        {
            int type = Json["accessors"]![accessor]!["componentType"]!.GetValue<int>();
            int size = type switch { 5125 => 4, 5123 => 2, 5121 => 1, _ => throw new InvalidDataException($"bad index type {type}") };
            (byte[] data, int offset, int stride, int count, _) = View(accessor, size);
            var r = new int[count];
            for (int i = 0; i < count; i++)
            {
                int at = offset + i * stride;
                r[i] = size switch { 4 => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)), 2 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)), _ => data[at] };
            }

            return r;
        }

        /// <summary>The material's name, registering it (colour, image, alpha, sides) on first use.</summary>
        public string Material(int index, SourceMesh mesh)
        {
            if (_materials.TryGetValue(index, out string? done))
            {
                return done;
            }

            JsonObject m = Json["materials"]![index]!.AsObject();
            string name = m["name"]?.GetValue<string>() ?? $"Material{index}";
            var sm = new SourceMaterial(name)
            {
                DoubleSided = m["doubleSided"]?.GetValue<bool>() == true,
                AlphaMode = m["alphaMode"]?.GetValue<string>() is "MASK" or "BLEND" ? m["alphaMode"]!.GetValue<string>() : null,
            };
            JsonObject? pbr = m["pbrMetallicRoughness"] as JsonObject;
            if (pbr?["baseColorFactor"] is JsonArray c && c.Count >= 3)
            {
                sm.Color = new Vector3(c[0]!.GetValue<float>(), c[1]!.GetValue<float>(), c[2]!.GetValue<float>());
            }

            if (pbr?["baseColorTexture"]?["index"] is JsonValue ti && Json["textures"]?[ti.GetValue<int>()]?["source"] is JsonValue si &&
                Json["images"]?[si.GetValue<int>()] is JsonObject image)
            {
                if (image["uri"]?.GetValue<string>() is { } uri)
                {
                    if (DataUri(uri) is { } bytes)
                    {
                        sm.ImageBytes = bytes;
                    }
                    else
                    {
                        sm.ImageFile = Path.Combine(dir, Uri.UnescapeDataString(uri));
                    }
                }
                else if (image["bufferView"] is JsonValue bv)
                {
                    JsonObject view = Json["bufferViews"]![bv.GetValue<int>()]!.AsObject();
                    sm.ImageBytes = buffers[view["buffer"]!.GetValue<int>()].AsSpan(view["byteOffset"]?.GetValue<int>() ?? 0,
                        view["byteLength"]!.GetValue<int>()).ToArray();
                }
            }

            string unique = name;
            for (int n = 2; mesh.Materials.ContainsKey(unique); n++)
            {
                unique = $"{name}{n}";
            }

            mesh.Materials[unique] = sm;
            _materials[index] = unique;
            return unique;
        }
    }
}
