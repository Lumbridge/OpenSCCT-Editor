using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Commands;

/// <summary>
/// A deep copy of everything in a project except its objects: id and title, source, LevelInfo, map settings, map info,
/// map identity, layers, prefab instances, workspace, baked world, lighting settings, outfits and extra data. Small, so
/// project-level commands snapshot all of it and undo and redo are always exact, whatever the edit touched.
/// </summary>
public sealed class ProjectState
{
    private string _id = "";
    private string _title = "";
    private ProjectSource _source = new();
    private PropSet _levelInfo = new();
    private SortedDictionary<string, PropSet> _mapSettings = new(StringComparer.Ordinal);
    private PropSet _mapInfo = new();
    private ProjectIdentity _identity = new();
    private List<Layer> _layers = new();
    private List<PrefabInstance> _prefabs = new();
    private Workspace _workspace = new();
    private BakedWorld? _baked;
    private LightingSettings _lighting = new();
    private OpenScct.Content.MapOutfits _outfits = OpenScct.Content.MapOutfits.None;
    private SvJsonObject _extra = new();

    private ProjectState()
    {
    }

    /// <summary>The storey the views showed (<see cref="Workspace.ShownStorey"/>).</summary>
    public double? ShownStorey => _workspace.ShownStorey;

    public static ProjectState Capture(MapProject p) => new()
    {
        _id = p.Id,
        _title = p.Title,
        _source = p.Source.Clone(),
        _levelInfo = p.LevelInfo.Clone(),
        _mapSettings = CopySettings(p.MapSettings),
        _mapInfo = p.MapInfo.Clone(),
        _identity = p.Identity.Clone(),
        _layers = p.Layers.Select(l => l.Clone()).ToList(),
        _prefabs = p.Prefabs.Select(i => i.Clone()).ToList(),
        _workspace = p.Workspace.Clone(),
        _baked = p.Baked?.Clone(),
        _lighting = p.Lighting.Clone(),
        _outfits = p.Outfits,
        _extra = JsonCopy.Object(p.Extra),
    };

    /// <summary>Puts this state back. Every part is a fresh copy, so the state can be restored again later.</summary>
    public void Restore(MapProject p)
    {
        p.Id = _id;
        p.Title = _title;
        p.Source = _source.Clone();
        p.LevelInfo = _levelInfo.Clone();
        p.MapSettings = CopySettings(_mapSettings);
        p.MapInfo = _mapInfo.Clone();
        p.Identity = _identity.Clone();
        p.Layers = _layers.Select(l => l.Clone()).ToList();
        p.Prefabs = _prefabs.Select(i => i.Clone()).ToList();
        p.Workspace = _workspace.Clone();
        p.Baked = _baked?.Clone();
        p.Lighting = _lighting.Clone();
        p.Outfits = _outfits;
        p.Extra = JsonCopy.Object(_extra);
    }

    private static SortedDictionary<string, PropSet> CopySettings(SortedDictionary<string, PropSet> s) =>
        new(s.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()), StringComparer.Ordinal);
}

/// <summary>Deep copies of JSON trees (objects and arrays are copied; leaves are immutable and shared).</summary>
public static class JsonCopy
{
    public static SvJsonObject Object(SvJsonObject o)
    {
        var copy = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            copy.Add(kv.Key, Node(kv.Value));
        }

        return copy;
    }

    public static SvJsonNode Node(SvJsonNode n) => n switch
    {
        SvJsonObject o => Object(o),
        SvJsonArray a => new SvJsonArray(a.Select(Node)),
        _ => n,
    };
}
