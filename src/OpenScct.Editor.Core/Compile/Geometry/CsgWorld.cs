using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>The CSG of a whole brush list: every brush's output polygons, in brush order then face order.</summary>
public sealed class CsgResult
{
    public required IReadOnlyList<CsgBrush> Brushes { get; init; }

    /// <summary>Output polygons per brush, in brush order.</summary>
    public required IReadOnlyList<IReadOnlyList<Poly>> PerBrush { get; init; }

    /// <summary>The state outside every brush.</summary>
    public bool WorldSolid { get; init; } = true;

    /// <summary>Brushes whose output came from the cache.</summary>
    public int Reused { get; init; }

    public IEnumerable<Poly> Polys => PerBrush.SelectMany(p => p);
}

/// <summary>
/// Runs <see cref="CsgSolver"/> over a brush list and keeps a cache, so a second run after an edit only solves the
/// brushes whose own shape or neighbourhood changed (a brush's output depends on the brushes whose bounds touch it and
/// on their order relative to it). One instance per open project; not thread safe.
/// </summary>
public sealed class CsgWorld
{
    private Dictionary<string, CsgBrush> _prepared = new(StringComparer.Ordinal);
    private Dictionary<string, (ulong Key, IReadOnlyList<Poly> Polys)> _solved = new(StringComparer.Ordinal);
    private bool _worldSolid = true;

    public CsgResult Build(IReadOnlyList<Brush> brushes, bool worldSolid = true)
    {
        if (worldSolid != _worldSolid)
        {
            _solved.Clear();
            _worldSolid = worldSolid;
        }

        var prepared = new CsgBrush[brushes.Count];
        var nextPrepared = new Dictionary<string, CsgBrush>(StringComparer.Ordinal);
        for (int i = 0; i < brushes.Count; i++)
        {
            Brush b = brushes[i];
            ulong fp = CsgBrush.FingerprintOf(b);
            CsgBrush cb = _prepared.TryGetValue(b.Id, out CsgBrush? old) && old.Fingerprint == fp
                ? old.WithOrder(b, i)
                : CsgBrush.Prepare(b, i, fp);
            prepared[i] = cb;
            nextPrepared.TryAdd(b.Id, cb);
        }

        List<CsgBrush>[] neighbours = Neighbours(prepared);
        var keys = new ulong[prepared.Length];
        var results = new IReadOnlyList<Poly>?[prepared.Length];
        int reused = 0;
        for (int i = 0; i < prepared.Length; i++)
        {
            keys[i] = KeyOf(prepared[i], neighbours[i]);
            if (_solved.TryGetValue(prepared[i].Id, out var hit) && hit.Key == keys[i])
            {
                results[i] = hit.Polys;
                reused++;
            }
        }

        Parallel.For(0, prepared.Length, i =>
        {
            results[i] ??= CsgSolver.Solve(prepared[i], neighbours[i], worldSolid);
        });

        var nextSolved = new Dictionary<string, (ulong, IReadOnlyList<Poly>)>(StringComparer.Ordinal);
        for (int i = 0; i < prepared.Length; i++)
        {
            nextSolved.TryAdd(prepared[i].Id, (keys[i], results[i]!));
        }

        _prepared = nextPrepared;
        _solved = nextSolved;
        return new CsgResult { Brushes = prepared, PerBrush = results!, Reused = reused, WorldSolid = worldSolid };
    }

    /// <summary>For each brush, the brushes whose bounds touch its own (sweep along X).</summary>
    private static List<CsgBrush>[] Neighbours(CsgBrush[] brushes)
    {
        var result = new List<CsgBrush>[brushes.Length];
        for (int i = 0; i < brushes.Length; i++)
        {
            result[i] = new List<CsgBrush>();
        }

        int[] byX = Enumerable.Range(0, brushes.Length).Where(i => !brushes[i].Bounds.IsEmpty)
            .OrderBy(i => brushes[i].Bounds.Min.X).ThenBy(i => i).ToArray();
        for (int a = 0; a < byX.Length; a++)
        {
            CsgBrush ba = brushes[byX[a]];
            for (int b = a + 1; b < byX.Length; b++)
            {
                CsgBrush bb = brushes[byX[b]];
                if (bb.Bounds.Min.X > ba.Bounds.Max.X + Plane3.Epsilon)
                {
                    break;
                }

                if (ba.Bounds.Overlaps(bb.Bounds))
                {
                    result[byX[a]].Add(bb);
                    result[byX[b]].Add(ba);
                }
            }
        }

        foreach (List<CsgBrush> list in result)
        {
            list.Sort((x, y) => x.Order.CompareTo(y.Order));
        }

        return result;
    }

    /// <summary>What a brush's output depends on: its own shape and kind, and each neighbour's shape and whether it
    /// comes before or after it.</summary>
    private static ulong KeyOf(CsgBrush b, List<CsgBrush> neighbours)
    {
        var h = new Hash64();
        h.Add(b.Fingerprint);
        foreach (CsgBrush n in neighbours)
        {
            h.Add(n.Fingerprint);
            h.Add(n.Order < b.Order);
        }

        return h.Value;
    }
}
