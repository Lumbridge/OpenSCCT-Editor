using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>One thing that happens when a mission (a zone) is complete.</summary>
/// <param name="Kind">door, light, sound, message, event or actor (from the receivers' classes).</param>
/// <param name="Event">The Tag fired.</param>
/// <param name="Receivers">The actors with that Tag.</param>
/// <param name="Relay">The SMagicEvent that fires it (with its group and action index), or null when the mission fires
/// it directly.</param>
/// <param name="Own">True when the action can be changed without touching anything else: it sits in the mission's own
/// relay, or the mission is the only sender of the Tag it fires directly.</param>
public sealed record ZoneAction(string Kind, string Event, IReadOnlyList<string> Receivers, double Delay, MagicType Type, string? Relay, int Group, int Index,
    bool Own);

/// <summary>
/// Zone on-complete actions (RE+ Map Design zones). A mission fires its <c>Event</c> when it is complete
/// (spec/objectives/missions.md rule 29). To do several things, with delays, the editor puts an SMagicEvent relay in
/// between: the mission fires the relay's Tag and each relay action fires a door, light, sound, HUD message or any
/// actor. Everything stays stock classes, so it plays the same in OpenSCCT and in the original game.
/// <para>
/// The editor only ever changes the mission's own relay: an SMagicEvent that the mission alone fires, with one plain
/// group (not a sequence, not switched off, no StopActor). A mission that fires anything else gets a new relay, whose
/// first action fires what the mission fired before, so other senders keep their wiring and the map's own events stay
/// as they are. An actor already reached on completion is refused (a second firing would toggle it back, and an
/// action that fires its own relay would repeat forever).
/// </para>
/// </summary>
public static class ZoneActions
{
    /// <summary>What a mission does when complete: what its Event reaches directly, and the actions of the
    /// SMagicEvents among those that answer the mission's trigger (or untrigger).</summary>
    public static List<ZoneAction> Of(MapProject p, GameplayGraph graph, Actor mission)
    {
        var actions = new List<ZoneAction>();
        if (EventOf(mission) is not { } tag)
        {
            return actions;
        }

        MagicType sent = Sent(mission);
        Actor? own = OwnRelay(p, graph, mission);
        bool soleSender = SendersOf(graph, tag).All(s => Same(s, mission.Id));
        foreach (string id in MagicEvents.Receivers(graph, tag))
        {
            Actor r = p.FindActor(id)!;
            if (!MagicEvents.IsMagicEvent(r))
            {
                actions.Add(new ZoneAction(KindOf(r), tag, [id], 0, sent, null, -1, -1, soleSender));
                continue;
            }

            bool isOwn = ReferenceEquals(r, own);
            List<MagicGroup> groups = MagicEvents.Read(r);
            for (int g = 0; g < groups.Count; g++)
            {
                if (groups[g].Repeat < 0)
                {
                    continue;
                }

                for (int i = 0; i < groups[g].Actions.Count; i++)
                {
                    MagicAction a = groups[g].Actions[i];
                    if (!Answers(a.On, sent))
                    {
                        continue;
                    }

                    IReadOnlyList<string> receivers = MagicEvents.Receivers(graph, a.Event);
                    string kind = receivers.Count > 0 ? KindOf(p.FindActor(receivers[0])!) : "actor";
                    actions.Add(new ZoneAction(kind, a.Event, receivers, a.Delay, a.Type, r.Id, g, i, isOwn));
                }
            }
        }

        return actions;
    }

    /// <summary>The kind of thing an actor is, for the action list.</summary>
    public static string KindOf(Actor a) => a switch
    {
        _ when MoverBase.IsMover(a) => "door",
        _ when ClassDefaults.IsA(a, "Engine.Light") => "light",
        _ when a.ShortClass == "SMessageTrigger" => "message",
        _ when MagicEvents.IsMagicEvent(a) => "event",
        _ when a.ShortClass.Contains("Sound", StringComparison.OrdinalIgnoreCase) || a.ShortClass.Contains("Music", StringComparison.OrdinalIgnoreCase) => "sound",
        _ => "actor",
    };

    /// <summary>
    /// Adds actions, one undo step: the mission's own relay fires each receiver after <paramref name="delay"/>
    /// seconds (a relay is made when the mission has none). A receiver whose Tag is shared with actors it should not
    /// drag along gets its own Tag first (<see cref="Wiring.TagFor"/>). Refused for the mission itself, its relay, and
    /// actors already reached when it completes.
    /// </summary>
    public static IEditCommand Add(MapProject p, string missionId, IReadOnlyList<string> receiverIds, double delay = 0, MagicType type = MagicType.Trigger)
    {
        Actor mission = Mission(p, missionId);
        CheckDelay(delay);
        GameplayGraph graph = GameplayGraph.Build(p);
        Actor? own = OwnRelay(p, graph, mission);
        HashSet<string> reached = Reached(p, graph, mission);
        var parts = new List<IEditCommand>();
        var actions = new List<MagicAction>();
        foreach (string receiverId in receiverIds)
        {
            Actor receiver = p.FindActor(receiverId) ?? throw new ArgumentException($"no actor '{receiverId}'");
            if (ReferenceEquals(receiver, mission) || ReferenceEquals(receiver, own))
            {
                throw new ArgumentException($"{receiver.Id} cannot fire itself when {mission.Id} completes");
            }

            (string tag, bool isNew) = Wiring.TagFor(p, receiver);
            if (!reached.Add(tag))
            {
                throw new ArgumentException($"{receiver.Id} already reacts when {mission.Id} completes (its Tag {tag} is fired then); " +
                    "a second firing would switch it back, so change the existing action instead");
            }

            if (isNew)
            {
                parts.Add(ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(receiver.Id)], $"Tag {receiver.Id}", actor: a => a.Props.Set("Tag", SvJsonNode.String(tag))));
            }

            actions.Add(new MagicAction(tag, delay, type));
        }

        parts.Add(AddToRelay(p, mission, own, actions));
        string what = receiverIds.Count == 1 ? receiverIds[0] : $"{receiverIds.Count} actors";
        return new CompositeCommand($"{mission.Id} on complete: {type.ToString().ToLowerInvariant()} {what}", parts);
    }

    /// <summary>Adds a HUD message on completion: a new SMessageTrigger with the spies' and mercs' lines, fired by the
    /// mission's own relay after <paramref name="delay"/> seconds. One undo step.</summary>
    public static IEditCommand AddMessage(MapProject p, string missionId, string spyText, string mercText, int displaySeconds = 6, double delay = 0)
    {
        Actor mission = Mission(p, missionId);
        CheckDelay(delay);
        string id = GeShapes.IdSource(p)("SMessageTrigger");
        var message = new Actor(id, "SBase.SMessageTrigger") { Location = mission.Location + new Vec3(0, 0, 32) };
        message.Props.Set("Tag", SvJsonNode.String(id));
        message.Props.Set("MessageSpy", SvJsonNode.String(spyText));
        message.Props.Set("MessageMerc", SvJsonNode.String(mercText));
        message.Props.Set("DisplayTime", SvJsonNode.Integer(displaySeconds));
        var add = new AddObjectsCommand($"Add {id}", actors: [message]);
        Actor? own = OwnRelay(p, GameplayGraph.Build(p), mission);
        return new CompositeCommand($"{mission.Id} on complete: message", [add, AddToRelay(p, mission, own, [new MagicAction(id, delay)])]);
    }

    /// <summary>Changes the delay and type of the action at <paramref name="index"/> of <see cref="Of"/> (null keeps
    /// the current one), one undo step. Only actions in the mission's own relay can be timed; a receiver the mission
    /// fires directly hears it at once.</summary>
    public static ReplaceObjectsCommand SetTiming(MapProject p, GameplayGraph graph, string missionId, int index, double? delay, MagicType? type)
    {
        (Actor mission, ZoneAction action) = Pick(p, graph, missionId, index);
        double newDelay = delay ?? action.Delay;
        CheckDelay(newDelay);
        if (action.Relay is null || !action.Own)
        {
            throw new ArgumentException(action.Relay is null
                ? $"{mission.Id} fires {action.Event} directly, at once; remove it and add it again to give it a delay"
                : $"{action.Event} is fired by {action.Relay}, which other senders also use: time it in the Event Sequencer");
        }

        List<MagicGroup> groups = MagicEvents.Read(p.FindActor(action.Relay)!);
        var list = groups[action.Group].Actions.ToList();
        list[action.Index] = list[action.Index] with { Delay = newDelay, Type = type ?? action.Type };
        groups[action.Group] = groups[action.Group] with { Actions = list };
        return MagicEvents.SetGroups(p, action.Relay, groups, $"Time {action.Event} in {mission.Id} on complete");
    }

    /// <summary>
    /// Removes the action at <paramref name="index"/> of <see cref="Of"/>, one undo step. An action of the mission's
    /// own relay is deleted from it. A receiver the mission fires directly gets its own Tag (its id), so it no longer
    /// hears the mission; that is refused when other actors fire the same Tag, since they would lose it too. When it was
    /// the Tag's last receiver the mission's Event is cleared.
    /// </summary>
    public static ReplaceObjectsCommand Remove(MapProject p, GameplayGraph graph, string missionId, int index)
    {
        (Actor mission, ZoneAction gone) = Pick(p, graph, missionId, index);
        if (gone.Relay is { } relayId)
        {
            if (!gone.Own)
            {
                throw new ArgumentException($"{gone.Event} is fired by {relayId}, which other senders also use: edit it in the Event Sequencer");
            }

            List<MagicGroup> groups = MagicEvents.Read(p.FindActor(relayId)!);
            var kept = groups[gone.Group].Actions.ToList();
            kept.RemoveAt(gone.Index);
            groups[gone.Group] = groups[gone.Group] with { Actions = kept };
            return MagicEvents.SetGroups(p, relayId, groups, $"Remove {gone.Event} from {missionId} on complete");
        }

        if (!gone.Own)
        {
            List<string> others = SendersOf(graph, gone.Event).Where(s => !Same(s, mission.Id)).ToList();
            throw new ArgumentException($"{gone.Receivers[0]} also hears {string.Join(", ", others)} through Tag {gone.Event}; " +
                "taking it off the mission would cut those off too, so rewire it in the Connections panel");
        }

        string receiver = gone.Receivers[0];
        var cmd = ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(receiver)], $"Remove {receiver} from {missionId} on complete",
            actor: a => a.Props.Set("Tag", SvJsonNode.String(a.Id)));
        if (MagicEvents.Receivers(graph, gone.Event).Count == 1)
        {
            Actor after = mission.Clone();
            after.Props.Remove("Event");
            cmd.Actor(mission.Clone(), after);
        }

        return cmd;
    }

    /// <summary>
    /// The mission's own relay: the SMagicEvent its Event reaches when the mission sends a trigger, nothing else fires
    /// that Tag, and the SMagicEvent has one plain group (not a sequence, not switched off) and no StopActor, so an
    /// action added to it fires on completion and at no other time. Null when there is none.
    /// </summary>
    public static Actor? OwnRelay(MapProject p, GameplayGraph graph, Actor mission)
    {
        if (EventOf(mission) is not { } tag || Sent(mission) != MagicType.Trigger || SendersOf(graph, tag).Any(s => !Same(s, mission.Id)))
        {
            return null;
        }

        List<Actor> relays = MagicEvents.Receivers(graph, tag).Select(id => p.FindActor(id)!).Where(MagicEvents.IsMagicEvent).ToList();
        if (relays is not [{ } relay] || relay.Props["StopActor"] is not null)
        {
            return null;
        }

        List<MagicGroup> groups = MagicEvents.Read(relay);
        return groups.Count == 0 || (groups is [{ Sequence: false, Repeat: >= 0 } g] && g.Actions.All(a => Answers(a.On, MagicType.Trigger))) ? relay : null;
    }

    /// <summary>Every actor that fires <paramref name="tag"/>.</summary>
    private static IEnumerable<string> SendersOf(GameplayGraph graph, string tag) =>
        MagicEvents.Receivers(graph, tag).SelectMany(graph.In).Where(l => l.Kind == LinkKind.Fire && string.Equals(l.Event, tag, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.From).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Tags fired when the mission completes: its Event and what the SMagicEvents it reaches fire.</summary>
    private static HashSet<string> Reached(MapProject p, GameplayGraph graph, Actor mission)
    {
        var reached = new HashSet<string>(Of(p, graph, mission).Select(a => a.Event), StringComparer.OrdinalIgnoreCase);
        if (EventOf(mission) is { } tag)
        {
            reached.Add(tag);
        }

        return reached;
    }

    /// <summary>Appends actions to the mission's own relay, or makes one and points the mission at it. What the mission
    /// fired before becomes the new relay's first action, in the same direction, so everything it reached still hears
    /// it.</summary>
    private static IEditCommand AddToRelay(MapProject p, Actor mission, Actor? own, IReadOnlyList<MagicAction> actions)
    {
        if (own is not null)
        {
            List<MagicGroup> groups = MagicEvents.Read(own);
            if (groups.Count == 0)
            {
                groups.Add(new MagicGroup([]));
            }

            groups[0] = groups[0] with { Actions = [.. groups[0].Actions, .. actions] };
            return MagicEvents.SetGroups(p, own.Id, groups, $"{mission.Id} on complete");
        }

        var first = new List<MagicAction>();
        if (EventOf(mission) is { } old)
        {
            first.Add(new MagicAction(old, 0, Sent(mission)));
        }

        string tag = FreeTag(p, mission.Id + "_Done");
        AddObjectsCommand create = MagicEvents.Create(p, mission.Location + new Vec3(0, 0, 48), tag, [new MagicGroup([.. first, .. actions])]);
        ReplaceObjectsCommand point = ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(mission.Id)], $"{mission.Id} fires {tag}", actor: m =>
        {
            m.Props.Set("Event", SvJsonNode.String(tag));
            m.Props.Remove("bUntriggerEvent");
        });
        return new CompositeCommand($"Relay for {mission.Id}", [create, point]);
    }

    private static (Actor Mission, ZoneAction Action) Pick(MapProject p, GameplayGraph graph, string missionId, int index)
    {
        Actor mission = Mission(p, missionId);
        List<ZoneAction> actions = Of(p, graph, mission);
        return index >= 0 && index < actions.Count
            ? (mission, actions[index])
            : throw new ArgumentException($"{missionId} has no action {index} (it has {actions.Count})");
    }

    private static Actor Mission(MapProject p, string id) =>
        p.FindActor(id) is { } m && ClassDefaults.IsA(m, "SBase.SObjective")
            ? m
            : throw new ArgumentException($"'{id}' is not a mission or objective (SMission, SObjective)");

    private static string? EventOf(Actor mission) =>
        mission.Props.GetString("Event") is { Length: > 0 } tag && !tag.Equals("None", StringComparison.OrdinalIgnoreCase) ? tag : null;

    /// <summary>What the mission sends on completion (rule 29: an untrigger with <c>bUntriggerEvent</c>).</summary>
    private static MagicType Sent(Actor mission) => mission.Props.GetBool("bUntriggerEvent") ? MagicType.Untrigger : MagicType.Trigger;

    /// <summary>Whether an action with ValidOn <paramref name="on"/> answers <paramref name="sent"/> (magic-event.md
    /// rule 3d: the two alternating values answer either).</summary>
    private static bool Answers(MagicType on, MagicType sent) => on is MagicType.TriggerUntrigger or MagicType.UntriggerTrigger || on == sent;

    private static void CheckDelay(double delay)
    {
        if (!double.IsFinite(delay) || delay < 0)
        {
            throw new ArgumentException("the delay is seconds, 0 or more");
        }
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string FreeTag(MapProject p, string stem)
    {
        string tag = stem;
        for (int n = 2; p.Actors.Any(a => string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase)); n++)
        {
            tag = $"{stem}{n}";
        }

        return tag;
    }
}
