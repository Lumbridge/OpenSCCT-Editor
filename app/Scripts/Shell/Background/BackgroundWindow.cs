using System.Globalization;
using System.Runtime.InteropServices;
using Godot;

namespace OpenScctEditor.Shell.Background;

/// <summary>
/// Keeps a background run's window away from the person using the machine: it never takes focus, has no taskbar
/// button and sits off-screen at (-32000, -32000), where it still renders (screenshots read the editor's own image,
/// not the screen). A window started minimised by a run-time <c>app/override.cfg</c> is restored off-screen, because
/// a minimised window does not draw. An autoload, so the taskbar button goes before the main scene loads a map.
/// Port of OpenSCCT's <c>BackgroundWindow</c>; <see cref="BackgroundMode"/> holds the rest of background mode.
/// </summary>
public partial class BackgroundWindow : Node
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExNoActivate = 0x08000000;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int OffScreen = -32000;

    private int _frames;

    public override void _EnterTree()
    {
        // The first editor code: where this run keeps its settings is decided before anything reads them.
        UserData.Init();
        if (!BackgroundMode.Wanted(OS.GetCmdlineUserArgs()))
        {
            NormalRun();
            QueueFree();
            return;
        }

        BackgroundMode.Enable(GetTree());
        Hide();
    }

    public override void _Process(double delta)
    {
        // Godot may still show or move the window around its first frames; repeat a few times, then stop. Once the
        // editor is quitting the window stays hidden (EditorLifetime hides it).
        if (++_frames > 30 || EditorLifetime.IsQuitting)
        {
            SetProcess(false);
            GD.Print("Background window: " + Report());
            return;
        }

        if (_frames is 1 or 2 or 5 or 15 or 30)
        {
            Hide();
        }
    }

    /// <summary>Resizes the window's client area (screenshots at other sizes); in background mode it stays off-screen.</summary>
    public static void Resize(Vector2I size)
    {
        DisplayServer.WindowSetSize(size);
        if (BackgroundMode.Active)
        {
            Hide();
        }
    }

    /// <summary>Takes the window off the screen when the editor quits, so it does not stay up while background jobs
    /// wind down and the engine shuts down: hidden on Windows, minimised elsewhere.</summary>
    public static void HideForQuit()
    {
        if (DisplayServer.GetName() == "headless")
        {
            return;
        }

        if (NativeHandle() is { } hwnd)
        {
            ShowWindow(hwnd, SwHide);
        }
        else
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Minimized);
        }
    }

    /// <summary>Where the window is and how it is styled, for the run log and the screenshot manifest.</summary>
    public static string Report()
    {
        if (NativeHandle() is not { } hwnd)
        {
            return "no native window";
        }

        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        GetWindowRect(hwnd, out Rect r);
        return string.Create(CultureInfo.InvariantCulture,
            $"at ({r.Left}, {r.Top}) size {r.Right - r.Left}x{r.Bottom - r.Top}, " +
            $"tool window {(style & WsExToolWindow) != 0}, no-activate {(style & WsExNoActivate) != 0}, " +
            $"taskbar button {(style & WsExAppWindow) != 0 && (style & WsExToolWindow) == 0}, " +
            $"minimised {IsIconic(hwnd)}, foreground {GetForegroundWindow() == hwnd}");
    }

    /// <summary>A normal run: undo what a leftover run-time override.cfg would do (start minimised, never focus,
    /// embedded popups), so the editor always activates on click.</summary>
    private void NormalRun()
    {
        if (DisplayServer.GetName() == "headless")
        {
            return;
        }

        Window window = GetWindow();
        window.Unfocusable = false;
        window.GuiEmbedSubwindows = false;
        if (window.Mode == Window.ModeEnum.Minimized)
        {
            window.Mode = Window.ModeEnum.Windowed;
        }
    }

    private static void Hide()
    {
        if (NativeHandle() is not { } hwnd)
        {
            return;
        }

        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        long wanted = (style | WsExNoActivate | WsExToolWindow) & ~WsExAppWindow;
        bool restyle = wanted != style;
        if (restyle)
        {
            // The taskbar picks up a style change only while the window is hidden.
            ShowWindow(hwnd, SwHide);
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(wanted));
        }

        var place = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref place))
        {
            return;
        }

        // The placement holds the outer size (frame included); keep it, only move the window.
        int w = place.Normal.Right - place.Normal.Left;
        int h = place.Normal.Bottom - place.Normal.Top;
        if (place.Normal.Left != OffScreen || place.Normal.Top != OffScreen || place.ShowCmd != SwShowNoActivate || restyle)
        {
            place.Normal = new Rect { Left = OffScreen, Top = OffScreen, Right = OffScreen + w, Bottom = OffScreen + h };
            place.ShowCmd = SwShowNoActivate;
            SetWindowPlacement(hwnd, ref place);
        }
    }

    private static IntPtr? NativeHandle()
    {
        if (!OperatingSystem.IsWindows() || DisplayServer.GetName() == "headless")
        {
            return null;
        }

        var hwnd = new IntPtr(DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle));
        return hwnd == IntPtr.Zero ? null : hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        public Rect Normal;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement place);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement place);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
