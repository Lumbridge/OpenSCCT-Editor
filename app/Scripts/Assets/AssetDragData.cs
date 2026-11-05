using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Assets;

/// <summary>
/// The drag data of an asset dragged out of a list: <see cref="AssetDrag"/> (kind, package-qualified path, display
/// name) carried through Godot's drag and drop. A list starts the drag with <see cref="Start"/> from its
/// <c>_GetDragData</c>; a drop target reads it back with <see cref="Read"/> in <c>_CanDropData</c> and
/// <c>_DropData</c> and takes the kinds it can use. A tile of the placeables palette travels the same way as a
/// <see cref="Placeable"/> (<see cref="StartPlaceable"/>, <see cref="ReadPlaceable"/>), so the views place it through the
/// same drop pipeline. Other drag data (outliner rows, prefabs) reads as null.
/// </summary>
public sealed partial class AssetDragData : RefCounted
{
    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public AssetDragData()
    {
    }

    private AssetDragData(AssetDrag drag) => Drag = drag;

    private AssetDragData(Placeable placeable) => Placeable = placeable;

    public AssetDrag? Drag { get; }

    /// <summary>The palette placeable being dragged, or null for an asset.</summary>
    public Placeable? Placeable { get; }

    /// <summary>The payload of <paramref name="drag"/>, and the drag preview <paramref name="from"/> shows under the
    /// pointer: the asset's thumbnail (when it has one) over its name.</summary>
    public static Variant Start(Control from, AssetDrag drag, Texture2D? thumbnail)
    {
        from.SetDragPreview(Preview(drag, thumbnail));
        return Variant.From(new AssetDragData(drag));
    }

    /// <summary>The payload of a palette tile, and the drag preview <paramref name="from"/> shows: its picture over its
    /// name.</summary>
    public static Variant StartPlaceable(Control from, Placeable placeable, Texture2D? picture)
    {
        from.SetDragPreview(Preview(placeable.Title, picture));
        return Variant.From(new AssetDragData(placeable));
    }

    /// <summary>The asset in a drag's data, or null for any other drag.</summary>
    public static AssetDrag? Read(Variant data) =>
        data.VariantType == Variant.Type.Object && data.AsGodotObject() is AssetDragData { Drag: { } drag } ? drag : null;

    /// <summary>The palette placeable in a drag's data, or null for any other drag.</summary>
    public static Placeable? ReadPlaceable(Variant data) =>
        data.VariantType == Variant.Type.Object && data.AsGodotObject() is AssetDragData { Placeable: { } placeable } ? placeable : null;

    private static Control Preview(AssetDrag drag, Texture2D? thumbnail) => Preview(drag.Name, thumbnail);

    /// <summary>A small tile: the thumbnail and the name, a little below and right of the pointer so it never hides
    /// what the pointer is over (a mesh's ghost in a view).</summary>
    private static Control Preview(string name, Texture2D? thumbnail)
    {
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        var panel = new PanelContainer
        {
            Position = new Vector2(EditorTheme.SpaceL, EditorTheme.SpaceL),
            Modulate = new Color(1, 1, 1, 0.85f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride("panel", EditorTheme.Box(new Color(EditorTheme.Panel, 0.92f), border: 1, padX: EditorTheme.SpaceXs,
            padY: EditorTheme.SpaceXs));
        holder.AddChild(panel);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        panel.AddChild(box);
        if (thumbnail is not null)
        {
            box.AddChild(new TextureRect
            {
                Texture = thumbnail,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(AssetThumbnails.Size, AssetThumbnails.Size),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }

        box.AddChild(new Label { Text = name, HorizontalAlignment = HorizontalAlignment.Center });
        return holder;
    }
}
