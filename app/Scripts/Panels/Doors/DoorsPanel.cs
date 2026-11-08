using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

[Panel("Doors", DockSide.Left, Order = -17)]
public partial class DoorsPanel : EditorPanel
{
    private VBoxContainer fields = null!;
    private readonly List<Button> tiles = [];
    protected override void Build()
    {
        PanelUi.Title(this, "Doors");
        PanelUi.Caption(this, "Choose a style, then drag it into a view or click Place Door. Point at the bottom of a wall to cut an opening. Blue lines show the leaf; faint lines show where it opens.");
        var body = PanelUi.Scroll(this); var flow = new HFlowContainer(); body.AddChild(flow);
        foreach (string style in DoorDesign.Styles)
        {
            var button = new Button { Text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(style), Icon = Thumbnail(style), ToggleMode = true, IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top, CustomMinimumSize = new(112, 110), TooltipText = "Choose or drag this door style", ButtonPressed = style == "panelled" };
            void Choose() { DoorTools.Tool.Settings.Style = style; foreach (var tile in tiles) tile.ButtonPressed = tile == button; Rebuild(); }
            button.Pressed += Choose; flow.AddChild(button); tiles.Add(button);
            button.SetDragForwarding(Callable.From<Vector2, Variant>(_ => { if (DoorTools.Tool.Settings.Style != style) Choose(); return Drag(button); }), Callable.From<Vector2, Variant, bool>((_, _) => false), Callable.From<Vector2, Variant>((_, _) => { }));
        }
        PanelUi.Header(body, "Door assemblies");
        var pairs = PanelUi.Toolbar(body);
        PanelUi.Button(pairs, "Hinged pair", "Open the editable double-door prefab; both leaves share an automatic player sensor", () => PrefabsPanel.ShowPrefab("double_hinged_door"));
        PanelUi.Button(pairs, "Sliding pair", "Open the split sliding-door prefab; both leaves open together", () => PrefabsPanel.ShowPrefab("double_sliding_door"));
        PanelUi.Button(pairs, "Automatic access", "Configure spies, mercs, both or neither separately on each side", () => PrefabsPanel.ShowPrefab("automatic_door"));
        PanelUi.Button(pairs, "Hack panel", "A timed hack panel with a movable link to its door; also available on either double-door assembly", () => PrefabsPanel.ShowPrefab("hackable_door"));
        PanelUi.Primary(body, "Place Door", "Click in a viewport to place a door; R rotates and Escape cancels", () => { DoorTools.Tool.Cancel(); ToolManager.Instance!.Activate("doors"); });
        PanelUi.Button(body, "Learn door tools", "Start the built-in door tutorial", () => Context.Run("tutorial.doors"));
        fields = new VBoxContainer(); body.AddChild(fields); EditorLifetime.Defer(Rebuild);
    }
    private void Rebuild()
    {
        foreach (var child in fields.GetChildren()) { fields.RemoveChild(child); child.QueueFree(); }
        DoorFields.Build(fields, DoorTools.Tool.Settings, edit => { var next = DoorTools.Tool.Settings.Clone(); edit(next); next.Validate(); DoorTools.Tool.Settings = next; }, Context.SetStatus, () => EditorLifetime.Defer(Rebuild));
    }
    private Variant Drag(Control tile)
    {
        var session = Context.Session; PrefabDragData data = null!;
        tile.SetDragPreview(new Label { Text = DoorTools.Tool.Settings.Style + " door" });
        data = new PrefabDragData("Door", _ =>
        {
            if (Context.Session != session || data.DoorPlacement is not { } door) return;
            Context.Execute(new OpenScct.Editor.Core.Commands.AddObjectsCommand("Add Door", actors: [door.Clone()])); Context.Selection.Set([ObjectRef.Actor(door.Id)]);
            Context.SetStatus("Added door. Edit dimensions and opening behaviour in Inspector.");
        }) { DoorPreview = DoorTools.Tool.Settings.Clone() };
        return Variant.From(data);
    }
    public static Texture2D Thumbnail(string style)
    {
        var c = DoorAssets.Colour("Editor.Door_" + style); string colour = $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        string detail = style switch
        {
            "panelled" => "<path d='M32 20h31v24H32zM32 52h31v27H32z' fill='#704b31' stroke='#bb8c65'/>",
            "glazed" => "<path d='M31 18h33v35H31z' fill='#73b3cb'/><path d='M34 43l21-20' stroke='#d7f2fc'/>",
            "louvred" => "<path d='M32 25h31M32 32h31M32 39h31M32 46h31M32 53h31M32 60h31M32 67h31M32 74h31' stroke='#c1d0c6' stroke-width='3'/>",
            "industrial" => "<path d='M27 25h41M27 49h41M27 73h41' stroke='#9fadb1' stroke-width='4'/>",
            "security" => "<path d='M27 25h41M27 49h41M27 73h41M33 15v68M62 15v68' stroke='#a2adb8' stroke-width='4'/>", _ => "",
        };
        var image = new Image(); image.LoadSvgFromString($"<svg xmlns='http://www.w3.org/2000/svg' width='96' height='92'><path d='M17 88V6h61v82h-7V13H24v75z' fill='#697988'/><path d='M25 14h45v72H25z' fill='{colour}' stroke='#c0c9ce'/>{detail}<path d='M60 49h6v4h-6z' fill='#e2d3a1'/><path d='M12 89h73' stroke='#44515d'/></svg>"); return ImageTexture.CreateFromImage(image);
    }
}

public static class DoorFields
{
    public static void Build(Control parent, DoorDesign d, Action<Action<DoorDesign>> edit, Action<string> error, Action? rebuild = null)
    {
        bool Apply(Action<DoorDesign> action) { try { edit(action); return true; } catch (ArgumentException ex) { error(ex.Message); return false; } }
        void Pick(string title, string value, string[] options, Action<DoorDesign, string> set, string help)
        {
            var row = PanelUi.Row(parent, title, help, 100); var pick = PanelUi.Options(row, help); pick.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            string Label(string option) => option switch { "BumpOpenTimed" => "Contact · timed", "TriggerOpenTimed" => "Trigger · timed", "TriggerToggle" => "Trigger · toggle", "TriggerControl" => "Trigger · held", "TriggerOpenOnceOnly" => "Trigger · once", _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(option) };
            foreach (var option in options) pick.AddItem(Label(option)); pick.Select(Array.IndexOf(options, value));
            pick.ItemSelected += i => { if (Apply(x => set(x, options[(int)i]))) rebuild?.Invoke(); };
        }
        void Number(string label, double value, double min, double max, Action<DoorDesign, double> set, string help, double step = 1)
        {
            var row = PanelUi.Row(parent, label, help, 100); var spin = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, UpdateOnTextChanged = false }; row.AddChild(spin);
            double accepted = value; spin.ValueChanged += v => { if (Apply(x => set(x, v))) accepted = v; else spin.SetValueNoSignal(accepted); };
        }
        Pick("Style", d.Style, DoorDesign.Styles, (x, v) => x.Style = v, "Built-in door finish and details");
        Pick("Movement", d.Motion, DoorDesign.Motions, (x, v) => x.Motion = v, "Swing about a jamb or slide out of the opening");
        Number("Width", d.Width, 48, 8192, (x, v) => x.Width = v, "Clear doorway width in Unreal units");
        Number("Height", d.Height, 64, 8192, (x, v) => x.Height = v, "Clear doorway height");
        Number("Thickness", d.Thickness, 1, 512, (x, v) => x.Thickness = v, "Door leaf thickness");
        if (d.Motion.StartsWith("swing", StringComparison.Ordinal)) Number("Open angle", d.OpenAngle, 1, 175, (x, v) => x.OpenAngle = v, "Degrees the door swings");
        else Number("Travel", d.Travel, 0, 16384, (x, v) => x.Travel = v, "0 automatically clears the opening; otherwise distance in units");
        var reverse = new CheckBox { Text = "Reverse opening", ButtonPressed = d.Reverse }; parent.AddChild(reverse); reverse.Toggled += v => Apply(x => x.Reverse = v);
        Pick("Activation", d.Behaviour, DoorDesign.Behaviours, (x, v) => x.Behaviour = v, "Bump opens on contact. Trigger modes need a switch or trigger linked to the door.");
        Number("Open time", d.MoveTime, .1, 120, (x, v) => x.MoveTime = v, "Seconds to travel between closed and open", .1);
        Number("Stay open", d.StayOpen, 0, 600, (x, v) => x.StayOpen = v, "Seconds before a timed door closes", .1);
        var framed = new CheckBox { Text = "Include frame", ButtonPressed = d.Framed }; parent.AddChild(framed); framed.Toggled += v => { if (Apply(x => x.Framed = v)) rebuild?.Invoke(); };
        if (d.Framed) { Number("Frame width", d.FrameWidth, 1, 128, (x, v) => x.FrameWidth = v, "Width of jambs and lintel"); Number("Frame depth", d.FrameDepth, 1, 2048, (x, v) => x.FrameDepth = v, "Depth through the wall"); }
        Number("Edge gap", d.Clearance, 0, Math.Min(d.Width, d.Height) / 2 - 1, (x, v) => x.Clearance = v, "Clearance between the leaf and frame", .25);
        void Material(string title, string value, Action<DoorDesign, string> set)
        {
            var row = PanelUi.Row(parent, title, "Material asset path; empty uses the built-in finish", 112); var field = new LineEdit { Text = value, PlaceholderText = "Automatic", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; row.AddChild(field);
            string accepted = value; void Submit() { string text = field.Text.Trim(); if (text != accepted && Apply(x => set(x, text))) accepted = text; } field.TextSubmitted += _ => Submit(); field.FocusExited += Submit;
        }
        Material("Door material", d.Material, (x, v) => x.Material = v);
        if (d.Framed) Material("Frame material", d.FrameMaterial, (x, v) => x.FrameMaterial = v.Length > 0 ? v : "Editor.DoorFrame");
        if (d.Behaviour != "BumpOpenTimed") PanelUi.Caption(parent, "Link a switch or trigger to this door in Gameplay. Its Tag is the event destination.");
    }
}
