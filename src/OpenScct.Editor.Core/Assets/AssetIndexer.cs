using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>The assets one source offers (a converted map, the shared content, the project), with the digest of the
/// files it was read from.</summary>
public sealed record AssetSource(string Name, string Digest, IReadOnlyList<AssetEntry> Entries);

/// <summary>
/// Reads the assets of one source. A converted map gives its textured materials (<c>render/materials.json</c>),
/// its library meshes with bounds and how often it places each (<c>render/world.glb</c>, JSON chunk only), its
/// texture files and its sounds (<c>audio/audio.json</c>). The shared content gives its named textures and the WAV
/// library. The project gives what <c>assets/assets.json</c> lists.
/// </summary>
public static class AssetIndexer
{
    /// <summary>The digest of a converted map: its manifest lists every file's SHA-256, so hashing it covers them.</summary>
    public static string MapDigest(string mapDir) => Hash(File.ReadAllBytes(Path.Combine(mapDir, "manifest.json")));

    public static AssetSource IndexMap(string mapDir)
    {
        string map = Path.GetFileName(mapDir);
        string render = Path.Combine(mapDir, "render");
        var entries = new List<AssetEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((_, LibraryMaterial m) in ContentLibrary.ReadMaterials(mapDir))
        {
            if (!seen.Add("m|" + m.Source))
            {
                continue;
            }

            string? texture = m.Texture is { } t ? Path.Combine(render, t) : null;
            entries.Add(new AssetEntry
            {
                Kind = AssetKind.Material, Path = m.Source, Origin = map, File = texture, Texture = texture, Width = m.USize, Height = m.VSize,
                UsedIn = One(map), Gloss = AssetGlossary.Gloss(m.Source),
            });
        }

        entries.AddRange(Meshes(mapDir, map));
        string textures = Path.Combine(render, "textures");
        if (Directory.Exists(textures))
        {
            foreach (string file in Directory.EnumerateFiles(textures, "*.png").Order(StringComparer.Ordinal))
            {
                // The map's baked lightmaps belong to its world, not to a library.
                if (!Path.GetFileName(file).StartsWith(map + "_lightmap", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(TextureEntry(file, map, TexturePath(file)));
                }
            }
        }

        entries.AddRange(MapSounds(mapDir, map));
        return new AssetSource(map, MapDigest(mapDir), entries);
    }

    private static IEnumerable<AssetEntry> Meshes(string mapDir, string map)
    {
        string path = Path.Combine(mapDir, "render", "world.glb");
        JsonObject? json = GlbCache.TryReadJson(path);
        if (json is null)
        {
            yield break;
        }

        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (json["extras"]?["svmap"]?["instances"] is JsonArray instances)
        {
            foreach (JsonNode? i in instances)
            {
                if (i?["mesh"]?.GetValue<string>() is { } node)
                {
                    string asset = node.Split('#')[0];
                    uses[asset] = uses.GetValueOrDefault(asset) + 1;
                }
            }
        }

        foreach (LibraryMesh m in ContentLibrary.ReadMeshes(mapDir))
        {
            yield return MeshEntry(json, m, path, map, uses.GetValueOrDefault(m.Asset));
        }
    }

    /// <summary>A mesh entry with bounds and triangle count from the glb's accessors (no binary read).</summary>
    public static AssetEntry MeshEntry(JsonObject glbJson, LibraryMesh m, string glbPath, string origin, int uses)
    {
        Vec3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        int triangles = 0;
        var materials = new List<string>();
        JsonArray? accessors = glbJson["accessors"] as JsonArray;
        if (glbJson["meshes"]?[m.Mesh]?["primitives"] is JsonArray prims)
        {
            foreach (JsonNode? p in prims)
            {
                if (p?["attributes"]?["POSITION"] is JsonValue pv && accessors?[pv.GetValue<int>()] is JsonObject acc &&
                    acc["min"] is JsonArray lo && acc["max"] is JsonArray hi)
                {
                    // glTF metres, Y up → Unreal units, Z up.
                    min = Vec3.Min(min, new Vec3(lo[0]!.GetValue<double>() * 100, lo[2]!.GetValue<double>() * 100, lo[1]!.GetValue<double>() * 100));
                    max = Vec3.Max(max, new Vec3(hi[0]!.GetValue<double>() * 100, hi[2]!.GetValue<double>() * 100, hi[1]!.GetValue<double>() * 100));
                }

                int count = p?["indices"] is JsonValue iv && accessors?[iv.GetValue<int>()]?["count"] is JsonValue c ? c.GetValue<int>()
                    : p?["attributes"]?["POSITION"] is JsonValue pv2 && accessors?[pv2.GetValue<int>()]?["count"] is JsonValue c2 ? c2.GetValue<int>() : 0;
                triangles += count / 3;
                if (p?["material"] is JsonValue mv && glbJson["materials"]?[mv.GetValue<int>()]?["name"]?.GetValue<string>() is { } name &&
                    !materials.Contains(name))
                {
                    materials.Add(name);
                }
            }
        }

        if (min.X > max.X)
        {
            (min, max) = (Vec3.Zero, Vec3.Zero);
        }

        return new AssetEntry
        {
            Kind = AssetKind.Mesh, Path = m.Asset, Origin = origin, File = glbPath, Mesh = m.Mesh, Triangles = triangles, BoundsMin = min,
            BoundsMax = max, Materials = materials, UsedIn = uses > 0 ? new Dictionary<string, int> { [origin] = uses } : new Dictionary<string, int>(),
            Gloss = AssetGlossary.Gloss(m.Asset),
        };
    }

    private static IEnumerable<AssetEntry> MapSounds(string mapDir, string map)
    {
        string file = Path.Combine(mapDir, "audio", "audio.json");
        if (!File.Exists(file) || Parse(file)?["sounds"] is not JsonObject sounds)
        {
            yield break;
        }

        foreach ((string path, JsonNode? s) in sounds)
        {
            string? rel = s?["file"]?.GetValue<string>();
            yield return new AssetEntry
            {
                Kind = AssetKind.Sound, Path = path, Origin = map, File = rel is null ? null : Path.Combine(mapDir, "audio", rel),
                Duration = s?["duration"] is JsonValue d && d.TryGetValue(out double dd) ? dd : 0, SoundKind = s?["kind"]?.GetValue<string>(),
                UsedIn = One(map), Gloss = AssetGlossary.Gloss(path),
            };
        }
    }

    /// <summary>The digest of the shared content: render.json and the names and sizes of the shared texture and WAV files.</summary>
    public static string SharedDigest(string contentRoot)
    {
        var sb = new StringBuilder();
        string renderJson = Path.Combine(contentRoot, "shared", "render", "render.json");
        if (File.Exists(renderJson))
        {
            sb.Append(Hash(File.ReadAllBytes(renderJson)));
        }

        foreach (string dir in SharedDirs(contentRoot))
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                sb.Append(Path.GetRelativePath(contentRoot, f)).Append('|').Append(new FileInfo(f).Length).Append('\n');
            }
        }

        return Hash(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static IEnumerable<string> SharedDirs(string contentRoot) =>
        new[] { Path.Combine(contentRoot, "shared", "render", "textures"), Path.Combine(contentRoot, "shared", "audio", "wav") }.Where(Directory.Exists);

    public static AssetSource IndexShared(string contentRoot)
    {
        var entries = new List<AssetEntry>();
        string render = Path.Combine(contentRoot, "shared", "render");
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Parse(Path.Combine(render, "render.json"))?["textures"] is JsonObject textures)
        {
            foreach ((string path, JsonNode? rel) in textures)
            {
                if (rel?.GetValue<string>() is { } r)
                {
                    named[Path.GetFullPath(Path.Combine(render, r))] = path;
                }
            }
        }

        string textureDir = Path.Combine(render, "textures");
        if (Directory.Exists(textureDir))
        {
            foreach (string file in Directory.EnumerateFiles(textureDir, "*.png").Order(StringComparer.Ordinal))
            {
                entries.Add(TextureEntry(file, "shared", named.GetValueOrDefault(Path.GetFullPath(file)) ?? TexturePath(file)));
            }
        }

        string wav = Path.Combine(contentRoot, "shared", "audio", "wav");
        if (Directory.Exists(wav))
        {
            foreach (string file in Directory.EnumerateFiles(wav, "*.wav", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(wav, file);
                string path = Path.ChangeExtension(rel, null).Replace('\\', '.').Replace('/', '.');
                entries.Add(new AssetEntry
                {
                    Kind = AssetKind.Sound, Path = path, Origin = "shared", File = file, Duration = WavSeconds(file), SoundKind = "sound",
                    Gloss = AssetGlossary.Gloss(path),
                });
            }
        }

        return new AssetSource("shared", SharedDigest(contentRoot), entries);
    }

    /// <summary>The project's own assets (never cached: the manifest is small and changes as you import).</summary>
    public static AssetSource IndexProject(ProjectAssets assets)
    {
        var entries = new List<AssetEntry>();
        foreach ((string path, JsonNode? _) in assets.Materials)
        {
            if (assets.FindMaterial(path) is { } m)
            {
                string? texture = m.Texture is { } t ? assets.FilePath(t) : null;
                entries.Add(new AssetEntry
                {
                    Kind = AssetKind.Material, Path = path, Origin = AssetPaths.LocalPackage, File = texture, Texture = texture, Width = m.USize,
                    Height = m.VSize, Gloss = AssetGlossary.Gloss(path),
                });
            }
        }

        foreach ((string path, JsonNode? _) in assets.Meshes)
        {
            if (assets.FindMesh(path) is { } m && GlbCache.TryReadJson(m.GlbPath) is { } json)
            {
                entries.Add(MeshEntry(json, m, m.GlbPath, AssetPaths.LocalPackage, 0));
            }
        }

        foreach ((string path, JsonNode? t) in assets.Textures)
        {
            if (t?["file"]?.GetValue<string>() is { } rel)
            {
                entries.Add(TextureEntry(assets.FilePath(rel), AssetPaths.LocalPackage, path));
            }
        }

        foreach ((string path, JsonNode? s) in assets.Sounds)
        {
            if (s?["file"]?.GetValue<string>() is { } rel)
            {
                string file = assets.FilePath(rel);
                entries.Add(new AssetEntry
                {
                    Kind = AssetKind.Sound, Path = path, Origin = AssetPaths.LocalPackage, File = file, SoundKind = "sound",
                    Duration = file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? WavSeconds(file) : 0,
                });
            }
        }

        return new AssetSource(AssetPaths.LocalPackage, "", entries);
    }

    private static AssetEntry TextureEntry(string file, string origin, string path)
    {
        (int w, int h) = Png.SizeOf(file) ?? (0, 0);
        return new AssetEntry
        {
            Kind = AssetKind.Texture, Path = path, Origin = origin, File = file, Texture = file, Width = w, Height = h,
            UsedIn = origin is "shared" or AssetPaths.LocalPackage ? new Dictionary<string, int>() : One(origin), Gloss = AssetGlossary.Gloss(path),
        };
    }

    /// <summary>A texture file's asset path: the converter names files Package_Name, so the first _ is the dot.</summary>
    private static string TexturePath(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file);
        int cut = stem.IndexOf('_', StringComparison.Ordinal);
        return cut > 0 && cut < stem.Length - 1 ? $"{stem[..cut]}.{stem[(cut + 1)..]}" : stem;
    }

    /// <summary>A PCM WAV's length from its header (0 when it cannot tell).</summary>
    private static double WavSeconds(string file)
    {
        try
        {
            using FileStream s = File.OpenRead(file);
            Span<byte> head = stackalloc byte[44];
            if (s.Read(head) < 44 || head[0] != 'R' || head[8] != 'W')
            {
                return 0;
            }

            int byteRate = BitConverter.ToInt32(head[28..32]);
            return byteRate > 0 ? Math.Max(0, s.Length - 44) / (double)byteRate : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static Dictionary<string, int> One(string map) => new() { [map] = 1 };

    private static JsonObject? Parse(string file)
    {
        try
        {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllBytes(file)) as JsonObject : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data))[..20];
}
