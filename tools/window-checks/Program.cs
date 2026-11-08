using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Windows;
using OpenScct.Editor.Core.Identity;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Game.SBase;
using OpenScct.Physics.Collision;
using OpenScct.Net;
using OpenScct.Server;
using OpenScctGame.Play.Session;
using Actor = OpenScct.Editor.Core.Model.Actor;
using Brush = OpenScct.Editor.Core.Model.Brush;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
EditorServices.Discover(typeof(MapProject).Assembly); EditorServices.Classes = ClassCatalog.GeneratedOnly();
string root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(Path.GetTempPath(), "ose-windows-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var gallery = MapProject.CreateDefault("WindowTest", "Window tools test gallery"); Starter.AddStarterRoom(gallery);
gallery.LevelInfo.Set("AmbientBrightness", OpenScct.Content.Json.SvJsonNode.Integer(160));
gallery.LevelInfo.Set("AmbientSaturation", OpenScct.Content.Json.SvJsonNode.Integer(255));
gallery.Brushes[0].Shape = Box.Shape(new Vec3(4096, 4096, 2048)); gallery.Brushes[0].Location = new Vec3(0, 0, 1024);
gallery.Workspace.CameraLocation = new Vec3(-1400, -1100, 900); gallery.Workspace.CameraRotation = new Rot(-1600, 6500, 0);
foreach (string shape in WindowDesign.Shapes) foreach (string finish in WindowDesign.Finishes)
{
    int i = Array.IndexOf(WindowDesign.Shapes, shape), j = Array.IndexOf(WindowDesign.Finishes, finish);
    var d = new WindowDesign { Shape = shape, Finish = finish, Framed = true, Width = 160, Height = shape == "circle" ? 160 : 128 };
    var a = WindowCommands.Create(gallery, d, new Vec3(0, (i - 2.5) * 240, 200 + j * 240)); gallery.Actors.Add(a);
    var outline = WindowGeometry.Outline(d); Check(WindowGeometry.Triangles(outline).Count == outline.Count - 2, shape + " triangulates");
    Check(WindowGeometry.Pane(d).Faces.All(f => f.Vertices.All(v => double.IsFinite(v.X + v.Y + v.Z))), "Finite pane geometry");
    var raw = ModelJson.ActorFromJson(ModelJson.ActorToJson(a)); Check(raw.Window!.ToJson() == d.ToJson(), "Clipboard design " + shape + "/" + finish);
    var edit = WindowCommands.Edit(gallery, a.Id, x => WindowTransforms.Resize(x, 192, x.Shape == "circle" ? 192 : 180)); edit.Do(gallery);
    Check(gallery.FindActor(a.Id)!.Window!.Width == 192, "Resize " + shape); edit.Undo(gallery);
    Check(gallery.FindActor(a.Id)!.Window!.Width == 160, "Undo resize");
    var clone = a.Clone("Independent"); clone.Window!.Tint[0] = .1; Check(a.Window!.Tint[0] != .1, "Clone owns its tint array");
}
Console.WriteLine("PASS shape/finish geometry and editing");
bool refusedClearance = false;
try { WindowGeometry.Pane(new WindowDesign { Clearance = 96 }); } catch (ArgumentException) { refusedClearance = true; }
Check(refusedClearance, "Excessive clearance cannot invert a pane");
string save = Path.Combine(root, "WindowTest.osmap"); ProjectStore.Save(gallery, save); gallery = ProjectStore.Load(save);
Check(File.ReadAllText(Path.Combine(save, "map.json")).Contains("osmap/3"), "Feature format gate");
Check(gallery.Actors.Count(a => a.Window is not null) == 18, "Save/reopen all shapes and finishes");
var legacy = MapProject.CreateDefault("Legacy", "Legacy"); ProjectStore.Save(legacy, Path.Combine(root, "Legacy.osmap"));
Check(!File.ReadAllText(Path.Combine(root, "Legacy.osmap", "map.json")).Contains("osmap/3"), "Ordinary projects retain their format");

// Closed imported meshes, both directions, non-uniform scale, rotation and an off-origin pivot.
foreach (string shape in WindowDesign.Shapes)
{
    var p = new MapProject(); var d = new WindowDesign { Shape = shape, Framed = true, Height = shape == "circle" ? 160 : 128 };
    var template = WindowCommands.Create(p, d, Vec3.Zero); string path = WindowAssets.Prepare(template) + "_Frame";
    var frame = new Actor("ImportedFrame", "Engine.StaticMeshActor") { Location = new Vec3(200, 300, 100), Rotation = new Rot(2200, 10500, 1200), DrawScale3D = new Vec3(1.5, .75, 2), PrePivot = new Vec3(100, 20, 30) };
    frame.Props.Set("StaticMesh", PropValues.AssetRef(path)); p.Actors.Add(frame);
    Vec3 centre = frame.LocalToWorld(Vec3.Zero), normal = frame.Rotation.Axes().Forward;
    foreach (int side in new[] { -1, 1 })
    {
        var hits = WindowFit.Find(p, AssetResolver.Empty, centre + normal * 1000 * side, normal * -side);
        Check(hits.Count == 1, $"Find imported {shape} side={side}: {hits.Count}");
        Check(Math.Abs(Vec3.Dot(hits[0].Centre - centre, normal)) < .1, $"Fit off-origin {shape} frame plane");
        Check(Math.Abs(WindowGeometry.Area(hits[0].Outline) - WindowGeometry.Area(WindowGeometry.Outline(d)) * .75 * 2) < 1, "Fit preserves actual opening area " + shape);
        var pane = WindowCommands.Fit(p, hits[0]); p.Actors.Add(pane);
        Check(WindowFit.Find(p, AssetResolver.Empty, centre + normal * 1000 * side, normal * -side).Count == 0, "Prevent duplicate glass"); p.Actors.Remove(pane);
    }
    frame.Editor.Locked = true; Check(WindowFit.Find(p, AssetResolver.Empty, centre + normal * 1000, -normal).Count == 0, "Ignore locked source");
    frame.Editor.Locked = false;
    var oldGlass = new Actor("ExistingGlass", "SBase.SVitre") { Location = centre, Rotation = frame.Rotation, DrawScale3D = frame.DrawScale3D };
    oldGlass.Props.Set("StaticMesh", PropValues.AssetRef(WindowAssets.Prepare(template))); p.Actors.Add(oldGlass);
    Check(WindowFit.Find(p, AssetResolver.Empty, centre + normal * 1000, -normal).Count == 0, "Existing non-parametric glass prevents duplicate insertion");
}
Console.WriteLine("PASS imported shapes, pivots, transforms and duplicate protection");

// An actual imported mesh containing several openings: the cursor chooses one pane, not the full mesh bounds.
var multi = new MapProject(); var obj = new System.Text.StringBuilder(); int vertex = 1;
foreach (double y in new[] { -100.0, 100.0 })
{
    var frame = WindowCommands.Create(multi, new WindowDesign { Framed = true }, new Vec3(0, y, 0));
    foreach (var part in WindowGeometry.Parts(frame))
    {
        obj.AppendLine("g part" + vertex);
        foreach (var face in part.WorldFaces())
        {
            foreach (var v in face.Vertices) obj.AppendLine(FormattableString.Invariant($"v {v.X:R} {v.Y:R} {v.Z:R}"));
            obj.AppendLine("f " + string.Join(' ', Enumerable.Range(vertex, face.Vertices.Count))); vertex += face.Vertices.Count;
        }
    }
}
string objPath = Path.Combine(root, "two-frames.obj"); File.WriteAllText(objPath, obj.ToString());
string assetFolder = Path.Combine(root, "Imported.osmap");
var imported = OpenScct.Editor.Core.Assets.Import.MeshImporter.Import(assetFolder, objPath, new() { Name = "TwoFrames", ZUp = true, Collision = OpenScct.Editor.Core.Assets.Import.CollisionMode.Hull, Overwrite = true });
var multiFrame = new Actor("TwoFrames", "Engine.StaticMeshActor"); multiFrame.Props.Set("StaticMesh", PropValues.AssetRef(imported.Path)); multi.Actors.Add(multiFrame);
foreach (double y in new[] { -100.0, 100.0 })
{
    var openings = WindowFit.Find(multi, AssetResolver.For(null, assetFolder), new Vec3(-1000, y, 0), Vec3.UnitX);
    Check(openings.Count == 1 && Math.Abs(openings[0].Centre.Y - y) < .1, "Imported multi-pane frame chooses clicked opening");
}
var farther = multiFrame.Clone("SecondFrame"); farther.Location = new Vec3(64, 0, 0); multi.Actors.Add(farther);
Check(WindowFit.Find(multi, AssetResolver.For(null, assetFolder), new Vec3(-1000, -100, 0), Vec3.UnitX).Count == 2, "Ambiguous depth returns candidates for preview");
multi.Actors.Remove(farther); multiFrame.Location = new Vec3(0, 0, 192); multi.Id = "FitTest"; multi.Title = "Fit glass into an imported frame";
Starter.AddStarterRoom(multi); multi.Workspace.CameraLocation = new Vec3(-512, -100, 192); multi.Workspace.CameraRotation = Rot.Zero;
ProjectStore.Save(multi, assetFolder);
var fittedOpening = WindowFit.Find(multi, AssetResolver.For(null, assetFolder), new Vec3(-400, -100, 192), Vec3.UnitX).Single();
var fittedGlass = WindowCommands.Fit(multi, fittedOpening); multi.Actors.Add(fittedGlass);
fittedGlass.Window!.Binding = null; // Detaching must retain the frame's usable collision opening.
var importedRuntime = AnalysisMap.Build(multi, assetFolder); OpenScct.Game.Devices.DeviceNatives.Install(importedRuntime.World.Level, importedRuntime.Info.Physics!.Driver);
importedRuntime.Collision.LineCheck(out var importedIntact, null, new FVector(100,-100,192), new FVector(-100,-100,192), default, ETraceFlags.TRACE_AllBlocking);
Check(importedIntact.Time < 1, "Imported frame's fitted pane blocks while intact");
foreach (var pane in Panes(importedRuntime.World.Level)) pane.eventTakeDamage(1,null,default,default,SDamageType.SDTFlash);
importedRuntime.Collision.LineCheck(out var importedBroken, null, new FVector(100,-100,192), new FVector(-100,-100,192), default, ETraceFlags.TRACE_AllBlocking);
Check(importedBroken.Time == 1, "Imported hull cannot leave invisible collision in the frame after shattering");
var overlapping = new MapProject();
foreach (var (at, size) in new[] { (new Vec3(0,-90,0),new Vec3(16,20,160)), (new Vec3(0,90,0),new Vec3(16,20,160)), (new Vec3(0,0,-70),new Vec3(16,200,20)), (new Vec3(0,0,70),new Vec3(16,200,20)) })
    overlapping.Brushes.Add(new Brush("Bar" + overlapping.Brushes.Count) { Shape = Box.Shape(size), Location = at, Oper = CsgOper.Add });
Check(WindowFit.Find(overlapping, AssetResolver.Empty, new Vec3(-1000, 0, 0), Vec3.UnitX).Count == 1, "Overlapping brush bars union into one frame");
overlapping.Brushes.RemoveAt(0); Check(WindowFit.Find(overlapping, AssetResolver.Empty, new Vec3(-1000, 0, 0), Vec3.UnitX).Count == 0, "An open-sided frame is rejected");
Console.WriteLine("PASS real mesh import, multiple panes, ambiguity and invalid frames");
var tiltedDesign = WindowCommands.Create(new MapProject(), new WindowDesign { Framed = true }, Vec3.Zero, new Rot(1600,7000,2400));
var tiltedObj = new System.Text.StringBuilder(); int tiltedVertex = 1;
foreach (var part in WindowGeometry.Parts(tiltedDesign)) foreach (var face in part.WorldFaces())
{
    foreach (var v in face.Vertices) tiltedObj.AppendLine(FormattableString.Invariant($"v {v.X:R} {v.Y:R} {v.Z:R}"));
    tiltedObj.AppendLine("f " + string.Join(' ', Enumerable.Range(tiltedVertex, face.Vertices.Count))); tiltedVertex += face.Vertices.Count;
}
string tiltedPath = Path.Combine(root,"tilted-frame.obj"); File.WriteAllText(tiltedPath, tiltedObj.ToString());
var tiltedImport = OpenScct.Editor.Core.Assets.Import.MeshImporter.Import(assetFolder, tiltedPath, new() { Name = "TiltedFrame", ZUp = true, Overwrite = true });
var tiltedProject = new MapProject(); var tiltedActor = new Actor("TiltedFrame", "Engine.StaticMeshActor"); tiltedActor.Props.Set("StaticMesh", PropValues.AssetRef(tiltedImport.Path)); tiltedProject.Actors.Add(tiltedActor);
var tiltedNormal = tiltedDesign.Rotation.Axes().Forward;
Check(WindowFit.Find(tiltedProject, AssetResolver.For(null,assetFolder), tiltedNormal * 1000, -tiltedNormal).Count == 1, "Frame plane rotated inside the imported asset is detected");

var linked = new MapProject(); var prototype = WindowCommands.Create(linked, new WindowDesign { Framed = true }, Vec3.Zero);
linked.Brushes.AddRange(WindowGeometry.Parts(prototype));
var brushHits = WindowFit.Find(linked, AssetResolver.Empty, new Vec3(-1000, 0, 0), Vec3.UnitX);
Check(brushHits.Count == 1, "Separate brush bars form one opening: " + brushHits.Count);
var bound = WindowCommands.Fit(linked, brushHits[0]); linked.Actors.Add(bound); var stack = new CommandStack(linked);
var sourceRefs = bound.Window!.Binding!.Sources.Select(id => ObjectRef.Brush(id)).ToList();
stack.Execute(ReplaceObjectsCommand.Edit(linked, sourceRefs, "Move frame", brush: b => b.Location += new Vec3(100, 50, 20), mergeKey: "drag"));
Check((linked.FindActor(bound.Id)!.Location - new Vec3(100, 50, 20)).Length < .01, "Glass follows frame movement");
stack.Execute(ReplaceObjectsCommand.Edit(linked, sourceRefs, "Move frame", brush: b => b.Location += new Vec3(10, 0, 0), mergeKey: "drag"));
Check(stack.Position == 1, "Linked movement merges"); stack.Undo(); Check(linked.FindActor(bound.Id)!.Location.Length < .01, "Undo source plus linked glass"); stack.Redo();
Check((linked.FindActor(bound.Id)!.Location - new Vec3(110, 50, 20)).Length < .01, "Redo source plus linked glass");
stack.EndMerge(); stack.Execute(new DeleteObjectsCommand(sourceRefs)); Check(linked.Actors.Count == 0, "Delete frame removes glass"); stack.Undo(); Check(linked.Actors.Count == 1, "Undo cascade deletion");
var array = SelectionEdit.Array(linked, sourceRefs.Append(ObjectRef.Actor(bound.Id)).ToList(), 1, new Vec3(0, 400, 0), out _); stack.Execute(array);
Check(linked.Actors.Last().Window!.Binding!.Sources.All(id => !sourceRefs.Any(r => r.Id == id)), "Copied assembly links to copied frame");
var detachedCopy = SelectionEdit.Array(linked, [ObjectRef.Actor(bound.Id)], 1, new Vec3(0, 800, 0), out _); stack.Execute(detachedCopy);
Check(linked.Actors.Last().Window!.Binding is null, "Copied pane alone detaches");
stack.EndMerge(); stack.Execute(ReplaceObjectsCommand.Edit(linked, [sourceRefs.Last()], "Change one frame bar", brush: b => b.Location += new Vec3(0, 0, 20)));
Check(linked.FindActor(bound.Id)!.Window!.Binding!.NeedsRefit, "An individually moved frame bar flags Refit"); stack.Undo();
Check(!linked.FindActor(bound.Id)!.Window!.Binding!.NeedsRefit, "Undo clears invalidated fit");
var normalBefore = linked.FindActor(bound.Id)!.Window!.Thickness;
stack.EndMerge(); stack.Execute(ReplaceObjectsCommand.Edit(linked, sourceRefs, "Scale frame", brush: b => { b.Location *= 2; b.MainScale *= 2; }));
Check(Math.Abs(linked.FindActor(bound.Id)!.Window!.Width - 320) < .1 && Math.Abs(linked.FindActor(bound.Id)!.Window!.Thickness - normalBefore * 2) < .01, "Linked pane follows frame scale in all dimensions");
Check(!linked.FindActor(bound.Id)!.Window!.Binding!.NeedsRefit, "Scaling the whole frame keeps a valid fit"); stack.Undo();
stack.EndMerge(); var turn = new Rot(0,8192,0);
stack.Execute(ReplaceObjectsCommand.Edit(linked, sourceRefs, "Rotate frame", brush: b => { b.Location = turn.Rotate(b.Location); b.Rotation = turn; }));
Check(Vec3.Dot(linked.FindActor(bound.Id)!.Rotation.Axes().Forward, turn.Axes().Forward) > .999, "Linked glass follows frame rotation"); stack.Undo();
Console.WriteLine("PASS brush fitting, linked history, deletion and copying");

var circle = gallery.Actors.First(a => a.Window?.Shape == "circle");
var scale = ReplaceObjectsCommand.Edit(gallery, [ObjectRef.Actor(circle.Id)], "Scale circle", actor: a => a.DrawScale3D = new Vec3(1, 2, 1)); scale.Do(gallery);
Check(gallery.FindActor(circle.Id)!.Window!.Width == 320 && gallery.FindActor(circle.Id)!.Window!.Height == 320, "Circle aspect survives scale gizmo"); scale.Undo(gallery);
var expanded = WindowGeometry.Expand(gallery); Check(gallery.Brushes.Count == 1 && expanded.Brushes.Count > 1, "Generated frame brushes do not leak into project");
Check(WindowGeometry.Expand(expanded).Brushes.Count == expanded.Brushes.Count, "Window lowering is idempotent");
var windowFindings = new OpenScct.Editor.Core.Validation.MapChecker().Check(gallery, new CheckOptions { Only = ["window."] });
Check(!windowFindings.Any(f => f.Severity == Severity.Error), "Window Map Check accepts the gallery");
var originalFindings = new OpenScct.Editor.Core.Validation.MapChecker().Check(gallery, new CheckOptions { Only = ["window."], Target = TargetGame.Original });
Check(!originalFindings.Any(f => f.Severity == Severity.Error) && originalFindings.Count(f => f.Code == "window.originalFallback") == 18,
    "Original Map Check explains the automatic SDC window fallback without blocking valid windows");
Console.WriteLine("Building sample map…");
var compiled = new MapCompiler().Compile(gallery, Path.Combine(root, "WindowTest"), new CompileOptions { FastLighting = true, ProjectFolder = save });
Check(compiled.Ok, "Compile sample: " + string.Join(';', compiled.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
Check(!compiled.Findings.Any(f => f.Code == "render.mesh-missing"), "All generated glass meshes packaged");

// Real map loader and collision filters: each pane blocks, shatters and frees its exact outline.
var analysis = AnalysisMap.Build(gallery, save); var panes = Panes(analysis.World.Level);
OpenScct.Game.Devices.DeviceNatives.Install(analysis.World.Level, analysis.Info.Physics!.Driver);
Check(panes.Count == 18, "Runtime loads ordinary SVitre actors");
foreach (var pane in panes)
{
    var colliders = new List<int>(); analysis.Collision.GetActorColliders(pane, colliders);
    Check(colliders.Count > 0 && colliders.All(i => analysis.Collision.GetCollider(i).Enabled), "Intact pane collision");
    var from = pane.Location + new FVector(-100, 0, 0); var to = pane.Location + new FVector(100, 0, 0);
    analysis.Collision.LineCheck(out var intact, null, to, from, default, ETraceFlags.TRACE_AllBlocking);
    Check(intact.Time < 1, "Shot hits glass centre");
    bool isCircle = gallery.Actors.Any(a => a.Window?.Shape == "circle" && (a.Location - new Vec3(pane.Location.X,pane.Location.Y,pane.Location.Z)).Length < .1);
    if (isCircle)
    {
        var outside = new FVector(0,70,70);
        analysis.Collision.LineCheck(out var emptyCorner,null,to + outside,from + outside,default,ETraceFlags.TRACE_AllBlocking);
        Check(emptyCorner.Time == 1, "Circular glass has no rectangular corner collider");
    }
    var mover = new SPawnAttaque { bIsAPawn = true, bCollideActors = true, bBlockActors = true, bBlockPlayers = true };
    analysis.Collision.LineCheck(out var movement, mover, to, from, new FVector(2, 2, 2), ETraceFlags.TRACE_AllBlocking);
    Check(movement.Time < 1, "Intact glass blocks a movement sweep");
    pane.eventTakeDamage(1, null, default, default, SDamageType.SDTFlash);
    Check(colliders.All(i => !analysis.Collision.GetCollider(i).Enabled), $"Broken glass frees collision: hp={pane.HitPoints} mesh={pane.StaticMesh} stages={pane.DamagedMeshes.Count} initial={pane.InitialHitPoints}");
    analysis.Collision.LineCheck(out var broken, null, to, from, default, ETraceFlags.TRACE_AllBlocking);
    Check(broken.Time == 1, "Shot passes through broken pane");
    analysis.Collision.LineCheck(out var free, mover, to, from, new FVector(2, 2, 2), ETraceFlags.TRACE_AllBlocking);
    Check(free.Time == 1, "Broken glass frees movement sweep");
    Check(pane.DrawType == EDrawType.DT_None, "Broken pane hides generated mesh");
    if (isCircle)
    {
        var edge = new FVector(0,83,0);
        analysis.Collision.LineCheck(out var frameHit,null,to + edge,from + edge,default,ETraceFlags.TRACE_AllBlocking);
        Check(frameHit.Time < 1, "Freestanding frame retains collision after breaking glass");
    }
}
Console.WriteLine("PASS compiled map loader, glass damage and collision");

// Managed cuts travel with their owner and preserve the shape's empty corners.
var cutMap = MapProject.CreateDefault("WindowCut", "Managed window opening"); Starter.AddStarterRoom(cutMap);
var wall = new Brush("Wall") { Oper = CsgOper.Add, Shape = Box.Shape(new Vec3(64, 512, 384)), Location = new Vec3(0, 0, 192) }; cutMap.Brushes.Add(wall);
cutMap.Workspace.CameraLocation = new Vec3(-384,0,192); cutMap.Workspace.CameraRotation = Rot.Zero;
ProjectStore.Save(cutMap,Path.Combine(root,"WallPlacement.osmap"));
var carvedWindow = WindowCommands.Create(cutMap, new WindowDesign { Shape = "circle", Width = 160, Framed = true, CutDepth = 66, Wall = wall.Id }, new Vec3(0, 0, 192)); cutMap.Actors.Add(carvedWindow);
var cutAnalysis = AnalysisMap.Build(cutMap, null); OpenScct.Game.Devices.DeviceNatives.Install(cutAnalysis.World.Level, cutAnalysis.Info.Physics!.Driver);
foreach (var pane in Panes(cutAnalysis.World.Level)) pane.eventTakeDamage(1, null, default, default, SDamageType.SDTFlash);
cutAnalysis.Collision.LineCheck(out var hole, null, new FVector(100, 0, 192), new FVector(-100, 0, 192), default, ETraceFlags.TRACE_AllBlocking);
Check(hole.Time == 1, "Managed opening cuts through the full wall");
cutAnalysis.Collision.LineCheck(out var corner, null, new FVector(100, 75, 267), new FVector(-100, 75, 267), default, ETraceFlags.TRACE_AllBlocking);
Check(corner.Time < 1, "Circle leaves the wall outside its actual outline");
cutAnalysis.Collision.LineCheck(out var rim, null, new FVector(100, 83, 192), new FVector(-100, 83, 192), default, ETraceFlags.TRACE_AllBlocking);
Check(rim.Time < 1, "Frame remains solid after the pane shatters");
var remove = new DeleteObjectsCommand([ObjectRef.Actor(carvedWindow.Id)]); remove.Do(cutMap); var restored = AnalysisMap.Build(cutMap, null);
restored.Collision.LineCheck(out var filled, null, new FVector(100, 0, 192), new FVector(-100, 0, 192), default, ETraceFlags.TRACE_AllBlocking);
Check(filled.Time < 1, "Deleting a window restores its owned wall opening"); remove.Undo(cutMap);
Console.WriteLine("PASS shaped wall cutting and permanent frame collision");

if (args.Contains("--network"))
{
    var clock = new WindowClock(); using var play = new PlaySession(new PlaySettings { Map = "WindowTest", MapRoots = [root], Side = PlaySide.Spy, Rules = PlayRules.PlayLevel, HostMode = PlayHostMode.Inline, Bots = 0 }, clock);
    void Tick() { clock.NowMicroseconds += 1_000_000 / Sim.Hz; play.Step(clock.NowMicroseconds); play.Input.EndTick(); }
    for (int tick = 0; tick < 900 && (play.LocalPawn is null || play.ServerController?.Pawn is null); tick++) Tick();
    for (int i = 0; i < 120; i++) Tick();
    Check(play.ServerController is not null && play.LocalController is not null, "Network pair starts");
    var serverPanes = Panes(play.ServerController!.XLevel); Check(serverPanes.Count == 18, "Server loads panes");
    foreach (var pane in serverPanes) pane.eventTakeDamage(1, null, default, default, SDamageType.SDTFlash);
    for (int i = 0; i < 180; i++) Tick();
    var clientPanes = Panes(play.LocalController!.XLevel);
    Check(clientPanes.Count == 18 && clientPanes.All(pane => pane.DrawType == EDrawType.DT_None), "Client observes shattered generated glass");
    foreach (var pane in clientPanes)
    {
        var clientColliders = new List<int>(); var collision = play.Client.Map!.Physics!.Collision;
        collision.GetActorColliders(pane,clientColliders);
        Check(clientColliders.Count > 0 && clientColliders.All(i => !collision.GetCollider(i).Enabled), "Client clears shattered pane collision");
    }
    Console.WriteLine("PASS host/client shattered state replication");
}
if (args.Contains("--render"))
{
    string exe = GameExe.Find() ?? throw new Exception("No OpenSCCT executable is available for the render check");
    var bookmark = new Bookmark { Name = "Window gallery", Location = new Vec3(-1100, 0, 440), Rotation = Rot.Zero };
    var recipe = PictureRecipe.OfCamera(bookmark.Name);
    var shot = new CameraShot("banner", recipe, bookmark, 1280, 720, MapShots.Key(recipe, bookmark, 1280, 720));
    var rendered = await MapShots.RenderAsync(gallery, save, Path.Combine(root, "WindowTest"), [shot], exe, true, Console.WriteLine);
    Check(rendered.Count == 1 && rendered[0].Ok, "In-game sample render: " + string.Join(';', rendered.Select(s => s.Error)));
    File.Copy(rendered[0].File!, Path.Combine(root, "gallery-game.png"), true);
    Console.WriteLine("PASS in-game render: " + Path.Combine(root, "gallery-game.png"));
}
Console.WriteLine($"PASS {checks} window checks. Sample: {save}");
static List<SVitre> Panes(Level level) { var result = new List<SVitre>(); foreach (var pane in level.AllActors<SVitre>()) result.Add(pane); return result; }
sealed class WindowClock : IClock { public long NowMicroseconds { get; set; } }
