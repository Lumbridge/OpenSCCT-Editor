using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>What the viewports draw and let the user pick: hidden objects, hidden layers, objects outside the storey
/// shown (<see cref="StoreyFilter"/>) and other game modes' objects (when View/Game Mode shows one mode) are not drawn;
/// locked objects and locked layers are drawn but not pickable.</summary>
public static class ObjectFilter
{
    public static bool IsVisible(MapProject p, EditorMeta meta) => IsInWorld(p, meta) && !StoreyFilter.Hides(p, meta);

    /// <summary>True when an object is part of the world the views build and light (the CSG preview, the Lit Preview
    /// bake): as <see cref="IsVisible"/>, except that the storey filter leaves it in. The views cut that world to the
    /// storey as they draw it (<see cref="Render.StoreyClip"/>), so the storey shown keeps the light of the whole map.</summary>
    public static bool IsInWorld(MapProject p, EditorMeta meta) =>
        !meta.Hidden && (FindLayer(p, meta.Layer)?.Visible ?? true) && !Gameplay.GameModeFilter.Hides(p, meta);

    public static bool IsPickable(MapProject p, EditorMeta meta) =>
        IsVisible(p, meta) && !meta.Locked && !(FindLayer(p, meta.Layer)?.Locked ?? false);

    public static bool IsPickable(MapProject p, ObjectRef r) => p.MetaOf(r) is { } m && IsPickable(p, m);

    /// <summary>Every visible, unlocked object.</summary>
    public static IEnumerable<ObjectRef> Pickable(MapProject p) =>
        p.Brushes.Where(b => IsPickable(p, b.Editor)).Select(b => ObjectRef.Brush(b.Id))
            .Concat(p.Actors.Where(a => IsPickable(p, a.Editor)).Select(a => ObjectRef.Actor(a.Id)))
            .Concat(p.Ges.Where(g => IsPickable(p, g.Editor)).Select(g => ObjectRef.Ge(g.Id)));

    /// <summary>The layer of an object; "" is the default layer (named "Default" in new maps).</summary>
    private static Layer? FindLayer(MapProject p, string name)
    {
        if (name.Length == 0)
        {
            name = "Default";
        }

        foreach (Layer l in p.Layers)
        {
            if (string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return l;
            }
        }

        return null;
    }
}
