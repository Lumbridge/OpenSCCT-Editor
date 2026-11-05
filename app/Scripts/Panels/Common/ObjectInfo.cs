using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Panels;

/// <summary>One placed object as the lists show it. <see cref="Group"/> is the UE2 Group field (RE+ groups) without
/// its "None" entry: comma-separated group names, or empty.</summary>
internal readonly record struct ObjectRow(ObjectRef Ref, EditorMeta Meta, string Class, string Tag, string Event, string Group)
{
    /// <summary>The first group the object is in, or empty.</summary>
    public string PrimaryGroup => Group.Length == 0 ? "" : Group.Split(',')[0];

    /// <summary>What the search filters look in: id, class, tag, event, group, layer and folder.</summary>
    public string SearchText => $"{Ref.Id} {Class} {Tag} {Event} {Group} {Meta.Layer} {Meta.Folder}";
}

/// <summary>Names and groupings of placed objects, shared by the outliner, layers, Map Check and the palette.</summary>
internal static class ObjectInfo
{
    public const string BrushClass = "Brush";

    /// <summary>Every object in map order: brushes (CSG order), actors (level order), then GEs.</summary>
    public static IEnumerable<ObjectRow> All(MapProject p) =>
        p.Brushes.Select(Row).Concat(p.Actors.Select(Row)).Concat(p.Ges.Select(Row));

    public static ObjectRow Row(Brush b) =>
        new(ObjectRef.Brush(b.Id), b.Editor, BrushClass, b.Props.GetString("Tag") ?? "", "", Groups(b.Props.GetString("Group")));

    public static ObjectRow Row(Actor a) =>
        new(ObjectRef.Actor(a.Id), a.Editor, a.ShortClass, a.Tag, a.Props.GetString("Event") ?? "", Groups(a.Props.GetString("Group")));

    public static ObjectRow Row(Ge g) => new(ObjectRef.Ge(g.Id), g.Editor, GeClass(g.Kind), "", "", "");

    /// <summary>What the lists call a GE of a kind: "GE Ladder", "GE Pipe"; the short kinds stay as the game spells
    /// them ("GE LG", "GE HOH").</summary>
    public static string GeClass(string kind) =>
        "GE " + (kind.Length <= 3 ? kind : char.ToUpperInvariant(kind[0]) + kind[1..].ToLowerInvariant());

    /// <summary>A UE2 Group value ("None,Zone01,Doors") as its group names ("Zone01,Doors").</summary>
    private static string Groups(string? value) => value is null
        ? ""
        : string.Join(',', value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(g => !string.Equals(g, "None", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Every object by reference, built in one pass (for lookups over many objects).</summary>
    public static Dictionary<ObjectRef, ObjectRow> Lookup(MapProject p)
    {
        var rows = new Dictionary<ObjectRef, ObjectRow>();
        foreach (ObjectRow r in All(p))
        {
            rows.TryAdd(r.Ref, r);
        }

        return rows;
    }

    public static ObjectRow? Find(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Brush when p.FindBrush(r.Id) is { } b => Row(b),
        ObjectKind.Actor when p.FindActor(r.Id) is { } a => Row(a),
        ObjectKind.Ge when p.FindGe(r.Id) is { } g => Row(g),
        _ => null,
    };

    /// <summary>"PlayerStart3 (PlayerStart)", or the bare id when the object is gone.</summary>
    public static string Describe(MapProject p, ObjectRef r) => Find(p, r) is { } row ? $"{r.Id} ({row.Class})" : r.Id;

    /// <summary>The default layer. Objects on it store an empty layer name; it cannot be renamed or deleted (the same
    /// rule as the core's <see cref="LayerEdits"/>, the views and the scripting API).</summary>
    public const string DefaultLayer = "Default";

    /// <summary>The name of the layer an object with this meta is on.</summary>
    public static string LayerName(EditorMeta meta) => meta.Layer.Length > 0 ? meta.Layer : DefaultLayer;

    public static bool IsDefaultLayer(string name) => LayerEdits.IsDefault(name);

    /// <summary>The project's layer names, with the default layer first when the list lacks it.</summary>
    public static List<string> LayerNames(MapProject p)
    {
        var names = p.Layers.Select(l => l.Name).ToList();
        if (!names.Exists(IsDefaultLayer))
        {
            names.Insert(0, DefaultLayer);
        }

        return names;
    }

    public static Layer? FindLayer(MapProject p, string name) =>
        p.Layers.Find(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The meta value that puts an object on <paramref name="layer"/> (empty for the default layer).</summary>
    public static string LayerValue(string layer) => LayerEdits.Stored(layer);

    /// <summary>True when the object is hidden by itself or by its layer.</summary>
    public static bool IsHidden(MapProject p, EditorMeta meta) => meta.Hidden || FindLayer(p, LayerName(meta)) is { Visible: false };
}
