using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>A placeable put down: the one undo step that adds it (and wires it), its id, what the step wired, and the
/// links it still needs, in words.</summary>
public sealed record PlacedPlaceable(IEditCommand Command, string Id, IReadOnlyList<string> Wired, IReadOnlyList<string> Needs)
{
    /// <summary>"Placed X (what): wired ... . Still to wire: ... . It is on (where)." for the status bar and the log.</summary>
    public string Describe(string what, string where)
    {
        string text = $"Placed {Id} ({what})";
        if (Wired.Count > 0)
        {
            text += ": " + string.Join("; ", Wired);
        }

        if (Needs.Count > 0)
        {
            text += $". Still to wire: {string.Join("; ", Needs)}";
        }

        return $"{text}. It is on {where}.";
    }
}

/// <summary>
/// Puts a palette placeable (<see cref="Placeables"/>) into a map as one undo step, with the links it needs made where
/// the selection or the map leaves no doubt, the way the workbenches make them, and every link left to make named:
/// <list type="bullet">
/// <item>a computer, bomb target or disk to carry joins the selected objective's Triggers (or the map's only
/// objective's), else gets an objective of its own (an SObjective listing it) in the selected mission, or the map's only
/// objective mission;</item>
/// <item>a hunt disk joins the selected disk hunt mission's Triggers, or the map's only disk hunt mission's;</item>
/// <item>a disk drop zone goes in the selected disk's DropZone list, or the map's only disk's;</item>
/// <item>an objective joins the selected mission, or the map's only objective mission;</item>
/// <item>a camera, laser, sensor, presence trigger or switch fires the one receiver selected
/// (<see cref="Wiring.Link(MapProject, string, string)"/>: an alarm, a mover...); a new alarm is set off by the selected
/// senders;</item>
/// <item>a network camera joins the ring of the selected network camera (after it), or the map's only network.</item>
/// </list>
/// </summary>
public static class PlaceableWiring
{
    private const int DiskHunt = 4;
    private const int DeathMatch = 6;
    private const int MercDeathMatch = 7;

    /// <summary>Places <paramref name="entry"/> at a spot. <paramref name="selected"/> are the ids selected when it was
    /// placed (they say which mission, objective, disk, alarm or network the new actor joins).</summary>
    public static PlacedPlaceable Place(MapProject p, Placeable entry, Vec3 location, Rot rotation, IReadOnlyList<string> selected,
        IClassCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(selected);
        catalog ??= ClassDefaults.Catalog;
        Func<string, string> ids = GeShapes.IdSource(p);
        var actor = new Actor(ids(entry.ShortClass), entry.Class) { Location = GeEdits.Round(location), Rotation = rotation };
        MoverBase.Place(actor);
        List<Actor> chosen = selected.Select(p.FindActor).OfType<Actor>().ToList();
        var job = new Job(ActorKinds.OfClass(entry.Class, catalog), actor, [actor], chosen);

        // Links that only touch the new actors are made on them before they are added: an objective trigger with no
        // objective to join gets one of its own.
        if (job.Kind is ActorKind.Computer or ActorKind.BombTarget or ActorKind.Flag)
        {
            job.Objective = Pick(p, chosen, IsPlainObjective);
            if (job.Objective is null)
            {
                var made = new Actor(ids("SObjective"), "SBase.SObjective") { Location = actor.Location + new Vec3(0, 0, 64) };
                made.Props.Set("Triggers", PropValues.Array([PropValues.ActorRef(actor.Id)]));
                job.Added.Add(made);
                job.Wired.Add($"added objective {made.Id}, completed by it");
            }
        }

        var add = new AddObjectsCommand(job.Added.Count == 1 ? $"Place {entry.Title}" : $"Place {entry.Title} and its objective",
            actors: job.Added);

        // Links that change actors already in the map are worked out on the map with the new actors in it, each on the
        // map as the one before left it, then all undone: the step does them together.
        add.Do(p);
        try
        {
            Wire(p, job);
        }
        finally
        {
            for (int i = job.Edits.Count - 1; i >= 0; i--)
            {
                job.Edits[i].Undo(p);
            }

            add.Undo(p);
        }

        IEditCommand command = job.Edits.Count == 0 ? add : new CompositeCommand(add.Name, [add, .. job.Edits]);
        return new PlacedPlaceable(command, actor.Id, job.Wired, job.Needs);
    }

    private static void Wire(MapProject p, Job job)
    {
        Actor actor = job.Actor;
        List<Actor> chosen = job.Chosen;
        List<string> wired = job.Wired, needs = job.Needs;
        switch (job.Kind)
        {
            case ActorKind.Computer or ActorKind.BombTarget or ActorKind.Flag:
                if (job.Objective is { } objective)
                {
                    job.Apply(p, Append(p, objective.Id, "Triggers", actor.Id));
                    wired.Add($"completes objective {objective.Id}");
                }
                else
                {
                    JoinMission(p, job, job.Added[^1]);
                }

                if (job.Kind == ActorKind.Flag)
                {
                    needs.Add("give it a drop zone: with it selected, place a disk drop zone");
                }

                break;
            case ActorKind.Objective:
                JoinMission(p, job, actor);
                needs.Add("give it a trigger: with it selected, place a computer, bomb target or disk to carry");
                break;
            case ActorKind.Mission:
                needs.Add("set its GameMode and MinimumObjectives in the Inspector, and give it objectives: with it selected, place them");
                break;
            case ActorKind.Disk:
                if (Pick(p, chosen, m => MatchPlan.IsMission(m) && Mode(m) == DiskHunt) is { } hunt)
                {
                    job.Apply(p, Append(p, hunt.Id, "Triggers", actor.Id));
                    wired.Add($"counts for disk hunt mission {hunt.Id}");
                }
                else
                {
                    needs.Add("list it in a disk hunt mission's Triggers: select the mission (GameMode 4), then place disks");
                }

                break;
            case ActorKind.FlagDropZone:
                if (Pick(p, chosen, a => ClassDefaults.IsA(a, "SBase.SFlag")) is not { } disk)
                {
                    needs.Add("list it in a disk's DropZone: select the disk to carry, then place drop zones");
                }
                else if (AddDropZone(p, disk, actor.Id) is { } zone)
                {
                    job.Apply(p, zone);
                    wired.Add($"disk {disk.Id} can be delivered to it");
                }
                else
                {
                    needs.Add($"{disk.Id} already has 5 drop zones, the most it takes: list it in another disk's DropZone");
                }

                break;
            case ActorKind.Alarm:
                List<Actor> senders = chosen.Where(IsSender).ToList();
                foreach (Actor sender in senders)
                {
                    job.Apply(p, Wiring.Link(p, sender.Id, actor.Id));
                }

                if (senders.Count > 0)
                {
                    wired.Add($"set off by {string.Join(", ", senders.Select(s => s.Id))}");
                }
                else
                {
                    needs.Add("nothing sets it off: select cameras or lasers before placing it, or link them in the security tool's Link mode");
                }

                break;
            case ActorKind.Camera when CameraNetworks.IsNetworkCamera(actor):
                JoinNetwork(p, job);
                break;
            case ActorKind.Camera or ActorKind.Laser or ActorKind.MotionSensor or ActorKind.PresenceSensor or ActorKind.Switch:
                if (chosen.Count == 1 && Receives(chosen[0], job.Kind))
                {
                    wired.Add(Wiring.Describe(p, actor.Id, chosen[0].Id));
                    job.Apply(p, Wiring.Link(p, actor.Id, chosen[0].Id));
                }
                else
                {
                    needs.Add(job.Kind == ActorKind.Switch
                        ? "it fires nothing: select what it works (a door, a light) before placing it, or link it in the security tool's Link mode"
                        : "it fires nothing: select an alarm before placing it, or link it in the security tool's Link mode");
                }

                if (job.Kind == ActorKind.MotionSensor)
                {
                    needs.Add("list the volumes it watches in its Volumes");
                }

                break;
            case ActorKind.Mover or ActorKind.Lift or ActorKind.Door:
                if (PropValues.AssetPath(actor.Props["StaticMesh"] ?? ClassDefaults.Catalog.Default(actor.Class, "StaticMesh")) is null)
                {
                    needs.Add("give it a StaticMesh in the Inspector and its keys in the Mover panel");
                }

                break;
        }

        if (ClassDefaults.IsA(actor, "SBase.SGPOControl"))
        {
            needs.Add("name it in the ERControl of the devices in its area");
        }
        else if (ClassDefaults.IsA(actor, "SBase.SSprinkler"))
        {
            needs.Add($"fire its Tag ({actor.Tag}) from a sender: link one to it in the security tool's Link mode");
        }
    }

    /// <summary>An objective (new or not) joins the selected mission, or the map's only objective mission.</summary>
    private static void JoinMission(MapProject p, Job job, Actor objective)
    {
        if (Pick(p, job.Chosen, m => MatchPlan.IsMission(m) && Mode(m) is not (DiskHunt or DeathMatch or MercDeathMatch)) is { } mission)
        {
            job.Apply(p, Append(p, mission.Id, "Objectives", objective.Id));
            job.Wired.Add($"{objective.Id} is an objective of mission {mission.Id}");
        }
        else
        {
            job.Needs.Add(p.Actors.Any(MatchPlan.IsMission)
                ? $"{objective.Id} is in no mission: select its mission before placing, or add it to the mission's Objectives"
                : $"no mission lists {objective.Id}: place a mission, then with it selected place its objectives");
        }
    }

    /// <summary>A network camera joins the selected network camera's ring (after it), or the map's only network (at its
    /// end).</summary>
    private static void JoinNetwork(MapProject p, Job job)
    {
        Actor camera = job.Actor;
        List<CameraNetwork> networks = CameraNetworks.Networks(p).Where(n => n.Cameras.All(c => !Same(c.Id, camera.Id))).ToList();
        string? after = job.Chosen.FirstOrDefault(a => CameraNetworks.IsNetworkCamera(a) && !Same(a.Id, camera.Id))?.Id;
        CameraNetwork? network = after is not null ? networks.FirstOrDefault(n => n.Cameras.Any(c => Same(c.Id, after)))
            : networks.Count == 1 ? networks[0] : null;
        if (network is null)
        {
            job.Needs.Add(networks.Count == 0
                ? "it is a network of one: place more, select them in order and use Link Selected into a Ring (Camera Network panel)"
                : "it is in no network: select a camera of the network it joins before placing it, or link it in the Camera Network panel");
            return;
        }

        List<string> order = network.Cameras.Select(c => c.Id).ToList();
        int at = after is null ? order.Count : order.FindIndex(id => Same(id, after)) + 1;
        order.Insert(at, camera.Id);
        job.Apply(p, CameraNetworks.Link(p, order, network.Loop));
        job.Wired.Add($"joins the camera network of {network.Cameras[0].Id}, after {order[at - 1]}");
    }

    /// <summary>The one selected actor that passes <paramref name="test"/>; with none selected, the map's only one.</summary>
    private static Actor? Pick(MapProject p, List<Actor> chosen, Func<Actor, bool> test)
    {
        List<Actor> picked = chosen.Where(test).Take(2).ToList();
        if (picked.Count > 0)
        {
            return picked.Count == 1 ? picked[0] : null;
        }

        List<Actor> all = p.Actors.Where(test).Take(2).ToList();
        return all.Count == 1 ? all[0] : null;
    }

    private static bool IsPlainObjective(Actor a) => MatchPlan.IsObjective(a) && !MatchPlan.IsMission(a);

    private static int Mode(Actor mission) => (int)ClassDefaults.Number(mission, "GameMode", 0);

    /// <summary>Actors that set an alarm off: cameras, lasers, sensors, presence triggers, switches.</summary>
    private static bool IsSender(Actor a) =>
        ActorKinds.OfClass(a.Class, ClassDefaults.Catalog) is ActorKind.Camera or ActorKind.Laser or ActorKind.MotionSensor or ActorKind.PresenceSensor
            or ActorKind.Switch && !CameraNetworks.IsNetworkCamera(a);

    /// <summary>What a new sender fires when it is the one actor selected: an alarm (or an event, a mover, a light) for
    /// a security device; any gameplay actor or light for a switch.</summary>
    private static bool Receives(Actor a, ActorKind sender) => sender == ActorKind.Switch
        ? GameplayGraph.IsGameplay(a) || ClassDefaults.IsA(a, "Engine.Light")
        : ActorKinds.OfClass(a.Class, ClassDefaults.Catalog) is ActorKind.Alarm or ActorKind.MagicEvent or ActorKind.Mover or ActorKind.Door
            or ActorKind.Lift or ActorKind.TriggerLight;

    /// <summary>Adds an actor reference to an array property of <paramref name="ownerId"/> (Triggers, Objectives).</summary>
    private static ReplaceObjectsCommand Append(MapProject p, string ownerId, string prop, string id) =>
        ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(ownerId)], $"Add {id} to {ownerId}.{prop}", actor: a =>
        {
            List<SvJsonNode> items = a.Props[prop] is SvJsonArray arr ? [.. arr] : [];
            if (!items.Any(n => Same(PropValues.ActorId(n), id)))
            {
                items.Add(PropValues.ActorRef(id));
            }

            a.Props.Set(prop, PropValues.Array(items));
        });

    /// <summary>Puts a drop zone in a disk's first free DropZone slot (the static array holds 5), or null when none is free.</summary>
    private static ReplaceObjectsCommand? AddDropZone(MapProject p, Actor disk, string zone)
    {
        SortedDictionary<int, SvJsonNode> slots = MoverSetup.Elements(disk.Props["DropZone"]);
        int free = Enumerable.Range(0, 5).Where(i => !slots.TryGetValue(i, out SvJsonNode? n) || PropValues.ActorId(n) is null)
            .DefaultIfEmpty(-1).First();
        if (free < 0)
        {
            return null;
        }

        slots[free] = PropValues.ActorRef(zone);
        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(disk.Id)], $"Add {zone} to {disk.Id}.DropZone", actor: a =>
        {
            var elements = new SvJsonObject();
            foreach ((int i, SvJsonNode n) in slots)
            {
                elements.Add(i.ToString(CultureInfo.InvariantCulture), n);
            }

            var o = new SvJsonObject();
            o.Add("$elements", elements);
            a.Props.Set("DropZone", o);
        });
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a placement is wiring: the new actor (and the objective it joins), the selection, and the edits and
    /// words so far.</summary>
    private sealed class Job(ActorKind kind, Actor actor, List<Actor> added, List<Actor> chosen)
    {
        public ActorKind Kind { get; } = kind;

        public Actor Actor { get; } = actor;

        /// <summary>The new actors: the placed one first.</summary>
        public List<Actor> Added { get; } = added;

        public List<Actor> Chosen { get; } = chosen;

        public Actor? Objective { get; set; }

        public List<IEditCommand> Edits { get; } = [];

        public List<string> Wired { get; } = [];

        public List<string> Needs { get; } = [];

        /// <summary>Does an edit on the map now (the later ones are worked out after it) and keeps it for the step.</summary>
        public void Apply(MapProject p, IEditCommand? cmd)
        {
            if (cmd is null or ReplaceObjectsCommand { IsEmpty: true })
            {
                return;
            }

            cmd.Do(p);
            Edits.Add(cmd);
        }
    }
}
