using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Session;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Game.Objectives;
using OpenScct.Game.SBase;
using OpenScct.Physics.Collision;
using Actor = OpenScct.Editor.Core.Model.Actor;

internal static class DoorAssemblyChecks
{
    private static SvJsonObject Params(params (string, SvJsonNode?)[] pairs) => ApiJson.Obj(pairs);
    private static void Place(EditorSession s, string name, SvJsonObject? parameters = null, Vec3 at = default)
    {
        var result = ChangeApplier.Apply(s, ApiJson.Obj(("ops", ApiJson.Arr([ApiJson.Obj(("op", ApiJson.Str("prefab.place")), ("name", ApiJson.Str(name)), ("at", ApiJson.Vec(at)), ("params", parameters))]))), writeFiles: false);
        if (!result.Ok) throw new Exception(ApiJson.Write(result.Json));
    }
    private static List<T> Actors<T>(AnalysisMap a) where T : OpenScct.Core.Engine.Actor
    { var list = new List<T>(); foreach (var actor in a.World.Level.AllActors<T>()) list.Add(actor); return list; }
    private static MapProject Room(string id)
    {
        var p = MapProject.CreateDefault(id, id); Starter.AddStarterRoom(p);
        p.Brushes[0].Shape = Box.Shape(new(4096,4096,768)); p.Brushes[0].Location = new(0,0,384); return p;
    }
    public static void Run(Action<bool, string> check, string output)
    {
        foreach (string name in new[] { "automatic_door", "hackable_door" }) foreach (string movement in DoorDesign.Motions)
        {
            var s = new EditorSession(Room("SingleAccessMotion")); Place(s,name,Params(("movement",ApiJson.Str(movement))));
            check(s.Project.Actors.Single(a => a.Door is not null).Door!.Motion==movement,"Access single-door prefab keeps the chosen movement");
        }
        foreach (string name in new[] { "automatic_door", "double_hinged_door", "double_sliding_door" })
        foreach (string front in new[] { "none", "spies", "mercs", "both" }) foreach (string back in new[] { "none", "spies", "mercs", "both" })
        {
            var s = new EditorSession(Room("AccessMatrix")); Place(s,name,Params(("frontAccess",ApiJson.Str(front)),("backAccess",ApiJson.Str(back))));
            var sensors = s.Project.Actors.Where(a => a.ShortClass == "Trigger").ToList();
            check(sensors.Count == (front == "none" ? 0 : 1) + (back == "none" ? 0 : 1), name+" independently omits disabled sides");
            foreach (var sensor in sensors)
            {
                var bounds = MapObjects.ActorBounds(sensor); bool positive = sensor.Location.X > 0;
                check(positive ? bounds.Item1.X >= 64 : bounds.Item2.X <= -64,"Side sensor cannot reach through the doorway to the opposite side");
                string cls = ((sensor.Props["ClassProximityType"] as SvJsonObject)?["$class"] as SvJsonString)?.Value ?? "";
                string expected = (positive ? front : back) switch { "spies" => "SBase.SPawnAttaque", "mercs" => "SBase.SPAWNDEFENSE", _ => "SBase.SBasePawn" };
                check(string.Equals(cls,expected,StringComparison.OrdinalIgnoreCase),name+" correct team on its own side");
            }
        }
        foreach (string name in new[] { "double_hinged_door", "double_sliding_door" })
        {
            var p = Room("DoubleRuntime"); var s = new EditorSession(p);
            var rules = Params(("frontAccess",ApiJson.Str("spies")),("backAccess",ApiJson.Str("mercs")));
            Place(s,name,rules); Place(s,name,rules,new(0,1024,0));
            check(p.Actors.Count(a => a.Door is not null)==4,"Two pairs have four editable leaves");
            var loaded = AnalysisMap.Build(p,null); OpenScct.Game.Devices.DeviceNatives.Install(loaded.World.Level,loaded.Info.Physics!.Driver);
            var triggers = Actors<Trigger>(loaded); var leaves = Actors<Mover>(loaded);
            var front = triggers.Single(t => t.Location.X>0 && Math.Abs(t.Location.Y)<1); var back = triggers.Single(t => t.Location.X<0 && Math.Abs(t.Location.Y)<1);
            var pair = leaves.Where(a => a.Tag==front.Event).ToList(); var other = leaves.Where(a => a.Tag!=front.Event).ToList();
            check(pair.Count==2 && other.Count==2 && back.Event==front.Event,"Both sides link only to their own pair");
            var spy = loaded.World.Level.SpawnActor(new SPawnAttaque { Health=100, bCollideActors=true, bBlockActors=false, bBlockPlayers=false },new FVector(800,0,112),default)!;
            var merc = loaded.World.Level.SpawnActor(new SPAWNDEFENSE { Health=100, bCollideActors=true, bBlockActors=false, bBlockPlayers=false },new FVector(800,0,112),default)!;
            spy.SetPhysics(EPhysics.PHYS_None); merc.SetPhysics(EPhysics.PHYS_None);
            check(front.IsRelevant(spy) && !front.IsRelevant(merc) && !back.IsRelevant(spy) && back.IsRelevant(merc),"Runtime team filters distinguish spies and mercs on both sides");
            spy.Location=new(120,0,112); check(loaded.Collision.IsOverlapping(spy,front) && !loaded.Collision.IsOverlapping(spy,back),"Actual brush trigger overlaps only the front side");
            spy.Location=new(-120,0,112); check(!loaded.Collision.IsOverlapping(spy,front) && loaded.Collision.IsOverlapping(spy,back),"Actual brush trigger overlaps only the back side"); spy.Location=new(800,0,112);
            front.eventTouch(merc); back.eventTouch(spy); loaded.World.Step(5); check(pair.All(a => a.KeyNum==0),"Disallowed teams cannot open the pair");
            front.eventTouch(spy); loaded.World.Step(90); check(pair.All(a => a.KeyNum==1 && !a.bInterpolating),"Both leaves open together");
            check(other.All(a => a.KeyNum==0),"Another pair is unaffected");
            back.eventTouch(merc); front.eventUnTouch(spy); loaded.World.Step(90); check(pair.All(a => a.KeyNum==1),"An allowed player on the other side keeps both leaves open");
            back.eventUnTouch(merc); loaded.World.Step(90); check(pair.All(a => a.KeyNum==0 && !a.bInterpolating),"Both leaves close after the last allowed player leaves");
        }
        foreach (string name in new[] { "hackable_door", "double_hinged_door", "double_sliding_door" })
        {
            var p=Room("HackDoor"); var s=new EditorSession(p);
            Place(s,name,Params(("hackPanel",ApiJson.Bool(true)),("hackTime",ApiJson.Num(7)),("frontAccess",ApiJson.Str("none")),("backAccess",ApiJson.Str("none"))));
            var panel=p.Actors.Single(a => a.ShortClass=="SInterrupteur"); var targets=DoorAccess.Targets(p,panel); int count=name=="hackable_door"?1:2;
            check(targets.Count==count && p.Actors.All(a => a.ShortClass!="Trigger"),"Hack-only assembly has no automatic bypass");
            Vec3 moved=panel.Location+new Vec3(200,180,0);
            s.Execute(ReplaceObjectsCommand.Edit(p,[ObjectRef.Actor(panel.Id)],"Move panel",actor:a=>a.Location=moved));
            s.Execute(DoorAccess.HackTime(p,panel.Id,9)); panel=p.FindActor(panel.Id)!;
            check(panel.Location==moved && DoorAccess.Targets(p,panel).Select(a=>a.Id).SequenceEqual(targets.Select(a=>a.Id)),"Moving the panel and changing hack time preserve its door links");
            s.Execute(DoorCommands.Material(p,targets[0].Id,"Editor.Kit.Metal"));
            check(DoorAccess.Targets(p,panel).All(a=>a.Door!.Material=="Editor.Kit.Metal"),"A material drop paints all linked leaves in one instance");
            check(AssetEdits.InUse(p).GetValueOrDefault("Editor.Kit.Metal")==count && AssetEdits.Usages(p,"Editor.Kit.Metal").Count==count,"Door finishes appear in the asset browser's usages and in-use filter");
            s.Commands.Undo(); check(DoorAccess.Targets(p,panel).All(a=>a.Door!.Material==""),"Undo restores every leaf finish"); s.Commands.Redo();
            string folder=Path.Combine(output,name+".osmap"); ProjectStore.Save(p,folder); var reopened=ProjectStore.Load(folder);
            check(reopened.FindActor(panel.Id)!.Location==moved && reopened.FindActor(panel.Id)!.Props.GetInt("iHackingTime")==9 && DoorAccess.Targets(reopened,reopened.FindActor(panel.Id)!).Count==count,"Save/reopen keeps moved hack panel, timing and links");
            var loaded=AnalysisMap.Build(reopened,folder); OpenScct.Game.Devices.DeviceNatives.Install(loaded.World.Level,loaded.Info.Physics!.Driver);
            var sw=Actors<SInterrupteur>(loaded).Single(); var entry=sw.TriggerInteraction!; var leaves=Actors<Mover>(loaded);
            check(sw.iHackingTime==9 && entry.bHackable && leaves.All(a=>a.KeyNum==0),"Runtime starts locked with the configured hack duration");
            var pawn=loaded.World.Level.SpawnActor(new SPawnAttaque { Health=100 },sw.Location+new FVector(40,0,0),default)!; pawn.SetPhysics(EPhysics.PHYS_None);
            var pc=loaded.World.Level.Spawn<SPlayerController>()!; pc.Pawn=pawn; pawn.Controller=pc;
            entry.InstigatorTempo=pc; entry.GotoState("s_Hacking"); SwitchHackRules.UseBeginState(pc);
            check(pc.TimerRate==9,"Hack timer uses the moved panel's configured seconds");
            entry.eventPostInteract(pc,0); loaded.World.Step(5); check(leaves.All(a=>a.KeyNum==0),"Cancelled hack does not open any leaf");
            entry.InstigatorTempo=pc; entry.GotoState("s_Hacking"); entry.eventPostInteract(pc,1); loaded.World.Step(90);
            check(leaves.All(a=>a.KeyNum==1),"Successful hack opens all linked leaves");
        }
        var gallery=Room("AccessDoorGallery"); gallery.LevelInfo.Set("AmbientBrightness",SvJsonNode.Integer(190)); var gs=new EditorSession(gallery);
        Place(gs,"double_hinged_door",at:new(0,-380,0)); Place(gs,"double_sliding_door",at:new(0,380,0));
        gallery.Workspace.CameraLocation=new(-650,-650,350); gallery.Workspace.CameraRotation=new(-1500,7000,0);
        string galleryFolder=Path.Combine(output,"AccessDoorGallery.osmap"); ProjectStore.Save(gallery,galleryFolder);
        var assets=ProjectAssets.For(galleryFolder); var manifest=assets.Edit(); manifest["materials"] = new JsonObject { ["MyLevel.Materials.DoorPaint"] = new JsonObject { ["flags"] = new JsonObject { ["texture"]="textures/door-paint.png" }, ["uSize"]=32,["vSize"]=32 } };
        Directory.CreateDirectory(Path.Combine(assets.Root,"textures")); File.WriteAllBytes(Path.Combine(assets.Root,"textures","door-paint.png"),Png.EncodeRgba(1,1,[44,159,117,255])); ProjectAssets.Save(galleryFolder,manifest);
        var paint=Room("DoorMaterialDrop"); var leaf=DoorCommands.Create(paint,new DoorDesign { Width=256, Framed=true },new(0,0,112)); paint.Actors.Add(leaf);
        paint.Workspace.CameraLocation=new(-400,0,112); paint.Workspace.CameraRotation=Rot.Zero;
        string paintFolder=Path.Combine(output,"DoorMaterialDrop.osmap"); ProjectStore.Save(paint,paintFolder); ProjectAssets.CopyFolder(galleryFolder,paintFolder);
        Console.WriteLine("PASS double-door synchronization, side/team matrix, hack gating, moved links, materials and persistence");
    }
}
