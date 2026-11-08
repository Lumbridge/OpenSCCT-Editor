using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Shell;

/// <summary>Read-only lesson guidance based on the actors and wiring placement actually produced.</summary>
public static class TutorialMapGuidance
{
    public static string? DeviceClass(string lesson) => lesson switch
    {
        "laptop" => "SBase.SComputerObjectiveTrigger", "bomb" => "SBase.SBombTargetObjectiveTrigger",
        "disk" => "SBase.SFlag", _ => null,
    };

    public static Actor? Subject(MapProject project, string lesson, IReadOnlyCollection<ObjectRef> selection, string? previous = null)
    {
        if (DeviceClass(lesson) is not { } type) return null;
        bool Matches(Actor actor) => ClassDefaults.IsA(actor, type);
        var selected = selection.Where(r => r.Kind == ObjectKind.Actor).Select(r => project.FindActor(r.Id)).OfType<Actor>().ToList();
        if (selected.FirstOrDefault(Matches) is { } direct) return direct;
        if (previous is not null && project.FindActor(previous) is { } old && Matches(old)) return old;
        var owned = selected.Where(MissionLinks.IsObjective).SelectMany(a => MissionTree.Refs(a.Props["Triggers"]))
            .Select(project.FindActor).OfType<Actor>().Where(Matches).DistinctBy(a => a.Id).Take(2).ToList();
        if (owned.Count == 1) return owned[0];
        var all = project.Actors.Where(Matches).Take(2).ToList();
        return all.Count == 1 ? all[0] : null;
    }

    public static Actor? Objective(MapProject project, Actor? device) => device is null ? null
        : project.Actors.FirstOrDefault(a => MissionLinks.IsObjective(a) && MissionLinks.Contains(a, "Triggers", device.Id));

    public static IReadOnlyList<Actor> Missions(MapProject project, Actor? objective) => objective is null ? []
        : MatchPlan.IsMission(objective) ? [objective]
        : project.Actors.Where(a => MatchPlan.IsMission(a) && MissionLinks.Contains(a, "Objectives", objective.Id)).ToList();

    public static IReadOnlyList<Actor> DropZones(MapProject project, Actor? disk) => disk is null ? []
        : MoverSetup.Elements((project.FindActor(disk.Id) ?? disk).Props["DropZone"]).Values.Select(PropValues.ActorId).OfType<string>()
            .Select(project.FindActor).OfType<Actor>().ToList();

    public static TutorialStep Resolve(EditorTutorial lesson, int step, MapProject project, Actor? device)
    {
        TutorialStep fallback = lesson.Steps[step];
        if (DeviceClass(lesson.Id) is not { } type) return fallback;
        string noun = lesson.Id switch { "laptop" => "computer", "bomb" => "bomb target", _ => "carry disk" };
        if (device is null)
        {
            if (step == 0 && !project.Actors.Any(a => ClassDefaults.IsA(a, type))) return fallback;
            return new("Select the device to work on", $"Select the {noun} you already placed in the map or Outliner, then choose Show me again. "
                + "The guide follows that device's existing objective and mission. Only place another device when you want another task.", "panel:Outliner");
        }
        Actor? objective = Objective(project, device);
        var missions = Missions(project, objective);
        string deviceName = MissionLinks.Name(device);
        if (step == 0) return new("Use the device already placed", $"{deviceName} is already in your map. "
            + (objective is null ? "Its objective link needs checking in the next steps. " : $"It is already connected to objective {MissionLinks.Name(objective)}. ")
            + "Continue with Next; you do not need to place a second device.", "setup:devices");
        if (lesson.Id == "disk")
        {
            var zones = DropZones(project, device);
            if (step == 1 && zones.Count > 0) return new("Use the existing drop zone", $"{deviceName} already names "
                + string.Join(", ", zones.Select(MissionLinks.Name)) + ". Inspect the destination and continue. Place another zone only if you want another extraction point.", "setup:dropzone");
            if (step == 1) return fallback;
            if (step == 2) return new("Check the disk's destination", $"Inspect {deviceName}'s DropZone references. "
                + (zones.Count == 0 ? "No valid drop zone is connected yet: go Back, select the disk and place its drop zone."
                    : "The existing drop-zone link is already set; check its position rather than making it again."), "setup:inspect");
            if (step == 3) return MissionStep(project, objective, missions, deviceName);
            return fallback;
        }
        if (step == 1 && objective is not null && MatchPlan.IsMission(objective)) return new("Use the mission's existing device link",
            $"{deviceName} already belongs directly to mission {MissionLinks.Name(objective)}. Inspect the mission's own devices; this setup does not need another objective.", "setup:devices");
        if (step == 1 && objective is not null) return new("Use the existing objective", $"{deviceName} already completes {MissionLinks.Name(objective)}. "
            + "Placement creates this objective automatically unless you placed into an objective selected earlier. Use this existing objective; do not create another one for the same device.", "setup:objective");
        if (step is 1 or 2) return MissionStep(project, objective, missions, deviceName);
        if (step == 3 && objective is not null) return new("The device link is already complete", $"{deviceName} is already linked as a device of {MissionLinks.Name(objective)}. "
            + "Inspect this relationship in Missions. Add devices is for a missing link or additional devices, so you can continue without linking this one again.", "setup:devices");
        return fallback;
    }

    private static TutorialStep MissionStep(MapProject project, Actor? objective, IReadOnlyList<Actor> missions, string device)
    {
        if (objective is not null && MatchPlan.IsMission(objective)) return new("The mission link is already complete",
            $"{device} is already counted directly by mission {MissionLinks.Name(objective)}. Keep this existing device link; only make a separate objective if you intend to split the task.", "setup:mission");
        if (objective is null) return new("Connect the device to an objective", $"{device} has no objective link. In Missions, choose an existing objective and use Add devices to link it. "
            + "Only if no suitable objective exists, choose a mission first and use New objective. This is needed for an unlinked imported device, not a normally placed one.", "setup:objective");
        if (missions.Count > 0) return new("The mission link is already complete", $"{MissionLinks.Name(objective)} already belongs to "
            + string.Join(", ", missions.Select(MissionLinks.Name)) + ". The guide is showing the existing link. Continue without creating another mission or objective.", "setup:mission");
        bool hasMission = project.Actors.Any(MatchPlan.IsMission);
        return new("Link the existing objective to a mission", $"{MissionLinks.Name(objective)} is selected for you. "
            + (hasMission ? "Click Choose mission, pick the Story mission you want, then Choose. This adds the objective to that mission; existing links are kept."
                : "There are no missions yet. Click New mission once, then reselect this objective and use Choose mission to add it to that mission.")
            + " Do not create a replacement objective.", hasMission ? "setup:mission" : "setup:new-mission");
    }
}
