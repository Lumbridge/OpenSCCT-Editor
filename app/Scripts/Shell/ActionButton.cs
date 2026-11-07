using Godot;
using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>A toolbar button that runs an editor action and mirrors its enabled and checked state. With an icon it
/// shows only the icon unless given a label; the tooltip names the action, its shortcut and what it does.</summary>
public sealed partial class ActionButton : Button
{
    private readonly EditorContext _ctx;

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public ActionButton()
        : this(EditorContext.Instance, "")
    {
    }

    /// <param name="label">Text shown beside the icon (a short form of the action's title); null shows the icon only,
    /// or the action's title when there is no icon.</param>
    /// <param name="variation">The look: <c>ToolButton</c> (flat until hovered), or e.g. <c>PrimaryButton</c>.</param>
    public ActionButton(EditorContext ctx, string actionId, Texture2D? icon = null, string? label = null, string variation = "ToolButton")
    {
        _ctx = ctx;
        ActionId = actionId;
        Icon = icon;
        Label = label;
        ThemeTypeVariation = variation;
        FocusMode = FocusModeEnum.None;
        Pressed += () =>
        {
            _ctx.Run(ActionId);
            Refresh();
        };
        Refresh();
    }

    /// <summary>The older form: a text button, or an icon button that shows its label too when asked.</summary>
    public ActionButton(EditorContext ctx, string actionId, string label, Texture2D? icon = null, bool showLabel = false)
        : this(ctx, actionId, icon, icon is null || showLabel ? label : null)
    {
    }

    public string ActionId { get; }

    private string? Label { get; }

    /// <summary>The tooltip: the action's name with its shortcut dim at the right, and what it does as a caption
    /// below.</summary>
    public override GodotObject? _MakeCustomTooltip(string forText)
    {
        if (_ctx.FindAction(ActionId) is not { } a)
        {
            return null;
        }

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", EditorTheme.SpaceL);
        box.AddChild(head);
        head.AddChild(new Label { Text = a.Title, ThemeTypeVariation = "TooltipLabel", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (ShortcutBindings.Text(a) is { } key)
        {
            head.AddChild(new Label { Text = key, ThemeTypeVariation = "TooltipKey" });
        }

        if (a.Description is { Length: > 0 } d)
        {
            var caption = new Label { Text = d, ThemeTypeVariation = "TooltipCaption" };
            // Wraps at the tooltip's width only when it is longer than that.
            if (caption.GetThemeFont("font").GetStringSize(d, fontSize: caption.GetThemeFontSize("font_size")).X > EditorTheme.TooltipMaxWidth)
            {
                caption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                caption.CustomMinimumSize = new Vector2(EditorTheme.TooltipMaxWidth, 0);
            }

            box.AddChild(caption);
        }

        return box;
    }

    public void Refresh()
    {
        EditorAction? a = _ctx.FindAction(ActionId);
        Visible = a is not null && WorkspaceMode.AllowsAction(ActionId);
        if (a is null)
        {
            return;
        }

        Text = Label ?? (Icon is null ? a.Title : "");
        Disabled = !a.IsEnabled();
        TooltipText = a.Title + (ShortcutBindings.Text(a) is { } s ? "  " + s : "") + (a.Description is { } d ? "\n" + d : "");
        if (a.IsChecked is not null)
        {
            ToggleMode = true;
            SetPressedNoSignal(a.IsChecked());
        }
    }
}
