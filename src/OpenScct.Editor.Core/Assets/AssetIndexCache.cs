using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// The on-disk cache of indexed sources: one small JSON file per source named <c>&lt;source&gt;-&lt;digest&gt;.json</c>,
/// files stored relative to the source folder. A digest that no longer matches means a new file; old ones of that
/// source are removed. A broken cache file is ignored and rebuilt. Glosses are not stored.
/// </summary>
public static class AssetIndexCache
{
    private const int Version = 2;

    /// <summary>The cached index of a source, or a fresh one (written to the cache). Cached is true when read from disk.</summary>
    public static (AssetSource Source, bool Cached) Get(string cacheDir, string name, string digest, string baseDir, Func<AssetSource> index)
    {
        string file = Path.Combine(cacheDir, $"{name}-{digest}.json");
        if (TryRead(file, name, digest, baseDir) is { } cached)
        {
            return (cached, true);
        }

        AssetSource fresh = index();
        try
        {
            Directory.CreateDirectory(cacheDir);
            foreach (string old in Directory.EnumerateFiles(cacheDir, $"{name}-*.json"))
            {
                File.Delete(old);
            }

            string tmp = file + ".tmp";
            File.WriteAllText(tmp, Write(fresh, baseDir).ToJsonString());
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No cache this time; the index itself is fine.
        }

        return (fresh, false);
    }

    private static AssetSource? TryRead(string file, string name, string digest, string baseDir)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(file)) is not JsonObject root || root["v"]?.GetValue<int>() != Version ||
                root["entries"] is not JsonArray list)
            {
                return null;
            }

            var entries = new List<AssetEntry>(list.Count);
            foreach (JsonNode? n in list)
            {
                if (n is JsonObject o)
                {
                    entries.Add(Read(o, name, baseDir));
                }
            }

            return new AssetSource(name, digest, entries);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static JsonObject Write(AssetSource s, string baseDir)
    {
        var list = new JsonArray();
        foreach (AssetEntry e in s.Entries)
        {
            var o = new JsonObject { ["k"] = (int)e.Kind, ["p"] = e.Path };
            Put(o, "f", Rel(e.File, baseDir));
            Put(o, "t", Rel(e.Texture, baseDir));
            if (e.Width > 0)
            {
                o["w"] = e.Width;
                o["h"] = e.Height;
            }

            if (e.Kind == AssetKind.Mesh)
            {
                o["m"] = e.Mesh;
                o["tri"] = e.Triangles;
                o["b"] = new JsonArray(e.BoundsMin.X, e.BoundsMin.Y, e.BoundsMin.Z, e.BoundsMax.X, e.BoundsMax.Y, e.BoundsMax.Z);
                o["mats"] = new JsonArray(e.Materials.Select(x => (JsonNode)x).ToArray());
            }

            if (e.Duration > 0)
            {
                o["d"] = e.Duration;
            }

            Put(o, "sk", e.SoundKind);
            if (e.UsedIn.Count > 0)
            {
                var used = new JsonObject();
                foreach ((string map, int n) in e.UsedIn)
                {
                    used[map] = n;
                }

                o["u"] = used;
            }

            list.Add(o);
        }

        return new JsonObject { ["v"] = Version, ["source"] = s.Name, ["digest"] = s.Digest, ["entries"] = list };
    }

    private static AssetEntry Read(JsonObject o, string origin, string baseDir)
    {
        double[] b = o["b"] is JsonArray ba ? ba.Select(x => x!.GetValue<double>()).ToArray() : new double[6];
        return new AssetEntry
        {
            Kind = (AssetKind)o["k"]!.GetValue<int>(),
            Path = o["p"]!.GetValue<string>(),
            Origin = origin,
            File = Abs(o["f"], baseDir),
            Texture = Abs(o["t"], baseDir),
            Width = o["w"]?.GetValue<int>() ?? 0,
            Height = o["h"]?.GetValue<int>() ?? 0,
            Mesh = o["m"]?.GetValue<int>() ?? -1,
            Triangles = o["tri"]?.GetValue<int>() ?? 0,
            BoundsMin = new Vec3(b[0], b[1], b[2]),
            BoundsMax = new Vec3(b[3], b[4], b[5]),
            Materials = o["mats"] is JsonArray mats ? mats.Select(x => x!.GetValue<string>()).ToList() : [],
            Duration = o["d"]?.GetValue<double>() ?? 0,
            SoundKind = o["sk"]?.GetValue<string>(),
            // Glosses are made on read, so a new word in the user's list applies at once.
            Gloss = AssetGlossary.Gloss(o["p"]!.GetValue<string>()),
            UsedIn = o["u"] is JsonObject u ? u.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(),
        };
    }

    private static void Put(JsonObject o, string key, string? value)
    {
        if (value is not null)
        {
            o[key] = value;
        }
    }

    private static string? Rel(string? file, string baseDir) => file is null ? null : Path.GetRelativePath(baseDir, file).Replace('\\', '/');

    private static string? Abs(JsonNode? rel, string baseDir) =>
        rel?.GetValue<string>() is { } r ? Path.GetFullPath(Path.Combine(baseDir, r)) : null;
}
