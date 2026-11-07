using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Stairs;
using OpenScct.Game.Movement;
using OpenScct.Game.SBase;
using OpenScct.Net;
using OpenScct.Server;
using OpenScctGame.Play.Session;
EditorServices.Discover(typeof(MapProject).Assembly); EditorServices.Classes=ClassCatalog.GeneratedOnly();
string root=Path.Combine(Path.GetTempPath(),"ose-stair-net-"+Guid.NewGuid().ToString("N"));
var p=MapProject.CreateDefault("StairNet","Network stairs");Starter.AddStarterRoom(p);
p.Brushes[0].Shape=Box.Shape(new Vec3(2048,2048,1024));p.Brushes[0].Location=new Vec3(0,0,512);
StairCommands.Place(p,new StairDesign{Overhang=2,EdgeRadius=2},Vec3.Zero).Do(p);
var compiled=new MapCompiler().Compile(p,Path.Combine(root,p.Id),new CompileOptions{FastLighting=true});
if(!compiled.Ok)throw new Exception(string.Join(';',compiled.Findings.Select(f=>f.Message)));
foreach(var side in new[]{PlaySide.Spy,PlaySide.Merc})foreach(int latency in new[]{0,100}){
var clock=new StairClock();using var session=new PlaySession(new PlaySettings{Map="StairNet",MapRoots=[root],Side=side,Rules=PlayRules.PlayLevel,HostMode=PlayHostMode.Inline,SimLink=latency==0?null:SimLinkProfile.Get("100"),Bots=0},clock);
void Tick(){clock.NowMicroseconds+=1_000_000/Sim.Hz;session.Step(clock.NowMicroseconds);session.Input.EndTick();}
for(int tick=0;tick<600 && (session.LocalPawn is null ||session.ServerController?.Pawn is null ||session.LocalController!.IsInState("WaitingForPawn"));tick++)Tick();
for(int i=0;i<60;i++)Tick();
var pc=(SPlayerController)session.LocalController!;var server=(SBasePawn)session.ServerController!.Pawn!;var local=(SBasePawn)session.LocalPawn!;
MovementNatives.FarMoveActor(server,new FVector(-100,0,server.CollisionHeight+2));server.Velocity=default;server.Acceleration=default;server.Rotation=default;session.ServerController.Rotation=default;
var endpoint=((NetMovementBridge)session.ServerController.XLevel.GetService<IMovementNet>()!).EndpointOf(session.ServerController)!;
endpoint.Server!.ReinitPawn();for(int i=0;i<90;i++)Tick();pc.SetViewRotation(default);
session.Input.Set("BT_Up",true);float highest=0;int corrections= ((NetMovementBridge)pc.XLevel.GetService<IMovementNet>()!).EndpointOf(pc)!.Client!.CorrectionsApplied;
for(int tick=0;tick<360 && server.Location.X<460;tick++){Tick();highest=Math.Max(highest,server.Location.Z-server.CollisionHeight);}
session.Input.Set("BT_Up",false);
for(int i=0;i<30;i++)Tick();
if(highest<230 ||server.Location.X<430)throw new Exception($"{side}/{latency} failed to climb: {server.Location}, highest feet={highest}");
if(server.Location.Z-server.CollisionHeight<230)throw new Exception($"{side}/{latency} did not remain supported after stopping");
float error=(local.Location-server.Location).Size();if(error>20)throw new Exception($"{side}/{latency} client/server error {error}");
int added=((NetMovementBridge)pc.XLevel.GetService<IMovementNet>()!).EndpointOf(pc)!.Client!.CorrectionsApplied-corrections;
Console.WriteLine($"PASS {side} latency={latency}: highest feet={highest:0.##}, settled error={error:0.###}, corrections={added}");
}
sealed class StairClock:IClock{public long NowMicroseconds{get;set;}}
