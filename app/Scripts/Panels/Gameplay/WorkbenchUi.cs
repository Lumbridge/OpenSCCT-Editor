using Godot;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Builders the workbench panels (Mover, Event Sequencer, Match, Camera Network, Identity, Prefabs, Assets...) share on
/// top of <see cref="PanelUi"/>: buttons bound to an editor action's enabled state, callouts whose text changes, drop-down
/// command buttons, the trailing slot of row actions, and captions for picture tiles.
/// </summary>
internal static class WorkbenchUi
{
    /// <summary>A command button that runs an editor action and is disabled while the action is.</summary>
    public static Button Action(Control parent, string text, string tooltip, string actionId, EditorContext context)
    {
        Button b = PanelUi.Button(parent, text, tooltip, () => context.Run(actionId));
        Guard(b, () => context.FindAction(actionId)?.IsEnabled() ?? false);
        return b;
    }

    /// <summary>Keeps <paramref name="button"/> disabled while <paramref name="enabled"/> is false (checked each frame
    /// while the button is on screen, the first time once the panel is built, so the test may use controls made after
    /// the button).</summary>
    public static T Guard<T>(T button, Func<bool> enabled)
        where T : BaseButton
    {
        button.AddChild(new ButtonGuard { Button = button, Enabled = enabled });
        return button;
    }

    /// <summary>A callout (an inline note, warning or error on a wash of its colour) whose text is set later; hide the
    /// box while there is nothing to say.</summary>
    public static (PanelContainer Box, Label Text) Callout(Control parent, Severity s)
    {
        var box = new PanelContainer
        {
            ThemeTypeVariation = s switch { Severity.Error => "CalloutError", Severity.Warning => "Callout", _ => "CalloutInfo" },
        };
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        box.AddChild(row);
        row.AddChild(new TextureRect
        {
            Texture = PanelIcons.Severity(s),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        });
        var text = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(80, 0),
        };
        row.AddChild(text);
        parent.AddChild(box);
        return (box, text);
    }

    /// <summary>A raised button that opens a menu, with a chevron on its right. With no <paramref name="text"/> it is a
    /// square chevron, for a picker beside a field.</summary>
    public static MenuButton DropButton(Control parent, string text, string tooltip)
    {
        var b = new MenuButton
        {
            Text = text,
            TooltipText = tooltip,
            Flat = false,
            ThemeTypeVariation = "DropButton",
            Icon = Icons.Get("chevron-down", Colors.White),
            IconAlignment = text.Length == 0 ? HorizontalAlignment.Center : HorizontalAlignment.Right,
            FocusMode = Control.FocusModeEnum.None,
        };
        if (text.Length == 0)
        {
            b.CustomMinimumSize = new Vector2(EditorTheme.ControlHeight, 0);
        }

        PanelUi.CapPopup(b.GetPopup());
        parent.AddChild(b);
        return b;
    }

    /// <summary>The trailing slot of a row's actions (move, show, delete): right-aligned and packed, so the icons of
    /// rows below one another line up.</summary>
    public static HBoxContainer Actions(Control row)
    {
        var slot = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        slot.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        row.AddChild(slot);
        return slot;
    }

    /// <summary>An icon toggle: a row action that is also a state (on: an accent icon on an accent wash).</summary>
    public static Button IconToggle(Control parent, string icon, string tooltip, bool on, Action pressed)
    {
        Button b = PanelUi.IconButton(parent, icon, tooltip, pressed);
        b.ThemeTypeVariation = "IconToggle";
        b.ToggleMode = true;
        b.SetPressedNoSignal(on);
        return b;
    }

    /// <summary>A value that takes the room it is given and ends in "…" when it is longer, with the whole text in its
    /// tooltip.</summary>
    public static Label Value(Control parent, string text, string variation = "")
    {
        var l = new Label
        {
            Text = text,
            ThemeTypeVariation = variation,
            TooltipText = text,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(1, 0),
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        parent.AddChild(l);
        return l;
    }

    /// <summary>A flexible space that pushes what follows to the end of a row.</summary>
    public static Control Spacer(Control row)
    {
        var s = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(s);
        return s;
    }

    /// <summary>A short list for a caption: the first <paramref name="most"/> items, then "and n more".</summary>
    public static string Join(IReadOnlyList<string> items, int most = 3) =>
        items.Count <= most ? string.Join(", ", items) : $"{string.Join(", ", items.Take(most))} and {items.Count - most} more";

    /// <summary>Removes and frees every child of <paramref name="box"/>.</summary>
    public static void Clear(Control box)
    {
        foreach (Node child in box.GetChildren())
        {
            box.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>
    /// A tile caption of at most <paramref name="lines"/> lines that fit <paramref name="width"/>: broken at spaces and
    /// after "_", "." and "-", never inside a word. When the rest does not fit (the last line, or a part too long for any
    /// line) the line keeps its start and its end with "…" between, as names of a family differ at the end
    /// (PATCH_ESB…Actor10192). Show the whole name in the item's tooltip.
    /// </summary>
    public static string TileCaption(string text, Font font, int fontSize, float width, int lines = 2)
    {
        float Width(string s) => font.GetStringSize(s, HorizontalAlignment.Left, -1, fontSize).X;
        var tokens = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is ' ' or '_' or '.' or '-')
            {
                tokens.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            tokens.Add(text[start..]);
        }

        var result = new List<string>();
        int t = 0;
        while (t < tokens.Count && result.Count < lines)
        {
            bool last = result.Count == lines - 1;
            string line = "";
            while (t < tokens.Count && Width((line + tokens[t]).TrimEnd()) <= width)
            {
                line += tokens[t++];
            }

            // What is left does not fit (on the last line, or a part no line holds whole): the line ends in "…", so a
            // word is never broken in two.
            if (t < tokens.Count && (last || line.Length == 0))
            {
                line = Middle((line + string.Concat(tokens.Skip(t))).Trim(), width, Width);
                t = tokens.Count;
            }

            result.Add(line.TrimEnd());
        }

        return string.Join("\n", result);
    }

    /// <summary><paramref name="text"/> cut in the middle to fit <paramref name="width"/>: some of its start and a little
    /// more of its end, with "…" between.</summary>
    private static string Middle(string text, float width, Func<string, float> measure)
    {
        for (int keep = text.Length - 1; keep > 0; keep--)
        {
            int tail = (keep * 3 + 4) / 5;
            string cut = text[..(keep - tail)] + "…" + text[^tail..];
            if (measure(cut) <= width)
            {
                return cut;
            }
        }

        return "…";
    }
}
