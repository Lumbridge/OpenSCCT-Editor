using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Assets;

/// <summary>Opens the content catalogue on demand from the Assets and Tools menus.</summary>
[ShellPart(Order = -21)]
public sealed class ContentPacksPart : IShellPart
{
    private static ContentPacksPart? s_current;
    private MainWindow _window = null!;
    private ContentPacksWindow? _packs;

    public void Attach(MainWindow window, EditorContext context)
    {
        _window = window;
        s_current = this;
        context.AddAction(new EditorAction("content.packs", "Content Packs…", "Tools", () => Open())
        {
            Order = 810,
            Description = "Find and download map asset libraries and shared data from OpenSCCT-Content",
        });
    }

    /// <summary>Shows the catalogue, optionally searching for a map code.</summary>
    public static void Open(string? mapCode = null) => s_current?.Show(mapCode);

    private void Show(string? mapCode)
    {
        if (_packs is null)
        {
            _packs = new ContentPacksWindow();
            _window.AddChild(_packs);
        }

        _packs.Open(mapCode);
    }
}
