using System.Runtime.CompilerServices;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Core.Uc;
using OpenScct.Game.AI;
using OpenScct.Game.Objectives;
using OpenScct.Game.SBase;
using OpenScct.Physics.Collision;
using Actor = OpenScct.Core.Engine.Actor;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>What a mover is to someone getting about: a door (it closes a way) or a lift (it carries you up or down).</summary>
public enum MoverKind
{
    Door,
    Lift,
}

/// <summary>How a mover comes to move.</summary>
public enum MoverWorks
{
    /// <summary>Walking into it or standing on it moves it (BumpOpenTimed, BumpButton, StandOpenTimed).</summary>
    Walk,

    /// <summary>An event with its Tag moves it: one of its <see cref="MapMover.Openers"/> must be reached.</summary>
    Trigger,

    /// <summary>It starts open and only closes (TriggerClose states): it never blocks.</summary>
    StartsOpen,

    /// <summary>A trigger state with nothing in the map that sends it a trigger: it never moves.</summary>
    Never,

    /// <summary>A lift's landing or cabin door (<see cref="SLift"/>): the lift opens it at its floor, so it works when the
    /// lift does (<see cref="MapMover.Lift"/>).</summary>
    ByLift,
}

/// <summary>How an opener is worked: used through its Use menu entry, or touched (a touch trigger, a presence sensor).</summary>
public enum OpenerKind
{
    Use,
    Touch,
}

/// <summary>An actor whose first use or touch sends a trigger that reaches a mover.</summary>
public sealed record MoverOpener(string Id, Actor Actor, OpenerKind How);

/// <summary>
/// A mover of the map as reachability sees it: a door (its closed place blocks the edges through it) or a lift (its
/// platform top at each key, where it takes on and lets off riders), how it is worked and what opens it.
/// </summary>
public sealed class MapMover
{
    internal MapMover(string id, Mover actor, MoverKind kind, FVector min, FVector max, MoverWorks works, IReadOnlyList<MoverOpener> openers, string? lift = null)
    {
        Id = id;
        Actor = actor;
        Kind = kind;
        Min = min;
        Max = max;
        Works = works;
        Openers = openers;
        Lift = lift;
    }

    public string Id { get; }

    public Mover Actor { get; }

    public MoverKind Kind { get; }

    /// <summary>Its collision bounds where it starts.</summary>
    public FVector Min { get; }

    public FVector Max { get; }

    public MoverWorks Works { get; }

    public IReadOnlyList<MoverOpener> Openers { get; }

    /// <summary>The lift whose door this is (<see cref="MoverWorks.ByLift"/>), or null.</summary>
    public string? Lift { get; }

    /// <summary>The offset of key <paramref name="k"/> from where the mover starts.</summary>
    public FVector KeyOffset(int k) => Actor.KeyPos[k] - Actor.KeyPos[Math.Min(Actor.KeyNum, (byte)(Keys - 1))];

    public int Keys => Math.Clamp((int)Actor.NumKeys, 1, Actor.KeyPos.Length);
}

/// <summary>
/// What a side made of a mover: <paramref name="Used"/> when the side could work it and got to it (a door it reached and
/// could pass, a lift it could board), and what opened it when walking up to it does not.
/// </summary>
public sealed record MoverUse(string Id, MoverKind Kind, MoverWorks Works, bool Used, string? By);

/// <summary>
/// The doors and lifts of a map and the search that takes them into account: a closed door blocks the edges through it
/// unless something the side reaches opens it (a switch it can use by the game's own use test, a touch trigger or
/// presence sensor that detects it, or walking into the door); a lift joins the floors beside its platform at each key
/// once it can be worked. The game's own lift (<see cref="SLift"/>) is worked by its call switches and opens its landing
/// and cabin doors itself. The search runs again each time a newly reached switch opens something.
/// </summary>
public static class MoverReach
{
    /// <summary>A mover key this far up or down from another makes it a lift, when it is wide enough to stand on and
    /// has room to stand on its top.</summary>
    public const float LiftRise = 64f;

    /// <summary>A platform narrower than this either way (a shutter, a sliding panel) carries nobody.</summary>
    private const float LiftMinWidth = 2 * AiK.PawnRadius;

    /// <summary>How far from a lift's platform a floor at the platform's height counts as getting on and off it.</summary>
    private const float BoardingReach = AiK.PawnRadius + 48f;

    /// <summary>How far above or below the platform top a boarding floor may be (a step).</summary>
    private const float BoardingRise = 40f;

    /// <summary>The height over the floor the door lines are cast at (a walker's waist).</summary>
    private const float Waist = 40f;

    private static readonly ConditionalWeakTable<AnalysisMap, IReadOnlyList<MapMover>> s_movers = new();
    private static readonly ConditionalWeakTable<NavGraph, Csr> s_csr = new();
    private static readonly ConditionalWeakTable<NavGraph, Dictionary<string, int[]>> s_blocked = new();
    private static readonly ConditionalWeakTable<NavGraph, Dictionary<string, int[][]>> s_boarding = new();

    /// <summary>The doors and lifts of the map (call under <see cref="AnalysisMap.Gate"/>).</summary>
    public static IReadOnlyList<MapMover> Movers(AnalysisMap map) => s_movers.GetValue(map, Find);

    private static List<MapMover> Find(AnalysisMap map)
    {
        var bounds = new Dictionary<Mover, (FVector Min, FVector Max)>(ReferenceEqualityComparer.Instance);
        CollisionWorld world = map.Collision;
        for (int i = 0; i < world.ColliderCount; i++)
        {
            ColliderInfo c = world.GetCollider(i);
            if (c.Actor is Mover m)
            {
                bounds[m] = bounds.TryGetValue(m, out var b) ? (Min(b.Min, c.Min), Max(b.Max, c.Max)) : (c.Min, c.Max);
            }
        }

        var events = new MapEvents(map);
        var openers = new Dictionary<Mover, List<MoverOpener>>(ReferenceEqualityComparer.Instance);
        foreach ((string id, Actor a) in events.Actors)
        {
            if (OpenerKindOf(a) is not { } how || MapEvents.FirstUse(a) is not ({ Count: > 0 } names, bool untrigger))
            {
                continue;
            }

            foreach ((_, Actor receiver, bool un) in events.Deliver(names, untrigger))
            {
                if (receiver is not Mover m || un)
                {
                    continue;
                }

                if (!openers.TryGetValue(m, out List<MoverOpener>? list))
                {
                    openers[m] = list = [];
                }

                if (list.All(o => o.Id != id))
                {
                    list.Add(new MoverOpener(id, a, how));
                }
            }
        }

        // The game's lifts: worked by their call switches, and they open their own landing and cabin doors.
        var ids = new Dictionary<Actor, string>(ReferenceEqualityComparer.Instance);
        foreach ((string id, Actor a) in events.Actors)
        {
            ids[a] = id;
        }

        var liftOf = new Dictionary<Mover, string>(ReferenceEqualityComparer.Instance);
        var callers = new Dictionary<Mover, List<MoverOpener>>(ReferenceEqualityComparer.Instance);
        foreach ((string id, Actor a) in events.Actors)
        {
            if (a is not SLift lift)
            {
                continue;
            }

            callers[lift] = [.. lift.DoorsAndSwitches.SelectMany(k => k.Switches).OfType<SInterrupteur>().Where(ids.ContainsKey).Distinct()
                .Select(sw => new MoverOpener(ids[sw], sw, OpenerKind.Use))];
            foreach (Mover door in lift.DoorsAndSwitches.SelectMany(k => k.Doors).Concat(lift.LiftDoors).OfType<Mover>())
            {
                liftOf.TryAdd(door, id);
            }
        }

        var movers = new List<MapMover>();
        foreach ((string id, Actor a) in events.Actors)
        {
            if (a is not Mover m || !bounds.TryGetValue(m, out var box))
            {
                continue;
            }

            if (m is SLift)
            {
                List<MoverOpener> calls = callers[m];
                movers.Add(new MapMover(id, m, MoverKind.Lift, box.Min, box.Max, calls.Count > 0 ? MoverWorks.Trigger : MoverWorks.Never, calls));
                continue;
            }

            if (liftOf.TryGetValue(m, out string? liftId))
            {
                movers.Add(new MapMover(id, m, MoverKind.Door, box.Min, box.Max, MoverWorks.ByLift, [], liftId));
                continue;
            }

            IReadOnlyList<MoverOpener> who = openers.GetValueOrDefault(m) ?? [];
            string state = m.InitialState.ToString();
            MoverWorks works = state switch
            {
                "BumpOpenTimed" or "BumpButton" or "StandOpenTimed" => MoverWorks.Walk,
                _ when state.StartsWith("TriggerClose", StringComparison.Ordinal) => MoverWorks.StartsOpen,
                _ when state.StartsWith("Trigger", StringComparison.Ordinal) => who.Count > 0 ? MoverWorks.Trigger : MoverWorks.Never,
                _ => MoverWorks.Walk,
            };
            var mover = new MapMover(id, m, MoverKind.Door, box.Min, box.Max, works, who);
            bool rises = Enumerable.Range(0, mover.Keys).Any(k => MathF.Abs(mover.KeyOffset(k).Z) >= LiftRise);
            bool wide = box.Max.X - box.Min.X >= LiftMinWidth && box.Max.Y - box.Min.Y >= LiftMinWidth;
            movers.Add(rises && wide && RoomOnTop(map, mover) ? new MapMover(id, m, MoverKind.Lift, box.Min, box.Max, works, who) : mover);
        }

        return movers;
    }

    /// <summary>
    /// True when a pawn has room to stand on the mover's top at one of its keys: a lift platform has, a thick door that
    /// rises out of the way into its lintel or ceiling has not.
    /// </summary>
    private static bool RoomOnTop(AnalysisMap map, MapMover m)
    {
        var centre = new FVector((m.Min.X + m.Max.X) * 0.5f, (m.Min.Y + m.Max.Y) * 0.5f, m.Max.Z);
        var headroom = new FVector(0f, 0f, 2 * AiK.StandingHeight);
        for (int k = 0; k < m.Keys; k++)
        {
            FVector top = centre + m.KeyOffset(k) + new FVector(0f, 0f, 2f);
            if (!map.Collision.LineCheck(out _, m.Actor, top + headroom, top, default, ETraceFlags.TRACE_World))
            {
                return true;
            }
        }

        return false;
    }

    private static OpenerKind? OpenerKindOf(Actor a) => a switch
    {
        SBasicGamePlayObject { InteractionObj: not null } => OpenerKind.Use,
        Trigger or SPresenceTrigger => OpenerKind.Touch,
        _ => null,
    };

    private static FVector Min(FVector a, FVector b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));

    private static FVector Max(FVector a, FVector b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));

    // ---- the search ------------------------------------------------------------------------------------------

    /// <summary>
    /// The side's reach from <paramref name="sources"/>, with its doors and lifts: a search, then again while a switch,
    /// trigger or sensor it reached opens a door or works a lift it could not before. <paramref name="opened"/> is a
    /// door or lift taken as working whatever opens it (to ask what it would let the side reach). Call under
    /// <see cref="AnalysisMap.Gate"/>.
    /// </summary>
    public static Explored Explore(AnalysisMap map, NavGraph graph, ReachProfile profile, IReadOnlyList<int> sources, string? opened = null)
    {
        IReadOnlyList<MapMover> movers = Movers(map);
        var working = new HashSet<string>(movers.Where(m => m.Works is MoverWorks.Walk or MoverWorks.StartsOpen || m.Id == opened).Select(m => m.Id));
        var by = new Dictionary<string, string>();
        Csr csr = s_csr.GetValue(graph, g => new Csr(g));
        while (true)
        {
            // A lift's doors open with it.
            foreach (MapMover d in movers)
            {
                if (d.Lift is { } lift && working.Contains(lift) && working.Add(d.Id))
                {
                    by[d.Id] = lift;
                }
            }

            var blocked = new HashSet<int>();
            var rides = new Dictionary<int, List<(int To, float Cost, int Ride)>>();
            for (int i = 0; i < movers.Count; i++)
            {
                MapMover m = movers[i];
                if (m.Kind == MoverKind.Door && !working.Contains(m.Id))
                {
                    blocked.UnionWith(BlockedEdges(map, graph, m));
                }
                else if (m.Kind == MoverKind.Lift && working.Contains(m.Id))
                {
                    AddRides(map, graph, m, i, rides);
                }
            }

            Explored result = csr.Search(sources, blocked, rides);
            bool more = false;
            foreach (MapMover m in movers.Where(m => m.Works == MoverWorks.Trigger && !working.Contains(m.Id)))
            {
                if (m.Openers.FirstOrDefault(o => Reaches(map, graph, result, profile, o)) is { } opener)
                {
                    working.Add(m.Id);
                    by[m.Id] = opener.Id;
                    more = true;
                }
            }

            if (!more)
            {
                result.Movers = movers;
                result.Uses = movers.Select(m => new MoverUse(m.Id, m.Kind, m.Works, working.Contains(m.Id) && Met(map, graph, csr, result, m),
                    by.GetValueOrDefault(m.Id))).ToList();
                return result;
            }
        }
    }

    /// <summary>True when the side got to the mover: a door with a reached floor on one of the edges it stands across, a
    /// lift with a reached floor beside its platform.</summary>
    private static bool Met(AnalysisMap map, NavGraph graph, Csr csr, Explored reach, MapMover m) => m.Kind == MoverKind.Lift
        ? Boarding(graph, m).Any(floors => floors.Any(reach.Reached))
        : BlockedEdges(map, graph, m).Any(e => reach.Reached(csr.From(e)));

    /// <summary>True when a floor the side reached can work <paramref name="opener"/>: use it by the game's use test, or
    /// stand in its touch cylinder when it detects the side.</summary>
    private static bool Reaches(AnalysisMap map, NavGraph graph, Explored reach, ReachProfile profile, MoverOpener opener) => opener.How switch
    {
        OpenerKind.Use => opener.Actor is SBasicGamePlayObject { InteractionObj: { } entry } && UseTest.Usable(graph, reach, profile, entry),
        _ => Detects(opener.Actor, profile) && Touches(graph, reach, opener.Actor),
    };

    /// <summary>Whether a touch trigger or presence sensor fires for a pawn of the side.</summary>
    private static bool Detects(Actor sensor, ReachProfile profile) => sensor switch
    {
        SPresenceTrigger p => IsA(profile.PawnClass, p.DetectedClass),
        Trigger t => t.TriggerType switch
        {
            ETriggerType.TT_PlayerProximity or ETriggerType.TT_PawnProximity or ETriggerType.TT_AnyProximity or ETriggerType.TT_HumanPlayerProximity => true,
            ETriggerType.TT_ClassProximity => IsA(profile.PawnClass, t.ClassProximityType),
            _ => false,
        },
        _ => false,
    };

    private static bool IsA(UcClassInfo? type, ClassRef wanted)
    {
        for (; type is not null; type = type.Super)
        {
            if (string.Equals(type.Path, wanted.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A reached floor where a standing pawn's cylinder overlaps the sensor's.</summary>
    private static bool Touches(NavGraph graph, Explored reach, Actor sensor)
    {
        var near = new List<int>();
        graph.NodesNear(sensor.Location - new FVector(0f, 0f, 100f), sensor.CollisionRadius + AiK.PawnRadius, near);
        return near.Any(n => reach.Reached(n) &&
            MathF.Abs(graph.Node(n).Floor.Z + AiK.StandingHeight - sensor.Location.Z) <= sensor.CollisionHeight + AiK.StandingHeight);
    }

    /// <summary>
    /// The doors and lifts that stop a side where it got to: shut doors standing across an edge from a floor it reached
    /// to one it did not, and lifts it could not work with a reached floor beside them.
    /// </summary>
    public static IEnumerable<MapMover> Stopping(AnalysisMap map, NavGraph graph, Explored reach)
    {
        ArgumentNullException.ThrowIfNull(reach);
        Csr csr = s_csr.GetValue(graph, g => new Csr(g));
        var working = reach.Uses.Where(u => u.Used || u.Works is MoverWorks.Walk or MoverWorks.StartsOpen || u.By is not null)
            .Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        foreach (MapMover m in reach.Movers.Where(m => !working.Contains(m.Id)))
        {
            bool stops = m.Kind == MoverKind.Lift
                ? Boarding(graph, m).Any(floors => floors.Any(reach.Reached))
                : BlockedEdges(map, graph, m).Any(e => !reach.Reached(csr.To[e]) && reach.Reached(csr.From(e)));
            if (stops)
            {
                yield return m;
            }
        }
    }

    /// <summary>The kind and link (-1 for none) of the graph's edge <paramref name="edge"/>.</summary>
    public static (NavEdgeKind Kind, int Link) Edge(NavGraph graph, int edge)
    {
        Csr csr = s_csr.GetValue(graph, g => new Csr(g));
        return (csr.Kind[edge], csr.Link[edge]);
    }

    /// <summary>The edges the doors among <paramref name="movers"/> stand across when shut, by edge index.</summary>
    public static Dictionary<int, string> DoorEdges(AnalysisMap map, NavGraph graph, IReadOnlyList<MapMover> movers)
    {
        var edges = new Dictionary<int, string>();
        foreach (MapMover door in movers.Where(m => m.Kind == MoverKind.Door))
        {
            foreach (int e in BlockedEdges(map, graph, door))
            {
                edges.TryAdd(e, door.Id);
            }
        }

        return edges;
    }

    /// <summary>The edges a closed door stands across: those whose line at waist height hits it.</summary>
    private static int[] BlockedEdges(AnalysisMap map, NavGraph graph, MapMover door)
    {
        Dictionary<string, int[]> cache = s_blocked.GetValue(graph, _ => new Dictionary<string, int[]>(StringComparer.Ordinal));
        if (cache.TryGetValue(door.Id, out int[]? known))
        {
            return known;
        }

        Csr csr = s_csr.GetValue(graph, g => new Csr(g));
        FVector centre = (door.Min + door.Max) * 0.5f;
        float reach = new FVector(door.Max.X - door.Min.X, door.Max.Y - door.Min.Y, 0f).Size() * 0.5f + 2 * graph.Profile.Spacing;
        var near = new List<int>();
        graph.NodesNear(new FVector(centre.X, centre.Y, door.Min.Z), reach, near);
        var waist = new FVector(0f, 0f, Waist);
        var blocked = new List<int>();
        foreach (int n in near)
        {
            for (int e = csr.Start[n]; e < csr.Start[n + 1]; e++)
            {
                FVector from = graph.Node(n).Floor + waist, to = graph.Node(csr.To[e]).Floor + waist;
                if (map.Collision.LineCheck(out FCheckResult hit, null, to, from, default, ETraceFlags.TRACE_Movers) && ReferenceEquals(hit.Actor, door.Actor))
                {
                    blocked.Add(e);
                }
            }
        }

        return cache[door.Id] = [.. blocked];
    }

    /// <summary>Joins the floors beside a lift's platform at each key to those at every other key (a ride costs its rise).</summary>
    private static void AddRides(AnalysisMap map, NavGraph graph, MapMover lift, int index, Dictionary<int, List<(int To, float Cost, int Ride)>> rides)
    {
        int[][] boarding = Boarding(graph, lift);
        for (int a = 0; a < boarding.Length; a++)
        {
            for (int b = 0; b < boarding.Length; b++)
            {
                if (a == b || boarding[b].Length == 0)
                {
                    continue;
                }

                float rise = MathF.Abs(lift.KeyOffset(b).Z - lift.KeyOffset(a).Z);
                foreach (int from in boarding[a])
                {
                    if (!rides.TryGetValue(from, out List<(int, float, int)>? list))
                    {
                        rides[from] = list = [];
                    }

                    // One floor of the far landing is enough: the walk graph spreads from it.
                    list.Add((boarding[b][0], rise, index));
                }
            }
        }
    }

    /// <summary>
    /// Per key, the floors beside or on the lift at that key's height: a platform is boarded on its top, the game's lift
    /// (<see cref="SLift"/>) is a cabin, boarded on its floor, which lies somewhere up its walls (its box runs from under
    /// the floor to the roof).
    /// </summary>
    public static int[][] Boarding(NavGraph graph, MapMover lift)
    {
        Dictionary<string, int[][]> cache = s_boarding.GetValue(graph, _ => new Dictionary<string, int[][]>(StringComparer.Ordinal));
        if (cache.TryGetValue(lift.Id, out int[][]? known))
        {
            return known;
        }

        bool cabin = lift.Actor is SLift;
        var result = new int[lift.Keys][];
        var near = new List<int>();
        for (int k = 0; k < lift.Keys; k++)
        {
            FVector offset = lift.KeyOffset(k);
            FVector min = lift.Min + offset, max = lift.Max + offset;
            (float low, float high) = cabin ? (min.Z - BoardingRise, max.Z - AiK.StandingHeight) : (max.Z - BoardingRise, max.Z + BoardingRise);
            var centre = new FVector((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (low + high) * 0.5f);
            float reach = new FVector(max.X - min.X, max.Y - min.Y, 0f).Size() * 0.5f + BoardingReach;
            graph.NodesNear(centre, reach, near);
            result[k] = near.Where(n =>
            {
                FVector f = graph.Node(n).Floor;
                return f.Z >= low && f.Z <= high && f.X >= min.X - BoardingReach && f.X <= max.X + BoardingReach && f.Y >= min.Y - BoardingReach &&
                    f.Y <= max.Y + BoardingReach;
            }).OrderBy(n => (graph.Node(n).Floor - centre).SizeSquared()).ToArray();
        }

        return cache[lift.Id] = result;
    }

    /// <summary>A search's result: the cost of each node and how it was reached.</summary>
    public sealed class Explored
    {
        internal Explored(float[] cost, int[] previous, int[] via)
        {
            Cost = cost;
            Previous = previous;
            Via = via;
        }

        /// <summary>Path cost from the nearest source, per node (infinity when unreached).</summary>
        public float[] Cost { get; }

        /// <summary>The node each node was reached from (-1 for sources and unreached nodes).</summary>
        public int[] Previous { get; }

        /// <summary>How each node was reached: a graph edge index, or <c>-2 - m</c> for a ride on mover <c>m</c> of <see cref="Movers"/>.</summary>
        public int[] Via { get; }

        public IReadOnlyList<MapMover> Movers { get; internal set; } = [];

        /// <summary>What the side made of each door and lift.</summary>
        public IReadOnlyList<MoverUse> Uses { get; internal set; } = [];

        public bool Reached(int node) => node >= 0 && !float.IsPositiveInfinity(Cost[node]);
    }

    /// <summary>The graph's edges in arrays (by node, in the graph's own edge order, so indices match its edge indices).</summary>
    internal sealed class Csr
    {
        public Csr(NavGraph graph)
        {
            Start = new int[graph.NodeCount + 1];
            var to = new List<int>(graph.EdgeCount);
            var cost = new List<float>(graph.EdgeCount);
            var kind = new List<NavEdgeKind>(graph.EdgeCount);
            var link = new List<int>(graph.EdgeCount);
            for (int n = 0; n < graph.NodeCount; n++)
            {
                Start[n] = to.Count;
                foreach ((int t, NavEdgeKind k, float c, int l) in graph.EdgesOf(n))
                {
                    to.Add(t);
                    cost.Add(c);
                    kind.Add(k);
                    link.Add(l);
                }
            }

            Start[graph.NodeCount] = to.Count;
            To = [.. to];
            Cost = [.. cost];
            Kind = [.. kind];
            Link = [.. link];
        }

        public int[] Start { get; }

        public int[] To { get; }

        public float[] Cost { get; }

        public NavEdgeKind[] Kind { get; }

        public int[] Link { get; }

        /// <summary>The node edge <paramref name="edge"/> leaves: the last whose edges start at or before it.</summary>
        public int From(int edge)
        {
            int n = Array.BinarySearch(Start, edge);
            n = n < 0 ? ~n - 1 : n;
            while (n + 1 < Start.Length - 1 && Start[n + 1] <= edge)
            {
                n++;
            }

            return n;
        }

        /// <summary>Dijkstra from every source over the edges not <paramref name="blocked"/>, plus the lift rides.</summary>
        public Explored Search(IReadOnlyList<int> sources, HashSet<int> blocked, Dictionary<int, List<(int To, float Cost, int Ride)>> rides)
        {
            int count = Start.Length - 1;
            var cost = new float[count];
            Array.Fill(cost, float.PositiveInfinity);
            var previous = new int[count];
            Array.Fill(previous, -1);
            var via = new int[count];
            Array.Fill(via, -1);
            var open = new PriorityQueue<int, float>();
            foreach (int s in sources.Where(s => s >= 0))
            {
                cost[s] = 0f;
                open.Enqueue(s, 0f);
            }

            while (open.TryDequeue(out int n, out float c))
            {
                if (c > cost[n])
                {
                    continue;
                }

                for (int e = Start[n]; e < Start[n + 1]; e++)
                {
                    if (blocked.Contains(e))
                    {
                        continue;
                    }

                    Relax(n, To[e], c + Cost[e], e);
                }

                if (rides.TryGetValue(n, out List<(int To, float Cost, int Ride)>? out_))
                {
                    foreach ((int t, float rc, int ride) in out_)
                    {
                        Relax(n, t, c + rc, -2 - ride);
                    }
                }
            }

            return new Explored(cost, previous, via);

            void Relax(int from, int to, float total, int how)
            {
                if (total < cost[to])
                {
                    cost[to] = total;
                    previous[to] = from;
                    via[to] = how;
                    open.Enqueue(to, total);
                }
            }
        }
    }
}

/// <summary>
/// The game's own use test from the floors a side reaches: a pawn standing or crouched on the floor would be allowed to
/// use the entry by the server's reach rule (<see cref="InteractionReach"/>), the entry's UsableBy and its in-front side.
/// </summary>
public static class UseTest
{
    /// <summary>True when a floor the side reached can use <paramref name="entry"/>.</summary>
    public static bool Usable(NavGraph graph, MoverReach.Explored reach, ReachProfile profile, SInteractObject entry) =>
        UsableFrom(graph, reach.Reached, profile, entry) >= 0;

    /// <summary>The nearest floor (to the entry) passing <paramref name="reached"/> that can use the entry, or -1.</summary>
    public static int UsableFrom(NavGraph graph, Func<int, bool> reached, ReachProfile profile, SInteractObject entry)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(entry);
        if (!ObjectiveNatives.UsableByAllows(entry.UsableBy, profile.PawnType))
        {
            return -1;
        }

        float radius = MathF.Max(entry.CollisionRadius, entry.ItRadius) + AiK.PawnRadius;
        var near = new List<int>();
        graph.NodesNear(entry.Location - new FVector(0f, 0f, 100f), radius, near);
        foreach (int n in near.Where(reached).OrderBy(n => (graph.Node(n).Floor - entry.Location).SizeSquared()))
        {
            FVector floor = graph.Node(n).Floor;
            if (entry.bInteractInFront && !entry.IsOnFrontSide(floor))
            {
                continue;
            }

            foreach (float half in new[] { AiK.StandingHeight, profile.CrouchHeight })
            {
                if (InteractionReach.From(entry, null, floor + new FVector(0f, 0f, half), AiK.PawnRadius, half))
                {
                    return n;
                }
            }
        }

        return -1;
    }
}
