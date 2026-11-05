using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>A grabbable ledge lip: a level line along a top edge, with its hang side.</summary>
public sealed record Lip(Vec3 A, Vec3 B, Vec3 Out, double Drop)
{
    public double Length => (B - A).Length;

    public Vec3 Mid => (A + B) / 2;

    /// <summary>Unit direction from A to B.</summary>
    public Vec3 Dir => (B - A).Normalized();

    /// <summary>True when an end was moved to meet another ledge at a corner.</summary>
    public bool Joined { get; init; }
}

/// <summary>What a detection run found, with counts of the edges it looked at and why points were refused.</summary>
public sealed record LedgeResult(IReadOnlyList<Lip> Lips, int Edges, int Probes, IReadOnlyDictionary<LipProblem, int> Refused);

/// <summary>
/// Finds the grabbable ledges of a map from its geometry. It walks the boundary edges of walkable surfaces (edges a
/// single up-facing polygon owns), probes points along each one with <see cref="LedgeProbe"/> (a drop on the outer
/// side, room to hang and to climb), keeps the runs that pass (so obstacles split a ledge), merges runs that continue
/// each other, and joins ends that meet at a corner so the shimmy carries round it.
/// </summary>
public static class LedgeDetector
{
    private const int RefineSteps = 5;

    /// <summary>Lips this close across (and in height) lie on one line.</summary>
    private const double SameLine = 4;

    public static LedgeResult Detect(TraversalWorld w, LedgeOptions o)
    {
        List<(Vec3 A, Vec3 B, Vec3 Inner)> edges = BoundaryEdges(w, o);
        var refused = new Dictionary<LipProblem, int>();
        int probes = 0;
        var lips = new List<Lip>();
        foreach ((Vec3 a, Vec3 b, Vec3 inner) in edges)
        {
            lips.AddRange(Runs(w, a, b, inner, o, refused, ref probes));
        }

        lips = Merge(lips);
        lips = JoinCorners(lips, o.JoinDistance);
        lips = lips.Where(l => l.Length >= o.MinLength || (l.Joined && l.Length >= 1)).ToList();
        lips.Sort((x, y) => x.A.Z != y.A.Z ? x.A.Z.CompareTo(y.A.Z) : x.A.X != y.A.X ? x.A.X.CompareTo(y.A.X) : x.A.Y.CompareTo(y.A.Y));
        return new LedgeResult(lips, edges.Count, probes, refused);
    }

    /// <summary>Level edges owned by exactly one walkable polygon, with a point inside that polygon.</summary>
    private static List<(Vec3 A, Vec3 B, Vec3 Inner)> BoundaryEdges(TraversalWorld w, LedgeOptions o)
    {
        var edges = new Dictionary<(long, long, long, long, long, long), (Vec3 A, Vec3 B, Vec3 Inner, int Count)>();
        foreach (SurfacePoly p in w.Surfaces)
        {
            if (p.Normal.Z < Limits.WalkableNormalZ || (p.Source == SurfaceSource.Mesh && !o.IncludeMeshes) ||
                (o.Region is { } r && !Overlaps(p.Verts, r)))
            {
                continue;
            }

            Vec3 centre = p.Verts.Aggregate(Vec3.Zero, (s, v) => s + v) / p.Verts.Length;
            for (int i = 0; i < p.Verts.Length; i++)
            {
                Vec3 a = p.Verts[i], b = p.Verts[(i + 1) % p.Verts.Length];
                double flat = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (flat < 4 || Math.Abs(b.Z - a.Z) > o.MaxSlope * flat + 0.5)
                {
                    continue;
                }

                var key = Key(a, b);
                edges[key] = edges.TryGetValue(key, out var e) ? e with { Count = e.Count + 1 } : (a, b, centre, 1);
            }
        }

        return edges.Values.Where(e => e.Count == 1).Select(e => (e.A, e.B, e.Inner)).ToList();
    }

    /// <summary>The passing runs of one edge, their ends refined to about a unit.</summary>
    private static List<Lip> Runs(TraversalWorld w, Vec3 a, Vec3 b, Vec3 inner, LedgeOptions o,
        Dictionary<LipProblem, int> refused, ref int probes)
    {
        Vec3 flat = new Vec3(b.X - a.X, b.Y - a.Y, 0).Normalized();
        var @out = new Vec3(flat.Y, -flat.X, 0);
        if (Vec3.Dot(@out, inner - a) > 0)
        {
            @out = -@out;
        }

        double length = (b - a).Length;
        int n = Math.Max(2, (int)Math.Ceiling((length - 2) / o.Step) + 1);
        var t = new double[n];
        var ok = new bool[n];
        var drops = new double[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = 1 + (length - 2) * i / (n - 1);
            (LipProblem problem, double drop) = LedgeProbe.Evaluate(w, Vec3.Lerp(a, b, t[i] / length), @out, o);
            probes++;
            ok[i] = problem == LipProblem.None;
            drops[i] = drop;
            if (!ok[i])
            {
                refused[problem] = refused.GetValueOrDefault(problem) + 1;
                if (i == 0 && problem is LipProblem.NoTop or LipProblem.Step && n > 2)
                {
                    // Most boundary edges are not lips at all; one more look at the middle before giving up.
                    (LipProblem midProblem, _) = LedgeProbe.Evaluate(w, Vec3.Lerp(a, b, 0.5), @out, o);
                    probes++;
                    if (midProblem is LipProblem.NoTop or LipProblem.Step)
                    {
                        return [];
                    }
                }
            }
        }

        var result = new List<Lip>();
        for (int i = 0; i < n;)
        {
            if (!ok[i])
            {
                i++;
                continue;
            }

            int j = i;
            while (j + 1 < n && ok[j + 1])
            {
                j++;
            }

            double start = i == 0 ? 0 : Refine(w, a, b, @out, o, t[i - 1], t[i], ref probes);
            double end = j == n - 1 ? length : Refine(w, a, b, @out, o, t[j + 1], t[j], ref probes);
            double drop = drops.Skip(i).Take(j - i + 1).Min();
            result.Add(new Lip(Vec3.Lerp(a, b, start / length), Vec3.Lerp(a, b, end / length), @out, drop));
            i = j + 1;
        }

        return result;
    }

    /// <summary>Bisects between a failing and a passing distance along the edge; returns the last passing one.</summary>
    private static double Refine(TraversalWorld w, Vec3 a, Vec3 b, Vec3 @out, LedgeOptions o, double bad, double good, ref int probes)
    {
        double length = (b - a).Length;
        for (int k = 0; k < RefineSteps; k++)
        {
            double m = (bad + good) / 2;
            probes++;
            if (LedgeProbe.Evaluate(w, Vec3.Lerp(a, b, m / length), @out, o).Problem == LipProblem.None)
            {
                good = m;
            }
            else
            {
                bad = m;
            }
        }

        return good;
    }

    /// <summary>Joins lips that continue each other: same hang side, height and line, touching or overlapping. Lips a
    /// few units apart on parallel lines are one lip seen on two overlapping surfaces (a mesh over the BSP).</summary>
    private static List<Lip> Merge(List<Lip> lips)
    {
        var groups = lips.GroupBy(l => (Math.Round(Math.Atan2(l.Out.Y, l.Out.X) * 180 / Math.PI) + 360) % 360);
        var result = new List<Lip>();
        foreach (var g in groups)
        {
            var pending = g.ToList();
            while (pending.Count > 0)
            {
                Lip cur = pending[0];
                pending.RemoveAt(0);
                bool merged = true;
                while (merged)
                {
                    merged = false;
                    for (int i = 0; i < pending.Count; i++)
                    {
                        if (TryMerge(cur, pending[i]) is { } m)
                        {
                            cur = m;
                            pending.RemoveAt(i);
                            merged = true;
                            break;
                        }
                    }
                }

                result.Add(cur);
            }
        }

        return result;
    }

    private static Lip? TryMerge(Lip x, Lip y)
    {
        if (Vec3.Dot(x.Out, y.Out) < 0.9995 || Math.Abs(x.A.Z - y.A.Z) > SameLine)
        {
            return null;
        }

        Vec3 d = x.Dir;
        if (Math.Abs(Vec3.Dot(y.A - x.A, x.Out)) > SameLine || Math.Abs(Vec3.Dot(y.B - x.A, x.Out)) > SameLine)
        {
            return null;
        }

        double y0 = Vec3.Dot(y.A - x.A, d), y1 = Vec3.Dot(y.B - x.A, d);
        double lo = Math.Min(y0, y1), hi = Math.Max(y0, y1);
        if (lo > x.Length + 2 || hi < -2)
        {
            return null;
        }

        double s = Math.Min(0, lo), e = Math.Max(x.Length, hi);
        Vec3 at(double k) => x.A + d * k;
        return new Lip(at(s), at(e), x.Out, Math.Min(x.Drop, y.Drop));
    }

    /// <summary>Moves ends that nearly meet at a corner onto the corner itself (closest pairs first). With
    /// <paramref name="skipConnected"/>, ends the engine already connects (within 2) are left alone.</summary>
    public static List<Lip> JoinCorners(List<Lip> lips, double maxDistance, bool skipConnected = false)
    {
        var ends = new List<(int Lip, bool IsB)>();
        for (int i = 0; i < lips.Count; i++)
        {
            ends.Add((i, false));
            ends.Add((i, true));
        }

        Vec3 EndOf((int Lip, bool IsB) e) => e.IsB ? lips[e.Lip].B : lips[e.Lip].A;
        var pairs = new List<(double D, (int, bool) E1, (int, bool) E2, Vec3 Corner)>();
        var connected = new HashSet<(int, bool)>();
        var grid = new Dictionary<(long, long, long), List<(int, bool)>>();
        double cell = Math.Max(maxDistance, 1);
        (long, long, long) CellOf(Vec3 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
        foreach (var e in ends)
        {
            var c = CellOf(EndOf(e));
            if (!grid.TryGetValue(c, out var list))
            {
                grid[c] = list = new List<(int, bool)>();
            }

            list.Add(e);
        }

        foreach (var e1 in ends)
        {
            Vec3 p1 = EndOf(e1);
            var c = CellOf(p1);
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var near))
                {
                    continue;
                }

                foreach (var e2 in near)
                {
                    if (e2.Item1 == e1.Lip)
                    {
                        continue;
                    }

                    if (skipConnected && GeShapes.Connected(p1, EndOf(e2)))
                    {
                        // Already a working corner: neither end moves, so the join cannot break it.
                        connected.Add(e1);
                        connected.Add(e2);
                        continue;
                    }

                    if (e2.Item1 < e1.Lip || Corner(lips[e1.Lip], lips[e2.Item1], p1, EndOf(e2), maxDistance) is not { } corner)
                    {
                        continue;
                    }

                    pairs.Add(((p1 - EndOf(e2)).Length, e1, e2, corner));
                }
            }
        }

        var used = new HashSet<(int, bool)>(connected);
        var moved = lips.ToArray();
        foreach (var (_, e1, e2, corner) in pairs.OrderBy(p => p.D))
        {
            if (used.Contains(e1) || used.Contains(e2))
            {
                continue;
            }

            used.Add(e1);
            used.Add(e2);
            moved[e1.Item1] = MoveEnd(moved[e1.Item1], e1.Item2, corner);
            moved[e2.Item1] = MoveEnd(moved[e2.Item1], e2.Item2, corner);
        }

        return moved.ToList();
    }

    /// <summary>The corner two lips meet at: their lines' crossing, when both ends are near it and the heights agree.</summary>
    internal static Vec3? Corner(Lip l1, Lip l2, Vec3 end1, Vec3 end2, double maxDistance)
    {
        if (Math.Abs(end1.Z - end2.Z) > 2 || (end1 - end2).Length > maxDistance)
        {
            return null;
        }

        Vec3 d1 = l1.Dir, d2 = l2.Dir;
        double cross = d1.X * d2.Y - d1.Y * d2.X;
        double z = (end1.Z + end2.Z) / 2;
        if (Math.Abs(cross) < 0.2)
        {
            // Nearly parallel: only a small gap in one line closes, at the middle. The ends must lie on one line, so
            // the gap is measured across the direction (lips made from GEs have no hang side): two ledges back to back
            // on a thin wall are not a gap.
            Vec3 gap = end2 - end1;
            return Math.Abs(gap.X * d1.Y - gap.Y * d1.X) < 1 && gap.Length <= 8 ? (end1 + end2) / 2 : null;
        }

        Vec3 w = l2.A - l1.A;
        double s = (w.X * d2.Y - w.Y * d2.X) / cross;
        Vec3 p = l1.A + d1 * s;
        var corner = new Vec3(p.X, p.Y, z);
        return Flat(corner - end1) <= maxDistance && Flat(corner - end2) <= maxDistance ? corner : null;
    }

    private static Lip MoveEnd(Lip l, bool isB, Vec3 to) => (isB ? l with { B = to } : l with { A = to }) with { Joined = true };

    private static double Flat(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    private static bool Overlaps(Vec3[] verts, (Vec3 Min, Vec3 Max) r) =>
        verts.Any(v => v.X >= r.Min.X && v.Y >= r.Min.Y && v.Z >= r.Min.Z && v.X <= r.Max.X && v.Y <= r.Max.Y && v.Z <= r.Max.Z);

    private static (long, long, long, long, long, long) Key(Vec3 a, Vec3 b)
    {
        (long, long, long) ka = (R(a.X), R(a.Y), R(a.Z)), kb = (R(b.X), R(b.Y), R(b.Z));
        if (ka.CompareTo(kb) > 0)
        {
            (ka, kb) = (kb, ka);
        }

        return (ka.Item1, ka.Item2, ka.Item3, kb.Item1, kb.Item2, kb.Item3);
    }

    private static long R(double v) => (long)Math.Round(v * 4);
}
