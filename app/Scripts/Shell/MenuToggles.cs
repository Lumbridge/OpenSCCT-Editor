using Godot;
using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>
/// Menus whose check and radio items leave the menu open when clicked, so several toggles or choices can be set in one
/// visit; plain commands still close it. Godot does not redraw an item's mark by itself, so the menu's sync sets the
/// marks again after each click, once the item's own handler has run.
/// </summary>
public static class MenuToggles
{
    /// <summary>Keeps <paramref name="popup"/> open on check and radio items and runs <paramref name="sync"/> after each
    /// such click while it is still open.</summary>
    public static void KeepOpen(PopupMenu popup, Action sync)
    {
        popup.HideOnCheckableItemSelection = false;
        popup.IndexPressed += index =>
        {
            // True for radio items as well as check boxes.
            if (!popup.IsItemCheckable((int)index))
            {
                return;
            }

            // Deferred: the item's IdPressed handler (which runs the action) may come after this one.
            EditorLifetime.Defer(() =>
            {
                if (GodotObject.IsInstanceValid(popup) && popup.Visible)
                {
                    sync();
                }
            });
        };
    }

    /// <summary>Sets each action item's mark and enabled state from its action, for menus whose item ids index
    /// <paramref name="ids"/> (action ids; empty for separators and other rows).</summary>
    public static void SyncActions(PopupMenu popup, IReadOnlyList<string> ids, EditorContext ctx)
    {
        for (int i = 0; i < popup.ItemCount; i++)
        {
            int id = popup.GetItemId(i);
            if (popup.IsItemSeparator(i) || id < 0 || id >= ids.Count || ctx.FindAction(ids[id]) is not { } a)
            {
                continue;
            }

            if (a.IsChecked is { } isChecked && popup.IsItemCheckable(i))
            {
                popup.SetItemChecked(i, isChecked());
            }

            popup.SetItemDisabled(i, !a.IsEnabled());
        }
    }
}
