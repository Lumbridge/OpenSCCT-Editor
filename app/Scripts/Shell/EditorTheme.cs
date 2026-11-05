using Godot;

namespace OpenScctEditor.Shell;

/// <summary>
/// The editor's dark theme and its design tokens (see docs/UI-DESIGN.md). Surfaces go from <see cref="Background"/>
/// (darkest) through <see cref="Field"/>, <see cref="Bar"/>, <see cref="Panel"/> and <see cref="Popup"/> to
/// <see cref="PanelLight"/> (raised controls); one accent means selected, focused, on or the primary action.
/// Every colour, size, radius and spacing the UI uses comes from here as a named constant or a theme type variation,
/// so panels set a <c>ThemeTypeVariation</c> instead of overriding colours or font sizes.
/// <para>Variations: buttons <c>PrimaryButton</c>, <c>FlatButton</c>, <c>IconButton</c>, <c>DangerIconButton</c>,
/// <c>ToolButton</c>, <c>ToolMenu</c>, <c>Chip</c>, <c>Segment</c>/<c>SegmentFirst</c>/<c>SegmentLast</c>,
/// <c>ViewLabel</c>; labels <c>Header</c>, <c>Title</c>, <c>Caption</c>, <c>Dim</c>, <c>Mono</c>; containers
/// <c>Backdrop</c>, <c>TopBar</c>, <c>Bar</c>, <c>StatusBar</c>, <c>ViewTag</c>, <c>Palette</c>,
/// <c>Callout</c>, <c>CalloutInfo</c>, <c>CalloutError</c>; <c>SubTabs</c> (TabBar) and <c>ScrubSpin</c> (SpinBox).</para>
/// <para>Each UI area adds the styles only it needs in its own partial file (<c>EditorTheme.Shell.cs</c>,
/// <c>.Panels.cs</c>, <c>.Viewports.cs</c>, <c>.Workbenches.cs</c>), called at the end of <see cref="Create"/>.</para>
/// </summary>
public static partial class EditorTheme
{
    // Surfaces, darkest to lightest.
    public static readonly Color Background = new(0.075f, 0.078f, 0.09f);
    public static readonly Color Field = new(0.086f, 0.09f, 0.104f);
    public static readonly Color Bar = new(0.098f, 0.102f, 0.118f);
    public static readonly Color Panel = new(0.122f, 0.127f, 0.145f);

    /// <summary>Menus, popups, tooltips and the command palette.</summary>
    public static readonly Color Popup = new(0.145f, 0.151f, 0.176f);

    /// <summary>A raised control at rest (buttons, foldable bars, tiles).</summary>
    public static readonly Color PanelLight = new(0.165f, 0.172f, 0.2f);
    public static readonly Color Hover = new(0.2f, 0.208f, 0.24f);

    /// <summary>A push button while it is held.</summary>
    public static readonly Color Pressed = new(0.235f, 0.245f, 0.285f);

    /// <summary>The square tile behind an asset or prefab picture.</summary>
    public static readonly Color Tile = PanelLight;

    /// <summary>The tile behind a mesh picture: a step lighter, so dark meshes stand off it.</summary>
    public static readonly Color TileMesh = Hover;

    // Lines.
    public static readonly Color Border = new(0.2f, 0.21f, 0.245f);

    /// <summary>Hairlines: under headers, between table rows and columns, between status bar segments.</summary>
    public static readonly Color Divider = new(Border, 0.6f);

    /// <summary>The hairline under a section header: firmer than <see cref="Divider"/>, as it carries a form's
    /// structure.</summary>
    public static readonly Color HeaderRule = Border;

    /// <summary>Dialog frames, swatch frames and outlines that must stand out without being the accent.</summary>
    public static readonly Color BorderStrong = new(0.27f, 0.28f, 0.33f);

    // Text.
    public static readonly Color Text = new(0.86f, 0.87f, 0.9f);

    /// <summary>Hovered and pressed text, selected rows, titles.</summary>
    public static readonly Color TextBright = new(0.96f, 0.97f, 0.99f);

    /// <summary>Section headers (semibold).</summary>
    public static readonly Color TextHeading = new(0.76f, 0.78f, 0.84f);

    /// <summary>Field names, captions, hints, units and passive icons.</summary>
    public static readonly Color TextDim = new(0.56f, 0.58f, 0.64f);
    public static readonly Color TextDisabled = new(0.42f, 0.44f, 0.49f);
    public static readonly Color TextOnAccent = Colors.White;

    /// <summary>The text of a hidden object's row in the outliner.</summary>
    public static readonly Color TextHidden = new(TextDim, 0.6f);

    /// <summary>Rest colour of a row's passive state glyph (a visible object's eye, an unlocked padlock).</summary>
    public static readonly Color GlyphRest = new(TextDim, 0.6f);

    /// <summary>Rest colour of a padlock that is open: quieter than the eye, so locked rows stand out.</summary>
    public static readonly Color GlyphFaint = new(TextDim, 0.35f);

    // Accent and state. The accent means selected, focused, on, or the primary action: never "a header".
    public static readonly Color Accent = new(0.33f, 0.58f, 0.96f);
    public static readonly Color AccentHover = Accent.Lightened(0.08f);
    public static readonly Color AccentPressed = Accent.Darkened(0.12f);

    /// <summary>The fill of a chip or tool that is on.</summary>
    public static readonly Color AccentWash = new(Accent, 0.16f);

    /// <summary>The accent as a selection fill behind a row, tile or text.</summary>
    public static readonly Color Selected = new(Accent, 0.32f);

    /// <summary>Row, tab and list hover over a panel or field.</summary>
    public static readonly Color HoverWash = new(Text, 0.07f);

    /// <summary>A hovered list or tree row: a step firmer than <see cref="HoverWash"/>, as a row band is wide and thin
    /// and the eye follows it across the columns. Still well below <see cref="Selected"/>.</summary>
    public static readonly Color RowHover = new(Text, 0.105f);

    /// <summary>The tint of a toolbar icon whose toggle is on (the ToolButton variation's pressed icon colour).</summary>
    public static readonly Color ToolIconOn = new(0.78f, 0.87f, 1f);

    // Semantic colours; use Wash() for a background and Line() for its outline.
    public static readonly Color Ok = new(0.4f, 0.8f, 0.5f);
    public static readonly Color Success = Ok;
    public static readonly Color Warning = new(0.95f, 0.72f, 0.25f);
    public static readonly Color Error = new(0.93f, 0.36f, 0.36f);
    public static readonly Color Info = new(0.45f, 0.68f, 0.95f);

    // Domain colours shown in the UI.
    public static readonly Color AxisX = new(0.9f, 0.3f, 0.3f);
    public static readonly Color AxisY = new(0.35f, 0.85f, 0.35f);
    public static readonly Color AxisZ = new(0.35f, 0.55f, 1f);

    /// <summary>The CSG Add and Subtract icons (the stock add and subtract brush colours of the views).</summary>
    public static readonly Color CsgAdd = new(0.38f, 0.55f, 1f);
    public static readonly Color CsgSubtract = new(1f, 0.62f, 0.2f);

    public static readonly Color TimelineTrack = Field;
    public static readonly Color TimelineSegment = new(Accent, 0.35f);
    public static readonly Color TimelineHold = PanelLight;
    public static readonly Color TimelineEvent = new(0.72f, 0.6f, 0.98f);
    public static readonly Color Playhead = Warning;

    /// <summary>The full-window shade behind a modal dialog.</summary>
    public static readonly Color Scrim = new(Background, 0.55f);
    public static readonly Color Shadow = new(0, 0, 0, 0.45f);

    // Spacing scale.
    public const int SpaceXxs = 2;
    public const int SpaceXs = 4;
    public const int SpaceS = 6;
    public const int SpaceM = 8;
    public const int SpaceL = 12;
    public const int SpaceXl = 16;
    public const int SpaceXxl = 24;

    // Radii.
    public const int RadiusS = 3;
    public const int RadiusM = 4;
    public const int RadiusL = 6;
    public const int RadiusXl = 8;
    public const int RadiusPill = 11;

    // Type scale (UI pixels). Nothing goes below FontSizeMicro.
    public const int FontSizeTitle = 15;
    public const int FontSize = 14;
    public const int FontSizeSmall = 13;
    public const int FontSizeCaption = 12;
    public const int FontSizeMicro = 11;

    // Icon sizes.
    public const int IconSize = 16;
    public const int IconSizeHero = 32;
    public const int IconSizeTile = 48;

    // Control sizes.
    public const int ControlHeight = 28;
    public const int ControlHeightCompact = 24;
    public const int RowHeight = 24;
    public const int MenuRowHeight = 24;
    public const int TabHeight = 30;
    public const int ToolButtonSize = 28;
    public const int IconButtonSize = 24;

    /// <summary>The widest a rich tooltip's description runs before it wraps.</summary>
    public const int TooltipMaxWidth = 320;
    public const int LabelColumn = 104;
    public const int LabelColumnWide = 168;

    /// <summary>Width of the label column in a workbench's nested settings (a picture's focal point, a camera's look).</summary>
    public const int LabelColumnNarrow = 56;
    public const int SpinMinWidth = 72;

    /// <summary>The narrowest a tool hint gets in the options row, so a stub of it always shows.</summary>
    public const int HintMinWidth = 80;
    public const int SpinCompactWidth = 56;
    public const int SpinButtonsWidth = 16;
    public const int NumberFieldWidth = 96;
    /// <summary>Below this width a panel's filter chips show their icon only (the text moves to the tooltip).</summary>
    public const int CompactPanelWidth = 320;

    /// <summary>The space between a dock's scrolling content and its scroll bar.</summary>
    public const int ScrollGutter = SpaceS;

    /// <summary>The narrowest a search box gets: room for a short placeholder.</summary>
    public const int SearchMinWidth = 110;
    public const int PopupMaxHeight = 480;
    public const int DialogMinWidth = 420;

    /// <summary>The least height of a box for prose (a summary, a description): three lines of text.</summary>
    public const int ProseMinHeight = 72;

    /// <summary>The least width of a pair of message fields that share a line (a spy's and a mercenary's): below it
    /// they move to a line of their own.</summary>
    public const int MessagesMinWidth = 360;

    /// <summary>Width of a picker beside a field (the event sequencer's send and answer types).</summary>
    public const int PickerWidth = 150;

    /// <summary>Height of a timeline drawn in a workbench (the mover's activation).</summary>
    public const int TimelineHeight = 56;

    /// <summary>Height of a lane timeline (the event sequencer: a lane per group).</summary>
    public const int LaneTimelineHeight = 96;

    /// <summary>The cursor field of the status bar is this wide, so the fields after it do not move as it changes.</summary>
    public const int StatusCursorWidth = 200;

    /// <summary>The width of the Preferences category list.</summary>
    public const int PreferencesNavWidth = 132;

    /// <summary>The size Preferences opens at on a canvas <paramref name="canvas"/> UI pixels big: three fifths of it,
    /// never smaller than a Shortcuts page whose action names fit nor larger than a form needs, and always inside the
    /// window.</summary>
    public static Vector2I PreferencesSize(Vector2 canvas) => new(
        (int)Mathf.Min(Mathf.Clamp(canvas.X * 0.6f, 720, 880), canvas.X - 2 * SpaceXxl),
        (int)Mathf.Min(Mathf.Clamp(canvas.Y * 0.6f, 520, 640), canvas.Y - 2 * SpaceXxl));

    /// <summary>The width of a key column in a table of shortcuts (the shortcut and its default).</summary>
    public const int ShortcutColumnWidth = 120;

    /// <summary>A colour swatch in a form (Preferences > Views).</summary>
    public static readonly Vector2 SwatchSize = new(44, 20);

    /// <summary>A semantic colour as a background wash (callouts, finding rows).</summary>
    public static Color Wash(Color c) => new(c, 0.1f);

    /// <summary>A semantic colour as an outline.</summary>
    public static Color Line(Color c) => new(c, 0.35f);

    /// <summary>The initial width of the left dock for a canvas <paramref name="width"/> UI pixels wide: never so
    /// narrow that outliner names lose more than their last characters.</summary>
    public static int DockLeft(float width) => (int)Mathf.Clamp(width * 0.19f, 270, 300);

    /// <summary>The initial width of the right column: at least what an Inspector form needs (label column and three
    /// axis fields).</summary>
    public static int DockRight(float width) => (int)Mathf.Clamp(width * 0.24f, 320, 380);

    /// <summary>The initial height of the bottom dock for a canvas <paramref name="height"/> UI pixels high: a toolbar
    /// and a few rows of results even on a 720 px window.</summary>
    public static int DockBottom(float height) => (int)Mathf.Clamp(height * 0.29f, 210, 280);

    /// <summary>The share of the right column the workbench dock starts with; the Inspector's dock gets the rest (about
    /// two thirds), as the Inspector is the form a user reads most.</summary>
    public const float DockWorkbenchShare = 0.35f;

    /// <summary>Below this canvas height the right column is too short to split without crushing the Inspector to its
    /// first block: the workbench panels join the Inspector's dock as more tabs (those that do not fit in its tab row
    /// are in the dock's overflow menu).</summary>
    public const int CompactCanvasHeight = 860;

    private static Font? s_uiFont;
    private static Font? s_uiFontSemibold;
    private static Font? s_monoFont;
    private static Font? s_numberFont;

    /// <summary>The platform's UI typeface (Segoe UI on Windows), with its symbol font behind it for arrows and stars;
    /// elsewhere the system sans-serif.</summary>
    public static Font UiFont => s_uiFont ??= SystemUi(400);

    /// <summary>The UI typeface at weight 600 (Segoe UI Semibold): headers, titles, dialog titles.</summary>
    public static Font UiFontSemibold => s_uiFontSemibold ??= SystemUi(600);

    /// <summary>How far the monospaced face's glyphs drop (a fraction of its height) to sit on the UI face's baseline:
    /// a table cell centres each font on its own ascent and descent, and the monospaced faces' are smaller.</summary>
    public const float MonoBaselineOffset = 0.1f;

    /// <summary>A monospaced face for codes and identifiers in tables (Map Check codes, log times), on the same
    /// baseline as the UI face beside it.</summary>
    public static Font MonoFont => s_monoFont ??= new FontVariation
    {
        BaseFont = new SystemFont
        {
            FontNames = ["Cascadia Mono", "Consolas", "monospace"],
            Antialiasing = TextServer.FontAntialiasing.Gray,
            Hinting = TextServer.Hinting.Light,
        },
        BaselineOffset = MonoBaselineOffset,
    };

    /// <summary>The UI typeface with tabular (fixed-width) digits, for numbers that update live.</summary>
    public static Font NumberFont => s_numberFont ??= new FontVariation
    {
        BaseFont = UiFont,
        OpentypeFeatures = new Godot.Collections.Dictionary { [TextServerManager.GetPrimaryInterface().NameToTag("tnum")] = 1 },
    };

    private static SystemFont SystemUi(int weight)
    {
        var symbols = new SystemFont { FontNames = ["Segoe UI Symbol", "Noto Sans Symbols 2", "DejaVu Sans"] };
        return new SystemFont
        {
            FontNames = ["Segoe UI", "Inter", "Noto Sans", "sans-serif"],
            FontWeight = weight,
            Antialiasing = TextServer.FontAntialiasing.Gray,
            Hinting = TextServer.Hinting.Light,
            SubpixelPositioning = TextServer.SubpixelPositioning.Auto,
            Fallbacks = [symbols],
        };
    }

    public static Theme Create()
    {
        var t = new Theme { DefaultFontSize = FontSize, DefaultFont = UiFont };
        Containers(t);
        Buttons(t);
        ButtonVariations(t);
        Fields(t);
        Ranges(t);
        Lists(t);
        Popups(t);
        Docks(t);
        Labels(t);
        ShellStyles(t);
        PanelStyles(t);
        ViewportStyles(t);
        WorkbenchStyles(t);
        return t;
    }

    static partial void ShellStyles(Theme t);

    static partial void PanelStyles(Theme t);

    static partial void ViewportStyles(Theme t);

    static partial void WorkbenchStyles(Theme t);

    /// <summary>A flat box: background, optional border, corner radius and content margins.</summary>
    public static StyleBoxFlat Box(Color bg, int radius = RadiusM, int border = 0, Color? borderColor = null, int padX = SpaceM, int padY = SpaceXs)
    {
        var s = new StyleBoxFlat { BgColor = bg, BorderColor = borderColor ?? Border, AntiAliasing = true };
        s.SetBorderWidthAll(border);
        s.SetCornerRadiusAll(radius);
        s.ContentMarginLeft = s.ContentMarginRight = padX;
        s.ContentMarginTop = s.ContentMarginBottom = padY;
        return s;
    }

    /// <summary>The translucent tag drawn over a view (view name, grid scale, storey slider, drag readout), for code
    /// that draws it itself in <c>_Draw</c>. Controls use the <c>ViewTag</c> variation.
    /// A hairline frame makes it read as one box on the dark ortho views as on a lit perspective view.</summary>
    public static StyleBoxFlat ViewTagBox() => Box(new Color(Background, 0.88f), RadiusM, 1, new Color(Border, 0.9f), padX: 7, padY: SpaceXxs);

    /// <summary>The small dark pill behind a world label in a view (zone tips, team starts, event names): the view tag's
    /// backdrop, lighter and tighter, so labels read over lit floors and busy wires alike.</summary>
    public static StyleBoxFlat WorldLabelBox() => Box(new Color(Background, 0.75f), RadiusS, padX: SpaceXs, padY: 1);

    /// <summary>A box that draws nothing but keeps content margins (so a flat control lines up with boxed ones).</summary>
    public static StyleBoxEmpty Empty(int padX = 0, int padY = 0) =>
        new() { ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY };

    private static StyleBoxFlat Corners(StyleBoxFlat s, int topLeft, int topRight, int bottomRight, int bottomLeft)
    {
        s.CornerRadiusTopLeft = topLeft;
        s.CornerRadiusTopRight = topRight;
        s.CornerRadiusBottomRight = bottomRight;
        s.CornerRadiusBottomLeft = bottomLeft;
        return s;
    }

    private static StyleBoxFlat Borders(StyleBoxFlat s, int left, int top, int right, int bottom)
    {
        s.BorderWidthLeft = left;
        s.BorderWidthTop = top;
        s.BorderWidthRight = right;
        s.BorderWidthBottom = bottom;
        return s;
    }

    private static void Containers(Theme t)
    {
        t.SetStylebox("panel", "Panel", Box(Panel, 0, padX: 0, padY: 0));
        t.SetStylebox("panel", "PanelContainer", Box(Panel, 0, padX: 0, padY: 0));

        t.SetTypeVariation("Backdrop", "Panel");
        t.SetStylebox("panel", "Backdrop", Box(Background, 0, padX: 0, padY: 0));

        // The menu bar and toolbar share one surface; a hairline closes the toolbar (with the tool options) off from
        // the docks, and the status bar has one along its top.
        t.SetTypeVariation("TopBar", "PanelContainer");
        t.SetStylebox("panel", "TopBar", Box(Bar, 0, padX: SpaceXs, padY: 1));
        t.SetTypeVariation("Bar", "PanelContainer");
        t.SetStylebox("panel", "Bar", Borders(Box(Bar, 0, padX: SpaceS, padY: 3), 0, 0, 0, 1));
        t.SetTypeVariation("StatusBar", "PanelContainer");
        t.SetStylebox("panel", "StatusBar", Borders(Box(Bar, 0, padX: 10, padY: 3), 0, 1, 0, 0));

        t.SetTypeVariation("ViewTag", "PanelContainer");
        t.SetStylebox("panel", "ViewTag", ViewTagBox());

        t.SetTypeVariation("Palette", "PanelContainer");
        t.SetStylebox("panel", "Palette", PopupBox(SpaceS, SpaceS));

        foreach ((string name, Color c) in new[] { ("Callout", Warning), ("CalloutInfo", Info), ("CalloutError", Error) })
        {
            t.SetTypeVariation(name, "PanelContainer");
            t.SetStylebox("panel", name, Box(Wash(c), RadiusM, 1, Line(c), padX: SpaceM, padY: SpaceS));
        }

        foreach (string split in new[] { "HSplitContainer", "VSplitContainer", "SplitContainer" })
        {
            t.SetConstant("separation", split, SpaceXs);
            t.SetConstant("minimum_grab_thickness", split, SpaceM);
            t.SetConstant("autohide", split, 1);
            t.SetIcon("grabber", split, new ImageTexture());
            t.SetIcon("h_grabber", split, new ImageTexture());
            t.SetIcon("v_grabber", split, new ImageTexture());
        }

        t.SetConstant("separation", "HBoxContainer", SpaceXs);
        t.SetConstant("separation", "VBoxContainer", SpaceXs);
        t.SetConstant("h_separation", "GridContainer", SpaceS);
        t.SetConstant("v_separation", "GridContainer", SpaceXs);
        t.SetConstant("h_separation", "HFlowContainer", SpaceXs);
        t.SetConstant("v_separation", "HFlowContainer", SpaceXs);

        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Border, Thickness = 1 });
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Border, Thickness = 1, Vertical = true, GrowBegin = -SpaceXs, GrowEnd = -SpaceXs });
        t.SetConstant("separation", "HSeparator", SpaceM);
        t.SetConstant("separation", "VSeparator", 10);

        foreach (string bar in new[] { "HScrollBar", "VScrollBar" })
        {
            t.SetStylebox("scroll", bar, Empty(3, 3));
            t.SetStylebox("scroll_focus", bar, Empty(3, 3));
            t.SetStylebox("grabber", bar, Box(new Color(Text, 0.16f), RadiusS, padX: 3, padY: 3));
            t.SetStylebox("grabber_highlight", bar, Box(new Color(Text, 0.28f), RadiusS, padX: 3, padY: 3));
            t.SetStylebox("grabber_pressed", bar, Box(new Color(Text, 0.36f), RadiusS, padX: 3, padY: 3));
            foreach (string icon in new[] { "increment", "increment_highlight", "increment_pressed", "decrement", "decrement_highlight", "decrement_pressed" })
            {
                t.SetIcon(icon, bar, new ImageTexture());
            }
        }

        // Foldable sections (Inspector properties, Map info): a raised bar with a chevron, 2 px apart.
        const string fold = "FoldableContainer";
        t.SetStylebox("title_panel", fold, Box(PanelLight, RadiusM, padX: SpaceM, padY: SpaceXs));
        t.SetStylebox("title_hover_panel", fold, Box(Hover, RadiusM, padX: SpaceM, padY: SpaceXs));
        t.SetStylebox("title_collapsed_panel", fold, Box(PanelLight, RadiusM, padX: SpaceM, padY: SpaceXs));
        t.SetStylebox("title_collapsed_hover_panel", fold, Box(Hover, RadiusM, padX: SpaceM, padY: SpaceXs));
        t.SetStylebox("panel", fold, Box(Panel, 0, padX: SpaceS, padY: SpaceS));
        t.SetStylebox("focus", fold, new StyleBoxEmpty());
        t.SetColor("font_color", fold, Text);
        t.SetColor("hover_font_color", fold, TextBright);
        t.SetColor("collapsed_font_color", fold, Text);
        t.SetConstant("h_separation", fold, SpaceS);
        t.SetIcon("expanded_arrow", fold, Icons.Get("chevron-down", TextDim));
        t.SetIcon("expanded_arrow_mirrored", fold, Icons.Get("chevron-down", TextDim));
        t.SetIcon("folded_arrow", fold, Icons.Get("chevron-right", TextDim));
        t.SetIcon("folded_arrow_mirrored", fold, Icons.Get("chevron-left", TextDim));

        // A fold inside a fold (the categories of Map info): a flat chevron and semibold title, no bar, so only the
        // top level is a raised band. Its chevron lines up with the bar's, and its rows sit one step in from the bar's
        // rows, so the level reads from the left edge.
        const string nested = "FoldNested";
        t.SetTypeVariation(nested, fold);
        t.SetStylebox("title_panel", nested, Box(new Color(0, 0, 0, 0), RadiusM, padX: SpaceXxs, padY: SpaceXs));
        t.SetStylebox("title_hover_panel", nested, Box(HoverWash, RadiusM, padX: SpaceXxs, padY: SpaceXs));
        t.SetStylebox("title_collapsed_panel", nested, Box(new Color(0, 0, 0, 0), RadiusM, padX: SpaceXxs, padY: SpaceXs));
        t.SetStylebox("title_collapsed_hover_panel", nested, Box(HoverWash, RadiusM, padX: SpaceXxs, padY: SpaceXs));
        StyleBoxFlat nestedBody = Box(new Color(0, 0, 0, 0), 0, padX: 0, padY: SpaceXxs);
        nestedBody.ContentMarginLeft = SpaceL;
        t.SetStylebox("panel", nested, nestedBody);
        t.SetFont("font", nested, UiFontSemibold);
        t.SetFontSize("font_size", nested, FontSizeSmall);
        t.SetColor("font_color", nested, TextHeading);
        t.SetColor("collapsed_font_color", nested, TextHeading);
    }

    private static void Buttons(Theme t)
    {
        foreach (string type in new[] { "Button", "OptionButton", "MenuButton", "CheckBox", "CheckButton" })
        {
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, TextBright);
            t.SetColor("font_pressed_color", type, TextBright);
            t.SetColor("font_hover_pressed_color", type, TextBright);
            t.SetColor("font_focus_color", type, Text);
            t.SetColor("font_disabled_color", type, TextDisabled);
            // Icons carry their own colour (white icons are tinted by the IconButton variations).
            t.SetColor("icon_normal_color", type, Colors.White);
            t.SetColor("icon_hover_color", type, Colors.White);
            t.SetColor("icon_pressed_color", type, Colors.White);
            t.SetColor("icon_hover_pressed_color", type, Colors.White);
            t.SetColor("icon_focus_color", type, Colors.White);
            t.SetColor("icon_disabled_color", type, new Color(1, 1, 1, 0.4f));
            t.SetStylebox("focus", type, new StyleBoxEmpty());
            t.SetConstant("h_separation", type, SpaceS);
        }

        // A command: raised at rest. The pressed box also marks a toggle that is on: an accent wash and outline,
        // quieter than a solid fill when several are on together.
        t.SetStylebox("normal", "Button", Box(PanelLight, padX: 10));
        t.SetStylebox("hover", "Button", Box(Hover, padX: 10));
        t.SetStylebox("pressed", "Button", Box(AccentWash, border: 1, borderColor: new Color(Accent, 0.6f), padX: 10));
        t.SetStylebox("hover_pressed", "Button", Box(new Color(Accent, 0.24f), border: 1, borderColor: new Color(Accent, 0.8f), padX: 10));
        t.SetStylebox("disabled", "Button", Box(new Color(PanelLight, 0.5f), padX: 10));

        t.SetStylebox("normal", "OptionButton", Box(PanelLight, padX: SpaceM));
        t.SetStylebox("hover", "OptionButton", Box(Hover, padX: SpaceM));
        t.SetStylebox("pressed", "OptionButton", Box(Hover, padX: SpaceM));
        t.SetStylebox("disabled", "OptionButton", Box(new Color(PanelLight, 0.5f), padX: SpaceM));
        t.SetConstant("arrow_margin", "OptionButton", SpaceS);
        t.SetIcon("arrow", "OptionButton", Icons.Get("chevron-down", TextDim));

        // Menu buttons (view menus) look like toolbar buttons with a label.
        t.SetStylebox("normal", "MenuButton", Empty(SpaceS, SpaceXs));
        t.SetStylebox("hover", "MenuButton", Box(Hover, padX: SpaceS));
        t.SetStylebox("pressed", "MenuButton", Box(Hover, padX: SpaceS));
        t.SetStylebox("disabled", "MenuButton", Empty(SpaceS, SpaceXs));

        foreach (string check in new[] { "CheckBox", "CheckButton" })
        {
            t.SetStylebox("normal", check, Empty(SpaceXs, 3));
            t.SetStylebox("hover", check, Box(HoverWash, padX: SpaceXs, padY: 3));
            t.SetStylebox("pressed", check, Empty(SpaceXs, 3));
            t.SetStylebox("hover_pressed", check, Box(HoverWash, padX: SpaceXs, padY: 3));
            t.SetStylebox("disabled", check, Empty(SpaceXs, 3));
            t.SetColor("font_pressed_color", check, Text);
            t.SetColor("font_hover_pressed_color", check, TextBright);
        }

        t.SetIcon("checked", "CheckBox", Icons.Get("check-on", Accent));
        t.SetIcon("unchecked", "CheckBox", Icons.Get("check-off", TextDim));
        t.SetIcon("checked_disabled", "CheckBox", Icons.Get("check-on", TextDisabled));
        t.SetIcon("unchecked_disabled", "CheckBox", Icons.Get("check-off", new Color(TextDisabled, 0.7f)));
        t.SetIcon("radio_checked", "CheckBox", Icons.Get("radio-on", Accent));
        t.SetIcon("radio_unchecked", "CheckBox", Icons.Get("radio-off", TextDim));
        t.SetIcon("radio_checked_disabled", "CheckBox", Icons.Get("radio-on", TextDisabled));
        t.SetIcon("radio_unchecked_disabled", "CheckBox", Icons.Get("radio-off", new Color(TextDisabled, 0.7f)));
    }

    private static void ButtonVariations(Theme t)
    {
        // The one primary action of a surface (dialog default, Run check, Play).
        const string primary = "PrimaryButton";
        t.SetTypeVariation(primary, "Button");
        t.SetStylebox("normal", primary, Box(Accent, padX: 10));
        t.SetStylebox("hover", primary, Box(AccentHover, padX: 10));
        t.SetStylebox("pressed", primary, Box(AccentPressed, padX: 10));
        t.SetStylebox("hover_pressed", primary, Box(AccentPressed, padX: 10));
        t.SetStylebox("disabled", primary, Box(new Color(PanelLight, 0.5f), padX: 10));
        foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
        {
            t.SetColor(c, primary, TextOnAccent);
        }

        // An inline text action in a dense row: nothing drawn until hovered; accent text when toggled on.
        const string flat = "FlatButton";
        t.SetTypeVariation(flat, "Button");
        t.SetStylebox("normal", flat, Empty(10, SpaceXs));
        t.SetStylebox("hover", flat, Box(Hover, padX: 10));
        t.SetStylebox("pressed", flat, Box(Pressed, padX: 10));
        t.SetStylebox("hover_pressed", flat, Box(Pressed, padX: 10));
        t.SetStylebox("disabled", flat, Empty(10, SpaceXs));
        t.SetColor("font_pressed_color", flat, Accent);
        t.SetColor("font_hover_pressed_color", flat, AccentHover);

        // A 24 px square row action (move up, delete, go to). Its icon is drawn white and tinted here, dim at rest.
        const string icon = "IconButton";
        t.SetTypeVariation(icon, "Button");
        t.SetStylebox("normal", icon, Empty(SpaceXs, SpaceXs));
        t.SetStylebox("hover", icon, Box(HoverWash, padX: SpaceXs, padY: SpaceXs));
        t.SetStylebox("pressed", icon, Box(new Color(Text, 0.12f), padX: SpaceXs, padY: SpaceXs));
        t.SetStylebox("hover_pressed", icon, Box(new Color(Text, 0.12f), padX: SpaceXs, padY: SpaceXs));
        t.SetStylebox("disabled", icon, Empty(SpaceXs, SpaceXs));
        t.SetColor("icon_normal_color", icon, TextDim);
        t.SetColor("icon_focus_color", icon, TextDim);
        t.SetColor("icon_hover_color", icon, Text);
        t.SetColor("icon_pressed_color", icon, TextBright);
        t.SetColor("icon_hover_pressed_color", icon, TextBright);
        t.SetColor("icon_disabled_color", icon, new Color(TextDisabled, 0.6f));

        // The same for a destructive row action: the icon turns red under the pointer.
        const string danger = "DangerIconButton";
        t.SetTypeVariation(danger, icon);
        t.SetColor("icon_hover_color", danger, Error);
        t.SetColor("icon_pressed_color", danger, Error);
        t.SetColor("icon_hover_pressed_color", danger, Error);

        // Main toolbar buttons: nothing drawn until hovered; an accent wash and outline while their tool is on.
        const string tool = "ToolButton";
        t.SetTypeVariation(tool, "Button");
        t.SetStylebox("normal", tool, Empty(SpaceS, SpaceXs));
        t.SetStylebox("hover", tool, Box(Hover, padX: SpaceS));
        t.SetStylebox("pressed", tool, Box(AccentWash, border: 1, borderColor: new Color(Accent, 0.85f), padX: SpaceS));
        t.SetStylebox("hover_pressed", tool, Box(new Color(Accent, 0.26f), border: 1, borderColor: Accent, padX: SpaceS));
        t.SetStylebox("disabled", tool, Empty(SpaceS, SpaceXs));
        t.SetColor("icon_pressed_color", tool, new Color(0.78f, 0.87f, 1f));
        t.SetColor("icon_hover_pressed_color", tool, Colors.White);

        // A toolbar drop-down: the toolbar button's margins in every state, a box while hovered or open. The shell
        // gives it a chevron-down icon on the right.
        const string toolMenu = "ToolMenu";
        t.SetTypeVariation(toolMenu, "MenuButton");
        t.SetStylebox("normal", toolMenu, Empty(SpaceS, SpaceXs));
        t.SetStylebox("hover", toolMenu, Box(Hover, padX: SpaceS));
        t.SetStylebox("pressed", toolMenu, Box(Hover, padX: SpaceS));
        t.SetStylebox("hover_pressed", toolMenu, Box(Hover, padX: SpaceS));
        t.SetStylebox("disabled", toolMenu, Empty(SpaceS, SpaceXs));
        t.SetConstant("h_separation", toolMenu, SpaceXs);

        // A filter toggle: an outlined pill when off, an accent wash when on.
        const string chip = "Chip";
        t.SetTypeVariation(chip, "Button");
        t.SetStylebox("normal", chip, Box(new Color(0, 0, 0, 0), RadiusPill, 1, Border, padX: 9, padY: SpaceXxs));
        t.SetStylebox("hover", chip, Box(HoverWash, RadiusPill, 1, BorderStrong, padX: 9, padY: SpaceXxs));
        t.SetStylebox("pressed", chip, Box(AccentWash, RadiusPill, 1, new Color(Accent, 0.16f), padX: 9, padY: SpaceXxs));
        t.SetStylebox("hover_pressed", chip, Box(new Color(Accent, 0.24f), RadiusPill, 1, new Color(Accent, 0.24f), padX: 9, padY: SpaceXxs));
        t.SetStylebox("disabled", chip, Box(new Color(0, 0, 0, 0), RadiusPill, 1, new Color(Border, 0.5f), padX: 9, padY: SpaceXxs));
        t.SetColor("font_color", chip, TextDim);
        t.SetColor("font_hover_color", chip, Text);
        t.SetColor("font_pressed_color", chip, Text);
        t.SetColor("font_hover_pressed_color", chip, TextBright);
        t.SetFontSize("font_size", chip, FontSizeSmall);
        t.SetConstant("h_separation", chip, SpaceXs);

        // The parts of a segmented control (All / Usual / None): one raised strip, hairlines between the parts,
        // rounded only at its ends.
        Color segmentOn = PanelLight.Lerp(Accent, 0.28f);
        foreach ((string name, int left, int right, int divider) in new[] { ("Segment", 0, 0, 1), ("SegmentFirst", RadiusM, 0, 1), ("SegmentLast", 0, RadiusM, 0) })
        {
            t.SetTypeVariation(name, "Button");
            StyleBoxFlat Part(Color bg) => Borders(Corners(Box(bg, padX: 10, borderColor: Divider), left, right, right, left), 0, 0, divider, 0);
            t.SetStylebox("normal", name, Part(PanelLight));
            t.SetStylebox("hover", name, Part(Hover));
            t.SetStylebox("pressed", name, Part(segmentOn));
            t.SetStylebox("hover_pressed", name, Part(segmentOn.Lightened(0.06f)));
            t.SetStylebox("disabled", name, Part(new Color(PanelLight, 0.5f)));
        }

        // The tag over a view: its name and render mode, a menu of view options.
        const string viewLabel = "ViewLabel";
        t.SetTypeVariation(viewLabel, "MenuButton");
        // Every state is the view tag's box with only its fill (and, while the menu is open, its frame) changed, so
        // the text never moves or loses room between states.
        StyleBoxFlat ViewTagState(Color bg, Color? frame = null)
        {
            StyleBoxFlat s = ViewTagBox();
            s.BgColor = bg;
            s.BorderColor = frame ?? s.BorderColor;
            return s;
        }

        t.SetStylebox("normal", viewLabel, ViewTagBox());
        t.SetStylebox("hover", viewLabel, ViewTagState(new Color(PanelLight, 0.92f)));
        t.SetStylebox("pressed", viewLabel, ViewTagState(new Color(PanelLight, 0.96f), BorderStrong));
        t.SetStylebox("hover_pressed", viewLabel, ViewTagState(new Color(PanelLight, 0.96f), BorderStrong));
        t.SetStylebox("disabled", viewLabel, ViewTagBox());
        t.SetStylebox("focus", viewLabel, new StyleBoxEmpty());
        t.SetColor("font_hover_color", viewLabel, TextBright);
        t.SetColor("font_pressed_color", viewLabel, TextBright);
        t.SetColor("font_hover_pressed_color", viewLabel, TextBright);
        t.SetFontSize("font_size", viewLabel, FontSizeSmall);
    }

    private static void Fields(Theme t)
    {
        StyleBoxFlat field = Box(Field, RadiusM, 1, padX: SpaceS, padY: SpaceXs);
        StyleBoxFlat focus = Box(new Color(0, 0, 0, 0), RadiusM, 1, Accent, padX: SpaceS, padY: SpaceXs);
        StyleBoxFlat readOnly = Box(new Color(Field, 0.5f), RadiusM, 1, new Color(Border, 0.5f), padX: SpaceS, padY: SpaceXs);
        t.SetStylebox("normal", "LineEdit", field);
        // Boxes take their width from the layout (or their own minimum), not from four characters of the font.
        t.SetConstant("minimum_character_width", "LineEdit", 0);
        t.SetStylebox("focus", "LineEdit", focus);
        t.SetStylebox("read_only", "LineEdit", readOnly);
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_selected_color", "LineEdit", TextBright);
        t.SetColor("font_uneditable_color", "LineEdit", TextDim);
        t.SetColor("font_placeholder_color", "LineEdit", new Color(TextDim, 0.7f));
        t.SetColor("caret_color", "LineEdit", Text);
        t.SetColor("selection_color", "LineEdit", Selected);
        t.SetColor("clear_button_color", "LineEdit", TextDim);
        t.SetColor("clear_button_color_pressed", "LineEdit", Accent);
        t.SetIcon("clear", "LineEdit", Icons.Get("close", TextDim));

        t.SetStylebox("normal", "TextEdit", field);
        t.SetStylebox("focus", "TextEdit", focus);
        t.SetStylebox("read_only", "TextEdit", readOnly);
        t.SetColor("font_color", "TextEdit", Text);
        t.SetColor("font_readonly_color", "TextEdit", TextDim);
        t.SetColor("font_placeholder_color", "TextEdit", new Color(TextDim, 0.7f));
        t.SetColor("selection_color", "TextEdit", Selected);
        t.SetColor("caret_color", "TextEdit", Text);

        SpinBoxes(t);
    }

    /// <summary>A spin box is one field: the text on the left, the stepper joined inside the same outline on the
    /// right, and room for four digits at least so it never collapses to a stub.</summary>
    private static void SpinBoxes(Theme t)
    {
        const string inner = "SpinBoxInnerLineEdit";
        t.SetTypeVariation(inner, "LineEdit");
        t.SetConstant("minimum_character_width", inner, 4);
        t.SetStylebox("normal", inner, Corners(Box(Field, RadiusM, 1, padX: SpaceS, padY: SpaceXs), RadiusM, 0, 0, RadiusM));
        t.SetStylebox("focus", inner, Corners(Box(new Color(0, 0, 0, 0), RadiusM, 1, Accent, padX: SpaceS, padY: SpaceXs), RadiusM, 0, 0, RadiusM));
        t.SetStylebox("read_only", inner, Corners(Box(new Color(Field, 0.5f), RadiusM, 1, new Color(Border, 0.5f), padX: SpaceS, padY: SpaceXs), RadiusM, 0, 0, RadiusM));

        // The up half has the top and right border, the down half the right and bottom ones and a top line that
        // separates the two.
        StyleBoxFlat Up(Color bg) => Borders(Corners(Box(bg, RadiusM, padX: 0, padY: 0), 0, RadiusM, 0, 0), 0, 1, 1, 0);
        StyleBoxFlat Down(Color bg) => Borders(Corners(Box(bg, RadiusM, padX: 0, padY: 0), 0, 0, RadiusM, 0), 0, 1, 1, 1);
        foreach ((string state, Color bg) in new[] { ("", Field), ("_hovered", Hover), ("_pressed", Pressed), ("_disabled", new Color(Field, 0.5f)) })
        {
            t.SetStylebox("up_background" + state, "SpinBox", Up(bg));
            t.SetStylebox("down_background" + state, "SpinBox", Down(bg));
        }

        t.SetStylebox("up_down_buttons_separator", "SpinBox", new StyleBoxEmpty());
        t.SetStylebox("field_and_buttons_separator", "SpinBox", new StyleBoxEmpty());
        t.SetConstant("buttons_width", "SpinBox", SpinButtonsWidth);
        t.SetConstant("buttons_vertical_separation", "SpinBox", 0);
        t.SetConstant("field_and_buttons_separation", "SpinBox", 0);
        t.SetConstant("set_min_buttons_width_from_icons", "SpinBox", 0);
        foreach (string state in new[] { "", "_hover", "_pressed", "_disabled" })
        {
            t.SetIcon("up" + state, "SpinBox", Icons.Get("spin-up", Colors.White, 12));
            t.SetIcon("down" + state, "SpinBox", Icons.Get("spin-down", Colors.White, 12));
        }

        // Godot draws the old combined "updown" icon over the whole stepper when it is set: leave it empty so the
        // halves draw their own.
        t.SetIcon("updown", "SpinBox", new ImageTexture());
        foreach (string half in new[] { "up", "down" })
        {
            t.SetColor(half + "_icon_modulate", "SpinBox", TextDim);
            t.SetColor(half + "_hover_icon_modulate", "SpinBox", Text);
            t.SetColor(half + "_pressed_icon_modulate", "SpinBox", TextBright);
            t.SetColor(half + "_disabled_icon_modulate", "SpinBox", TextDisabled);
        }

        // Dense grids (the Match win grid): no stepper drawn, changed with the wheel, arrow keys or typing.
        const string scrub = "ScrubSpin";
        t.SetTypeVariation(scrub, "SpinBox");
        t.SetConstant("buttons_width", scrub, 0);
        foreach (string state in new[] { "", "_hover", "_pressed", "_disabled" })
        {
            t.SetIcon("up" + state, scrub, new ImageTexture());
            t.SetIcon("down" + state, scrub, new ImageTexture());
        }

        t.SetIcon("updown", scrub, new ImageTexture());
        foreach (string state in new[] { "", "_hovered", "_pressed", "_disabled" })
        {
            t.SetStylebox("up_background" + state, scrub, new StyleBoxEmpty());
            t.SetStylebox("down_background" + state, scrub, new StyleBoxEmpty());
        }
    }

    /// <summary>Sliders (a 4 px track, accent fill, round knob) and progress bars (a 4 px bar).</summary>
    private static void Ranges(Theme t)
    {
        foreach ((string type, bool vertical) in new[] { ("HSlider", false), ("VSlider", true) })
        {
            StyleBoxFlat Track(Color c) => Box(c, RadiusS, padX: vertical ? 2 : 0, padY: vertical ? 0 : 2);
            t.SetStylebox("slider", type, Track(new Color(Text, 0.12f)));
            // A vertical slider picks a position (a storey), not an amount: its track is not filled.
            t.SetStylebox("grabber_area", type, Track(vertical ? new Color(Text, 0.12f) : new Color(Accent, 0.55f)));
            t.SetStylebox("grabber_area_highlight", type, Track(vertical ? new Color(Text, 0.2f) : Accent));
            t.SetIcon("grabber", type, Icons.Get("slider-knob", Text, 12));
            t.SetIcon("grabber_highlight", type, Icons.Get("slider-knob", TextBright, 12));
            t.SetIcon("grabber_disabled", type, Icons.Get("slider-knob", TextDisabled, 12));
            t.SetIcon("tick", type, Tick(vertical));
            t.SetConstant("center_grabber", type, 0);
            t.SetConstant("grabber_offset", type, 0);
        }

        t.SetStylebox("background", "ProgressBar", Box(Field, 2, padX: 0, padY: 2));
        t.SetStylebox("fill", "ProgressBar", Box(Accent, 2, padX: 0, padY: 2));
        t.SetColor("font_color", "ProgressBar", TextDim);
        t.SetFontSize("font_size", "ProgressBar", FontSizeCaption);
    }

    /// <summary>A 1 px slider tick mark.</summary>
    private static ImageTexture Tick(bool vertical)
    {
        Image img = Image.CreateEmpty(vertical ? 4 : 1, vertical ? 1 : 4, false, Image.Format.Rgba8);
        img.Fill(new Color(TextDim, 0.4f));
        return ImageTexture.CreateFromImage(img);
    }

    private static void Lists(Theme t)
    {
        // Rows select and hover as one full-width band: no corner radius, so the cells of a row join up.
        StyleBoxFlat selected = Box(Selected, 0, padX: 0, padY: 0);
        StyleBoxFlat hovered = Box(RowHover, 0, padX: 0, padY: 0);
        StyleBoxFlat hoveredSelected = Box(new Color(Accent, 0.4f), 0, padX: 0, padY: 0);

        t.SetStylebox("panel", "Tree", Box(Field, RadiusM, 0, padX: 2, padY: 2));
        t.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        t.SetStylebox("selected", "Tree", selected);
        t.SetStylebox("selected_focus", "Tree", selected);
        t.SetStylebox("hovered", "Tree", hovered);
        t.SetStylebox("hovered_dimmed", "Tree", Box(new Color(Text, 0.04f), 0, padX: 0, padY: 0));
        t.SetStylebox("hovered_selected", "Tree", hoveredSelected);
        t.SetStylebox("hovered_selected_focus", "Tree", hoveredSelected);
        t.SetStylebox("cursor", "Tree", new StyleBoxEmpty());
        t.SetStylebox("cursor_unfocused", "Tree", new StyleBoxEmpty());
        t.SetStylebox("button_hover", "Tree", Box(HoverWash, RadiusS, padX: 0, padY: 0));
        t.SetStylebox("button_pressed", "Tree", Box(new Color(Text, 0.12f), RadiusS, padX: 0, padY: 0));
        t.SetStylebox("title_button_normal", "Tree", Box(Bar, 0, padX: SpaceM, padY: SpaceXs));
        t.SetStylebox("title_button_hover", "Tree", Box(Hover, 0, padX: SpaceM, padY: SpaceXs));
        t.SetStylebox("title_button_pressed", "Tree", Box(Hover, 0, padX: SpaceM, padY: SpaceXs));
        t.SetColor("title_button_color", "Tree", TextDim);
        t.SetFontSize("title_button_font_size", "Tree", FontSizeSmall);
        t.SetColor("font_color", "Tree", Text);
        t.SetColor("font_selected_color", "Tree", TextBright);
        t.SetColor("font_hovered_color", "Tree", Text);
        t.SetColor("font_hovered_selected_color", "Tree", TextBright);
        t.SetColor("font_disabled_color", "Tree", TextDisabled);
        t.SetColor("guide_color", "Tree", new Color(Text, 0.04f));
        t.SetColor("relationship_line_color", "Tree", new Color(Text, 0.1f));
        t.SetColor("children_hl_line_color", "Tree", new Color(Text, 0.1f));
        t.SetColor("parent_hl_line_color", "Tree", new Color(Text, 0.16f));
        t.SetConstant("draw_guides", "Tree", 0);
        t.SetConstant("draw_relationship_lines", "Tree", 0);
        t.SetConstant("v_separation", "Tree", SpaceXxs);
        t.SetConstant("h_separation", "Tree", SpaceS);
        t.SetConstant("item_margin", "Tree", 14);
        t.SetConstant("inner_item_margin_left", "Tree", SpaceXs);
        t.SetConstant("inner_item_margin_right", "Tree", SpaceXs);
        t.SetConstant("button_margin", "Tree", SpaceXxs);
        t.SetConstant("scroll_border", "Tree", SpaceXs);
        t.SetIcon("arrow", "Tree", Icons.Get("chevron-down", TextDim));
        t.SetIcon("arrow_collapsed", "Tree", Icons.Get("chevron-right", TextDim));
        t.SetIcon("arrow_collapsed_mirrored", "Tree", Icons.Get("chevron-left", TextDim));
        t.SetIcon("checked", "Tree", Icons.Get("check-on", Accent));
        t.SetIcon("unchecked", "Tree", Icons.Get("check-off", TextDim));
        t.SetIcon("indeterminate", "Tree", Icons.Get("check-mixed", Accent));
        t.SetIcon("checked_disabled", "Tree", Icons.Get("check-on", TextDisabled));
        t.SetIcon("unchecked_disabled", "Tree", Icons.Get("check-off", new Color(TextDisabled, 0.7f)));
        t.SetIcon("indeterminate_disabled", "Tree", Icons.Get("check-mixed", TextDisabled));
        t.SetIcon("select_arrow", "Tree", Icons.Get("chevron-down", TextDim));
        t.SetIcon("updown", "Tree", Icons.Get("updown", TextDim));

        t.SetStylebox("panel", "ItemList", Box(Field, RadiusM, 0, padX: SpaceXs, padY: SpaceXs));
        t.SetStylebox("focus", "ItemList", new StyleBoxEmpty());
        t.SetStylebox("selected", "ItemList", selected);
        t.SetStylebox("selected_focus", "ItemList", selected);
        t.SetStylebox("hovered", "ItemList", hovered);
        t.SetStylebox("hovered_selected", "ItemList", hoveredSelected);
        t.SetStylebox("hovered_selected_focus", "ItemList", hoveredSelected);
        t.SetStylebox("cursor", "ItemList", new StyleBoxEmpty());
        t.SetStylebox("cursor_unfocused", "ItemList", new StyleBoxEmpty());
        t.SetColor("font_color", "ItemList", Text);
        t.SetColor("font_selected_color", "ItemList", TextBright);
        t.SetColor("font_hovered_color", "ItemList", Text);
        t.SetColor("font_hovered_selected_color", "ItemList", TextBright);
        t.SetColor("guide_color", "ItemList", new Color(0, 0, 0, 0));
        t.SetConstant("v_separation", "ItemList", SpaceXs);
        t.SetConstant("h_separation", "ItemList", SpaceM);
    }

    /// <summary>The raised box of menus, popups and the command palette.</summary>
    private static StyleBoxFlat PopupBox(int padX, int padY)
    {
        StyleBoxFlat box = Box(Popup, RadiusL, 1, padX: padX, padY: padY);
        box.ShadowColor = Shadow;
        box.ShadowSize = SpaceM;
        box.ShadowOffset = new Vector2(0, 3);
        return box;
    }

    /// <summary>
    /// The box of menus, popups and tooltips that open as their own OS windows (embed_subwindows is off). A window is
    /// a rectangle, so rounded corners and a shadow would leave its corners and edges black: these are square with a
    /// crisp outline instead.
    /// </summary>
    private static StyleBoxFlat WindowPopupBox(int padX, int padY) => Box(Popup, 0, 1, BorderStrong, padX: padX, padY: padY);

    private static void Popups(Theme t)
    {
        t.SetStylebox("panel", "PopupMenu", WindowPopupBox(SpaceXs, 5));
        t.SetStylebox("panel", "PopupPanel", WindowPopupBox(SpaceXs, SpaceXs));
        t.SetStylebox("hover", "PopupMenu", Box(Accent, RadiusM, padX: 0, padY: 0));
        t.SetStylebox("separator", "PopupMenu", new StyleBoxLine { Color = Divider, Thickness = 1, GrowBegin = -SpaceS, GrowEnd = -SpaceS });
        t.SetStylebox("labeled_separator_left", "PopupMenu", new StyleBoxLine { Color = Divider, Thickness = 1, GrowBegin = -SpaceS });
        t.SetStylebox("labeled_separator_right", "PopupMenu", new StyleBoxLine { Color = Divider, Thickness = 1, GrowEnd = -SpaceS });
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", TextOnAccent);
        t.SetColor("font_disabled_color", "PopupMenu", TextDisabled);
        t.SetColor("font_accelerator_color", "PopupMenu", new Color(TextDim, 0.8f));
        t.SetColor("font_separator_color", "PopupMenu", TextDim);
        t.SetFontSize("font_separator_size", "PopupMenu", FontSizeCaption);
        t.SetConstant("v_separation", "PopupMenu", 5);
        t.SetConstant("h_separation", "PopupMenu", SpaceM);
        t.SetConstant("item_start_padding", "PopupMenu", 10);
        t.SetConstant("item_end_padding", "PopupMenu", 14);
        t.SetIcon("submenu", "PopupMenu", Icons.Get("chevron-right", TextDim));
        t.SetIcon("submenu_mirrored", "PopupMenu", Icons.Get("chevron-left", TextDim));
        t.SetIcon("checked", "PopupMenu", Icons.Get("check-on", Accent));
        t.SetIcon("unchecked", "PopupMenu", Icons.Get("check-off", new Color(TextDim, 0.6f)));
        t.SetIcon("checked_disabled", "PopupMenu", Icons.Get("check-on", TextDisabled));
        t.SetIcon("unchecked_disabled", "PopupMenu", Icons.Get("check-off", new Color(TextDisabled, 0.6f)));
        t.SetIcon("radio_checked", "PopupMenu", Icons.Get("radio-on", Accent));
        t.SetIcon("radio_unchecked", "PopupMenu", Icons.Get("radio-off", new Color(TextDim, 0.6f)));
        t.SetIcon("radio_checked_disabled", "PopupMenu", Icons.Get("radio-on", TextDisabled));
        t.SetIcon("radio_unchecked_disabled", "PopupMenu", Icons.Get("radio-off", new Color(TextDisabled, 0.6f)));

        t.SetStylebox("panel", "TooltipPanel", WindowPopupBox(SpaceM, 5));
        t.SetColor("font_color", "TooltipLabel", Text);
        t.SetFontSize("font_size", "TooltipLabel", FontSizeSmall);

        // A rich tooltip (toolbar buttons): the shortcut dim beside the name, the description as a caption below.
        t.SetTypeVariation("TooltipKey", "Label");
        t.SetColor("font_color", "TooltipKey", TextDim);
        t.SetFontSize("font_size", "TooltipKey", FontSizeSmall);
        t.SetTypeVariation("TooltipCaption", "Label");
        t.SetColor("font_color", "TooltipCaption", TextDim);
        t.SetFontSize("font_size", "TooltipCaption", FontSizeCaption);

        // Dialogs and the frame Godot draws around embedded windows: the frame is the title bar's colour with a
        // strong outline; the dialog body (AcceptDialog's panel) covers it below the title, so the title reads as
        // a bar.
        StyleBoxFlat frame = Box(Bar, RadiusXl, 1, BorderStrong, padX: 0, padY: 0);
        frame.ExpandMarginTop = 31;
        frame.ExpandMarginLeft = frame.ExpandMarginRight = frame.ExpandMarginBottom = 1;
        frame.ShadowColor = new Color(0, 0, 0, 0.5f);
        frame.ShadowSize = 14;
        frame.ShadowOffset = new Vector2(0, 4);
        t.SetStylebox("embedded_border", "Window", frame);
        t.SetStylebox("embedded_unfocused_border", "Window", frame);
        t.SetColor("title_color", "Window", Text);
        t.SetFont("title_font", "Window", UiFontSemibold);
        t.SetFontSize("title_font_size", "Window", FontSizeSmall);
        t.SetConstant("title_height", "Window", 30);
        t.SetIcon("close", "Window", Icons.Get("close", TextDim));
        t.SetIcon("close_pressed", "Window", Icons.Get("close", TextBright));

        // The body is square: a dialog is its own OS window, so rounded corners would show black behind them.
        StyleBoxFlat body = Borders(Box(Panel, 0, padX: SpaceXl, padY: SpaceXl), 0, 1, 0, 0);
        body.BorderColor = Divider;
        t.SetStylebox("panel", "AcceptDialog", body);
        t.SetConstant("buttons_separation", "AcceptDialog", SpaceM);
        t.SetConstant("buttons_min_width", "AcceptDialog", 80);
    }

    private static void Docks(Theme t)
    {
        foreach (string type in new[] { "TabContainer", "TabBar" })
        {
            t.SetStylebox("tab_selected", type, Tab(Panel, Accent));
            t.SetStylebox("tab_unselected", type, Tab(new Color(0, 0, 0, 0), null));
            t.SetStylebox("tab_hovered", type, Tab(new Color(Hover, 0.7f), null));
            t.SetStylebox("tab_disabled", type, Tab(new Color(0, 0, 0, 0), null));
            t.SetStylebox("tab_focus", type, new StyleBoxEmpty());
            t.SetColor("font_selected_color", type, Text);
            t.SetColor("font_unselected_color", type, TextDim);
            t.SetColor("font_hovered_color", type, TextBright);
            t.SetColor("font_disabled_color", type, TextDisabled);
            t.SetFontSize("font_size", type, FontSizeSmall);
            t.SetConstant("tab_separation", type, 0);
            t.SetConstant("side_margin", type, 0);
            t.SetConstant("icon_separation", type, SpaceXs);
            t.SetIcon("increment", type, Icons.Get("chevron-right", TextDim));
            t.SetIcon("decrement", type, Icons.Get("chevron-left", TextDim));
            t.SetIcon("increment_highlight", type, Icons.Get("chevron-right", Text));
            t.SetIcon("decrement_highlight", type, Icons.Get("chevron-left", Text));
            t.SetIcon("close", type, Icons.Get("close", TextDim));
        }

        // The dock overflow menu (TabContainer.SetPopup) shows as a "more" button at the end of the tab row. It is the
        // one way to the tabs that do not fit, so docks draw no scroll arrows beside it.
        t.SetIcon("menu", "TabContainer", Icons.Get("more", TextDim));
        t.SetIcon("menu_highlight", "TabContainer", Icons.Get("more", TextBright));
        foreach (string arrow in new[] { "increment", "decrement", "increment_highlight", "decrement_highlight" })
        {
            t.SetIcon(arrow, "TabContainer", new ImageTexture());
        }

        StyleBoxFlat dock = Box(Panel, RadiusL, padX: SpaceM, padY: SpaceM);
        dock.CornerRadiusTopLeft = 0;
        t.SetStylebox("panel", "TabContainer", dock);
        t.SetStylebox("tabbar_background", "TabContainer", new StyleBoxEmpty());

        // Tabs inside a panel (asset kinds): no box, an accent underline on the chosen one.
        const string sub = "SubTabs";
        t.SetTypeVariation(sub, "TabBar");
        StyleBoxFlat underline = Borders(Box(new Color(0, 0, 0, 0), 0, padX: SpaceM, padY: SpaceXs), 0, 0, 0, 2);
        underline.BorderColor = Accent;
        t.SetStylebox("tab_selected", sub, underline);
        t.SetStylebox("tab_unselected", sub, Empty(SpaceM, SpaceXs));
        t.SetStylebox("tab_hovered", sub, Box(HoverWash, RadiusS, padX: SpaceM, padY: SpaceXs));
        t.SetStylebox("tab_disabled", sub, Empty(SpaceM, SpaceXs));
        t.SetColor("font_selected_color", sub, Text);
        t.SetColor("font_unselected_color", sub, TextDim);
        t.SetColor("font_hovered_color", sub, Text);

        // Menu bar titles get room to breathe.
        t.SetStylebox("normal", "MenuBar", Empty(9, 3));
        t.SetStylebox("hover", "MenuBar", Box(Hover, RadiusM, padX: 9, padY: 3));
        t.SetStylebox("pressed", "MenuBar", Box(Hover, RadiusM, padX: 9, padY: 3));
        t.SetStylebox("hover_pressed", "MenuBar", Box(Hover, RadiusM, padX: 9, padY: 3));
        t.SetStylebox("disabled", "MenuBar", Empty(9, 3));
        t.SetStylebox("focus", "MenuBar", new StyleBoxEmpty());
        t.SetColor("font_color", "MenuBar", Text);
        t.SetColor("font_hover_color", "MenuBar", TextBright);
        t.SetColor("font_pressed_color", "MenuBar", TextBright);
        t.SetColor("font_hover_pressed_color", "MenuBar", TextBright);
        t.SetColor("font_disabled_color", "MenuBar", TextDisabled);
        t.SetConstant("h_separation", "MenuBar", 0);
    }

    /// <summary>A dock tab: the selected one is the panel colour with an accent line on top, so it reads as joined to
    /// the panel below it.</summary>
    private static StyleBoxFlat Tab(Color bg, Color? line)
    {
        StyleBoxFlat s = Box(bg, 0, padX: SpaceM, padY: 5);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = 5;
        if (line is { } l)
        {
            s.BorderColor = l;
            s.BorderWidthTop = 2;
        }

        return s;
    }

    private static void Labels(Theme t)
    {
        foreach (string type in new[] { "Label", "RichTextLabel" })
        {
            t.SetColor("font_color", type, Text);
        }

        t.SetColor("default_color", "RichTextLabel", Text);
        t.SetColor("selection_color", "RichTextLabel", Selected);

        t.SetTypeVariation("Dim", "Label");
        t.SetColor("font_color", "Dim", TextDim);

        // Links (release notes, the status bar's update link) are Accent, lighter on hover.
        t.SetColor("font_color", "LinkButton", Accent);
        t.SetColor("font_focus_color", "LinkButton", Accent);
        t.SetColor("font_hover_color", "LinkButton", AccentHover);
        t.SetColor("font_pressed_color", "LinkButton", AccentPressed);
        t.SetColor("font_hover_pressed_color", "LinkButton", AccentHover);

        // A section header: semibold and quiet, 12 px of space above it and a hairline across the panel below.
        const string header = "Header";
        t.SetTypeVariation(header, "Label");
        StyleBoxFlat rule = Borders(Box(new Color(0, 0, 0, 0), 0, padX: 0, padY: 0), 0, 0, 0, 1);
        rule.BorderColor = HeaderRule;
        rule.DrawCenter = false;
        rule.ContentMarginTop = SpaceL;
        rule.ContentMarginBottom = SpaceXs;
        t.SetStylebox("normal", header, rule);
        t.SetFont("font", header, UiFontSemibold);
        t.SetFontSize("font_size", header, FontSizeSmall);
        t.SetColor("font_color", header, TextHeading);

        // The selection's name at the top of a panel, a dialog's message, an empty state's title.
        t.SetTypeVariation("Title", "Label");
        t.SetFont("font", "Title", UiFontSemibold);
        t.SetFontSize("font_size", "Title", FontSizeTitle);
        t.SetColor("font_color", "Title", TextBright);

        // Hints, status lines, units, info rows.
        t.SetTypeVariation("Caption", "Label");
        t.SetFontSize("font_size", "Caption", FontSizeCaption);
        t.SetColor("font_color", "Caption", TextDim);

        // Codes and identifiers in tables.
        t.SetTypeVariation("Mono", "Label");
        t.SetFont("font", "Mono", MonoFont);
        t.SetFontSize("font_size", "Mono", FontSizeSmall);
        t.SetColor("font_color", "Mono", TextDim);
    }
}
