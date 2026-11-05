using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The placeables palette's command (Gameplay > Placeables Palette, also in the command palette and on the security
/// and place tools' option bars): shows the Placeables panel and puts the typing focus in its search box.
/// </summary>
[ShellPart(Order = -27)]
public sealed class PlaceablesActions : IShellPart
{
    /// <summary>The action's id.</summary>
    public const string Open = "gameplay.placeables";

    public void Attach(MainWindow window, EditorContext context) =>
        context.AddAction(new EditorAction(Open, "Placeables Palette", "Gameplay", () =>
        {
            if (DockTabs.ShowPanel("Placeables"))
            {
                PlaceablesPanel.Instance?.FocusSearch();
                context.SetStatus("Placeables: drag a tile into a view to place it, or click one to place it with the place tool");
            }
        })
        {
            Order = 5,
            Description = "Shows the palette of security devices, objectives, stations and interactive objects to drag into the views",
        });
}
