using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// Two zone actors in one connected space, and what would seal them apart: the smallest set of openings whose removal
/// separates them (a minimum cut of the open boundaries between BSP leaves, by area), where a zone portal belongs, and
/// the shortest way from one actor to the other through the openings, to draw. <see cref="Cut"/> is empty when both
/// actors stand in one leaf.
/// </summary>
public sealed record ZoneLeak(string ActorId, string OtherId, Vec3 At, IReadOnlyList<Vec3[]> Cut, double CutArea, IReadOnlyList<Vec3> Path)
{
    /// <summary>The area-weighted centre of the cut's openings, or null without a cut.</summary>
    public Vec3? CutCentre => Cut.Count == 0 ? null : ZoneLeaks.Centre(Cut);

    public string Message => CutCentre is { } centre
        ? $"zone actor {ActorId} is in the zone of {OtherId}; add a zone portal between them: the smallest opening that separates them is " +
          $"{Math.Round(CutArea)} square units at {Round(centre)}" + (Cut.Count > 1 ? $" ({Cut.Count} faces)" : "")
        : $"zone actor {ActorId} is in the zone of {OtherId}, in the same open space with no opening between them; add a zone portal between them";

    /// <summary>Where the finding points: the cut, else the zone actor.</summary>
    public Vec3 Location => CutCentre ?? At;

    private static Vec3 Round(Vec3 v) => new(Math.Round(v.X), Math.Round(v.Y), Math.Round(v.Z));
}

/// <summary>
/// A zone portal brush that seals nothing: its sheets touch no open space, or the space on both of its sides is one
/// zone because it flows round the portal (<see cref="Path"/>: the way round, from one side to the other).
/// </summary>
public sealed record ZonePortalProblem(string BrushId, string Message, Vec3 At, IReadOnlyList<Vec3> Path, IReadOnlyList<Vec3[]> Sheets);

/// <summary>
/// The open boundaries between the empty leaves of a zoned BSP (the faces two leaves share, minus portal sheets), and
/// the leak searches over them. A boundary seen from both of its leaves counts once, at the larger of its two open
/// areas.
/// </summary>
internal sealed class ZoneLeaks
{
    /// <summary>
    /// Leaves joined to a zone actor's leaf through openings that lie wholly this close to it are its side of a cut: a
    /// cut must not simply box in the small leaf an actor happens to stand in, which can have less open area than the
    /// doorway between two rooms. Only openings wholly within reach count, so a doorway the actor stands near stays
    /// open to the cut.
    /// </summary>
    private const double ActorReach = 128;

    private readonly List<(int A, int B, double Area, Vec3 Centre, List<Poly> Pieces)> _boundaries = [];
    private readonly List<(int Boundary, int To)>?[] _next;
    private readonly Vec3[] _centre;
    private readonly Aabb[] _bounds;

    /// <param name="openings">Open area between two leaves, its area-weighted sum of centres and the pieces, by (from, to).</param>
    /// <param name="cellBounds">Each leaf's cell bounds (empty for solid leaves).</param>
    public ZoneLeaks(Dictionary<(int From, int To), (double Area, Vec3 Sum, List<Poly> Pieces)> openings, Aabb[] cellBounds)
    {
        var index = new Dictionary<(int, int), int>();
        foreach (((int a, int b), (double area, Vec3 sum, List<Poly> pieces)) in openings)
        {
            (int, int) key = a < b ? (a, b) : (b, a);
            if (index.TryGetValue(key, out int seen))
            {
                if (area > _boundaries[seen].Area)
                {
                    _boundaries[seen] = (key.Item1, key.Item2, area, sum / area, pieces);
                }

                continue;
            }

            index[key] = _boundaries.Count;
            _boundaries.Add((key.Item1, key.Item2, area, sum / area, pieces));
        }

        int leaves = cellBounds.Length;
        _bounds = cellBounds;
        _centre = cellBounds.Select(b => b.IsEmpty ? Vec3.Zero : b.Center).ToArray();
        _next = new List<(int, int)>?[leaves];
        for (int i = 0; i < _boundaries.Count; i++)
        {
            (int a, int b, _, _, _) = _boundaries[i];
            (_next[a] ??= []).Add((i, b));
            (_next[b] ??= []).Add((i, a));
        }
    }

    /// <summary>The leak between the zone actor <paramref name="seed"/> (in leaf <paramref name="from"/>) and the earlier
    /// actor <paramref name="other"/> (at <paramref name="otherAt"/>, in leaf <paramref name="to"/>).</summary>
    public ZoneLeak Leak(ZoneSeed seed, string other, Vec3 otherAt, int from, int to)
    {
        if (from == to)
        {
            return new ZoneLeak(seed.ActorId, other, seed.Location, [], 0, [seed.Location, otherAt]);
        }

        List<Vec3> path = [seed.Location, .. Way(from, to), otherAt];
        HashSet<int> sources = Near(from, seed.Location), sinks = Near(to, otherAt);
        if (sources.Overlaps(sinks))
        {
            // The actors are close enough that their reaches meet: cut between their own leaves only.
            sources = [from];
            sinks = [to];
        }

        (List<int> cut, double area) = MinimumCut(sources, sinks);
        return new ZoneLeak(seed.ActorId, other, seed.Location, cut.SelectMany(i => _boundaries[i].Pieces).Select(p => p.Verts).ToList(), area, path);
    }

    /// <summary>
    /// Portal brushes that seal nothing, from the leaf pairs each portal sheet covers some of the face between
    /// (<paramref name="across"/>: leaf, leaf, sheet index), each leaf's zone and whether two leaves are one open space
    /// (<paramref name="joined"/>). Portals with no open space on either side, and portals whose two sides are one open
    /// space (it flows round them), are reported; a portal that parts at least one pair of leaves seals something.
    /// </summary>
    public List<ZonePortalProblem> UnsealedPortals(IReadOnlyList<Poly> sheets, IReadOnlyList<(int A, int B, int Sheet)> across, Func<int, int> zoneOf,
        Func<int, int, bool> joined)
    {
        var problems = new List<ZonePortalProblem>();
        foreach (IGrouping<string, int> brush in Enumerable.Range(0, sheets.Count).GroupBy(i => sheets[i].Surface.BrushId))
        {
            var mine = brush.ToHashSet();
            List<(int A, int B, int Sheet)> pairs = across.Where(p => mine.Contains(p.Sheet)).ToList();
            List<Vec3[]> outline = brush.Select(i => sheets[i].Verts).ToList();
            Vec3 at = Centre(outline);
            if (pairs.Count == 0)
            {
                problems.Add(new ZonePortalProblem(brush.Key, $"zone portal {brush.Key} seals nothing: it has no open space on either side (it lies in solid or outside the map)",
                    at, [], outline));
                continue;
            }

            if (pairs.Any(p => !joined(p.A, p.B)))
            {
                // It parts the space somewhere (both sides can still share a zone number on a map with more than 63
                // spaces: that is the numbering, not the portal).
                continue;
            }

            // Space flows round the portal: the way from one side to the other shows where.
            (int a, int b, _) = pairs[0];
            problems.Add(new ZonePortalProblem(brush.Key,
                $"zone portal {brush.Key} seals nothing: both of its sides are zone {zoneOf(a)}, because the space flows round it (the drawn path); " +
                "make it cover the whole opening, or close the gap round it", at, [_centre[a], .. Way(a, b), _centre[b]], outline));
        }

        return problems;
    }

    /// <summary>The area-weighted centre of some polygons.</summary>
    public static Vec3 Centre(IReadOnlyList<Vec3[]> polys)
    {
        Vec3 sum = Vec3.Zero;
        double total = 0;
        foreach (Vec3[] verts in polys)
        {
            Vec3 c = Vec3.Zero, cross = Vec3.Zero;
            for (int i = 0; i < verts.Length; i++)
            {
                c += verts[i];
                if (i >= 1 && i < verts.Length - 1)
                {
                    cross += Vec3.Cross(verts[i] - verts[0], verts[i + 1] - verts[0]);
                }
            }

            double area = cross.Length / 2;

            sum += c / verts.Length * Math.Max(area, 1e-6);
            total += Math.Max(area, 1e-6);
        }

        return total > 0 ? sum / total : Vec3.Zero;
    }

    /// <summary>The opening centres on the shortest way (leaf centre to opening to leaf centre) from one leaf to another.</summary>
    private List<Vec3> Way(int from, int to)
    {
        int n = _next.Length;
        var cost = new double[n];
        Array.Fill(cost, double.PositiveInfinity);
        var via = new int[n];
        Array.Fill(via, -1);
        var at = new Vec3[n];
        cost[from] = 0;
        at[from] = _centre[from];
        var open = new PriorityQueue<int, double>();
        open.Enqueue(from, 0);
        while (open.TryDequeue(out int leaf, out double c))
        {
            if (leaf == to)
            {
                break;
            }

            if (c > cost[leaf])
            {
                continue;
            }

            foreach ((int boundary, int other) in _next[leaf] ?? [])
            {
                Vec3 door = _boundaries[boundary].Centre;
                double total = c + (door - at[leaf]).Length;
                if (total < cost[other])
                {
                    cost[other] = total;
                    via[other] = boundary;
                    at[other] = door;
                    open.Enqueue(other, total);
                }
            }
        }

        var way = new List<Vec3>();
        for (int leaf = to; leaf != from && via[leaf] >= 0;)
        {
            (int a, int b, _, Vec3 centre, _) = _boundaries[via[leaf]];
            way.Add(centre);
            leaf = a == leaf ? b : a;
        }

        way.Reverse();
        return way;
    }

    /// <summary>The leaf an actor stands in and the leaves round it joined to it through openings within <see cref="ActorReach"/>.</summary>
    private HashSet<int> Near(int leaf, Vec3 p)
    {
        Aabb reach = new Aabb(p, p).Expand(ActorReach);
        var near = new HashSet<int> { leaf };
        var queue = new Queue<int>();
        queue.Enqueue(leaf);
        while (queue.Count > 0)
        {
            foreach ((int boundary, int other) in _next[queue.Dequeue()] ?? [])
            {
                if (!_bounds[other].IsEmpty && _bounds[other].Overlaps(reach) &&
                    _boundaries[boundary].Pieces.All(piece => piece.Verts.All(v => (v - p).Length <= ActorReach)) && near.Add(other))
                {
                    queue.Enqueue(other);
                }
            }
        }

        return near;
    }

    /// <summary>
    /// A minimum cut between two sets of leaves over the open boundaries, by area (Dinic's max flow; each boundary
    /// carries its area both ways): the boundaries of the cut and its total area.
    /// </summary>
    private (List<int> Boundaries, double Area) MinimumCut(HashSet<int> sources, HashSet<int> sinks)
    {
        int n = _next.Length, source = n, sink = n + 1;
        var flow = new MaxFlow(n + 2);
        foreach ((int a, int b, double area, _, _) in _boundaries)
        {
            flow.Add(a, b, area, area);
        }

        foreach (int s in sources)
        {
            flow.Add(source, s, double.PositiveInfinity, 0);
        }

        foreach (int t in sinks)
        {
            flow.Add(t, sink, double.PositiveInfinity, 0);
        }

        double total = flow.Run(source, sink);
        bool[] side = flow.SourceSide(source);
        var cut = new List<int>();
        for (int i = 0; i < _boundaries.Count; i++)
        {
            (int a, int b, _, _, _) = _boundaries[i];
            if (side[a] != side[b])
            {
                cut.Add(i);
            }
        }

        return (cut, total);
    }

    /// <summary>Dinic's maximum flow over an arc list (each arc stored with its reverse).</summary>
    private sealed class MaxFlow(int nodes)
    {
        private readonly List<int>[] _out = Enumerable.Range(0, nodes).Select(_ => new List<int>()).ToArray();
        private readonly List<int> _to = [];
        private readonly List<double> _cap = [];
        private int[] _level = [];
        private int[] _iter = [];

        /// <summary>Adds an arc and its reverse, with their capacities.</summary>
        public void Add(int a, int b, double forward, double back)
        {
            int e = _to.Count;
            _to.Add(b);
            _cap.Add(forward);
            _out[a].Add(e);
            _to.Add(a);
            _cap.Add(back);
            _out[b].Add(e + 1);
        }

        public double Run(int s, int t)
        {
            double total = 0;
            while (Levels(s, t))
            {
                _iter = new int[_out.Length];
                double pushed;
                while ((pushed = Push(s, t, double.PositiveInfinity)) > 1e-9)
                {
                    total += pushed;
                }
            }

            return total;
        }

        /// <summary>The nodes still reachable from <paramref name="s"/> in the residual graph (the source side of the cut).</summary>
        public bool[] SourceSide(int s)
        {
            var seen = new bool[_out.Length];
            var queue = new Queue<int>();
            queue.Enqueue(s);
            seen[s] = true;
            while (queue.Count > 0)
            {
                foreach (int e in _out[queue.Dequeue()])
                {
                    if (_cap[e] > 1e-9 && !seen[_to[e]])
                    {
                        seen[_to[e]] = true;
                        queue.Enqueue(_to[e]);
                    }
                }
            }

            return seen;
        }

        private bool Levels(int s, int t)
        {
            _level = new int[_out.Length];
            Array.Fill(_level, -1);
            _level[s] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                foreach (int e in _out[v])
                {
                    if (_cap[e] > 1e-9 && _level[_to[e]] < 0)
                    {
                        _level[_to[e]] = _level[v] + 1;
                        queue.Enqueue(_to[e]);
                    }
                }
            }

            return _level[t] >= 0;
        }

        // Iterative depth-first push along the level graph (maps have tens of thousands of leaves: no recursion).
        private double Push(int s, int t, double limit)
        {
            var stack = new List<int>();
            int v = s;
            while (true)
            {
                if (v == t)
                {
                    double amount = limit;
                    foreach (int e in stack)
                    {
                        amount = Math.Min(amount, _cap[e]);
                    }

                    foreach (int e in stack)
                    {
                        _cap[e] -= amount;
                        _cap[e ^ 1] += amount;
                    }

                    return amount;
                }

                bool advanced = false;
                for (; _iter[v] < _out[v].Count; _iter[v]++)
                {
                    int e = _out[v][_iter[v]];
                    if (_cap[e] > 1e-9 && _level[_to[e]] == _level[v] + 1)
                    {
                        stack.Add(e);
                        v = _to[e];
                        advanced = true;
                        break;
                    }
                }

                if (advanced)
                {
                    continue;
                }

                // Dead end: no augmenting path through v at this level.
                _level[v] = -1;
                if (stack.Count == 0)
                {
                    return 0;
                }

                int back = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                v = _to[back ^ 1];
                _iter[v]++;
            }
        }
    }
}
