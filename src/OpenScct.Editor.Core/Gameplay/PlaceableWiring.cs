using System.Globalization;
using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>A placeable put down: the one undo step that adds it (and wires it), its id, what the step wired, and the
/// links it still needs (short, each with how to make it).</summary>
public sealed record PlacedPlaceable(IEditCommand Command, string Id, IReadOnlyList<string> Wired, IReadOnlyList<string> Needs)
{
    /// <summary>The status bar's line: what still needs wiring first (it is what matters, and the bar is short), else
    /// what was wired. <see cref="Describe"/> has the whole of it, for the log.</summary>
    public string Status(string what) => Needs.Count > 0
        ? $"Placed {Id}. Still to wire: {string.Join("; ", Needs)}"
        : Wired.Count > 0 ? $"Placed {Id} ({what}): {string.Join("; ", Wired)}" : $"Placed {Id} ({what})";

    /// <summary>"Placed X (what): wired ... . Still to wire: ... . It is on (where)." for the log.</summary>
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
/// the selection (or the map) leaves no doubt, the way the workbenches make them, and every link left to make named:
/// <list type="bullet">
/// <item>a computer, bomb target or disk to carry joins the Triggers of the one objective selected; with no objective
/// selected it gets an objective of its own (an SObjective listing it) in the selected mission, or the map's only
/// objective mission (not disk hunt, deathmatch or a mission of missions);</item>
/// <item>a hunt disk joins the selected disk hunt mission's Triggers, or the map's only disk hunt mission's;</item>
/// <item>a disk drop zone goes in the selected disk's DropZone list, or the map's only disk's;</item>
/// <item>an objective joins the selected mission, or the map's only objective mission;</item>
/// <item>a camera, laser, sensor or presence trigger sets off the one alarm (or event, mover, light) selected; a switch
/// works the one door, lift, light, alarm or event selected (<see cref="Wiring.Link(MapProject, string, string)"/>);
/// a new alarm gets a Tag of its own and is set off by the selected senders that fire nothing yet (one that already
/// fires something is left alone, and named);</item>
/// <item>a network camera joins the ring of the selected network camera (after it), or the map's only network.</item>
/// </list>
/// </summary>
public static class PlaceableWiring
{
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
        var job = new Job(KindOf(entry.Class, catalog), actor, [actor], chosen, catalog);

        // A new receiver answers to a Tag of its own where its class-name Tag would be shared: an alarm always (the next
        // alarm placed would answer to "SAlarm" too), anything else when an event in the map already names its class.
        if (job.Kind == ActorKind.Alarm || NamedByEvent(p, actor.ShortClass))
        {
            actor.Props.Set("Tag", SvJsonNode.String(actor.Id));
        }

        // Links that only touch the new actors are made on them before they are added: an objective trigger placed with
        // no objective selected gets one of its own.
        if (job.Kind is ActorKind.Computer or ActorKind.BombTarget or ActorKind.Flag)
        {
            List<Actor> objectives = chosen.Where(IsPlainObjective).ToList();
            job.Objective = objectives.Count == 1 ? objectives[0] : null;
            if (job.Objective is null)
            {
                var made = new Actor(ids("SObjective"), "SBase.SObjective") { Location = actor.Location + new Vec3(0, 0, 64) };
                made.Props.Set("Triggers", PropValues.Array([PropValues.ActorRef(actor.Id)]));
                job.Added.Add(made);
                job.Wired.Add($"added objective {made.Id}, completed by it");
                if (objectives.Count > 1)
                {
                    job.Needs.Add($"its objective: {objectives.Count} were selected, so it got one of its own (select one objective to add triggers to it)");
                }
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

    /// <summary>What to select before placing <paramref name="entry"/> so it is wired, for the place tool's hint; empty
    /// for a class with nothing to wire by selection.</summary>
    public static string Hint(Placeable entry, IClassCatalog? catalog = null) => KindOf(entry.Class, catalog ?? ClassDefaults.Catalog) switch
    {
        ActorKind.Computer or ActorKind.BombTarget or ActorKind.Flag => "Select its objective first, else it gets one of its own in the selected mission",
        ActorKind.Objective => "Select its mission first to wire it",
        ActorKind.Disk => "Select the disk hunt mission first to wire it",
        ActorKind.FlagDropZone => "Select the disk to carry first to wire it",
        ActorKind.Alarm => "Select the cameras and lasers that set it off first",
        ActorKind.CameraNetwork => "Select the network camera it comes after first",
        ActorKind.Camera or ActorKind.Laser or ActorKind.MotionSensor or ActorKind.PresenceSensor => "Select the alarm it sets off first to wire it",
        ActorKind.Switch => "Select the door, lift, light or alarm it works first",
        _ => "",
    };

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
                    needs.Add("a drop zone (with it selected, place a disk drop zone)");
                }

                break;
            case ActorKind.Objective:
                JoinMission(p, job, actor);
                needs.Add("a trigger (with it selected, place a computer, bomb target or disk to carry)");
                break;
            case ActorKind.Mission:
                needs.Add("its GameMode and MinimumObjectives (Inspector), and objectives (with it selected, place them)");
                break;
            case ActorKind.Disk:
                if (Pick(p, chosen, m => MatchPlan.IsMission(m) && Mode(m) == GameModes.DiskHunt) is { } hunt)
                {
                    job.Apply(p, Append(p, hunt.Id, "Triggers", actor.Id));
                    wired.Add($"counts for disk hunt mission {hunt.Id}");
                }
                else
                {
                    needs.Add("a disk hunt mission (select it first, then place disks)");
                }

                break;
            case ActorKind.FlagDropZone:
                if (Pick(p, chosen, a => ClassDefaults.IsA(a, "SBase.SFlag")) is not { } disk)
                {
                    needs.Add("a disk (select the disk to carry first, then place drop zones)");
                }
                else if (AddDropZone(p, disk, actor.Id) is { } zone)
                {
                    job.Apply(p, zone);
                    wired.Add($"disk {disk.Id} can be delivered to it");
                }
                else
                {
                    needs.Add($"a disk: {disk.Id} already has 5 drop zones, the most it takes");
                }

                break;
            case ActorKind.Alarm:
                List<Actor> senders = chosen.Where(a => IsSender(a, job.Catalog)).ToList();
                List<Actor> busy = senders.Where(a => FiredEvent(a, job.Catalog) is not null).ToList();
                List<Actor> free = senders.Except(busy).ToList();
                foreach (Actor sender in free)
                {
                    job.Apply(p, Wiring.Link(p, sender.Id, actor.Id));
                }

                if (free.Count > 0)
                {
                    wired.Add($"set off by {string.Join(", ", free.Select(s => s.Id))}");
                }

                if (busy.Count > 0)
                {
                    needs.Add($"{(free.Count > 0 ? "more senders" : "a sender")}: "
                        + string.Join(", ", busy.Select(a => $"{a.Id} already sets off {FiredEvent(a, job.Catalog)}"))
                        + $", so {(busy.Count == 1 ? "it was" : "they were")} left alone (relink in the security tool's Link mode)");
                }
                else if (free.Count == 0)
                {
                    needs.Add("a sender (select cameras or lasers first, or use the security tool's Link mode)");
                }

                break;
            case ActorKind.CameraNetwork:
                JoinNetwork(p, job);
                needs.Add("a CamName for the mercenaries' HUD (Camera Network panel)");
                break;
            case ActorKind.Camera or ActorKind.Laser or ActorKind.MotionSensor or ActorKind.PresenceSensor or ActorKind.Switch:
                List<Actor> receivers = chosen.Where(a => Receives(a, job.Kind, job.Catalog)).ToList();
                string what = job.Kind == ActorKind.Switch ? "what it works" : "what it sets off";
                if (receivers.Count == 1)
                {
                    wired.Add(Fires(p, actor, receivers[0], job.Kind == ActorKind.Switch ? "works" : "sets off"));
                    job.Apply(p, Wiring.Link(p, actor.Id, receivers[0].Id));
                }
                else if (receivers.Count > 1)
                {
                    needs.Add($"{what}: {receivers.Count} were selected ({string.Join(", ", receivers.Take(3).Select(a => a.Id))}"
                        + $"{(receivers.Count > 3 ? "..." : "")}), so none was linked (use the security tool's Link mode)");
                }
                else
                {
                    needs.Add(job.Kind == ActorKind.Switch
                        ? "what it works (select a door, lift or light first, or use the security tool's Link mode)"
                        : "what it sets off (select an alarm first, or use the security tool's Link mode)");
                }

                if (job.Kind == ActorKind.MotionSensor)
                {
                    needs.Add("the volumes it watches (its Volumes)");
                }

                break;
            case ActorKind.Mover or ActorKind.Lift or ActorKind.Door:
                if (PropValues.AssetPath(actor.Props["StaticMesh"] ?? job.Catalog.Default(actor.Class, "StaticMesh")) is null)
                {
                    needs.Add("a StaticMesh (Inspector) and its keys (Mover panel)");
                }

                break;
            case ActorKind.Zone:
                needs.Add("the devices of its area (name it in their ERControl)");
                break;
        }

        if (ClassDefaults.IsA(actor, "SBase.SSprinkler"))
        {
            needs.Add($"a sender firing its Tag {actor.Tag} (security tool's Link mode)");
        }
    }

    /// <summary>An objective (new or not) joins the selected mission, or the map's only objective mission.</summary>
    private static void JoinMission(MapProject p, Job job, Actor objective)
    {
        if (Pick(p, job.Chosen, m => IsObjectiveMission(p, m)) is { } mission)
        {
            job.Apply(p, Append(p, mission.Id, "Objectives", objective.Id));
            job.Wired.Add($"{objective.Id} is an objective of mission {mission.Id}");
        }
        else
        {
            job.Needs.Add(p.Actors.Any(m => IsObjectiveMission(p, m))
                ? $"a mission for {objective.Id} (select the mission first, or add it to the mission's Objectives)"
                : $"a mission for {objective.Id} (place a mission, then with it selected place its objectives)");
        }
    }

    /// <summary>A mission objectives go in: not disk hunt or deathmatch, and not a mission of missions (one whose
    /// Objectives are missions, as a GM_Multi top mission over zone missions is; a GM_Multi mission listing objectives
    /// takes them).</summary>
    private static bool IsObjectiveMission(MapProject p, Actor m) =>
        MatchPlan.IsMission(m) && Mode(m) is not (GameModes.DiskHunt or GameModes.DeathMatch or GameModes.MercDeathMatch)
        && !MissionTree.Refs(m.Props["Objectives"]).Any(id => p.FindActor(id) is { } o && MatchPlan.IsMission(o));

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
                ? "a network: it is the only network camera (place more, then Link Selected into a Ring in the Camera Network panel)"
                : "a network (select the network camera it comes after first, or use the Camera Network panel)");
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

    /// <summary>The kind a class is wired as: its icon kind, but a security zone (SGPOControl, drawn as a switch) fires
    /// nothing.</summary>
    private static ActorKind KindOf(string cls, IClassCatalog catalog) =>
        catalog.IsA(cls, "SBase.SGPOControl") ? ActorKind.Zone : ActorKinds.OfClass(cls, catalog);

    /// <summary>Actors that set an alarm off: cameras, lasers, sensors, presence triggers and switches (not network
    /// cameras, security zones or switch managers).</summary>
    private static bool IsSender(Actor a, IClassCatalog catalog) =>
        KindOf(a.Class, catalog) is ActorKind.Camera or ActorKind.Laser or ActorKind.MotionSensor or ActorKind.PresenceSensor or ActorKind.Switch
        && !catalog.IsA(a.Class, "SBase.SInterrupteurManager");

    /// <summary>What a new sender fires when it is selected: an alarm (or an event, a mover, a light) for a security
    /// device; a door, lift, mover, light, alarm, event, sound, message or switch manager for a switch, never another
    /// sender.</summary>
    private static bool Receives(Actor a, ActorKind sender, IClassCatalog catalog)
    {
        ActorKind kind = KindOf(a.Class, catalog);
        return sender == ActorKind.Switch
            ? kind is ActorKind.Mover or ActorKind.Door or ActorKind.Lift or ActorKind.TriggerLight or ActorKind.PointLight or ActorKind.SpotLight
                or ActorKind.Alarm or ActorKind.MagicEvent or ActorKind.Emitter or ActorKind.Sound or ActorKind.Message
                || catalog.IsA(a.Class, "SBase.SInterrupteurManager")
            : kind is ActorKind.Alarm or ActorKind.MagicEvent or ActorKind.Mover or ActorKind.Door or ActorKind.Lift or ActorKind.TriggerLight;
    }

    /// <summary>The Tag a sender's Event fires, or null when it fires nothing.</summary>
    private static string? FiredEvent(Actor a, IClassCatalog catalog) =>
        (a.Props.GetString("Event") ?? (catalog.Default(a.Class, "Event") as SvJsonString)?.Value) is { Length: > 0 } e
        && !e.Equals("None", StringComparison.OrdinalIgnoreCase) ? e : null;

    /// <summary>"sets off CamAlarm" (with the others answering to its Tag, or the Tag it is given) for a new link.</summary>
    private static string Fires(MapProject p, Actor from, Actor to, string verb)
    {
        (string tag, bool isNew) = Wiring.TagFor(p, to);
        int others = isNew ? 0 : p.Actors.Count(a => !ReferenceEquals(a, to) && !ReferenceEquals(a, from) && Same(a.Tag, tag) && GameplayGraph.IsGameplay(a));
        return $"{verb} {to.Id}" + (isNew ? $" (given the Tag {tag})" : others > 0 ? $" and {others} more with its Tag {tag}" : "");
    }

    /// <summary>True when an event property in the map names <paramref name="tag"/>.</summary>
    private static bool NamedByEvent(MapProject p, string tag) =>
        p.Actors.Any(a => a.Props.Any(kv => GameplayDoc.IsEventKey(kv.Key) && kv.Value switch
        {
            SvJsonString s => Same(s.Value, tag),
            SvJsonArray arr => arr.Any(n => n is SvJsonString s && Same(s.Value, tag)),
            _ => false,
        }));

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
    private sealed class Job(ActorKind kind, Actor actor, List<Actor> added, List<Actor> chosen, IClassCatalog catalog)
    {
        public ActorKind Kind { get; } = kind;

        public IClassCatalog Catalog { get; } = catalog;

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
