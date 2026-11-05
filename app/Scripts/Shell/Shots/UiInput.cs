using Godot;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell.Shots;

/// <summary>
/// Synthetic mouse and keyboard input for screenshots and scripts. Events go through <see cref="Input.ParseInputEvent"/>,
/// as the OS's own do, so code that polls <see cref="Input"/> (fly mode's WASD) sees them too; the real pointer and
/// keyboard are never touched. Positions are in the main window's canvas coordinates (what <see cref="UiTree"/>
/// reports); they are mapped to window pixels here, so they stay right at any UI scale.
/// </summary>
public static class UiInput
{
    private static Vector2 s_window;
    private static MouseButtonMask s_held;
    private static bool s_entered;

    /// <summary>Moves the pointer to <paramref name="at"/> in <paramref name="steps"/> motion events.</summary>
    public static void Move(Window root, Vector2 at, int steps = 1)
    {
        Vector2 from = EditorPointer.LastPosition;
        for (int i = 1; i <= Math.Max(1, steps); i++)
        {
            Vector2 p = from.Lerp(at, i / (float)Math.Max(1, steps));
            Vector2 w = ToWindow(root, p);
            // Popup menus ignore motion without velocity (they treat it as the pointer resting while keys scroll).
            Vector2 relative = w - s_window;
            var e = new InputEventMouseMotion
            {
                Position = w, GlobalPosition = w, Relative = relative, ScreenRelative = relative, Velocity = relative * 60,
                ScreenVelocity = relative * 60, ButtonMask = s_held,
            };
            Send(root, p, w, e);
        }
    }

    public static void Button(Window root, Vector2 at, MouseButton button, bool pressed, KeyModifierMask mods = 0, bool doubleClick = false)
    {
        Move(root, at);
        MouseButtonMask mask = MaskOf(button);
        s_held = pressed ? s_held | mask : s_held & ~mask;
        Vector2 w = ToWindow(root, at);
        var e = new InputEventMouseButton
        {
            Position = w, GlobalPosition = w, ButtonIndex = button, Pressed = pressed, ButtonMask = s_held, DoubleClick = doubleClick,
            Factor = 1,
        };
        SetModifiers(e, mods);
        Send(root, at, w, e);
    }

    public static void Click(Window root, Vector2 at, MouseButton button = MouseButton.Left, KeyModifierMask mods = 0)
    {
        Button(root, at, button, true, mods);
        Button(root, at, button, false, mods);
    }

    /// <summary>Presses and releases a key chord such as "Ctrl+Shift+P" or "F1".</summary>
    public static bool Key(string chord)
    {
        if (KeyChord.Parse(chord) is not { } parsed)
        {
            return false;
        }

        InputEventKey down = parsed.ToEvent();
        down.PhysicalKeycode = down.Keycode;
        down.Pressed = true;
        Input.ParseInputEvent(down);
        var up = (InputEventKey)down.Duplicate();
        up.Pressed = false;
        Input.ParseInputEvent(up);
        Input.FlushBufferedEvents();
        return true;
    }

    /// <summary>Holds or releases one key (fly mode's WASD).</summary>
    public static void Hold(Key key, bool pressed)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed });
        Input.FlushBufferedEvents();
    }

    /// <summary>Types text into whatever has the keyboard focus.</summary>
    public static void Type(string text)
    {
        foreach (char c in text)
        {
            Input.ParseInputEvent(new InputEventKey { Unicode = c, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Unicode = c, Pressed = false });
        }

        Input.FlushBufferedEvents();
    }

    /// <summary>Releases every mouse button still held (after a capture of a drag in progress).</summary>
    public static void ReleaseAll(Window root)
    {
        foreach (MouseButton b in new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle })
        {
            if ((s_held & MaskOf(b)) != 0)
            {
                Button(root, EditorPointer.LastPosition, b, false);
            }
        }
    }

    /// <summary>Canvas coordinates to window pixels (the UI scale and any canvas transform).</summary>
    public static Vector2 ToWindow(Window root, Vector2 canvas) => root.GetFinalTransform() * canvas;

    private static void Send(Window root, Vector2 canvas, Vector2 window, InputEvent e)
    {
        // The real pointer never enters an off-screen window, and Godot ignores hover for a window it thinks the
        // pointer is outside of. Once is enough: nothing tells it the pointer left.
        if (!s_entered)
        {
            s_entered = true;
            root.NotifyMouseEntered();
        }

        s_window = window;
        EditorPointer.LastPosition = canvas;
        Input.ParseInputEvent(e);
        Input.FlushBufferedEvents();
    }

    private static void SetModifiers(InputEventWithModifiers e, KeyModifierMask mods)
    {
        e.CtrlPressed = (mods & KeyModifierMask.MaskCtrl) != 0;
        e.ShiftPressed = (mods & KeyModifierMask.MaskShift) != 0;
        e.AltPressed = (mods & KeyModifierMask.MaskAlt) != 0;
    }

    private static MouseButtonMask MaskOf(MouseButton b) => b switch
    {
        MouseButton.Left => MouseButtonMask.Left,
        MouseButton.Right => MouseButtonMask.Right,
        MouseButton.Middle => MouseButtonMask.Middle,
        _ => 0,
    };
}
