using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>A zone form constrained to the editor canvas. Only the body scrolls; its actions stay reachable.</summary>
internal sealed partial class MissionZoneDialog : Control
{
    private static int s_openCount;
    internal static bool IsOpen => s_openCount > 0;
    private readonly PanelContainer _box = new() { ThemeTypeVariation = "Palette" };
    private LineEdit _name = null!;
    private SpinBox _needed = null!;
    private CheckBox _sequential = null!;
    private Button _create = null!;
    private Action<string, int, bool>? _picked;

    public MissionZoneDialog() { }

    public static void Open(Control anchor, string parentName, string defaultName, Action<string, int, bool> picked)
    {
        Control host = anchor;
        while (host.GetParent() is Control parent) host = parent;
        var form = new MissionZoneDialog { Name = "NewMissionZone", _picked = picked, MouseFilter = MouseFilterEnum.Stop };
        host.AddChild(form);
        form.SetAnchorsPreset(LayoutPreset.FullRect);
        var scrim = new ColorRect { Color = EditorTheme.Scrim, MouseFilter = MouseFilterEnum.Ignore };
        form.AddChild(scrim);
        scrim.SetAnchorsPreset(LayoutPreset.FullRect);
        form.AddChild(form._box);
        var stack = new VBoxContainer();
        form._box.AddChild(stack);
        PanelUi.Title(stack, "Create mission zone");
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        stack.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(body);
        PanelUi.Caption(body, $"Add a zone inside {parentName}. Its objectives are available together; completing the required number finishes this zone.");
        body.AddChild(new Label { Text = "Zone name" });
        form._name = new LineEdit { Name = "ZoneName", Text = defaultName, SelectAllOnFocus = true };
        body.AddChild(form._name);
        var count = new HBoxContainer();
        body.AddChild(count);
        count.AddChild(new Label { Text = "Objectives to complete (0 = all)" });
        form._needed = PanelUi.Spin(count, 0, int.MaxValue, 1, 0,
            "Complete any N objectives in this zone to finish it. Add the objectives after creating the zone.", 64);
        form._needed.Name = "ZoneMinimum";
        form._sequential = new CheckBox { Name = "SequentialZones", Text = "Unlock zones one at a time", ButtonPressed = true,
            TooltipText = "Chains the parent mission: complete this zone's required objectives before the next zone unlocks. Turning this off makes the parent's children available together." };
        body.AddChild(form._sequential);
        PanelUi.Caption(body, "Order zones with the tree arrows. Use Parent mission to return here and add the next zone. These are logical groups; physical doors and routes use Completion actions.");
        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        stack.AddChild(footer);
        PanelUi.Button(footer, "Cancel", "Close without creating a zone", form.QueueFree);
        form._create = PanelUi.Primary(footer, "Create zone", "Create and link this zone as one undo step", form.Submit);
        form._name.TextChanged += text => form._create.Disabled = string.IsNullOrWhiteSpace(text);
        void Fit()
        {
            form._box.Size = new Vector2(Math.Min(520, host.Size.X - EditorTheme.SpaceM * 2), Math.Min(340, host.Size.Y - EditorTheme.SpaceM * 2));
            form._box.Position = (host.Size - form._box.Size) * 0.5f;
        }
        host.Resized += Fit;
        form.TreeExiting += () => host.Resized -= Fit;
        Fit();
        form._name.GrabFocus();
        form._name.SelectAll();
    }

    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) return;
        _needed.Apply();
        _picked?.Invoke(_name.Text.Trim(), (int)_needed.Value, _sequential.ButtonPressed);
        QueueFree();
    }

    public override void _EnterTree() => s_openCount++;
    public override void _ExitTree() => s_openCount--;

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            QueueFree();
            GetViewport().SetInputAsHandled();
        }
    }
}
