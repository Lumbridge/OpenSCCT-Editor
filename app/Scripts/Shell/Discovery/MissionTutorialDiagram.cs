using Godot;
using OpenScctEditor.Panels;

namespace OpenScctEditor.Shell;

/// <summary>Read-only reference shared by the objective, chain and zone lessons.</summary>
public partial class MissionTutorialDiagram : Control
{
    private int _kind;
    public MissionTutorialDiagram() { }
    private MissionTutorialDiagram(int kind, int height)
    {
        _kind = kind;
        CustomMinimumSize = new Vector2(620, height);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Resized += QueueRedraw;
    }

    public static void Open(Control anchor, Action? closed = null)
    {
        DiscoveryNavigation.Invalidate();
        Control host = anchor;
        while (host.GetParent() is Control parent) host = parent;
        var overlay = new MissionDiagramOverlay { Name = "MissionDiagram", Color = new Color(0, 0, 0, 0.45f), ZIndex = 1002, Closed = closed };
        host.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dialog = new PanelContainer { ThemeTypeVariation = "Palette" };
        overlay.AddChild(dialog);
        void Fit()
        {
            dialog.Size = new Vector2(Math.Min(760, overlay.Size.X - 40), Math.Min(680, overlay.Size.Y - 40));
            dialog.Position = (overlay.Size - dialog.Size) / 2;
        }
        overlay.Resized += Fit;
        var stack = new VBoxContainer();
        dialog.AddChild(stack);
        var title = PanelUi.Caption(stack, "Missions, objectives and zones");
        title.AddThemeFontOverride("font", EditorTheme.UiFontSemibold);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, SizeFlagsVertical = SizeFlags.ExpandFill };
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(640, 0) };
        body.AddThemeConstantOverride("separation", EditorTheme.SpaceM);
        stack.AddChild(scroll);
        scroll.AddChild(body);
        void Section(string title, string explanation, int kind, int height)
        {
            var heading = PanelUi.Caption(body, title);
            heading.AddThemeFontOverride("font", EditorTheme.UiFontSemibold);
            PanelUi.Caption(body, explanation);
            body.AddChild(new MissionTutorialDiagram(kind, height));
        }
        Section("1. Devices complete objectives; objectives complete missions",
            "Arrows point from a parent to the children it lists. A mission's Objectives list contains tasks; an objective's Triggers list contains devices. "
            + "A plain objective completes when any one of its devices is taken. Put tasks that must both be completed in separate objectives.", 0, 220);
        Section("2. Chain tasks in order",
            "With bChained on, the mission activates its children in list order. Only the current child is active. With bChained off, tasks activate together: "
            + "MinimumObjectives = 0 requires all; a positive minimum allows that many. RandomObjectives can limit the offered tasks. Minimum and random counts do not apply to a chain.", 1, 105);
        Section("3. Chain zones, with several tasks inside each zone",
            "Use Story (GM_Multi, 5) on the top mission and Undefined (GM_Undefined, 0) on its zone missions. The parent activates each zone. "
            + "Here the top mission chains Zone A then Zone B, while each zone offers its own objectives together. Each zone can also chain its tasks.", 2, 260);
        PanelUi.Caption(body, "To reuse an existing map: keep its devices and objectives, link each task into its intended zone, then unlink its former parent row with ×. "
            + "Link the zones into the top mission. Give each device and task one parent. Mission zones group gameplay; rooms and BSP zones are separate. "
            + "Use Completion actions on a zone to open a door, change lights or show a message. Build and play Story from the start to test activation and the final win.");
        Button close = PanelUi.Button(stack, "Close", "Return to your tutorial", overlay.Close);
        close.FocusMode = FocusModeEnum.All;
        close.GrabFocus();
        Fit();
    }

    public override void _Draw()
    {
        float offset = Math.Max(0, (Size.X - 620) / 2);
        Vector2 P(float x, float y) => new(x + offset, y);
        void Box(float x, float y, float width, string title, string? detail = null)
        {
            float height = detail is null ? 36 : 50;
            DrawRect(new Rect2(P(x, y), new Vector2(width, height)), EditorTheme.Background);
            DrawRect(new Rect2(P(x, y), new Vector2(width, height)), EditorTheme.Accent, false, 1);
            DrawString(GetThemeDefaultFont(), P(x + 10, y + 22), title, fontSize: EditorTheme.FontSizeSmall, modulate: EditorTheme.Text);
            if (detail is not null) DrawString(GetThemeDefaultFont(), P(x + 10, y + 41), detail, fontSize: EditorTheme.FontSizeSmall, modulate: EditorTheme.TextDim);
        }
        void Arrow(float x, float y, float toX, float toY)
        {
            Vector2 from = P(x, y), to = P(toX, toY);
            DrawLine(from, to, EditorTheme.Accent, 2, true);
            Vector2 direction = (to - from).Normalized(), normal = new(-direction.Y, direction.X);
            DrawLine(to, to - direction * 8 + normal * 4, EditorTheme.Accent, 2, true);
            DrawLine(to, to - direction * 8 - normal * 4, EditorTheme.Accent, 2, true);
        }
        if (_kind == 0)
        {
            Box(215, 4, 190, "Story mission", "Objectives → tasks");
            Arrow(260, 54, 145, 84); Arrow(360, 54, 475, 84);
            Box(35, 84, 220, "Objective: disable server", "Triggers → computer");
            Box(365, 84, 220, "Objective: extract data", "Triggers → carry disk");
            Arrow(145, 134, 145, 166); Arrow(475, 134, 475, 166);
            Box(35, 166, 220, "Computer (hack)");
            Box(365, 166, 220, "Carry disk → drop zone");
        }
        else if (_kind == 1)
        {
            Box(8, 25, 170, "1. Hack terminal"); Arrow(178, 43, 222, 43);
            Box(222, 25, 170, "2. Plant bomb"); Arrow(392, 43, 436, 43);
            Box(436, 25, 175, "3. Extract disk");
            DrawString(GetThemeDefaultFont(), P(8, 90), "Complete current objective → activate next → complete mission", fontSize: EditorTheme.FontSizeSmall, modulate: EditorTheme.TextDim);
        }
        else
        {
            Box(195, 4, 230, "Top: Story mission", "bChained = true · A then B");
            Arrow(250, 54, 145, 90); Arrow(370, 54, 475, 90);
            Box(25, 90, 240, "1. Zone A · Undefined", "bChained = false · need 2 of 3");
            Box(355, 90, 240, "2. Zone B · Undefined", "bChained = false · need all");
            Arrow(145, 140, 145, 176); Arrow(475, 140, 475, 176);
            Box(25, 176, 240, "Objectives A1, A2, A3", "Each has its own device(s)");
            Box(355, 176, 240, "Objectives B1, B2", "Each has its own device(s)");
            DrawString(GetThemeDefaultFont(), P(25, 250), "Zone A complete → Zone B activates → final zone complete → Story win", fontSize: EditorTheme.FontSizeSmall, modulate: EditorTheme.TextDim);
        }
    }
}

public partial class MissionDiagramOverlay : ColorRect
{
    public Action? Closed { get; init; }
    public void Close() { QueueFree(); Closed?.Invoke(); }
    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        Close();
        GetViewport().SetInputAsHandled();
    }
}
