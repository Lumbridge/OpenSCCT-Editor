using System.Globalization;
using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell;

/// <summary>
/// The size of the whole UI. Godot draws in physical pixels, so on a 125% or 150% Windows display an unscaled editor
/// is tiny; by default the UI follows the screen's DPI, and View > UI Scale picks a fixed size instead (kept in the
/// settings). Text and icons are drawn again at the new size, so they stay sharp; the 3D views render at the
/// screen's full resolution (<see cref="Viewports.EditorViewport"/>). Background runs use 100% unless a screenshot
/// script asks for another scale, so their pictures do not depend on the machine.
/// </summary>
[ShellPart(Order = 1)]
public sealed class UiScale : IShellPart
{
    private const string Section = "ui";
    private const string Key = "scale_percent";
    private static readonly Vector2I BaseSize = new(1600, 900);
    private static readonly Vector2I MinimumSize = new(1200, 700);
    private static readonly int[] s_choices = [100, 125, 150, 175, 200, 250];

    private Window _root = null!;

    /// <summary>The chosen scale in percent; 0 follows the screen.</summary>
    public static int Setting
    {
        get => AppSettings.GetInt(Section, Key, 0);
        private set => AppSettings.Set(Section, Key, value);
    }

    public void Attach(MainWindow window, EditorContext context)
    {
        _root = window.GetTree().Root;
        Apply(first: true);
        context.AddAction(new EditorAction("view.uiScale.auto", "Match the Screen", "View/UI Scale", () => Choose(0))
        {
            // The submenu sits in the View menu's 400s; "Match the Screen" is set apart from the fixed sizes.
            Order = 400, RadioGroup = "view.uiScale", IsChecked = () => Setting == 0,
            Description = "Scale the UI by the screen's DPI setting",
        });
        foreach (int percent in s_choices)
        {
            int p = percent;
            context.AddAction(new EditorAction("view.uiScale." + p.ToString(CultureInfo.InvariantCulture), $"{p}%", "View/UI Scale", () => Choose(p))
            {
                Order = 500 + p / 10, RadioGroup = "view.uiScale", IsChecked = () => Setting == p,
            });
        }
    }

    /// <summary>The scale the UI should have now.</summary>
    public static float Wanted()
    {
        if (Setting > 0)
        {
            return Setting / 100f;
        }

        if (BackgroundMode.Active || DisplayServer.GetName() == "headless")
        {
            return 1;
        }

        int screen = DisplayServer.WindowGetCurrentScreen();
        // Windows reports the DPI (120 at 125%); macOS and Wayland report a scale instead.
        float scale = Math.Max(DisplayServer.ScreenGetDpi(screen) / 96f, DisplayServer.ScreenGetScale(screen));
        return Math.Clamp(MathF.Round(scale * 4) / 4, 1, 3);
    }

    private void Choose(int percent)
    {
        Setting = percent;
        Apply(first: false);
    }

    /// <summary>Sets the UI scale of <paramref name="root"/> and the smallest window that still fits the docks at that
    /// scale, never more than the screen's free area (the minimum is in screen pixels, so a big scale on a small
    /// screen would otherwise push the window past the taskbar).</summary>
    public static void Set(Window root, float scale)
    {
        root.ContentScaleFactor = scale;
        var wanted = (Vector2I)((Vector2)MinimumSize * scale);
        Rect2I usable = DisplayServer.GetName() == "headless" || BackgroundMode.Active
            ? new Rect2I(Vector2I.Zero, wanted)
            : DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        root.MinSize = new Vector2I(Math.Min(wanted.X, usable.Size.X), Math.Min(wanted.Y, usable.Size.Y));
    }

    private void Apply(bool first)
    {
        float old = _root.ContentScaleFactor;
        float scale = Wanted();
        Set(_root, scale);
        if (BackgroundMode.Active || _root.Mode != Window.ModeEnum.Windowed)
        {
            return;
        }

        // Keep the same amount of editor on screen: grow or shrink the window with the scale. A window that would
        // not fit on the screen (title bar and taskbar included) is maximised instead.
        var size = (Vector2I)(first ? (Vector2)BaseSize * scale : (Vector2)_root.Size * (scale / old));
        Rect2I usable = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        Vector2I frame = _root.GetSizeWithDecorations() - _root.Size;
        if (size.X + frame.X > usable.Size.X || size.Y + frame.Y > usable.Size.Y)
        {
            _root.Mode = Window.ModeEnum.Maximized;
            return;
        }

        _root.Size = size;
        if (first)
        {
            _root.Position = usable.Position + (usable.Size - size - frame) / 2;
        }
    }
}
