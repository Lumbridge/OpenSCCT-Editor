using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Session;

/// <summary>A copy of a project that background work can read while the user keeps editing the live one.</summary>
public static class ProjectSnapshot
{
    /// <summary>Copies everything an edit can change (objects, LevelInfo, settings, layers); workspace, baked world
    /// and extra data are shared because no edit command replaces them while a job runs.</summary>
    public static MapProject Take(MapProject p) => new()
    {
        Id = p.Id,
        Title = p.Title,
        Source = p.Source,
        LevelInfo = p.LevelInfo.Clone(),
        MapSettings = new SortedDictionary<string, PropSet>(p.MapSettings.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()), StringComparer.Ordinal),
        MapInfo = p.MapInfo.Clone(),
        Brushes = p.Brushes.Select(b => b.Clone()).ToList(),
        Actors = p.Actors.Select(a => a.Clone()).ToList(),
        Ges = p.Ges.Select(g => g.Clone()).ToList(),
        Layers = p.Layers.Select(l => new Layer(l.Name) { Visible = l.Visible, Locked = l.Locked, Color = l.Color }).ToList(),
        Workspace = p.Workspace,
        Baked = p.Baked,
        Lighting = p.Lighting.Clone(),
        Identity = p.Identity.Clone(),
        Outfits = p.Outfits,
        Extra = p.Extra,
    };
}
