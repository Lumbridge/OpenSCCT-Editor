using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Windows;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;
using Orientation = OpenScct.Editor.Core.Geometry.Edit.Orientation;
using OpenScct.Editor.Core.Scripting;
using ObjectFilter = OpenScctEditor.Viewports.ObjectFilter;

namespace OpenScctEditor.Tools;

[ShellPart(Order = -19)]
public sealed class WindowTools : IShellPart
{
    public static WindowTool Tool { get; private set; } = null!;
    public void Attach(MainWindow window, EditorContext context)
    {
        Tool = new WindowTool(ToolManager.Instance!); ToolManager.Instance!.AddTool(Tool);
        context.AddAction(new EditorAction("windows.palette", "Windows Palette", "Tools", () => DockTabs.ShowPanel("Windows")));
        context.AddAction(new EditorAction("windows.add", "Add Window", "", () => Tool.AddAtContext()));
    }
    public static List<WindowOpening> Find(EditorViewport view, Vector2 at)
    {
        var ctx = EditorContext.Instance; var ray = view.Cam.Ray(at);
        return WindowFit.Find(ctx.Project, AssetService.Instance.Resolver, ray.Origin, ray.Direction, m => ObjectFilter.IsPickable(ctx.Project, m));
    }
}

public sealed class WindowTool : IViewportTool
{
    private readonly ToolManager tools;
    private EditorViewport? view;
    private Actor? preview;
    private string? problem;
    private Vector2 pointer;
    private List<WindowOpening>? fitting;
    private int fitIndex;
    public List<WindowOpening> ContextOpenings { get; set; } = [];
    public WindowDesign Settings { get; set; } = new() { Framed = true };
    public int Yaw { get; set; }
    public string Id => "windows";
    public string Title => "Windows";
    public string Shortcut => "";
    public string Description => "Place scalable windows or fit breakable glass into a frame";
    public WindowTool(ToolManager manager) { tools = manager; manager.Context.SessionReplaced += () => { fitting = null; preview = null; ContextOpenings = []; }; }
    public void Deactivate() { fitting = null; preview = null; }
    public void AddAtContext()
    {
        var choices = ContextOpenings; ContextOpenings = [];
        if (choices.Count == 0) { tools.Context.SetStatus("Point inside a closed window frame, then choose Add Window"); return; }
        if (choices.Count == 1) { Commit(WindowCommands.Fit(tools.Context.Project, choices[0])); return; }
        tools.Activate(Id); fitting = choices; fitIndex = 0; preview = WindowCommands.Fit(tools.Context.Project, fitting[0]);
        tools.RefreshOptions();
        tools.Context.SetStatus("Choose the highlighted opening, adjust clearance/depth, then click to add glass; Esc cancels");
    }
    private void Commit(Actor actor)
    {
        var p = tools.Context.Project; actor.Id = new FreshIds(p).Next("Window"); WindowAssets.Prepare(actor);
        tools.Context.Execute(new AddObjectsCommand("Add Window", actors: [actor])); tools.Context.Selection.Set([ObjectRef.Actor(actor.Id)]);
        tools.Context.SetStatus("Added breakable window. Resize with the Scale tool or edit dimensions in Inspector.");
    }
    public void BuildOptions(HBoxContainer bar)
    {
        if (fitting is { Count: > 0 })
        {
            var choices = new OptionButton(); for (int i = 0; i < fitting.Count; i++) choices.AddItem($"Opening {i + 1} · {string.Join(", ", fitting[i].Sources.Take(2))}");
            choices.Select(fitIndex); choices.ItemSelected += i => { fitIndex = (int)i; preview = WindowCommands.Fit(tools.Context.Project, fitting[fitIndex]); view?.Redraw(); }; bar.AddChild(choices);
            void Number(string label, double value, Action<double> set) { bar.AddChild(new Label { Text = label }); var field = new SpinBox { MinValue = label == "Clearance" ? 0 : -1024, MaxValue = 1024, Step = .25, Value = value }; field.ValueChanged += v => { set(v); view?.Redraw(); }; bar.AddChild(field); }
            Number("Clearance", preview?.Window?.Clearance ?? .5, v => { if (preview?.Window is { } d) d.Clearance = v; });
            Number("Depth", preview?.Window?.DepthOffset ?? 0, v => { if (preview?.Window is { } d) d.DepthOffset = v; });
            var add = new Button { Text = "Add Window" }; add.Pressed += () => { if (preview is not null) { try { WindowAssets.Prepare(preview); Commit(preview); fitting = null; preview = null; tools.Activate("move"); } catch (ArgumentException ex) { tools.Context.SetStatus(ex.Message); } } }; bar.AddChild(add);
        }
        else bar.AddChild(new Label { Text = "Click to place · R rotates · Esc cancels · Dimensions in Windows palette" });
    }
    public bool Input(EditorViewport target, InputEvent e)
    {
        view = target;
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R }) { Yaw = Rot.Wrap(Yaw + 8192); if (fitting is null) e = new InputEventMouseMotion { Position = pointer }; }
        if (e is not InputEventMouseMotion && e is not InputEventMouseButton) return false;
        pointer = e is InputEventMouseMotion motion ? motion.Position : ((InputEventMouseButton)e).Position;
        if (fitting is null) (preview, problem) = Preview(target, pointer, Settings, Yaw);
        else problem = null;
        target.Redraw();
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            if (preview is not null && problem is null)
            {
                try { Commit(preview.Clone()); preview = null; if (fitting is not null) { fitting = null; tools.Activate("move"); } }
                catch (ArgumentException ex) { tools.Context.SetStatus(ex.Message); }
            }
            else tools.Context.SetStatus(problem ?? "Point at a placement surface");
            return true;
        }
        if (problem is not null) tools.Context.SetStatus(problem);
        return true;
    }
    public bool Cancel() { bool had = preview is not null || fitting is not null; fitting = null; preview = null; view?.Redraw(); return had; }
    public void Draw(EditorViewport target, Control canvas) { if (preview is not null && (view is null || view == target)) DrawPreview(target, canvas, preview, problem is not null); }
    public static (Actor? Actor, string? Problem) Preview(EditorViewport view, Vector2 at, WindowDesign settings, int yaw)
    {
        var ctx = EditorContext.Instance; var d = settings.Clone(); d.Binding = null; d.CutDepth = 0; d.Wall = null;
        var hit = Picking.Surface(view.Cam, ctx.Project, at);
        Vec3 point = hit?.Point ?? (view.Cam.IsOrtho ? view.Cam.PlanePoint(at, 0) : view.Cam.Ray(at).Origin + view.Cam.Ray(at).Direction * 512);
        Rot rotation = new(0, yaw, 0); string? problem = null;
        if (hit is { } wall && Math.Abs(wall.Normal.Z) < .9)
        {
            rotation = Orientation.FromAxes(wall.Normal, Vec3.UnitZ);
            rotation = RotatorMath.Compose(new Rot(0, 0, yaw), rotation);
            if (d.Framed)
            {
                if (wall.Object is null) problem = "This retained baked wall cannot be cut. Place glass inside an existing opening.";
                else if (ctx.Project.FindBrush(wall.Object.Value.Id) is { } source)
                {
                    d.Wall = source.Id;
                    if (!WindowGeometry.CanCarve(ctx.Project, d)) problem = "This retained baked wall cannot be cut. Place glass inside an existing opening.";
                    else if (!ObjectFilter.IsPickable(ctx.Project, source.Editor)) problem = "Unlock this wall before cutting a window.";
                    double depth;
                    if (source.Oper == CsgOper.Add)
                    { var vertices = source.WorldFaces().SelectMany(f => f.Vertices).ToArray(); depth = vertices.Max(v => Vec3.Dot(v, wall.Normal)) - vertices.Min(v => Vec3.Dot(v, wall.Normal)); }
                    else depth = WallPieces.WallDepth(new OpenScct.Editor.Core.Scripting.WorldGeometry(ctx.Project, ctx.Session.Folder), new WallAnchor(default, point, -wall.Normal, null), 0);
                    d.CutDepth = Math.Max(2, depth) + 2;
                    d.FrameDepth = Math.Max(d.FrameDepth, depth + 4);
                    d.Wall = source.Id; point -= wall.Normal * depth / 2;
                }
            }
        }
        if (ToolManager.Instance is { GridSnap: true } tools)
        {
            Vec3 snapped = tools.Snap(point);
            if (hit is { } surface) snapped -= surface.Normal * Vec3.Dot(snapped - point, surface.Normal);
            point = snapped;
        }
        try { return (WindowCommands.Create(ctx.Project, d, point, rotation), problem); }
        catch (ArgumentException ex) { return (null, ex.Message); }
    }
    public static void DrawPreview(EditorViewport view, Control canvas, Actor actor, bool invalid = false)
    {
        var color = invalid ? Colors.OrangeRed : Colors.LightSkyBlue;
        try
        {
            var d = actor.Window!; var outline = WindowGeometry.Outline(d);
            void Loop(IReadOnlyList<WindowPoint> points, double depth, Color tint)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i]; var q = points[(i + 1) % points.Count];
                    if (view.Cam.ToScreen(actor.LocalToWorld(new Vec3(depth,p.Y,p.Z))) is { } a
                        && view.Cam.ToScreen(actor.LocalToWorld(new Vec3(depth,q.Y,q.Z))) is { } b) canvas.DrawLine(a,b,tint,1.5f,true);
                }
            }
            Loop(WindowGeometry.Offset(outline,-d.Clearance), d.DepthOffset, color);
            if (d.Framed) { var outer = WindowGeometry.Offset(outline,d.FrameWidth); Loop(outer,-d.FrameDepth / 2,color); Loop(outer,d.FrameDepth / 2,color); }
            if (d.CutDepth > 0) { var cut = invalid ? Colors.OrangeRed : new Color(.3f,.9f,.5f,.35f); Loop(outline,-d.CutDepth / 2,cut); Loop(outline,d.CutDepth / 2,cut); }
        }
        catch (ArgumentException) { }
    }
}
