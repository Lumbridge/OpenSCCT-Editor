using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Scripting;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;
using Orientation = OpenScct.Editor.Core.Geometry.Edit.Orientation;
using ObjectFilter = OpenScctEditor.Viewports.ObjectFilter;

namespace OpenScctEditor.Tools;

[ShellPart(Order = -18)]
public sealed class DoorTools : IShellPart
{
    public static DoorTool Tool { get; private set; } = null!;
    public void Attach(MainWindow window, EditorContext context)
    {
        Tool = new DoorTool(ToolManager.Instance!); ToolManager.Instance!.AddTool(Tool);
        context.AddAction(new EditorAction("doors.palette", "Doors Palette", "Tools", () => DockTabs.ShowPanel("Doors")));
    }
}

public sealed class DoorTool : IViewportTool
{
    private readonly ToolManager tools;
    private EditorViewport? view;
    private Actor? preview;
    private string? problem;
    private Vector2 pointer;
    public DoorDesign Settings { get; set; } = new();
    public int Yaw { get; set; }
    public string Id => "doors";
    public string Title => "Doors";
    public string Shortcut => "";
    public string Description => "Place a framed mover door, then edit its size, style and opening behaviour";
    public DoorTool(ToolManager manager) { tools = manager; tools.Context.SessionReplaced += () => preview = null; }
    public void Deactivate() { preview = null; view?.Redraw(); }
    public void BuildOptions(HBoxContainer bar)
    {
        bar.AddChild(new Label { Text = "Click at the bottom of the doorway · R rotates · Esc cancels" });
        var settings = new Button { Text = "Door settings" }; settings.Pressed += () => DockTabs.ShowPanel("Doors"); bar.AddChild(settings);
    }
    public bool Input(EditorViewport target, InputEvent e)
    {
        view = target;
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R }) { Yaw = Rot.Wrap(Yaw + 16384); e = new InputEventMouseMotion { Position = pointer }; }
        if (e is not InputEventMouseMotion && e is not InputEventMouseButton) return false;
        pointer = e is InputEventMouseMotion motion ? motion.Position : ((InputEventMouseButton)e).Position;
        (preview, problem) = Preview(target, pointer, Settings, Yaw); target.Redraw();
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            if (preview is not null && problem is null)
            {
                var actor = preview.Clone(); actor.Id = new FreshIds(tools.Context.Project).Next("Door"); actor.Props.Set("Tag", OpenScct.Content.Json.SvJsonNode.String(actor.Id));
                tools.Context.Execute(new AddObjectsCommand("Add Door", actors: [actor])); tools.Context.Selection.Set([ObjectRef.Actor(actor.Id)]);
                tools.Context.SetStatus("Added door. Edit size and opening in Inspector; Scale shows resize handles.");
            }
            else tools.Context.SetStatus(problem ?? "Point at a placement surface");
            return true;
        }
        if (problem is not null) tools.Context.SetStatus(problem);
        return true;
    }
    public bool Cancel() { bool had = preview is not null; preview = null; view?.Redraw(); return had; }
    public void Draw(EditorViewport target, Control canvas) { if (preview is not null && view == target) DrawPreview(target, canvas, preview, problem is not null); }
    public static (Actor? Actor, string? Problem) Preview(EditorViewport view, Vector2 at, DoorDesign settings, int yaw)
    {
        var ctx = EditorContext.Instance; var d = settings.Clone(); d.CutDepth = 0; d.Wall = null;
        var hit = Picking.Surface(view.Cam, ctx.Project, at);
        Vec3 point = hit?.Point ?? (view.Cam.IsOrtho ? view.Cam.PlanePoint(at, 0) : view.Cam.Ray(at).Origin + view.Cam.Ray(at).Direction * 512);
        Rot rotation = new(0, yaw, 0); string? problem = null;
        if (hit is { } wall && Math.Abs(wall.Normal.Z) < .1)
        {
            rotation = Orientation.FromAxes(wall.Normal, Vec3.UnitZ);
            // Wall placement stays aligned to the wall; R swaps its facing and opening side.
            rotation = new(0, Rot.Wrap(rotation.Yaw + (yaw / 16384 % 2) * 32768), 0);
            if (wall.Object is null) problem = "Retained baked walls cannot be cut. Place this door in an existing opening.";
            else if (ctx.Project.FindBrush(wall.Object.Value.Id) is { } source)
            {
                d.Wall = source.Id;
                if (!DoorGeometry.CanCarve(ctx.Project, d)) problem = "Retained baked walls cannot be cut. Place this door in an existing opening.";
                else if (!ObjectFilter.IsPickable(ctx.Project, source.Editor)) problem = "Unlock this wall before cutting a doorway.";
                double depth;
                if (source.Oper == CsgOper.Add)
                { var vertices = source.WorldFaces().SelectMany(f => f.Vertices).ToArray(); depth = vertices.Max(v => Vec3.Dot(v, wall.Normal)) - vertices.Min(v => Vec3.Dot(v, wall.Normal)); }
                else depth = WallPieces.WallDepth(new WorldGeometry(ctx.Project, ctx.Session.Folder), new WallAnchor(default, point, -wall.Normal, null), 0);
                if (depth <= 0 || depth > 2048) problem = "This wall has no nearby back face. Carve a doorway first, then place the door in the opening.";
                else
                {
                    d.CutDepth = depth + 2;
                    d.Thickness = Math.Max(1, depth);
                    d.FrameDepth = depth + 4;
                    point -= wall.Normal * depth / 2;
                }
            }
        }
        if (ToolManager.Instance is { GridSnap: true } tools)
        { var snapped = tools.Snap(point); if (hit is { } surface) snapped -= surface.Normal * Vec3.Dot(snapped - point, surface.Normal); point = snapped; }
        try { return (DoorCommands.Create(ctx.Project, d, point + Vec3.UnitZ * d.Height / 2, rotation), problem); }
        catch (ArgumentException ex) { return (null, ex.Message); }
    }
    public static void DrawPreview(EditorViewport view, Control canvas, Actor actor, bool invalid = false)
    {
        Color colour = invalid ? Colors.OrangeRed : Colors.LightSkyBlue;
        void Shape(BrushShape shape, Func<Vec3, Vec3> world, Color c)
        {
            foreach (var face in shape.Faces) for (int i = 0; i < face.Vertices.Count; i++)
                if (view.Cam.ToScreen(world(face.Vertices[i])) is { } a && view.Cam.ToScreen(world(face.Vertices[(i + 1) % face.Vertices.Count])) is { } b) canvas.DrawLine(a, b, c, 1.2f, true);
        }
        foreach (var part in DoorGeometry.Parts(actor)) Shape(part.Shape, part.LocalToWorld, part.Oper == CsgOper.Subtract ? new Color(.3f, .9f, .5f, .4f) : colour);
        foreach (var part in DoorGeometry.Leaf(actor.Door!)) Shape(part.Shape, actor.LocalToWorld, colour);
        var m = MoverSetup.Read(actor); var open = actor.Clone(); var pose = m.KeyPose(1); open.Location = pose.Location; open.Rotation = pose.Rotation;
        Shape(Box.Shape(new(actor.Door!.Thickness, actor.Door.Width, actor.Door.Height)), open.LocalToWorld, new Color(colour, .35f));
    }
}
