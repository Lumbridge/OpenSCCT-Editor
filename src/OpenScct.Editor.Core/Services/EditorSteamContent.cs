using OpenScct.Launcher.Content;
using OpenScct.Launcher.Install;
using OpenScct.Maps;

namespace OpenScct.Editor.Core.Services;

/// <summary>Standalone and launcher installs use the same converted Steam files.</summary>
public static class EditorSteamContent
{
    public static LauncherLayout Layout => new(GameExe.LauncherRoot);
    public static string? ActiveRoot => SteamContentInstall.ActiveRoot(Layout.Content);
    public static string? FindSource() => GodotSteamContent.FindSource();

    public static Task<string> BuildAsync(string? source, IProgress<ContentProgress>? progress, CancellationToken cancel)
    {
        var layout = Layout;
        string versus = GodotSteamContent.FindSource(source) ?? throw new InvalidOperationException(
            "Choose your installed Steam copy of Splinter Cell Chaos Theory (app 13570).");
        // GUI binaries are beside the tools. The CLI and the Godot assemblies are one level below them.
        var exe = new FileInfo(Environment.ProcessPath ?? AppContext.BaseDirectory).Directory;
        string? installedGame = new VersionStore(layout).Current()?.ToString();
        string? toolsOwner = new[] { exe?.FullName, exe?.Parent?.FullName,
                installedGame is null ? null : layout.VersionDir(installedGame) }
            .FirstOrDefault(folder => folder is not null && File.Exists(Path.Combine(folder, "contenttools", "steam-builder-v1")));
        if (toolsOwner is null)
            throw new InvalidOperationException("Install or repair the current OpenSCCT Editor or Godot release to get the Steam content builder.");
        return new GodotSteamContent(layout).BuildAsync(toolsOwner, versus, progress, cancel);
    }
}
