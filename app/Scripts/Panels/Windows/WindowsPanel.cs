using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Windows;
using OpenScctEditor.Shell;
using OpenScctEditor.Core;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

[Panel("Windows", DockSide.Left, Order = -18)]
public partial class WindowsPanel : EditorPanel
{
    private VBoxContainer settings = null!;
    private readonly List<(Button Tile, string Search, WindowDesign Preset)> tiles = [];
    private bool framed = true;
    private WindowDesign Design => WindowTools.Tool.Settings;
    protected override void Build()
    {
        PanelUi.Title(this, "Windows");
        PanelUi.Caption(this, "Drag a window into a view, or choose a preset and click Place Window. Right-click inside an existing frame to add fitted glass.");
        var tabs = new TabBar { ClipTabs = true, ThemeTypeVariation = "SubTabs", FocusMode = FocusModeEnum.None };
        tabs.AddTab("Framed Windows"); tabs.AddTab("Glass Only"); tabs.TabChanged += i => { framed = i == 0; Design.Framed = framed; foreach (var t in tiles) { var look = t.Preset.Clone(); look.Framed = framed; t.Tile.Icon = Thumbnail(look); } RebuildSettings(); }; AddChild(tabs);
        var bar = PanelUi.Toolbar(this); PanelUi.Search(bar, "Search windows", query => { foreach (var t in tiles) t.Tile.Visible = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => t.Search.Contains(word, StringComparison.OrdinalIgnoreCase)); });
        var scroll = PanelUi.Scroll(this); var flow = new HFlowContainer(); scroll.AddChild(flow);
        foreach (string shape in WindowDesign.Shapes) foreach (string finish in WindowDesign.Finishes)
        {
            var preset = new WindowDesign { Shape = shape, Finish = finish, Height = shape == "circle" ? 160 : 128, Framed = true };
            string title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(shape) + " · " + finish;
            var tile = new Button { Text = title, Icon = Thumbnail(preset), IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top, CustomMinimumSize = new Vector2(128, 98), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, ExpandIcon = false, TooltipText = title + "\nSelect to change dimensions below, or drag into a view. Glass breaks when shot or crashed through." };
            tile.Pressed += () => Choose(preset);
            tile.SetDragForwarding(Callable.From<Vector2, Variant>(_ => Drag(tile, preset)), Callable.From<Vector2, Variant, bool>((_, _) => false), Callable.From<Vector2, Variant>((_, _) => { }));
            flow.AddChild(tile); tiles.Add((tile, title, preset));
        }
        var actions = PanelUi.Toolbar(this); PanelUi.Button(actions, "Place Window", "Preview, then click to place. R rotates; Escape cancels.", () => { WindowTools.Tool.Cancel(); ToolManager.Instance!.Activate("windows"); });
        settings = PanelUi.Scroll(this); EditorLifetime.Defer(RebuildSettings);
    }
    private void Choose(WindowDesign preset) { WindowTools.Tool.Settings = preset.Clone(); Design.Framed = framed; RebuildSettings(); }
    private void RebuildSettings()
    {
        if (settings is null) return;
        foreach (Node child in settings.GetChildren()) { settings.RemoveChild(child); child.QueueFree(); }
        WindowFields.Build(settings, Design, edit => { var next = Design.Clone(); edit(next); next.Validate(); WindowTools.Tool.Settings = next; }, Context.SetStatus, () => EditorLifetime.Defer(RebuildSettings));
    }
    private Variant Drag(Control tile, WindowDesign preset)
    {
        if (Design.Shape != preset.Shape || Design.Finish != preset.Finish) Choose(preset);
        var session = Context.Session; var design = Design.Clone();
        tile.SetDragPreview(new Label { Text = "Breakable " + design.Shape + " window" });
        PrefabDragData data = null!;
        data = new PrefabDragData("Window", _ => {
            if (Context.Session != session || data.WindowPlacement is not { } a) return;
            Context.Execute(new OpenScct.Editor.Core.Commands.AddObjectsCommand("Add Window", actors: [a.Clone()])); Context.Selection.Set([ObjectRef.Actor(a.Id)]);
            Context.SetStatus("Added breakable window. Edit dimensions in Inspector; Shift+S shows resize handles.");
        }) { WindowPreview = design };
        return Variant.From(data);
    }
    public static Texture2D Thumbnail(WindowDesign design)
    {
        var p = WindowGeometry.Outline(design); double minY = p.Min(v => v.Y), maxY = p.Max(v => v.Y), minZ = p.Min(v => v.Z), maxZ = p.Max(v => v.Z);
        double scale = Math.Min(90 / (maxY - minY), 55 / (maxZ - minZ));
        string points = string.Join(' ', p.Select(v => string.Create(CultureInfo.InvariantCulture, $"{55 + v.Y * scale:0.##},{34 - v.Z * scale:0.##}")));
        string color = design.Finish == "frosted" ? "#b8cdd8" : design.Finish == "tinted" ? "#3c687f" : "#76b9d0";
        var img = new Image(); img.LoadSvgFromString($"<svg xmlns='http://www.w3.org/2000/svg' width='96' height='62' viewBox='0 0 110 68'><polygon points='{points}' fill='{color}' fill-opacity='.65' stroke='#9da8b4' stroke-width='{(design.Framed ? 5 : 1)}'/><path d='M43 27l12 -10M48 39l18 -17' stroke='#d7edf3' stroke-width='1.5' opacity='.8'/></svg>");
        return ImageTexture.CreateFromImage(img);
    }
}

public static class WindowFields
{
    public static void Build(Control parent, WindowDesign d, Action<Action<WindowDesign>> edit, Action<string> error, Action? rebuild = null)
    {
        bool Apply(Action<WindowDesign> action) { try { edit(action); return true; } catch (ArgumentException ex) { error(ex.Message); return false; } }
        bool linked = d.Binding is not null;
        if (linked) PanelUi.Caption(parent, d.Binding!.NeedsRefit ? "Frame geometry changed. Refit this pane, or detach it to edit independently." : "Fitted to frame. Resize the frame to resize the glass; detach for independent dimensions.");
        var shapeRow = PanelUi.Row(parent, "Shape", "Choose a preset shape, or keep the detected frame outline", 110);
        var shape = new OptionButton { Disabled = linked };
        foreach (string name in WindowDesign.Shapes) shape.AddItem(name);
        if (d.Outline.Count > 0) { shape.AddItem("Fitted outline"); shape.Select(WindowDesign.Shapes.Length); }
        else shape.Select(Array.IndexOf(WindowDesign.Shapes, d.Shape));
        shape.ItemSelected += i => { if (i < WindowDesign.Shapes.Length && Apply(x => { x.Shape = WindowDesign.Shapes[(int)i]; x.Outline.Clear(); if (x.Shape == "circle") x.Height = x.Width; })) rebuild?.Invoke(); };
        shapeRow.AddChild(shape);
        void Number(string label, double value, double min, double max, Action<WindowDesign, double> set, bool locked = false, double step = .25)
        {
            var row = PanelUi.Row(parent, label, label + " in Unreal units", 110); var field = new SpinBox { MinValue = min, MaxValue = max, Step = min == .125 ? .125 : step, Value = value, Editable = !locked, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, UpdateOnTextChanged = false }; row.AddChild(field);
            double accepted = value; field.ValueChanged += v => { if (Apply(x => set(x, v))) accepted = v; else field.SetValueNoSignal(accepted); };
        }
        Number(d.Shape == "circle" ? "Diameter" : "Width", d.Width, 2, 262144, (x, v) => { double factor = v / x.Width; x.Width = v; if (x.Outline.Count > 0) x.Outline = x.Outline.Select(p => p with { Y = p.Y * factor }).ToList(); if (x.Shape == "circle") x.Height = v; }, linked);
        if (d.Shape != "circle") Number("Height", d.Height, 2, 262144, (x, v) => { double factor = v / x.Height; x.Height = v; if (x.Outline.Count > 0) x.Outline = x.Outline.Select(p => p with { Z = p.Z * factor }).ToList(); }, linked);
        Number("Glass thickness", d.Thickness, .125, 1024, (x, v) => x.Thickness = v);
        Number("Edge clearance", d.Clearance, 0, 1024, (x, v) => x.Clearance = v);
        Number("Glass depth", d.DepthOffset, -1024, 1024, (x, v) => x.DepthOffset = v);
        if (d.Shape == "polygon") Number("Polygon sides", d.Sides, 3, 32, (x, v) => x.Sides = (int)v, linked, step: 1);
        if (d.Framed) { Number("Frame width", d.FrameWidth, .125, 1024, (x, v) => x.FrameWidth = v); Number("Frame depth", d.FrameDepth, .125, 262144, (x, v) => x.FrameDepth = v); }
        var finishes = PanelUi.Row(parent, "Glass finish", "Clear, tinted or cloudy frosted glass", 110); var finish = new OptionButton(); foreach (string f in WindowDesign.Finishes) finish.AddItem(f); finish.Select(Array.IndexOf(WindowDesign.Finishes, d.Finish)); finishes.AddChild(finish); finish.ItemSelected += i => Apply(x => x.Finish = WindowDesign.Finishes[(int)i]);
        var tintRow = PanelUi.Row(parent, "Glass tint", "Tint of the generated glass material", 110); var tint = new ColorPickerButton { Color = new Color((float)d.Tint[0], (float)d.Tint[1], (float)d.Tint[2]), EditAlpha = false, CustomMinimumSize = new Vector2(80, 26) }; tintRow.AddChild(tint); tint.ColorChanged += c => Apply(x => x.Tint = [c.R, c.G, c.B]);
        void Material(string title, string value, Action<WindowDesign, string> set)
        { var row = PanelUi.Row(parent, title, "Material asset path; leave glass empty to use the chosen finish", 110); var field = new LineEdit { Text = value, PlaceholderText = "Automatic", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; row.AddChild(field); string accepted = value; void Submit() { string text = field.Text.Trim(); if (text != accepted && Apply(x => set(x, text))) accepted = text; } field.TextSubmitted += _ => Submit(); field.FocusExited += Submit; }
        Material("Glass material", d.GlassMaterial, (x, v) => x.GlassMaterial = v);
        if (d.Framed) Material("Frame material", d.FrameMaterial, (x, v) => x.FrameMaterial = string.IsNullOrEmpty(v) ? "Editor.WindowFrame" : v);
    }
}
