using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Assets;

/// <summary>Opens the asset-pack catalogue or the File menu's downloadable-map picker.</summary>
[ShellPart(Order = -21)]
public sealed class ContentPacksPart : IShellPart
{
    private static ContentPacksPart? s_current;
    private MainWindow _window = null!;
    private ContentPacksWindow? _packs;
    private ContentPacksWindow? _maps;

    public void Attach(MainWindow window, EditorContext context)
    {
        _window = window;
        s_current = this;
        context.AddAction(new EditorAction("content.packs", "Content Packs…", "Tools", () => Open())
        {
            Order = 810,
            Description = "Build stock game assets from Steam and browse community maps",
        });
        context.AddAction(new EditorAction("file.downloadOpen", "Download & Open…", "File", () =>
        {
            _maps ??= CreateWindow(openMaps: true);
            _maps.Open(null);
        })
        {
            Order = 115,
            IsEnabled = () => !BackgroundJobs.IsRunning("Import"),
            Description = "Download a community map and open it for editing",
        });
        if (!OpenScct.Editor.Core.Updates.EditorRelease.IsDevBuild &&
            OpenScct.Editor.Core.Services.EditorServices.ContentRoot is null && Godot.DisplayServer.GetName() != "headless")
            Godot.Callable.From(() => Open()).CallDeferred();
    }

    /// <summary>Shows the catalogue, optionally searching for a map code.</summary>
    public static void Open(string? mapCode = null) => s_current?.Show(mapCode);

    private void Show(string? mapCode)
    {
        if (_packs is null)
        {
            _packs = CreateWindow(openMaps: false);
        }

        _packs.Open(mapCode);
    }

    private ContentPacksWindow CreateWindow(bool openMaps)
    {
        var window = new ContentPacksWindow(openMaps);
        _window.AddChild(window);
        return window;
    }
}
