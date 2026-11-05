using OpenScct.Content;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.AI;
using OpenScct.Game.SBase;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>A side's reach: its graph, the cost to every node from the side's player starts (infinity when unreached) and
/// what it made of the map's doors and lifts.</summary>
public sealed class ReachMap
{
    private List<(MapMover, MoverReach.Explored)>? _openings;

    internal ReachMap(ReachProfile profile, byte mask, NavGraph graph, MoverReach.Explored explored, IReadOnlyList<(string Id, int Node)> starts)
    {
        Profile = profile;
        Mask = mask;
        Graph = graph;
        Explored = explored;
        Starts = starts;
    }

    /// <summary>The search behind the reach: costs, how each node was reached, the doors and lifts.</summary>
    public MoverReach.Explored Explored { get; }

    /// <summary>The doors the side opened or found shut, and the lifts it rode or could not work.</summary>
    public IReadOnlyList<MoverUse> Movers => Explored.Uses;

    public ReachProfile Profile { get; }

    /// <summary>The GE activation mask the graph was built with.</summary>
    public byte Mask { get; }

    public NavGraph Graph { get; }

    /// <summary>Path cost from the nearest start, per node (<see cref="float.PositiveInfinity"/> when no start reaches it).</summary>
    public float[] Cost => Explored.Cost;

    /// <summary>The side's enabled player starts and the node each stands on (-1 when it stands on none).</summary>
    public IReadOnlyList<(string Id, int Node)> Starts { get; }

    public bool Reached(int node) => node >= 0 && !float.IsPositiveInfinity(Cost[node]);

    public int ReachedCount => Cost.Count(c => !float.IsPositiveInfinity(c));

    /// <summary>
    /// True when the side can use <paramref name="a"/> from a floor it reaches: by the game's own use test when it has a
    /// Use menu entry, else from a reached floor within <see cref="Reachability.UseReach"/> across.
    /// </summary>
    public bool CanUse(OpenScct.Core.Engine.Actor a) => CanUse(a, Reached);

    /// <summary>As <see cref="CanUse(OpenScct.Core.Engine.Actor)"/>, from the floors <paramref name="reached"/> passes.</summary>
    public bool CanUse(OpenScct.Core.Engine.Actor a, Func<int, bool> reached)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a is SBasicGamePlayObject { InteractionObj: { } entry })
        {
            return UseTest.UsableFrom(Graph, reached, Profile, entry) >= 0;
        }

        // From 220 below to 20 above (a switch on a wall, an item on the floor).
        var near = new List<int>();
        Graph.NodesNear(a.Location - new FVector(0f, 0f, 100f), Reachability.UseReach, near);
        return near.Any(reached);
    }

    /// <summary>
    /// Each shut door and unworkable lift that stops the side where it got to, with the side's reach were that one
    /// open (to name the ones that keep it from a place). Worked out once. Call under <see cref="AnalysisMap.Gate"/>.
    /// </summary>
    public IReadOnlyList<(MapMover Mover, MoverReach.Explored Reach)> Openings(AnalysisMap map)
    {
        _openings ??= [.. MoverReach.Stopping(map, Graph, Explored)
            .Select(m => (m, MoverReach.Explore(map, Graph, Profile, Starts.Select(s => s.Node).ToList(), opened: m.Id)))];
        return _openings;
    }
}

/// <summary>One leg of a route: how it is travelled (walk, crouch, drop, a GE move in words, "lift" or "door"), where it
/// starts and ends, and what it uses (a GE, or the mover it rides or passes and what opened it).</summary>
public sealed record RouteLeg(string How, Vec3 From, Vec3 To, double Length, string? Ge, float Drop, float Damage)
{
    /// <summary>The lift ridden or the door passed through.</summary>
    public string? Mover { get; init; }

    /// <summary>What opened the door or worked the lift, when walking into it does not.</summary>
    public string? OpenedBy { get; init; }

    public bool IsWalk => How is "walk" or "crouch";

    /// <summary>The leg in words: how, then the GE or mover it uses and what opened it.</summary>
    public string Label => How + ((Ge ?? Mover) is { } what ? " " + what : "") + (OpenedBy is { } by ? $" (opened by {by})" : "");
}

/// <summary>The answer to "can this side get from A to B". <paramref name="Damage"/> is the health its drops cost in
/// all; a route whose drops add up to a fatal fall is not reachable, and keeps its legs to show why.</summary>
public sealed record RouteResult(bool Reachable, string? Problem, IReadOnlyList<RouteLeg> Legs, double Length, float Cost, Vec3? From, Vec3? To,
    Vec3? Closest)
{
    public float Damage => Legs.Sum(l => l.Damage);
}

/// <summary>
/// Reach maps over OpenSCCT's own navigation (<see cref="NavBuilder"/> on the compiled collision, the GEs active under
/// the activation mask) with the map's doors and lifts (<see cref="MoverReach"/>): which floors each side reaches from
/// its starts, what objectives and switches are out of reach (by the game's own use test), where only spies go, and
/// routes between two points. A closed door blocks unless something the side reaches opens it; a lift carries the side
/// once it can work it.
/// </summary>
public static class Reachability
{
    /// <summary>How far across from an objective or switch without a Use menu entry a reached floor must be to count
    /// as reaching it (those with an entry take the game's own use test).</summary>
    public const float UseReach = 160f;

    public const string ObjectiveCode = "reach.objective";
    public const string UseSpotCode = "reach.use-spot";
    public const string StartOffFloorCode = "reach.start-off-floor";

    /// <summary>The finding codes of <see cref="Findings"/>.</summary>
    public static readonly IReadOnlyList<string> Codes = [ObjectiveCode, UseSpotCode, StartOffFloorCode];

    /// <summary>The reach of <paramref name="profile"/> from its side's enabled player starts. Call under <see cref="AnalysisMap.Gate"/>.</summary>
    public static ReachMap FromStarts(AnalysisMap map, ReachProfile profile, byte mask)
    {
        NavGraph graph = map.Graph(profile, mask);
        var starts = new List<(string Id, int Node)>();
        foreach ((string id, PlayerStart start) in StartsOf(map, profile.Side))
        {
            // As the bots find a start's floor: up to 200 under its centre. A start's own collision height (80) is not
            // the pawn's, and stock starts stand with their feet below the floor they spawn on.
            starts.Add((id, graph.NearestNode(start.Location, 0f)));
        }

        MoverReach.Explored explored = MoverReach.Explore(map, graph, profile, starts.Select(s => s.Node).ToList());
        return new ReachMap(profile, mask, graph, explored, starts);
    }

    /// <summary>The enabled player starts of a side, by actor id.</summary>
    public static IEnumerable<(string Id, PlayerStart Start)> StartsOf(AnalysisMap map, TeamSide side)
    {
        byte team = ReachProfile.Team(side);
        foreach (ActorRecord record in map.Loaded.Gameplay.Actors)
        {
            if (map.Loaded.ActorOf(record) is PlayerStart { bEnabled: true } s && s.TeamNumber == team)
            {
                yield return (record.Id, s);
            }
        }
    }

    /// <summary>
    /// Problems the two reach maps show: objectives spies cannot use from where they get, switches and supplies neither
    /// side can use, and starts that stand on no floor. On a map that changes as it plays (walls an event breaks, GE
    /// triggers) the unreached objects are notes, not warnings: the check reads the map as a round starts.
    /// </summary>
    public static List<Finding> Findings(AnalysisMap map, ReachMap spy, ReachMap merc)
    {
        var found = new List<Finding>();
        foreach (ReachMap side in new[] { spy, merc })
        {
            foreach ((string id, int node) in side.Starts.Where(s => s.Node < 0))
            {
                found.Add(Finding.Warning(StartOffFloorCode, $"{id} ({Name(side)} start) has no floor of the {Name(side)} graph within 200 under it",
                    ObjectRef.Actor(id)));
            }
        }

        List<string> changes = Changes(map);
        Severity unreached = changes.Count > 0 ? Severity.Info : Severity.Warning;
        string caveat = changes.Count > 0
            ? $" (the map changes as it plays, which this check does not follow: {string.Join(", ", changes.Take(4))}{(changes.Count > 4 ? "..." : "")})"
            : "";
        foreach (ActorRecord record in map.Loaded.Gameplay.Actors)
        {
            OpenScct.Core.Engine.Actor? a = map.Loaded.ActorOf(record);
            if (a is SObjectiveTrigger && !spy.CanUse(a))
            {
                found.Add(new Finding(unreached, ObjectiveCode, $"{record.Id} ({a.GetType().Name}): spies cannot use it from anywhere they get from their starts" +
                    Through(map, spy, a) + caveat, [ObjectRef.Actor(record.Id)], AnalysisMap.V(a.Location)));
            }
            else if (a is SInterrupteur or SAmmoStock or SInfirmary && !spy.CanUse(a) && !merc.CanUse(a))
            {
                found.Add(new Finding(unreached, UseSpotCode, $"{record.Id} ({a.GetType().Name}): neither side can use it from anywhere they get from their starts" +
                    Through(map, spy, a) + Through(map, merc, a) + caveat, [ObjectRef.Actor(record.Id)], AnalysisMap.V(a.Location)));
            }
        }

        return found;
    }

    /// <summary>The shut doors and unworkable lifts that alone keep the side from using <paramref name="a"/>, as a note.</summary>
    private static string Through(AnalysisMap map, ReachMap side, OpenScct.Core.Engine.Actor a)
    {
        List<string> keys = [.. side.Openings(map).Where(o => side.CanUse(a, o.Reach.Reached)).Select(o => Label(o.Mover))];
        return keys.Count == 0 ? "" : $"; {(side.Profile.Side == TeamSide.Spy ? "spies" : "mercs")} get to it once {Either(keys)} opens";
    }

    private static string Label(MapMover m) => m.Id + (m.Kind == MoverKind.Lift ? " (lift)" : " (door)");

    /// <summary>"A", "A or B", "A, B or C".</summary>
    private static string Either(List<string> names) => names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];

    /// <summary>What changes the map's ways as it plays: blocking objects an event breaks, and GE triggers.</summary>
    private static List<string> Changes(AnalysisMap map)
    {
        var changes = new List<string>();
        foreach (ActorRecord record in map.Loaded.Gameplay.Actors)
        {
            switch (map.Loaded.ActorOf(record))
            {
                case SGamePlayObject { bBlockPlayers: true, DestroyOnEventFrom.Count: > 0 }:
                    changes.Add($"{record.Id} breaks on an event");
                    break;
                case SGETrigger:
                    changes.Add($"{record.Id} switches GEs");
                    break;
            }
        }

        return changes;
    }

    /// <summary>
    /// The floors spies reach that mercs do not (with the profiles given), grouped into connected areas, largest first:
    /// (centre, nodes, area in square units, one node of it).
    /// </summary>
    public static List<SpyOnlyArea> SpyOnlyAreas(ReachMap spy, ReachMap merc)
    {
        bool[] only = SpyOnly(spy, merc);
        var seen = new bool[only.Length];
        var areas = new List<SpyOnlyArea>();
        double cell = spy.Graph.Profile.Spacing * (double)spy.Graph.Profile.Spacing;
        for (int i = 0; i < only.Length; i++)
        {
            if (!only[i] || seen[i])
            {
                continue;
            }

            var members = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            seen[i] = true;
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                members.Add(n);
                foreach ((int to, NavEdgeKind kind, _, _) in spy.Graph.EdgesOf(n))
                {
                    if (kind is NavEdgeKind.Walk or NavEdgeKind.Crouch && only[to] && !seen[to])
                    {
                        seen[to] = true;
                        queue.Enqueue(to);
                    }
                }
            }

            Vec3 sum = members.Aggregate(Vec3.Zero, (s, n) => s + AnalysisMap.V(spy.Graph.Node(n).Floor));
            areas.Add(new SpyOnlyArea(sum / members.Count, members.Count, members.Count * cell, members[0]));
        }

        return areas.OrderByDescending(a => a.Nodes).ToList();
    }

    /// <summary>Per spy node: reached by spies, and no merc node at the same floor is reached by mercs.</summary>
    public static bool[] SpyOnly(ReachMap spy, ReachMap merc)
    {
        var only = new bool[spy.Graph.NodeCount];
        for (int i = 0; i < only.Length; i++)
        {
            if (!spy.Reached(i))
            {
                continue;
            }

            int m = merc.Graph.NearestNode(spy.Graph.Node(i).Floor, 0f, below: 24f);
            only[i] = m < 0 || (merc.Graph.Node(m).Floor - spy.Graph.Node(i).Floor).Size() > spy.Graph.Profile.Spacing || !merc.Reached(m);
        }

        return only;
    }

    /// <summary>
    /// A route for <paramref name="profile"/> from one point to another, or why there is none and the reachable point
    /// nearest the goal. The start snaps to the floor under it; the goal is any floor the side reaches within
    /// <see cref="UseReach"/> across of it (from 220 below to 20 above), the nearest first, so a switch on a wall or an
    /// objective on a desk counts as reached from the floor in front of it. Health does not come back during a round,
    /// so drops that each hurt but add up to a fatal fall make a route lethal: then a route with no hurting drop is
    /// looked for, and failing that the route is unreachable. Call under <see cref="AnalysisMap.Gate"/>.
    /// </summary>
    public static RouteResult Route(AnalysisMap map, ReachProfile profile, byte mask, Vec3 from, Vec3 to)
    {
        RouteResult r = ShortestRoute(map, profile, mask, from, to);
        if (!r.Reachable || r.Damage < 100f)
        {
            return r;
        }

        if (profile.Drops == DropRule.Survivable && ShortestRoute(map, profile with { Drops = DropRule.Unhurt }, mask, from, to) is { Reachable: true } safe)
        {
            return safe;
        }

        return r with { Reachable = false, Problem = $"the only route found has drops that cost {r.Damage:0}% of a {Name(profile)}'s health in all, a fatal fall" };
    }

    private static RouteResult ShortestRoute(AnalysisMap map, ReachProfile profile, byte mask, Vec3 from, Vec3 to)
    {
        NavGraph graph = map.Graph(profile, mask);
        FVector goal = AnalysisMap.F(to);
        int a = graph.NearestNode(AnalysisMap.F(from), 0f, below: 400f);
        var near = new List<int>();
        graph.NodesNear(goal - new FVector(0f, 0f, 100f), UseReach, near);
        if (graph.NearestNode(goal, 0f, below: 400f) is >= 0 and int under)
        {
            near.Add(under);
        }

        if (a < 0 || near.Count == 0)
        {
            string which = a < 0 && near.Count == 0 ? "start and the goal" : a < 0 ? "start" : "goal";
            return new RouteResult(false, $"no floor a {Name(profile)} stands on near the {which}", [], 0, 0, Floor(graph, a), null, null);
        }

        // Doors and lifts as a walker from this start finds them: opened by what it reaches on the way.
        MoverReach.Explored reach = MoverReach.Explore(map, graph, profile, [a]);
        int b = near.Where(reach.Reached).OrderBy(n => Across(graph.Node(n).Floor, goal)).DefaultIfEmpty(-1).First();
        if (b >= 0)
        {
            List<RouteLeg> legs = Legs(map, graph, profile, reach, a, b);
            return new RouteResult(true, null, legs, legs.Sum(l => l.Length), reach.Cost[b], Floor(graph, a), Floor(graph, b), null);
        }

        // Unreachable: the reached node nearest the goal shows how far the side gets.
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < graph.NodeCount; i++)
        {
            float d = (graph.Node(i).Floor - goal).Size();
            if (reach.Reached(i) && d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        // The shut doors and unworkable lifts that alone stand in the way.
        List<string> keys = [.. MoverReach.Stopping(map, graph, reach)
            .Where(m => MoverReach.Explore(map, graph, profile, [a], opened: m.Id) is var through && near.Any(through.Reached))
            .Select(Label)];
        string doors = keys.Count > 0 ? $" (it gets there once {Either(keys)} opens)" : "";
        return new RouteResult(false, $"no way for a {Name(profile)} from the start to the goal{doors}", [], 0, 0, Floor(graph, a),
            Floor(graph, near[0]), Floor(graph, best));
    }

    private static float Across(FVector a, FVector b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    /// <summary>The search's path from <paramref name="start"/> to <paramref name="end"/> merged into legs: runs of walking
    /// or crouching, each drop and each GE link, each lift ride and each door passed.</summary>
    private static List<RouteLeg> Legs(AnalysisMap map, NavGraph graph, ReachProfile profile, MoverReach.Explored reach, int start, int end)
    {
        var steps = new List<(int Node, int Via)>();
        for (int n = end; n != start && n >= 0; n = reach.Previous[n])
        {
            steps.Add((n, reach.Via[n]));
        }

        steps.Reverse();
        Dictionary<int, string> geIds = map.GeList().ToDictionary(g => g.Index, g => g.Id);
        Dictionary<int, string> doorEdges = MoverReach.DoorEdges(map, graph, reach.Movers);
        Dictionary<string, string?> openedBy = reach.Uses.ToDictionary(u => u.Id, u => u.By);
        var legs = new List<RouteLeg>();
        Vec3 at = AnalysisMap.V(graph.Node(start).Floor);
        foreach ((int node, int via) in steps)
        {
            Vec3 next = AnalysisMap.V(graph.Node(node).Floor);
            double length = (next - at).Length;
            if (via <= -2)
            {
                string lift = reach.Movers[-2 - via].Id;
                legs.Add(new RouteLeg("lift", at, next, length, null, 0f, 0f) { Mover = lift, OpenedBy = openedBy.GetValueOrDefault(lift) });
            }
            else if (doorEdges.TryGetValue(via, out string? door))
            {
                legs.Add(new RouteLeg("door", at, next, length, null, 0f, 0f) { Mover = door, OpenedBy = openedBy.GetValueOrDefault(door) });
            }
            else
            {
                (NavEdgeKind kind, int link) = MoverReach.Edge(graph, via);
                string how = AnalysisJson.Words(kind);
                if (legs.Count > 0 && link < 0 && legs[^1].IsWalk && legs[^1].How == how)
                {
                    RouteLeg last = legs[^1];
                    legs[^1] = last with { To = next, Length = last.Length + length };
                }
                else
                {
                    string? ge = link >= 0 && geIds.TryGetValue(graph.Links[link].GE, out string? id) ? id : null;
                    float drop = kind == NavEdgeKind.Drop ? (float)(at.Z - next.Z) : 0f;
                    legs.Add(new RouteLeg(how, at, next, length, ge, drop, profile.Damage(drop)));
                }
            }

            at = next;
        }

        return legs;
    }

    private static Vec3? Floor(NavGraph graph, int node) => node >= 0 ? AnalysisMap.V(graph.Node(node).Floor) : null;

    private static string Name(ReachMap m) => Name(m.Profile);

    private static string Name(ReachProfile p) => p.Side == TeamSide.Spy ? "spy" : "merc";
}

/// <summary>A connected area of floor only spies reach: its centre, size and one node in it.</summary>
public sealed record SpyOnlyArea(Vec3 Centre, int Nodes, double Area, int Node);
