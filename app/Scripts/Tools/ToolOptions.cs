using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Tools;

/// <summary>
/// Builders for a tool's part of the options row, so every tool lays it out alike: its controls first (boxed command
/// buttons, chips for switches, drop-downs that never widen the row), then a divider and the hint as one dim caption
/// at the end, in sentence case with its parts joined by " · " (docs/UI-DESIGN.md, "Toolbars").
/// </summary>
internal static class ToolOptions
{
    /// <summary>The " · " that joins the parts of a hint.</summary>
    public const string Separator = " · ";

    /// <summary>A command button that runs an editor action; its tooltip names the action, its key and what it does.</summary>
    public static Button Action(HBoxContainer bar, EditorContext ctx, string actionId, string text)
    {
        Button b = PanelUi.Button(bar, text, Tooltip(ctx, actionId, text), () => ctx.Run(actionId));
        return b;
    }

    /// <summary>A command button.</summary>
    public static Button Command(HBoxContainer bar, string text, string tooltip, Action run) => PanelUi.Button(bar, text, tooltip, run);

    /// <summary>A dim label naming the control after it ("Class", "GE").</summary>
    public static Label Name(HBoxContainer bar, string text)
    {
        var l = new Label { Text = text, ThemeTypeVariation = "Dim" };
        bar.AddChild(l);
        return l;
    }

    /// <summary>A drop-down at least <paramref name="minWidth"/> wide that never widens the row (long items end in "…").</summary>
    public static OptionButton Options(HBoxContainer bar, string tooltip, int minWidth)
    {
        OptionButton o = PanelUi.Options(bar, tooltip);
        o.CustomMinimumSize = new Vector2(minWidth, 0);
        return o;
    }

    /// <summary>The hint at the end of the row: a divider (when the tool has controls), then the parts joined by " · " in
    /// one dim caption that ends in "…" when the row is short of room (the tooltip lists the parts one per line).</summary>
    public static Label Hint(HBoxContainer bar, params string[] parts)
    {
        if (bar.GetChildCount() > 0)
        {
            bar.AddChild(new VSeparator());
        }

        Label hint = PanelUi.Caption(bar, string.Join(Separator, parts), wrap: false);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.TooltipText = string.Join('\n', parts);
        hint.CustomMinimumSize = new Vector2(EditorTheme.HintMinWidth, 0);
        return hint;
    }

    /// <summary>An action's tooltip: its title and key ("Weld  Ctrl+W"), then what it does.</summary>
    public static string Tooltip(EditorContext ctx, string actionId, string fallback)
    {
        if (ctx.FindAction(actionId) is not { } a)
        {
            return fallback;
        }

        string head = ShortcutBindings.Of(a) is { } key ? $"{a.Title}  {key}" : a.Title;
        return a.Description is { } d ? $"{head}\n{d}" : head;
    }
}
