using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenScct.Editor.Core.Compile.Output;

/// <summary>
/// A glTF 2.0 binary being written: JSON plus one binary buffer. Starts empty or from an existing .glb (a baked
/// world), and can copy meshes out of another document (static meshes from converted maps). Positions and normals
/// are given in glTF space; <see cref="GltfSpace"/> converts from UE space.
/// </summary>
public sealed class Glb
{
    private const uint Magic = 0x46546C67;   // "glTF"
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    public const int ArrayBuffer = 34962;
    public const int ElementArrayBuffer = 34963;
    public const int Float = 5126;
    public const int UnsignedByte = 5121;
    public const int UnsignedInt = 5125;

    private readonly MemoryStream _bin = new();
    private readonly Dictionary<string, int> _materialsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _imagesByUri = new(StringComparer.Ordinal);

    private Glb(JsonObject json) => Json = json;

    public JsonObject Json { get; }

    public static Glb Create(string generator)
    {
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["generator"] = generator, ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray() }),
            ["nodes"] = new JsonArray(),
            ["meshes"] = new JsonArray(),
            ["materials"] = new JsonArray(),
            ["accessors"] = new JsonArray(),
            ["bufferViews"] = new JsonArray(),
            ["buffers"] = new JsonArray(),
            ["images"] = new JsonArray(),
            ["textures"] = new JsonArray(),
            ["samplers"] = new JsonArray(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 }),
        };
        return new Glb(json);
    }

    /// <summary>Reads a .glb (JSON and BIN chunks).</summary>
    public static Glb Load(ReadOnlySpan<byte> data)
    {
        (JsonObject json, int binStart, int binLength) = Split(data);
        var glb = new Glb(json);
        glb._bin.Write(data.Slice(binStart, binLength));
        foreach (string key in new[] { "nodes", "meshes", "materials", "accessors", "bufferViews", "buffers", "images", "textures", "samplers" })
        {
            json[key] ??= new JsonArray();
        }

        JsonArray materials = glb.Array("materials");
        for (int i = 0; i < materials.Count; i++)
        {
            if (materials[i]?["name"]?.GetValue<string>() is { } name)
            {
                glb._materialsByName.TryAdd(name, i);
            }
        }

        JsonArray images = glb.Array("images");
        for (int i = 0; i < images.Count; i++)
        {
            if (images[i]?["uri"]?.GetValue<string>() is { } uri)
            {
                glb._imagesByUri.TryAdd(uri, i);
            }
        }

        return glb;
    }

    /// <summary>Only the JSON chunk of a .glb file (reads the file's head, not its binary chunk).</summary>
    public static JsonObject ReadJson(string path)
    {
        using FileStream f = File.OpenRead(path);
        Span<byte> head = stackalloc byte[20];
        f.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic || BinaryPrimitives.ReadUInt32LittleEndian(head[16..]) != JsonChunk)
        {
            throw new InvalidDataException($"{path} is not a glTF binary");
        }

        var json = new byte[BinaryPrimitives.ReadUInt32LittleEndian(head[12..])];
        f.ReadExactly(json);
        return JsonNode.Parse(json)!.AsObject();
    }

    public JsonArray Array(string key) => Json[key]!.AsArray();

    public int Count(string key) => Json[key] is JsonArray a ? a.Count : 0;

    /// <summary>Appends data as a buffer view (4-byte aligned) and an accessor over it; returns the accessor.</summary>
    public int AddAccessor(ReadOnlySpan<byte> data, int componentType, int count, string type, int target, bool normalized = false,
        float[]? min = null, float[]? max = null)
    {
        while (_bin.Length % 4 != 0)
        {
            _bin.WriteByte(0);
        }

        var view = new JsonObject { ["buffer"] = 0, ["byteLength"] = data.Length, ["byteOffset"] = _bin.Length, ["target"] = target };
        _bin.Write(data);
        Array("bufferViews").Add(view);
        var acc = new JsonObject
        {
            ["bufferView"] = Count("bufferViews") - 1,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        };
        if (min is not null && max is not null)
        {
            acc["min"] = new JsonArray(min.Select(v => (JsonNode)v).ToArray());
            acc["max"] = new JsonArray(max.Select(v => (JsonNode)v).ToArray());
        }

        if (normalized)
        {
            acc["normalized"] = true;
        }

        Array("accessors").Add(acc);
        return Count("accessors") - 1;
    }

    /// <summary>A textured material (one per name); returns its index.</summary>
    /// <param name="lightmapUri">A baked lightmap the material reads on TEXCOORD_1: carried as the occlusion texture
    /// and named in <c>extras.scct_lightmap</c>, as the converter writes BSP materials.</param>
    public int AddMaterial(string name, string imageUri, bool doubleSided, string? alphaMode, string? lightmapUri = null)
    {
        if (_materialsByName.TryGetValue(name, out int existing))
        {
            return existing;
        }

        var pbr = new JsonObject
        {
            ["baseColorTexture"] = new JsonObject { ["index"] = AddTexture(imageUri) },
            ["metallicFactor"] = 0.0f,
            ["roughnessFactor"] = 0.85f,
        };
        var m = new JsonObject { ["name"] = name, ["pbrMetallicRoughness"] = pbr };
        if (alphaMode is not null)
        {
            m["alphaMode"] = alphaMode;
        }

        if (doubleSided)
        {
            m["doubleSided"] = true;
        }

        if (lightmapUri is not null)
        {
            m["occlusionTexture"] = new JsonObject { ["index"] = AddTexture(lightmapUri), ["texCoord"] = 1 };
            m["extras"] = new JsonObject { ["scct_lightmap"] = lightmapUri };
        }

        Array("materials").Add(m);
        _materialsByName[name] = Count("materials") - 1;
        return _materialsByName[name];
    }

    private int AddTexture(string uri)
    {
        if (!_imagesByUri.TryGetValue(uri, out int image))
        {
            Array("images").Add(new JsonObject { ["uri"] = uri });
            image = Count("images") - 1;
            _imagesByUri[uri] = image;
        }

        if (Count("samplers") == 0)
        {
            Array("samplers").Add(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
        }

        Array("textures").Add(new JsonObject { ["sampler"] = 0, ["source"] = image });
        return Count("textures") - 1;
    }

    /// <summary>One triangle-list primitive to write: arrays in glTF space; <see cref="Uvs2"/> (lightmap UVs) and
    /// <see cref="Colors"/> (RGBA8) are optional.</summary>
    public sealed record MeshPart(float[] Positions, float[] Normals, float[] Uvs, float[]? Uvs2, byte[]? Colors, uint[] Indices, int Material);

    /// <summary>A one-primitive triangle mesh; arrays are in glTF space. Returns the mesh index.</summary>
    public int AddMesh(string name, float[] positions, float[] normals, float[] uvs, byte[]? colors, uint[] indices, int material) =>
        AddMesh(name, [new MeshPart(positions, normals, uvs, null, colors, indices, material)]);

    /// <summary>A triangle mesh of one primitive per part. Returns the mesh index.</summary>
    public int AddMesh(string name, IReadOnlyList<MeshPart> parts)
    {
        var prims = new JsonArray();
        foreach (MeshPart part in parts)
        {
            prims.Add(Primitive(part));
        }

        Array("meshes").Add(new JsonObject { ["name"] = name, ["primitives"] = prims });
        return Count("meshes") - 1;
    }

    /// <summary>A copy of a mesh that shares its accessors but carries its own COLOR_0 per primitive (null keeps the
    /// primitive's own): an instance's baked vertex light. Returns the new mesh index.</summary>
    public int AddColorVariant(int mesh, string name, IReadOnlyDictionary<int, byte[]> colors)
    {
        JsonObject copy = Array("meshes")[mesh]!.DeepClone().AsObject();
        copy["name"] = name;
        JsonArray prims = copy["primitives"]!.AsArray();
        foreach ((int index, byte[] rgba) in colors)
        {
            JsonObject attrs = prims[index]!["attributes"]!.AsObject();
            attrs["COLOR_0"] = AddAccessor(rgba, UnsignedByte, rgba.Length / 4, "VEC4", ArrayBuffer, normalized: true);
        }

        Array("meshes").Add(copy);
        return Count("meshes") - 1;
    }

    private JsonObject Primitive(MeshPart part)
    {
        float[] positions = part.Positions, normals = part.Normals, uvs = part.Uvs;
        byte[]? colors = part.Colors;
        uint[] indices = part.Indices;
        int n = positions.Length / 3;
        float[] min = [float.MaxValue, float.MaxValue, float.MaxValue];
        float[] max = [float.MinValue, float.MinValue, float.MinValue];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                min[k] = Math.Min(min[k], positions[3 * i + k]);
                max[k] = Math.Max(max[k], positions[3 * i + k]);
            }
        }

        var attrs = new JsonObject
        {
            ["POSITION"] = AddAccessor(MemoryMarshal.AsBytes(positions.AsSpan()), Float, n, "VEC3", ArrayBuffer, min: min, max: max),
            ["NORMAL"] = AddAccessor(MemoryMarshal.AsBytes(normals.AsSpan()), Float, n, "VEC3", ArrayBuffer),
            ["TEXCOORD_0"] = AddAccessor(MemoryMarshal.AsBytes(uvs.AsSpan()), Float, n, "VEC2", ArrayBuffer),
        };
        if (part.Uvs2 is { } uvs2)
        {
            attrs["TEXCOORD_1"] = AddAccessor(MemoryMarshal.AsBytes(uvs2.AsSpan()), Float, n, "VEC2", ArrayBuffer);
        }

        if (colors is not null)
        {
            attrs["COLOR_0"] = AddAccessor(colors, UnsignedByte, n, "VEC4", ArrayBuffer, normalized: true);
        }

        return new JsonObject
        {
            ["attributes"] = attrs,
            ["indices"] = AddAccessor(MemoryMarshal.AsBytes(indices.AsSpan()), UnsignedInt, indices.Length, "SCALAR", ElementArrayBuffer),
            ["material"] = part.Material,
            ["mode"] = 4,
        };
    }

    public int AddNode(JsonObject node)
    {
        Array("nodes").Add(node);
        return Count("nodes") - 1;
    }

    public void AddSceneRoot(int node)
    {
        JsonArray scenes = Array("scenes");
        if (scenes.Count == 0)
        {
            scenes.Add(new JsonObject { ["nodes"] = new JsonArray() });
            Json["scene"] = 0;
        }

        JsonObject scene = scenes[Json["scene"]?.GetValue<int>() ?? 0]!.AsObject();
        (scene["nodes"] ??= new JsonArray()).AsArray().Add(node);
    }

    /// <summary>
    /// Copies a mesh of <paramref name="src"/> with its accessors, buffer views, materials and images into this
    /// document. Image URIs go through <paramref name="mapImage"/> (which copies the file and returns the new URI).
    /// Returns the new mesh index.
    /// </summary>
    public int ImportMesh(Glb src, int mesh, Func<string, string> mapImage)
    {
        JsonObject m = src.Array("meshes")[mesh]!.AsObject().DeepClone().AsObject();
        var accessorMap = new Dictionary<int, int>();
        foreach (JsonNode? p in m["primitives"]!.AsArray())
        {
            JsonObject prim = p!.AsObject();
            JsonObject attrs = prim["attributes"]!.AsObject();
            foreach (string key in attrs.Select(kv => kv.Key).ToList())
            {
                attrs[key] = ImportAccessor(src, attrs[key]!.GetValue<int>(), accessorMap);
            }

            if (prim["indices"] is JsonNode idx)
            {
                prim["indices"] = ImportAccessor(src, idx.GetValue<int>(), accessorMap);
            }

            if (prim["material"] is JsonNode mat)
            {
                prim["material"] = ImportMaterial(src, mat.GetValue<int>(), mapImage);
            }

            prim.Remove("targets");
        }

        Array("meshes").Add(m);
        return Count("meshes") - 1;
    }

    private int ImportAccessor(Glb src, int index, Dictionary<int, int> map)
    {
        if (map.TryGetValue(index, out int done))
        {
            return done;
        }

        JsonObject acc = src.Array("accessors")[index]!.AsObject().DeepClone().AsObject();
        acc.Remove("sparse");
        if (acc["bufferView"] is JsonNode bv)
        {
            JsonObject view = src.Array("bufferViews")[bv.GetValue<int>()]!.AsObject();
            int offset = view["byteOffset"]?.GetValue<int>() ?? 0;
            int length = view["byteLength"]!.GetValue<int>();
            while (_bin.Length % 4 != 0)
            {
                _bin.WriteByte(0);
            }

            var copy = view.DeepClone().AsObject();
            copy["buffer"] = 0;
            copy["byteOffset"] = _bin.Length;
            _bin.Write(src._bin.GetBuffer().AsSpan(offset, length));
            Array("bufferViews").Add(copy);
            acc["bufferView"] = Count("bufferViews") - 1;
        }

        Array("accessors").Add(acc);
        map[index] = Count("accessors") - 1;
        return map[index];
    }

    private int ImportMaterial(Glb src, int index, Func<string, string> mapImage)
    {
        JsonObject mat = src.Array("materials")[index]!.AsObject().DeepClone().AsObject();
        string name = mat["name"]?.GetValue<string>() ?? $"material{index}";
        if (_materialsByName.TryGetValue(name, out int existing))
        {
            return existing;
        }

        foreach (JsonObject texRef in TextureRefs(mat))
        {
            int t = texRef["index"]!.GetValue<int>();
            JsonObject tex = src.Array("textures")[t]!.AsObject();
            string? uri = tex["source"] is JsonNode s ? src.Array("images")[s.GetValue<int>()]?["uri"]?.GetValue<string>() : null;
            texRef["index"] = uri is null ? 0 : AddTexture(mapImage(uri));
        }

        Array("materials").Add(mat);
        _materialsByName[name] = Count("materials") - 1;
        return _materialsByName[name];
    }

    private static IEnumerable<JsonObject> TextureRefs(JsonNode node)
    {
        if (node is JsonObject o)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in o.ToList())
            {
                if (kv.Value is JsonObject child && kv.Key.EndsWith("Texture", StringComparison.Ordinal) && child["index"] is not null)
                {
                    yield return child;
                }
                else if (kv.Value is not null)
                {
                    foreach (JsonObject r in TextureRefs(kv.Value))
                    {
                        yield return r;
                    }
                }
            }
        }
        else if (node is JsonArray a)
        {
            foreach (JsonNode? item in a)
            {
                if (item is not null)
                {
                    foreach (JsonObject r in TextureRefs(item))
                    {
                        yield return r;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The triangles of a mesh back in UE space (uu, the glTF winding undone): positions and index triples, for
    /// collision built from a library mesh, and each triangle's material name (null when the primitive has none). Only
    /// triangle-list primitives with float positions are read.
    /// </summary>
    public (List<Model.Vec3> Positions, List<int> Indices, List<string?> Materials) ReadUeTriangles(int mesh)
    {
        var positions = new List<Model.Vec3>();
        var indices = new List<int>();
        var materials = new List<string?>();
        foreach (JsonNode? p in Array("meshes")[mesh]!["primitives"]!.AsArray())
        {
            if (p is null || (p["mode"]?.GetValue<int>() ?? 4) != 4 || p["attributes"]?["POSITION"] is not JsonValue pos)
            {
                continue;
            }

            int baseIndex = positions.Count;
            float[] xyz = ReadFloats(pos.GetValue<int>(), 3);
            for (int i = 0; i + 2 < xyz.Length; i += 3)
            {
                positions.Add(new Model.Vec3(xyz[i] * 100.0, xyz[i + 2] * 100.0, xyz[i + 1] * 100.0));
            }

            int count = xyz.Length / 3;
            int[] idx = p["indices"] is JsonValue ia ? ReadIndices(ia.GetValue<int>()) : Enumerable.Range(0, count).ToArray();
            string? material = p["material"] is JsonValue mv ? Array("materials")[mv.GetValue<int>()]?["name"]?.GetValue<string>() : null;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                materials.Add(material);
                // glTF (a, b, c) is UE (a, c, b).
                indices.Add(baseIndex + idx[t]);
                indices.Add(baseIndex + idx[t + 2]);
                indices.Add(baseIndex + idx[t + 1]);
            }
        }

        return (positions, indices, materials);
    }

    /// <summary>One triangle-list primitive of a mesh back in UE space: positions, normals (zero when the primitive has
    /// none), texture coordinates (TEXCOORD_0 pairs, or null), index triples in UE winding, the primitive's COLOR_0
    /// accessor (or -1) and its material name.</summary>
    public sealed record UePrimitive(Model.Vec3[] Positions, Model.Vec3[] Normals, float[]? Uvs, int[] Indices, int ColorAccessor, string? Material)
    {
        /// <summary>The primitive's index in its mesh.</summary>
        public int Index { get; init; }
    }

    /// <summary>The triangle-list primitives of a mesh in UE space (for lighting: positions with normals, per
    /// primitive so vertex colours can be written back in the same order).</summary>
    public List<UePrimitive> ReadUePrimitives(int mesh)
    {
        var result = new List<UePrimitive>();
        JsonArray primitives = Array("meshes")[mesh]!["primitives"]!.AsArray();
        for (int pi = 0; pi < primitives.Count; pi++)
        {
            JsonNode? p = primitives[pi];
            if (p is null || (p["mode"]?.GetValue<int>() ?? 4) != 4 || p["attributes"]?["POSITION"] is not JsonValue pos)
            {
                continue;
            }

            float[] xyz = ReadFloats(pos.GetValue<int>(), 3);
            float[]? nxyz = p["attributes"]?["NORMAL"] is JsonValue nv ? ReadFloats(nv.GetValue<int>(), 3) : null;
            int count = xyz.Length / 3;
            var positions = new Model.Vec3[count];
            var normals = new Model.Vec3[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = new Model.Vec3(xyz[3 * i] * 100.0, xyz[(3 * i) + 2] * 100.0, xyz[(3 * i) + 1] * 100.0);
                normals[i] = nxyz is null ? Model.Vec3.Zero : new Model.Vec3(nxyz[3 * i], nxyz[(3 * i) + 2], nxyz[(3 * i) + 1]);
            }

            int[] idx = p["indices"] is JsonValue ia ? ReadIndices(ia.GetValue<int>()) : Enumerable.Range(0, count).ToArray();
            var ue = new int[idx.Length - (idx.Length % 3)];
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                // glTF (a, b, c) is UE (a, c, b).
                (ue[t], ue[t + 1], ue[t + 2]) = (idx[t], idx[t + 2], idx[t + 1]);
            }

            string? material = p["material"] is JsonValue mv ? Array("materials")[mv.GetValue<int>()]?["name"]?.GetValue<string>() : null;
            int color = p["attributes"]?["COLOR_0"] is JsonValue cv ? cv.GetValue<int>() : -1;
            float[]? uvs = p["attributes"]?["TEXCOORD_0"] is JsonValue tv ? ReadFloats(tv.GetValue<int>(), 2) : null;
            result.Add(new UePrimitive(positions, normals, uvs, ue, color, material) { Index = pi });
        }

        return result;
    }

    private (ReadOnlyMemory<byte> Data, int Stride, int Count) View(int accessor, int elementSize)
    {
        JsonObject acc = Array("accessors")[accessor]!.AsObject();
        int count = acc["count"]!.GetValue<int>();
        JsonObject view = Array("bufferViews")[acc["bufferView"]!.GetValue<int>()]!.AsObject();
        int offset = (view["byteOffset"]?.GetValue<int>() ?? 0) + (acc["byteOffset"]?.GetValue<int>() ?? 0);
        int stride = view["byteStride"]?.GetValue<int>() ?? elementSize;
        int length = count == 0 ? 0 : (stride * (count - 1)) + elementSize;
        return (_bin.GetBuffer().AsMemory(offset, length), stride, count);
    }

    /// <summary>A float accessor's values, <paramref name="components"/> per element.</summary>
    public float[] ReadFloats(int accessor, int components)
    {
        (ReadOnlyMemory<byte> data, int stride, int count) = View(accessor, 4 * components);
        var result = new float[count * components];
        ReadOnlySpan<byte> s = data.Span;
        for (int i = 0; i < count; i++)
        {
            for (int k = 0; k < components; k++)
            {
                result[i * components + k] = BinaryPrimitives.ReadSingleLittleEndian(s[(i * stride + 4 * k)..]);
            }
        }

        return result;
    }

    /// <summary>A COLOR_0 accessor's values as RGBA8: normalized bytes as stored, floats scaled to bytes.</summary>
    public byte[] ReadColors(int accessor)
    {
        if (Array("accessors")[accessor]!["componentType"]!.GetValue<int>() == Float)
        {
            return ReadFloats(accessor, 4).Select(f => (byte)Math.Round(Math.Clamp(f, 0f, 1f) * 255)).ToArray();
        }

        (ReadOnlyMemory<byte> data, int stride, int count) = View(accessor, 4);
        var result = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            data.Span.Slice(i * stride, 4).CopyTo(result.AsSpan(i * 4));
        }

        return result;
    }

    /// <summary>An index accessor's values (8, 16 or 32-bit).</summary>
    public int[] ReadIndices(int accessor)
    {
        int type = Array("accessors")[accessor]!["componentType"]!.GetValue<int>();
        int size = type switch { UnsignedInt => 4, 5123 => 2, _ => 1 };
        (ReadOnlyMemory<byte> data, int stride, int count) = View(accessor, size);
        ReadOnlySpan<byte> s = data.Span;
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> at = s[(i * stride)..];
            result[i] = size switch { 4 => (int)BinaryPrimitives.ReadUInt32LittleEndian(at), 2 => BinaryPrimitives.ReadUInt16LittleEndian(at), _ => at[0] };
        }

        return result;
    }

    /// <summary>The .glb bytes, with <paramref name="extras"/> as the document's extras.</summary>
    public byte[] ToBytes(JsonObject? extras)
    {
        while (_bin.Length % 4 != 0)
        {
            _bin.WriteByte(0);
        }

        if (extras is not null)
        {
            Json["extras"] = extras;
        }

        JsonArray buffers = Array("buffers");
        buffers.Clear();
        if (_bin.Length > 0)
        {
            buffers.Add(new JsonObject { ["byteLength"] = _bin.Length });
        }

        foreach (string key in new[] { "images", "textures", "samplers", "materials", "meshes", "accessors", "bufferViews", "buffers" })
        {
            if (Json[key] is JsonArray { Count: 0 })
            {
                Json.Remove(key);
            }
        }

        byte[] json = Encoding.UTF8.GetBytes(Json.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        int jsonPadded = (json.Length + 3) & ~3;
        int binLength = (int)_bin.Length;
        int total = 12 + 8 + jsonPadded + (binLength > 0 ? 8 + binLength : 0);
        var output = new byte[total];
        Span<byte> o = output;
        BinaryPrimitives.WriteUInt32LittleEndian(o, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(o[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(o[12..], (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(o[16..], JsonChunk);
        json.CopyTo(o[20..]);
        o.Slice(20 + json.Length, jsonPadded - json.Length).Fill((byte)' ');
        if (binLength > 0)
        {
            int at = 20 + jsonPadded;
            BinaryPrimitives.WriteUInt32LittleEndian(o[at..], (uint)binLength);
            BinaryPrimitives.WriteUInt32LittleEndian(o[(at + 4)..], BinChunk);
            _bin.GetBuffer().AsSpan(0, binLength).CopyTo(o[(at + 8)..]);
        }

        return output;
    }

    private static (JsonObject Json, int BinStart, int BinLength) Split(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException("not a glTF binary");
        }

        int at = 12;
        JsonObject? json = null;
        int binStart = 0, binLength = 0;
        while (at + 8 <= data.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            ReadOnlySpan<byte> chunk = data.Slice(at + 8, length);
            if (type == JsonChunk)
            {
                json = JsonNode.Parse(chunk)!.AsObject();
            }
            else if (type == BinChunk)
            {
                (binStart, binLength) = (at + 8, length);
            }

            at += 8 + length;
        }

        return (json ?? throw new InvalidDataException("glTF binary without JSON"), binStart, binLength);
    }
}

/// <summary>UE space (uu, Z up, left-handed) to glTF space (metres, Y up): (x, z, y) / 100, triangles wound the other way.</summary>
public static class GltfSpace
{
    public static void Position(Model.Vec3 p, float[] into, int at)
    {
        into[at] = (float)(p.X / 100);
        into[at + 1] = (float)(p.Z / 100);
        into[at + 2] = (float)(p.Y / 100);
    }

    public static void Normal(Model.Vec3 n, float[] into, int at)
    {
        into[at] = (float)n.X;
        into[at + 1] = (float)n.Z;
        into[at + 2] = (float)n.Y;
    }
}
