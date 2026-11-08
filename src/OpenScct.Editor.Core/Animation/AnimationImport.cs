using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using OpenScct.Animation;

namespace OpenScct.Editor.Core.Animation;

public static class AnimationImport
{
    /// <summary>The app/CLI supplies the packaged headless Godot importer; Core never references Godot.</summary>
    public static Func<string, string>? ConvertFbx { get; set; }
    public static AnimationProject Read(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".glb" or ".gltf" => new GltfAnimationImport(path).Read(path),
        ".bvh" => ReadBvh(path),
        ".psa" or ".psk" or ".pskx" => ReadActorX(path),
        ".fbx" => ReadFbx(path),
        _ => throw new InvalidDataException("Supported animation formats: GLB, glTF, FBX, BVH, PSK and PSA."),
    };
    private static AnimationProject ReadFbx(string path)
    {
        AnimationProject project = Read(ConvertFbx?.Invoke(path) ?? throw new InvalidOperationException("FBX import requires the packaged OpenSCCT Editor Godot helper."));
        foreach (AnimationClip clip in project.Clips) clip.Source = Path.GetFullPath(path);
        return project;
    }
    public static void CopyDependencies(AnimationProject project, string sourceFolder, string destinationFolder)
    {
        RewriteAudio(project, value =>
        {
            string extension = Path.GetExtension(value).ToLowerInvariant();
            if (extension is not (".wav" or ".ogg")) return value;
            string source = Path.IsPathRooted(value) ? value : Path.Combine(sourceFolder, value);
            byte[] bytes = File.ReadAllBytes(source);
            string relative = "assets/audio/" + OpenScct.Content.SvMapHash.Sha256Hex(bytes) + extension;
            string destination = Path.Combine(destinationFolder, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(destination, bytes);
            return relative;
        });
        foreach (SkeletonDefinition skeleton in project.Skeletons)
        {
            if (skeleton.Model is not { } model) continue;
            string source = Path.IsPathRooted(model) ? model : Path.Combine(sourceFolder, model);
            if (!File.Exists(source)) throw new FileNotFoundException("Missing animation model.", source);
            string relative = Path.Combine("assets", skeleton.Id, Path.GetFileName(source));
            string destination = Path.Combine(destinationFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) File.Copy(source, destination, true);
            if (Path.GetExtension(source).Equals(".gltf", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(source).Equals(".glb", StringComparison.OrdinalIgnoreCase))
            {
                var data = new GltfAnimationImport(source);
                foreach (string uri in new[] { "buffers", "images" }.SelectMany(key => data.Json[key]?.AsArray() ?? []).Select(n => n?["uri"]?.GetValue<string>()).OfType<string>())
                {
                    if (uri.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string name = Uri.UnescapeDataString(uri);
                    byte[] bytes = data.ReadUri(uri);
                    string output = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(destination)!, name));
                    if (!output.StartsWith(Path.GetDirectoryName(destination)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe glTF dependency.");
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllBytes(output, bytes);
                }
            }
            skeleton.Model = relative.Replace('\\', '/');
        }
    }
    public static void RewriteAudio(AnimationProject project, Func<string, string> rewrite)
    {
        void Events(IEnumerable<AnimationEvent> events)
        {
            foreach (AnimationEvent e in events) if (e.Kind is AnimationEventKind.Sound or AnimationEventKind.StopSound) e.Value = rewrite(e.Value);
        }
        foreach (AnimationClip clip in project.Clips) Events(clip.Events);
        foreach (AnimationSequence sequence in project.Sequences)
            foreach (SequenceTrack track in sequence.Tracks)
            {
                Events(track.Events);
                if (track.Kind == SequenceTrackKind.Audio)
                    foreach (SequenceSegment segment in track.Segments) if (segment.Asset is { } asset) segment.Asset = rewrite(asset);
            }
    }
    public static AnimationProject ReadBvh(string path)
    {
        string[] tokens = Regex.Matches(File.ReadAllText(path), @"[{}]|[^\s{}]+").Select(m => m.Value).ToArray();
        int cursor = 0;
        string Take() => cursor < tokens.Length ? tokens[cursor++] : throw new InvalidDataException("Truncated BVH.");
        void Expect(string token) { if (!Take().Equals(token, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("BVH expected " + token); }
        float Number() => float.Parse(Take(), CultureInfo.InvariantCulture);
        var skeleton = new SkeletonDefinition { Name = Path.GetFileNameWithoutExtension(path) };
        var channels = new List<(int Bone, string Channel)>();
        void Joint(int parent, string name)
        {
            if (skeleton.Bones.Count >= 4096) throw new InvalidDataException("BVH has too many joints.");
            int index = skeleton.Bones.Count; skeleton.Bones.Add(new() { Name = name, Parent = parent }); Expect("{");
            while (true)
            {
                string token = Take(); if (token == "}") break;
                switch (token.ToUpperInvariant())
                {
                    case "OFFSET": skeleton.Bones[index].Rest = new(new Vector3(Number(), Number(), Number()) * .01f, Quaternion.Identity, Vector3.One); break;
                    case "CHANNELS":
                        int count = checked((int)Number()); if (count < 0 || count > 6) throw new InvalidDataException("Invalid BVH channel count.");
                        for (int i = 0; i < count; i++) channels.Add((index, Take())); break;
                    case "JOINT": Joint(index, Take()); break;
                    case "END": Expect("Site"); Joint(index, name + "_End" + index); break;
                    default: throw new InvalidDataException("Unknown BVH token: " + token);
                }
            }
        }
        Expect("HIERARCHY"); Expect("ROOT"); Joint(-1, Take()); Expect("MOTION"); Expect("Frames:"); int frames = checked((int)Number());
        Expect("Frame"); Expect("Time:"); float step = Number();
        if (frames < 1 || frames > 1_000_000 || !(step > 0) || !float.IsFinite(step) || (long)frames * channels.Count > 16_000_000) throw new InvalidDataException("Invalid BVH frame count/rate.");
        var clip = new AnimationClip { Name = skeleton.Name, Skeleton = skeleton.Id, Source = Path.GetFullPath(path), FramesPerSecond = 1 / step, Duration = MathF.Max(step, (frames - 1) * step) };
        for (int frame = 0; frame < frames; frame++)
        {
            BoneTransform[] pose = skeleton.Bones.Select(b => b.Rest).ToArray();
            foreach (var (bone, channel) in channels)
            {
                float value = Number(); if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite BVH value.");
                BoneTransform p = pose[bone];
                Vector3 axis = char.ToUpperInvariant(channel[0]) switch { 'X' => Vector3.UnitX, 'Y' => Vector3.UnitY, 'Z' => Vector3.UnitZ, _ => throw new InvalidDataException("Unknown BVH axis.") };
                if (channel.EndsWith("position", StringComparison.OrdinalIgnoreCase)) p = p with { Position = p.Position + axis * value * .01f };
                else if (channel.EndsWith("rotation", StringComparison.OrdinalIgnoreCase)) p = p with { Rotation = Quaternion.Normalize(p.Rotation * Quaternion.CreateFromAxisAngle(axis, value * MathF.PI / 180)) };
                else throw new InvalidDataException("Unknown BVH channel: " + channel);
                pose[bone] = p;
            }
            for (int bone = 0; bone < pose.Length; bone++) AnimationEdits.AddPoseKeys(clip, skeleton.Bones[bone].Name, pose[bone], frame * step);
        }
        var project = new AnimationProject { Name = skeleton.Name, Skeletons = [skeleton], Clips = [clip], Scene = [new() { Name = skeleton.Name, Skeleton = skeleton.Id, Clip = clip.Id }] };
        AnimationJson.Validate(project); return project;
    }
    private sealed record Chunk(int Size, int Count, byte[] Data);
    private static Dictionary<string, Chunk> Chunks(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path)); var chunks = new Dictionary<string, Chunk>(StringComparer.Ordinal);
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            if (reader.BaseStream.Length - reader.BaseStream.Position < 32) throw new InvalidDataException("Truncated ActorX header.");
            string name = Encoding.ASCII.GetString(reader.ReadBytes(20)).TrimEnd('\0'); reader.ReadInt32();
            int size = reader.ReadInt32(), count = reader.ReadInt32();
            long length = (long)size * count;
            if (size < 0 || count < 0 || length > 256_000_000 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid ActorX chunk size.");
            if (!chunks.TryAdd(name, new(size, count, reader.ReadBytes((int)length)))) throw new InvalidDataException("Duplicate ActorX chunk: " + name);
        }
        return chunks;
    }
    private static AnimationProject ReadActorX(string path)
    {
        Dictionary<string, Chunk> chunks = Chunks(path);
        Chunk bones = chunks.GetValueOrDefault("BONENAMES") ?? chunks.GetValueOrDefault("REFSKELT") ?? throw new InvalidDataException("ActorX file has no bones.");
        if (bones.Size != 120 || bones.Count is < 1 or > 4096) throw new InvalidDataException("Unsupported ActorX skeleton layout.");
        var skeleton = new SkeletonDefinition { Name = Path.GetFileNameWithoutExtension(path) };
        using (var reader = new BinaryReader(new MemoryStream(bones.Data)))
            for (int i = 0; i < bones.Count; i++)
            {
                string name = Encoding.ASCII.GetString(reader.ReadBytes(64)).TrimEnd('\0'); reader.ReadInt32(); reader.ReadInt32(); int parent = reader.ReadInt32();
                Quaternion rotation = ReadQuaternion(reader); Vector3 position = ReadPosition(reader); reader.ReadBytes(16);
                skeleton.Bones.Add(new() { Name = name, Parent = i == 0 ? -1 : parent, Rest = new(position, rotation, Vector3.One) });
            }
        var project = new AnimationProject { Name = skeleton.Name, Skeletons = [skeleton] };
        if (chunks.TryGetValue("ANIMINFO", out Chunk? info) && chunks.TryGetValue("ANIMKEYS", out Chunk? keys))
        {
            if (info.Size != 168 || keys.Size != 32) throw new InvalidDataException("Unsupported ActorX animation layout.");
            using var reader = new BinaryReader(new MemoryStream(info.Data));
            for (int i = 0; i < info.Count; i++)
            {
                string name = Encoding.ASCII.GetString(reader.ReadBytes(64)).TrimEnd('\0'); reader.ReadBytes(64);
                int count = reader.ReadInt32(); reader.ReadBytes(20); float rate = reader.ReadSingle(); int startBone = reader.ReadInt32(); int first = reader.ReadInt32(), frames = reader.ReadInt32();
                if (count != bones.Count || startBone != 0 || frames < 1 || first < 0 || rate <= 0 || !float.IsFinite(rate) || ((long)first + frames) * count > keys.Count) throw new InvalidDataException("Invalid ActorX animation range.");
                var clip = new AnimationClip { Name = name, Skeleton = skeleton.Id, FramesPerSecond = rate, Duration = MathF.Max(1 / rate, (frames - 1) / rate), Source = Path.GetFullPath(path) };
                using var keyReader = new BinaryReader(new MemoryStream(keys.Data)); keyReader.BaseStream.Position = (long)first * count * 32;
                for (int frame = 0; frame < frames; frame++) for (int bone = 0; bone < count; bone++)
                {
                    Vector3 position = ReadPosition(keyReader); Quaternion rotation = ReadQuaternion(keyReader); keyReader.ReadSingle();
                    AnimationEdits.AddPoseKeys(clip, skeleton.Bones[bone].Name, new(position, rotation, Vector3.One), frame / rate);
                }
                project.Clips.Add(clip);
            }
        }
        project.Scene.Add(new() { Name = skeleton.Name, Skeleton = skeleton.Id, Clip = project.Clips.FirstOrDefault()?.Id });
        if (chunks.ContainsKey("PNTS0000") && chunks.ContainsKey("VTXW0000"))
            skeleton.Model = ActorXMesh.Write(project, chunks.ToDictionary(c => c.Key, c => (c.Value.Size, c.Value.Count, c.Value.Data), StringComparer.Ordinal));
        AnimationJson.Validate(project); return project;
    }
    private static Vector3 ReadPosition(BinaryReader reader) { float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(); return new Vector3(x, z, y) / 100; }
    private static Quaternion ReadQuaternion(BinaryReader reader)
    {
        float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(), w = reader.ReadSingle();
        return Quaternion.Normalize(new(-x, -z, -y, w));
    }
}
