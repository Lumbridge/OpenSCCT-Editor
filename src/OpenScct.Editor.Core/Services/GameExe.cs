using System.Text.Json;

namespace OpenScct.Editor.Core.Services;

/// <summary>
/// Finds OpenSCCT.exe for Play, the same way for the CLI and the app: the given path, else <c>OSE_GAME_EXE</c>, else
/// <c>OPENSCCT_EXE</c>, else the launcher's current install, else an OpenSCCT checkout's <c>local/testbuild</c> (then
/// its <c>local/build</c> and its root), else the newest other installed version.
/// </summary>
public static class GameExe
{
    /// <summary>The environment variables that name the exe, in the order they are read.</summary>
    public static readonly IReadOnlyList<string> Variables = ["OSE_GAME_EXE", "OPENSCCT_EXE"];

    /// <summary>The launcher's folder (install.json, versions, content).</summary>
    public const string LauncherRootVariable = "OSE_LAUNCHER_ROOT";

    public static string? ManagedLauncherRoot => Environment.GetEnvironmentVariable(LauncherRootVariable) is { Length: > 0 } root
        ? Path.GetFullPath(root) : null;

    public static string LauncherRoot => ManagedLauncherRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSCCT");

    /// <summary>The full path of the first candidate that exists, or null.</summary>
    public static string? Find(string? given = null)
    {
        foreach (string? c in Candidates(given))
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c))
            {
                return Path.GetFullPath(c);
            }
        }

        return null;
    }

    /// <summary>The launcher's content folder for an exe it installed (the game finds a checkout's on its own).</summary>
    public static string? LauncherContent(string exe)
    {
        if (!exe.StartsWith(Path.Combine(LauncherRoot, "versions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        string packs = Path.Combine(LauncherRoot, "packs");
        if (File.Exists(Path.Combine(packs, "shared", "classes.json"))) return packs;
        string content = Path.Combine(LauncherRoot, "content");
        return ManagedLauncherRoot is null && Directory.Exists(content) ? content : null;
    }

    /// <summary>
    /// The content folder the game beside <paramref name="exe"/> uses, for a run that starts a copy of it elsewhere:
    /// the launcher's, else the <c>.content</c> of the OpenSCCT checkout holding the exe (which the game would find
    /// above itself), else, without <c>SVM_CONTENT</c> (which the copy reads as the game would), the exe's own folder
    /// when its content (<c>shared/</c>) is beside it, else null.
    /// </summary>
    public static string? ContentOf(string exe)
    {
        if (LauncherContent(exe) is { } launcher)
        {
            return launcher;
        }

        for (DirectoryInfo? d = new FileInfo(Path.GetFullPath(exe)).Directory; d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "OpenSCCT.slnx")))
            {
                string content = Path.Combine(d.FullName, ".content");
                return Directory.Exists(content) ? content : null;
            }
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SVM_CONTENT")) && Directory.Exists(Path.Combine(folder, "shared")) ? folder : null;
    }

    private static IEnumerable<string?> Candidates(string? given)
    {
        yield return given;
        if (ManagedLauncherRoot is not null)
        {
            if (CurrentVersion() is { } managedVersion)
                yield return Path.Combine(LauncherRoot, "versions", managedVersion, "OpenSCCT.exe");
            yield break;
        }
        foreach (string name in Variables)
        {
            yield return Environment.GetEnvironmentVariable(name);
        }

        string versions = Path.Combine(LauncherRoot, "versions");
        if (CurrentVersion() is { } current)
        {
            yield return Path.Combine(versions, current, "OpenSCCT.exe");
        }

        if (EditorServices.OpenScctRoot is { } root)
        {
            yield return Path.Combine(root, "local", "testbuild", "OpenSCCT.exe");
            yield return Path.Combine(root, "local", "build", "OpenSCCT.exe");
            yield return Path.Combine(root, "OpenSCCT.exe");
        }

        if (Directory.Exists(versions))
        {
            foreach (string dir in Directory.GetDirectories(versions).OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out Version? v) ? v : null))
            {
                yield return Path.Combine(dir, "OpenSCCT.exe");
            }
        }
    }

    /// <summary>The oldest OpenSCCT the editor starts: older games lack options and map data the editor's builds use.</summary>
    public static readonly Version MinimumVersion = new(0, 8, 93);

    /// <summary>
    /// The version of the game at <paramref name="exe"/>: the version in its <c>openscct-build.json</c> (a release), else
    /// the file version of its script assembly (<c>data_*/OpenScctGame.dll</c>); null when neither says.
    /// </summary>
    public static Version? VersionOf(string exe)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".";
        try
        {
            string build = Path.Combine(folder, "openscct-build.json");
            if (File.Exists(build))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(build));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out JsonElement v)
                    && v.ValueKind == JsonValueKind.String && ParseVersion(v.GetString()) is { } fromBuild)
                {
                    return fromBuild;
                }
            }

            string? assembly = Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder, "data_*").Select(d => Path.Combine(d, "OpenScctGame.dll")).FirstOrDefault(File.Exists)
                : null;
            return assembly is null ? null : ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly).FileVersion);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why the game at <paramref name="exe"/> cannot be used (it is older than <see cref="MinimumVersion"/>), or null when
    /// it can or its version is unknown (a build laid out some other way is given the benefit of the doubt).
    /// </summary>
    public static string? TooOld(string exe) => VersionOf(exe) is { } version && version < MinimumVersion
        ? $"OpenSCCT {version.ToString(3)} ({exe}) is too old for this editor, which needs {MinimumVersion.ToString(3)} or newer: " +
          "update it with the OpenSCCT launcher, or point OSE_GAME_EXE at a newer build"
        : null;

    /// <summary>"0.8.93", "0.8.93.0" or "0.8.93-beta.1+abc" as major.minor.patch; null when it is not a version.</summary>
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string core = text.Trim().Split('-', '+', ' ')[0];
        return Version.TryParse(core, out Version? v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>The console build beside <paramref name="exe"/> when there is one (its output can be read), else the exe.</summary>
    public static string ConsoleOf(string exe)
    {
        string console = Path.Combine(Path.GetDirectoryName(exe) ?? ".", Path.GetFileNameWithoutExtension(exe) + ".console.exe");
        return File.Exists(console) ? console : exe;
    }

    /// <summary>
    /// False when the game beside <paramref name="exe"/> is known to lack <c>--svm-mapshot</c> (map pictures came in
    /// after 0.8.84): its script assembly (<c>data_*/OpenScctGame.dll</c>) does not hold the option's text. True when it
    /// does, or when there is no such assembly to read (a build laid out some other way is given the benefit of the doubt).
    /// </summary>
    public static bool SupportsMapShot(string exe)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".";
        string? assembly = Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder, "data_*").Select(d => Path.Combine(d, "OpenScctGame.dll")).FirstOrDefault(File.Exists)
            : null;
        if (assembly is null)
        {
            return true;
        }

        try
        {
            // C# string literals are stored as UTF-16 in the assembly's user string heap.
            return File.ReadAllBytes(assembly).AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(MapShotOption)) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The game's option that renders map pictures and quits.</summary>
    public const string MapShotOption = "--svm-mapshot=";

    /// <summary>The launcher's current version (install.json <c>Current</c>), or null.</summary>
    private static string? CurrentVersion()
    {
        string file = Path.Combine(LauncherRoot, "install.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, "Current", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    return p.Value.GetString() is { Length: > 0 } v ? v : null;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
