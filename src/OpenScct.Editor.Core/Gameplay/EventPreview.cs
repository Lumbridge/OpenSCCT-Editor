using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>An event reaching an actor during an event preview: when, the event, trigger or untrigger, and the
/// SMagicEvent that sent it.</summary>
public sealed record EventDelivery(double Time, string Receiver, string Event, bool Trigger, string From);

/// <summary>What an event preview has done to the map by a time: the events delivered so far, each trigger light's
/// state, and the movers a trigger has reached with the time of the latest such trigger.</summary>
public sealed record EventState(IReadOnlyList<EventDelivery> Deliveries, IReadOnlyDictionary<string, bool> Lights,
    IReadOnlyDictionary<string, double> Movers);

/// <summary>
/// What an SMagicEvent's activations do to the map, as the game delivers events: an event reaches every actor with
/// that Tag; another SMagicEvent passes it on through its own groups, delays and repeats
/// (<see cref="MagicEvents.Simulate"/>, at most <see cref="MaxRelays"/> relays deep, so event rings end); a trigger
/// light switches as STriggerLight.Trigger and UnTrigger do (spec/devices/trigger-light.md) from its start state; a
/// mover starts moving on a trigger. The chain is played once in full (<see cref="Deliveries"/>, ending at
/// <see cref="End"/>); <see cref="At"/> gives the state at a time. The actor that started the chain, which relays
/// pass on as Other, is the previewed trigger: no relay's StopActor. Only SMagicEvents pass events on here: a gameplay
/// object or objective an event reaches is listed, not played.
/// </summary>
public sealed class EventPreview
{
    /// <summary>How many relays deep events are followed.</summary>
    public const int MaxRelays = 8;

    private readonly MapProject _project;

    private EventPreview(MapProject project, List<EventDelivery> deliveries, Dictionary<string, bool> start)
    {
        _project = project;
        Deliveries = deliveries;
        StartLights = start;
    }

    /// <summary>Every event delivered by the whole chain, in time order.</summary>
    public IReadOnlyList<EventDelivery> Deliveries { get; }

    /// <summary>The time of the last delivery (0 when nothing is delivered).</summary>
    public double End => Deliveries.Count == 0 ? 0 : Deliveries[^1].Time;

    /// <summary>Each trigger light of the map and whether it is on when the round starts (bInitialyOn and a light type).</summary>
    public IReadOnlyDictionary<string, bool> StartLights { get; }

    public static bool IsTriggerLight(Actor a) => ClassDefaults.IsA(a, "SBase.STriggerLight");

    /// <summary>A trigger light's state when the round starts: STriggerLight.PostBeginPlay turns it off (LT_None)
    /// unless bInitialyOn.</summary>
    public static bool StartsOn(Actor light) => ClassDefaults.Flag(light, "bInitialyOn") && HasLightType(light);

    /// <summary>Whether the light's placed type gives light: LightOn and LightToggle bring back the placed type, so a
    /// light placed with LT_None stays dark.</summary>
    public static bool HasLightType(Actor light) => ClassDefaults.Number(light, "LightType", 1) != 0;

    /// <summary>Plays <paramref name="activations"/> of the SMagicEvent <paramref name="sourceId"/> and every relay
    /// they reach.</summary>
    public static EventPreview Build(MapProject p, GameplayGraph graph, string sourceId, IReadOnlyList<MagicActivation> activations)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(activations);

        // Each round plays every relay with the activations the round before delivered to it, until they settle; a
        // relay keeps its own state across its activations as the game's does.
        var played = new Dictionary<string, List<MagicActivation>>(StringComparer.OrdinalIgnoreCase) { [sourceId] = [.. activations] };
        var deliveries = new List<EventDelivery>();
        for (int round = 0; round <= MaxRelays; round++)
        {
            deliveries.Clear();
            var next = new Dictionary<string, List<MagicActivation>>(StringComparer.OrdinalIgnoreCase) { [sourceId] = [.. activations] };
            foreach ((string relayId, List<MagicActivation> acts) in played)
            {
                if (p.FindActor(relayId) is not { } relay)
                {
                    continue;
                }

                foreach (MagicFiring f in MagicEvents.Simulate(MagicEvents.Read(relay), acts))
                {
                    foreach (string receiver in MagicEvents.Receivers(graph, f.Event))
                    {
                        deliveries.Add(new EventDelivery(f.Time, receiver, f.Event, f.Trigger, relayId));
                        if (p.FindActor(receiver) is { } r && MagicEvents.IsMagicEvent(r))
                        {
                            if (!next.TryGetValue(receiver, out List<MagicActivation>? list))
                            {
                                next[receiver] = list = [];
                            }

                            list.Add(new MagicActivation(f.Time, f.Trigger));
                        }
                    }
                }
            }

            bool settled = next.Count == played.Count &&
                next.All(kv => played.TryGetValue(kv.Key, out List<MagicActivation>? before) && before.SequenceEqual(kv.Value));
            played = next;
            if (settled)
            {
                break;
            }
        }

        var start = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors.Where(IsTriggerLight))
        {
            start[a.Id] = StartsOn(a);
        }

        // Stable, so the deliveries of one moment keep the order the game sends them in.
        return new EventPreview(p, [.. deliveries.OrderBy(d => d.Time)], start);
    }

    /// <summary>The state at <paramref name="time"/>: the deliveries up to it played on the trigger lights and movers.</summary>
    public EventState At(double time)
    {
        List<EventDelivery> delivered = [.. Deliveries.TakeWhile(d => d.Time <= time)];
        var lights = new Dictionary<string, bool>(StartLights, StringComparer.OrdinalIgnoreCase);
        var movers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (EventDelivery d in delivered)
        {
            if (_project.FindActor(d.Receiver) is not { } a)
            {
                continue;
            }

            if (lights.TryGetValue(d.Receiver, out bool on))
            {
                // TriggerMethode: 0 TriggerToggle, 1 TriggerControl, 2 UnTriggerOnly (no answer to a trigger).
                lights[d.Receiver] = d.Trigger && ClassDefaults.Number(a, "TriggerMethode", 0) switch
                {
                    0 => !on && HasLightType(a),
                    1 => HasLightType(a),
                    _ => on,
                };
            }
            else if (d.Trigger && MoverBase.IsMover(a))
            {
                movers[d.Receiver] = d.Time;
            }
        }

        return new EventState(delivered, lights, movers);
    }
}
