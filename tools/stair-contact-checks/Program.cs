using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Stairs;

EditorServices.Discover(typeof(MapProject).Assembly);
var project = MapProject.CreateDefault("FloorContact", "Floor contact");
Starter.AddStarterRoom(project);
foreach (var face in project.Brushes[0].Shape.Faces)
{
    face.Material = "Floor.Original";
    face.PanU = 83;
}
var stair = StairGeometry.Create("ContactStair", new StairDesign { ClearanceStart = 32, SideMaterial = "Stair.Side" }, Vec3.Zero);
project.Brushes.Add(stair);
var world = WorldModel.Build(StairGeometry.Expand(project.Brushes).ToList(), [], true, null);
var approach = world.Visible.Where(p => p.Plane.N.Z > .9 && p.Verts.All(v => Math.Abs(v.Z) < .01)
    && p.Verts.Any(v => v.X < -1) && p.Verts.Any(v => v.X > -31)).ToList();
foreach (var polygon in approach)
    Console.WriteLine($"Floor surface: {polygon.Surface.BrushId}, {polygon.Surface.Material}, pan {polygon.Surface.PanU}");
if (approach.Any(p => p.Surface.BrushId.StartsWith("ContactStair", StringComparison.Ordinal)))
    throw new Exception("Stair clearance replaced the approach floor's material or texture alignment");
if (approach.Count == 0) throw new Exception("The approach floor disappeared");
Console.WriteLine("PASS: stair clearance preserves the approach floor surface");
project.Brushes.Remove(stair);
project.Brushes[0].Shape = Box.Shape(new(2048, 3072, 1024));
project.Brushes[0].Location = new(0, 0, 512);
project.Workspace.CameraLocation = new(-700, -850, 500);
project.Workspace.CameraRotation = Orientation.FromAxes(new Vec3(0, 500, 100) - project.Workspace.CameraLocation, Vec3.UnitZ);
OpenScct.Editor.Core.Project.ProjectStore.Save(project, Path.GetFullPath("local/stair-contact-checks/Ghost.osmap"));
