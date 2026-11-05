using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Assets;

/// <summary>
/// The drag data of an asset dragged out of a list: <see cref="AssetDrag"/> (kind, package-qualified path, display
/// name) carried through Godot's drag and drop. A list starts the drag with <see cref="Start"/> from its
/// <c>_GetDragData</c>; a drop target reads it back with <see cref="Read"/> in <c>_CanDropData</c> and
/// <c>_DropData</c> and takes the kinds it can use. Other drag data (outliner rows, prefabs) reads as null.
/// </summary>
public sealed partial class AssetDragData : RefCounted
{
    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public AssetDragData()
    {
    }

    private AssetDragData(AssetDrag drag) => Drag = drag;

    public AssetDrag? Drag { get; }

    /// <summary>The payload of <paramref name="drag"/>, and the drag preview <paramref name="from"/> shows under the
    /// pointer: the asset's thumbnail (when it has one) over its name.</summary>
    public static Variant Start(Control from, AssetDrag drag, Texture2D? thumbnail)
    {
        from.SetDragPreview(Preview(drag, thumbnail));
        return Variant.From(new AssetDragData(drag));
    }

    /// <summary>The asset in a drag's data, or null for any other drag.</summary>
    public static AssetDrag? Read(Variant data) =>
        data.VariantType == Variant.Type.Object && data.AsGodotObject() is AssetDragData { Drag: { } drag } ? drag : null;

    /// <summary>A small tile: the thumbnail and the name, a little below and right of the pointer so it never hides
    /// what the pointer is over (a mesh's ghost in a view).</summary>
    private static Control Preview(AssetDrag drag, Texture2D? thumbnail)
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

        box.AddChild(new Label { Text = drag.Name, HorizontalAlignment = HorizontalAlignment.Center });
        return holder;
    }
}
