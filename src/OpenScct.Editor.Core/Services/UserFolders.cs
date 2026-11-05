namespace OpenScct.Editor.Core.Services;

/// <summary>
/// The editor's per-user folders: its settings folder (<c>%APPDATA%\OpenSCCT Editor</c>: settings, autosaves, crash
/// markers, builds, the prefab library, property sets and backup settings) and the live discovery folder
/// (<c>%LOCALAPPDATA%\OpenSCCT Editor\live</c>). <c>OSE_USER_DATA</c> moves all of them into one folder of its own (live
/// discovery to its <c>live</c> subfolder), so a scripted, headless or background run never reads or writes the
/// user's own. The editor sets it itself for such runs; <c>ose</c> run with the same value finds that editor live,
/// unless <c>OSE_LIVE_DIR</c> moved its discovery file (the background launch script sets it: use that folder).
/// </summary>
public static class UserFolders
{
    /// <summary>The environment variable that keeps a run's user data apart.</summary>
    public const string Variable = "OSE_USER_DATA";

    /// <summary>The run's own user data folder when <see cref="Variable"/> is set, else null (the user's folders).</summary>
    public static string? Separate =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } dir ? Path.GetFullPath(dir) : null;

    /// <summary>The settings folder: <see cref="Separate"/>, else <c>%APPDATA%\OpenSCCT Editor</c>.</summary>
    public static string Settings =>
        Separate ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSCCT Editor");

    /// <summary>The live discovery folder: <c>live</c> under <see cref="Separate"/>, else
    /// <c>%LOCALAPPDATA%\OpenSCCT Editor\live</c>.</summary>
    public static string Live =>
        Separate is { } dir
            ? Path.Combine(dir, "live")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSCCT Editor", "live");
}
