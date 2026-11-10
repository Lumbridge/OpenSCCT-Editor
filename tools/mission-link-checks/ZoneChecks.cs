using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Content.Loading;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Validation;
using OpenScct.Game.SBase;
using Actor = OpenScct.Editor.Core.Model.Actor;

internal static class ZoneChecks
{
    public static void Run(Action<bool, string> check, string root, string? content)
    {
        var session = EditorSession.New("Zones", "Sequential mission zones");
        MapProject p = session.Project;
        Starter.AddStarterRoom(p);
        var story = new Actor("Story", "SBase.SMission");
        story.Props.Set("ObjectiveName", SvJsonNode.String("Infiltrate the facility"));
        story.Props.Set("GameMode", SvJsonNode.Integer(5));
        story.Props.Set("bChained", SvJsonNode.Bool(false));
        story.Props.Set("Event", SvJsonNode.String("KeepZoneEvent"));
        p.Actors.Add(story);
        session.Execute(MissionLinks.CreateZone(p, story.Id, "ZoneA", "Archives", 2, sequential: true));
        Actor zone = p.FindActor("ZoneA")!;
        check(MissionLinks.Contains(p.FindActor(story.Id)!, "Objectives", zone.Id)
            && zone.Props.GetInt("GameMode") == 0 && !zone.Props.GetBool("bChained")
            && zone.Props.GetInt("MinimumObjectives") == 2, "Zone creation links a parent-activated, any-order, 2-objective zone");
        check(p.FindActor(story.Id)!.Props.GetBool("bChained"), "Sequential zones explicitly chain their parent");
        session.Commands.Undo();
        check(p.FindActor("ZoneA") is null && !p.FindActor(story.Id)!.Props.GetBool("bChained")
            && MissionTree.Refs(p.FindActor(story.Id)!.Props["Objectives"]).Count == 0, "One undo removes zone and restores parent's previous sequencing and links");
        session.Commands.Redo();
        check(p.FindActor("ZoneA") is not null && p.FindActor(story.Id)!.Props.GetString("Event") == "KeepZoneEvent", "Redo restores the zone without changing event wiring");
        session.Execute(MissionLinks.CreateZone(p, story.Id, "ZoneB", "Control room", 1, sequential: true));
        for (int i = 0; i < 5; i++)
        {
            var objective = new Actor($"Task{i}", "SBase.SObjective");
            objective.Props.Set("ObjectiveName", SvJsonNode.String($"Terminal {i + 1}"));
            var device = new Actor($"Computer{i}", "SBase.SComputerObjectiveTrigger") { Location = new Vec3(-192 + i * 96, 0, 64) };
            p.Actors.Add(device);
            session.Execute(MissionLinks.CreateObjective(p, i < 3 ? "ZoneA" : "ZoneB", objective));
            session.Execute(MissionLinks.Add(p, objective.Id, [device.Id])!);
        }
        session.Execute(MissionLinks.Move(p, story.Id, "ZoneB", -1)!);
        check(MissionTree.Refs(p.FindActor(story.Id)!.Props["Objectives"]).SequenceEqual(["ZoneB", "ZoneA"]), "Zones reorder as units with all their objectives");
        session.Commands.Undo();
        check(MissionTree.Refs(p.FindActor(story.Id)!.Props["Objectives"]).SequenceEqual(["ZoneA", "ZoneB"]), "Undo restores zone unlock order");

        p.FindActor("ZoneB")!.Props.Set("GameMode", SvJsonNode.Integer(5));
        check(new MapChecker().Check(p, new CheckOptions()).Any(f => f.Code == "mission.zone-game-mode"), "Map Check warns when a nested zone can start independently of its parent");
        p.FindActor("ZoneB")!.Props.Set("GameMode", SvJsonNode.Integer(0));
        string folder = Path.Combine(root, "Zones.osmap");
        ProjectStore.Save(p, folder);
        MapProject reopened = ProjectStore.Load(folder);
        check(MissionTree.Build(reopened).Missions.Count == 3 && MissionTree.Refs(reopened.FindActor("ZoneA")!.Props["Objectives"]).Count == 3,
            "Nested zones, thresholds and relationships survive save/reopen");
        var built = new MapCompiler().Compile(reopened, Path.Combine(root, "zones-compiled"),
            new CompileOptions { ContentRoot = content, ProjectFolder = folder, FastLighting = true });
        check(built.Ok, "Sequential zone map compiles with strict game validation");
        var registry = ActorClassRegistry.Create(CorePackage.Info, EnginePackage.Info, SBasePackage.Info);
        var loaded = new MapLoader(registry).Load(new World(1), SvMap.Load(built.OutputFolder));
        var runtime = (SMission)loaded.FindActor(story.Id)!;
        var devices = Enumerable.Range(0, 5).Select(i => (SComputerObjectiveTrigger)loaded.FindActor($"Computer{i}")!).ToArray();
        foreach (SMission mission in loaded.Level.AllActors<SMission>()) mission.Desactivate();
        runtime.Reset();
        check(devices.Take(3).All(d => d.bActive) && devices.Skip(3).All(d => !d.bActive), "Only Zone 1 starts active, with all three objectives available together");
        ((SObjective)loaded.FindActor("Task0")!).NotifyTaken(null, devices[0]);
        check(runtime.CurrentObjectiveIndex == 0 && devices.Skip(3).All(d => !d.bActive), "One of three objectives does not unlock Zone 2 when two are needed");
        ((SObjective)loaded.FindActor("Task1")!).NotifyTaken(null, devices[1]);
        check(runtime.CurrentObjectiveIndex == 1 && !runtime.ObjectiveTaken && !devices[2].bActive && devices.Skip(3).All(d => d.bActive),
            "Completing any two of three objectives closes Zone 1 and unlocks Zone 2");
        ((SObjective)loaded.FindActor("Task3")!).NotifyTaken(null, devices[3]);
        check(runtime.ObjectiveTaken && devices.All(d => !d.bActive), "Completing Zone 2's required count completes the parent mission");
    }
}
