using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports;

/// <summary>Shows an action's key on a popup item of a view menu, as the main menus do: for reference only (the
/// shortcut router runs shortcuts), and not on a disabled item.</summary>
internal static class MenuKeys
{
    public static void Show(PopupMenu menu, int index, EditorContext ctx, string actionId)
    {
        if (menu.IsItemDisabled(index) || ctx.FindAction(actionId) is not { } a || KeyChord.Parse(ShortcutBindings.Of(a)) is not { } chord)
        {
            return;
        }

        menu.SetItemShortcut(index, new Godot.Shortcut { Events = [chord.ToEvent()] }, global: false);
        menu.SetItemShortcutDisabled(index, true);
    }
}
