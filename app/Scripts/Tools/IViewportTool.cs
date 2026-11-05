using Godot;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// A viewport tool (select, move, place actor, brush, GE...). The active tool gets every mouse and key event a view
/// does not use for camera navigation, draws its handles over the view, and may put options in the toolbar.
/// </summary>
public interface IViewportTool
{
    /// <summary>Stable id (the action is <c>tools.&lt;id&gt;</c>).</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>The tool's key (Q, W, E, R...).</summary>
    string Shortcut { get; }

    /// <summary>What the tool does, for its tooltip and the command palette, or null.</summary>
    string? Description => null;

    void Activate()
    {
    }

    void Deactivate()
    {
    }

    /// <summary>Handles an event in a view; returns true when it used it.</summary>
    bool Input(EditorViewport view, InputEvent e);

    /// <summary>Draws over a view (handles, rubber bands, pending points), in view pixels.</summary>
    void Draw(EditorViewport view, Control canvas)
    {
    }

    /// <summary>Fills the toolbar's tool-options area while the tool is active.</summary>
    void BuildOptions(HBoxContainer bar)
    {
    }

    /// <summary>True while a drag of the tool's is changing the map (views hide the selection's name tag meanwhile).</summary>
    bool IsDragging => false;

    /// <summary>Drops a pending operation (Escape); returns false when there was none.</summary>
    bool Cancel() => false;
}
