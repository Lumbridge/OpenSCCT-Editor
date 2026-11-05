using Godot;

namespace OpenScctEditor.Shell;

/// <summary>Theme styles only the workbenches area needs (see docs/UI-DESIGN.md, "Rules for all areas"). Shared tokens and
/// variations live in EditorTheme.cs.</summary>
public static partial class EditorTheme
{
    /// <summary>The width of a row's source menu ("Set", "Add"), so the trailing slots of rows line up.</summary>
    public const int SlotMenuWidth = 68;

    static partial void WorkbenchStyles(Theme t)
    {
        // A command that opens a menu (the stop actor's senders, a picture's source, the tag picker): raised like a
        // command button, with a dim chevron on its right (set as the button's icon).
        const string drop = "DropButton";
        t.SetTypeVariation(drop, "MenuButton");
        t.SetStylebox("normal", drop, Box(PanelLight, padX: SpaceM));
        t.SetStylebox("hover", drop, Box(Hover, padX: SpaceM));
        t.SetStylebox("pressed", drop, Box(Hover, padX: SpaceM));
        t.SetStylebox("hover_pressed", drop, Box(Hover, padX: SpaceM));
        t.SetStylebox("disabled", drop, Box(new Color(PanelLight, 0.5f), padX: SpaceM));
        t.SetConstant("h_separation", drop, SpaceXs);
        t.SetColor("icon_normal_color", drop, TextDim);
        t.SetColor("icon_focus_color", drop, TextDim);
        t.SetColor("icon_hover_color", drop, Text);
        t.SetColor("icon_pressed_color", drop, Text);
        t.SetColor("icon_hover_pressed_color", drop, Text);
        t.SetColor("icon_disabled_color", drop, new Color(TextDisabled, 0.6f));

        // A combo box made of a text field and a drop-down (the event sequencer's tag): one outline, the field rounded
        // on the left, the chevron button joined on the right like a spin box's stepper.
        const string comboField = "ComboField";
        t.SetTypeVariation(comboField, "LineEdit");
        t.SetStylebox("normal", comboField, Corners(Box(Field, RadiusM, 1, padX: SpaceS, padY: SpaceXs), RadiusM, 0, 0, RadiusM));
        t.SetStylebox("focus", comboField, Corners(Box(new Color(0, 0, 0, 0), RadiusM, 1, Accent, padX: SpaceS, padY: SpaceXs), RadiusM, 0, 0, RadiusM));
        t.SetStylebox("read_only", comboField, Corners(Box(new Color(Field, 0.5f), RadiusM, 1, new Color(Border, 0.5f), padX: SpaceS, padY: SpaceXs), RadiusM, 0, 0, RadiusM));
        const string comboButton = "ComboButton";
        t.SetTypeVariation(comboButton, drop);
        StyleBoxFlat ComboPart(Color bg) => Borders(Corners(Box(bg, RadiusM, 1, padX: SpaceXs), 0, RadiusM, RadiusM, 0), 0, 1, 1, 1);
        t.SetStylebox("normal", comboButton, ComboPart(Field));
        t.SetStylebox("hover", comboButton, ComboPart(Hover));
        t.SetStylebox("pressed", comboButton, ComboPart(Pressed));
        t.SetStylebox("hover_pressed", comboButton, ComboPart(Pressed));
        t.SetStylebox("disabled", comboButton, ComboPart(new Color(Field, 0.5f)));

        // A row action that is also a state (the key a mover is drawn at, the key a round starts at): an icon button
        // whose icon turns accent on an accent wash while on.
        const string toggle = "IconToggle";
        t.SetTypeVariation(toggle, "IconButton");
        t.SetStylebox("pressed", toggle, Box(AccentWash, padX: SpaceXs, padY: SpaceXs));
        t.SetStylebox("hover_pressed", toggle, Box(new Color(Accent, 0.24f), padX: SpaceXs, padY: SpaceXs));
        t.SetColor("icon_pressed_color", toggle, Accent);
        t.SetColor("icon_hover_pressed_color", toggle, AccentHover);

        // The name of a group inside a section (an event sequencer group, a mover key): semibold body text, no
        // spacing of its own (a Header spans the width; this sits in a row with the group's controls).
        const string group = "GroupLabel";
        t.SetTypeVariation(group, "Label");
        t.SetFont("font", group, UiFontSemibold);
        t.SetFontSize("font_size", group, FontSizeSmall);
        t.SetColor("font_color", group, TextHeading);

        // The frame of a picture preview (a map picture's crop or render): a tile with a hairline edge, shown even
        // while there is no picture yet.
        const string picture = "PictureTile";
        t.SetTypeVariation(picture, "PanelContainer");
        t.SetStylebox("panel", picture, Box(Tile, RadiusS, border: 1, borderColor: Divider, padX: SpaceXxs, padY: SpaceXxs));
    }
}
