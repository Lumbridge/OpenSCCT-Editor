using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;

namespace OpenScctEditor.Shell.Shots;

/// <summary>
/// Cheap automatic checks of what is on screen, run with every screenshot: clipped or cut-off text (labels, buttons,
/// a text box's placeholder, list and tree cells), tab bars that hide tabs behind scroll arrows, controls sticking out
/// of their box or the window, buttons with nothing on them, and characters the UI font does not have (they fall back
/// to another typeface or draw as boxes). Findings go into the manifest and the contact sheet, so a regression shows
/// without anyone looking at every picture. A label or button shortened with an ellipsis whose tooltip holds the whole
/// text is a choice, not a finding.
/// </summary>
public static class UiLint
{
    public static SvJsonArray Run(Window root)
    {
        var findings = new SvJsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // An overflow is reported once, on the outermost control: everything inside it overflows too.
        var overflowing = new HashSet<Node>();
        Rect2 window = root.GetVisibleRect();

        void Add(string code, Control c, string message)
        {
            string path = UiTree.PathOf(root, c);
            if (seen.Add(code + "|" + path))
            {
                findings.Add(ApiJson.Obj(("code", ApiJson.Str(code)), ("path", ApiJson.Str(path)), ("rect", UiTree.RectJson(UiTree.RectInRoot(c))),
                    ("message", ApiJson.Str(message))));
            }
        }

        foreach (Control c in UiTree.Visible(root))
        {
            Rect2 r = UiTree.RectInRoot(c);
            if (r.Size.X < 1 || r.Size.Y < 1)
            {
                continue;
            }

            if (UiTree.TextOf(c) is { Length: > 0 } text)
            {
                CheckText(c, text, Add);
            }

            switch (c)
            {
                // A tab container with a tab list menu (the docks) keeps every tab one click away.
                case TabContainer tc when tc.GetPopup() is null && tc.GetTabBar().GetOffsetButtonsVisible():
                    Add("tabs-overflow", c, $"{tc.GetTabCount()} tabs do not fit; some hide behind the scroll arrows");
                    break;
                case TabBar tb when tb.GetParent() is not TabContainer && tb.GetOffsetButtonsVisible():
                    Add("tabs-overflow", c, $"{tb.TabCount} tabs do not fit; some hide behind the scroll arrows");
                    break;
                case Button b when string.IsNullOrEmpty(b.Text) && b.Icon is null && b is not CheckBox and not CheckButton and not ColorPickerButton:
                    Add("empty-button", c, "a button with neither text nor icon");
                    break;
                case Tree tree:
                    CheckTree(tree, Add);
                    break;
            }

            // A window-level control must stay inside the window; inside a box, it must stay inside the box (scroll
            // areas excepted: their content is meant to be larger).
            if (c.GetParent() is { } up && overflowing.Contains(up))
            {
                overflowing.Add(c);
            }
            else if (c.GetViewport() == root && !InsideScroll(c) && !window.Grow(1).Encloses(r))
            {
                overflowing.Add(c);
                Add("outside-window", c, $"reaches outside the window ({Describe(r)} in {Describe(window)})");
            }
            else if (c.GetParent() is Container parent and not ScrollContainer && parent.IsVisibleInTree() &&
                     !UiTree.RectInRoot(parent).Grow(2).Encloses(r))
            {
                overflowing.Add(c);
                Add("overflows-parent", c, $"sticks out of its {parent.GetClass()} ({Describe(r)} in {Describe(UiTree.RectInRoot(parent))})");
            }
        }

        return findings;
    }

    /// <summary>Scroll content is meant to be larger than what shows; the scroll area itself is checked instead.</summary>
    private static bool InsideScroll(Control c)
    {
        for (Node? n = c.GetParent(); n is not null; n = n.GetParent())
        {
            if (n is ScrollContainer)
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckText(Control c, string text, Action<string, Control, string> add)
    {
        Font font = c.GetThemeFont("font");
        int size = c.GetThemeFontSize("font_size");
        string missing = new(text.Where(ch => !char.IsWhiteSpace(ch) && !font.HasChar(ch)).Distinct().ToArray());
        if (missing.Length > 0)
        {
            add("missing-glyph", c, $"the UI font has no {string.Join(" ", missing.Select(ch => $"'{ch}' (U+{(int)ch:X4})"))}");
        }

        // Only the first line: multi-line labels wrap or grow on purpose.
        string line = text.Split('\n')[0];
        float needed = font.GetStringSize(line, HorizontalAlignment.Left, -1, size).X;
        float room = c switch
        {
            Label l when l.AutowrapMode == TextServer.AutowrapMode.Off && (l.ClipText || l.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming)
                => l.Size.X - l.GetThemeStylebox("normal").GetMinimumSize().X,
            Button b when b.ClipText || b.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming
                => b.Size.X - b.GetThemeStylebox("normal").GetMinimumSize().X - IconRoom(b),
            // A placeholder is cut at the box's edge; typed text scrolls, so only the placeholder is checked.
            LineEdit e when e.Text.Length == 0
                => e.Size.X - e.GetThemeStylebox("normal").GetMinimumSize().X - (e.RightIcon?.GetWidth() ?? 0) -
                   (e.ClearButtonEnabled ? e.GetThemeIcon("clear").GetWidth() : 0),
            _ => float.PositiveInfinity,
        };
        // Shortened with an ellipsis and readable in full on hover: a choice, not a fault.
        bool readable = c is Label { TextOverrunBehavior: TextServer.OverrunBehavior.TrimEllipsis or TextServer.OverrunBehavior.TrimWordEllipsis }
                            or Button { TextOverrunBehavior: TextServer.OverrunBehavior.TrimEllipsis or TextServer.OverrunBehavior.TrimWordEllipsis } &&
                        c.TooltipText.Contains(line, StringComparison.Ordinal);
        if (needed > room + 1 && !readable)
        {
            add("clipped-text", c, $"'{Short(line)}' needs {needed:0} px but has {room:0} px");
        }
    }

    /// <summary>Cells of the rows on screen whose text does not fit their column (Tree trims it with an ellipsis), one
    /// finding per column.</summary>
    private static void CheckTree(Tree tree, Action<string, Control, string> add)
    {
        Font font = tree.GetThemeFont("font");
        int size = tree.GetThemeFontSize("font_size");
        int margin = tree.GetThemeConstant("item_margin");
        int gap = tree.GetThemeConstant("h_separation");
        var cut = new int[tree.Columns];
        var rows = new int[tree.Columns];
        var example = new string?[tree.Columns];
        TreeItem? item = tree.GetRoot();
        if (tree.HideRoot)
        {
            item = item?.GetNextVisible();
        }

        for (int shown = 0; item is not null && shown < 300; item = item.GetNextVisible(), shown++)
        {
            Rect2 rowRect = tree.GetItemAreaRect(item, 0);
            if (rowRect.Position.Y > tree.Size.Y)
            {
                break;
            }

            for (int col = 0; col < tree.Columns; col++)
            {
                string text = item.GetText(col);
                // A wrapping cell grows taller instead of cutting its text.
                if (text.Length == 0 || item.GetAutowrapMode(col) != TextServer.AutowrapMode.Off)
                {
                    continue;
                }

                float used = col == 0 ? margin * Depth(tree, item) : 0;
                if (item.GetIcon(col) is { } icon)
                {
                    int max = item.GetIconMaxWidth(col);
                    used += (max > 0 ? Math.Min(icon.GetWidth(), max) : icon.GetWidth()) + gap;
                }

                for (int b = 0; b < item.GetButtonCount(col); b++)
                {
                    used += tree.GetItemAreaRect(item, col, b).Size.X + gap;
                }

                float needed = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
                rows[col]++;
                if (needed > tree.GetColumnWidth(col) - used - gap + 1)
                {
                    cut[col]++;
                    example[col] ??= text;
                }
            }
        }

        for (int col = 0; col < tree.Columns; col++)
        {
            if (cut[col] > 0)
            {
                add("clipped-cell", tree, $"{cut[col]} of {rows[col]} rows on screen do not fit column {col + 1} " +
                    $"({tree.GetColumnWidth(col)} px), e.g. '{Short(example[col]!)}'");
            }
        }
    }

    /// <summary>How deep a row is indented (the hidden root's children are not).</summary>
    private static int Depth(Tree tree, TreeItem item)
    {
        int depth = 0;
        for (TreeItem? p = item.GetParent(); p is not null; p = p.GetParent())
        {
            depth++;
        }

        return tree.HideRoot ? depth - 1 : depth;
    }

    private static float IconRoom(Button b) =>
        b.Icon is null ? 0 : b.Icon.GetWidth() + (string.IsNullOrEmpty(b.Text) ? 0 : b.GetThemeConstant("h_separation"));

    private static string Short(string s) => s.Length <= 40 ? s : s[..37] + "...";

    private static string Describe(Rect2 r) => $"{r.Position.X:0},{r.Position.Y:0} {r.Size.X:0}x{r.Size.Y:0}";
}
