using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Panels;

/// <summary>Enum value names for the inspector's dropdowns: from the class catalog when it knows them, else read once
/// from the <c>enums</c> table of the content's <c>shared/classes.json</c>.</summary>
internal static class EnumTable
{
    private static Dictionary<string, IReadOnlyList<string>>? s_fromFile;

    public static IReadOnlyList<string>? Values(string? enumName)
    {
        if (string.IsNullOrEmpty(enumName))
        {
            return null;
        }

        if (EditorServices.Classes?.EnumValues(enumName) is { Count: > 0 } fromCatalog)
        {
            return fromCatalog;
        }

        Dictionary<string, IReadOnlyList<string>> table = s_fromFile ??= Load();
        if (table.TryGetValue(enumName, out IReadOnlyList<string>? values))
        {
            return values;
        }

        // Accept a short name (EPhysics) for a full one (Engine.Actor.EPhysics).
        string suffix = "." + enumName;
        return table.FirstOrDefault(kv => kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static Dictionary<string, IReadOnlyList<string>> Load()
    {
        var table = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        string? root = EditorServices.ContentRoot;
        string? file = root is null ? null : Path.Combine(root, "shared", "classes.json");
        if (file is null || !File.Exists(file))
        {
            return table;
        }

        try
        {
            if (SvJson.Parse(File.ReadAllBytes(file), file) is SvJsonObject doc && doc["enums"] is SvJsonObject enums)
            {
                foreach (KeyValuePair<string, SvJsonNode> e in enums)
                {
                    if (e.Value is SvJsonArray names)
                    {
                        table[e.Key] = names.OfType<SvJsonString>().Select(s => s.Value).ToArray();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SvMapFormatException or UnauthorizedAccessException)
        {
            Godot.GD.PushWarning($"Enum names unavailable: {ex.Message}");
        }

        return table;
    }
}
