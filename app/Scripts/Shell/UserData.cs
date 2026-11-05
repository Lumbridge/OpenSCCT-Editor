using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell;

/// <summary>
/// Where the editor keeps its own files: settings (recent files, layout), autosaves, crash markers, builds, shots, the
/// prefab library, property sets and live discovery. An interactive editor uses the user's folders (Godot's user
/// folder, <c>%APPDATA%\OpenSCCT Editor</c>, and <c>%LOCALAPPDATA%\OpenSCCT Editor\live</c>). An automated run (headless,
/// background mode, <c>--run=</c> or <c>--shots=</c>) gets a folder of its own under
/// <c>%TEMP%\OpenSCCT Editor\runs</c>, so it never reads or rewrites the user's; <c>-- --user-data=&lt;folder&gt;</c> or
/// <c>OSE_USER_DATA</c> names one. The choice goes into <c>OSE_USER_DATA</c> for the editor's own libraries
/// (<see cref="UserFolders"/>), and such a run also keeps the asset cache and imports (<c>OSE_CACHE</c>,
/// <c>OSE_DATA</c>) apart from the user's unless the launch set them. Godot's log is opened before any editor code
/// runs: an automated launch passes <c>--log-file &lt;path&gt;</c> to keep it out of the user's log folder, and the
/// user's own editor (<c>Run-Editor.cmd</c>) logs to a file of its own per session, <c>logs/editor-&lt;time&gt;.log</c>,
/// which no other run writes.
/// </summary>
public static class UserData
{
    public const string Flag = "--user-data=";

    /// <summary>How long old automated run folders are kept.</summary>
    private static readonly TimeSpan s_keepRuns = TimeSpan.FromDays(7);

    /// <summary>How many of the user's own session logs (<c>logs/editor-*.log</c>) are kept.</summary>
    private const int KeepSessionLogs = 10;

    private static string? s_folder;
    private static bool s_runFolder;

    /// <summary>Where automated runs keep what outlives them: <c>%TEMP%\OpenSCCT Editor</c>.</summary>
    private static string AutomatedRoot => Path.Combine(Path.GetTempPath(), "OpenSCCT Editor");

    /// <summary>The folder (absolute).</summary>
    public static string Folder => s_folder ??= Path.GetFullPath(ProjectSettings.GlobalizePath("user://"));

    /// <summary>True when this run keeps its data apart from the user's own.</summary>
    public static bool IsSeparate { get; private set; }

    /// <summary>A file or folder in <see cref="Folder"/>.</summary>
    public static string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>Where screenshots go when the run names no folder: <c>shots</c> in <see cref="Folder"/>, or for a run
    /// with a new run folder (tidied away after a week) <c>%TEMP%\OpenSCCT Editor\shots</c>, which is kept.</summary>
    public static string ShotsFolder => s_runFolder ? Path.Combine(AutomatedRoot, "shots") : PathOf("shots");

    /// <summary>Picks the folder. Runs once, from the first editor code (the background window autoload), before
    /// anything reads settings.</summary>
    public static void Init()
    {
        string[] args = OS.GetCmdlineUserArgs();
        string? chosen = args.FirstOrDefault(a => a.StartsWith(Flag, StringComparison.Ordinal))?[Flag.Length..];
        if (string.IsNullOrWhiteSpace(chosen))
        {
            chosen = UserFolders.Separate;
        }

        if (string.IsNullOrWhiteSpace(chosen) && Automated(args) is { } why)
        {
            chosen = NewRunFolder();
            s_runFolder = true;
            GD.Print($"User data: {why}, so this run keeps its settings, autosaves and live discovery in {chosen}");
        }

        if (string.IsNullOrWhiteSpace(chosen))
        {
            s_folder = Path.GetFullPath(ProjectSettings.GlobalizePath("user://"));
            PruneSessionLogs();
            return;
        }

        s_folder = Path.GetFullPath(chosen);
        Directory.CreateDirectory(s_folder);
        IsSeparate = true;
        System.Environment.SetEnvironmentVariable(UserFolders.Variable, s_folder);
        // The asset index, thumbnails and imports are only caches, but the user's editor reads them while it runs:
        // automated runs share copies of their own (warm after the first run) unless the launch chose folders.
        KeepApart("OSE_CACHE", Path.Combine(AutomatedRoot, "cache"));
        KeepApart(SvmbuildRunner.DataVariable, Path.Combine(AutomatedRoot, "data"));
        if (GodotLogIsUsers())
        {
            GD.Print("User data: Godot's own log goes to the user's log folder (logs/godot.log); launch with --log-file <path> to keep it apart.");
        }
    }

    /// <summary>Points <paramref name="variable"/> at <paramref name="folder"/> unless the launch set it.</summary>
    private static void KeepApart(string variable, string folder)
    {
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(variable)))
        {
            System.Environment.SetEnvironmentVariable(variable, folder);
        }
    }

    /// <summary>Keeps the newest <see cref="KeepSessionLogs"/> of the user's session logs.</summary>
    private static void PruneSessionLogs()
    {
        string logs = PathOf("logs");
        if (!Directory.Exists(logs))
        {
            return;
        }

        foreach (FileInfo old in new DirectoryInfo(logs).EnumerateFiles("editor-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepSessionLogs).ToList())
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log still open (another editor) stays.
            }
        }
    }

    /// <summary>Why this run is automated, or null for an interactive one.</summary>
    private static string? Automated(string[] args)
    {
        if (DisplayServer.GetName() == "headless")
        {
            return "a headless run";
        }

        if (BackgroundMode.Wanted(args))
        {
            return "a background run";
        }

        return args.Any(a => a.StartsWith("--run=", StringComparison.Ordinal)) ? "a run with --run=" : null;
    }

    /// <summary>A new folder for this run under %TEMP%\OpenSCCT Editor\runs; run folders older than a week go.</summary>
    private static string NewRunFolder()
    {
        string runs = Path.Combine(AutomatedRoot, "runs");
        if (Directory.Exists(runs))
        {
            foreach (DirectoryInfo old in new DirectoryInfo(runs).EnumerateDirectories()
                .Where(d => DateTime.UtcNow - d.LastWriteTimeUtc > s_keepRuns).ToList())
            {
                try
                {
                    old.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Old run folders are only tidied up; one in use stays, and the others still go.
                }
            }
        }

        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
            System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        return Path.Combine(runs, name);
    }

    /// <summary>True when Godot logs to a file in the user's own Godot user folder (no <c>--log-file</c>, and the
    /// launch did not move the user folder itself).</summary>
    private static bool GodotLogIsUsers()
    {
        if (!ProjectSettings.GetSettingWithOverride("debug/file_logging/enable_file_logging").AsBool() ||
            System.Environment.GetCommandLineArgs().Concat(OS.GetCmdlineArgs()).Any(a => a.StartsWith("--log-file", StringComparison.Ordinal)))
        {
            return false;
        }

        string users = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "OpenSCCT Editor");
        return string.Equals(Path.GetFullPath(ProjectSettings.GlobalizePath("user://")).TrimEnd('\\', '/'), Path.GetFullPath(users).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }
}
