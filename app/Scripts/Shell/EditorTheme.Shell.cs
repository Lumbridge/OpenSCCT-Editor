using Godot;

namespace OpenScctEditor.Shell;

/// <summary>Theme styles only the shell area needs (see docs/UI-DESIGN.md, "Rules for all areas"). Shared tokens and
/// variations live in EditorTheme.cs.</summary>
public static partial class EditorTheme
{
    static partial void ShellStyles(Theme t)
    {
        SnapControls(t);
        StatusBar(t);
        Navigation(t);
    }

    /// <summary>A snap toggle joined to its size drop-down: one outlined control, a hairline between the parts, the
    /// toggle washed in the accent while snapping is on.</summary>
    private static void SnapControls(Theme t)
    {
        StyleBoxFlat Toggle(Color bg) =>
            Corners(Box(bg, RadiusM, 1, Border, padX: SpaceS, padY: SpaceXs), RadiusM, 0, 0, RadiusM);
        StyleBoxFlat Size(Color bg) =>
            Borders(Corners(Box(bg, RadiusM, 1, Border, padX: SpaceS, padY: SpaceXs), 0, RadiusM, RadiusM, 0), 0, 1, 1, 1);

        const string toggle = "SnapToggle";
        t.SetTypeVariation(toggle, "Button");
        t.SetStylebox("normal", toggle, Toggle(new Color(0, 0, 0, 0)));
        t.SetStylebox("hover", toggle, Toggle(Hover));
        t.SetStylebox("pressed", toggle, Toggle(AccentWash));
        t.SetStylebox("hover_pressed", toggle, Toggle(new Color(Accent, 0.26f)));
        t.SetStylebox("disabled", toggle, Toggle(new Color(0, 0, 0, 0)));
        t.SetColor("icon_normal_color", toggle, TextDim);
        t.SetColor("icon_hover_color", toggle, Text);
        t.SetColor("icon_pressed_color", toggle, ToolIconOn);
        t.SetColor("icon_hover_pressed_color", toggle, TextBright);

        const string size = "SnapMenu";
        t.SetTypeVariation(size, "ToolMenu");
        t.SetStylebox("normal", size, Size(new Color(0, 0, 0, 0)));
        t.SetStylebox("hover", size, Size(Hover));
        t.SetStylebox("pressed", size, Size(Hover));
        t.SetStylebox("hover_pressed", size, Size(Hover));
        t.SetStylebox("disabled", size, Size(new Color(0, 0, 0, 0)));
    }

    /// <summary>The status bar: small text, each field after the status text set off by a hairline on its left.</summary>
    private static void StatusBar(Theme t)
    {
        const string text = "StatusText";
        t.SetTypeVariation(text, "Label");
        t.SetFontSize("font_size", text, FontSizeSmall);
        t.SetColor("font_color", text, Text);

        StyleBoxFlat Segment()
        {
            StyleBoxFlat s = Borders(Box(new Color(0, 0, 0, 0), 0, padX: 0, padY: 0), 1, 0, 0, 0);
            s.BorderColor = Divider;
            s.DrawCenter = false;
            s.ContentMarginLeft = SpaceM;
            return s;
        }

        const string field = "StatusField";
        t.SetTypeVariation(field, "Label");
        t.SetStylebox("normal", field, Segment());
        t.SetFontSize("font_size", field, FontSizeSmall);
        t.SetColor("font_color", field, TextDim);

        // A field holding controls (the progress of a background job).
        const string segment = "StatusSegment";
        t.SetTypeVariation(segment, "PanelContainer");
        t.SetStylebox("panel", segment, Segment());
    }

    /// <summary>Preferences: the category buttons down the left (a wash on the chosen one), the hairline between the
    /// list and the page, and the colour swatches.</summary>
    private static void Navigation(Theme t)
    {
        const string nav = "NavButton";
        t.SetTypeVariation(nav, "Button");
        t.SetStylebox("normal", nav, Empty(SpaceM, SpaceS));
        t.SetStylebox("hover", nav, Box(HoverWash, RadiusM, padX: SpaceM, padY: SpaceS));
        t.SetStylebox("pressed", nav, Box(AccentWash, RadiusM, padX: SpaceM, padY: SpaceS));
        t.SetStylebox("hover_pressed", nav, Box(new Color(Accent, 0.22f), RadiusM, padX: SpaceM, padY: SpaceS));
        t.SetStylebox("disabled", nav, Empty(SpaceM, SpaceS));
        t.SetColor("font_color", nav, TextDim);
        t.SetColor("font_hover_color", nav, Text);
        t.SetColor("font_pressed_color", nav, TextBright);
        t.SetColor("font_hover_pressed_color", nav, TextBright);
        t.SetConstant("align_to_largest_stylebox", nav, 1);

        const string divider = "PaneDivider";
        t.SetTypeVariation(divider, "VSeparator");
        t.SetStylebox("separator", divider, new StyleBoxLine { Color = Divider, Thickness = 1, Vertical = true });
        t.SetConstant("separation", divider, 1);

        const string swatch = "Swatch";
        t.SetTypeVariation(swatch, "ColorPickerButton");
        t.SetStylebox("normal", swatch, Box(Field, RadiusS, 1, BorderStrong, padX: SpaceXxs, padY: SpaceXxs));
        t.SetStylebox("hover", swatch, Box(Field, RadiusS, 1, TextDim, padX: SpaceXxs, padY: SpaceXxs));
        t.SetStylebox("pressed", swatch, Box(Field, RadiusS, 1, Accent, padX: SpaceXxs, padY: SpaceXxs));
        t.SetStylebox("hover_pressed", swatch, Box(Field, RadiusS, 1, Accent, padX: SpaceXxs, padY: SpaceXxs));
        // Drawn over the swatch when it has the keyboard focus (Enter or Space opens the picker).
        t.SetStylebox("focus", swatch, Box(new Color(0, 0, 0, 0), RadiusS, 1, Accent, padX: SpaceXxs, padY: SpaceXxs));
    }
}
