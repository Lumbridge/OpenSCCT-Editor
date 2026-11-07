using Godot;
using OpenScctEditor.Core;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Stairs;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

[ShellPart(Order = -20)]
public sealed class StairTools : IShellPart
{
    public static StairTool? Tool { get; private set; }
    public void Attach(MainWindow window, EditorContext context)
    {
        var manager = ToolManager.Instance!;
        Tool = new StairTool(manager); manager.AddTool(Tool);
        context.AddAction(new EditorAction("stairs.palette", "Stairs palette", "Tools", () => DockTabs.ShowPanel("Stairs")));
        context.AddAction(new EditorAction("stairs.convert", "Convert to Editable Stairs", "Brush", () =>
        {
            try { if (context.Selection.Primary is { } r) {
                var command = (StairCommands.StairReplacement)StairCommands.Convert(context.Project, r.Id);
                context.Execute(command); context.Selection.Set(command.Added);
            } }
            catch (ArgumentException ex) { context.SetStatus(ex.Message); }
        }));
        context.AddAction(new EditorAction("stairs.break", "Break Apart Staircase", "Brush", () =>
        {
            try { if (context.Selection.Primary is { } r) context.Execute(StairCommands.BreakApart(context.Project, r.Id)); }
            catch (ArgumentException ex) { context.SetStatus(ex.Message); }
        }));
    }
}

public sealed class StairTool(ToolManager tools) : IViewportTool
{
    public string Id => "stairs";
    public string Title => "Stairs";
    public string Shortcut => "";
    public string Description => "Place editable stairs or connect two floor surfaces";
    public StairDesign Settings { get; set; } = new();
    public bool Connect { get; set; }
    public int Yaw { get; set; }
    private Vec3? _bottom;
    private Brush? _preview;
    private string? _problem;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Brush, StairGeometry.Generated> PreviewGeometry = new();
    private EditorViewport? _view;
    private Vector2 _lastPointer;
    public bool Cancel() { bool pending = _bottom is not null || _preview is not null; _bottom = null; _preview = null; _view?.Redraw(); return pending; }
    public void Deactivate() => Cancel();
    public void BuildOptions(HBoxContainer bar)
    {
        bar.AddChild(new Label { Text = Connect ? "Pick bottom, then upper floor · R rotate · F reverse turn · Esc cancel" : "Click to place · R rotate · F reverse turn · Esc cancel" });
    }
    public bool Input(EditorViewport view, InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.R) { Yaw = Rot.Wrap(Yaw + Rot.UnitsPerTurn / 4); e = new InputEventMouseMotion { Position = _lastPointer }; }
            if (key.Keycode == Key.F) { Settings.Left = !Settings.Left; e = new InputEventMouseMotion { Position = _lastPointer }; }
        }
        Vector2? pointer = e is InputEventMouseMotion motion ? motion.Position : e is InputEventMouseButton button ? button.Position : null;
        if (pointer is null) return false;
        _view = view; _lastPointer = pointer.Value;
        Vec3 at = Point(view, pointer.Value, tools);
        try
        {
            _preview = Connect && _bottom is { } bottom ? StairFit.Between("StairPreview", Settings, bottom, at)
                : StairGeometry.Create("StairPreview", Settings, at, new Rot(0, Yaw, 0));
            _problem = StairPlacement.Problem(tools.Context.Project, _preview, tools.Context.Session.Folder);
        }
        catch (ArgumentException ex) { _preview = null; _problem = ex.Message; }
        tools.Context.SetStatus(_problem ?? (Connect && _bottom is null ? "Pick the bottom floor connection" : Connect ? "Pick the upper floor connection" : "Click to place staircase"));
        view.Redraw();
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            if (Connect && _bottom is null) { _bottom = at; return true; }
            if (_preview is not null && _problem is null)
            {
                var cmd = StairCommands.Place(tools.Context.Project, _preview.Stair!, _preview.Location, _preview.Rotation);
                tools.Context.Execute(cmd); tools.Context.Selection.Set(cmd.Added); _bottom = null;
            }
            return true;
        }
        return e is InputEventMouseMotion;
    }
    public void Draw(EditorViewport view, Control canvas)
    {
        if (_view == view && _preview is not null) DrawPreview(view, canvas, _preview, _problem is not null);
    }
    public static Vec3 Point(EditorViewport view, Vector2 mouse, ToolManager tools)
    {
        Vec3 p = Picking.Surface(view.Cam, tools.Context.Project, mouse)?.Point ?? (view.Cam.IsOrtho
            ? view.Cam.PlanePoint(mouse, 0) : view.Cam.Ray(mouse).Origin + view.Cam.Ray(mouse).Direction * 512);
        double grid = tools.GridSnap ? tools.GridSize : 1;
        return new Vec3(Math.Round(p.X / grid) * grid, Math.Round(p.Y / grid) * grid, p.Z);
    }
    public static void DrawPreview(EditorViewport view, Control canvas, Brush b, bool invalid = false)
    {
        void Shape(Brush shape, Color color)
        {
            foreach (var f in shape.WorldFaces()) for (int k = 0; k < f.Vertices.Count; k++)
                if (view.Cam.ToScreen(f.Vertices[k]) is { } a && view.Cam.ToScreen(f.Vertices[(k + 1) % f.Vertices.Count]) is { } c) canvas.DrawLine(a, c, color, 1, true);
        }
        Shape(b, invalid ? Colors.OrangeRed : Colors.LightSkyBlue);
        var generated = PreviewGeometry.GetValue(b, StairGeometry.Generate);
        foreach (Brush cut in generated.Cuts) Shape(cut, new Color(.4f, .9f, .5f, .2f));
        foreach (var ramp in generated.Smoothing)
            Shape(new Brush(ramp.Id) { Shape = ramp.Shape!, Location = ramp.Location, Rotation = ramp.Rotation, MainScale = ramp.DrawScale3D }, new Color(1, .75f, .2f, .3f));
        if (view.Cam.ToScreen(b.Location) is { } from && view.Cam.ToScreen(generated.Top) is { } to)
        { canvas.DrawLine(from, to, Colors.Yellow, 2, true); canvas.DrawCircle(from, 5, Colors.LimeGreen); canvas.DrawCircle(to, 5, Colors.Yellow); }
    }
}
