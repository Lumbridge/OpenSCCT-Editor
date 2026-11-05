using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Panels;

/// <summary>The panels' edits of editor-only project data (layers, bookmarks, play options, movement limits). Thin
/// wrappers over the core commands, so the panels, the CLI and scripts make the same undo steps.</summary>
internal static class PanelCommands
{
    public static IEditCommand Layers(MapProject p, string name, Action<List<Layer>> edit) => LayerEdits.Layers(name, edit);

    public static IEditCommand Bookmarks(MapProject p, string name, Action<List<Bookmark>> edit) => WorkspaceEdits.Bookmarks(name, edit);

    public static IEditCommand Play(MapProject p, string name, Action<PlayOptions> edit) => WorkspaceEdits.Play(name, edit);

    public static IEditCommand Movement(MapProject p, string name, Action<MovementLimits> edit) => WorkspaceEdits.Movement(name, edit);

    /// <summary>Flips a layer's visibility or lock. A layer the objects name but the list lacks is added first.</summary>
    public static IEditCommand ToggleLayer(MapProject p, string name, bool visibility) => LayerEdits.Toggle(name, visibility);

    /// <summary>Renames a layer and moves its objects with it, as one step.</summary>
    public static IEditCommand RenameLayer(MapProject p, string oldName, string newName) => LayerEdits.Rename(p, oldName, newName);

    /// <summary>Deletes a layer; its objects move to the default layer.</summary>
    public static IEditCommand DeleteLayer(MapProject p, string name) => LayerEdits.Delete(p, name);

    /// <summary>A layer name no layer uses yet: "Layer 1", "Layer 2"...</summary>
    public static string NewLayerName(MapProject p, string stem = "Layer") => LayerEdits.NewName(p, stem);
}
