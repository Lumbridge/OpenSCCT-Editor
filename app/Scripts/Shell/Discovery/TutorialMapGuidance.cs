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
        Actor? old = previous is null ? null : project.FindActor(previous);
        var direct = selected.Where(Matches).ToList();
        if (direct.Count > 0) return direct.Count == 1 ? direct[0] : null;
        var owners = selected.Where(MissionLinks.IsObjective).ToList();
        if (owners.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<Actor> Devices(Actor owner)
            {
                if (!seen.Add(owner.Id)) yield break;
                foreach (string id in MissionTree.Refs(owner.Props["Triggers"]))
                    if (project.FindActor(id) is { } device && Matches(device)) yield return device;
                foreach (string id in MissionTree.Refs(owner.Props["Objectives"]))
                    if (project.FindActor(id) is { } child && MissionLinks.IsObjective(child))
                        foreach (Actor device in Devices(child)) yield return device;
            }
            var owned = owners.SelectMany(Devices).DistinctBy(a => a.Id).ToList();
            if (owned.Count == 1) return owned[0];
            return old is not null && owned.Any(a => a.Id == old.Id) ? old : null;
        }
        if (lesson == "disk" && selected.Any(a => ClassDefaults.IsA(a, "SBase.SFlagDropZone")))
        {
            var disks = project.Actors.Where(Matches).Where(d => DropZones(project, d).Any(z => selected.Any(a => a.Id == z.Id))).ToList();
            return disks.Count == 1 ? disks[0] : null;
        }
        if (old is not null && Matches(old)) return old;
        var all = project.Actors.Where(Matches).Take(2).ToList();
        return all.Count == 1 ? all[0] : null;
    }

    public static IReadOnlyList<Actor> Owners(MapProject project, Actor? device) => device is null ? []
        : project.Actors.Where(a => MissionLinks.IsObjective(a) && MissionLinks.Contains(a, "Triggers", device.Id)).ToList();

    public static Actor? Objective(MapProject project, Actor? device) => Owners(project, device) is { Count: 1 } owners ? owners[0] : null;

    public static IReadOnlyList<Actor> Missions(MapProject project, Actor? objective) => objective is null ? []
        : MatchPlan.IsMission(objective) ? [objective]
        : project.Actors.Where(a => MatchPlan.IsMission(a) && MissionLinks.Contains(a, "Objectives", objective.Id)).ToList();

    public static IReadOnlyList<Actor> DropZones(MapProject project, Actor? disk) => disk is null ? []
        : MoverSetup.Elements((project.FindActor(disk.Id) ?? disk).Props["DropZone"]).Values.Select(PropValues.ActorId).OfType<string>()
            .Select(project.FindActor).OfType<Actor>().Where(a => ClassDefaults.IsA(a, "SBase.SFlagDropZone")).DistinctBy(a => a.Id).ToList();

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
        if (Owners(project, device).Count > 1) return new("Review the device's shared links",
            $"{deviceName} is listed by more than one objective or mission. In Missions, keep it under the intended task and unlink the extra device rows with ×. "
            + "This keeps the device itself. A device has one runtime owner, so shared links do not create independent tasks.", "setup:devices");
        if (step == 0) return new("Use the device already placed", $"{deviceName} is already in your map. "
            + (objective is null ? "Its objective link needs checking in the next steps. " : $"It is already connected to objective {MissionLinks.Name(objective)}. ")
            + "Continue with Next; you do not need to place a second device.", "setup:devices");
        if (lesson.Id == "disk")
        {
            var zones = DropZones(project, device);
            if (step == 1 && zones.Count > 0) return new("Use the existing drop zone", $"{deviceName} already names "
                + string.Join(", ", zones.Select(MissionLinks.Name)) + ". Inspect the destination and continue. Place another zone only if you want another extraction point.", "setup:dropzone");
            if (step == 1) return new("Place the disk's drop zone", $"{deviceName} is selected for wiring. Drag Disk drop zone to the destination once. "
                + "Keep the disk selected so the new zone joins this disk, even when the map contains several disks.", fallback.Target);
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
        bool HasActiveRoot(Actor mission, HashSet<string> seen) => seen.Add(mission.Id) && ((int)ClassDefaults.Number(mission, "GameMode", 0) != 0
            || project.Actors.Where(a => MatchPlan.IsMission(a) && MissionLinks.Contains(a, "Objectives", mission.Id)).Any(a => HasActiveRoot(a, seen)));
        TutorialStep OrphanZone(Actor zone) => new("Connect the zone to a game-mode mission", $"{MissionLinks.Name(zone)} has no parent with a game mode. "
            + "Select the intended top mission in Missions and use Add objectives… to link this existing zone. Use Story on the top mission and Undefined on its zones. "
            + "Keep this zone's objectives and devices. The mission diagram shows this hierarchy.", "setup:devices");
        if (objective is not null && MatchPlan.IsMission(objective) && !HasActiveRoot(objective, new(StringComparer.OrdinalIgnoreCase)))
            return OrphanZone(objective);
        if (objective is not null && MatchPlan.IsMission(objective)) return new("The mission link is already complete",
            $"{device} is already counted directly by mission {MissionLinks.Name(objective)}. Keep this existing device link; only make a separate objective if you intend to split the task.", "setup:mission");
        if (objective is null) return new("Connect the device to an objective", $"{device} has no objective link. It is selected for you: click Choose objective and pick the intended existing task to link this device. "
            + "Only if no suitable objective exists, choose a mission first and use New objective, then select this device again and use Choose objective. Keep existing tasks and devices.", "setup:objective");
        if (missions.Count > 1) return new("Review the objective's shared mission links", $"{MissionLinks.Name(objective)} belongs to "
            + string.Join(", ", missions.Select(MissionLinks.Name)) + ". Keep the intended parent and unlink extra objective rows with ×; this preserves the objective and its devices. "
            + "For zones, put the objective in one zone mission and put that zone in the top mission.", "setup:devices");
        if (missions.Count == 1)
        {
            if (!HasActiveRoot(missions[0], new(StringComparer.OrdinalIgnoreCase))) return OrphanZone(missions[0]);
            return new("The mission link is already complete", $"{MissionLinks.Name(objective)} already belongs to "
                + MissionLinks.Name(missions[0]) + ". Keep this hierarchy, including any parent zones. Play with its top mission's game mode; do not create another mission or objective.", "setup:mission");
        }
        bool hasMission = project.Actors.Any(MatchPlan.IsMission);
        return new("Link the existing objective to a mission", $"{MissionLinks.Name(objective)} is selected for you. "
            + (hasMission ? "Click Choose mission, pick the intended Story mission or one of its zone missions, then Choose. This adds the objective there; existing links are kept."
                : "There are no missions yet. Click New mission once, then reselect this objective and use Choose mission to add it to that mission.")
            + " Do not create a replacement objective.", hasMission ? "setup:mission" : "setup:new-mission");
    }
}
