using OpenScct.Content;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Project;

/// <summary>
/// The project's <c>outfits.json</c> (<c>osmap.outfits/1</c>): SVMap 1.2's <c>render/outfits.json</c> with paths
/// relative to the project folder (the files live under <c>assets/outfits/</c>). The compiler copies the files into the
/// map's <c>render/</c> and rewrites the paths.
/// </summary>
public static class OutfitsJson
{
    public const string FormatId = "osmap.outfits/1";

    public static SvJsonObject ToJson(MapOutfits outfits)
    {
        var root = new SvJsonObject();
        root.Add("format", SvJsonNode.String(FormatId));
        foreach (KeyValuePair<string, SvJsonNode> kv in OutfitsDoc.ToJson(outfits).Where(kv => kv.Key != "format"))
        {
            root.Add(kv.Key, kv.Value);
        }

        return root;
    }

    /// <summary>Reads the project file; throws <see cref="SvMapFormatException"/> for a bad one.</summary>
    public static MapOutfits FromJson(SvJsonNode node, string file = ProjectStore.OutfitsFile)
    {
        if (node is not SvJsonObject o)
        {
            throw new SvMapFormatException(file, "expected an object");
        }

        // The same members as the map file under the map file's format id, so one reader checks both.
        var doc = new SvJsonObject();
        doc.Add("format", SvJsonNode.String(OutfitsDoc.FormatId));
        foreach (KeyValuePair<string, SvJsonNode> kv in o.Where(kv => kv.Key != "format"))
        {
            doc.Add(kv.Key, kv.Value);
        }

        return OutfitsDoc.FromJson(doc, file);
    }
}
