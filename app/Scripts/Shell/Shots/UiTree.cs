using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell.Shots;

/// <summary>
/// The editor's visible controls as a script or a screenshot run sees them: paths, kinds, rectangles in the main
/// window's canvas coordinates, texts and states. Also turns a target ("action:tools.move", "tab:Inspector",
/// "menu:View", "view:Top", "text:Apply" or a node path) into the rectangle to click.
/// </summary>
public static class UiTree
{
    /// <summary>Every visible control under the root window (embedded popups included), parents first.</summary>
    public static IEnumerable<Control> Visible(Window root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (n is Window w && w != root && !(w.Visible && w.IsEmbedded()))
            {
                continue;
            }

            if (n is CanvasItem item && !item.IsVisibleInTree())
            {
                continue;
            }

            if (n is Control c)
            {
                yield return c;
            }

            // Views render their own scene; nothing inside a SubViewport is part of the editor's UI.
            if (n is SubViewport)
            {
                continue;
            }

            for (int i = n.GetChildCount() - 1; i >= 0; i--)
            {
                stack.Push(n.GetChild(i));
            }
        }
    }

    /// <summary>A control's rectangle in the root window's canvas coordinates (through embedded popups).</summary>
    public static Rect2 RectInRoot(Control c)
    {
        Rect2 r = c.GetGlobalRect();
        Viewport? vp = c.GetViewport();
        while (vp is Window w && w.IsEmbedded())
        {
            r.Position += w.Position;
            vp = w.GetParent()?.GetViewport();
        }

        return r;
    }

    /// <summary>The path scripts use to name a control (relative to the root window).</summary>
    public static string PathOf(Window root, Node n) => root.GetPathTo(n).ToString();

    /// <summary>The visible text of a control, if it shows one.</summary>
    public static string? TextOf(Control c) => c switch
    {
        Label l => l.Text,
        Button b => b.Text,
        SpinBox spin => spin.GetLineEdit().Text,
        LineEdit e => e.Text.Length > 0 ? e.Text : e.PlaceholderText,
        _ => null,
    };

    public static SvJsonObject Describe(Window root, Control c)
    {
        Rect2 r = RectInRoot(c);
        string? text = TextOf(c);
        return ApiJson.Obj(
            ("path", ApiJson.Str(PathOf(root, c))),
            ("type", ApiJson.Str(c.GetClass())),
            ("rect", RectJson(r)),
            ("text", string.IsNullOrEmpty(text) ? null : ApiJson.Str(text)),
            ("tooltip", string.IsNullOrEmpty(c.TooltipText) ? null : ApiJson.Str(c.TooltipText)),
            ("action", DiscoveryNavigation.ActionId(c) is { } actionId ? ApiJson.Str(actionId) : null),
            ("highlightTarget", c is NavigationHighlight highlight && highlight.TargetPath is { } target ? ApiJson.Str(target) : null),
            ("highlightRect", c is NavigationHighlight { Active: true } rectHighlight ? RectJson(rectHighlight.TargetRect) : null),
            ("tutorial", c is TutorialRunner { LessonId: { } lessonId } ? ApiJson.Str(lessonId) : null),
            ("tutorialStep", c is TutorialRunner tutorialStep ? SvJsonNode.Integer(tutorialStep.StepIndex) : null),
            ("paletteSelected", c is CommandPalette { SelectedResultKey: { } selectedKey } ? ApiJson.Str(selectedKey) : null),
            ("paletteResults", c is CommandPalette palette ? ApiJson.Arr(palette.Results.Select(e => ApiJson.Obj(
                ("key", ApiJson.Str(e.Key)), ("title", ApiJson.Str(e.Title)), ("kind", ApiJson.Str(e.Kind)), ("enabled", ApiJson.Bool(e.Available))))) : null),
            ("pressed", c is BaseButton { ToggleMode: true } t ? ApiJson.Bool(t.ButtonPressed) : null),
            ("disabled", c is BaseButton { Disabled: true } ? ApiJson.Bool(true) : null),
            ("focused", c.HasFocus() ? ApiJson.Bool(true) : null),
            ("tabs", c is TabContainer tc ? ApiJson.Strings(Enumerable.Range(0, tc.GetTabCount()).Select(i => DockTabs.Name(tc, i))) : null),
            ("currentTab", c is TabContainer tc2 ? ApiJson.Str(DockTabs.Name(tc2, tc2.CurrentTab)) : null));
    }

    /// <summary>The visible controls under <paramref name="under"/> (a path; the whole window when null), at most
    /// <paramref name="limit"/>. Bare layout boxes are left out unless <paramref name="all"/>.</summary>
    public static SvJsonObject Dump(Window root, string? under, bool all, int limit)
    {
        Node start = under is null ? root : root.GetNodeOrNull(under) ?? throw new ApiException($"no node at '{under}'", "under");
        var list = new SvJsonArray();
        int count = 0;
        foreach (Control c in Visible(root))
        {
            if (start != root && c != start && !start.IsAncestorOf(c))
            {
                continue;
            }

            if (!all && IsBareLayout(c))
            {
                continue;
            }

            if (++count > limit)
            {
                break;
            }

            list.Add(Describe(root, c));
        }

        var popups = new SvJsonArray();
        foreach (Window w in root.GetEmbeddedSubwindows())
        {
            if (w.Visible)
            {
                popups.Add(ApiJson.Obj(("path", ApiJson.Str(PathOf(root, w))), ("type", ApiJson.Str(w.GetClass())),
                    ("rect", RectJson(new Rect2(w.Position, w.Size))), ("items", w is PopupMenu pm ? MenuItems(pm) : null)));
            }
        }

        return ApiJson.Obj(
            ("window", ApiJson.Arr([SvJsonNode.Integer(root.Size.X), SvJsonNode.Integer(root.Size.Y)])),
            ("scale", ApiJson.Num(root.ContentScaleFactor)),
            ("canvas", ApiJson.Arr([ApiJson.Num(root.GetVisibleRect().Size.X), ApiJson.Num(root.GetVisibleRect().Size.Y)])),
            ("focus", root.GuiGetFocusOwner() is { } f ? ApiJson.Str(PathOf(root, f)) : null),
            ("hovered", root.GuiGetHoveredControl() is { } h ? ApiJson.Str(PathOf(root, h)) : null),
            ("popups", popups),
            ("controls", list),
            ("truncated", count > limit ? ApiJson.Bool(true) : null));
    }

    /// <summary>Resolves a target to a rectangle in canvas coordinates. Forms: <c>[x, y]</c>, <c>action:&lt;id&gt;</c>
    /// (a toolbar button), <c>tab:&lt;panel&gt;</c> (a dock tab), <c>dock:&lt;panel&gt;</c> (the dock holding that panel,
    /// wherever the layout has put it), <c>menu:&lt;title&gt;</c> (a menu bar title),
    /// <c>item:&lt;text&gt;</c> (an item of the open popup menu), <c>view:&lt;kind&gt;</c> (a viewport),
    /// <c>text:&lt;text&gt;</c> (a control showing that text) or a node path.</summary>
    public static Rect2 Resolve(Window root, SvJsonNode target)
    {
        if (target is SvJsonArray { Count: 2 } xy)
        {
            var p = new Vector2((float)ApiJson.RequireNumber(xy[0], "at"), (float)ApiJson.RequireNumber(xy[1], "at"));
            return new Rect2(p, Vector2.Zero);
        }

        if (target is not SvJsonString { Value: var s })
        {
            throw new ApiException("a target is [x, y] or a string such as \"action:tools.move\" or \"tab:Inspector\"", "at");
        }

        int colon = s.IndexOf(':', StringComparison.Ordinal);
        string kind = colon > 0 ? s[..colon] : "";
        string value = colon > 0 ? s[(colon + 1)..] : s;
        Rect2? found = kind switch
        {
            "action" => Visible(root).FirstOrDefault(b => DiscoveryNavigation.ActionId(b) == value) is { } b ? RectInRoot(b) : null,
            "tab" => TabRect(root, value),
            "dock" => DockOf(root, value) is { } dock ? RectInRoot(dock) : null,
            "menu" => MenuTitleRect(root, value),
            "item" => MenuItemRect(root, value),
            "view" => ViewportLayout.Instance?.Views.FirstOrDefault(v => v.IsVisibleInTree() &&
                string.Equals(v.Kind.ToString(), value, StringComparison.OrdinalIgnoreCase)) is { } v ? RectInRoot(v) : null,
            "text" => Visible(root).LastOrDefault(c => TextOf(c) is { } t && t.Trim() == value) is { } c ? RectInRoot(c) : null,
            _ => root.GetNodeOrNull<Control>(s) is { } n && n.IsVisibleInTree() ? RectInRoot(n) : null,
        };
        return found ?? throw new ApiException($"nothing visible matches '{s}'", "at");
    }

    public static SvJsonArray RectJson(Rect2 r) => ApiJson.Arr([ApiJson.Num(Math.Round(r.Position.X, 1)), ApiJson.Num(Math.Round(r.Position.Y, 1)),
        ApiJson.Num(Math.Round(r.Size.X, 1)), ApiJson.Num(Math.Round(r.Size.Y, 1))]);

    private static bool IsBareLayout(Control c) =>
        c.GetType() == typeof(Control) || (c is BoxContainer or MarginContainer or ScrollBar or SplitContainer && c.TooltipText.Length == 0);

    private static TabContainer? DockOf(Window root, string title) =>
        Visible(root).OfType<TabContainer>().FirstOrDefault(tc =>
            Enumerable.Range(0, tc.GetTabCount()).Any(i => string.Equals(DockTabs.Name(tc, i), title, StringComparison.OrdinalIgnoreCase)));

    private static Rect2? TabRect(Window root, string title)
    {
        foreach (TabContainer tc in Visible(root).OfType<TabContainer>())
        {
            for (int i = 0; i < tc.GetTabCount(); i++)
            {
                if (string.Equals(DockTabs.Name(tc, i), title, StringComparison.OrdinalIgnoreCase))
                {
                    TabBar bar = tc.GetTabBar();
                    Rect2 tab = bar.GetTabRect(i);
                    return new Rect2(RectInRoot(bar).Position + tab.Position, tab.Size);
                }
            }
        }

        // A panel's own tab bar (the Assets panel's kinds).
        foreach (TabBar bar in Visible(root).OfType<TabBar>().Where(b => b.GetParent() is not TabContainer))
        {
            for (int i = 0; i < bar.TabCount; i++)
            {
                if (string.Equals(bar.GetTabTitle(i), title, StringComparison.OrdinalIgnoreCase))
                {
                    Rect2 tab = bar.GetTabRect(i);
                    return new Rect2(RectInRoot(bar).Position + tab.Position, tab.Size);
                }
            }
        }

        return null;
    }

    /// <summary>A menu bar title's rectangle. Godot does not expose it, so it is measured the way the bar lays its
    /// titles out: each title's text plus the normal style's margins, then the theme separation.</summary>
    private static Rect2? MenuTitleRect(Window root, string title)
    {
        if (Visible(root).OfType<MenuBar>().FirstOrDefault() is not { } bar)
        {
            return null;
        }

        Font font = bar.GetThemeFont("font");
        int size = bar.GetThemeFontSize("font_size");
        Vector2 margins = bar.GetThemeStylebox("normal").GetMinimumSize();
        int gap = bar.GetThemeConstant("h_separation");
        float x = 0;
        Rect2 barRect = RectInRoot(bar);
        for (int i = 0; i < bar.GetMenuCount(); i++)
        {
            if (bar.IsMenuHidden(i))
            {
                continue;
            }

            string t = bar.GetMenuTitle(i);
            float w = font.GetStringSize(t, HorizontalAlignment.Left, -1, size).X + margins.X;
            if (string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
            {
                return new Rect2(barRect.Position + new Vector2(x, 0), new Vector2(w, barRect.Size.Y));
            }

            x += w + gap;
        }

        return null;
    }

    /// <summary>An item of the topmost open popup menu, found by its text (the rows are measured from the menu's
    /// height, which Godot gives every row equally apart from separators).</summary>
    private static Rect2? MenuItemRect(Window root, string text)
    {
        PopupMenu? menu = root.GetEmbeddedSubwindows().OfType<PopupMenu>().LastOrDefault(m => m.Visible);
        if (menu is null)
        {
            return null;
        }

        int index = Enumerable.Range(0, menu.ItemCount).FirstOrDefault(i => string.Equals(menu.GetItemText(i).Trim(), text, StringComparison.OrdinalIgnoreCase), -1);
        if (index < 0)
        {
            return null;
        }

        // Rows: separators are thinner, so step through by measuring with the theme's sizes.
        Font font = menu.GetThemeFont("font");
        int fontSize = menu.GetThemeFontSize("font_size");
        float rowH = font.GetHeight(fontSize) + menu.GetThemeConstant("v_separation");
        float sepH = Math.Max(menu.GetThemeStylebox("separator").GetMinimumSize().Y, menu.GetThemeConstant("v_separation"));
        StyleBox panel = menu.GetThemeStylebox("panel");
        float y = panel.GetMargin(Side.Top);
        for (int i = 0; i < index; i++)
        {
            y += menu.IsItemSeparator(i) ? sepH : rowH;
        }

        return new Rect2(menu.Position + new Vector2(panel.GetMargin(Side.Left), y), new Vector2(menu.Size.X - panel.GetMinimumSize().X, rowH));
    }

    private static SvJsonArray MenuItems(PopupMenu m) =>
        ApiJson.Arr(Enumerable.Range(0, m.ItemCount).Select(i => (SvJsonNode)ApiJson.Obj(
            ("text", ApiJson.Str(m.IsItemSeparator(i) ? "---" : m.GetItemText(i))),
            ("disabled", m.IsItemDisabled(i) ? ApiJson.Bool(true) : null),
            ("checked", m.IsItemCheckable(i) || m.IsItemRadioCheckable(i) ? ApiJson.Bool(m.IsItemChecked(i)) : null),
            ("shortcut", m.GetItemShortcut(i) is { } sc && sc.GetAsText() is { Length: > 0 } st && st != "None" ? ApiJson.Str(st) : null))));
}
