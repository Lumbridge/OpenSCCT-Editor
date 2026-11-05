using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>What an SMagicEvent action sends, and which activations it answers (UE2 <c>EEventType</c>).</summary>
public enum MagicType
{
    Trigger = 0,
    Untrigger = 1,

    /// <summary>Trigger, then Untrigger the next time (alternates).</summary>
    TriggerUntrigger = 2,

    /// <summary>Untrigger, then Trigger the next time (alternates).</summary>
    UntriggerTrigger = 3,
}

/// <summary>One action of an SMagicEvent group (UE2 <c>SuperEvent</c>): fire <paramref name="Event"/> (a Tag)
/// <paramref name="Delay"/> seconds after the activation, as <paramref name="Type"/>, when the activation matches
/// <paramref name="On"/>.</summary>
public sealed record MagicAction(string Event, double Delay, MagicType Type = MagicType.Trigger, MagicType On = MagicType.Trigger);

/// <summary>A group of actions (UE2 <c>EventGroup</c>). <paramref name="Repeat"/>: 0 answers every activation, n
/// answers n cycles, -1 is off. A <paramref name="Sequence"/> group fires one action per activation, in order.</summary>
public sealed record MagicGroup(IReadOnlyList<MagicAction> Actions, int Repeat = 0, bool Sequence = false);

/// <summary>An action fired during a simulated run: when, by which group and action, and what it sent.</summary>
public sealed record MagicFiring(double Time, int Group, int Action, string Event, bool Trigger);

/// <summary>An activation of a simulated run: a trigger, an untrigger, or a trigger from the StopActor (clears what
/// is waiting).</summary>
public sealed record MagicActivation(double Time, bool Trigger = true, bool FromStopActor = false);

/// <summary>
/// The SMagicEvent workbench's data (spec/devices/magic-event.md): reading and writing <c>Groups</c> exactly as the
/// game stores them, simple edits as undo steps, a simulation of the native logic for the timeline and the preview,
/// and RE+'s JSON exchange format (<c>scct.smagic-event</c> v1) so events move between the two editors.
/// </summary>
public static class MagicEvents
{
    public const string Class = "SBase.SMagicEvent";
    public const string ExchangeFormat = "scct.smagic-event";

    /// <summary>The game's queue runs on a 0.05 s timer, so a delayed action fires up to that much late.</summary>
    public const double QueueStep = 0.05;

    public static readonly IReadOnlyList<string> TypeNames = ["EVT_Trigger", "EVT_Untrigger", "EVT_TriggerUntrigger", "EVT_UntriggerTrigger"];

    public static bool IsMagicEvent(Actor a) => ClassDefaults.IsA(a, Class);

    public static List<MagicGroup> Read(Actor a) => Read(a.Props["Groups"]);

    public static List<MagicGroup> Read(SvJsonNode? groups)
    {
        var result = new List<MagicGroup>();
        if (groups is not SvJsonArray arr)
        {
            return result;
        }

        foreach (SvJsonObject g in arr.OfType<SvJsonObject>())
        {
            var actions = new List<MagicAction>();
            if (g["EventGroup"] is SvJsonArray entries)
            {
                foreach (SvJsonObject e in entries.OfType<SvJsonObject>())
                {
                    actions.Add(new MagicAction((e["Event"] as SvJsonString)?.Value ?? "", PropValues.Num(e["Delay"]),
                        (MagicType)Math.Clamp((int)PropValues.Num(e["Type"]), 0, 3), (MagicType)Math.Clamp((int)PropValues.Num(e["ValidOn"]), 0, 3)));
                }
            }

            result.Add(new MagicGroup(actions, (int)PropValues.Num(g["Repeat"]), g["Sequence"] is SvJsonLiteral { Kind: SvJsonKind.True }));
        }

        return result;
    }

    /// <summary>The <c>Groups</c> value as the converter writes it (SequenceIndex 0, the runtime start).</summary>
    public static SvJsonArray Write(IEnumerable<MagicGroup> groups)
    {
        var arr = new SvJsonArray();
        foreach (MagicGroup g in groups)
        {
            var entries = new SvJsonArray();
            foreach (MagicAction a in g.Actions)
            {
                var e = new SvJsonObject();
                e.Add("Delay", SvJsonNode.Float((float)a.Delay));
                e.Add("Event", SvJsonNode.String(a.Event));
                e.Add("Type", SvJsonNode.Integer((int)a.Type));
                e.Add("ValidOn", SvJsonNode.Integer((int)a.On));
                entries.Add(e);
            }

            var o = new SvJsonObject();
            o.Add("EventGroup", entries);
            o.Add("Repeat", SvJsonNode.Integer(g.Repeat));
            o.Add("Sequence", SvJsonNode.Bool(g.Sequence));
            o.Add("SequenceIndex", SvJsonNode.Integer(0));
            arr.Add(o);
        }

        return arr;
    }

    /// <summary>Replaces an SMagicEvent's groups, one undo step (edits with the same <paramref name="mergeKey"/> in a
    /// row make one step). Actions need an event name and a delay of 0 or more.</summary>
    public static ReplaceObjectsCommand SetGroups(MapProject p, string id, IReadOnlyList<MagicGroup> groups, string? name = null, string? mergeKey = null)
    {
        if (p.FindActor(id) is not { } a || !IsMagicEvent(a))
        {
            throw new ArgumentException($"'{id}' is not an SMagicEvent");
        }

        Check(groups);
        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], name ?? $"Edit {id} actions",
            actor: x => x.Props["Groups"] = groups.Count == 0 ? null : Write(groups), mergeKey: mergeKey);
    }

    /// <summary>Refuses actions without an event to fire, negative delays and repeats below -1.</summary>
    private static void Check(IReadOnlyList<MagicGroup> groups)
    {
        if (groups.FirstOrDefault(g => g.Repeat < -1) is { } bad)
        {
            throw new ArgumentException($"group {groups.ToList().IndexOf(bad)}: repeat is -1 (off), 0 (every time) or a count");
        }

        foreach ((MagicAction action, int i) in groups.SelectMany(g => g.Actions).Select((x, i) => (x, i)))
        {
            if (string.IsNullOrWhiteSpace(action.Event) || action.Event.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"action {i} has no event to fire");
            }

            if (!double.IsFinite(action.Delay) || action.Delay < 0)
            {
                throw new ArgumentException($"action {i} ({action.Event}): the delay is seconds, 0 or more");
            }
        }
    }

    /// <summary>Sets (or clears, with null) the actor whose trigger stops everything waiting.</summary>
    public static ReplaceObjectsCommand SetStopActor(MapProject p, string id, string? stopId)
    {
        if (stopId is not null && p.FindActor(stopId) is null)
        {
            throw new ArgumentException($"no actor '{stopId}'");
        }

        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], $"Stop actor of {id}",
            actor: x => x.Props["StopActor"] = stopId is null ? null : PropValues.ActorRef(stopId));
    }

    /// <summary>A new SMagicEvent with one group, tagged <paramref name="tag"/> so senders can fire it.</summary>
    public static AddObjectsCommand Create(MapProject p, Vec3 at, string tag, IReadOnlyList<MagicGroup> groups)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("a new SMagicEvent needs the Tag its senders fire");
        }

        Check(groups);
        string id = GeShapes.IdSource(p)("SMagicEvent");
        var a = new Actor(id, Class) { Location = GeEdits.Round(at) };
        a.Props.Set("Tag", SvJsonNode.String(tag));
        if (groups.Count > 0)
        {
            a.Props.Set("Groups", Write(groups));
        }

        return new AddObjectsCommand($"Add {id}", actors: [a]);
    }

    /// <summary>
    /// Plays activations through the game's logic (spec/devices/magic-event.md rules 1 to 5): groups answer by their
    /// Repeat and ValidOn, sequences step one action per answering activation, delayed actions wait in a queue
    /// drained every <see cref="QueueStep"/> seconds, alternating types flip on the action fired at once (a delayed
    /// copy flips only itself), and a trigger from the StopActor clears the queue. Returns the firings in order.
    /// </summary>
    public static List<MagicFiring> Simulate(IReadOnlyList<MagicGroup> authored, IReadOnlyList<MagicActivation> activations)
    {
        // Runtime copies: the game edits the group's Repeat and SequenceIndex and the entries' alternating types.
        var groups = authored.Select(g => (Actions: g.Actions.Select(a => a.Type).ToArray(), Repeat: g.Actions.Count == 0 ? -1 : g.Repeat, Index: 0)).ToList();
        var queue = new List<(double Due, int Group, int Action, MagicType Type)>();
        var fired = new List<MagicFiring>();

        void Fire(double time, int g, int i, MagicType type) =>
            fired.Add(new MagicFiring(time, g, i, authored[g].Actions[i].Event, type is MagicType.Trigger or MagicType.TriggerUntrigger));

        void Drain(double until)
        {
            // Due entries leave the queue on the next timer tick at or after their due time.
            while (queue.Count > 0)
            {
                double tick = queue.Min(q => Math.Ceiling(q.Due / QueueStep - 1e-9) * QueueStep);
                if (tick > until)
                {
                    return;
                }

                foreach (var q in queue.Where(q => q.Due <= tick + 1e-9).ToList())
                {
                    queue.Remove(q);
                    Fire(tick, q.Group, q.Action, q.Type);
                }
            }
        }

        foreach (MagicActivation act in activations.OrderBy(a => a.Time))
        {
            Drain(act.Time);
            if (act.FromStopActor)
            {
                queue.Clear();
                continue;
            }

            for (int g = 0; g < groups.Count; g++)
            {
                var state = groups[g];
                if (state.Repeat <= -1)
                {
                    continue;
                }

                bool cycleDone;
                if (!authored[g].Sequence)
                {
                    for (int i = 0; i < state.Actions.Length; i++)
                    {
                        Answer(g, i, ref state.Actions[i]);
                    }

                    cycleDone = true;
                }
                else
                {
                    cycleDone = false;
                    if (state.Index < state.Actions.Length && Answers(authored[g].Actions[state.Index].On, act.Trigger))
                    {
                        Answer(g, state.Index, ref state.Actions[state.Index]);
                        if (++state.Index >= state.Actions.Length)
                        {
                            state.Index = 0;
                            cycleDone = true;
                        }
                    }
                }

                if (cycleDone && state.Repeat > 0 && --state.Repeat == 0)
                {
                    state.Repeat = -1;
                }

                groups[g] = state;
            }

            void Answer(int g, int i, ref MagicType type)
            {
                MagicAction a = authored[g].Actions[i];
                if (!Answers(a.On, act.Trigger))
                {
                    return;
                }

                if (a.Delay == 0)
                {
                    Fire(act.Time, g, i, type);
                    type = Flip(type);
                }
                else
                {
                    // A queued copy carries the authored entry's type now; its own flip is thrown away with it.
                    queue.Insert(0, (act.Time + a.Delay, g, i, type));
                }
            }
        }

        Drain(double.MaxValue);
        return fired;
    }

    /// <summary>How long one activation takes to play out (the longest delay of an answering action).</summary>
    public static double Length(IReadOnlyList<MagicGroup> groups) =>
        groups.SelectMany(g => g.Actions).Select(a => a.Delay).DefaultIfEmpty(0).Max();

    /// <summary>The actors an event name reaches (every gameplay actor whose effective Tag is that name).</summary>
    public static IReadOnlyList<string> Receivers(GameplayGraph graph, string eventName) =>
        graph.TagIndex.TryGetValue(eventName, out List<string>? ids) ? ids : [];

    /// <summary>One line describing an action ("door1 at 1.5 s, trigger then untrigger").</summary>
    public static string Describe(MagicAction a) =>
        string.Create(CultureInfo.InvariantCulture, $"{a.Event} at {a.Delay:0.##} s, {TypeWords(a.Type)}") + (a.On == MagicType.Trigger ? "" : $" (on {TypeWords(a.On)})");

    public static string TypeWords(MagicType t) => t switch
    {
        MagicType.Trigger => "trigger",
        MagicType.Untrigger => "untrigger",
        MagicType.TriggerUntrigger => "trigger then untrigger",
        _ => "untrigger then trigger",
    };

    // ---- the RE+ JSON exchange (tests/SMagicEventJson.md in RE+): every leaf is a string ------------------------

    /// <summary>The exchange document of an SMagicEvent: its groups and StopActor, plus, as reference, the Tags of the
    /// actors it fires and of its StopActor.</summary>
    public static SvJsonObject Export(MapProject p, Actor a)
    {
        var groups = new SvJsonArray();
        foreach (MagicGroup g in Read(a))
        {
            var entries = new SvJsonArray();
            foreach (MagicAction x in g.Actions)
            {
                var e = new SvJsonObject();
                e.Add("Delay", SvJsonNode.String(x.Delay.ToString("0.######", CultureInfo.InvariantCulture)));
                e.Add("Event", SvJsonNode.String(x.Event));
                e.Add("Type", SvJsonNode.String(TypeNames[(int)x.Type]));
                e.Add("ValidOn", SvJsonNode.String(TypeNames[(int)x.On]));
                entries.Add(e);
            }

            var o = new SvJsonObject();
            o.Add("EventGroup", entries);
            o.Add("Repeat", SvJsonNode.String(g.Repeat.ToString(CultureInfo.InvariantCulture)));
            o.Add("Sequence", SvJsonNode.String(g.Sequence ? "True" : "False"));
            o.Add("SequenceIndex", SvJsonNode.String("0"));
            groups.Add(o);
        }

        var props = new SvJsonObject();
        props.Add("Groups", groups);
        props.Add("StopActor", SvJsonNode.String(PropValues.ActorId(a.Props["StopActor"]) ?? "None"));
        var fired = new HashSet<string>(Read(a).SelectMany(g => g.Actions).Select(x => x.Event), StringComparer.OrdinalIgnoreCase);
        string? stopId = PropValues.ActorId(a.Props["StopActor"]);
        var tags = new SvJsonObject();
        foreach (Actor x in p.Actors.Where(x => (GameplayGraph.IsGameplay(x) && fired.Contains(x.Tag)) || string.Equals(x.Id, stopId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            tags.Add(x.Id, SvJsonNode.String(x.Tag));
        }

        var doc = new SvJsonObject();
        doc.Add("format", SvJsonNode.String(ExchangeFormat));
        doc.Add("version", SvJsonNode.Integer(1));
        doc.Add("class", SvJsonNode.String(Class));
        doc.Add("properties", props);
        doc.Add("context", tags);
        return doc;
    }

    /// <summary>Reads an exchange document's groups and StopActor (null when the document leaves it out). Only
    /// <c>format</c>, <c>version</c>, <c>class</c> and <c>properties</c> are read; numbers and booleans may be strings
    /// (RE+) or JSON values.</summary>
    public static (List<MagicGroup>? Groups, string? StopActor, bool HasStopActor) Import(SvJsonNode doc)
    {
        if (doc is not SvJsonObject o || (o["format"] as SvJsonString)?.Value != ExchangeFormat)
        {
            throw new ArgumentException($"not an SMagicEvent exchange document (format \"{ExchangeFormat}\")");
        }

        if (PropValues.Num(o["version"]) != 1 && (o["version"] as SvJsonString)?.Value != "1")
        {
            throw new ArgumentException("only version 1 exchange documents are read");
        }

        if (o["properties"] is not SvJsonObject props)
        {
            throw new ArgumentException("the document has no properties");
        }

        List<MagicGroup>? groups = null;
        if (props["Groups"] is SvJsonArray arr)
        {
            groups = [];
            foreach ((SvJsonObject g, int gi) in arr.OfType<SvJsonObject>().Select((g, i) => (g, i)))
            {
                var actions = new List<MagicAction>();
                foreach (SvJsonObject e in (g["EventGroup"] as SvJsonArray ?? []).OfType<SvJsonObject>())
                {
                    actions.Add(new MagicAction(Text(e["Event"]) ?? throw new ArgumentException($"group {gi}: an action has no Event"),
                        Number(e["Delay"], $"group {gi} Delay"), TypeOf(e["Type"]), TypeOf(e["ValidOn"])));
                }

                groups.Add(new MagicGroup(actions, (int)Number(g["Repeat"], $"group {gi} Repeat"), Flag(g["Sequence"])));
            }
        }

        string? stop = Text(props["StopActor"]);
        return (groups, stop is null or "None" ? null : stop, props["StopActor"] is not null);
    }

    private static string? Text(SvJsonNode? n) => n switch
    {
        SvJsonString s => s.Value,
        SvJsonNumber num => PropValues.Num(num).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static double Number(SvJsonNode? n, string what) => n switch
    {
        null => 0,
        SvJsonNumber => PropValues.Num(n),
        SvJsonString s when double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) => v,
        _ => throw new ArgumentException($"{what} is not a number"),
    };

    private static bool Flag(SvJsonNode? n) => n switch
    {
        SvJsonLiteral { Kind: SvJsonKind.True } => true,
        SvJsonString s => s.Value.Equals("True", StringComparison.OrdinalIgnoreCase) || s.Value == "1",
        _ => false,
    };

    /// <summary>A type by name (<c>EVT_Trigger</c> or <c>Trigger</c>) or index.</summary>
    public static MagicType TypeOf(SvJsonNode? n)
    {
        string? text = Text(n);
        if (text is null)
        {
            return MagicType.Trigger;
        }

        for (int i = 0; i < TypeNames.Count; i++)
        {
            if (text.Equals(TypeNames[i], StringComparison.OrdinalIgnoreCase) || text.Equals(TypeNames[i][4..], StringComparison.OrdinalIgnoreCase)
                || text == i.ToString(CultureInfo.InvariantCulture))
            {
                return (MagicType)i;
            }
        }

        throw new ArgumentException($"unknown event type '{text}' ({string.Join(", ", TypeNames)})");
    }

    private static bool Answers(MagicType on, bool trigger) => on switch
    {
        MagicType.Trigger => trigger,
        MagicType.Untrigger => !trigger,
        _ => true,
    };

    private static MagicType Flip(MagicType t) => t switch
    {
        MagicType.TriggerUntrigger => MagicType.UntriggerTrigger,
        MagicType.UntriggerTrigger => MagicType.TriggerUntrigger,
        _ => t,
    };
}
