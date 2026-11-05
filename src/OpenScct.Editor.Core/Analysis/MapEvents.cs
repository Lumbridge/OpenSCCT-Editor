using OpenScct.Content;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Game.SBase;
using Actor = OpenScct.Core.Engine.Actor;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>
/// Events as the game delivers them, for the analyses that follow a switch to what it works (lights, doors, lifts): an
/// event reaches every actor with that Tag; an SMagicEvent passes it on (each answering entry of its groups, delays
/// ignored, a sequence its first step); an animated actor it triggers plays its sequence, whose notifies fire their own
/// events (the map's notify edges, times ignored). Built once per analysis from the loaded map.
/// </summary>
public sealed class MapEvents
{
    private readonly Dictionary<string, List<(string Id, Actor Actor)>> _byTag = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILookup<string, string> _notifies;

    public MapEvents(AnalysisMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        foreach (ActorRecord record in map.Loaded.Gameplay.Actors)
        {
            if (map.Loaded.ActorOf(record) is not { } a)
            {
                continue;
            }

            Actors.Add((record.Id, a));
            string tag = a.Tag.ToString();
            if (!_byTag.TryGetValue(tag, out List<(string, Actor)>? list))
            {
                _byTag[tag] = list = [];
            }

            list.Add((record.Id, a));
        }

        _notifies = map.Loaded.Gameplay.EventEdges.Where(e => e.Kind == EventEdge.NotifyKind)
            .ToLookup(e => e.From, e => e.Event, StringComparer.Ordinal);
    }

    /// <summary>The gameplay actors of the map with their ids.</summary>
    public List<(string Id, Actor Actor)> Actors { get; } = [];

    /// <summary>
    /// Every delivery of <paramref name="events"/> sent as triggers or untriggers, in the order the game makes them:
    /// the receiver and whether it gets an untrigger. Relays are passed through (each relay once per kind of event)
    /// and not listed; an animated actor is listed and its notifies followed (once).
    /// </summary>
    public IEnumerable<(string Id, Actor Actor, bool Untrigger)> Deliver(IEnumerable<string> events, bool untrigger)
    {
        var queue = new Queue<(string Event, bool Untrigger)>(events.Select(e => (e, untrigger)));
        var relayed = new HashSet<(string, bool)>();
        while (queue.Count > 0)
        {
            (string evt, bool un) = queue.Dequeue();
            foreach ((string id, Actor receiver) in _byTag.GetValueOrDefault(evt) ?? [])
            {
                if (receiver is SMagicEvent relay)
                {
                    if (relayed.Add((id, un)))
                    {
                        foreach ((string Event, bool Untrigger) next in Relay(relay, un))
                        {
                            queue.Enqueue(next);
                        }
                    }

                    continue;
                }

                if (!un && _notifies.Contains(id) && relayed.Add((id, false)))
                {
                    foreach (string notify in _notifies[id])
                    {
                        queue.Enqueue((notify, false));
                    }
                }

                yield return (id, receiver, un);
            }
        }
    }

    /// <summary>The events an actor's first use fires and whether they go as untriggers, or null when it fires none: a
    /// gameplay object by its TriggerMethode, an objective by bUntriggerEvent, a presence sensor by bUntrigger; touch
    /// triggers, other sensors, cameras and alarms a trigger.</summary>
    public static (IReadOnlyList<string> Events, bool Untrigger)? FirstUse(Actor a)
    {
        ArgumentNullException.ThrowIfNull(a);
        IReadOnlyList<string> own = IsEvent(a.Event) ? [a.Event.ToString()] : [];
        return a switch
        {
            SObjectiveTrigger o => (own, o.bUntriggerEvent),
            SObjective o => (own, o.bUntriggerEvent),

            // Sensors and cameras fire their Event themselves; other gameplay objects through their interaction, whose
            // first use is an untrigger only for UnTriggerOnly (STriggerInteraction.HandleEventInfo).
            SCamera or SLaserTrigger or SVolumetricSensor => (own, false),
            SBasicGamePlayObject g => (own, g.TriggerMethode == TriggerType.UnTriggerOnly),
            SPresenceTrigger p => (own, p.bUntrigger),
            Trigger => (own, false),
            SAlarm alarm => ([.. alarm.Events.Where(IsEvent).Select(e => e.ToString())], false),
            _ => null,
        };
    }

    private static bool IsEvent(Name name) => !name.IsNone && name.ToString().Length > 0;

    /// <summary>What an SMagicEvent fires when it gets a trigger or an untrigger (SMagicEvent.NativeTrigger).</summary>
    private static IEnumerable<(string Event, bool Untrigger)> Relay(SMagicEvent relay, bool untrigger)
    {
        foreach (FEventGroup group in relay.Groups)
        {
            foreach (FSuperEvent entry in group.Sequence ? group.EventGroup.Take(1) : group.EventGroup)
            {
                bool answers = entry.ValidOn switch
                {
                    EEventType.EVT_Trigger => !untrigger,
                    EEventType.EVT_Untrigger => untrigger,
                    _ => true,
                };
                if (answers && IsEvent(entry.Event))
                {
                    yield return (entry.Event.ToString(), entry.Type is EEventType.EVT_Untrigger or EEventType.EVT_UntriggerTrigger);
                }
            }
        }
    }
}
