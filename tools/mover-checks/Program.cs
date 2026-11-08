using System.Text.Json.Nodes;
using OpenScct.Content;
using OpenScct.Content.Loading;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Net;
using OpenScctGame.Play.Session;
using Actor = OpenScct.Editor.Core.Model.Actor;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
EditorServices.Discover(typeof(MapProject).Assembly); EditorServices.Classes = ClassCatalog.GeneratedOnly();
string root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("local", "mover-checks")); Directory.CreateDirectory(root);
var p = MapProject.CreateDefault("MoveTest", "Mover workshop"); Starter.AddStarterRoom(p);
p.Brushes[0].Shape = Box.Shape(new Vec3(2048, 2048, 1024)); p.Brushes[0].Location = new Vec3(0, 0, 512);
foreach (var template in Enum.GetValues<MoverTemplate>())
{
    Actor a = MoverAuthoring.Create(p, template, Vec3.Zero);
    var setup = MoverSetup.Read(a);
    Check(a.Shape?.Faces.Count == 6 && setup.Keys.Count == 2 && setup.Keys[0] != setup.Keys[1], template + " has appearance and movement");
}
// Conversion retains a scaled/rotated brush's surface positions and all its references through undo/redo.
var brush = Box.Make("MovingBrush", new Vec3(32, 128, 224), CsgOper.Add, Starter.DefaultMaterial);
brush.Location = new Vec3(320, 0, 112); brush.Rotation = new Rot(0, 8192, 0); brush.MainScale = new Vec3(1, 2, 1);
p.Brushes.Add(brush); var before = brush.WorldFaces().SelectMany(f => f.Vertices).ToArray();
var convert = MoverAuthoring.Convert(p, ObjectRef.Brush(brush.Id), MoverTemplate.Slide); convert.Do(p);
Check(p.FindBrush(brush.Id) is null && p.FindActor(brush.Id) is { }, "Conversion replaces the brush");
var moved = p.FindActor(brush.Id)!;
Check(before.SequenceEqual(moved.Shape!.Faces.SelectMany(f => f.Vertices.Select(moved.LocalToWorld))), "Conversion preserves geometry in world space");
convert.Undo(p); Check(p.FindBrush(brush.Id) == brush && p.FindActor(brush.Id) is null, "Conversion undoes exactly");
convert.Do(p); convert.Undo(p);
var meshSource = new Actor("MeshSource", "Engine.StaticMeshActor") { DrawScale = 2, Rotation = new Rot(0, 8192, 0) };
meshSource.Props["StaticMesh"] = PropValues.AssetRef("Test.Mesh"); p.Actors.Add(meshSource);
var meshConversion = MoverAuthoring.Convert(p, ObjectRef.Actor(meshSource.Id), MoverTemplate.Slide); meshConversion.Do(p);
Check(p.FindActor(meshSource.Id) is { DrawScale: 2 } meshMover && meshMover.Class == "Engine.Mover" &&
    PropValues.Num((OpenScct.Content.Json.SvJsonNumber)meshMover.Props["DrawType"]!) == (int)EDrawType.DT_StaticMesh &&
    PropValues.AssetPath(meshMover.Props["StaticMesh"]) == "Test.Mesh", "Mesh conversion keeps its appearance and sets the mover draw type");
meshConversion.Undo(p); p.Actors.RemoveAll(a => a.Id == meshSource.Id);

Actor platform = MoverAuthoring.Create(p, MoverTemplate.Platform, new Vec3(0, 0, 224)); p.Actors.Add(platform);
string id = platform.Id;
var keys = Enumerable.Range(0, 300).Select(i => new MoverKey(new Vec3(i % 16 * 8, i / 16 * 8, 0), Rot.Zero)).ToArray();
MoverEdits.SetKeys(p, id, keys).Do(p); MoverEdits.SetState(p, id, "TriggerToggle").Do(p); MoverEdits.SetTime(p, id, "MoveTime", .035).Do(p);
MoverEdits.SetLegTime(p, id, 299, .1).Do(p);
MoverEdits.EditKey(p, id, 280).Do(p);
var edit = ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], "Place stop", actor: a => a.Location += new Vec3(0, 0, 2)); edit.Do(p);
Check(MoverSetup.Read(p.FindActor(id)!).Keys[280].Offset.Z == 2, "Viewport editing writes a stop beyond byte range"); edit.Undo(p);
MoverEdits.EditKey(p, id, 0).Do(p);
var moveFirst = ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], "Place first stop", actor: a => a.Location += new Vec3(0, 0, 2)); moveFirst.Do(p);
Check(MoverSetup.Read(p.FindActor(id)!).Keys[0].Offset.Z == 2, "Starting stop can be edited independently of the whole path"); moveFirst.Undo(p);
var duplicate = MoverEdits.InsertKey(p, id, 280); duplicate.Do(p); Check(MoverSetup.Read(p.FindActor(id)!).Keys.Count == 301, "Insert beyond original arrays"); duplicate.Undo(p);
var reorder = MoverEdits.MoveKey(p, id, 299, -1); reorder.Do(p);
Check(p.FindActor(id)!.Legs!.Keys[298].Time == .1f, "Timing follows reordered stops"); reorder.Undo(p);
var remove = MoverEdits.DeleteKey(p, id, 299); remove.Do(p); remove.Undo(p); Check(p.FindActor(id)!.Legs!.Keys[299].Time == .1f, "Delete undo restores timing");
MoverEdits.ShowKey(p, id, 0).Do(p);
var addSwitch = MoverAuthoring.AddSwitch(p, id, new Vec3(128, 0, 96)); addSwitch.Do(p);
Check(p.Actors.Single(a => a.ShortClass == "SInterrupteur").Props.GetString("Event") == p.FindActor(id)!.Tag, "One-click switch is wired");
addSwitch.Undo(p); Check(!p.Actors.Any(a => a.ShortClass == "SInterrupteur"), "Switch placement and wiring undo together");
string project = Path.Combine(root, "MoverWorkshop.osmap"); ProjectStore.Save(p, project); p = ProjectStore.Load(project);
Check(MoverSetup.Read(p.FindActor(id)!).Keys.Count == 300 && p.FindActor(id)!.Legs!.Keys.Count == 300, "Save and reopen keep all stops and timing");
foreach (var exporter in new IMapExporter[] { new OriginalExporter(), new SdcExporter(), new T3dExporter() })
{
    string destination = Path.Combine(root, "sentinel-" + exporter.Name + ".txt"); File.WriteAllText(destination, "keep");
    Check(!exporter.Export(p, destination, new ExportOptions()).Ok && File.ReadAllText(destination) == "keep", "Legacy " + exporter.Name + " refuses without touching its destination");
}
string maps = Path.Combine(root, "maps"); string folder = Path.Combine(maps, p.Id);
MoverEdits.EditKey(p, id, 280).Do(p);
Vec3 editingPose = p.FindActor(id)!.Location;
var result = new MapCompiler().Compile(p, folder, new CompileOptions { FastLighting = true });
Check(result.Ok, "Compile: " + string.Join(';', result.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
Check(p.FindActor(id)!.Location == editingPose && p.FindActor(id)!.Editor.MoverKey == 280, "Build preserves the editor's chosen stop");
var map = SvMap.Load(folder); Check(map.MoverPaths![id].Keys.Count == 300, "Native path compiled intact");
var clientWorld = new World(1, ENetMode.NM_Client);
var clientLoaded = new MapLoader().Load(clientWorld, map);
var clientStart = clientLoaded.PlacedActors().Select(a => a.Item1).OfType<Mover>().Single();
Check(clientStart.Location == clientStart.BasePos + clientStart.KeyPosition(0), "Client starts at the round pose while the editor shows a later stop");
var instances = OpenScct.Editor.Core.Compile.Output.Glb.Load(File.ReadAllBytes(Path.Combine(folder, "render", "world.glb"))).Json["extras"]!["svmap"]!["instances"]!;
Check(instances.ToJsonString().Contains("mover"), "Brush mover has a rendered instance");
var imported = new SvMapImporter().Import(folder, new ImportOptions()).Project!;
Check(MoverSetup.Read(imported.FindActor(id)!).Keys.Count == 300 && imported.FindActor(id)!.Legs!.Keys[299].Time == .1f, "SVMap import keeps native paths");
// Start positions above 255 are applied before BeginPlay even through the self-contained map loader.
var high = MapProject.CreateDefault("HighStop", "High start"); Starter.AddStarterRoom(high);
Actor highMover = MoverAuthoring.Create(high, MoverTemplate.Platform, new Vec3(0, 0, 224)); high.Actors.Add(highMover);
MoverEdits.SetKeys(high, highMover.Id, keys, startKey: 280).Do(high);
var highResult = new MapCompiler().Compile(high, Path.Combine(maps, high.Id), new CompileOptions { FastLighting = true }); Check(highResult.Ok, "High start compiles");
var world = new World(1); var loaded = new MapLoader().Load(world, SvMap.Load(highResult.OutputFolder));
var live = loaded.PlacedActors().Select(a => a.Item1).OfType<Mover>().Single();
Check(live.CurrentKey == 280 && live.Location == live.BasePos + live.KeyPosition(280), "Native start applied before BeginPlay");

foreach (int latency in new[] { 0, 100 })
{
    var clock = new MoverClock(); using var session = new PlaySession(new PlaySettings { Map = p.Id, MapRoots = [maps], Side = PlaySide.Spy,
        Rules = PlayRules.PlayLevel, HostMode = PlayHostMode.Inline, Bots = 0, SimLink = latency == 0 ? null : SimLinkProfile.Get("100") }, clock);
    void Tick(int count = 1) { for (int i = 0; i < count; i++) { clock.NowMicroseconds += 1_000_000 / Sim.Hz; session.Step(clock.NowMicroseconds); session.Input.EndTick(); } }
    for (int i = 0; i < 600 && session.LocalPawn is null; i++) Tick(); Tick(60);
    Check(session.LocalPawn is not null, "Client joined");
    Mover host = session.Host.Map.MapActors.OfType<Mover>().Single(m => m.Path is not null);
    Mover client = session.Client.Map!.MapActors.OfType<Mover>().Single(m => m.Path is not null);
    host.eventTrigger(null, null); bool crossedByte = false;
    for (int i = 0; i < 900; i++) { Tick(); crossedByte |= client.CurrentKey > 255; }
    Check(crossedByte && host.CurrentKey == 299 && client.CurrentKey == 299 && !host.bInterpolating && !client.bInterpolating,
        $"{latency}ms reaches final stop: {host.CurrentKey}/{client.CurrentKey}");
    Check((host.Location - client.Location).Size() < 3, "Clients settle at the same pose");
    host.eventTrigger(null, null); Tick(900);
    Check(host.CurrentKey == 0 && client.CurrentKey == 0 && !client.bInterpolating, "Both close across all native stops");
    Console.WriteLine($"PASS 300-stop host/client open and return, latency={latency}ms");
}
Console.WriteLine($"PASS {checks} mover workflow checks. Evidence: {root}");
sealed class MoverClock : IClock { public long NowMicroseconds { get; set; } }
