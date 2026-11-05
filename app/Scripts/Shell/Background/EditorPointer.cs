using Godot;

namespace OpenScctEditor.Shell.Background;

/// <summary>
/// The one place the editor captures, warps or reads the mouse pointer. Detached (background mode) it never touches
/// the real pointer: a requested capture is only remembered, warps are dropped, and the pointer position is the last
/// synthetic event's, because Godot answers mouse position queries for the main window from the real OS cursor.
/// </summary>
public static class EditorPointer
{
    private static Input.MouseModeEnum s_mode = Input.MouseModeEnum.Visible;

    /// <summary>True in background mode.</summary>
    public static bool Detached { get; set; }

    /// <summary>Where the last synthetic mouse event went, in the main window's canvas coordinates.</summary>
    public static Vector2 LastPosition { get; set; }

    public static Input.MouseModeEnum Mode
    {
        get => Detached ? s_mode : Input.MouseMode;
        set
        {
            s_mode = value;
            if (!Detached)
            {
                Input.MouseMode = value;
            }
        }
    }

    /// <summary>Moves the pointer to <paramref name="to"/>, in <paramref name="item"/>'s canvas coordinates (what
    /// <see cref="Global"/> returns). Goes through the viewport so the UI scale is applied: Input.WarpMouse takes
    /// window pixels, which differ from canvas coordinates whenever the UI scale is not 100%.</summary>
    public static void Warp(CanvasItem item, Vector2 to)
    {
        if (!Detached)
        {
            item.GetViewport().WarpMouse(item.GetCanvasTransform() * to);
        }
    }

    /// <summary>The pointer in <paramref name="item"/>'s local coordinates.</summary>
    public static Vector2 Local(CanvasItem item) =>
        Detached ? item.GetGlobalTransformWithCanvas().AffineInverse() * LastPosition : item.GetLocalMousePosition();

    /// <summary>The pointer in the main window's canvas coordinates.</summary>
    public static Vector2 Global(CanvasItem item) => Detached ? LastPosition : item.GetGlobalMousePosition();
}
