using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Compile;

/// <summary>Writes a compiled map folder (files, then <c>manifest.json</c> with the digests and group hashes OpenSCCT
/// verifies) and checks one with OpenSCCT's own strict loader.</summary>
public static class MapFolder
{
    /// <summary>
    /// Writes <paramref name="files"/> to a fresh folder next to <paramref name="target"/>, adds the manifest, then
    /// swaps it in. An existing target is replaced only when it is empty or an SVMap folder.
    /// </summary>
    public static void Write(string target, string mapId, string title, string formatVersion, IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyList<(string Pack, string Sha256)> requires)
    {
        string full = Path.GetFullPath(target);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, SvMapHash.ManifestFile)))
        {
            throw new IOException($"{full} exists and is not a map folder; not replacing it");
        }

        string staging = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".building";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        foreach ((string rel, byte[] data) in files)
        {
            string path = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        File.WriteAllBytes(Path.Combine(staging, SvMapHash.ManifestFile), Manifest(staging, mapId, title, formatVersion, requires));
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Directory.Move(staging, full);
    }

    /// <summary>The manifest of the files under <paramref name="root"/> (every file but the manifest).</summary>
    public static byte[] Manifest(string root, string mapId, string title, string formatVersion, IReadOnlyList<(string Pack, string Sha256)> requires)
    {
        SvMapHashes hashes = SvMapHash.HashMap(root);
        var files = new SvJsonObject();
        foreach (SvMapFileDigest f in hashes.Files)
        {
            var entry = new SvJsonObject();
            entry.Add("sha256", SvJsonNode.String(f.Sha256));
            entry.Add("size", SvJsonNode.Integer(f.Size));
            files.Add(f.Path, entry);
        }

        var groups = new SvJsonObject();
        foreach ((string group, string hash) in hashes.Groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            groups.Add(group, SvJsonNode.String(hash));
        }

        var req = new SvJsonArray();
        foreach ((string pack, string sha) in requires)
        {
            var r = new SvJsonObject();
            r.Add("pack", SvJsonNode.String(pack));
            r.Add("sha256", SvJsonNode.String(sha));
            req.Add(r);
        }

        var source = new SvJsonObject();
        source.Add("generator", SvJsonNode.String(CompileContext.Generator));
        source.Add("generatorVersion", SvJsonNode.Integer(CompileContext.GeneratorVersion));
        source.Add("kind", SvJsonNode.String("authored"));
        var root2 = new SvJsonObject();
        root2.Add("files", files);
        root2.Add("format", SvJsonNode.String(SvMapManifest.Format));
        root2.Add("formatVersion", SvJsonNode.String(formatVersion));
        root2.Add("hashes", groups);
        root2.Add("map", SvJsonNode.String(mapId));
        root2.Add("requires", req);
        root2.Add("source", source);
        root2.Add("title", SvJsonNode.String(title));
        return CanonicalJson.ToFileBytes(root2);
    }

    /// <summary>
    /// Loads a map folder the way OpenSCCT does (strict collision, sim hash, references) and checks the manifest
    /// against every file and the render files' structure. Returns findings (empty when the folder is good) and
    /// counts.
    /// </summary>
    public static (List<Finding> Findings, SortedDictionary<string, long> Stats) Validate(string folder)
    {
        var findings = new List<Finding>();
        var stats = new SortedDictionary<string, long>(StringComparer.Ordinal);
        try
        {
            SvMap map = SvMap.Load(folder, new SvMapLoadOptions { VerifySimHash = true, StrictCollision = true, CheckReferences = true });
            stats["actors"] = map.Gameplay.AllActors.Count;
            stats["ges"] = map.Traversal.Ges.Count;
            stats["colliders"] = map.Collision.Colliders.Length;
            stats["triangles"] = map.Collision.Triangles.Length;
            stats["bspNodes"] = map.Collision.BspNodes.Length;
            stats["zones"] = map.Collision.Zones.Length;
            foreach (string p in map.Manifest.Verify(folder))
            {
                findings.Add(Finding.Error("validate.manifest", p));
            }

            // One map, one version: OpenSCCT's validator rejects a folder whose files declare different versions.
            string[] versions = [map.Manifest.FormatVersion, map.Gameplay.FormatVersion, map.Traversal.FormatVersion];
            if (versions.Distinct(StringComparer.Ordinal).Count() > 1)
            {
                findings.Add(Finding.Error("validate.version",
                    $"formatVersion differs between manifest.json ({versions[0]}), sim/gameplay.json ({versions[1]}) and sim/traversal.json ({versions[2]})"));
            }

            foreach (string p in map.Gameplay.CheckDerived())
            {
                findings.Add(Finding.Error("validate.derived", p));
            }

            foreach (string p in map.Gameplay.CheckReferences())
            {
                findings.Add(Finding.Warning("validate.reference", p));
            }
        }
        catch (Exception e) when (e is SvMapFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            findings.Add(Finding.Error("validate.load", e.Message));
            return (findings, stats);
        }

        string glb = Path.Combine(folder, "render", "world.glb");
        if (File.Exists(glb))
        {
            try
            {
                Glb doc = Glb.Load(File.ReadAllBytes(glb));
                stats["render.meshes"] = doc.Count("meshes");
                stats["render.nodes"] = doc.Count("nodes");
                foreach (System.Text.Json.Nodes.JsonNode? image in doc.Json["images"] as System.Text.Json.Nodes.JsonArray ?? [])
                {
                    if (image?["uri"]?.GetValue<string>() is { } uri && !File.Exists(Path.Combine(folder, "render", uri)))
                    {
                        findings.Add(Finding.Error("validate.texture", $"render/world.glb names {uri}, which is missing"));
                    }
                }
            }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
            {
                findings.Add(Finding.Error("validate.glb", $"render/world.glb: {e.Message}"));
            }
        }

        foreach (string json in new[] { "render/materials.json", "render/lighting.json", "render/fx.json", "ui/mapinfo.json", "audio/audio.json" })
        {
            string path = Path.Combine(folder, json);
            if (File.Exists(path))
            {
                try
                {
                    System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path));
                }
                catch (System.Text.Json.JsonException e)
                {
                    findings.Add(Finding.Error("validate.json", $"{json}: {e.Message}"));
                }
            }
        }

        return (findings, stats);
    }
}
