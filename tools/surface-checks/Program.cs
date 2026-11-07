using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Windows;
using OpenScctEditor.Tools;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
Brush Box(string id, Vec3 min, Vec3 max, CsgOper op = CsgOper.Subtract) => new(id)
{
    Oper = op,
    Shape = new BrushShape { Faces = ConvexSolid.Box(min, max).Select(v => FaceKit.Make(Starter.DefaultMaterial, v)).ToList() },
};
var p = MapProject.CreateDefault("SurfaceTest", "Doorway surfaces");
p.Brushes = [Box("Room", new(-512, -512, 0), new(512, 512, 384)),
    Box("NextRoom", new(576, -512, 0), new(1600, 512, 384)),
    Box("Door", new(480, -96, 0), new(608, 96, 224))];
var preview = new WorldPreview();
IReadOnlyList<PreviewPolygon> surfaces = preview.Build(p);
Vec3 eye = new(320, 0, 128);
FaceHit Pick(Vec3 target, Vec3? from = null) =>
    FacePicking.PickSurfaces(p, surfaces, from ?? eye, (target - (from ?? eye)).Normalized())
    ?? throw new Exception("No surface at " + target);

FaceHit left = Pick(new(544, -96, 128));
FaceHit right = Pick(new(544, 96, 128));
FaceHit top = Pick(new(544, 0, 224), new(400, 0, 160));
Check(left.Face.Brush == "Door" && (left.Point - new Vec3(544, -96, 128)).Length < 1e-6, "Left jamb is picked through the wall opening");
Check(right.Face.Brush == "Door" && (right.Point - new Vec3(544, 96, 128)).Length < 1e-6, "Right jamb is picked through the wall opening");
Check(top.Face.Brush == "Door" && (top.Point - new Vec3(544, 0, 224)).Length < 1e-6, "Doorway soffit is picked through the wall opening");
Check(new[] { left.Face, right.Face, top.Face }.Distinct().Count() == 3, "Jambs and soffit have separate editable face references");
Check((left.Normal - new Vec3(0, -1, 0)).Length < 1e-6 && (top.Normal - Vec3.UnitZ).Length < 1e-6, "Extrusion retains the source brush's outward normals");

FaceHit through = Pick(new(1600, 0, 128));
Check(through.Face.Brush == "NextRoom" && Math.Abs(through.Point.X - 1600) < 1e-6, "Clicking through the opening reaches the next room, not an invisible wall face");
FaceHit wall = Pick(new(512, 200, 128));
Check(wall.Face.Brush == "Room", "Wall beside the opening remains selectable");
var wallPolys = surfaces.Where(s => s.BrushId == wall.Face.Brush && s.FaceIndex == wall.Face.Face).ToList();
Check(wallPolys.Count > 1 && wallPolys.All(poly => !Picking.RayPolygon(eye, Vec3.UnitX, poly.Vertices, poly.Normal, out _)), "Wall highlight polygons leave the doorway open");
Check(Pick(new(544, -96, 128), new(800, 0, 128)).Face == left.Face, "The same jamb can be selected from the adjoining room");

var session = new EditorSession(p, null);
session.Execute(FaceEdit.Apply(p, [left.Face], "Paint doorway jamb", FaceEdit.SetMaterial("Test.Jamb")));
Check(FaceEdit.Find(p, left.Face)?.Material == "Test.Jamb", "Painting changes the selected jamb");
Check(FaceEdit.Find(p, right.Face)?.Material == Starter.DefaultMaterial && FaceEdit.Find(p, top.Face)?.Material == Starter.DefaultMaterial
    && FaceEdit.Find(p, wall.Face)?.Material == Starter.DefaultMaterial, "Painting a jamb leaves the other jamb, soffit and room wall alone");
session.Commands.Undo();
Check(FaceEdit.Find(p, left.Face)?.Material == Starter.DefaultMaterial, "Undo restores only the edited jamb");
session.Commands.Redo();
Check(FaceEdit.Find(p, left.Face)?.Material == "Test.Jamb", "Redo restores the jamb material");
session.Commands.Undo();

Brush door = p.FindBrush("Door")!;
door.Editor.Locked = true;
Check(FacePicking.PickSurfaces(p, surfaces, eye, (new Vec3(544, -96, 128) - eye).Normalized(), b => b.Id == "Door") is null,
    "Locked doorway surfaces cannot be picked");
door.Editor.Locked = false;
door.Editor.Hidden = true;
Check(FacePicking.PickSurfaces(p, surfaces, eye, (new Vec3(544, -96, 128) - eye).Normalized(), b => b.Id == "Door") is null,
    "Hidden doorway surfaces cannot be picked");
door.Editor.Hidden = false;

// Ordered CSG edits must update both picks and the polygons used to highlight a selected wall.
p.Brushes.Remove(door);
surfaces = preview.Build(p);
Check(Pick(new(544, 0, 128)).Face.Brush == "Room", "Removing the cut restores the room wall as the nearest surface");
p.Brushes.Add(door);
surfaces = preview.Build(p);
Check(Pick(new(544, -96, 128)).Face == left.Face, "Restoring the cut restores independent doorway picking");

// Transform the complete layout: source face identity must survive non-axis-aligned walls.
var rotation = new Rot(0, 8192, 0);
Vec3 offset = new(100, -200, 32);
foreach (Brush b in p.Brushes) { b.Rotation = rotation; b.Location = offset; }
surfaces = preview.Build(p);
Check(FacePicking.PickSurfaces(p, surfaces, offset + rotation.Rotate(eye), rotation.Rotate((new Vec3(544, -96, 128) - eye).Normalized()))?.Face == left.Face,
    "Rotated and translated doorway jambs retain source face identity");

if (args.Length > 0)
{
    foreach (Brush b in p.Brushes) { b.Rotation = Rot.Zero; b.Location = Vec3.Zero; }
    p.Workspace.CameraLocation = new Vec3(300, -280, 160);
    Vec3 look = (new Vec3(544, 0, 128) - p.Workspace.CameraLocation).Normalized();
    p.Workspace.CameraRotation = new Rot((int)(Math.Asin(look.Z) * 32768 / Math.PI), (int)(Math.Atan2(look.Y, look.X) * 32768 / Math.PI), 0);
    ProjectStore.Save(p, Path.GetFullPath(args[0]));
}
Console.WriteLine($"PASS {checks} surface selection checks");

// Selection must hit the shaped window inside the carved wall, from either side, before assets are loaded.
var windows = MapProject.CreateDefault("WindowPick", "Window selection regression");
windows.Brushes = [Box("Room", new(-512, -768, 0), new(512, 768, 768)),
    Box("NextRoom", new(576, -768, 0), new(1600, 768, 768))];
windows.Actors.Clear();
string[] shapes = ["rectangle", "circle", "oval", "arch", "triangle", "polygon"];
foreach ((string shape, int index) in shapes.Select((shape, index) => (shape, index)))
{
    var window = new Actor("Window" + index, "SBase.SVitre") { Location = new(544, -500 + index * 200, 300),
        Window = new WindowDesign { Shape = shape, Width = 140, Height = shape == "circle" ? 140 : 160,
            Framed = true, CutDepth = 128, FrameDepth = 68, Wall = "Room" } };
    windows.Actors.Add(window);
    foreach (int side in new[] { -1, 1 })
    {
        Vec3 from = window.Location + Vec3.UnitX * side * 200, direction = Vec3.UnitX * -side;
        Check(Picking.RayWindow(from, direction, window, 0, out double t) && Math.Abs(t - 199) < 1e-6,
            shape + " glass is selectable from both sides without a loaded mesh");
    }
    Check(!Picking.RayWindow(window.Location + new Vec3(-200, 90, 0), Vec3.UnitX, window, 0, out _),
        shape + " opening cut does not become a pick target");
    Vec3 vertex = new(0, WindowGeometry.Outline(window.Window)[0].Y, WindowGeometry.Outline(window.Window)[0].Z);
    Vec3 edge = vertex * 1.025;
    Check(Picking.RayWindow(window.Location + edge - Vec3.UnitX * 200, Vec3.UnitX, window, 0, out _),
        shape + " solid frame is selectable");
}
var cutProject = ProjectSnapshot.Take(windows);
foreach (Actor window in windows.Actors) cutProject.Brushes.AddRange(WindowGeometry.Parts(window));
var cutSurfaces = preview.Build(cutProject);
foreach (Actor window in windows.Actors)
{
    Vec3 from = window.Location - Vec3.UnitX * 300;
    Check(FacePicking.PickSurfaces(windows, cutSurfaces, from, Vec3.UnitX) is { Distance: > 300 },
        window.Window!.Shape + " carved wall does not intercept the glass click");
}
Check(FacePicking.PickSurfaces(windows, cutSurfaces, new(244, -600, 300), Vec3.UnitX)?.Face.Brush == "Room",
    "Wall beside window stays selectable");
var circle = windows.Actors[1];
Vec3 corner = circle.Location + new Vec3(-300, 60, 60);
Check(!Picking.RayWindow(corner, Vec3.UnitX, circle, 0, out _)
    && FacePicking.PickSurfaces(windows, cutSurfaces, corner, Vec3.UnitX)?.Face.Brush == "Room",
    "Circle corners select the visible wall, not its rectangular bounds");
var transformed = circle.Clone();
transformed.Rotation = new(1700, 7800, 2300); transformed.DrawScale3D = new(1.4, .8, 1.7);
transformed.PrePivot = new(50, -20, 12);
Vec3 center = transformed.LocalToWorld(Vec3.Zero), forward = transformed.Rotation.Rotate(Vec3.UnitX);
Check(Picking.RayWindow(center - forward * 300, forward, transformed, 0, out _),
    "Rotated scaled window with an offset pivot is selectable");
cutProject.Brushes.Add(Box("Occluder", new(350, -768, 0), new(380, 768, 768), CsgOper.Add));
windows.Brushes.Add(cutProject.Brushes[^1]);
Check(FacePicking.PickSurfaces(windows, preview.Build(cutProject), new(244, -500, 300), Vec3.UnitX)?.Face.Brush == "Occluder",
    "A real foreground wall still wins over glass behind it");
windows.Brushes.RemoveAt(windows.Brushes.Count - 1);
windows.Workspace.CameraLocation = new(0, 0, 300); windows.Workspace.CameraRotation = Rot.Zero;
if (args.Length > 1) ProjectStore.Save(windows, Path.GetFullPath(args[1]));
Console.WriteLine($"PASS {checks} surface and window selection checks");
