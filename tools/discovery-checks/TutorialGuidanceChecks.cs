using OpenScct.Content.Json;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Shell;

internal static class TutorialGuidanceChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (string id in new[] { "laptop", "bomb", "disk" })
        {
            var session = EditorSession.New("GuideChecks", "Guide checks");
            MapProject p = session.Project;
            EditorTutorial lesson = TutorialCatalog.All.Single(t => t.Id == id);
            string type = TutorialMapGuidance.DeviceClass(id)!;
            check(TutorialMapGuidance.Resolve(lesson, 0, p, null).Target.StartsWith("placeable:", StringComparison.Ordinal), id + " empty-map placement");
            PlacedPlaceable placed = PlaceableWiring.Place(p, Placeables.Stock.Single(e => e.Class == type), Vec3.Zero, Rot.Zero, []);
            session.Execute(placed.Command);
            Actor device = p.FindActor(placed.Id)!;
            Actor objective = TutorialMapGuidance.Objective(p, device)!;
            check(objective is not null && MissionLinks.Contains(objective, "Triggers", device.Id), id + " placement creates and links its actual objective");
            int before = p.Actors.Count;
            check(TutorialMapGuidance.Resolve(lesson, 0, p, device).Title == "Use the device already placed", id + " existing device is reused");
            if (id != "disk") check(TutorialMapGuidance.Resolve(lesson, 1, p, device).Title == "Use the existing objective", id + " auto-created objective is reused");
            int missionStep = id == "disk" ? 3 : 2;
            check(TutorialMapGuidance.Resolve(lesson, missionStep, p, device).Target == "setup:new-mission", id + " only requests a mission when none exists");
            var mission = new Actor("MissionGuide", "SBase.SMission");
            mission.Props.Set("GameMode", SvJsonNode.Integer(5));
            p.Actors.Add(mission);
            check(TutorialMapGuidance.Resolve(lesson, missionStep, p, device).Target == "setup:mission", id + " offers an existing mission");
            session.Execute(MissionLinks.Add(p, mission.Id, [objective!.Id])!);
            check(TutorialMapGuidance.Resolve(lesson, missionStep, p, device).Title == "The mission link is already complete", id + " recognises a completed setup");
            check(TutorialMapGuidance.Subject(p, id, [ObjectRef.Actor(objective.Id)])?.Id == device.Id, id + " follows a selected objective to its device");
            check(TutorialMapGuidance.Subject(p, id, [ObjectRef.Actor(mission.Id)], device.Id)?.Id == device.Id, id + " retains the device while inspecting a mission");
            check(p.Actors.Count == before + 1, id + " guidance does not create duplicate actors");
            var second = PlaceableWiring.Place(p, Placeables.Stock.Single(e => e.Class == type), Vec3.One, Rot.Zero, []);
            session.Execute(second.Command);
            Actor secondDevice = p.FindActor(second.Id)!;
            check(TutorialMapGuidance.Missions(p, TutorialMapGuidance.Objective(p, secondDevice)).Single().Id == mission.Id,
                id + " placement automatically joins the map's only objective mission");
            check(TutorialMapGuidance.Resolve(lesson, missionStep, p, secondDevice).Title == "The mission link is already complete",
                id + " auto-linked mission is recognised without a manual linking step");
            check(TutorialMapGuidance.Subject(p, id, []) is null, id + " does not guess between several devices");
            Actor secondObjective = TutorialMapGuidance.Objective(p, secondDevice)!;
            check(TutorialMapGuidance.Subject(p, id, [ObjectRef.Actor(secondObjective.Id)], device.Id)?.Id == secondDevice.Id,
                id + " explicitly selected objective replaces the previous device");
            check(TutorialMapGuidance.Subject(p, id, [ObjectRef.Actor(device.Id), ObjectRef.Actor(secondDevice.Id)], device.Id) is null,
                id + " multiple selected devices require an explicit choice");
            var emptyObjective = new Actor("EmptyTask", "SBase.SObjective");
            p.Actors.Add(emptyObjective);
            check(TutorialMapGuidance.Subject(p, id, [ObjectRef.Actor(emptyObjective.Id)], device.Id) is null,
                id + " empty selected task never falls back to an unrelated device");
            check(TutorialMapGuidance.Resolve(lesson, 0, p, null).Target == "panel:Outliner", id + " asks to select an existing device instead of placing another");
            int objectiveCount = p.Actors.Count(a => MissionLinks.IsObjective(a) && !MatchPlan.IsMission(a));
            var reused = PlaceableWiring.Place(p, Placeables.Stock.Single(e => e.Class == type), Vec3.One, Rot.Zero, [objective.Id]);
            session.Execute(reused.Command);
            check(p.Actors.Count(a => MissionLinks.IsObjective(a) && !MatchPlan.IsMission(a)) == objectiveCount,
                id + " placement into a selected objective does not create another one");
            check(TutorialMapGuidance.Objective(p, p.FindActor(reused.Id))?.Id == objective.Id, id + " guidance follows the reused objective");
            if (id == "disk")
            {
                var zone = PlaceableWiring.Place(p, Placeables.Stock.Single(e => e.Class == "SBase.SFlagDropZone"), Vec3.One, Rot.Zero, [device.Id]);
                session.Execute(zone.Command);
                check(TutorialMapGuidance.DropZones(p, device).Count == 1, "Read the actual indexed DropZone reference");
                check(TutorialMapGuidance.Resolve(lesson, 1, p, device).Title == "Use the existing drop zone", "Do not ask to duplicate a connected drop zone");
                check(TutorialMapGuidance.Subject(p, id, [ObjectRef.Actor(zone.Id)], secondDevice.Id)?.Id == device.Id,
                    "Selecting a destination follows its actual disk instead of the previously inspected one");
            }

            var zoneMission = new Actor("ZoneMission", "SBase.SMission");
            p.Actors.Add(zoneMission); // Undefined: must be activated through its parent.
            session.Execute(MissionLinks.Remove(p, mission.Id, "Objectives", secondObjective.Id)!);
            session.Execute(MissionLinks.Add(p, zoneMission.Id, [secondObjective.Id])!);
            check(TutorialMapGuidance.Resolve(lesson, missionStep, p, secondDevice).Title == "Connect the zone to a game-mode mission",
                id + " an orphan zone is not reported as playable");
            session.Execute(MissionLinks.Add(p, mission.Id, [zoneMission.Id])!);
            check(TutorialMapGuidance.Subject(p, id, [ObjectRef.Actor(zoneMission.Id)], device.Id)?.Id == secondDevice.Id,
                id + " recursively follows a selected zone to its device");
            check(TutorialMapGuidance.Resolve(lesson, missionStep, p, secondDevice).Title == "The mission link is already complete",
                id + " recognises a zone activated by its Story parent");
            session.Execute(MissionLinks.Add(p, mission.Id, [secondObjective.Id])!);
            check(TutorialMapGuidance.Resolve(lesson, missionStep, p, secondDevice).Title == "Review the objective's shared mission links",
                id + " flags a task accidentally listed by its zone and top mission");
            session.Execute(MissionLinks.Add(p, emptyObjective.Id, [secondDevice.Id])!);
            check(TutorialMapGuidance.Objective(p, secondDevice) is null
                && TutorialMapGuidance.Resolve(lesson, 1, p, secondDevice).Title == "Review the device's shared links",
                id + " never guesses between multiple device owners");
            session.Execute(MissionLinks.Remove(p, emptyObjective.Id, "Triggers", secondDevice.Id)!);
            session.Execute(MissionLinks.Remove(p, secondObjective.Id, "Triggers", secondDevice.Id)!);
            check(TutorialMapGuidance.Resolve(lesson, missionStep, p, secondDevice).Title == "Connect the device to an objective",
                id + " repairs an unlinked imported device without replacing it");
            session.Commands.Undo();
            check(TutorialMapGuidance.Objective(p, secondDevice)?.Id == secondObjective.Id, id + " undo immediately restores existing guidance");
        }
        var direct = EditorSession.New("DirectGuide", "Direct mission device");
        var directMission = new Actor("DirectMission", "SBase.SMission");
        directMission.Props.Set("GameMode", SvJsonNode.Integer(5));
        var directComputer = new Actor("DirectComputer", "SBase.SComputerObjectiveTrigger");
        directMission.Props.Set("Triggers", PropValues.Array([PropValues.ActorRef(directComputer.Id)]));
        direct.Project.Actors.AddRange([directMission, directComputer]);
        var laptop = TutorialCatalog.All.Single(t => t.Id == "laptop");
        check(TutorialMapGuidance.Resolve(laptop, 1, direct.Project, directComputer).Title == "Use the mission's existing device link", "Recognise a mission's directly connected device");
        check(TutorialMapGuidance.Resolve(laptop, 2, direct.Project, directComputer).Title == "The mission link is already complete", "Do not ask to nest a direct-device mission inside another mission");
        directMission.Props.Set("GameMode", SvJsonNode.Integer(0));
        check(TutorialMapGuidance.Resolve(laptop, 2, direct.Project, directComputer).Title == "Connect the zone to a game-mode mission", "A direct-device zone still needs an active parent");
        directMission.Props.Set("Objectives", PropValues.Array([PropValues.ActorRef(directMission.Id)]));
        check(TutorialMapGuidance.Resolve(laptop, 2, direct.Project, directComputer).Title == "Connect the zone to a game-mode mission", "Malformed imported mission cycles do not recurse forever");
    }
}
