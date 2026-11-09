using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.Movement;
using OpenScct.Game.SBase;
using OpenScct.Net;
using OpenScct.Physics.Collision;
using OpenScct.Server;
using OpenScctGame.Play.Session;

EditorServices.Discover(typeof(MapProject).Assembly); EditorServices.Classes = ClassCatalog.GeneratedOnly();
string root = Path.GetFullPath(args.FirstOrDefault() ?? "local/material-network-checks"); Directory.CreateDirectory(root);
var project = MapProject.CreateDefault("MatNet", "Displaced collision network validation"); Starter.AddStarterRoom(project);
string folder = Path.Combine(root, "MatNet.osmap"); ProjectStore.Save(project, folder);
string height = Path.Combine(root, "Floor_height.png"); File.WriteAllBytes(height, new RgbaImage(1, 1, [255,255,255,255]).EncodePng());
var draft = TextureImporter.Preview([height]).Single(); draft.Material.Displacement = 32; draft.Material.HeightMidpoint = 0; draft.Material.Subdivision = 3; draft.Material.DisplacementCollision = true;
string material = TextureImporter.Import(folder, draft).Path;
var floor = project.Brushes[0].Shape.Faces.OrderBy(f => f.Vertices.Average(v => v.Z)).First(); floor.Material = material;
ProjectStore.Save(project, folder);
string mapFolder = Path.Combine(root, project.Id);
var build = new MapCompiler().Compile(project, mapFolder, new CompileOptions { ProjectFolder = folder, FastLighting = true });
if (!build.Ok) throw new Exception(string.Join(';', build.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
var second = new MapCompiler().Compile(project, Path.Combine(root, "repeat", project.Id), new CompileOptions { ProjectFolder = folder, FastLighting = true });
if (!second.Ok || !File.ReadAllBytes(Path.Combine(mapFolder,"sim/collision.bin")).SequenceEqual(File.ReadAllBytes(Path.Combine(root,"repeat",project.Id,"sim/collision.bin")))) throw new Exception("Physical displacement compilation is not deterministic.");
Console.WriteLine("PASS repeated compilation of physical displacement produces identical collision.bin");
foreach (var side in new[] { PlaySide.Spy, PlaySide.Merc }) foreach (int latency in new[] { 0, 100 })
{
    var clock = new MaterialClock();
    using var session = new PlaySession(new PlaySettings { Map = project.Id, MapRoots = [root], Side = side, Rules = PlayRules.PlayLevel, HostMode = PlayHostMode.Inline, SimLink = latency == 0 ? null : SimLinkProfile.Get("100"), Bots = 0 }, clock);
    void Tick() { clock.NowMicroseconds += 1_000_000 / Sim.Hz; session.Step(clock.NowMicroseconds); session.Input.EndTick(); }
    for (int tick = 0; tick < 600 && (session.LocalPawn is null || session.ServerController?.Pawn is null || session.LocalController!.IsInState("WaitingForPawn")); tick++) Tick();
    for (int i = 0; i < 60; i++) Tick();
    var pc = (SPlayerController)session.LocalController!; var server = (SBasePawn)session.ServerController!.Pawn!; var local = (SBasePawn)session.LocalPawn!;
    foreach (FVector extent in new[] { default(FVector), new FVector(4,4,4) })
    {
        var sc = ((OpenScct.Physics.Movement.PhysicsDriver)session.Host.World.Level.PhysicsDriver!).Collision;
        var cc = ((OpenScct.Physics.Movement.PhysicsDriver)session.Client.World.Level.PhysicsDriver!).Collision;
        bool sh = sc.LineCheck(out var a, null, new(0,0,-100), new(0,0,150), extent, ETraceFlags.TRACE_World);
        bool ch = cc.LineCheck(out var b, null, new(0,0,-100), new(0,0,150), extent, ETraceFlags.TRACE_World);
        if (!sh || !ch || Math.Abs(a.Location.Z - b.Location.Z) > .01 || a.Location.Z < 31.9) throw new Exception($"{side}/{latency}: server/client bullet or camera trace differs, or flat collision remains: {a.Location}, {b.Location}");
    }
    MovementNatives.FarMoveActor(server, new FVector(-350,0,server.CollisionHeight + 96)); server.Velocity = default; server.Acceleration = default; server.Rotation = default; session.ServerController.Rotation = default;
    var endpoint = ((NetMovementBridge)session.ServerController.XLevel.GetService<IMovementNet>()!).EndpointOf(session.ServerController)!;
    endpoint.Server!.ReinitPawn(); for (int i = 0; i < 90; i++) Tick(); pc.SetViewRotation(default);
    session.Input.Set("BT_Up",true); float highest = 0;
    for (int tick = 0; tick < 360 && server.Location.X < 100; tick++) { Tick(); highest = Math.Max(highest, server.Location.Z - server.CollisionHeight); }
    session.Input.Set("BT_Up",false); for (int i = 0; i < 45; i++) Tick();
    float error = (local.Location - server.Location).Size();
    if (highest < 28 || server.Location.X < 95 || server.Location.Z - server.CollisionHeight < 28 || error > 20) throw new Exception($"{side}/{latency}: physical relief movement failed; feet={server.Location.Z-server.CollisionHeight}, x={server.Location.X}, client error={error}");
    Console.WriteLine($"PASS {side} latency={latency}ms: movement supported on displaced floor; bullet/camera traces agree; client error={error:0.###}");
}
sealed class MaterialClock : IClock { public long NowMicroseconds { get; set; } }
