using OpenScct.Content.Json;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Physics.Collision;
using Brush = OpenScct.Editor.Core.Model.Brush;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
EditorServices.Discover(typeof(MapProject).Assembly); EditorServices.Classes = ClassCatalog.GeneratedOnly();
string output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "ose-door-prefabs-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output); Environment.SetEnvironmentVariable("OSE_PREFABS", Path.Combine(output, "personal-prefabs"));
var p = MapProject.CreateDefault("DoorGallery", "Door and prefab gallery"); Starter.AddStarterRoom(p);
p.Brushes[0].Shape = Box.Shape(new Vec3(8192, 8192, 1024)); p.Brushes[0].Location = new(0,0,512);
p.LevelInfo.Set("AmbientBrightness", SvJsonNode.Integer(180)); p.LevelInfo.Set("AmbientSaturation", SvJsonNode.Integer(255));
int index = 0;
foreach (string motion in DoorDesign.Motions) foreach (string style in DoorDesign.Styles)
{
    var d = new DoorDesign { Style = style, Motion = motion }; Vec3 centre = new(0,0,112);
    var a = DoorCommands.Create(p, d, centre, new Rot(0, 16384, 0));
    Check((a.LocalToWorld(Vec3.Zero) - centre).Length < .001, motion + " closed centre at a rotated hinge");
    var m = MoverSetup.Read(a); Check(m.Keys.Count == 2 && m.State == "BumpOpenTimed", "Functional mover keys and activation");
    var open = a.Clone(); open.Location = m.KeyPose(1).Location; open.Rotation = m.KeyPose(1).Rotation;
    if (motion.StartsWith("swing", StringComparison.Ordinal))
    {
        Check((a.LocalToWorld(d.Pivot) - open.LocalToWorld(d.Pivot)).Length < .001, motion + " hinge remains fixed");
        Check((a.LocalToWorld(Vec3.Zero) - open.LocalToWorld(Vec3.Zero)).Length > 80, "Leaf swings clear");
    }
    else Check(Math.Abs((open.Location - a.Location).Length - (motion == "raise" ? d.Height + d.FrameWidth : d.Width + d.FrameWidth)) < .001, "Slide clears full opening");
    var json = ModelJson.ActorFromJson(ModelJson.ActorToJson(a)); Check(json.Door!.ToJson() == d.ToJson(), "Clipboard retains design");
    var copy = a.Clone(); copy.Door!.Width = 196; Check(a.Door!.Width == 128, "Independent design clone");
    var parts = DoorGeometry.Leaf(d); Check(parts.SelectMany(s => s.Shape.Faces).SelectMany(f => f.Vertices).All(v => double.IsFinite(v.X + v.Y + v.Z)), "Finite generated mesh");
    Check(AssetResolver.Empty.FindMesh(DoorAssets.Prepare(a)) is not null, "Generated mesh resolves without content packs");
    var authored = DoorCommands.Create(p,d,new Vec3(-1500 + index%6*400,-1200 + index/6*450,112));
    p.Actors.Add(authored); index++;
    var session = new EditorSession(p); string id = authored.Id;
    session.Execute(DoorCommands.Edit(p,id,x => { x.Width = 192; x.Height = 256; }));
    var resized = p.FindActor(id)!;
    Check(Math.Abs(resized.LocalToWorld(new Vec3(0,0,-128)).Z) < .001,"Inspector resizing keeps threshold on floor");
    Check(session.Commands.Undo() && p.FindActor(id)!.Door!.Width == 128, "Undo door edit");
    Check(session.Commands.Redo() && p.FindActor(id)!.Door!.Width == 192, "Redo door edit"); session.Commands.Undo();
    var keyEdit = MoverEdits.ShowKey(p,id,1); keyEdit.Do(p);
    var frame = DoorGeometry.Parts(p.FindActor(id)!); keyEdit.Undo(p);
    Check(frame.SelectMany(b => b.WorldFaces()).SelectMany(f => f.Vertices).SequenceEqual(DoorGeometry.Parts(p.FindActor(id)!).SelectMany(b => b.WorldFaces()).SelectMany(f => f.Vertices)), "Open preview leaves frame fixed");
}
Console.WriteLine("PASS door styles, pivot motion, editing, clipboard, undo and fixed frames");
var first = p.Actors.First(a => a.Door is not null);
var slider = p.Actors.First(a => a.Door?.Motion == "slide right");
var rotate = ReplaceObjectsCommand.Edit(p,[ObjectRef.Actor(slider.Id)],"Turn door",actor: a => a.Rotation = new(0,16384,0)); rotate.Do(p);
var rotated = MoverSetup.Read(p.FindActor(slider.Id)!);
Check((rotated.Keys[1].Offset - new Vec3(-136,0,0)).Length < .01,"Rotating a door carries its sliding path with it"); rotate.Undo(p);
var scale = ReplaceObjectsCommand.Edit(p,[ObjectRef.Actor(first.Id)],"Scale door",actor: a => a.DrawScale3D = new(2,1.5,2)); scale.Do(p);
Check(p.FindActor(first.Id)!.Door!.Width == 192 && p.FindActor(first.Id)!.Door!.Height == 448, "Scale becomes authored dimensions"); scale.Undo(p);
var expanded = DoorGeometry.Expand(p); Check(p.Brushes.Count == 1 && expanded.Brushes.Count == 91, "Expansion leaves authored map unchanged");
Check(ReferenceEquals(expanded,DoorGeometry.Expand(expanded)), "Door expansion is idempotent");
string save = Path.Combine(output,"DoorGallery.osmap"); ProjectStore.Save(p,save); var reopened = ProjectStore.Load(save);
Check(reopened.Actors.Count(a => a.Door is not null) == 30, "Save and reopen every style and motion");
Check(File.ReadAllText(Path.Combine(save,"map.json")).Contains("osmap/4"), "Older editors reject door authoring instead of losing it");
var ordinary = MapProject.CreateDefault("Ordinary","No doors"); ProjectStore.Save(ordinary,Path.Combine(output,"Ordinary.osmap"));
Check(!File.ReadAllText(Path.Combine(output,"Ordinary.osmap","map.json")).Contains("osmap/4"), "Ordinary projects keep existing format");

Check(PrefabStore.Builtin.Count >= 42, "Expanded prefab library is embedded");
foreach (var def in PrefabStore.Builtin)
{
    var preview = PrefabPreview.Expand(def.Name,null,out string? problem); Check(preview is not null, def.Name + ": " + problem);
    Check(preview!.AllRefs().Any(),def.Name + " has real parts");
    foreach (string boundary in new[] { "min", "max" })
    {
        var values = new SvJsonObject();
        foreach (var param in def.Params) if ((boundary == "min" ? param.Min : param.Max) is { } value) values.Add(param.Name,ApiJson.Num(value));
        var session = EditorSession.New("Boundary","Parameter boundary");
        var op = ApiJson.Obj(("op",ApiJson.Str("prefab.place")),("name",ApiJson.Str(def.Name)),("params",values));
        var result = ChangeApplier.Apply(session,ApiJson.Obj(("format",ApiJson.Str(ChangeApplier.Format)),("ops",ApiJson.Arr([op]))),writeFiles:false);
        Check(result.Ok,def.Name + " at " + boundary + ": " + ApiJson.Write(result.Json));
    }
}
var ladder = PrefabPreview.Expand("ladder",null,out _)!;
Check(ladder.Ges.Single().Appearance is not null && GeGeometry.Expand(ladder).Brushes.Count > 2,"Ladder includes rails and rungs");
Check(PrefabPreview.Expand("zipline",null,out _)!.Ges.Single().Appearance is not null,"Zip line includes cable");
var linked = EditorSession.New("Links","Independent switches");
SvJsonObject Place(string name,Vec3 at) => ApiJson.Obj(("op",ApiJson.Str("prefab.place")),("name",ApiJson.Str(name)),("at",ApiJson.Vec(at)));
var pair = ChangeApplier.Apply(linked,ApiJson.Obj(("format",ApiJson.Str(ChangeApplier.Format)),("ops",ApiJson.Arr([Place("door_switch",Vec3.Zero),Place("door_switch",new Vec3(1024,0,0))]))),writeFiles:false);
Check(pair.Ok,"Place two linked assemblies");
var doors = linked.Project.Actors.Where(a => a.Door is not null).ToList(); var switches = linked.Project.Actors.Where(a => a.ShortClass == "SInterrupteur").ToList();
Check(doors.Count == 2 && switches.Count == 2 && doors[0].Tag != doors[1].Tag,"Door instances own distinct tags");
Check(switches.All(s => doors.Count(d => d.Tag == s.Props.GetString("Event")) == 1),"Each switch targets its own door");
Console.WriteLine("PASS all prefab defaults, min/max parameter combinations and event isolation");

var cut = MapProject.CreateDefault("DoorCut","Owned doorway"); Starter.AddStarterRoom(cut);
var wall = new Brush("Wall") { Oper = CsgOper.Add, Shape = Box.Shape(new(64,512,384)), Location = new(0,0,192) }; cut.Brushes.Add(wall);
var door = DoorCommands.Create(cut,new DoorDesign { Motion = "raise", CutDepth = 66, Wall = wall.Id },new(0,0,112)); cut.Actors.Add(door);
cut.Workspace.CameraLocation = new(-400,-350,260); cut.Workspace.CameraRotation = new(-1200,7000,0);
ProjectStore.Save(cut,Path.Combine(output,"DoorPlacement.osmap"));
Console.WriteLine("Compiling generated meshes and managed wall opening…");
var compiled = new MapCompiler().Compile(cut,Path.Combine(output,"DoorCut"),new CompileOptions { FastLighting = true });
Check(compiled.Ok,"Build and strict loader: " + string.Join(';',compiled.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
Check(!compiled.Findings.Any(f => f.Code.Contains("missing",StringComparison.Ordinal)),"No missing generated assets");
var analysis = AnalysisMap.Build(cut,null);
var movers = new List<OpenScct.Core.Engine.Mover>(); foreach (var mover in analysis.World.Level.AllActors<OpenScct.Core.Engine.Mover>()) movers.Add(mover);
Check(movers.Count == 1,"Game loads generated door as a mover");
analysis.Collision.LineCheck(out var closedHit,null,new FVector(100,0,100),new FVector(-100,0,100),default,ETraceFlags.TRACE_AllBlocking);
Check(closedHit.Time < 1,"Closed leaf blocks doorway");
OpenScct.Game.Devices.DeviceNatives.Install(analysis.World.Level, analysis.Info.Physics!.Driver);
var runtimeDoor = movers.Single(); runtimeDoor.InterpolateTo(1, .25f); analysis.World.Step(45);
Check(runtimeDoor.Location.Z > 200,"Runtime leaf travels to its open key");
analysis.Collision.LineCheck(out var openHit,null,new FVector(100,0,100),new FVector(-100,0,100),default,ETraceFlags.TRACE_AllBlocking);
Check(openHit.Time == 1,"Runtime moving collider frees doorway");
runtimeDoor.InterpolateTo(0,.25f); analysis.World.Step(45);
analysis.Collision.LineCheck(out var shutHit,null,new FVector(100,0,100),new FVector(-100,0,100),default,ETraceFlags.TRACE_AllBlocking);
Check(shutHit.Time < 1,"Runtime collider blocks again after closing");
var remove = new DeleteObjectsCommand([ObjectRef.Actor(door.Id)]); remove.Do(cut);
var restored = AnalysisMap.Build(cut,null); restored.Collision.LineCheck(out var restoredHit,null,new FVector(100,0,100),new FVector(-100,0,100),default,ETraceFlags.TRACE_AllBlocking);
Check(restoredHit.Time < 1,"Removing door restores wall"); remove.Undo(cut);
// Inspect CSG with the leaf removed after lowering: the hole must be empty, and jambs solid.
var onlyFrame = DoorGeometry.Expand(cut); onlyFrame.Actors.RemoveAll(a => a.Door is not null);
var opening = AnalysisMap.Build(onlyFrame,null);
opening.Collision.LineCheck(out var hole,null,new FVector(100,0,100),new FVector(-100,0,100),default,ETraceFlags.TRACE_AllBlocking);
Check(hole.Time == 1,"Owned opening cuts the full editable wall");
opening.Collision.LineCheck(out var jamb,null,new FVector(100,68,100),new FVector(-100,68,100),default,ETraceFlags.TRACE_AllBlocking);
Check(jamb.Time < 1,"Permanent jamb stays solid");

// Compile a full kit in a fresh map to catch packaging/material regressions across all new props.
var kit = MapProject.CreateDefault("PrefabGallery","Common prefab library"); Starter.AddStarterRoom(kit);
kit.Brushes[0].Shape = Box.Shape(new(8192,8192,1024)); kit.Brushes[0].Location = new(0,0,512); var kitSession = new EditorSession(kit); index = 0;
foreach (var def in PrefabStore.Builtin.Where(d => d.Category is "furniture" or "storage" or "technology" or "exterior" or "architecture" or "industrial" or "lighting" || d.Name is "swing_door" or "sliding_door" or "roller_door" or "glazed_door"))
{
    var result = ChangeApplier.Apply(kitSession,ApiJson.Obj(("format",ApiJson.Str(ChangeApplier.Format)),("ops",ApiJson.Arr([Place(def.Name,new Vec3(-2200+index%6*800,-1800+index/6*800,0))]))),writeFiles:false);
    Check(result.Ok,"Gallery placement "+def.Name); index++;
}
kit.Workspace.CameraLocation = new(-3300,-3200,2200); kit.Workspace.CameraRotation = new(-3500,7800,0); kit.LevelInfo.Set("AmbientBrightness",SvJsonNode.Integer(200));
ProjectStore.Save(kit,Path.Combine(output,"PrefabGallery.osmap"));
var builtKit = new MapCompiler().Compile(kit,Path.Combine(output,"PrefabGallery"),new CompileOptions { FastLighting=true });
Check(builtKit.Ok,"Kit builds and loads: " + string.Join(';',builtKit.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
Check(!builtKit.Findings.Any(f => f.Code.Contains("missing",StringComparison.Ordinal)),"All kit materials are packaged");
DoorAssemblyChecks.Run(Check,output);
Console.WriteLine($"PASS {checks} door/prefab checks. Samples: {output}");
