using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using OpenScct.Animation;

namespace OpenScct.Editor.Core.Animation;

/// <summary>Converts an ActorX skinned mesh to an ordinary GLB, retaining its material slots and four strongest weights.</summary>
internal static class ActorXMesh
{
    public static string Write(AnimationProject project, IReadOnlyDictionary<string, (int Size, int Count, byte[] Data)> chunks)
    {
        SkeletonDefinition skeleton = project.Skeletons[0];
        var points = chunks["PNTS0000"]; var wedges = chunks["VTXW0000"];
        if (points.Size != 12 || wedges.Size != 16) throw new InvalidDataException("Unsupported ActorX points or wedges.");
        var vertices = new Vector3[points.Count];
        using (var reader = new BinaryReader(new MemoryStream(points.Data)))
            for (int i = 0; i < vertices.Length; i++) { float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(); vertices[i] = new Vector3(x, z, y) / 100; }
        var positions = new List<float>(); var uv = new List<float>(); var pointIndex = new List<int>();
        using (var reader = new BinaryReader(new MemoryStream(wedges.Data)))
            for (int i = 0; i < wedges.Count; i++)
            {
                int point = checked((int)reader.ReadUInt32()); float u = reader.ReadSingle(), v = reader.ReadSingle(); reader.ReadBytes(4);
                if (point < 0 || point >= vertices.Length) throw new InvalidDataException("ActorX wedge references an unknown point.");
                Vector3 p = vertices[point]; positions.AddRange([p.X, p.Y, p.Z]); uv.AddRange([u, v]); pointIndex.Add(point);
            }
        var weights = Enumerable.Range(0, points.Count).Select(_ => new List<(int Bone, float Weight)>()).ToArray();
        if (chunks.TryGetValue("RAWWEIGHTS", out var storedWeights))
        {
            if (storedWeights.Size != 12) throw new InvalidDataException("Unsupported ActorX skin weights.");
            using var reader = new BinaryReader(new MemoryStream(storedWeights.Data));
            for (int i = 0; i < storedWeights.Count; i++)
            {
                float weight = reader.ReadSingle(); int point = reader.ReadInt32(), bone = reader.ReadInt32();
                if (point < 0 || point >= weights.Length || bone < 0 || bone >= skeleton.Bones.Count || !float.IsFinite(weight) || weight < 0) throw new InvalidDataException("Invalid ActorX skin weight.");
                weights[point].Add((bone, weight));
            }
        }
        var joints = new List<ushort>(); var jointWeights = new List<float>();
        foreach (int point in pointIndex)
        {
            var selected = weights[point].OrderByDescending(w => w.Weight).Take(4).ToArray(); float total = selected.Sum(w => w.Weight);
            for (int i = 0; i < 4; i++) { joints.Add((ushort)(i < selected.Length ? selected[i].Bone : 0)); jointWeights.Add(total > 0 && i < selected.Length ? selected[i].Weight / total : total == 0 && i == 0 ? 1 : 0); }
        }
        var faces = chunks.GetValueOrDefault("FACE0000"); bool wide = false;
        if (faces.Data is null) { faces = chunks.GetValueOrDefault("FACE3200"); wide = true; }
        if (faces.Data is null || (!wide && faces.Size != 12) || (wide && faces.Size is not (18 or 20))) throw new InvalidDataException("Unsupported ActorX faces.");
        var groups = new SortedDictionary<int, List<uint>>(); var normals = new Vector3[wedges.Count];
        using (var reader = new BinaryReader(new MemoryStream(faces.Data)))
            for (int i = 0; i < faces.Count; i++)
            {
                long at = reader.BaseStream.Position;
                uint a = wide ? reader.ReadUInt32() : reader.ReadUInt16(), b = wide ? reader.ReadUInt32() : reader.ReadUInt16(), c = wide ? reader.ReadUInt32() : reader.ReadUInt16();
                int material = reader.ReadByte(); reader.BaseStream.Position = at + faces.Size;
                if (a >= wedges.Count || b >= wedges.Count || c >= wedges.Count) throw new InvalidDataException("ActorX face references an unknown wedge.");
                if (!groups.TryGetValue(material, out var indices)) { indices = []; groups.Add(material, indices); }
                indices.AddRange([a, c, b]);
                Vector3 normal = Vector3.Cross(vertices[pointIndex[(int)c]] - vertices[pointIndex[(int)a]], vertices[pointIndex[(int)b]] - vertices[pointIndex[(int)a]]);
                normals[a] += normal; normals[b] += normal; normals[c] += normal;
            }
        string cache = Path.Combine(Path.GetTempPath(), "OpenSCCT-ActorX", skeleton.Id); Directory.CreateDirectory(cache);
        string output = Path.Combine(cache, "model.glb"); File.WriteAllBytes(output, AnimationExport.Glb(project, skeleton.Id));
        var data = new GltfAnimationImport(output); JsonObject json = data.Json;
        using var binary = new MemoryStream(); foreach (byte[] bytes in data.Buffers) binary.Write(bytes);
        int position = Floats(positions, 3, "VEC3", bounds: true), texcoords = Floats(uv, 2, "VEC2");
        int normalAccessor = Floats(normals.SelectMany(n => { Vector3 normal = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY; return new[] { normal.X, normal.Y, normal.Z }; }), 3, "VEC3");
        int weightAccessor = Floats(jointWeights, 4, "VEC4");
        int jointAccessor = Accessor(joints.Count / 4, 5123, "VEC4", writer => { foreach (ushort joint in joints) writer.Write(joint); });
        var inverseBind = new List<float>();
        foreach (Matrix4x4 world in ClipEvaluator.World(skeleton, skeleton.Bones.Select(b => b.Rest).ToArray()))
        {
            if (!Matrix4x4.Invert(world, out var m)) throw new InvalidDataException("ActorX reference skeleton has a singular transform.");
            inverseBind.AddRange([m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44]);
        }
        int bindAccessor = Floats(inverseBind, 16, "MAT4");
        var primitives = new JsonArray();
        json["materials"] = new JsonArray();
        int materialCount = Math.Max(1, groups.Keys.DefaultIfEmpty(0).Max() + 1);
        for (int i = 0; i < materialCount; i++)
        {
            string name = "Material " + i;
            if (chunks.TryGetValue("MATT0000", out var materials) && materials.Size >= 64 && i < materials.Count) name = Encoding.ASCII.GetString(materials.Data, i * materials.Size, 64).TrimEnd('\0');
            json["materials"]!.AsArray().Add(new JsonObject { ["name"] = name, ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 1 } });
        }
        foreach (var group in groups)
        {
            int indices = Accessor(group.Value.Count, 5125, "SCALAR", writer => { foreach (uint index in group.Value) writer.Write(index); });
            primitives.Add(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = position, ["NORMAL"] = normalAccessor, ["TEXCOORD_0"] = texcoords, ["JOINTS_0"] = jointAccessor, ["WEIGHTS_0"] = weightAccessor }, ["indices"] = indices, ["material"] = group.Key });
        }
        json["meshes"] = new JsonArray(new JsonObject { ["name"] = skeleton.Name, ["primitives"] = primitives });
        json["skins"] = new JsonArray(new JsonObject { ["inverseBindMatrices"] = bindAccessor, ["joints"] = new JsonArray(Enumerable.Range(0, skeleton.Bones.Count).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()), ["skeleton"] = 0 });
        int meshNode = json["nodes"]!.AsArray().Count; json["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "Mesh", ["mesh"] = 0, ["skin"] = 0 }); json["scenes"]![0]!["nodes"]!.AsArray().Add(meshNode);
        json["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = (int)binary.Length });
        byte[] document = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json); int jsonLength = (document.Length + 3) & ~3, binLength = ((int)binary.Length + 3) & ~3;
        using (var file = new BinaryWriter(File.Create(output)))
        {
            file.Write(0x46546c67u); file.Write(2); file.Write(12 + 8 + jsonLength + 8 + binLength); file.Write(jsonLength); file.Write(0x4e4f534au); file.Write(document);
            for (int i = document.Length; i < jsonLength; i++) file.Write((byte)0x20);
            file.Write(binLength); file.Write(0x004e4942u); file.Write(binary.ToArray()); for (long i = binary.Length; i < binLength; i++) file.Write((byte)0);
        }
        return output;
        int Floats(IEnumerable<float> source, int width, string type, bool bounds = false)
        {
            float[] values = source.ToArray(); int index = Accessor(values.Length / width, 5126, type, writer => { foreach (float v in values) writer.Write(v); });
            if (bounds)
            {
                var min = new JsonArray(); var max = new JsonArray();
                for (int axis = 0; axis < width; axis++) { min.Add(values.Where((_, i) => i % width == axis).Min()); max.Add(values.Where((_, i) => i % width == axis).Max()); }
                json["accessors"]![index]!["min"] = min; json["accessors"]![index]!["max"] = max;
            }
            return index;
        }
        int Accessor(int count, int component, string type, Action<BinaryWriter> write)
        {
            while (binary.Position % 4 != 0) binary.WriteByte(0); int offset = (int)binary.Position;
            using (var writer = new BinaryWriter(binary, Encoding.UTF8, true)) write(writer);
            int view = json["bufferViews"]!.AsArray().Count; json["bufferViews"]!.AsArray().Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = (int)binary.Position - offset });
            int accessor = json["accessors"]!.AsArray().Count; json["accessors"]!.AsArray().Add(new JsonObject { ["bufferView"] = view, ["componentType"] = component, ["count"] = count, ["type"] = type }); return accessor;
        }
    }
}
