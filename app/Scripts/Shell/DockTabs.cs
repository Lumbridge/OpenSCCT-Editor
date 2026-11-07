using Godot;
using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>
/// The tab row of a dock: every panel has an icon, and when the full titles do not fit, the tabs other than the open
/// one show their icon only (the title moves to the tooltip). What still does not fit leaves the row and is reached
/// through the dock's overflow (⋯) menu. A tab's panel is found by its node name (the panel's title), which never
/// changes.
/// </summary>
public static class DockTabs
{
    /// <summary>Each panel's tab icon, by panel title. A panel missing here keeps its title in every layout.</summary>
    private static readonly Dictionary<string, string> s_icons = new(StringComparer.Ordinal)
    {
        ["Outliner"] = "outliner",
        ["Building"] = "grid",
        ["Your first map"] = "check-list",
        ["Layers"] = "type-layer",
        ["Classes"] = "classes",
        ["Prefabs"] = "grid",
        ["Storeys"] = "storeys",
        ["Emitter Library"] = "type-emitter",
        ["Inspector"] = "inspector",
        ["Map"] = "map",
        ["Identity"] = "id-card",
        ["Surface"] = "surface",
        ["Bookmarks"] = "camera-bookmark",
        ["Map Check"] = "check-list",
        ["Log"] = "log",
        ["History"] = "history",
        ["Assets"] = "folder",
        ["Placeables"] = "palette",
        ["Geometry"] = "grid",
        ["Lights"] = "type-light",
        ["Windows"] = "type-glass",
        ["Connections"] = "link",
        ["Event Sequencer"] = "type-event",
        ["Missions"] = "type-objective",
        ["Mover"] = "type-mover",
        ["Match"] = "type-start",
        ["Camera Network"] = "type-camera",
        ["Properties"] = "property-sets",
        ["Tips"] = "type-tip",
        ["References"] = "image",
        ["Traversal"] = "traversal",
    };

    /// <summary>The panel title of tab <paramref name="index"/> (its node name), whatever the tab shows.</summary>
    public static string Name(TabContainer dock, int index) => index < 0 || index >= dock.GetTabCount() ? "" : dock.GetTabControl(index)?.Name ?? dock.GetTabTitle(index);

    /// <summary>Keeps a dock's tab row fitted from now on: after resizes, tab changes and panels coming or going.</summary>
    public static void Track(TabContainer dock)
    {
        // The tab bar takes its width after the dock's own resize, so the fit follows the bar.
        dock.GetTabBar().Resized += () => Fit(dock);
        dock.TabChanged += _ => Fit(dock);
        // A regular button provides the same keyboard focus, tooltip and synthetic-input behavior as other controls.
        // The tab's button icon reserves its space; this control sits over that icon.
        var close = new Button { Name = "CloseTutorial", Flat = true, Icon = Icons.Get("close", EditorTheme.TextDim),
            TooltipText = "Close tutorial. Reopen from Help > Your First Map.", Visible = false };
        foreach (string style in new[] { "normal", "hover", "pressed" }) close.AddThemeStyleboxOverride(style, new StyleBoxEmpty());
        close.Pressed += () => EditorLifetime.Defer(() => EditorContext.Instance.UpdateGuide(g => g.Hidden = true));
        dock.GetTabBar().AddChild(close);
        EditorContext.Instance.GuideChanged += () => Fit(dock);
        EditorContext.Instance.SessionReplaced += () => Fit(dock);
        dock.ChildOrderChanged += () => EditorLifetime.Defer(() => Fit(dock));
    }

    /// <summary>Shows tab <paramref name="index"/> of a dock, also one the tab row has no room for (see
    /// <see cref="Fit"/>): the dock's overflow menu and scripts open panels through this.</summary>
    public static void Show(TabContainer dock, int index)
    {
        if (Name(dock, index) == "Your first map" && EditorContext.Instance.Project.Workspace.FirstMap.Hidden)
            EditorContext.Instance.UpdateGuide(g => g.Hidden = false);
        dock.SetTabHidden(index, false);
        dock.CurrentTab = index;
        MainWindow.Instance?.RevealWorkspaceDock(dock);
    }

    /// <summary>Brings a docked panel to the front of its dock (and of any dock around that), wherever the layout put
    /// it: a panel sits in a <see cref="PanelHost"/>, not straight in the dock.</summary>
    public static void Reveal(Control panel)
    {
        Node child = panel;
        for (Node? parent = panel.GetParent(); parent is not null; child = parent, parent = parent.GetParent())
        {
            if (parent is TabContainer dock && child is Control tab && dock.GetTabIdxFromControl(tab) is >= 0 and var index)
            {
                Show(dock, index);
            }
        }
    }

    /// <summary>Shows the panel titled <paramref name="panel"/> in whichever dock holds it; false when no dock does.</summary>
    public static bool ShowPanel(string panel)
    {
        if (!WorkspaceMode.Allows(panel)) WorkspaceMode.Set(false);
        foreach (TabContainer dock in MainWindow.Instance?.Docks ?? [])
        {
            for (int i = 0; i < dock.GetTabCount(); i++)
            {
                if (string.Equals(Name(dock, i), panel, StringComparison.OrdinalIgnoreCase))
                {
                    Show(dock, i);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Lays out a dock's tab row by one rule in every dock: every tab shows its icon and title when they all
    /// fit; otherwise the open tab keeps its icon and title and the others show their icon only, with their title in
    /// the tooltip. Tabs that still do not fit leave the row from its end (never the open one) and are reached through
    /// the dock's overflow (⋯) menu, so the row never scrolls. A dock whose panels have no icons shows
    /// titles.</summary>
    public static void Fit(TabContainer dock)
    {
        if (!GodotObject.IsInstanceValid(dock) || dock.GetTabCount() == 0)
        {
            return;
        }
        bool Available(int i) => WorkspaceMode.Allows(Name(dock, i)) &&
            (Name(dock, i) != "Your first map" || !EditorContext.Instance.Project.Workspace.FirstMap.Hidden);
        int firstAllowed = Enumerable.Range(0, dock.GetTabCount()).FirstOrDefault(Available, -1);
        if (firstAllowed < 0) return; // The whole dock is hidden; do not deselect its last tab.
        if (dock.CurrentTab < 0 || !Available(dock.CurrentTab))
        {
            dock.SetTabHidden(firstAllowed, false);
            dock.CurrentTab = firstAllowed;
        }

        Font font = dock.GetThemeFont("font");
        int fontSize = dock.GetThemeFontSize("font_size");
        float chrome = dock.GetThemeStylebox("tab_unselected").GetMinimumSize().X;
        float separation = dock.GetThemeConstant("icon_separation");
        // The tab bar's own width: the dock less its overflow button and the bar's margins.
        float room = dock.GetTabBar().Size.X;
        int count = dock.GetTabCount();
        int open = dock.CurrentTab;
        var titles = new float[count];
        bool allIcons = true;
        for (int i = 0; i < count; i++)
        {
            string name = Name(dock, i);
            dock.SetTabButtonIcon(i, name == "Your first map" ? Icons.Get("close", new Color(1, 1, 1, 0)) : null);
            if (!Available(i)) continue;
            titles[i] = font.GetStringSize(name, fontSize: fontSize).X;
            if (name == "Your first map") titles[i] += EditorTheme.IconSize + separation;
            allIcons &= s_icons.ContainsKey(name);
        }

        bool full = allIcons
            ? Enumerable.Range(0, count).Where(Available).Sum(i => chrome + EditorTheme.IconSize + separation + titles[i]) <= room
            : Enumerable.Range(0, count).Where(Available).Sum(i => chrome + titles[i]) <= room;

        // The open tab's title and the other tabs' icons, from the first tab on, while they fit.
        // A dock too narrow for even the open tab's title shows that tab's icon too.
        float used = !allIcons || full || open < 0 ? 0 : chrome + EditorTheme.IconSize + separation + titles[open];
        bool openTitle = used <= room;
        if (!openTitle)
        {
            used = chrome + EditorTheme.IconSize;
        }

        for (int i = 0; i < count; i++)
        {
            string name = Name(dock, i);
            if (!Available(i))
            {
                dock.SetTabHidden(i, true);
                continue;
            }
            bool fits = true;
            if (allIcons && !full && i != open)
            {
                used += chrome + EditorTheme.IconSize;
                if (name == "Your first map") used += EditorTheme.IconSize + separation;
                fits = used <= room;
            }

            if (dock.IsTabHidden(i) == fits)
            {
                dock.SetTabHidden(i, !fits);
            }

            Texture2D? icon = allIcons && s_icons.TryGetValue(name, out string? key) ? Icons.Get(key, EditorTheme.TextDim) : null;
            bool title = icon is null || full || (i == open && openTitle);
            dock.SetTabIcon(i, icon);
            dock.SetTabTitle(i, title ? name : "");
            dock.SetTabTooltip(i, name == "Your first map" ? "Your first map · Close with ×; reopen from Help > Your First Map" : title ? "" : name);
        }

        // The estimate above can be a few pixels short (the open tab's box, icon rounding); the bar itself has the
        // last word, so tabs it cannot draw also leave the row from its end.
        TabBar bar = dock.GetTabBar();
        for (int i = count - 1; i >= 0 && bar.GetOffsetButtonsVisible(); i--)
        {
            if (i != open && !dock.IsTabHidden(i))
            {
                dock.SetTabHidden(i, true);
            }
        }
        if (bar.GetNodeOrNull<Button>("CloseTutorial") is { } close)
        {
            int guide = Enumerable.Range(0, count).FirstOrDefault(i => Name(dock, i) == "Your first map", -1);
            close.Visible = guide >= 0 && !dock.IsTabHidden(guide);
            if (close.Visible)
            {
                Rect2 tab = bar.GetTabRect(guide);
                float width = EditorTheme.IconSize + separation * 2;
                float right = dock.GetThemeStylebox(guide == open ? "tab_selected" : "tab_unselected").GetMargin(Side.Right);
                close.Position = new Vector2(tab.End.X - right - width, tab.Position.Y);
                close.Size = new Vector2(width, tab.Size.Y);
            }
        }
    }
}
