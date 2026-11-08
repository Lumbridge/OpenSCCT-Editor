using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Panels;

/// <summary>The existing prefab browser with a small building kit above it.</summary>
[Panel("Building", DockSide.Left, Order = -20)]
public partial class BuildingPanel : PrefabsPanel
{
    public static BuildingPanel? Instance { get; private set; }
    private ScrollContainer? _scroll;
    protected override bool Friendly => true;
    protected override void Build()
    {
        Instance = this;
        PanelUi.Title(this, "Build your map");
        Label hint = PanelUi.Caption(this, "Choose a shape, use Place Here to position it, and drag handles to size it. Add/Subtract commits the preview.");
        void RefreshHint()
        {
            string rotate = Context.FindAction("tools.rotate") is { } action ? ShortcutBindings.Text(action) ?? "the Rotate tool" : "the Rotate tool";
            hint.Text = $"Choose a shape, use Place Here to position it, and drag handles to size it. Add/Subtract commits the preview. Rotate selected objects with {rotate}.";
        }
        EditorLifetime.Defer(RefreshHint);
        ShortcutBindings.Changed += RefreshHint;
        PanelUi.Button(this, "Geometry palettes", "Browse common floors, walls and builder shapes", () => DockTabs.ShowPanel("Geometry"));
        var shapes = new HFlowContainer();
        AddChild(shapes);
        foreach (var (name, kind, icon, help) in new[]
        {
            ("Room", "room", "grid", "Carve a room out of the solid world. Connect rooms with doorways or corridors."),
            ("Corridor", "corridor", "grid", "Carve a walkable connection between rooms."),
            ("Doorway", "doorway", "grid", "Carve an opening through a wall. It must reach the open space on both sides."),
            ("Stairs", "stairs", "grid", "Create steps between different floor heights. Leave clear space above them."),
        })
        {
            string action = "brush.kind." + kind;
            Button button = PanelUi.Button(shapes, name, help, () => { if (kind == "stairs") DockTabs.ShowPanel("Stairs"); else Context.Run(action); });
            button.Icon = Icons.Get(icon, EditorTheme.TextDim);
        }
        var actions = PanelUi.Toolbar(this);
        PanelUi.Button(actions, "Place shape", "Commit the builder preview to your map. Undo removes the whole placement.", () => Context.Run("brush.subtract"));
        PanelUi.Button(actions, "Connect room", "Add a room with a doorway and light beside the selected carved room.", () => Context.Run("guide.connectRoom"));
        PanelUi.Button(actions, "Lights", "Choose a light, then adjust brightness and range in the Inspector.", () =>
        {
            if (DockTabs.ShowPanel("Lights")) return;
            BeginnerEdits.Apply(Context, "Place light", [ApiJson.Obj(("op", ApiJson.Str("create")),
                ("class", ApiJson.Str("Light")), ("at", ApiJson.Vec(Context.PlacementPoint?.Invoke() ?? Vec3.Zero)))]);
            DockTabs.ShowPanel("Inspector");
        });
        PanelUi.Button(actions, "First-map guide", "Open your map's checklist", () => Context.Run("help.firstMap"));
        PanelUi.Header(this, "Gameplay pieces");
        PanelUi.Button(this, "Link missions & objectives…", "Choose missions, objectives and their devices by name", MissionsPanel.OpenForSelection);
        base.Build();
        // The entire kit can scroll on short/high-DPI windows, including its headings and selected-piece settings.
        var controls = GetChildren().OfType<Control>().ToList();
        VBoxContainer body = PanelUi.Scroll(this);
        _scroll = PanelUi.ScrollOf(body);
        foreach (Control control in controls) { RemoveChild(control); body.AddChild(control); }
    }

    protected override void DetailsShown(Control heading) => EditorLifetime.Defer(() =>
    {
        if (_scroll is not null && IsInstanceValid(heading)) _scroll.EnsureControlVisible(heading);
    });

    public void Pick(string name)
    {
        DockTabs.ShowPanel("Building");
        Reveal(name);
    }
}
