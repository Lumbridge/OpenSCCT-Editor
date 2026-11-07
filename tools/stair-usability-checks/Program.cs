using System.Diagnostics;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Stairs;
using OpenScct.Editor.Core.Windows;
using OpenScct.Editor.Core.Identity;
using OpenScctEditor.Tools;

EditorServices.Discover(typeof(MapProject).Assembly); EditorServices.Classes = ClassCatalog.GeneratedOnly();
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
bool Inside(Brush brush, Vec3 point) => brush.Shape.Faces.Count > 0 && brush.Shape.Faces.All(f => Vec3.Dot(f.Normal(), point - f.Vertices[0]) <= 1e-5);
string root = Path.GetFullPath(args.FirstOrDefault() ?? "local/stair-usability"); Directory.CreateDirectory(root);
foreach (string layout in StairDesign.Layouts)
{
    var p = new MapProject();
    var start = StairGeometry.Create("Scale", new StairDesign { Layout = layout, TurnDegrees = layout == "spiral" ? 360 : 90 }, Vec3.Zero); p.Brushes.Add(start);
    var snapshot = new TransformSnapshot(p, [ObjectRef.Stair("Scale")]);
    foreach (double factor in new[] { 1.05, 1.1, 1.15 })
    {
        var predicted = snapshot.ScaledShape(Vec3.Zero, 2, factor);
        snapshot.Scale(p, Vec3.Zero, 2, factor, "test").Do(p);
        Check(Math.Abs(p.FindBrush("Scale")!.Stair!.Rise - 256 * factor) < 1e-6, "Scale events are absolute for " + layout);
        var actual = OpenScct.Editor.Core.Scripting.MapObjects.BrushBounds(p.FindBrush("Scale")!);
        Check((actual.Min-predicted.Min).Length < .01 && (actual.Max-predicted.Max).Length < .01, $"Snap bounds match rebuilt {layout}: actual {actual}, predicted {predicted.Min}/{predicted.Max}");
    }
}
{
    var p = new MapProject(); var a = WindowCommands.Create(p, new WindowDesign { Framed = true }, Vec3.Zero); p.Actors.Add(a);
    var snapshot = new TransformSnapshot(p, [ObjectRef.Actor(a.Id)]);
    foreach (double factor in new[] { 1.1, 1.2, 1.3 })
    {
        var predicted = snapshot.ScaledShape(Vec3.Zero, 1, factor);
        snapshot.Scale(p, Vec3.Zero, 1, factor, "test").Do(p);
        Check(Math.Abs(p.FindActor(a.Id)!.Window!.Width - 160 * factor) < 1e-6, "Window scale events are absolute");
        var actual = OpenScct.Editor.Core.Scripting.MapObjects.ActorBounds(p.FindActor(a.Id)!);
        Check((actual.Min-predicted.Min).Length < 1e-5 && (actual.Max-predicted.Max).Length < 1e-5, "Snap bounds include the window's fixed frame profile");
    }
}
foreach (bool rounded in new[] { false, true })
{
    var design = new StairDesign { Layout = "spiral", TurnDegrees = 360, EdgeRadius = rounded ? 4 : 0, Overhang = rounded ? 4 : 0 };
    var owner = StairGeometry.Create("Spiral", design, Vec3.Zero); var generated = StairGeometry.Generate(owner);
    var steps = StairGeometry.Steps(design);
    for (int i = 1; i < steps.Count; i++)
    {
        var step = steps[i]; Vec3 at = (step.A + step.B) / 2 + new Vec3(0, 0, step.Bottom + 2);
        Vec3 ahead = (step.C + step.D - step.A - step.B).Normalized() * .1;
        Check(generated.Solids.Any(s => Inside(s, at + ahead)), "Spiral has a closed riser below every tread");
    }
    Check(owner.Shape.Faces.Any(f => f.StairRole == StairGeometry.Riser), "Spiral risers retain their material role");
}
foreach (string layout in StairDesign.Layouts)
{
    var d = new StairDesign { Layout = layout, TurnDegrees = layout == "spiral" ? 360 : 90, ClearanceMargin = 8, ClearanceStart = 32, ClearanceEnd = 48 };
    var owner = StairGeometry.Create("Stair", d, new(17, 32, 0)); var generated = StairGeometry.Generate(owner);
    Check(generated.Cuts.Any(c => c.Shape.Faces.Any(f => f.Normal().Z > .1 && f.Normal().Z < .9999)), "Clearance ceiling slopes for " + layout);
    var p = MapProject.CreateDefault("StairFit", "Stair fit"); Starter.AddStarterRoom(p);
    p.Brushes[0].Shape = Box.Shape(new(2048, 3072, 300)); p.Brushes[0].Location = new(0, 0, 150);
    p.Brushes.Add(owner);
    var folder = Path.Combine(root, layout + ".osmap"); ProjectStore.Save(p, folder);
    var loaded = ProjectStore.Load(folder);
    Check(loaded.FindBrush("Stair")!.Stair!.ClearanceEnd == 48, "Clearance controls save/reopen");
    if (layout == "straight")
    {
        p.Workspace.CameraLocation = new(-700, -850, 250); p.Workspace.CameraRotation = Orientation.FromAxes(new Vec3(240, 0, 220) - p.Workspace.CameraLocation, Vec3.UnitZ);
        ProjectStore.Save(p, Path.Combine(root, "Handles.osmap"));
        var visible = new WorldPreview().Build(p);
        Console.WriteLine("Visible stair preview polygons: " + visible.Count(f => f.BrushId.StartsWith("Stair_")));
        var solid = new SolidQuery(StairGeometry.Expand(p.Brushes));
        for (double x = 144; x < 480; x += 11)
        {
            double ceiling = d.Headroom + d.Rise / d.Count + x * d.Rise / d.Run;
            Check(!solid.IsSolid(new(17+x,32,ceiling-.1)) && solid.IsSolid(new(17+x,32,ceiling+.1)), $"The actual carved ceiling is continuous: x={x}, expected {ceiling}, first solid {Enumerable.Range(300,300).FirstOrDefault(z=>solid.IsSolid(new(17+x,32,z)))}");
        }
    }
}
// A gallery for reviewing the actual stair geometry rather than just the palette icons.
var gallery = MapProject.CreateDefault("StairGallery", "Stair usability gallery"); Starter.AddStarterRoom(gallery);
gallery.Brushes[0].Shape = Box.Shape(new(4096, 6144, 2048)); gallery.Brushes[0].Location = new(0, 0, 1024);
int n = 0;
foreach (string layout in StairDesign.Layouts) foreach (bool rounded in new[] { false, true })
{
    var d = new StairDesign { Layout = layout, TurnDegrees = layout == "spiral" ? 360 : 90, Overhang = rounded ? 4 : 0, EdgeRadius = rounded ? 4 : 0, Carve = false };
    gallery.Brushes.Add(StairGeometry.Create("Stair" + n, d, new((n % 2) * 1200 - 900, (n / 2) * 1000 - 2400, 0))); n++;
}
gallery.Workspace.CameraLocation = new(-2200, -2300, 1100); gallery.Workspace.CameraRotation = Orientation.FromAxes(new Vec3(300, 0, 128) - gallery.Workspace.CameraLocation, Vec3.UnitZ);
ProjectStore.Save(gallery, Path.Combine(root, "Gallery.osmap"));
var watch = Stopwatch.StartNew();
for (int i = 0; i < 20; i++) StairGeometry.Create("Resize", new StairDesign { Rise = 256 + i, EdgeRadius = 4, Overhang = 4 }, Vec3.Zero);
Console.WriteLine($"Rounded stair rebuild: {watch.Elapsed.TotalMilliseconds / 20:0.0} ms per edit");
watch.Restart();
for (int i = 0; i < 20; i++) StairGeometry.Generate(new Brush("Resize") { Stair = new() { Rise = 256 + i, EdgeRadius = 4, Overhang = 4 } });
Console.WriteLine($"Rounded stair generation: {watch.Elapsed.TotalMilliseconds / 20:0.0} ms per edit");
if (args.Contains("--render"))
{
    var sample = MapProject.CreateDefault("Spirals", "Connected spiral stairs"); Starter.AddStarterRoom(sample);
    sample.Brushes[0].Shape = Box.Shape(new(3072,3072,1536)); sample.Brushes[0].Location = new(0,0,768);
    sample.LevelInfo.Set("AmbientBrightness", OpenScct.Content.Json.SvJsonNode.Integer(160));
    for (int i = 0; i < 2; i++) sample.Brushes.Add(StairGeometry.Create("Spiral"+i, new() { Layout="spiral", TurnDegrees=360, Overhang=i*4, EdgeRadius=i*4, Carve=false }, new(i*1300-650,-300,0)));
    var camera = new Bookmark { Name="Spirals", Location=new(-1250,-1250,1000), Rotation=Orientation.FromAxes(new Vec3(0,100,128)-new Vec3(-1250,-1250,1000),Vec3.UnitZ) };
    sample.Workspace.CameraLocation=camera.Location; sample.Workspace.CameraRotation=camera.Rotation;
    string folder=Path.Combine(root,"Spirals.osmap"); ProjectStore.Save(sample,folder);
    var built=new MapCompiler().Compile(sample,Path.Combine(root,"Spirals"),new CompileOptions{FastLighting=true,ProjectFolder=folder}); Check(built.Ok,"Spiral sample compiles");
    var recipe=PictureRecipe.OfCamera(camera.Name); var shot=new CameraShot("banner",recipe,camera,1280,720,MapShots.Key(recipe,camera,1280,720));
    var rendered=await MapShots.RenderAsync(sample,folder,built.OutputFolder!,[shot],GameExe.Find()!,true,Console.WriteLine); Check(rendered[0].Ok,"Spiral sample renders in-game");
    File.Copy(rendered[0].File!,Path.Combine(root,"spirals-game.png"),true);
}
Console.WriteLine($"PASS {checks} stair usability checks; samples {root}");
