using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Stairs;
int checks=0;void Check(bool ok,string msg){if(!ok)throw new Exception(msg);checks++;}
EditorServices.Discover(typeof(MapProject).Assembly);EditorServices.Classes=ClassCatalog.GeneratedOnly();
string root=Path.Combine(Path.GetTempPath(),"ose-stairs-"+Guid.NewGuid().ToString("N"));
foreach(string layout in StairDesign.Layouts)foreach(bool rounded in new[]{false,true}){
var p=MapProject.CreateDefault("StairTest","Stair test");Starter.AddStarterRoom(p);
var s=new StairDesign{Layout=layout,TurnDegrees=layout=="spiral"?360:90,Overhang=rounded?2:0,EdgeRadius=rounded?2:0};
var cmd=StairCommands.Place(p,s,Vec3.Zero);cmd.Do(p);var b=p.Brushes.Last();var g=StairGeometry.Generate(b);
Check(b.Shape.Faces.Count>0,$"{layout} preview exists");Check(g.Solids.All(x=>x.Shape.Faces.All(f=>f.StairRole is not null)),"Semantic roles");
var top=g.Top;var material=StairCommands.Material(p,b.Id,"tread","Test.Tread");material.Do(p);b=p.FindBrush(b.Id)!;
Check(StairGeometry.Generate(b).Top==top,"Material edit keeps endpoints");Check(b.Shape.Faces.Where(f=>f.StairRole=="tread").All(f=>f.Material=="Test.Tread"),"All treads changed");material.Undo(p);
var edit=StairCommands.Edit(p,b.Id,d=>{d.Width+=32;d.LandingLength+=32;});edit.Do(p);edit.Undo(p);Check(p.FindBrush(b.Id)!.Stair!.Width==192,"Undo dimensions");
string save=Path.Combine(root,layout+rounded+".osmap");ProjectStore.Save(p,save);var loaded=ProjectStore.Load(save);Check(loaded.Brushes.Last().Stair!.Layout==layout,"Save/reopen staircase");
var result=new MapCompiler().Compile(loaded,Path.Combine(root,layout+rounded,"StairTest"),new CompileOptions{FastLighting=true});
Check(result.Ok,$"Compile {layout}: {string.Join(';',result.Findings.Select(f=>f.Message))}");
Console.WriteLine($"PASS {layout} rounded={rounded} faces={b.Shape.Faces.Count}");
}

// Authoring commands and public object kind.
var apiProject=MapProject.CreateDefault("ApiStairs","API stairs");Starter.AddStarterRoom(apiProject);
var session=new OpenScct.Editor.Core.Session.EditorSession(apiProject);
var api=new OpenScct.Editor.Core.Scripting.ApiHost(session);
var request=OpenScct.Editor.Core.Scripting.ApiJson.Parse("""{"format":"osmap.changes/1","ops":[{"op":"stair","id":"Editable","at":[0,0,0],"set":{"layout":"straight","overhang":2,"edgeRadius":2}}]}""","test") as OpenScct.Content.Json.SvJsonObject;
// Use the actual change-document format constant.

var reply=api.Call("apply",request);Check(!reply.IsError,"Stair API creates object: "+reply.ToString());
Check(apiProject.Find("Editable")!.Value.Kind==ObjectKind.Stair,"Stairs have their own object kind");
var duplicate=apiProject.FindBrush("Editable")!.Clone("Copy");apiProject.Brushes.Add(duplicate);
var materialEdit=StairCommands.Material(apiProject,"Copy","riser","Other.Material");materialEdit.Do(apiProject);
Check(apiProject.FindBrush("Editable")!.Stair!.RiserMaterial!="Other.Material","Duplicate material independence");
var raw=ModelJson.BrushFromJson(ModelJson.BrushToJson(duplicate));Check(raw.Stair!.EdgeRadius==2,"Clipboard keeps stair design");
var deletion=new OpenScct.Editor.Core.Commands.DeleteObjectsCommand([ObjectRef.Stair("Copy")]);deletion.Do(apiProject);Check(apiProject.Find("Copy") is null,"Delete whole stair");deletion.Undo(apiProject);Check(apiProject.Find("Copy") is not null,"Undo whole stair deletion");
var split=StairCommands.BreakApart(apiProject,"Copy");split.Do(apiProject);Check(apiProject.Find("Copy") is null,"Break apart removes controller");split.Undo(apiProject);Check(apiProject.FindBrush("Copy")!.Stair is not null,"Undo break apart restores controller");
// Player smoothing is tested through the game's loader and real collision query filters.
var physicsProject=MapProject.CreateDefault("CollisionStairs","Collision stairs");Starter.AddStarterRoom(physicsProject);
StairCommands.Place(physicsProject,new StairDesign(),Vec3.Zero).Do(physicsProject);
var analysis=OpenScct.Editor.Core.Analysis.AnalysisMap.Build(physicsProject,null);
var pawn=new TestPawn{bIsAPawn=true,bCollideActors=true,bBlockActors=true,bBlockPlayers=true};
var start=new OpenScct.Core.FVector(16,0,100);var end=new OpenScct.Core.FVector(16,0,-10);
var flags=OpenScct.Physics.Collision.ETraceFlags.TRACE_AllBlocking;
analysis.Collision.LineCheck(out var shot,null,end,start,default,flags);
analysis.Collision.LineCheck(out var walk,pawn,end,start,new OpenScct.Core.FVector(1,1,1),flags);
Check(shot.Location.Z<20,"Shots follow visible tread");Check(walk.Location.Z>22,"Pawn sweep follows smoothing slope");
analysis.Collision.LineCheck(out var camera,pawn,end,start,new OpenScct.Core.FVector(1,1,1),flags|OpenScct.Physics.Collision.ETraceFlags.TRACE_Camera);
Check(camera.Location.Z<20,"Camera ignores invisible smoothing");
var prop=new TestActor{bCollideActors=true,bBlockActors=true};
analysis.Collision.LineCheck(out var objectHit,prop,end,start,new OpenScct.Core.FVector(1,1,1),flags);
Check(objectHit.Location.Z<20,"Non-pawn extent traces ignore smoothing");

foreach(string kind in new[]{"linearStair","stairs","curvedStair","spiralStair"})
{
    var project=new MapProject();
    var pieces=OpenScct.Editor.Core.Geometry.Builders.PiecePlacer.Create(project,new BuilderSpec(kind),new Vec3(30,40,50),oper:CsgOper.Add);
    project.Brushes.AddRange(pieces);var bounds=OpenScct.Editor.Core.Scripting.MapObjects.BrushBounds(new Brush("temp"){Shape=new BrushShape{Faces=pieces.Where(b=>b.Oper==CsgOper.Add).SelectMany(b=>b.WorldFaces()).ToList()}});
    var convert=StairCommands.Convert(project,pieces.Last().Id);convert.Do(project);
    var upgraded=project.Brushes.Single();var convertedBounds=OpenScct.Editor.Core.Scripting.MapObjects.BrushBounds(upgraded);
    Check((bounds.Min-convertedBounds.Min).Length<.1 && (bounds.Max-convertedBounds.Max).Length<.1,"Upgrade preserves bounds "+kind);
    convert.Undo(project);Check(project.Brushes.Count==pieces.Count,"Undo conversion "+kind);
}

foreach(string layout in new[]{"l","u","spiral"})
{
    var legacy=new MapProject();var spec=new BuilderSpec("stairs");spec.Strings["layout"]=layout;
    legacy.Brushes.AddRange(OpenScct.Editor.Core.Geometry.Builders.PiecePlacer.Create(legacy,spec,Vec3.Zero));
    var oldBounds=legacy.Brushes.Where(b=>b.Oper==CsgOper.Add).SelectMany(b=>b.WorldFaces()).SelectMany(f=>f.Vertices).ToList();
    var convert=StairCommands.Convert(legacy,legacy.Brushes.Last().Id);convert.Do(legacy);
    var upgraded=legacy.Brushes.Single();var newBounds=upgraded.WorldFaces().SelectMany(f=>f.Vertices).ToList();
    Check((oldBounds.Aggregate(Vec3.Min)-newBounds.Aggregate(Vec3.Min)).Length<.1 && (oldBounds.Aggregate(Vec3.Max)-newBounds.Aggregate(Vec3.Max)).Length<.1,"Upgrade blockout layout "+layout);
}
var fitted=StairFit.Between("Fit",new StairDesign(),new Vec3(10,20,0),new Vec3(410,320,256));
Check((StairGeometry.Generate(fitted).Top-new Vec3(410,320,256)).Length<.1,"Connect floors meets top exactly");
var baseStair=apiProject.FindBrush("Editable")!;
var scaled=baseStair.Clone();scaled.MainScale=new Vec3(2,1,1);StairTransforms.Resize(baseStair,scaled);
Check(scaled.Stair!.Run==baseStair.Stair!.Run*2 && scaled.MainScale==Vec3.One,"Scaling regenerates dimensions");

foreach(string layout in StairDesign.Layouts)
foreach(var team in new[]{OpenScct.Game.Movement.PawnTeam.Spy,OpenScct.Game.Movement.PawnTeam.Merc})
foreach(bool crouched in new[]{false,true})
{
    string folder=Path.Combine(root,layout+"True","StairTest");
    var rig=OpenScct.Game.Movement.PawnRig.ForMap(team,folder);
    var design=new StairDesign{Layout=layout,TurnDegrees=layout=="spiral"?360:90,Overhang=2,EdgeRadius=2};
    var route=StairGeometry.Steps(design).Select(t=>(t.A+t.B+t.C+t.D)/4).ToList();
    rig.Place(new OpenScct.Core.FVector(-100,0,rig.Pawn.CollisionHeight+2),default,default,false);
    rig.WaitStill(120);
    if(!crouched)
    {
        var profile=team==OpenScct.Game.Movement.PawnTeam.Spy?OpenScct.Game.AI.NavProfile.Spy:OpenScct.Game.AI.NavProfile.Merc;
        var graph=OpenScct.Game.AI.NavBuilder.Build(rig.Collision!,new OpenScct.Core.FVector(-256,-192,-32),new OpenScct.Core.FVector(768,1024,512),profile);
        var goal=route[^1];int from=graph.NearestNode(new OpenScct.Core.FVector(-100,0,0));
        int to=graph.NearestNode(new OpenScct.Core.FVector((float)goal.X,(float)goal.Y,(float)design.Rise));
        Check(to>=0 && graph.Node(to).Floor.Z>design.Rise-34,"Bot goal is on upper stair floor "+layout);
        Check(graph.FindPath(from,to,new List<OpenScct.Game.AI.NavStep>()),$"{team} bot route reaches {layout} top");
    }
    Check(rig.SetCrouched(crouched), "Set stair traversal stance");
    void Follow(IEnumerable<Vec3> targets)
    {
        foreach(Vec3 target in targets)
        for(int tick=0;tick<180;tick++)
        {
            double dx=target.X-rig.Pawn.Location.X,dy=target.Y-rig.Pawn.Location.Y;
            if(Math.Sqrt(dx*dx+dy*dy)<12)break;
            int yaw=(int)Math.Round(Math.Atan2(dy,dx)*65536/(2*Math.PI));
            rig.Step(new OpenScct.Game.Movement.PlayerInputFrame{BT_Up=1,View=new OpenScct.Core.FRotator(0,yaw,0)});
        }
    }
    Follow(route);
    float feet=rig.Pawn.Location.Z-rig.Pawn.CollisionHeight;
    Check(feet>design.Rise-24,$"{team} walks up {layout}: feet={feet} at {rig.Pawn.Location}");
    Follow(route.AsEnumerable().Reverse().Append(new Vec3(-256,0,0)));
    Check(rig.Pawn.Location.X< -220 && rig.Pawn.Location.Z-rig.Pawn.CollisionHeight<12,$"{team} walks down {layout}: {rig.Pawn.Location}");
    Console.WriteLine($"PASS walking {team} {layout} crouched={crouched}");
}

var selectionDoc=OpenScct.Editor.Core.Interop.T3d.T3dExport.FromProject(apiProject,null,[ObjectRef.Stair("Editable")]);
Check(selectionDoc.Actors.Count>1,"Selection export lowers editable stair parts");
var exportFolder=Path.Combine(root,"original-export");
// T3D remains the lossless authoring interchange; Reloaded Folder now writes native install packages.
Directory.CreateDirectory(exportFolder);
var t3d=Path.Combine(exportFolder,"ApiStairs.t3d");
var exportResult=new OpenScct.Editor.Core.Interop.T3d.T3dExporter().Export(apiProject,t3d,new ExportOptions());
Check(File.Exists(t3d),"T3D export writes stair geometry even with placeholder-material lint findings");
var handoff=OpenScct.Editor.Core.Interop.Original.HandoffFile.ToJson(apiProject,null,(_,_)=>null,null,[],File.ReadAllBytes(t3d));
File.WriteAllBytes(OpenScct.Editor.Core.Interop.Original.HandoffFile.PathFor(t3d),OpenScct.Content.Json.CanonicalJson.ToFileBytes(handoff));
var imported=new OpenScct.Editor.Core.Interop.T3d.T3dImporter().Import(t3d,new ImportOptions());
Check(imported.Project.Brushes.Count(b=>b.Stair is not null)==2,"Original handoff restores both editable stairs without loose copies");

var protectedStair=apiProject.FindBrush("Editable")!;var altered=protectedStair.Clone();altered.Shape.Faces[0].Material="Untracked.Face";
bool refused=false;try{new OpenScct.Editor.Core.Commands.ReplaceObjectsCommand("Raw face edit").Brush(protectedStair,altered);}catch(ArgumentException){refused=true;}
Check(refused,"Generated faces cannot diverge from the authored stair design");

var turned=StairGeometry.Create("Turned",new StairDesign{Layout="l"},Vec3.Zero);var wider=turned.Clone();wider.MainScale=new Vec3(1,2,1);StairTransforms.Resize(turned,wider);
Check(wider.Stair!.LandingLength>=wider.Stair.Width,"Widening an L staircase also keeps its landing wide enough");

foreach(string layout in new[]{"l","u","curved","spiral"})
{
    var design=new StairDesign{Layout=layout,TurnDegrees=layout=="spiral"?360:90};
    var original=StairGeometry.Create("FitOriginal",design,Vec3.Zero);var upper=StairGeometry.Generate(original).Top;
    var fittedLayout=StairFit.Between("FitResult",design,Vec3.Zero,upper);
    Check((StairGeometry.Generate(fittedLayout).Top-upper).Length<.1,"Connect-floor fitting roundtrip "+layout);
}
Console.WriteLine($"PASS {checks} checks; artifacts {root}");

sealed class TestPawn : OpenScct.Core.Engine.Pawn {}
sealed class TestActor : OpenScct.Core.Engine.Actor {}

