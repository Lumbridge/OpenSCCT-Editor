using System.Reflection;
using OpenScct.Launcher.GitHub;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Core.Updates;

/// <summary>
/// The editor's releases (docs/RELEASING.md): tags <c>v&lt;semver&gt;</c> on github.com/Lumbridge/OpenSCCT-Editor, each
/// with one archive per platform (<c>OpenSCCT-Editor-0.2.0-win-x64.zip</c>) and its checksum file (<c>.sha256</c>).
/// The archive holds one folder, <see cref="FolderName"/>, with the editor's exe, Godot's data folder, <c>ose</c> and
/// <c>version.json</c> (<see cref="InstallInfo"/>). Also the version this build carries.
/// </summary>
public static class EditorRelease
{
    public const string Owner = "Lumbridge";
    public const string Repo = "OpenSCCT-Editor";
    public const string Product = "OpenSCCT Editor";

    /// <summary>The archive's single top folder.</summary>
    public const string FolderName = "OpenSCCT Editor";

    public const string AssetPrefix = "OpenSCCT-Editor-";

    /// <summary>The folder of the command line tool inside a release.</summary>
    public const string CliFolder = "ose";

    /// <summary>The prerelease tag every build from source carries (Directory.Build.props).</summary>
    public const string DevTag = "dev";

    public static ReleaseSource Source { get; } = new(Owner, Repo, null);

    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>The .NET runtime identifier the release assets are named by: <c>win-x64</c>, <c>linux-x64</c>.</summary>
    public static string Platform { get; } = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unsupported";

    /// <summary>The editor's exe in a release folder.</summary>
    public static string ExeName => OperatingSystem.IsWindows() ? "OpenSCCT Editor.exe" : "OpenSCCT Editor.x86_64";

    public static string CliExeName => OperatingSystem.IsWindows() ? "ose.exe" : "ose";

    public static string ArchiveExtension => OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";

    /// <summary>The download of one version for this platform.</summary>
    public static string AssetName(SemVersion version) => $"{AssetPrefix}{version}-{Platform}{ArchiveExtension}";

    /// <summary>The informational version this build carries: <c>0.2.0+&lt;commit&gt;</c> for a release package,
    /// <c>0.2.0-dev+&lt;commit&gt;</c> for every other build.</summary>
    public static string InformationalVersion { get; } =
        typeof(EditorRelease).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

    /// <summary>This build's version (build metadata kept in <see cref="SemVersion.Build"/>).</summary>
    public static SemVersion Version { get; } =
        SemVersion.TryParse(InformationalVersion, out SemVersion? v) ? v : SemVersion.Parse("0.0.0-" + DevTag);

    /// <summary>The commit this build was made from, when the build recorded one.</summary>
    public static string? Commit => Version.Build.Length > 0 ? Version.Build : null;

    public static string? ShortCommit => Commit is { Length: > 7 } c ? c[..7] : Commit;

    /// <summary>A build from source, not a release package: it never updates itself.</summary>
    public static bool IsDevBuild => IsDevVersion(Version);

    public static bool IsDevVersion(SemVersion version) => version.Prerelease.Contains(DevTag, StringComparer.OrdinalIgnoreCase);

    /// <summary>"0.2.0 (eb6af75)", for About and the logs.</summary>
    public static string Display => ShortCommit is { } c ? $"{Version} ({c})" : Version.ToString();

    /// <summary>A version as people read it: "0.3.0-beta.1 (preview)" for a pre-release.</summary>
    public static string Describe(SemVersion version) => version.IsPrerelease ? $"{version} (preview)" : version.ToString();
}
