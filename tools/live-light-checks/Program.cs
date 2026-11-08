using OpenScct.Content.Json;
using OpenScct.Content;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Windows;

EditorServices.Discover(typeof(MapProject).Assembly);
EditorServices.Classes = ClassCatalog.GeneratedOnly();
string root = Path.GetFullPath(args.FirstOrDefault() ?? "local/live-light-checks");
var project = MapProject.CreateDefault("GlassPulse", "Animated glass lighting");
Starter.AddStarterRoom(project);
project.Brushes[0].Shape = Box.Shape(new(1600, 2400, 900)); project.Brushes[0].Location = new(0, 0, 450);
project.Actors.RemoveAll(a => a.ShortClass == "Light");
project.LevelInfo.Set("AmbientBrightness", SvJsonNode.Integer(3));
project.Lighting.Bounce = 0;
string[] shapes = ["circle", "triangle", "arch"];
double[][] tints = [[1,.06,.06],[.06,1,.06],[.06,.12,1]];
for (int i = 0; i < 3; i++)
{
    double y = (i - 1) * 600;
    project.Actors.Add(WindowCommands.Create(project, new WindowDesign { Shape = shapes[i], Finish = "tinted",
        Tint = tints[i], Framed = true, Width = 160, Height = 160 }, new(0,y,220), Orientation.FromAxes(Vec3.UnitZ,Vec3.UnitY)));
    var light = new Actor("Pulse"+i,"Engine.Light") { Location = new(0,y,400) };
    foreach (var (name,value) in new[] { ("LightBrightness",128),("LightRadius",24),("LightSaturation",255),("LightType",2),("LightPeriod",32) })
        light.Props.Set(name,SvJsonNode.Integer(value));
    light.Props.Set("bDynamicLight",SvJsonNode.True);
    light.Props.Set("bApplyToStaticLighting",SvJsonNode.False);
    project.Actors.Add(light);
}
var camera = new Bookmark { Location = new(-1050,-1200,1050) };
camera.Rotation = Orientation.FromAxes(-camera.Location,Vec3.UnitZ);
project.Workspace.CameraLocation = camera.Location; project.Workspace.CameraRotation = camera.Rotation;
string folder = Path.Combine(root,"GlassPulse.osmap"); ProjectStore.Save(project,folder);
var sheets = new WindowLightTransmission(project.Actors.Where(a=>a.Window is not null));
if (sheets.Sheets.Count != 3 || sheets.Sheets.Any(s=>s.Triangles.Count<3)) throw new Exception("Actual pane outlines are missing from live lighting");
var result = new MapCompiler().Compile(project,Path.Combine(root,"GlassPulse"),new CompileOptions { ProjectFolder=folder });
if (!result.Ok) throw new Exception(string.Join(';',result.Findings.Where(f=>f.Severity==Severity.Error)));
using (var stream=File.OpenRead(Path.Combine(result.OutputFolder!,WindowLightTransfer.RelativePath)))
{
    var response=WindowLightTransfer.Load(stream);
    if(response.Panes.Count!=3 || response.Batches.Any(b=>b.Samples.Count>0))
        throw new Exception("A map lit only by runtime lights must package its pane descriptors without baked response samples");
}
Console.WriteLine("PASS animated glass fixture: "+folder);
