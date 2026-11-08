using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Core.Engine;
using Actor = OpenScct.Editor.Core.Model.Actor;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>One mover key: its offset from the mover's base (UE2 <c>KeyPos[n]</c> in world axes, <c>KeyRot[n]</c>
/// added to <c>BaseRot</c> component by component).</summary>
public readonly record struct MoverKey(Vec3 Offset, Rot Turn);

/// <summary>Where a mover is drawn at a moment: its location and rotation.</summary>
public readonly record struct MoverPose(Vec3 Location, Rot Rotation);

/// <summary>A mover's <c>InitialState</c>: how it answers triggers, bumps and players standing on it.</summary>
/// <param name="Name">The state name the game reads.</param>
/// <param name="Title">A short plain name for menus.</param>
/// <param name="Description">What the mover does in that state, in plain words.</param>
/// <param name="Cycle">How the preview plays one activation.</param>
public sealed record MoverState(string Name, string Title, string Description, MoverCycle Cycle);

/// <summary>The shape of one activation of a mover state, for the timeline and the preview.</summary>
public enum MoverCycle
{
    /// <summary>Opens, waits StayOpenTime, closes.</summary>
    OpenWaitClose,

    /// <summary>Opens on one trigger and closes on the next (or when released).</summary>
    OpenThenClose,

    /// <summary>Opens, waits OtherTime, closes, waits StayOpenTime, and repeats while triggered.</summary>
    Pound,

    /// <summary>Opens once and stays open.</summary>
    OpenOnce,

    /// <summary>Starts open: closes, waits StayOpenTime, opens.</summary>
    CloseWaitOpen,

    /// <summary>Starts open: closes once and stays closed.</summary>
    CloseOnce,
}

/// <summary>
/// The mover workbench's data (spec/devices/mover.md): a mover travels from <c>BasePos + KeyPos[n]</c> to the next key,
/// each leg taking <c>MoveTime</c>, at constant speed (<c>MV_MoveByTime</c>) or eased with 3a^2 - 2a^3
/// (<c>MV_GlideByTime</c>), unless the map gives a leg its own time or a key its own easing (<see cref="Legs"/>,
/// OpenSCCT's spec/additions/mover-legs.md). "Open" is toward the last key (<c>NumKeys - 1</c>), "closed" is key 0, and it starts the
/// game at <c>KeyNum</c>. OpenSCCT paths grow independently of the original game's eight fixed keys.
/// </summary>
public sealed class MoverSetup
{
    /// <summary>The native path resource guard; original-game exports keep OriginalMaxKeys.</summary>
    public const int MaxKeys = MoverPath.MaxKeys;
    public const int OriginalMaxKeys = MoverK.MaxKeys;

    public static readonly IReadOnlyList<MoverState> States =
    [
        new("TriggerOpenTimed", "Trigger, open for a time", "Opens when triggered, stays open for the stay time, then closes.", MoverCycle.OpenWaitClose),
        new("BumpOpenTimed", "Bump, open for a time", "Opens when a player bumps it, stays open for the stay time, then closes.", MoverCycle.OpenWaitClose),
        new("StandOpenTimed", "Stand on, open for a time", "Opens when someone stands on it, stays open for the stay time, then closes (lifts).", MoverCycle.OpenWaitClose),
        new("TriggerToggle", "Trigger toggles", "Each trigger opens it, or closes it when it is open.", MoverCycle.OpenThenClose),
        new("TriggerControl", "Open while triggered", "Opens while triggered and closes when the last trigger is released.", MoverCycle.OpenThenClose),
        new("TriggerPound", "Pound while triggered", "Keeps opening and closing while triggered (open for the other time, closed for the stay time).", MoverCycle.Pound),
        new("BumpButton", "Button", "A button: opens when bumped and stays open until the trigger it fired is done.", MoverCycle.OpenThenClose),
        new("TriggerOpenOnceOnly", "Trigger, open once", "Opens once when triggered and stays open for the rest of the round.", MoverCycle.OpenOnce),
        new("TriggerCloseTimed", "Trigger, close for a time", "Starts open (start key 1): closes when triggered, stays closed for the stay time, then opens.", MoverCycle.CloseWaitOpen),
        new("TriggerCloseOnceOnly", "Trigger, close once", "Starts open (start key 1): closes once when triggered and stays closed.", MoverCycle.CloseOnce),
    ];

    public required string Id { get; init; }

    /// <summary>The keys, NumKeys long (at least one).</summary>
    public required IReadOnlyList<MoverKey> Keys { get; init; }

    /// <summary>The key the mover starts the game at (<c>KeyNum</c>).</summary>
    public int StartKey { get; init; }

    public Vec3 BasePos { get; init; }

    public Rot BaseRot { get; init; }

    /// <summary>Seconds per leg (<c>MoveTime</c>; one time for every leg, as the game reads it).</summary>
    public double MoveTime { get; init; }

    /// <summary>Seconds held open (<c>StayOpenTime</c>), or held closed in the close-timed states.</summary>
    public double StayOpenTime { get; init; }

    /// <summary>Seconds before opening (<c>DelayTime</c>).</summary>
    public double DelayTime { get; init; }

    /// <summary>Seconds before closing (<c>DelayTimeLastKey</c>), or before the first leg in the states that start by
    /// closing.</summary>
    public double DelayTimeLastKey { get; init; }

    /// <summary>Seconds held open by TriggerPound (<c>OtherTime</c>).</summary>
    public double OtherTime { get; init; }

    /// <summary>True for <c>MV_GlideByTime</c> (eased), false for <c>MV_MoveByTime</c> (constant speed).</summary>
    public bool Eased { get; init; }

    /// <summary>The timed and pound states stop after one activation (<c>bTriggerOnceOnly</c>).</summary>
    public bool TriggerOnceOnly { get; init; }

    /// <summary>The mover's own time per leg and easing per key, or null for <see cref="MoveTime"/> and
    /// <see cref="Eased"/> everywhere.</summary>
    public MoverLegs? Legs { get; init; }

    public required string State { get; init; }

    public required string Tag { get; init; }

    public int OpenKey => Keys.Count - 1;

    public MoverState? StateInfo => States.FirstOrDefault(s => string.Equals(s.Name, State, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads a mover's keys and timing, stored values first, then the class defaults.</summary>
    public static MoverSetup Read(Actor a)
    {
        SortedDictionary<int, SvJsonNode> pos = Elements(a.Props["KeyPos"]);
        SortedDictionary<int, SvJsonNode> rot = Elements(a.Props["KeyRot"]);
        int count = Math.Clamp((int)ClassDefaults.Number(a, "NumKeys", 2), 1, MaxKeys);
        var keys = new List<MoverKey>(count);
        for (int i = 0; i < count; i++)
        {
            Vec3 offset = pos.TryGetValue(i, out SvJsonNode? p) && PropValues.TryVector(p, out Vec3 v) ? v : Vec3.Zero;
            Rot turn = rot.TryGetValue(i, out SvJsonNode? r) && PropValues.TryRotator(r, out Rot t) ? t : Rot.Zero;
            keys.Add(new MoverKey(offset, turn));
        }

        return new MoverSetup
        {
            Id = a.Id,
            Keys = keys,
            StartKey = Math.Clamp((int)ClassDefaults.Number(a, "KeyNum", 0), 0, count - 1),
            BasePos = PropValues.TryVector(a.Props["BasePos"], out Vec3 b) ? b : a.Location,
            BaseRot = PropValues.TryRotator(a.Props["BaseRot"], out Rot br) ? br : a.Rotation,
            MoveTime = ClassDefaults.Number(a, "MoveTime", 1),
            StayOpenTime = ClassDefaults.Number(a, "StayOpenTime", 4),
            DelayTime = ClassDefaults.Number(a, "DelayTime", 0),
            DelayTimeLastKey = ClassDefaults.Number(a, "DelayTimeLastKey", 0),
            OtherTime = ClassDefaults.Number(a, "OtherTime", 0),
            Eased = ClassDefaults.Number(a, "MoverGlideType", 1) != 0,
            TriggerOnceOnly = a.Props.GetBool("bTriggerOnceOnly"),
            Legs = a.Legs,
            State = a.Props.GetString("InitialState") ?? (ClassDefaults.Catalog.Default(a.Class, "InitialState") as SvJsonString)?.Value ?? "BumpOpenTimed",
            Tag = a.Tag,
        };
    }

    /// <summary>
    /// What will surprise a map maker in the game: the game's open always heads for key 1 and its close for the key
    /// below, so a mover that does not start at key 0 (key 1 for the states that start open) moves in ways the key
    /// list does not suggest, and TriggerCloseTimed does nothing unless it starts at key 1 (mover.md rules 6, 7, 31).
    /// </summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        if (Keys.Count < 2)
        {
            problems.Add("It has one key, so it cannot move.");
            return problems;
        }

        MoverCycle cycle = StateInfo?.Cycle ?? MoverCycle.OpenWaitClose;
        bool startsOpen = cycle is MoverCycle.CloseWaitOpen or MoverCycle.CloseOnce;
        if (startsOpen && StartKey != 1)
        {
            problems.Add(string.Equals(State, "TriggerCloseTimed", StringComparison.OrdinalIgnoreCase)
                ? $"It starts at key {StartKey}: TriggerCloseTimed only moves when it starts at key 1, so it never moves."
                : $"It starts at key {StartKey}: closing heads for key {Math.Max(0, StartKey - 1)} first; the stock movers in this state start at key 1.");
        }
        else if (!startsOpen && StartKey != 0)
        {
            problems.Add($"It starts at key {StartKey}: opening always heads for key 1 first, so it {(StartKey == 1 ? "does not move when opened" : "travels down to key 0 when opened")}.");
        }

        return problems;
    }

    /// <summary>The seconds of a leg from key <paramref name="from"/> to <paramref name="to"/>: the later key's own time
    /// for neighbouring keys, else <see cref="MoveTime"/>.</summary>
    public double LegTime(int from, int to) => Legs?.LegTime(from, to) ?? MoveTime;

    /// <summary>True when the mover eases in and out at key <paramref name="key"/>.</summary>
    public bool SmoothAt(int key) => Legs?.SmoothAt(key, Eased) ?? Eased;

    /// <summary>The world pose of key <paramref name="index"/>.</summary>
    public MoverPose KeyPose(int index)
    {
        MoverKey k = Keys[Math.Clamp(index, 0, Keys.Count - 1)];
        return new MoverPose(BasePos + k.Offset, new Rot(BaseRot.Pitch + k.Turn.Pitch, BaseRot.Yaw + k.Turn.Yaw, BaseRot.Roll + k.Turn.Roll));
    }

    /// <summary>The pose on the leg from key <paramref name="from"/> to <paramref name="to"/> at <paramref name="alpha"/>
    /// (0..1 of the leg's time), eased at the smooth ends as the game moves it. The rotation is truncated toward zero,
    /// as the game's step is.</summary>
    public MoverPose Between(int from, int to, double alpha)
    {
        double s = MoverLegs.Ease(Math.Clamp(alpha, 0, 1), SmoothAt(from), SmoothAt(to));
        MoverPose p = KeyPose(from), q = KeyPose(to);
        return new MoverPose(p.Location + (q.Location - p.Location) * s,
            new Rot(p.Rotation.Pitch + (int)((q.Rotation.Pitch - p.Rotation.Pitch) * s),
                p.Rotation.Yaw + (int)((q.Rotation.Yaw - p.Rotation.Yaw) * s),
                p.Rotation.Roll + (int)((q.Rotation.Roll - p.Rotation.Roll) * s)));
    }

    /// <summary>The items of a static array property by index, in its plain-array or <c>$elements</c> form.</summary>
    public static SortedDictionary<int, SvJsonNode> Elements(SvJsonNode? node)
    {
        var items = new SortedDictionary<int, SvJsonNode>();
        switch (node)
        {
            case SvJsonArray arr:
                int i = 0;
                foreach (SvJsonNode item in arr)
                {
                    items[i++] = item;
                }

                break;
            case SvJsonObject o when o["$elements"] is SvJsonObject e:
                foreach (KeyValuePair<string, SvJsonNode> kv in e)
                {
                    if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    {
                        items[index] = kv.Value;
                    }
                }

                break;
        }

        return items;
    }
}

/// <summary>A stretch of a mover's timeline: a leg between two keys, or a wait at one.</summary>
/// <param name="Start">Seconds after the activation.</param>
/// <param name="End">Seconds after the activation.</param>
/// <param name="From">The key it leaves (the key it waits at, for a wait).</param>
/// <param name="To">The key it reaches (equal to From for a wait, or for a leg to the key it is already at, which the
/// game still times).</param>
/// <param name="Label">What happens, in words ("delay", "open", "stay open", "next trigger"...).</param>
public sealed record MoverStep(double Start, double End, int From, int To, string Label)
{
    public bool IsWait => From == To;
}

/// <summary>
/// One activation of a mover played as the game plays it (spec/devices/mover.md rules 1 to 7 and 28 to 37). Opening
/// always heads for key 1 (<c>DoOpen</c>) and closing for the key below the current one (<c>DoClose</c>); each key
/// reached then carries on through the keys in the direction of travel (<c>KeyFrameReached</c>), so a mover that does
/// not start at key 0 can stop at once or travel down while "opening". States that wait for another trigger hold for
/// one second, marked "next trigger".
/// </summary>
public sealed class MoverTimeline
{
    private const double NextTriggerHold = 1;

    private MoverTimeline(MoverSetup mover, List<MoverStep> steps, double? openAt)
    {
        Mover = mover;
        Steps = steps;
        OpenAt = openAt;
    }

    public MoverSetup Mover { get; }

    public IReadOnlyList<MoverStep> Steps { get; }

    public double Duration => Steps.Count == 0 ? 0 : Steps[^1].End;

    /// <summary>Seconds into the activation at which the mover is fully open (the end of its open motion, or 0 for
    /// the states that start open); null when the activation never opens it.</summary>
    public double? OpenAt { get; }

    /// <summary>The seconds after the activation at which each key is first reached (null for keys never reached).</summary>
    public IReadOnlyList<double?> KeyTimes()
    {
        var times = new double?[Mover.Keys.Count];
        times[Mover.StartKey] = 0;
        foreach (MoverStep s in Steps.Where(s => !s.IsWait && s.To < times.Length))
        {
            times[s.To] ??= s.End;
        }

        return times;
    }

    public static MoverTimeline Build(MoverSetup m)
    {
        var steps = new List<MoverStep>();
        double t = 0;
        int at = m.StartKey;
        double? openAt = null;

        void Wait(double seconds, string label)
        {
            if (seconds > 0)
            {
                steps.Add(new MoverStep(t, t + seconds, at, at, label));
                t += seconds;
            }
        }

        // InterpolateTo and the KeyFrameReached chain (rules 1 and 4): legs of MoveTime (or the leg's own time) until a
        // key ends the travel.
        void Travel(int first, string label)
        {
            int previous = at, target = Math.Clamp(first, 0, MoverSetup.MaxKeys - 1);
            while (true)
            {
                double leg = m.LegTime(at, target);
                steps.Add(new MoverStep(t, t + leg, at, target, target == at ? $"{label} (already at key {at})" : label));
                t += leg;
                previous = at;
                at = target;
                if (0 < at && at < previous)
                {
                    target = at - 1;
                }
                else if (previous < at && at < m.Keys.Count - 1)
                {
                    target = at + 1;
                }
                else
                {
                    return;
                }
            }
        }

        void Open(string label = "open")
        {
            Travel(1, label);
            openAt ??= t;
        }

        void Close(string label = "close") => Travel(Math.Max(0, at - 1), label);

        bool closeTimed = string.Equals(m.State, "TriggerCloseTimed", StringComparison.OrdinalIgnoreCase);
        switch (m.StateInfo?.Cycle ?? MoverCycle.OpenWaitClose)
        {
            case MoverCycle.OpenWaitClose:
                Wait(m.DelayTime, "delay");
                Open();
                Wait(m.StayOpenTime, "stay open");
                if (!m.TriggerOnceOnly)
                {
                    Wait(m.DelayTimeLastKey, "delay before closing");
                    Close();
                }

                break;
            case MoverCycle.OpenThenClose when string.Equals(m.State, "TriggerToggle", StringComparison.OrdinalIgnoreCase) && at != 0:
                // A toggle at rest away from key 0 closes on the first trigger (rule 32).
                Wait(m.DelayTimeLastKey, "delay");
                Close();
                Wait(NextTriggerHold, "next trigger");
                Wait(m.DelayTime, "delay");
                Open();
                break;
            case MoverCycle.OpenThenClose:
                Wait(m.DelayTime, "delay");
                Open();
                Wait(NextTriggerHold, "next trigger");
                Wait(m.DelayTimeLastKey, "delay");
                Close();
                break;
            case MoverCycle.Pound:
                for (int round = 0; round < (m.TriggerOnceOnly ? 1 : 2); round++)
                {
                    Wait(m.DelayTime, "delay");
                    Open();
                    Wait(m.OtherTime, "stay open");
                    Wait(m.DelayTimeLastKey, "delay");
                    Close();
                    Wait(m.StayOpenTime, "stay closed");
                }

                break;
            case MoverCycle.OpenOnce:
                Wait(m.DelayTime, "delay");
                Open("open (stays open)");
                break;
            case MoverCycle.CloseWaitOpen:
                // TriggerCloseTimed gives up unless it rests at key 1 (rule 31).
                if (closeTimed && at != 1)
                {
                    break;
                }

                openAt = 0;
                Wait(m.DelayTimeLastKey, "delay");
                Close();
                Wait(m.StayOpenTime, "stay closed");
                if (!m.TriggerOnceOnly)
                {
                    Wait(m.DelayTime, "delay");
                    Open();
                }

                break;
            case MoverCycle.CloseOnce:
                openAt = 0;
                Wait(m.DelayTimeLastKey, "delay");
                Close("close (stays closed)");
                break;
        }

        return new MoverTimeline(m, steps, openAt);
    }

    /// <summary>The mover's pose <paramref name="seconds"/> after the activation (held at the end afterwards).</summary>
    public MoverPose PoseAt(double seconds)
    {
        foreach (MoverStep s in Steps)
        {
            if (seconds < s.End)
            {
                return s.IsWait ? Mover.KeyPose(s.From) : Mover.Between(s.From, s.To, (seconds - s.Start) / (s.End - s.Start));
            }
        }

        return Mover.KeyPose(Steps.Count > 0 ? Steps[^1].To : Mover.StartKey);
    }
}

/// <summary>The actors that move a mover (senders whose events reach its Tag), the alarms that lock it
/// (<c>MoversToLock</c>: they stop it answering, they never move it), and the per-sender overrides it carries
/// (<c>SpecialBehaviors</c>).</summary>
public sealed record MoverTriggers(IReadOnlyList<GameplayLink> Senders, IReadOnlyList<GameplayLink> Locks,
    IReadOnlyList<(string Sender, string State, double MoveTime)> Overrides)
{
    public static MoverTriggers Of(MapProject p, GameplayGraph graph, Actor mover)
    {
        List<GameplayLink> senders = graph.In(mover.Id).Where(l => l.Kind == LinkKind.Fire).ToList();
        List<GameplayLink> locks = graph.In(mover.Id).Where(l => l.Kind != LinkKind.Fire && l.Via.Contains("MoverToLock", StringComparison.OrdinalIgnoreCase)).ToList();
        var overrides = new List<(string, string, double)>();
        if (mover.Props["SpecialBehaviors"] is SvJsonArray rows)
        {
            foreach (SvJsonObject row in rows.OfType<SvJsonObject>())
            {
                overrides.Add((PropValues.ActorId(row["EventFromActor"]) ?? "(none)", (row["InitialState"] as SvJsonString)?.Value ?? "",
                    PropValues.Num(row["MoveTime"])));
            }
        }

        return new MoverTriggers(senders, locks, overrides);
    }
}

/// <summary>
/// Undoable mover edits, one undo step each. Key edits keep <c>BasePos</c>/<c>BaseRot</c> and put the mover where the
/// game starts it (<c>BasePos + KeyPos[KeyNum]</c>), so what the views show is what the round starts with.
/// </summary>
public static class MoverEdits
{
    /// <summary>Replaces the native path stops. The start key becomes <paramref name="startKey"/> when given, else it is
    /// kept, clamped to the new count. The keys' own timing stays with the key numbers unless
    /// <paramref name="legs"/> rearranges it; it is cut to the new count.</summary>
    public static ReplaceObjectsCommand SetKeys(MapProject p, string id, IReadOnlyList<MoverKey> keys, string name = "Edit mover keys", int? startKey = null,
        Func<List<MoverKeyTiming>, List<MoverKeyTiming>>? legs = null)
    {
        if (keys.Count is < 1 or > MoverSetup.MaxKeys)
        {
            throw new ArgumentException($"a mover has 1 to {MoverSetup.MaxKeys} keys (OpenSCCT path resource limit)");
        }

        return Edit(p, id, name, a =>
        {
            int start = startKey ?? (int)ClassDefaults.Number(a, "KeyNum", 0);
            WriteKeys(a, keys);
            WriteStartKey(a, Math.Clamp(start, 0, keys.Count - 1));
            if (a.Legs is not null)
            {
                List<MoverKeyTiming> timing = [.. a.Legs.Keys];
                a.Legs = MoverTiming.Fit(legs?.Invoke(timing) ?? timing, keys.Count);
            }
        });
    }

    /// <summary>Adds a key after the last one: at <paramref name="at"/> (a world point) when given, else one leg
    /// further along the last leg (or 128 units up for a one-key mover).</summary>
    public static ReplaceObjectsCommand AddKey(MapProject p, string id, Vec3? at = null)
    {
        MoverSetup m = Read(p, id);
        if (m.Keys.Count >= MoverSetup.MaxKeys)
        {
            throw new ArgumentException($"this mover already has {MoverSetup.MaxKeys} keys, the most the game can hold");
        }

        MoverKey last = m.Keys[^1];
        MoverKey next = at is { } point
            ? new MoverKey(GeEdits.Round(point - m.BasePos), last.Turn)
            : m.Keys.Count >= 2
                ? new MoverKey(last.Offset + (last.Offset - m.Keys[^2].Offset), new Rot(2 * last.Turn.Pitch - m.Keys[^2].Turn.Pitch,
                    2 * last.Turn.Yaw - m.Keys[^2].Turn.Yaw, 2 * last.Turn.Roll - m.Keys[^2].Turn.Roll))
                : new MoverKey(last.Offset + new Vec3(0, 0, 128), last.Turn);
        return SetKeys(p, id, [.. m.Keys, next], $"Add key {m.Keys.Count} to {id}");
    }

    public static ReplaceObjectsCommand InsertKey(MapProject p, string id, int after, Vec3? at = null)
    {
        var m = Read(p, id); CheckIndex(m, after);
        var keys = m.Keys.ToList();
        var next = at is { } point ? m.Keys[after] with { Offset = point - m.BasePos } : m.Keys[after];
        keys.Insert(after + 1, next);
        return SetKeys(p, id, keys, "Insert mover stop", m.StartKey > after ? m.StartKey + 1 : m.StartKey, legs: timings =>
        {
            while (timings.Count < after + 1) timings.Add(default);
            timings.Insert(after + 1, default); return timings;
        });
    }

    /// <summary>Removes a key; the later keys move down one and the start key stays on the same key (on the key that
    /// takes the deleted one's place when the start key itself goes).</summary>
    public static ReplaceObjectsCommand DeleteKey(MapProject p, string id, int index)
    {
        MoverSetup m = Read(p, id);
        CheckIndex(m, index);
        if (m.Keys.Count == 1)
        {
            throw new ArgumentException("a mover keeps at least one key");
        }

        var keys = m.Keys.ToList();
        keys.RemoveAt(index);
        return SetKeys(p, id, keys, $"Delete key {index} of {id}", m.StartKey > index ? m.StartKey - 1 : m.StartKey, legs: timing =>
        {
            if (index < timing.Count)
            {
                timing.RemoveAt(index);
            }

            return timing;
        });
    }

    /// <summary>Swaps a key with its neighbour (<paramref name="by"/> -1 earlier, +1 later); the start key follows its key.</summary>
    public static ReplaceObjectsCommand MoveKey(MapProject p, string id, int index, int by)
    {
        MoverSetup m = Read(p, id);
        CheckIndex(m, index);
        int other = index + Math.Sign(by);
        CheckIndex(m, other);
        var keys = m.Keys.ToList();
        (keys[index], keys[other]) = (keys[other], keys[index]);
        int start = m.StartKey == index ? other : m.StartKey == other ? index : m.StartKey;
        // A key's own time and easing go with it (a time that lands on key 0 has no leg left and is dropped).
        return SetKeys(p, id, keys, $"Reorder keys of {id}", start, legs: timing =>
        {
            while (timing.Count <= Math.Max(index, other))
            {
                timing.Add(default);
            }

            (timing[index], timing[other]) = (timing[other], timing[index]);
            return timing;
        });
    }

    /// <summary>Sets one key's offset and turn.</summary>
    public static ReplaceObjectsCommand SetKey(MapProject p, string id, int index, MoverKey key)
    {
        MoverSetup m = Read(p, id);
        CheckIndex(m, index);
        var keys = m.Keys.ToList();
        keys[index] = key;
        return SetKeys(p, id, keys, $"Edit key {index} of {id}");
    }

    /// <summary>Makes the mover start the game at key <paramref name="index"/> (and draws it there).</summary>
    public static ReplaceObjectsCommand SetStartKey(MapProject p, string id, int index)
    {
        CheckIndex(Read(p, id), index);
        return Edit(p, id, $"Start {id} at key {index}", a => WriteStartKey(a, index), keepShown: false);
    }

    /// <summary>
    /// Draws the mover at key <paramref name="index"/> in the map (the start key, the base and the keys stay as they
    /// are), as the stock editor does when a key is picked: moving or turning the mover then places that key
    /// (<see cref="MoverBase.Follow"/>). Showing the start key puts it back where the round starts.
    /// </summary>
    public static ReplaceObjectsCommand ShowKey(MapProject p, string id, int index)
    {
        MoverSetup m = Read(p, id);
        CheckIndex(m, index);
        MoverPose pose = m.KeyPose(index);
        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], index == m.StartKey ? $"Show {id} where it starts" : $"Show {id} at key {index}", actor: a =>
        {
            a.Location = pose.Location;
            a.Rotation = pose.Rotation;
            a.Editor.MoverKey = index == m.StartKey ? null : index;
        }, followBase: false);
    }

    /// <summary>Explicitly edit any stop in the viewport, including the round's starting stop.</summary>
    public static ReplaceObjectsCommand EditKey(MapProject p, string id, int index)
    {
        var m = Read(p, id); CheckIndex(m, index); var pose = m.KeyPose(index);
        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], "Edit mover stop " + (index + 1), actor: a =>
        { a.Location = pose.Location; a.Rotation = pose.Rotation; a.Editor.MoverKey = index; }, followBase: false);
    }

    /// <summary>The key a mover is drawn at, when that is not its start key (it is being placed in the views).</summary>
    public static int? ShownKey(Actor a) => MoverBase.ShownKey(a);

    /// <summary>Sets a timing value in seconds (<c>MoveTime</c>, <c>StayOpenTime</c>, <c>DelayTime</c>,
    /// <c>DelayTimeLastKey</c> or <c>OtherTime</c>).</summary>
    /// <param name="mergeKey">Consecutive edits with the same key are one undo step (a number box's steps).</param>
    public static ReplaceObjectsCommand SetTime(MapProject p, string id, string prop, double seconds, string? mergeKey = null)
    {
        if (prop is not ("MoveTime" or "StayOpenTime" or "DelayTime" or "DelayTimeLastKey" or "OtherTime"))
        {
            throw new ArgumentException($"'{prop}' is not a mover time (MoveTime, StayOpenTime, DelayTime, DelayTimeLastKey, OtherTime)");
        }

        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentException("times are seconds, 0 or more");
        }

        return Edit(p, id, $"Set {prop} of {id}", a => a.Props[prop] = SvJsonNode.Float((float)seconds), mergeKey);
    }

    /// <summary>
    /// Gives the leg between key <paramref name="key"/> - 1 and <paramref name="key"/> its own time in seconds (either
    /// way), or null for the mover's MoveTime. An OpenSCCT setting (SVMap 1.2): the original game runs every leg in
    /// MoveTime.
    /// </summary>
    /// <param name="mergeKey">Consecutive edits with the same key are one undo step (a number box's steps).</param>
    public static ReplaceObjectsCommand SetLegTime(MapProject p, string id, int key, double? seconds, string? mergeKey = null)
    {
        CheckIndex(Read(p, id), key);
        if (key == 0 && seconds is not null)
        {
            throw new ArgumentException("key 0 has no leg before it; give the time to the key the leg arrives at (1 or more)");
        }

        if (seconds is { } t && !MoverTiming.IsLegTime(t))
        {
            throw new ArgumentException($"a leg takes {MoverTiming.ShortestLeg} to {MoverTiming.LongestLeg} seconds");
        }

        string name = seconds is { } time
            ? string.Create(CultureInfo.InvariantCulture, $"Leg to key {key} of {id}: {time:0.###} s")
            : $"Leg to key {key} of {id}: mover's time";
        return Edit(p, id, name, a => a.Legs = MoverTiming.With(a.Legs, key, k => k with { Time = seconds is { } v ? (float)v : null }), mergeKey);
    }

    /// <summary>Eases the mover in and out at key <paramref name="key"/> (true), moves through it at speed (false), or
    /// leaves it to the mover's glide (null). An OpenSCCT setting (SVMap 1.2).</summary>
    public static ReplaceObjectsCommand SetKeySmooth(MapProject p, string id, int key, bool? smooth)
    {
        CheckIndex(Read(p, id), key);
        string how = smooth switch { true => "smooth", false => "at speed", null => "mover's glide" };
        return Edit(p, id, $"Key {key} of {id}: {how}", a => a.Legs = MoverTiming.With(a.Legs, key, k => k with { Smooth = smooth }));
    }

    /// <summary>Constant speed (false) or eased in and out (true).</summary>
    public static ReplaceObjectsCommand SetEased(MapProject p, string id, bool eased) =>
        Edit(p, id, eased ? $"Ease {id}" : $"Constant speed for {id}", a => a.Props["MoverGlideType"] = SvJsonNode.Integer(eased ? 1 : 0));

    /// <summary>Sets the mover's <c>InitialState</c> (one of <see cref="MoverSetup.States"/>). Switching between the
    /// states that start closed (key 0) and those that start open (key 1) moves the start key along, since
    /// TriggerCloseTimed only works from key 1.</summary>
    public static ReplaceObjectsCommand SetState(MapProject p, string id, string state)
    {
        MoverState s = MoverSetup.States.FirstOrDefault(x => string.Equals(x.Name, state, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"unknown mover state '{state}' ({string.Join(", ", MoverSetup.States.Select(x => x.Name))})");
        MoverSetup m = Read(p, id);
        bool startsOpen = s.Cycle is MoverCycle.CloseWaitOpen or MoverCycle.CloseOnce;
        int start = m.Keys.Count < 2 ? m.StartKey
            : startsOpen && m.StartKey == 0 ? 1
            : !startsOpen && m.StartKey == 1 && m.StateInfo?.Cycle is MoverCycle.CloseWaitOpen or MoverCycle.CloseOnce ? 0
            : m.StartKey;
        return Edit(p, id, $"{id}: {s.Title}", a =>
        {
            a.Props["InitialState"] = SvJsonNode.String(s.Name);
            WriteStartKey(a, start);
        });
    }

    public static MoverSetup Read(MapProject p, string id) =>
        p.FindActor(id) is { } a && MoverBase.IsMover(a) ? MoverSetup.Read(a) : throw new ArgumentException($"'{id}' is not a mover");

    /// <summary>Writes KeyPos, KeyRot and NumKeys as the converter does (only keys that differ from zero), stores the
    /// base when the mover had none, and puts the mover at its start key.</summary>
    private static void WriteKeys(Actor a, IReadOnlyList<MoverKey> keys)
    {
        MoverSetup before = MoverSetup.Read(a);
        a.Props["BasePos"] ??= PropValues.Vector(before.BasePos);
        a.Props["BaseRot"] ??= PropValues.Rotator(before.BaseRot);
        a.Props["KeyPos"] = Sparse(keys.Select(k => k.Offset == Vec3.Zero ? null : PropValues.Vector(k.Offset)));
        a.Props["KeyRot"] = Sparse(keys.Select(k => k.Turn == Rot.Zero ? null : PropValues.Rotator(k.Turn)));
        bool isDefault = ClassDefaults.Catalog.Default(a.Class, "NumKeys") is SvJsonNumber n && PropValues.Num(n) == keys.Count;
        a.Props["NumKeys"] = isDefault ? null : SvJsonNode.Integer(keys.Count);
    }

    /// <summary>Stores the start key (<c>KeyNum</c>), leaving the class default 0 unstored.</summary>
    private static void WriteStartKey(Actor a, int index) => a.Props["KeyNum"] = index == 0 ? null : SvJsonNode.Integer(index);

    /// <summary>A static array in the <c>$elements</c> form with the non-null items, or null when every item is null.</summary>
    private static SvJsonNode? Sparse(IEnumerable<SvJsonNode?> items)
    {
        var elements = new SvJsonObject();
        int i = 0;
        foreach (SvJsonNode? item in items)
        {
            if (item is not null)
            {
                elements.Add(i.ToString(CultureInfo.InvariantCulture), item);
            }

            i++;
        }

        if (elements.Count == 0)
        {
            return null;
        }

        var o = new SvJsonObject();
        o.Add("$elements", elements);
        return o;
    }

    /// <summary>An edit of one mover that ends with the mover at its start key's pose (the base never moves).</summary>
    private static ReplaceObjectsCommand Edit(MapProject p, string id, string name, Action<Actor> edit, string? mergeKey = null, bool keepShown = true)
    {
        if (p.FindActor(id) is not { } mover || !MoverBase.IsMover(mover))
        {
            throw new ArgumentException($"'{id}' is not a mover");
        }

        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], name, actor: a =>
        {
            int? shown = keepShown ? MoverBase.ShownKey(a) : null;
            edit(a);
            MoverSetup m = MoverSetup.Read(a);
            int draw = shown is { } key ? Math.Clamp(key, 0, m.Keys.Count - 1) : m.StartKey;
            MoverPose start = m.KeyPose(draw);
            a.Location = start.Location;
            a.Rotation = start.Rotation;
            a.Editor.MoverKey = shown is null ? null : draw;
        }, mergeKey: mergeKey, followBase: false);
    }

    private static void CheckIndex(MoverSetup m, int index)
    {
        if (index < 0 || index >= m.Keys.Count)
        {
            throw new ArgumentException($"no key {index} (this mover has keys 0 to {m.Keys.Count - 1})");
        }
    }
}

/// <summary>Helpers for a mover's own per-key timing (<see cref="MoverLegs"/>) as the editor stores it.</summary>
public static class MoverTiming
{
    public const double ShortestLeg = MoverK.MinLegTime;

    public const double LongestLeg = OpenScct.Content.MoversDoc.LongestLeg;

    public static bool IsLegTime(double seconds) => seconds is >= ShortestLeg and <= LongestLeg;

    /// <summary>The timing with trailing keys that set nothing left out; null when no key sets anything.</summary>
    public static MoverLegs? OrNull(IReadOnlyList<MoverKeyTiming> keys)
    {
        int count = keys.Count;
        while (count > 0 && keys[count - 1].IsDefault)
        {
            count--;
        }

        return count == 0 ? null : new MoverLegs([.. keys.Take(count)]);
    }

    /// <summary>The timing cut to <paramref name="keyCount"/> keys, with no time on key 0.</summary>
    public static MoverLegs? Fit(IReadOnlyList<MoverKeyTiming> keys, int keyCount)
    {
        List<MoverKeyTiming> kept = [.. keys.Take(Math.Min(keyCount, MoverPath.MaxKeys))];
        if (kept.Count > 0)
        {
            kept[0] = kept[0] with { Time = null };
        }

        return OrNull(kept);
    }

    /// <summary>The timing with key <paramref name="key"/> changed.</summary>
    public static MoverLegs? With(MoverLegs? legs, int key, Func<MoverKeyTiming, MoverKeyTiming> change)
    {
        List<MoverKeyTiming> keys = legs is null ? [] : [.. legs.Keys];
        while (keys.Count <= key)
        {
            keys.Add(default);
        }

        keys[key] = change(keys[key]);
        return OrNull(keys);
    }
}
