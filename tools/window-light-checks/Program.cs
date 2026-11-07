using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Windows;
using OpenScct.Game.Lighting;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
bool Near(Vec3 a, Vec3 b) => (a - b).Length < 1e-6;
EditorServices.Discover(typeof(MapProject).Assembly); EditorServices.Classes = ClassCatalog.GeneratedOnly();
string root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(Path.GetTempPath(), "ose-window-light-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var red = new Actor("Red", "SBase.SVitre") { Window = new() { Tint = [1, .05, .05], Finish = "tinted" } };
foreach (string shape in WindowDesign.Shapes)
foreach (string finish in WindowDesign.Finishes)
{
    var pane = red.Clone(); pane.Window!.Shape = shape; pane.Window.Finish = finish; pane.Window.Height = 160;
    var filter = new WindowLightTransmission([pane]);
    Vec3 colour = filter.Between(new(-100, 0, 0), new(100, 0, 0));
    Check(colour.X > colour.Y * 10 && colour.X > colour.Z * 10 && colour.X <= 1, shape + "/" + finish + " transmits red light");
    Check(Near(colour, filter.Between(new(100, 0, 0), new(-100, 0, 0))), "Both sides transmit the same colour");
    Check(Near(filter.Between(new(-100, 0, 0), new(-50, 0, 0)), Vec3.One), "A pane beyond the light cannot colour it");
    pane.Rotation = new(4700, 11000, 2300); pane.DrawScale3D = new(-2, .7, 1.4); pane.PrePivot = new(9, 13, -7); pane.Location = new(100, -60, 200);
    Check(Near(colour, new WindowLightTransmission([pane]).Between(pane.LocalToWorld(new(-100, 0, 0)), pane.LocalToWorld(new(100, 0, 0)))), "Rotated, mirrored and offset panes use their actual plane");
}
var circle = red.Clone(); circle.Window!.Shape = "circle"; circle.Window.Height = 160;
Check(Near(new WindowLightTransmission([circle]).Between(new(-100, 75, 75), new(100, 75, 75)), Vec3.One), "Circle corners do not cast rectangular coloured shadows");
var second = red.Clone(); second.Location = new(10, 0, 0);
var one = new WindowLightTransmission([red]).Between(new(-100, 0, 0), new(100, 0, 0));
Check(Near(new WindowLightTransmission([red, second]).Between(new(-100, 0, 0), new(100, 0, 0)), one.Scale(one)), "Stacked panes multiply once per optical sheet");
Check(Near(new WindowLightTransmission([red]).Filter(new BakeLight { Id = "Sun", Direction = -Vec3.UnitX, Effect = ActorLightRules.LE_Sunlight }, new(-100, 0, 0)), one), "Sunlight transmits the same pane tint");

var light = new BakeLight { Id = "White", Location = new(100, 0, 0), Brightness = 96, LightRadius = 16 };
LightmapChart Chart() => new LightmapChart { Origin = new(-100, -8, -8), StepU = Vec3.UnitY * 4, StepV = Vec3.UnitZ * 4, Normal = Vec3.UnitX,
    Width = 4, Height = 4, Pieces = [new ChartPiece([new(0, 0), new(4, 0), new(4, 4), new(0, 4)], 0)] }.Seal();
BakeScene Scene(Actor? pane, bool wall = false) => new() { Charts = [Chart()], Lights = [light], Ambient = _ => Vec3.Zero,
    Transmission = pane is null ? null : new WindowLightTransmission([pane]).Filter,
    Occluders = TriangleBvh.Build(wall ? [new(new(20, -1000, -1000), new(20, 1000, -1000), new(20, 0, 1000), OccluderFlags.None, 0)] : []) };
foreach (bool faithful in new[] { false, true })
{
    var q = new BakeQuality { Faithful = faithful };
    var baker = new LightmapBaker();
    var neutral = baker.Bake(Scene(null), q, 64);
    var coloured = baker.Bake(Scene(red), q, 64);
    Check(coloured.Texels[0][0] > coloured.Texels[0][1] * 10, "Surface lightmaps contain filtered colour");
    Check(neutral.Texels[0][0] > coloured.Texels[0][0], "Glass attenuates, rather than emits, light");
    var green = red.Clone(); green.Window!.Tint = [.05, 1, .05];
    var warm = baker.Bake(Scene(green), q, 64);
    var cold = new LightmapBaker().Bake(Scene(green), q, 64);
    Check(warm.Rgba(0).SequenceEqual(cold.Rgba(0)) && warm.Texels[0][1] > warm.Texels[0][0] * 10, "Tint edits update colour even with cached shadow masks");
    var blocked = baker.Bake(Scene(red, wall: true), q, 64);
    Check(blocked.Texels[0].All(v => v == 0), "Tint cannot leak through an opaque wall");
    var fast = baker.Bake(Scene(red), q with { Shadows = false }, 64);
    Check(Math.Abs(fast.Texels[0][0] - fast.Texels[0][1]) < 1e-6, "Fast unshadowed passes stay neutral");
}

var project = MapProject.CreateDefault("GlassLight", "Coloured glass lighting"); Starter.AddStarterRoom(project);
project.Brushes[0].Shape = Box.Shape(new(1600, 2400, 900)); project.Brushes[0].Location = new(0, 0, 450);
foreach (var face in project.Brushes[0].Shape.Faces) face.LightMapScale = 12;
project.Actors.RemoveAll(a => a.ShortClass == "Light");
project.LevelInfo.Set("AmbientBrightness", SvJsonNode.Integer(3));
project.Lighting.Bounce = 0;
string[] shapes = ["circle", "triangle", "arch"];
double[][] tints = [[1, .06, .06], [.06, 1, .06], [.06, .12, 1]];
for (int i = 0; i < 3; i++)
{
    double y = (i - 1) * 600;
    var pane = WindowCommands.Create(project, new WindowDesign { Shape = shapes[i], Finish = "tinted", Tint = tints[i], Framed = true, Width = 160, Height = 160 }, new(0, y, 220), Orientation.FromAxes(Vec3.UnitZ, Vec3.UnitY));
    project.Actors.Add(pane);
    var source = new Actor("White" + i, "Engine.Light") { Location = new(0, y, 400) };
    source.Props.Set("LightBrightness", SvJsonNode.Integer(255)); source.Props.Set("LightRadius", SvJsonNode.Integer(24)); source.Props.Set("LightSaturation", SvJsonNode.Integer(255)); project.Actors.Add(source);
}
var camera = new Bookmark { Name = "Coloured shadows", Location = new(-1050, -1200, 1050) };
camera.Rotation = Orientation.FromAxes(new Vec3(0, 0, 0) - camera.Location, Vec3.UnitZ);
project.Workspace.CameraLocation = camera.Location; project.Workspace.CameraRotation = camera.Rotation;
string folder = Path.Combine(root, "GlassLight.osmap"); ProjectStore.Save(project, folder);
var compiled = new MapCompiler().Compile(project, Path.Combine(root, "GlassLight"), new CompileOptions { ProjectFolder = folder });
Check(compiled.Ok, "Coloured glass map compiles: " + string.Join(';', compiled.Findings.Where(f => f.Severity == Severity.Error)));
int[] colouredTexels = new int[3];
foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "GlassLight", "render", "textures"), "*lightmap*.png"))
{
    var image = Png.Decode(File.ReadAllBytes(file))!.Value;
    for (int i = 0; i < image.Rgba.Length; i += 4)
        for (int c = 0; c < 3; c++) if (image.Rgba[i + c] > 12 && image.Rgba[i + c] > 3 * image.Rgba[i + (c + 1) % 3] && image.Rgba[i + c] > 3 * image.Rgba[i + (c + 2) % 3]) colouredTexels[c]++;
}
Check(colouredTexels.All(n => n > 20), "Playable map contains red, green and blue transmitted light: " + string.Join(',', colouredTexels));
var preview = new LightPreview();
var final = preview.Passes(project, folder, new HashSet<string>(), LightPreviewStart.Cold, CancellationToken.None).Last();
Check(final.Final && final.Atlases.Any(a => Enumerable.Range(0, a.Rgba.Length / 4).Any(i => a.Rgba[4 * i] > 3 * a.Rgba[4 * i + 1] && a.Rgba[4 * i] > 12)), "Editor Lit Preview includes coloured transmitted light");
if (args.Contains("--render"))
{
    var recipe = PictureRecipe.OfCamera(camera.Name); var shot = new CameraShot("banner", recipe, camera, 1280, 720, MapShots.Key(recipe, camera, 1280, 720));
    var rendered = await MapShots.RenderAsync(project, folder, compiled.OutputFolder!, [shot], GameExe.Find()!, true, Console.WriteLine);
    Check(rendered.Count == 1 && rendered[0].Ok, "Coloured shadow game render");
    File.Copy(rendered[0].File!, Path.Combine(root, "coloured-shadows-game.png"), true);
}
Console.WriteLine($"PASS {checks} coloured-glass lighting checks. Sample: {folder}");
