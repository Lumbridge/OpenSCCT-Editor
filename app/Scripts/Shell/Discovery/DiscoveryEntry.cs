namespace OpenScctEditor.Shell;

internal sealed record DiscoveryEntry(string Key, string Title, string Kind, string Path, string Description,
    string Keywords, Action Open, Action? Locate = null, Func<bool>? Enabled = null)
{
    public bool Available => Enabled?.Invoke() ?? true;
}

/// <summary>Language shared by all sources, kept independent of the rendering and asset catalogue.</summary>
public static class DiscoverySearch
{
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
        { "how", "do", "i", "to", "a", "an", "the", "my", "your", "please", "can", "you", "where", "is", "are" };
    public static (string Query, string? Kind) Parse(string raw)
    {
        string text = raw.Trim();
        string? kind = null;
        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            string prefix = text[..colon].ToLowerInvariant();
            kind = prefix switch
            {
                "tutorial" or "tutorials" => "Tutorial", "asset" or "assets" => "Asset",
                "class" or "classes" => "Class", "command" or "commands" or ">" => "Command",
                "panel" or "panels" => "Panel", "setting" or "settings" or "control" or "controls" => "Control",
                "placeable" or "placeables" => "Placeable", "prefab" or "prefabs" => "Prefab",
                "preset" or "presets" => "Preset", "effect" or "effects" => "Effect",
                "tool" or "tools" => "Tool",
                "object" or "objects" => "Object", _ => null,
            };
            if (kind is not null) text = text[(colon + 1)..];
        }
        if (text.StartsWith('>')) { kind = "Command"; text = text[1..]; }
        if (text.StartsWith("how ", StringComparison.OrdinalIgnoreCase)) kind ??= "Tutorial";
        if (kind == "Tutorial") text = text.Replace("loading screen", "loading", StringComparison.OrdinalIgnoreCase);
        var words = text.ToLowerInvariant().Replace("drop-off", "dropoff", StringComparison.Ordinal)
            .Replace("set up", "setup", StringComparison.Ordinal)
            .Split([' ', '?', ',', '.', '!'], StringSplitOptions.RemoveEmptyEntries).Where(w => !Noise.Contains(w)).ToList();
        if (kind is null && words.Count > 0 && words[0] is "tutorial" or "tutorials")
        { kind = "Tutorial"; words.RemoveAt(0); }
        // These verbs describe intent, not a specific editor control.
        if (kind == "Tutorial") words.RemoveAll(w => w is "setup" or "use" or "using" or "make" or "create" or "add");
        return (string.Join(' ', words), kind);
    }

    public static string Aliases(string text)
    {
        string s = text.ToLowerInvariant();
        return (s.Contains("computer") ? " laptop terminal hack" : "")
            + (s.Contains("drop zone") ? " dropoff drop-off extraction" : "")
            + (s.Contains("scale") ? " resize size bigger smaller" : "")
            + (s.Contains("identity") ? " level name title thumbnail loading screen" : "")
            + (s.Contains("face") || s.Contains("surface") ? " surface paint wall floor ceiling" : "")
            + (s.Contains("mesh") ? " model prop meshes" : "")
            + (s.Contains("bomb") ? " explosive plant defuse" : "")
            + (s.Contains("glass") || s.Contains("window") ? " glass window transparent breakable" : "");
    }

    public static int? Score(string query, string title, string keywords)
    {
        if (query.Length == 0) return 0;
        string haystack = title + " " + keywords;
        int score = 0;
        foreach (string word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (title.Equals(word, StringComparison.OrdinalIgnoreCase)) score += 2000;
            else if (title.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 700;
            else if (haystack.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 250;
            else if (Panels.TextMatch.Fuzzy(word, title) is { } fuzzy) score += Math.Min(100, fuzzy);
            else return null;
        }
        return score;
    }
}
