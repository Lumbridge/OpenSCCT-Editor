using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;

namespace OpenScctEditor.Shell;

/// <summary>
/// The menu bar, built from the registered actions each time a menu opens (so actions added later, enabled states
/// and toggles are always current). An action's <see cref="EditorAction.Menu"/> is its path ("Brush/CSG" is the CSG
/// submenu of Brush); an empty path keeps it out of the menus (shortcut and palette only). Items sort by
/// <see cref="EditorAction.Order"/>; a separator goes between hundreds (100s, 200s...).
/// </summary>
[ShellPart(Order = 60)]
public sealed class MenuBarPart : IShellPart
{
    public static MenuBarPart? Instance { get; private set; }
    /// <summary>Top menus in this order; any other top menu follows, alphabetically, before Help.</summary>
    private static readonly string[] s_known = ["File", "Edit", "View", "Select", "Brush", "Actor", "Build", "Tools", "Window"];

    private EditorContext _ctx = null!;
    private MenuBar _bar = null!;
    private readonly Dictionary<PopupMenu, List<string>> _ids = new();

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        _bar = new MenuBar { Name = "MenuBar", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PreferGlobalMenu = false };
        window.MenuRow.AddChild(_bar);
        window.MenuRow.MoveChild(_bar, 0);
        foreach (string top in TopMenus())
        {
            var popup = new PopupMenu { Name = top };
            _bar.AddChild(popup);
            popup.AboutToPopup += () => Fill(popup, top);
            popup.IdPressed += id => RunItem(popup, (int)id);
            MenuToggles.KeepOpen(popup, () => SyncChecks(popup));
            Fill(popup, top);
        }
        void WorkspaceMenus()
        {
            for (int i = 0; i < _bar.GetMenuCount(); i++)
                _bar.SetMenuHidden(i, WorkspaceMode.Simple && _bar.GetMenuTitle(i) is "Brush" or "Tools" or "Gameplay" or "Placement" or "Surface");
        }
        WorkspaceMode.Changed += WorkspaceMenus;
        WorkspaceMenus();
    }

    /// <summary>Open the actual owning menu and focus a command without invoking it.</summary>
    public bool RevealAction(EditorAction action)
    {
        string[] path = action.Menu.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length == 0) return false;
        if (WorkspaceMode.Simple && path[0] is "Brush" or "Tools" or "Gameplay" or "Placement" or "Surface") WorkspaceMode.Set(false);
        int top = Enumerable.Range(0, _bar.GetMenuCount()).FirstOrDefault(i => _bar.GetMenuTitle(i) == path[0], -1);
        if (top < 0) return false;
        PopupMenu popup = _bar.GetMenuPopup(top);
        Fill(popup, path[0]);
        // Show the leaf menu beside its menu-bar title. Opening it runs no map operation.
        for (int i = 1; i < path.Length; i++)
        {
            PopupMenu? child = popup.GetChildren().OfType<PopupMenu>().FirstOrDefault(p => p.Name == path[i]);
            if (child is null) return false;
            popup = child;
        }
        if (!_ids.TryGetValue(popup, out List<string>? ids) || ids.IndexOf(action.Id) is not (>= 0 and var index)) return false;
        Font font = _bar.GetThemeFont("font");
        int fontSize = _bar.GetThemeFontSize("font_size");
        float x = _bar.GlobalPosition.X;
        for (int i = 0; i < top; i++)
            if (!_bar.IsMenuHidden(i)) x += font.GetStringSize(_bar.GetMenuTitle(i), fontSize: fontSize).X
                + _bar.GetThemeStylebox("normal").GetMinimumSize().X + _bar.GetThemeConstant("h_separation");
        popup.Position = PanelUi.ScreenPoint(_bar, new Vector2(x - _bar.GlobalPosition.X, _bar.Size.Y));
        popup.Popup();
        popup.SetFocusedItem(index);
        return true;
    }

    private IEnumerable<string> TopMenus()
    {
        var tops = _ctx.Actions.Where(a => a.Menu.Length > 0).Select(a => a.Menu.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        foreach (string k in s_known.Where(tops.Contains))
        {
            yield return k;
        }

        foreach (string t in tops.Where(t => !s_known.Contains(t) && t != "Help").Order(StringComparer.Ordinal))
        {
            yield return t;
        }

        yield return "Help";
    }

    /// <summary>Rebuilds a menu (and its submenus) from the actions under <paramref name="path"/>.</summary>
    private void Fill(PopupMenu popup, string path)
    {
        foreach (Node child in popup.GetChildren())
        {
            if (child is PopupMenu sub)
            {
                _ids.Remove(sub);
                popup.RemoveChild(sub);
                sub.QueueFree();
            }
        }

        popup.Clear();
        var ids = new List<string>();
        _ids[popup] = ids;

        // Entries: the actions directly in this menu, and one entry per submenu (placed at its first action's order).
        var direct = _ctx.Actions.Where(a => a.Menu == path).Select(a => (Order: a.Order, Title: a.Title, Action: (EditorAction?)a, Sub: (string?)null));
        var subs = _ctx.Actions
            .Where(a => a.Menu.StartsWith(path + "/", StringComparison.Ordinal))
            .GroupBy(a => a.Menu[(path.Length + 1)..].Split('/')[0], StringComparer.Ordinal)
            .Select(g => (Order: g.Min(a => a.Order), Title: g.Key, Action: (EditorAction?)null, Sub: (string?)(path + "/" + g.Key)));
        var entries = direct.Concat(subs).OrderBy(e => e.Order).ThenBy(e => e.Title, StringComparer.Ordinal).ToList();

        int? group = null;
        foreach (var entry in entries)
        {
            int g = entry.Order / 100;
            if (group is not null && g != group)
            {
                popup.AddSeparator();
                ids.Add("");
            }

            group = g;
            if (entry.Sub is { } subPath)
            {
                var sub = new PopupMenu { Name = entry.Title };
                popup.AddChild(sub);
                sub.IdPressed += id => RunItem(sub, (int)id);
                MenuToggles.KeepOpen(sub, () => SyncChecks(sub));
                Fill(sub, subPath);
                popup.AddSubmenuNodeItem(entry.Title, sub);
                ids.Add("");
                continue;
            }

            EditorAction a = entry.Action!;
            int itemId = ids.Count;
            if (a.IsChecked is null)
            {
                popup.AddItem(a.Title, itemId);
            }
            else
            {
                // Exclusive choices (tools, render modes, layouts) are radio items; independent toggles check boxes.
                if (a.RadioGroup is not null || IsExclusive(a))
                {
                    popup.AddRadioCheckItem(a.Title, itemId);
                }
                else
                {
                    popup.AddCheckItem(a.Title, itemId);
                }

                popup.SetItemChecked(popup.GetItemIndex(itemId), a.IsChecked());
            }

            int index = popup.GetItemIndex(itemId);
            bool enabled = a.IsEnabled();
            popup.SetItemDisabled(index, !enabled);
            if (a.Description is { } help)
            {
                popup.SetItemTooltip(index, help);
            }

            // A disabled item shows no shortcut: a bright key beside a dimmed name reads as available.
            if (enabled && KeyChord.Parse(ShortcutBindings.Of(a)) is { } chord)
            {
                // Shown for reference only: the shortcut router runs shortcuts, so the menu's own copy stays disabled.
                popup.SetItemShortcut(index, new Godot.Shortcut { Events = [chord.ToEvent()] }, global: false);
                popup.SetItemShortcutDisabled(index, true);
            }

            ids.Add(a.Id);
        }

        if (entries.Count == 0)
        {
            popup.AddItem("(empty)", 0);
            popup.SetItemDisabled(0, true);
            ids.Add("");
        }
    }

    /// <summary>Menus whose checked items are one choice among several even where the action names no radio group
    /// (the tools, and every size, mode and layout list).</summary>
    private static readonly string[] s_exclusiveMenus = ["Tools", "View/Layout", "View/Render Mode", "View/Game Mode", "View/Grid Size",
        "View/Rotation Snap Size", "View/UI Scale"];

    private static bool IsExclusive(EditorAction a) => s_exclusiveMenus.Contains(a.Menu, StringComparer.Ordinal) && a.Id != "view.grid.snap";

    private void SyncChecks(PopupMenu popup)
    {
        if (_ids.TryGetValue(popup, out List<string>? ids))
        {
            MenuToggles.SyncActions(popup, ids, _ctx);
        }
    }

    private void RunItem(PopupMenu popup, int id)
    {
        if (_ids.TryGetValue(popup, out List<string>? ids) && id >= 0 && id < ids.Count && ids[id].Length > 0)
        {
            _ctx.Run(ids[id]);
        }
    }
}
