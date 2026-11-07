using Godot;
using OpenScctEditor.Core;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Stairs;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

[Panel("Stairs", DockSide.Left, Order = -19)]
public partial class StairsPanel : EditorPanel
{
    private ItemList _list = null!;
    private VBoxContainer _settings = null!;
    private readonly List<StairDesign> _presets = [];
    protected override void Build()
    {
        PanelUi.Title(this, "Stairs");
        PanelUi.Caption(this, "Drag a starter staircase into a view, or connect two floors. Every staircase stays editable.");
        _list = new ItemList { IconMode = ItemList.IconModeEnum.Top, MaxColumns = 2, FixedIconSize = new Vector2I(110, 72),
            CustomMinimumSize = new Vector2(0, 245), SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(_list);
        foreach (string layout in StairDesign.Layouts) foreach (bool rounded in new[] { false, true })
        {
            var s = new StairDesign { Layout = layout, TurnDegrees = layout == "spiral" ? 360 : 90,
                Overhang = rounded ? 4 : 0, EdgeRadius = rounded ? 4 : 0 };
            _presets.Add(s);
            _list.AddItem($"{(layout is "l" or "u" ? layout.ToUpperInvariant() : char.ToUpperInvariant(layout[0]) + layout[1..])} · {(rounded ? "Rounded" : "Square")}", Thumbnail(s));
            _list.SetItemTooltip(_list.ItemCount - 1, rounded
                ? "Rounded tread nosings with a 4-unit radius and overhang. Spiral treads and risers are connected."
                : "Square tread nosings. Spiral treads and risers are connected.");
        }
        _list.ItemSelected += i => Choose((int)i);
        _list.SetDragForwarding(Callable.From<Vector2, Variant>(Drag), Callable.From((Vector2 _, Variant _) => false), Callable.From((Vector2 _, Variant _) => { }));
        var actions = PanelUi.Toolbar(this);
        PanelUi.Button(actions, "Place stairs", "Preview a staircase, then click to place", () => Start(false));
        PanelUi.Button(actions, "Connect floors", "Pick a bottom floor connection, then the upper floor connection", () => Start(true));
        _settings = PanelUi.Scroll(this);
        _list.Select(0); EditorLifetime.Defer(() => Choose(0));
    }
    private StairTool Tool => StairTools.Tool!;
    private void Start(bool connect) { Tool.Cancel(); Tool.Connect = connect; ToolManager.Instance!.Activate("stairs"); }
    private void Choose(int index)
    {
        if (StairTools.Tool is null) return;
        Tool.Settings = _presets[index].Clone();
        foreach (Node n in _settings.GetChildren()) { _settings.RemoveChild(n); n.QueueFree(); }
        StairFields.Build(_settings, Tool.Settings, change => {
            var next = Tool.Settings.Clone(); change(next); next.Validate(); Tool.Settings = next;
        }, message => Context.SetStatus(message), () => Tool.Settings);
    }
    private Variant Drag(Vector2 at)
    {
        int index = _list.GetItemAtPosition(at, true); if (index < 0) return default;
        if (!_list.IsSelected(index)) { _list.Select(index); Choose(index); }
        StairDesign design = Tool.Settings.Clone(); var session = Context.Session;
        _list.SetDragPreview(new Label { Text = "Editable " + design.Layout + " stairs" });
        PrefabDragData data = null!;
        data = new PrefabDragData("Editable staircase", point => {
            if (session != Context.Session) return;
            try { var cmd = StairCommands.Place(Context.Project, design, point, new Rot(0, data.StairYaw, 0)); Context.Execute(cmd); Context.Selection.Set(cmd.Added); }
            catch (ArgumentException ex) { Context.SetStatus(ex.Message); }
        }) { StairPreview = design, StairYaw = Tool.Yaw };
        return Variant.From(data);
    }
    private static Texture2D Thumbnail(StairDesign design)
    {
        var owner = StairGeometry.Create("Thumbnail", design, Vec3.Zero);
        var paths = new System.Text.StringBuilder();
        Vector2 Project(Vec3 p) => new((float)(p.X - p.Y) * .6f, -(float)(p.X + p.Y) * .25f - (float)p.Z * .7f);
        var points = owner.Shape.Faces.SelectMany(f => f.Vertices).Select(Project).ToArray();
        float minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        float scale = Math.Min(96 / Math.Max(1, maxX - minX), 62 / Math.Max(1, maxY - minY));
        foreach (var f in GeometryThumbnailFaces.Visible(owner.Shape.Faces, new Vec3(-1, -1, 5.0 / 7)))
        {
            string verts = string.Join(' ', f.Vertices.Select(Project).Select(p => $"{(p.X - minX) * scale + 7:0.##},{(p.Y - minY) * scale + 5:0.##}"));
            string fill = f.StairRole == StairGeometry.Tread ? "#a5d7e4" : f.StairRole == StairGeometry.Riser ? "#55788c" : "#334959";
            // BSP splits on a single side are not edges of the staircase.
            paths.Append($"<polygon points='{verts}' fill='{fill}' stroke='{(f.StairRole == StairGeometry.Tread ? "#365362" : fill)}' stroke-width='.35'/>");
        }
        paths.Append("<rect x='86' y='2' width='22' height='20' rx='3' fill='#202832' stroke='#405568'/>");
        paths.Append(design.EdgeRadius > 0 ? "<path d='M89 18V12Q89 6 95 6H105' fill='none' stroke='#a5d7e4' stroke-width='3'/>" : "<path d='M89 18V6H105' fill='none' stroke='#a5d7e4' stroke-width='3'/>");
        var image = new Image(); image.LoadSvgFromString($"<svg xmlns='http://www.w3.org/2000/svg' width='110' height='72'>{paths}</svg>");
        return ImageTexture.CreateFromImage(image);
    }
}

public static class StairFields
{
    private static bool AdvancedOpen;
    public static void Build(Control parent, StairDesign s, Action<Action<StairDesign>> edit, Action<string> error, Func<StairDesign>? current = null)
    {
        Label? countLabel = null;
        bool Apply(Action<StairDesign> action) {
            try { edit(action); if (countLabel is not null && current?.Invoke() is { } updated) countLabel.Text = $"{updated.Count} steps · {updated.Rise / updated.Count:0.##} rise each"; return true; }
            catch (ArgumentException ex) { error(ex.Message); return false; }
        }
        var layout = new OptionButton(); foreach (string name in StairDesign.Layouts) layout.AddItem(name);
        layout.Selected = Array.IndexOf(StairDesign.Layouts, s.Layout); parent.AddChild(layout);
        int acceptedLayout = layout.Selected;
        layout.ItemSelected += i => { if (Apply(d => d.Layout = StairDesign.Layouts[(int)i])) acceptedLayout = (int)i; else layout.Select(acceptedLayout); };
        void Number(string label, double value, double min, double max, Action<StairDesign, double> set, double step = 1)
        {
            var row = PanelUi.Row(parent, label, label + " for the whole staircase", 120);
            var box = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, UpdateOnTextChanged = false };
            double accepted = value;
            row.AddChild(box); box.ValueChanged += v => { if (Apply(d => set(d, v))) accepted = v; else box.SetValueNoSignal(accepted); };
        }
        void Flag(string label, bool value, Action<StairDesign, bool> set)
        { var box = new CheckBox { Text = label, ButtonPressed = value }; parent.AddChild(box); bool accepted = value; box.Toggled += v => { if (Apply(d => set(d, v))) accepted = v; else box.SetPressedNoSignal(accepted); }; }
        Number("Width", s.Width, 32, 65536, (d, v) => { d.Width = v; d.LandingLength = Math.Max(d.LandingLength, v); });
        Number("Total rise", s.Rise, 1, 65536, (d, v) => d.Rise = v);
        Number("Total flight run", s.Run, 32, 65536, (d, v) => d.Run = v);
        Number("Target step height", s.TargetStepHeight, 1, 128, (d, v) => d.TargetStepHeight = v);
        PanelUi.Caption(parent, $"{s.Count} steps · {s.Rise / s.Count:0.##} rise each");
        Number("Tread thickness", s.TreadThickness, .25, 256, (d, v) => d.TreadThickness = v, .25);
        Number("Tread overhang", s.Overhang, 0, 128, (d, v) => d.Overhang = v, .25);
        Number("Edge radius", s.EdgeRadius, 0, 128, (d, v) => d.EdgeRadius = v, .25);
        Flag("Reverse turn", s.Left, (d, v) => d.Left = v);
        Flag("Smooth player movement", s.Smooth, (d, v) => d.Smooth = v);
        Flag("Carve clearance and opening", s.Carve, (d, v) => d.Carve = v);
        var advanced = new VBoxContainer { Visible = AdvancedOpen }; var toggle = new CheckBox { Text = "Advanced", TooltipText = "Exact step count, landing/radius dimensions and materials", ButtonPressed = AdvancedOpen };
        parent.AddChild(toggle); parent.AddChild(advanced); toggle.Toggled += v => { AdvancedOpen = v; advanced.Visible = v; };
        parent = advanced;
        Number("Exact count (0 = auto)", s.StepCount, 0, 256, (d, v) => d.StepCount = (int)v);
        Number("Landing length", s.LandingLength, 32, 65536, (d, v) => d.LandingLength = v);
        Number("Inner radius", s.InnerRadius, 16, 65536, (d, v) => d.InnerRadius = v);
        Number("Total turn degrees", s.TurnDegrees, 1, 1440, (d, v) => d.TurnDegrees = v);
        Number("Headroom", s.Headroom, 1, 65536, (d, v) => d.Headroom = v);
        Number("Clearance side margin", s.ClearanceMargin, 0, 65536, (d, v) => d.ClearanceMargin = v);
        Number("Clearance before stairs", s.ClearanceStart, 0, 65536, (d, v) => d.ClearanceStart = v);
        Number("Clearance after stairs", s.ClearanceEnd, 0, 65536, (d, v) => d.ClearanceEnd = v);
        foreach (string role in new[] { "Tread", "Riser", "Side" })
        {
            var property = typeof(StairDesign).GetProperty(role + "Material")!;
            var text = new LineEdit { Text = (string)property.GetValue(s)!, PlaceholderText = role + " material", TooltipText = role + " material (or drop a texture onto this part in the view)" };
            PanelUi.Caption(parent, role + " material"); parent.AddChild(text);
            string acceptedText = text.Text;
            void CommitText() { if (text.Text == acceptedText) return; if (Apply(d => property.SetValue(d, text.Text))) acceptedText = text.Text; else text.Text = acceptedText; }
            text.TextSubmitted += _ => CommitText(); text.FocusExited += CommitText;
            var scale = typeof(StairDesign).GetProperty(role + "TextureScale")!;
            Number(role + " texture scale", (double)scale.GetValue(s)!, .01, 100, (d, v) => scale.SetValue(d, v), .05);
            foreach (var (suffix, label, min, max) in new[] { ("TextureRotation", " angle", -360d, 360d), ("PanU", " offset U", -65536d, 65536d), ("PanV", " offset V", -65536d, 65536d) })
            {
                var field = typeof(StairDesign).GetProperty(role + suffix)!;
                Number(role + label, (double)field.GetValue(s)!, min, max, (d, v) => field.SetValue(d, v));
            }
        }
    }
}
