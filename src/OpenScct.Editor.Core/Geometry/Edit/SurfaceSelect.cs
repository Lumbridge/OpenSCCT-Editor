using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>
/// The stock editor's "Select Surfaces > Adjacent" commands on brush faces: from the picked faces, spread to every face
/// that touches one already taken (shares an edge, or meets it along a line, as a doorway's side meets the wall it
/// cuts) and is of the asked kind, until no more are found. Faces are compared as they are seen: a subtractive brush's
/// faces face into its room.
/// </summary>
public static class SurfaceSelect
{
    /// <summary>How close two faces must come (units) to touch.</summary>
    private const double Touch = 0.5;

    /// <summary>The kinds the adjacent selections take: <c>all</c>, <c>coplanar</c>, <c>walls</c>, <c>floors</c>
    /// (floors and ceilings, as the stock command) and <c>slants</c>.</summary>
    public static readonly IReadOnlyList<string> AdjacentKinds = ["all", "coplanar", "walls", "floors", "slants"];

    /// <summary>
    /// The picked faces plus every face connected to them through faces of <paramref name="kind"/> (see
    /// <see cref="AdjacentKinds"/>). <paramref name="include"/> limits the brushes searched (hidden and locked ones,
    /// for the app). The picked faces come first, in their order.
    /// </summary>
    public static List<FaceRef> Adjacent(MapProject p, IEnumerable<FaceRef> picked, string kind, Func<Brush, bool>? include = null)
    {
        if (!AdjacentKinds.Contains(kind))
        {
            throw new ArgumentException($"adjacent kind must be {string.Join(", ", AdjacentKinds)}, not '{kind}'");
        }

        List<Surface> all = Surfaces(p, include);
        var byRef = new Dictionary<FaceRef, Surface>();
        foreach (Surface s in all)
        {
            byRef[s.Ref] = s;
        }

        var grid = new SurfaceGrid(all);
        var result = new List<FaceRef>();
        var taken = new HashSet<FaceRef>();
        var queue = new Queue<Surface>();
        foreach (FaceRef f in picked)
        {
            if (taken.Add(f))
            {
                result.Add(f);
                if (byRef.TryGetValue(f, out Surface? s))
                {
                    queue.Enqueue(s);
                }
            }
        }

        var near = new List<Surface>();
        while (queue.Count > 0)
        {
            Surface from = queue.Dequeue();
            grid.Near(from, near);
            foreach (Surface s in near)
            {
                if (taken.Contains(s.Ref) || !BoxesTouch(from, s) || !Matches(kind, from, s) || !Touches(from, s))
                {
                    continue;
                }

                taken.Add(s.Ref);
                result.Add(s.Ref);
                queue.Enqueue(s);
            }
        }

        return result;
    }

    /// <summary>The kind of a face by its seen normal: <c>walls</c> (|z| &lt; 0.3), <c>floors</c> (|z| &gt; 0.7, floors
    /// and ceilings) or <c>slants</c>.</summary>
    public static string SlopeKind(Vec3 seenNormal) =>
        Math.Abs(seenNormal.Z) < 0.3 ? "walls" : Math.Abs(seenNormal.Z) > 0.7 ? "floors" : "slants";

    private static bool Matches(string kind, Surface from, Surface s) => kind switch
    {
        "all" => true,
        "coplanar" => Vec3.Dot(from.Seen, s.Seen) > 0.999 && Math.Abs(Vec3.Dot(s.Seen, s.Points[0]) - Vec3.Dot(from.Seen, from.Points[0])) < Touch,
        _ => SlopeKind(s.Seen) == kind,
    };

    private static List<Surface> Surfaces(MapProject p, Func<Brush, bool>? include)
    {
        var list = new List<Surface>();
        foreach (Brush b in p.Brushes)
        {
            if (include is not null && !include(b))
            {
                continue;
            }

            ShapeTransform t = ShapeTransform.For(b);
            double sign = b.Oper == CsgOper.Subtract ? -1 : 1;
            for (int i = 0; i < b.Shape.Faces.Count; i++)
            {
                List<Vec3> v = t.FaceToWorld(b.Shape.Faces[i]);
                if (v.Count < 3 || Poly.Newell(v).Length < 1e-9)
                {
                    continue;
                }

                Vec3 n = Poly.Normal(v);
                var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
                var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
                foreach (Vec3 q in v)
                {
                    min = Vec3.Min(min, q);
                    max = Vec3.Max(max, q);
                }

                list.Add(new Surface(new FaceRef(b.Id, i), v, n, n * sign, min, max));
            }
        }

        return list;
    }

    private static bool BoxesTouch(Surface a, Surface b) =>
        a.Min.X - Touch <= b.Max.X && b.Min.X - Touch <= a.Max.X
        && a.Min.Y - Touch <= b.Max.Y && b.Min.Y - Touch <= a.Max.Y
        && a.Min.Z - Touch <= b.Max.Z && b.Min.Z - Touch <= a.Max.Z;

    /// <summary>True when an edge of either face lies on the other along more than <see cref="Touch"/>.</summary>
    private static bool Touches(Surface a, Surface b) => EdgeOn(a, b) || EdgeOn(b, a);

    private static bool EdgeOn(Surface edges, Surface on)
    {
        IReadOnlyList<Vec3> v = edges.Points;
        for (int i = 0; i < v.Count; i++)
        {
            if (SegmentOn(v[i], v[(i + 1) % v.Count], on))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when more than <see cref="Touch"/> of the segment lies in the face's plane, inside the face.</summary>
    private static bool SegmentOn(Vec3 a, Vec3 b, Surface face)
    {
        Vec3 n = face.Normal;
        double d = Vec3.Dot(n, face.Points[0]);
        if (Math.Abs(Vec3.Dot(n, a) - d) > Touch || Math.Abs(Vec3.Dot(n, b) - d) > Touch)
        {
            return false;
        }

        double length = (b - a).Length;
        if (length <= Touch)
        {
            return false;
        }

        // Cut the segment by each edge's inward half-plane (convex face), with the touch distance as slack.
        double t0 = 0, t1 = 1;
        IReadOnlyList<Vec3> p = face.Points;
        for (int i = 0; i < p.Count && t0 < t1; i++)
        {
            Vec3 e = p[(i + 1) % p.Count] - p[i];
            double el = e.Length;
            if (el < 1e-9)
            {
                continue;
            }

            Vec3 inward = Vec3.Cross(n, e) * (1 / el);
            double da = Vec3.Dot(a - p[i], inward) + Touch, db = Vec3.Dot(b - p[i], inward) + Touch;
            if (da < 0 && db < 0)
            {
                return false;
            }

            if (da < 0)
            {
                t0 = Math.Max(t0, da / (da - db));
            }
            else if (db < 0)
            {
                t1 = Math.Min(t1, da / (da - db));
            }
        }

        return (t1 - t0) * length > Touch;
    }

    /// <summary>A face in world space: its polygon, outward normal, the normal it is seen by, its box, and its place in
    /// the list the grid indexes.</summary>
    private sealed record Surface(FaceRef Ref, IReadOnlyList<Vec3> Points, Vec3 Normal, Vec3 Seen, Vec3 Min, Vec3 Max)
    {
        public int Index { get; set; }
    }

    /// <summary>
    /// The faces by the cells of a uniform grid their boxes (grown by <see cref="Touch"/>) overlap, so a face's
    /// neighbours are looked for only in the cells round it: the work grows with the faces taken, not with them times
    /// the faces of the map. A face spanning more than <see cref="MaxCells"/> cells (a big terrain plane) is kept in one
    /// list every lookup reads.
    /// </summary>
    private sealed class SurfaceGrid
    {
        private const double Cell = 256;
        private const int MaxCells = 64;
        private readonly Dictionary<(int X, int Y, int Z), List<Surface>> _cells = new();
        private readonly List<Surface> _big = new();
        private readonly int[] _seen;
        private int _stamp;

        public SurfaceGrid(List<Surface> all)
        {
            _seen = new int[all.Count];
            for (int i = 0; i < all.Count; i++)
            {
                Surface s = all[i];
                s.Index = i;
                ((int X, int Y, int Z) lo, (int X, int Y, int Z) hi) = Range(s);
                long cells = (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
                if (cells > MaxCells)
                {
                    _big.Add(s);
                    continue;
                }

                for (int x = lo.X; x <= hi.X; x++)
                {
                    for (int y = lo.Y; y <= hi.Y; y++)
                    {
                        for (int z = lo.Z; z <= hi.Z; z++)
                        {
                            if (!_cells.TryGetValue((x, y, z), out List<Surface>? list))
                            {
                                _cells[(x, y, z)] = list = new List<Surface>();
                            }

                            list.Add(s);
                        }
                    }
                }
            }
        }

        /// <summary>Fills <paramref name="near"/> with the faces sharing a cell with <paramref name="from"/>, each once.</summary>
        public void Near(Surface from, List<Surface> near)
        {
            near.Clear();
            _stamp++;
            _seen[from.Index] = _stamp;
            foreach (Surface s in _big)
            {
                Take(s, near);
            }

            ((int X, int Y, int Z) lo, (int X, int Y, int Z) hi) = Range(from);
            long cells = (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
            if (cells > _cells.Count)
            {
                // A face bigger than the occupied grid: reading every cell is cheaper than walking its range.
                foreach (List<Surface> list in _cells.Values)
                {
                    list.ForEach(s => Take(s, near));
                }

                return;
            }

            for (int x = lo.X; x <= hi.X; x++)
            {
                for (int y = lo.Y; y <= hi.Y; y++)
                {
                    for (int z = lo.Z; z <= hi.Z; z++)
                    {
                        if (_cells.TryGetValue((x, y, z), out List<Surface>? list))
                        {
                            list.ForEach(s => Take(s, near));
                        }
                    }
                }
            }
        }

        private void Take(Surface s, List<Surface> near)
        {
            if (_seen[s.Index] != _stamp)
            {
                _seen[s.Index] = _stamp;
                near.Add(s);
            }
        }

        private static ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) Range(Surface s) =>
            ((At(s.Min.X - Touch), At(s.Min.Y - Touch), At(s.Min.Z - Touch)), (At(s.Max.X + Touch), At(s.Max.Y + Touch), At(s.Max.Z + Touch)));

        private static int At(double v) => (int)Math.Floor(Math.Clamp(v / Cell, -1e6, 1e6));
    }
}
