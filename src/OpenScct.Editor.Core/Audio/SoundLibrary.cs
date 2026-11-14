using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets;

namespace OpenScct.Editor.Core.Audio;

public sealed record SoundSource(string Path, JsonObject Definition, string? File)
{
    public double Gain => Definition["volume"] is JsonValue value
        ? value.TryGetValue<double>(out double d) ? d : value.TryGetValue<float>(out float f) ? f : value.TryGetValue<int>(out int i) ? i : 1
        : 1;
}

/// <summary>Resolves both audio files and composite sounds without losing their original sound definitions.</summary>
public sealed class SoundLibrary(AssetCatalog catalog, string? contentRoot)
{
    private readonly Dictionary<string, JsonObject> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _maps;

    private JsonObject Table(string file)
    {
        if (_tables.TryGetValue(file, out var table)) return table;
        table = File.Exists(file) ? JsonNode.Parse(File.ReadAllBytes(file))?["sounds"] as JsonObject ?? new() : new();
        _tables[file] = table;
        return table;
    }

    private static JsonObject? Find(JsonObject table, string path) => table.FirstOrDefault(p =>
        p.Key.Equals(path, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;

    public SoundSource? Resolve(string path)
    {
        AssetEntry? entry = catalog.Find(AssetKind.Sound, path);
        if (entry is null) return null;
        JsonObject definition = new();
        if (contentRoot is not null)
        {
            foreach (string file in new[] { "audio.json", "sidearms.json", "weapons.json" })
                if (Find(Table(Path.Combine(contentRoot, "shared", "audio", file)), entry.Path) is { } shared)
                    definition = shared.DeepClone().AsObject();
            _maps ??= ContentLibrary.For(contentRoot).MapDirs().ToDictionary(dir => Path.GetFileName(dir), dir => dir, StringComparer.OrdinalIgnoreCase);
            if (_maps.TryGetValue(entry.Origin, out string? folder) && Find(Table(Path.Combine(folder, "audio", "audio.json")), entry.Path) is { } own)
                foreach (var p in own) definition[p.Key] = p.Value?.DeepClone();
        }
        definition["kind"] ??= entry.SoundKind is "random" or "sequence" or "switch" ? entry.SoundKind : "wave";
        definition["flags"] ??= 0;
        definition["volume"] ??= 1f;
        definition["radius"] ??= 0f;
        definition["loop"] ??= false;
        definition["positional"] ??= true;
        definition["duration"] ??= entry.Duration;
        return new SoundSource(entry.Path, definition, entry.File);
    }

    public IEnumerable<SoundSource> Leaves(string path)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<SoundSource> Walk(string next)
        {
            if (!visited.Add(next) || Resolve(next) is not { } source) yield break;
            if (source.File is not null) yield return source;
            else if (source.Definition["entries"] is JsonArray entries)
                foreach (var child in entries.OfType<JsonObject>())
                    if (child["sound"]?.GetValue<string>() is { } name)
                        foreach (var leaf in Walk(name)) yield return leaf;
        }
        return Walk(path);
    }
}
