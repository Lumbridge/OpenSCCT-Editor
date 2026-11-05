using Godot;

namespace OpenScctEditor.Shell;

/// <summary>Theme styles only the panels area needs (see docs/UI-DESIGN.md, "Rules for all areas"). Shared tokens and
/// variations live in EditorTheme.cs.</summary>
public static partial class EditorTheme
{
    /// <summary>The variation of the outliner and layer trees: <c>OutlinerTree</c> paints the panel, the selected rows
    /// and the row under the pointer behind the cells as one band per row, so the tree's own panel, selection and hover
    /// styles are off (the panel keeps its content margins).</summary>
    public const string RowTree = "RowTree";

    /// <summary>The <see cref="RowTree"/> stylebox <c>OutlinerTree</c> draws as the tree's panel.</summary>
    public const string RowTreePanel = "row_panel";

    /// <summary>The <see cref="RowTree"/> stylebox <c>OutlinerTree</c> draws behind a selected row.</summary>
    public const string RowTreeSelected = "row_selected";

    /// <summary>A menu button that looks like a command button (Add mode, an outfit's Pick menu); set
    /// <c>Flat = false</c> on it. A MenuButton given the plain Button variation keeps the flat MenuButton styles.</summary>
    public const string CommandMenu = "CommandMenu";

    static partial void PanelStyles(Theme t)
    {
        t.SetTypeVariation(RowTree, "Tree");
        StyleBox panel = t.GetStylebox("panel", "Tree");
        var bare = new StyleBoxEmpty();
        foreach (Side side in new[] { Side.Left, Side.Top, Side.Right, Side.Bottom })
        {
            bare.SetContentMargin(side, panel.GetMargin(side));
        }

        t.SetStylebox("panel", RowTree, bare);
        t.SetStylebox(RowTreePanel, RowTree, panel);
        t.SetStylebox(RowTreeSelected, RowTree, t.GetStylebox("selected", "Tree"));
        var none = new StyleBoxEmpty();
        foreach (string style in new[] { "selected", "selected_focus", "hovered", "hovered_dimmed", "hovered_selected", "hovered_selected_focus" })
        {
            t.SetStylebox(style, RowTree, none);
        }

        t.SetTypeVariation(CommandMenu, "MenuButton");
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled" })
        {
            t.SetStylebox(state, CommandMenu, t.GetStylebox(state, "Button"));
        }

        t.SetColor("font_hovered_color", RowTree, Text);
        t.SetColor("font_hovered_selected_color", RowTree, TextBright);
    }
}
