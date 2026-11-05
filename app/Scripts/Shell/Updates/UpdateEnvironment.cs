using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Updates;
using OpenScct.Launcher.Versioning;

namespace OpenScctEditor.Shell.Updates;

/// <summary>
/// Whether this run may update itself, worked out once at start. Only a release package does: an exported build
/// (Godot's "template" feature), with a release version (not <c>-dev</c>) that matches the <c>version.json</c> next to
/// its exe. Running from source (Run-Editor.cmd), a local export without the release stamp and a hand-assembled folder
/// all count as source runs, which never update.
/// </summary>
public sealed class UpdateEnvironment
{
    private bool? _canWrite;

    private UpdateEnvironment(string exe, string? whyNot)
    {
        Exe = exe;
        Install = UpdatePaths.InstallOf(exe);
        WhyNot = whyNot;
    }

    /// <summary>This run's answer.</summary>
    public static UpdateEnvironment Current { get; } = Detect();

    /// <summary>A release package that may update itself.</summary>
    public bool IsRelease => WhyNot is null;

    /// <summary>Why this run does not update itself; null for a release package.</summary>
    public string? WhyNot { get; }

    public string Exe { get; }

    public string Install { get; }

    public static SemVersion Version => EditorRelease.Version;

    /// <summary>Whether an update can be installed in place: the folder can be written (checked once, when first
    /// needed) and the platform's archives can be unpacked here. Otherwise updates are offered as a download.</summary>
    public bool CanInstall => _canWrite ??= IsRelease && OperatingSystem.IsWindows()
        && string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("FLATPAK_ID")) && UpdateStager.CanWrite(Install);

    public string Describe() => IsRelease ? $"release {Version} in {Install}" : $"off ({WhyNot})";

    private static UpdateEnvironment Detect()
    {
        string exe = OS.GetExecutablePath();
        if (!OS.HasFeature("template"))
        {
            return new UpdateEnvironment(exe, "running from source");
        }

        if (EditorRelease.IsDevBuild)
        {
            return new UpdateEnvironment(exe, "a development build");
        }

        InstallInfo? info = InstallInfo.TryRead(UpdatePaths.InstallOf(exe));
        if (info is null)
        {
            return new UpdateEnvironment(exe, $"no {InstallInfo.FileName} next to the editor");
        }

        return info.ParsedVersion != Version
            ? new UpdateEnvironment(exe, $"{InstallInfo.FileName} says {info.Version}, the editor is {Version}")
            : new UpdateEnvironment(exe, null);
    }
}

/// <summary>The update preferences, in the <c>[updates]</c> section of settings.cfg.</summary>
public static class UpdateSettings
{
    private const string Section = "updates";

    /// <summary>Check GitHub for a newer version when the editor starts.</summary>
    public static bool CheckOnStart
    {
        get => AppSettings.GetBool(Section, "check_on_start", true);
        set => AppSettings.Set(Section, "check_on_start", value);
    }

    /// <summary>Offer pre-releases (previews) too.</summary>
    public static bool Prereleases
    {
        get => AppSettings.GetBool(Section, "prereleases", false);
        set => AppSettings.Set(Section, "prereleases", value);
    }

    /// <summary>When GitHub last answered a check (not a check answered from the cache).</summary>
    public static DateTimeOffset? LastCheck
    {
        get => DateTimeOffset.TryParse(AppSettings.GetString(Section, "last_check", ""), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t)
            ? t : null;
        set => AppSettings.Set(Section, "last_check", value?.ToString("o", CultureInfo.InvariantCulture) ?? "");
    }

    /// <summary>The version the user chose to skip: no notice for it or older ones.</summary>
    public static SemVersion? Skipped
    {
        get => SemVersion.TryParse(AppSettings.GetString(Section, "skipped", ""), out SemVersion? v) ? v : null;
        set => AppSettings.Set(Section, "skipped", value?.ToString() ?? "");
    }
}
