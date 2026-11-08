using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using OpenScct.Animation;

namespace OpenScct.Editor.Core.Animation;

/// <summary>Portable glTF export preserves source geometry and directly representable animation keys.</summary>
public static class AnimationExport
{
    public static byte[] Glb(AnimationProject project, string skeletonId, string? projectFolder = null, float bakeRate = 60)
    {
        AnimationJson.Validate(project);
        SkeletonDefinition skeleton = project.Skeletons.Single(s => s.Id == skeletonId);
        JsonObject json;
        using var binary = new MemoryStream();
        if (skeleton.Model is { } model && File.Exists(Path.IsPathRooted(model) ? model : Path.Combine(projectFolder ?? ".", model)))
        {
            var original = new GltfAnimationImport(Path.IsPathRooted(model) ? model : Path.Combine(projectFolder ?? ".", model));
            json = (JsonObject)original.Json.DeepClone();
            var offsets = new List<int>();
            foreach (byte[] buffer in original.Buffers) { Align(binary); offsets.Add((int)binary.Position); binary.Write(buffer); }
            foreach (JsonNode? view in json["bufferViews"]?.AsArray() ?? [])
            {
                int buffer = view!["buffer"]!.GetValue<int>(); view["byteOffset"] = (view["byteOffset"]?.GetValue<int>() ?? 0) + offsets[buffer]; view["buffer"] = 0;
            }
            json["bufferViews"] ??= new JsonArray();
            foreach (JsonNode? image in json["images"]?.AsArray() ?? [])
            {
                if (image?["uri"]?.GetValue<string>() is not { } uri) continue;
                Align(binary); int offset = (int)binary.Position; byte[] bytes = original.ReadUri(uri); binary.Write(bytes);
                int index = json["bufferViews"]!.AsArray().Count;
                json["bufferViews"]!.AsArray().Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = bytes.Length });
                image["bufferView"] = index; image["mimeType"] = uri.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || uri.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("data:image/jpeg", StringComparison.Ordinal) ? "image/jpeg" : "image/png"; image.AsObject().Remove("uri");
            }
        }
        else
        {
            json = new() { ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "OpenSCCT Animation Studio" }, ["nodes"] = new JsonArray(), ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray() }), ["scene"] = 0 };
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                BoneDefinition bone = skeleton.Bones[i];
                json["nodes"]!.AsArray().Add(new JsonObject { ["name"] = bone.Name, ["translation"] = Array(bone.Rest.Position), ["rotation"] = Array(ClipEvaluator.V(bone.Rest.Rotation)), ["scale"] = Array(bone.Rest.Scale) });
            }
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                int parent = skeleton.Bones[i].Parent;
                if (parent < 0) json["scenes"]![0]!["nodes"]!.AsArray().Add(i);
                else { JsonObject node = json["nodes"]![parent]!.AsObject(); node["children"] ??= new JsonArray(); node["children"]!.AsArray().Add(i); }
            }
        }
        json["bufferViews"] ??= new JsonArray(); json["accessors"] ??= new JsonArray();
        var nodeByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < json["nodes"]!.AsArray().Count; i++)
            if (json["nodes"]![i]?["name"]?.GetValue<string>() is { } name) nodeByName.TryAdd(name, i);
        var animations = new JsonArray();
        foreach (AnimationClip source in project.Clips.Where(c => c.Skeleton == skeletonId))
        {
            AnimationClip clip = source.Layers.Count > 0 || source.Constraints.Count > 0 ? AnimationEdits.BakeScene(project, source, bakeRate) : source;
            var samplers = new JsonArray(); var channels = new JsonArray();
            foreach (BoneTrack track in clip.Tracks)
            {
                if (track.Keys.Count == 0 || !nodeByName.TryGetValue(track.Bone, out int node)) continue;
                // glTF interpolation is per sampler. Mixed native segments are sampled, not silently flattened.
                List<AnimationKey> keys = track.Keys;
                bool mixed = keys.Take(Math.Max(1, keys.Count - 1)).Select(k => k.Interpolation).Distinct().Count() > 1;
                if (mixed)
                {
                    keys = []; int count = Math.Max(1, (int)MathF.Ceiling(clip.Duration * bakeRate));
                    for (int i = 0; i <= count; i++) { float time = clip.Duration * i / count; keys.Add(new() { Time = time, Value = ClipEvaluator.Sample(track.Keys, time, track.Channel == TrackChannel.Rotation) }); }
                }
                int width = track.Channel == TrackChannel.Rotation ? 4 : 3;
                KeyInterpolation interpolation = keys[0].Interpolation;
                int input = Accessor(keys.Select(k => k.Time).ToArray(), 1, "SCALAR", true);
                var values = new List<float>();
                foreach (AnimationKey key in keys)
                {
                    if (interpolation == KeyInterpolation.Cubic) Add(key.InTangent);
                    Add(key.Value);
                    if (interpolation == KeyInterpolation.Cubic) Add(key.OutTangent);
                }
                void Add(Vector4 v) { values.Add(v.X); values.Add(v.Y); values.Add(v.Z); if (width == 4) values.Add(v.W); }
                int output = Accessor(values.ToArray(), width, width == 4 ? "VEC4" : "VEC3", false);
                int sampler = samplers.Count;
                samplers.Add(new JsonObject { ["input"] = input, ["output"] = output, ["interpolation"] = interpolation switch { KeyInterpolation.Step => "STEP", KeyInterpolation.Cubic => "CUBICSPLINE", _ => "LINEAR" } });
                channels.Add(new JsonObject { ["sampler"] = sampler, ["target"] = new JsonObject { ["node"] = node, ["path"] = track.Channel switch { TrackChannel.Translation => "translation", TrackChannel.Rotation => "rotation", _ => "scale" } } });
            }
            if (channels.Count > 0) animations.Add(new JsonObject { ["name"] = clip.Name, ["channels"] = channels, ["samplers"] = samplers,
                ["extras"] = new JsonObject { ["osanimId"] = clip.Id, ["duration"] = clip.Duration, ["loop"] = clip.Loop } });
        }
        json["animations"] = animations;
        // Avoid identifying authored clips as the converter's LINEAR-only legacy track format.
        if (json["extras"]?["svmap"] is JsonObject metadata) metadata.Remove("animation");
        json["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = (int)binary.Length });
        byte[] document = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json);
        int jsonLength = (document.Length + 3) & ~3, binLength = ((int)binary.Length + 3) & ~3;
        var glb = new byte[12 + 8 + jsonLength + 8 + binLength];
        void UInt(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(at), value);
        UInt(0, 0x46546c67); UInt(4, 2); UInt(8, (uint)glb.Length); UInt(12, (uint)jsonLength); UInt(16, 0x4e4f534a);
        glb.AsSpan(20, jsonLength).Fill(0x20); document.CopyTo(glb, 20);
        UInt(20 + jsonLength, (uint)binLength); UInt(24 + jsonLength, 0x004e4942); binary.ToArray().CopyTo(glb, 28 + jsonLength);
        return glb;

        int Accessor(float[] values, int width, string type, bool bounds)
        {
            Align(binary); int offset = (int)binary.Position;
            using (var writer = new BinaryWriter(binary, System.Text.Encoding.UTF8, true)) foreach (float value in values) writer.Write(value);
            int view = json["bufferViews"]!.AsArray().Count;
            json["bufferViews"]!.AsArray().Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Length * 4 });
            var accessor = new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = values.Length / width, ["type"] = type };
            if (bounds) { accessor["min"] = new JsonArray(values.Min()); accessor["max"] = new JsonArray(values.Max()); }
            int result = json["accessors"]!.AsArray().Count; json["accessors"]!.AsArray().Add(accessor); return result;
        }
    }
    public static void Write(AnimationProject project, string skeletonId, string path, string? folder = null, float bakeRate = 60)
    {
        byte[] glb = Glb(project, skeletonId, folder, bakeRate);
        if (Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
            JsonObject json = JsonNode.Parse(glb.AsSpan(20, jsonLength))!.AsObject();
            int binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(20 + jsonLength));
            string buffer = Path.GetFileNameWithoutExtension(path) + ".bin"; json["buffers"]![0]!["uri"] = buffer;
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, buffer), glb.AsSpan(28 + jsonLength, binLength).ToArray());
            File.WriteAllText(path, json.ToJsonString(new() { WriteIndented = true }));
        }
        else File.WriteAllBytes(path, glb);
    }
    private static void Align(MemoryStream stream) { while (stream.Position % 4 != 0) stream.WriteByte(0); }
    private static JsonArray Array(Vector3 v) => new(v.X, v.Y, v.Z);
    private static JsonArray Array(Vector4 v) => new(v.X, v.Y, v.Z, v.W);
}
