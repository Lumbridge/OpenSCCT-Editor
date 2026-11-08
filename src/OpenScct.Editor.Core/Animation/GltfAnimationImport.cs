using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using OpenScct.Animation;

namespace OpenScct.Editor.Core.Animation;

/// <summary>Reads original glTF keys without Godot's animation resampling.</summary>
public sealed class GltfAnimationImport
{
    public JsonObject Json { get; }
    public List<byte[]> Buffers { get; } = [];
    public string DirectoryPath { get; }
    public GltfAnimationImport(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        DirectoryPath = Path.GetDirectoryName(Path.GetFullPath(path))!;
        byte[]? bin = null;
        if (file.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(file) == 0x46546c67)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)) != file.Length)
                throw new InvalidDataException("Invalid GLB header.");
            JsonObject? json = null;
            for (int at = 12; at < file.Length;)
            {
                if (at + 8 > file.Length) throw new InvalidDataException("Truncated GLB chunk.");
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)));
                uint kind = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4)); at += 8;
                if (length < 0 || length > file.Length - at) throw new InvalidDataException("Invalid GLB chunk length.");
                if (kind == 0x4e4f534a) json = JsonNode.Parse(file.AsSpan(at, length))?.AsObject();
                if (kind == 0x004e4942) bin = file.AsSpan(at, length).ToArray();
                at += length;
            }
            Json = json ?? throw new InvalidDataException("GLB has no JSON chunk.");
        }
        else Json = JsonNode.Parse(file)?.AsObject() ?? throw new InvalidDataException("Empty glTF.");
        if (Json["asset"]?["version"]?.GetValue<string>() != "2.0") throw new InvalidDataException("Expected glTF 2.0.");
        foreach (JsonNode? buffer in Json["buffers"]?.AsArray() ?? [])
        {
            string? uri = buffer?["uri"]?.GetValue<string>();
            byte[] bytes = uri is null ? bin ?? throw new InvalidDataException("Missing GLB binary chunk.") : ReadUri(uri);
            int length = buffer?["byteLength"]?.GetValue<int>() ?? -1;
            if (length < 0 || bytes.Length < length) throw new InvalidDataException("Truncated glTF buffer.");
            Buffers.Add(bytes);
        }
    }
    public byte[] ReadUri(string uri)
    {
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = uri.IndexOf(',');
            if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.Ordinal)) throw new InvalidDataException("Only base64 glTF data URIs are supported.");
            return Convert.FromBase64String(uri[(comma + 1)..]);
        }
        string path = Path.GetFullPath(Path.Combine(DirectoryPath, Uri.UnescapeDataString(uri)));
        if (!path.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("glTF dependency escapes its source folder.");
        return File.ReadAllBytes(path);
    }
    public float[] Floats(int index)
    {
        JsonNode accessor = Json["accessors"]![index]!;
        if (accessor["sparse"] is not null) throw new InvalidDataException("Sparse animation accessors are not supported.");
        if (accessor["componentType"]!.GetValue<int>() != 5126) throw new InvalidDataException("Animation accessors must use float32.");
        int count = accessor["count"]!.GetValue<int>();
        int width = accessor["type"]!.GetValue<string>() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new InvalidDataException("Unsupported accessor shape.") };
        if (count < 0 || count > 16_000_000 / width) throw new InvalidDataException("Animation accessor is too large.");
        JsonNode view = Json["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
        byte[] buffer = Buffers[view["buffer"]!.GetValue<int>()];
        int viewOffset = view["byteOffset"]?.GetValue<int>() ?? 0, localOffset = accessor["byteOffset"]?.GetValue<int>() ?? 0;
        int length = view["byteLength"]!.GetValue<int>(), stride = view["byteStride"]?.GetValue<int>() ?? width * 4;
        if (localOffset < 0 || viewOffset < 0 || length < 0 || stride < width * 4 || viewOffset > buffer.Length - length ||
            (count > 0 && (long)localOffset + (long)(count - 1) * stride + width * 4 > length)) throw new InvalidDataException("Animation accessor exceeds its buffer view.");
        var values = new float[count * width];
        for (int i = 0; i < count; i++) for (int c = 0; c < width; c++) values[i * width + c] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(viewOffset + localOffset + i * stride + c * 4, 4));
        return values;
    }
    public AnimationProject Read(string source)
    {
        JsonArray nodes = Json["nodes"]?.AsArray() ?? throw new InvalidDataException("glTF has no nodes.");
        var parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
        for (int i = 0; i < nodes.Count; i++)
            foreach (JsonNode? child in nodes[i]?["children"]?.AsArray() ?? [])
            {
                int index = child!.GetValue<int>();
                if (index < 0 || index >= nodes.Count || parents[index] >= 0) throw new InvalidDataException("Invalid glTF node hierarchy.");
                parents[index] = i;
            }
        var joints = new HashSet<int>();
        foreach (JsonNode? skin in Json["skins"]?.AsArray() ?? []) foreach (JsonNode? joint in skin?["joints"]?.AsArray() ?? []) joints.Add(joint!.GetValue<int>());
        foreach (JsonNode? clip in Json["animations"]?.AsArray() ?? []) foreach (JsonNode? channel in clip?["channels"]?.AsArray() ?? [])
            if (channel?["target"]?["path"]?.GetValue<string>() != "weights") joints.Add(channel!["target"]!["node"]!.GetValue<int>());
        if (joints.Count == 0) throw new InvalidDataException("The file has no skeleton or transform animations.");
        foreach (int joint in joints.ToArray())
        {
            var visited = new HashSet<int>();
            for (int node = joint; node >= 0; node = parents[node])
            {
                if (node >= nodes.Count || !visited.Add(node)) throw new InvalidDataException("Cyclic or invalid glTF node hierarchy.");
                joints.Add(node);
            }
        }
        var ordered = new List<int>(); var seen = new HashSet<int>();
        void Add(int node) { if (!seen.Add(node)) return; if (parents[node] >= 0) Add(parents[node]); ordered.Add(node); }
        foreach (int node in joints.Order()) Add(node);
        var skeleton = new SkeletonDefinition { Name = Path.GetFileNameWithoutExtension(source), Model = Path.GetFullPath(source) };
        if (Json["extras"]?["svmap"] is JsonObject metadata)
        {
            var rotation = metadata["rotOrigin"] is JsonArray r ? new OpenScct.Core.FRotator(r[0]!.GetValue<int>(), r[1]!.GetValue<int>(), r[2]!.GetValue<int>()) : default;
            Vector3 scale = metadata["meshScale"] is JsonArray s ? new(s[0]!.GetValue<float>(), s[2]!.GetValue<float>(), s[1]!.GetValue<float>()) : Vector3.One;
            skeleton.DisplayTransform = new(Vector3.Zero, StudioSpace.FromUe(rotation), scale);
        }
        var names = new Dictionary<int, string>(); var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (int node in ordered)
        {
            string name = nodes[node]?["name"]?.GetValue<string>() ?? "Bone" + node;
            if (!used.Add(name)) throw new InvalidDataException("Duplicate bone name: " + name);
            names[node] = name;
            skeleton.Bones.Add(new() { Name = name, Parent = parents[node] < 0 ? -1 : ordered.IndexOf(parents[node]), Rest = Local(nodes[node]!) });
        }
        var project = new AnimationProject { Name = skeleton.Name, Skeletons = [skeleton] };
        int number = 0;
        foreach (JsonNode? animation in Json["animations"]?.AsArray() ?? [])
        {
            var clip = new AnimationClip { Name = animation?["name"]?.GetValue<string>() ?? "Clip " + ++number, Skeleton = skeleton.Id, Duration = 0, Source = Path.GetFullPath(source) };
            foreach (JsonNode? channel in animation!["channels"]!.AsArray())
            {
                string path = channel!["target"]!["path"]!.GetValue<string>();
                if (path == "weights") continue;
                TrackChannel kind = path switch { "translation" => TrackChannel.Translation, "rotation" => TrackChannel.Rotation, "scale" => TrackChannel.Scale, _ => throw new InvalidDataException("Unsupported animation channel: " + path) };
                JsonNode sampler = animation["samplers"]![channel["sampler"]!.GetValue<int>()]!;
                float[] times = Floats(sampler["input"]!.GetValue<int>()), values = Floats(sampler["output"]!.GetValue<int>());
                KeyInterpolation interpolation = (sampler["interpolation"]?.GetValue<string>() ?? "LINEAR") switch { "LINEAR" => KeyInterpolation.Linear, "STEP" => KeyInterpolation.Step, "CUBICSPLINE" => KeyInterpolation.Cubic, _ => throw new InvalidDataException("Unknown glTF interpolation.") };
                int width = kind == TrackChannel.Rotation ? 4 : 3, multiplier = interpolation == KeyInterpolation.Cubic ? 3 : 1;
                if (times.Length == 0 || values.Length != times.Length * width * multiplier) throw new InvalidDataException("Mismatched animation keys.");
                var track = new BoneTrack { Bone = names[channel["target"]!["node"]!.GetValue<int>()], Channel = kind };
                Vector4 Value(int at) => new(values[at], values[at + 1], values[at + 2], width == 4 ? values[at + 3] : 0);
                for (int i = 0; i < times.Length; i++)
                {
                    int at = i * width * multiplier;
                    track.Keys.Add(new() { Time = times[i], Value = Value(at + (multiplier == 3 ? width : 0)), InTangent = multiplier == 3 ? Value(at) : default,
                        OutTangent = multiplier == 3 ? Value(at + 2 * width) : default, Interpolation = interpolation });
                    clip.Duration = MathF.Max(clip.Duration, times[i]);
                }
                clip.Tracks.Add(track);
            }
            clip.Duration = MathF.Max(clip.Duration, 1f / 30);
            clip.SourceClip = clip.Name; project.Clips.Add(clip);
        }
        project.Scene.Add(new() { Name = skeleton.Name, Skeleton = skeleton.Id, Clip = project.Clips.FirstOrDefault()?.Id });
        StockAnimationMetadata.Apply(project, this, source);
        AnimationJson.Validate(project); return project;
    }
    private static BoneTransform Local(JsonNode node)
    {
        if (node["matrix"] is JsonArray m)
        {
            float[] a = m.Select(x => x!.GetValue<float>()).ToArray();
            if (a.Length != 16) throw new InvalidDataException("Invalid glTF matrix.");
            return BoneTransform.FromMatrix(new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]));
        }
        Vector3 Read3(string name, Vector3 fallback) => node[name] is JsonArray a ? new(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()) : fallback;
        Quaternion rotation = node["rotation"] is JsonArray q ? new(q[0]!.GetValue<float>(), q[1]!.GetValue<float>(), q[2]!.GetValue<float>(), q[3]!.GetValue<float>()) : Quaternion.Identity;
        return new(Read3("translation", Vector3.Zero), Quaternion.Normalize(rotation), Read3("scale", Vector3.One));
    }
}
