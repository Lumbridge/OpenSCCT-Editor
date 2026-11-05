using System.Runtime.InteropServices;
using Godot;

namespace OpenScctEditor.Shell.Background;

/// <summary>
/// Background mode: the editor runs for screenshots or scripts without touching the person at the machine. On with
/// <c>-- --background</c>, <c>OSE_BACKGROUND=1</c> or a shot run (<c>--shots=</c>); <c>OSE_FOCUS=1</c> turns it off
/// for debugging. The window stays off-screen and unfocused (<see cref="BackgroundWindow"/>); popups are drawn inside
/// it; native file dialogs, programs and folder windows are refused; the clipboard and the mouse pointer are the
/// editor's own (<see cref="EditorClipboard"/>, <see cref="EditorPointer"/>); sound is muted and the frame rate capped.
/// The run keeps its settings, autosaves and live discovery apart from the user's (<see cref="UserData"/>); the
/// launch script also moves Godot's user folder, log, caches and data folders, see <c>tools/Start-BackgroundEditor.ps1</c>.
/// </summary>
public static class BackgroundMode
{
    public const string Flag = "--background";

    /// <summary>Frames per second in background mode: plenty for screenshots, light on the GPU of a machine in use.</summary>
    public const int MaxFps = 30;

    /// <summary>True in a background run.</summary>
    public static bool Active { get; private set; }

    /// <summary>True when this run should stay in the background.</summary>
    public static bool Wanted(IEnumerable<string> args)
    {
        if (System.Environment.GetEnvironmentVariable("OSE_FOCUS") == "1")
        {
            return false;
        }

        return System.Environment.GetEnvironmentVariable("OSE_BACKGROUND") == "1" ||
               args.Any(a => a == Flag || a.StartsWith("--shots=", StringComparison.Ordinal));
    }

    /// <summary>Turns background mode on. Runs before the main scene exists, so no popup has been made yet.</summary>
    public static void Enable(SceneTree tree)
    {
        Active = true;
        // Menus, tooltips and dialogs become part of the main window's image instead of OS windows that Godot would
        // move onto the visible screen.
        tree.Root.GuiEmbedSubwindows = true;
        // Draw every frame (frame waits always finish), but not as fast as the GPU allows.
        OS.LowProcessorUsageMode = false;
        Engine.MaxFps = MaxFps;
        AudioServer.SetBusMute(0, true);
        EditorPointer.Detached = true;
        EditorClipboard.Local = true;
        if (OperatingSystem.IsWindows())
        {
            // A long stall on the main thread must not make Windows put a visible "Not responding" ghost in its place.
            DisableProcessWindowsGhosting();
        }

        GD.Print($"Background mode: user folder {UserData.Folder}, live folder " +
            (AllowsLiveServer ? OpenScct.Editor.Core.Scripting.Live.LiveDiscovery.Folder : "(none: live editing off)"));
    }

    /// <summary>Whether live editing may start: in background mode only with a discovery folder of its own,
    /// so <c>ose serve --live</c> in the user's own session never finds this editor.</summary>
    public static bool AllowsLiveServer =>
        !Active || UserData.IsSeparate || !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("OSE_LIVE_DIR"));

    /// <summary>Logs that something was skipped because the run is in the background; returns true when it was.</summary>
    public static bool Refuse(string what)
    {
        if (!Active)
        {
            return false;
        }

        GD.Print($"Background mode: {what} skipped.");
        return true;
    }

    [DllImport("user32.dll")]
    private static extern void DisableProcessWindowsGhosting();
}
