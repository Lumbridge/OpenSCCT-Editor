using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc.Recovery;

/// <summary>One face of a convex cell, in world space: wound counter-clockwise about its normal, which points out of the
/// cell. <see cref="Surface"/> is the surf of the BSP node whose plane made the face (it does not promise that surf
/// covers it: <see cref="FacePartition"/> finds what does); -1 for none.</summary>
public sealed class CellFace
{
    public CellFace(List<Vec3> points, Vec3 normal, int surface, bool artificial = false)
    {
        Points = points;
        Normal = normal;
        Surface = surface;
        Artificial = artificial;
    }

    public List<Vec3> Points { get; }

    public Vec3 Normal { get; }

    public int Surface { get; }

    /// <summary>A face of the clipping box, not of the map: a cell that keeps one is not closed by the BSP.</summary>
    public bool Artificial { get; }

    public double Distance => Vec3.Dot(Normal, Points[0]);

    public double Area()
    {
        Vec3 sum = Vec3.Zero;
        for (int i = 1; i + 1 < Points.Count; i++)
        {
            sum += Vec3.Cross(Points[i] - Points[0], Points[i + 1] - Points[0]);
        }

        return Vec3.Dot(sum, Normal) / 2;
    }

    public Aabb Bounds() => Aabb.Of(Points);
}

/// <summary>A convex cell or brush made of <see cref="CellFace"/>s.</summary>
public sealed class Cell
{
    public Cell(List<CellFace> faces) => Faces = faces;

    public List<CellFace> Faces { get; }

    /// <summary>The cells this one stands for (indices into the joined brushes; see <see cref="CellGeometry.Grow"/>).</summary>
    public List<int> Members { get; set; } = [];

    public Aabb Bounds()
    {
        Aabb box = Aabb.Empty;
        foreach (CellFace f in Faces)
        {
            foreach (Vec3 p in f.Points)
            {
                box = box.Add(p);
            }
        }

        return box;
    }

    public double Volume() => Faces.Count == 0 ? 0 : CellGeometry.VolumeOf(Faces, Faces[0].Points[0]);

    /// <summary>The smallest, over the faces, of the greatest depth of a corner behind the face: how thin the cell is.</summary>
    public double MinimumWidth()
    {
        double narrowest = double.PositiveInfinity;
        foreach (CellFace f in Faces)
        {
            double deepest = 0, d = f.Distance;
            foreach (CellFace o in Faces)
            {
                foreach (Vec3 p in o.Points)
                {
                    deepest = Math.Max(deepest, d - Vec3.Dot(f.Normal, p));
                }
            }

            narrowest = Math.Min(narrowest, deepest);
        }

        return double.IsFinite(narrowest) ? narrowest : 0;
    }

    public Vec3 Centroid()
    {
        Vec3 sum = Vec3.Zero;
        int n = 0;
        foreach (CellFace f in Faces)
        {
            foreach (Vec3 p in f.Points)
            {
                sum += p;
                n++;
            }
        }

        return n == 0 ? Vec3.Zero : sum / n;
    }
}

/// <summary>A work budget (vertex visits) shared by one run, with its cancellation.</summary>
public sealed class Work
{
    public Work(long budget, CancellationToken cancel)
    {
        Left = budget;
        Cancel = cancel;
    }

    public long Left { get; private set; }

    public CancellationToken Cancel { get; }

    public bool Exhausted => Left < 0;

    /// <summary>Spends <paramref name="n"/>; false once the budget is gone. Throws when the run is cancelled.</summary>
    public bool Spend(long n)
    {
        Left -= n;
        Cancel.ThrowIfCancellationRequested();
        return Left >= 0;
    }
}

/// <summary>
/// The convex-cell geometry of brush recovery, in doubles with RE+'s tolerances (Reconstruct, the face join and the
/// growth of RE+'s RecoveredBspGeometry): cells from a BSP, cells joined where they share a whole face, and brushes
/// grown over their neighbours where the original cells cover the grown region exactly.
/// </summary>
public static class CellGeometry
{
    /// <summary>Absolute coincidence and zero-volume tolerance.</summary>
    public const double Epsilon = 1e-6;

    /// <summary>The most faces one cell may have.</summary>
    public const int MaxFaces = 256;

    public static bool Near(Vec3 a, Vec3 b, double eps) => (a - b).LengthSquared <= eps * eps;

    /// <summary>Six times the signed volume about <paramref name="reference"/>, over six.</summary>
    public static double VolumeOf(List<CellFace> faces, Vec3 reference)
    {
        double six = 0;
        foreach (CellFace f in faces)
        {
            for (int i = 1; i + 1 < f.Points.Count; i++)
            {
                six += Vec3.Dot(f.Points[0] - reference, Vec3.Cross(f.Points[i] - reference, f.Points[i + 1] - reference));
            }
        }

        return six / 6;
    }

    /// <summary>An axis-aligned box as a cell whose faces are all artificial.</summary>
    public static List<CellFace> Box(Aabb b)
    {
        Vec3 lo = b.Min, hi = b.Max;
        Vec3 P(double x, double y, double z) => new(x, y, z);
        return
        [
            new([P(hi.X, lo.Y, lo.Z), P(hi.X, hi.Y, lo.Z), P(hi.X, hi.Y, hi.Z), P(hi.X, lo.Y, hi.Z)], Vec3.UnitX, -1, true),
            new([P(lo.X, lo.Y, lo.Z), P(lo.X, lo.Y, hi.Z), P(lo.X, hi.Y, hi.Z), P(lo.X, hi.Y, lo.Z)], -Vec3.UnitX, -1, true),
            new([P(lo.X, hi.Y, lo.Z), P(lo.X, hi.Y, hi.Z), P(hi.X, hi.Y, hi.Z), P(hi.X, hi.Y, lo.Z)], Vec3.UnitY, -1, true),
            new([P(lo.X, lo.Y, lo.Z), P(hi.X, lo.Y, lo.Z), P(hi.X, lo.Y, hi.Z), P(lo.X, lo.Y, hi.Z)], -Vec3.UnitY, -1, true),
            new([P(lo.X, lo.Y, hi.Z), P(hi.X, lo.Y, hi.Z), P(hi.X, hi.Y, hi.Z), P(lo.X, hi.Y, hi.Z)], Vec3.UnitZ, -1, true),
            new([P(lo.X, lo.Y, lo.Z), P(lo.X, hi.Y, lo.Z), P(hi.X, hi.Y, lo.Z), P(hi.X, lo.Y, lo.Z)], -Vec3.UnitZ, -1, true),
        ];
    }

    private static bool HasArea(List<Vec3> points, Vec3 normal)
    {
        if (points.Count < 3)
        {
            return false;
        }

        Vec3 area = Vec3.Zero;
        for (int i = 1; i + 1 < points.Count; i++)
        {
            area += Vec3.Cross(points[i] - points[0], points[i + 1] - points[0]);
        }

        double oriented = Vec3.Dot(area, normal);
        return double.IsFinite(oriented) && oriented > Epsilon * Epsilon;
    }

    private static void AppendUnique(List<Vec3> points, Vec3 p)
    {
        if (points.Count == 0 || !Near(points[^1], p, Epsilon))
        {
            points.Add(p);
        }
    }

    /// <summary>The outcome of a clip.</summary>
    public enum ClipResult
    {
        Ok,

        /// <summary>The split left fewer than three cap corners or a reversed cap: the cell is degenerate here.</summary>
        BadCap,

        /// <summary>The work budget ran out.</summary>
        Budget,
    }

    /// <summary>
    /// Keeps the part of a convex cell behind the plane (<c>n.p &lt;= d</c>); the new cap faces along <paramref name="n"/>
    /// and carries <paramref name="surface"/>. Zero decisions are exact; collinear corners are kept so neighbouring faces
    /// get no T-junctions. An empty output is a cell wholly in front.
    /// </summary>
    public static ClipResult Clip(List<CellFace> input, Vec3 n, double d, int surface, out List<CellFace> output, Work work)
    {
        output = [];
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (CellFace f in input)
        {
            if (!work.Spend(f.Points.Count))
            {
                return ClipResult.Budget;
            }

            foreach (Vec3 p in f.Points)
            {
                double side = Vec3.Dot(n, p) - d;
                min = Math.Min(min, side);
                max = Math.Max(max, side);
            }
        }

        if (min >= -Epsilon)
        {
            return ClipResult.Ok; // wholly in front, or collapsed onto the plane
        }

        if (max <= Epsilon)
        {
            output = input;
            return ClipResult.Ok;
        }

        var cap = new List<Vec3>();
        foreach (CellFace f in input)
        {
            var kept = new List<Vec3>(f.Points.Count + 1);
            List<Vec3> v = f.Points;
            for (int i = 0; i < v.Count; i++)
            {
                Vec3 a = v[i], b = v[(i + 1) % v.Count];
                double da = Vec3.Dot(n, a) - d, db = Vec3.Dot(n, b) - d;
                if (da <= 0)
                {
                    AppendUnique(kept, a);
                }

                if ((da < 0 && db > 0) || (da > 0 && db < 0))
                {
                    Vec3 x = a + (b - a) * (da / (da - db));
                    AppendUnique(kept, x);
                    cap.Add(x);
                }
                else if (da == 0)
                {
                    cap.Add(a);
                }
            }

            if (kept.Count > 1 && Near(kept[0], kept[^1], Epsilon))
            {
                kept.RemoveAt(kept.Count - 1);
            }

            if (HasArea(kept, f.Normal))
            {
                output.Add(new CellFace(kept, f.Normal, f.Surface, f.Artificial));
            }
        }

        var unique = new List<Vec3>();
        foreach (Vec3 p in cap)
        {
            if (!unique.Exists(o => Near(o, p, Epsilon)))
            {
                unique.Add(p);
            }
        }

        if (unique.Count < 3)
        {
            output = [];
            return ClipResult.BadCap;
        }

        Vec3 centre = unique[0];
        for (int i = 1; i < unique.Count; i++)
        {
            centre += (unique[i] - centre) * (1.0 / (i + 1));
        }

        Vec3 reference = Math.Abs(n.X) < 0.8 ? Vec3.UnitX : Vec3.UnitY;
        Vec3 u = Vec3.Cross(reference, n).Normalized();
        Vec3 w = Vec3.Cross(n, u);
        Vec3 c = centre;
        unique.Sort((a, b) => Math.Atan2(Vec3.Dot(w, a - c), Vec3.Dot(u, a - c)).CompareTo(Math.Atan2(Vec3.Dot(w, b - c), Vec3.Dot(u, b - c))));
        if (!HasArea(unique, n))
        {
            output = [];
            return ClipResult.BadCap;
        }

        output.Add(new CellFace(unique, n, surface));
        return output.Count > MaxFaces ? ClipResult.BadCap : ClipResult.Ok;
    }

    /// <summary>
    /// True for a closed convex cell at the reconstruction tolerance: every face planar with area, positive volume, and
    /// every directed edge (corners welded within <see cref="Epsilon"/>) met once by its reverse.
    /// </summary>
    public static bool IsClosed(List<CellFace> cell)
    {
        if (cell.Count < 4)
        {
            return false;
        }

        var points = new List<Vec3>();
        var edges = new List<(int, int)>();
        Vec3 reference = cell[0].Points[0];
        double six = 0;
        foreach (CellFace f in cell)
        {
            if (f.Points.Count < 3 || !HasArea(f.Points, f.Normal))
            {
                return false;
            }

            var idx = new int[f.Points.Count];
            for (int k = 0; k < f.Points.Count; k++)
            {
                Vec3 p = f.Points[k];
                if (!double.IsFinite(p.X + p.Y + p.Z) || Math.Abs(Vec3.Dot(f.Normal, p - f.Points[0])) > Epsilon * 8)
                {
                    return false;
                }

                int i = 0;
                while (i < points.Count && !Near(points[i], p, Epsilon))
                {
                    i++;
                }

                if (i == points.Count)
                {
                    points.Add(p);
                }

                idx[k] = i;
            }

            for (int k = 0; k < idx.Length; k++)
            {
                edges.Add((idx[k], idx[(k + 1) % idx.Length]));
            }

            for (int i = 1; i + 1 < f.Points.Count; i++)
            {
                six += Vec3.Dot(f.Points[0] - reference, Vec3.Cross(f.Points[i] - reference, f.Points[i + 1] - reference));
            }
        }

        if (!double.IsFinite(six) || six <= Epsilon * Epsilon * Epsilon)
        {
            return false;
        }

        edges.Sort();
        for (int i = 0; i < edges.Count; i++)
        {
            (int a, int b) = edges[i];
            if (a == b || (i > 0 && edges[i - 1] == edges[i]) || edges.BinarySearch((b, a)) < 0)
            {
                return false;
            }
        }

        return true;
    }

    // ---- join ------------------------------------------------------------------------------------------------------

    /// <summary>True when every corner of <paramref name="points"/> lies behind (within <see cref="Epsilon"/>) every
    /// face plane of <paramref name="planes"/> but <paramref name="excluded"/>.</summary>
    private static bool BehindPlanes(Cell planes, Cell points, int excluded)
    {
        for (int i = 0; i < planes.Faces.Count; i++)
        {
            if (i == excluded)
            {
                continue;
            }

            CellFace plane = planes.Faces[i];
            double d = plane.Distance;
            foreach (CellFace f in points.Faces)
            {
                foreach (Vec3 p in f.Points)
                {
                    if (Vec3.Dot(plane.Normal, p) - d > Epsilon)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool ContainsFace(CellFace outer, CellFace inner)
    {
        for (int i = 0; i < outer.Points.Count; i++)
        {
            Vec3 start = outer.Points[i];
            Vec3 edge = outer.Points[(i + 1) % outer.Points.Count] - start;
            double length = edge.Length;
            if (length <= Epsilon)
            {
                return false;
            }

            foreach (Vec3 p in inner.Points)
            {
                if (Vec3.Dot(outer.Normal, Vec3.Cross(edge, p - start)) < -Epsilon * length)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Rebuilds a convex region from a box around <paramref name="box"/> cut by the given planes; null when
    /// the planes do not close it or the result is not a closed cell.</summary>
    private static List<CellFace>? Hull(Aabb box, IEnumerable<CellFace> planes, Work work)
    {
        List<CellFace> cell = Box(box.Expand(1));
        foreach (CellFace f in planes)
        {
            if (Clip(cell, f.Normal, f.Distance, f.Surface, out List<CellFace> next, work) != ClipResult.Ok)
            {
                return null;
            }

            cell = next;
            if (cell.Count == 0 || cell.Count > MaxFaces)
            {
                return null;
            }
        }

        return cell.Exists(f => f.Artificial) || !IsClosed(cell) ? null : cell;
    }

    private static Cell? TryJoin(Cell a, int aFace, Cell b, int bFace, Work work)
    {
        CellFace first = a.Faces[aFace], second = b.Faces[bFace];
        if (!Near(first.Normal, -second.Normal, 1e-12))
        {
            return null;
        }

        double d = first.Distance;
        if (second.Points.Exists(p => Math.Abs(Vec3.Dot(first.Normal, p) - d) > Epsilon)
            || !ContainsFace(first, second) || !ContainsFace(second, first) || !BehindPlanes(a, b, aFace) || !BehindPlanes(b, a, bFace))
        {
            return null;
        }

        Aabb box = a.Bounds().Union(b.Bounds());
        double area = a.Faces.Sum(f => f.Area()) + b.Faces.Sum(f => f.Area());
        IEnumerable<CellFace> planes = a.Faces.Where((_, i) => i != aFace).Concat(b.Faces.Where((_, i) => i != bFace));
        if (Hull(box, planes, work) is not { } cell)
        {
            return null;
        }

        Vec3 reference = box.Min;
        double before = VolumeOf(a.Faces, reference) + VolumeOf(b.Faces, reference), after = VolumeOf(cell, reference);
        return before > 0 && after > 0 && Math.Abs(after - before) <= Epsilon * area + Math.Abs(before) * 1e-12 ? new Cell(cell) : null;
    }

    /// <summary>
    /// Joins cells that share a whole face where their union is convex and rebuilds the same volume (RE+'s
    /// MergeAdjacentConvexBrushes), in passes, largest shared face first, each cell joining once per pass.
    /// </summary>
    public static List<Cell> Join(List<Cell> input, Work work)
    {
        List<Cell> cells = [.. input];
        for (int pass = 0; pass < 64; pass++)
        {
            var groups = new Dictionary<(double, double, double, long), List<(int Cell, int Face, Aabb Box)>>();
            var candidates = new List<(int A, int B, int AFace, int BFace, double Area)>();
            for (int bi = 0; bi < cells.Count; bi++)
            {
                for (int fi = 0; fi < cells[bi].Faces.Count; fi++)
                {
                    CellFace f = cells[bi].Faces[fi];
                    Vec3 n = f.Normal;
                    if (n.X < 0 || (n.X == 0 && n.Y < 0) || (n.X == 0 && n.Y == 0 && n.Z < 0))
                    {
                        n = -n;
                    }

                    long bucket = (long)Math.Floor(Vec3.Dot(n, f.Points[0]));
                    Aabb box = f.Bounds();
                    for (long o = -1; o <= 1; o++)
                    {
                        if (!groups.TryGetValue((n.X, n.Y, n.Z, bucket + o), out var found))
                        {
                            continue;
                        }

                        foreach ((int oc, int of, Aabb ob) in found)
                        {
                            if (oc == bi || !Near(box.Min, ob.Min, Epsilon) || !Near(box.Max, ob.Max, Epsilon)
                                || Vec3.Dot(cells[oc].Faces[of].Normal, f.Normal) > 0)
                            {
                                continue;
                            }

                            candidates.Add((oc, bi, of, fi, f.Area()));
                        }
                    }

                    if (!groups.TryGetValue((n.X, n.Y, n.Z, bucket), out var list))
                    {
                        groups[(n.X, n.Y, n.Z, bucket)] = list = [];
                    }

                    list.Add((bi, fi, box));
                }
            }

            candidates.Sort((x, y) => x.Area != y.Area ? y.Area.CompareTo(x.Area)
                : (x.A, x.B, x.AFace, x.BFace).CompareTo((y.A, y.B, y.AFace, y.BFace)));
            var changed = new bool[cells.Count];
            var removed = new bool[cells.Count];
            int merged = 0;
            foreach ((int a, int b, int af, int bf, _) in candidates)
            {
                if (changed[a] || changed[b] || work.Exhausted)
                {
                    continue;
                }

                if (TryJoin(cells[a], af, cells[b], bf, work) is not { } joined)
                {
                    continue;
                }

                joined.Members = [.. cells[a].Members, .. cells[b].Members];
                cells[a] = joined;
                removed[b] = true;
                changed[a] = changed[b] = true;
                merged++;
            }

            if (merged == 0)
            {
                break;
            }

            cells = cells.Where((_, i) => !removed[i]).ToList();
        }

        return cells;
    }

    // ---- growth ----------------------------------------------------------------------------------------------------

    /// <summary>What <see cref="Grow"/> did.</summary>
    public sealed record GrowStats(int Grown, int Absorbed, bool BudgetReached);

    private static bool BoundsMeet(Aabb a, Aabb b, double margin) =>
        a.Min.X <= b.Max.X + margin && b.Min.X <= a.Max.X + margin &&
        a.Min.Y <= b.Max.Y + margin && b.Min.Y <= a.Max.Y + margin &&
        a.Min.Z <= b.Max.Z + margin && b.Min.Z <= a.Max.Z + margin;

    private static bool ShareFace(Cell a, Cell b)
    {
        foreach (CellFace f in a.Faces)
        {
            Aabb fb = f.Bounds();
            double d = f.Distance;
            foreach (CellFace g in b.Faces)
            {
                if (Vec3.Dot(f.Normal, g.Normal) > -1 + 1e-9 || Math.Abs(Vec3.Dot(f.Normal, g.Points[0]) - d) > Epsilon)
                {
                    continue;
                }

                if (BoundsMeet(fb, g.Bounds(), Epsilon))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A uniform grid of the reference cells' bounds.</summary>
    private sealed class CellGrid
    {
        private readonly Aabb _extent;
        private readonly int _size;
        private readonly double[] _step = new double[3];
        private readonly List<int>[] _buckets;

        public CellGrid(IReadOnlyList<Aabb> bounds)
        {
            _extent = Aabb.Empty;
            foreach (Aabb b in bounds)
            {
                _extent = _extent.Union(b);
            }

            _size = (int)Math.Min(48, Math.Max(1, Math.Cbrt(bounds.Count)));
            Vec3 span = _extent.Max - _extent.Min;
            for (int axis = 0; axis < 3; axis++)
            {
                _step[axis] = Math.Max(span[axis] / _size, Epsilon * 16);
            }

            _buckets = new List<int>[_size * _size * _size];
            for (int i = 0; i < _buckets.Length; i++)
            {
                _buckets[i] = [];
            }

            for (int i = 0; i < bounds.Count; i++)
            {
                (int[] lo, int[] hi) = Range(bounds[i]);
                for (int x = lo[0]; x <= hi[0]; x++)
                {
                    for (int y = lo[1]; y <= hi[1]; y++)
                    {
                        for (int z = lo[2]; z <= hi[2]; z++)
                        {
                            _buckets[(x * _size + y) * _size + z].Add(i);
                        }
                    }
                }
            }
        }

        public (int[] Lo, int[] Hi) Range(Aabb box)
        {
            var lo = new int[3];
            var hi = new int[3];
            for (int axis = 0; axis < 3; axis++)
            {
                lo[axis] = Math.Clamp((int)Math.Floor((box.Min[axis] - _extent.Min[axis]) / _step[axis]), 0, _size - 1);
                hi[axis] = Math.Clamp((int)Math.Floor((box.Max[axis] - _extent.Min[axis]) / _step[axis]), 0, _size - 1);
            }

            return (lo, hi);
        }

        public List<int> At(Vec3 p)
        {
            (int[] lo, _) = Range(new Aabb(p, p));
            return _buckets[(lo[0] * _size + lo[1]) * _size + lo[2]];
        }

        public IEnumerable<int> In(Aabb box)
        {
            (int[] lo, int[] hi) = Range(box);
            for (int x = lo[0]; x <= hi[0]; x++)
            {
                for (int y = lo[1]; y <= hi[1]; y++)
                {
                    for (int z = lo[2]; z <= hi[2]; z++)
                    {
                        foreach (int i in _buckets[(x * _size + y) * _size + z])
                        {
                            yield return i;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Grows brushes over their neighbours (RE+'s GrowConvexBrushes, with overlap allowed as RE+ runs it): for a pair of
    /// brushes sharing a face region, or both touching a common neighbour, the region their faces close around both
    /// (only the faces that support the other brush, no invented plane) replaces them when the original
    /// <paramref name="reference"/> cells cover it exactly (clipped volumes summing to the region's own). Brushes wholly
    /// inside a grown one are absorbed. Largest combined volume first; a refused pair is not tried again until one of
    /// its brushes changes. Each output brush lists in <see cref="Cell.Members"/> the input brushes it replaces.
    /// </summary>
    public static (List<Cell> Brushes, GrowStats Stats) Grow(IReadOnlyList<Cell> reference, IReadOnlyList<Cell> input, Work work)
    {
        var cellBounds = reference.Select(c => c.Bounds()).ToList();
        var cellVolumes = reference.Select(c => c.Volume()).ToList();
        var grid = new CellGrid(cellBounds);
        var stamp = new int[reference.Count];
        int query = 0;

        bool InsideReference(Vec3 p)
        {
            foreach (int i in grid.At(p))
            {
                Aabb b = cellBounds[i];
                if (p.X < b.Min.X - Epsilon || p.X > b.Max.X + Epsilon || p.Y < b.Min.Y - Epsilon || p.Y > b.Max.Y + Epsilon
                    || p.Z < b.Min.Z - Epsilon || p.Z > b.Max.Z + Epsilon)
                {
                    continue;
                }

                if (reference[i].Faces.TrueForAll(f => Vec3.Dot(f.Normal, p) - f.Distance <= Epsilon))
                {
                    return true;
                }
            }

            return false;
        }

        var brushes = new List<Cell>();
        var identity = new List<long>();
        long nextIdentity = 0;
        for (int i = 0; i < input.Count; i++)
        {
            brushes.Add(new Cell(input[i].Faces) { Members = [i] });
            identity.Add(nextIdentity++);
        }

        var rejected = new HashSet<(long, long)>();
        int grown = 0, absorbed = 0;
        bool stopped = false;
        for (int pass = 0; pass < 64 && !stopped; pass++)
        {
            var bounds = brushes.Select(b => b.Bounds()).ToList();
            var volume = brushes.Select(b => b.Volume()).ToList();
            int[] order = Enumerable.Range(0, brushes.Count).OrderBy(i => bounds[i].Min.X).ThenBy(i => i).ToArray();
            var pairs = new HashSet<(int, int)>();
            var neighbours = new List<int>[brushes.Count];
            for (int i = 0; i < neighbours.Length; i++)
            {
                neighbours[i] = [];
            }

            for (int i = 0; i < order.Length && !stopped; i++)
            {
                for (int j = i + 1; j < order.Length; j++)
                {
                    int a = order[i], b = order[j];
                    if (bounds[b].Min.X > bounds[a].Max.X + Epsilon)
                    {
                        break;
                    }

                    if (!work.Spend(1))
                    {
                        stopped = true;
                        break;
                    }

                    if (!BoundsMeet(bounds[a], bounds[b], Epsilon) || !ShareFace(brushes[a], brushes[b]))
                    {
                        continue;
                    }

                    neighbours[a].Add(b);
                    neighbours[b].Add(a);
                    pairs.Add((Math.Min(a, b), Math.Max(a, b)));
                }
            }

            // Brushes on either side of a common neighbour can span it: the arms of a crossing become one brush.
            foreach (List<int> around in neighbours)
            {
                for (int i = 0; i < around.Count && !stopped; i++)
                {
                    for (int j = i + 1; j < around.Count; j++)
                    {
                        if (pairs.Count >= 2_000_000 || !work.Spend(1))
                        {
                            stopped = true;
                            break;
                        }

                        pairs.Add((Math.Min(around[i], around[j]), Math.Max(around[i], around[j])));
                    }
                }
            }

            if (stopped)
            {
                break;
            }

            // Largest combined volume first: rooms absorb their fragments before small neighbours claim them.
            List<(int A, int B)> sorted = pairs.OrderByDescending(p => volume[p.Item1] + volume[p.Item2]).ThenBy(p => p.Item1).ThenBy(p => p.Item2).ToList();
            var changed = new bool[brushes.Count];
            var removed = new bool[brushes.Count];
            int merged = 0;
            foreach ((int a, int b) in sorted)
            {
                if (changed[a] || changed[b] || removed[a] || removed[b])
                {
                    continue;
                }

                (long, long) key = (Math.Min(identity[a], identity[b]), Math.Max(identity[a], identity[b]));
                if (rejected.Contains(key))
                {
                    continue;
                }

                List<CellFace>? hull = GrowRegion(brushes[a], brushes[b], bounds[a].Union(bounds[b]), work);
                if (work.Exhausted)
                {
                    stopped = true;
                    break;
                }

                if (hull is null || !Plausible(hull, InsideReference))
                {
                    rejected.Add(key);
                    continue;
                }

                double hullArea = hull.Sum(f => f.Area());
                double hullVolume = VolumeOf(hull, hull[0].Points[0]);
                Aabb hullBounds = Aabb.Of(hull.SelectMany(f => f.Points));
                query++;
                double covered = 0;
                bool measured = true;
                foreach (int ci in grid.In(hullBounds))
                {
                    if (stamp[ci] == query)
                    {
                        continue;
                    }

                    stamp[ci] = query;
                    if (!BoundsMeet(cellBounds[ci], hullBounds, -Epsilon))
                    {
                        continue;
                    }

                    // Whole cells inside or outside need no clipping.
                    Cell cell = reference[ci];
                    bool outside = false, within = true;
                    foreach (CellFace f in hull)
                    {
                        double nearest = double.PositiveInfinity, farthest = double.NegativeInfinity, d = f.Distance;
                        foreach (CellFace cf in cell.Faces)
                        {
                            foreach (Vec3 p in cf.Points)
                            {
                                double side = Vec3.Dot(f.Normal, p) - d;
                                nearest = Math.Min(nearest, side);
                                farthest = Math.Max(farthest, side);
                            }
                        }

                        if (nearest >= -Epsilon)
                        {
                            outside = true;
                            break;
                        }

                        if (farthest > Epsilon)
                        {
                            within = false;
                        }
                    }

                    if (outside)
                    {
                        continue;
                    }

                    if (within)
                    {
                        covered += cellVolumes[ci];
                        continue;
                    }

                    List<CellFace> clipped = cell.Faces;
                    foreach (CellFace f in hull)
                    {
                        if (Clip(clipped, f.Normal, f.Distance, -1, out List<CellFace> next, work) != ClipResult.Ok)
                        {
                            measured = false;
                            break;
                        }

                        clipped = next;
                        if (clipped.Count == 0)
                        {
                            break;
                        }
                    }

                    if (!measured)
                    {
                        break;
                    }

                    if (clipped.Count > 0)
                    {
                        covered += VolumeOf(clipped, clipped[0].Points[0]);
                    }
                }

                if (work.Exhausted)
                {
                    stopped = true;
                    break;
                }

                if (!measured || !double.IsFinite(covered) || hullVolume <= 0 || Math.Abs(covered - hullVolume) > Epsilon * hullArea + hullVolume * 1e-12)
                {
                    rejected.Add(key);
                    continue;
                }

                var grownCell = new Cell(hull) { Members = [.. brushes[a].Members, .. brushes[b].Members] };
                brushes[a] = grownCell;
                identity[a] = nextIdentity++;
                changed[a] = true;
                removed[b] = true;
                merged++;
                grown++;
                for (int other = 0; other < brushes.Count; other++)
                {
                    if (other == a || removed[other] || !BoundsMeet(bounds[other], hullBounds, Epsilon))
                    {
                        continue;
                    }

                    bool inside = brushes[other].Faces.TrueForAll(f => f.Points.TrueForAll(p => hull.TrueForAll(h => Vec3.Dot(h.Normal, p) - h.Distance <= Epsilon)));
                    if (!inside)
                    {
                        continue;
                    }

                    removed[other] = true;
                    grownCell.Members.AddRange(brushes[other].Members);
                    merged++;
                    absorbed++;
                }
            }

            var keep = new List<int>();
            for (int i = 0; i < brushes.Count; i++)
            {
                if (!removed[i])
                {
                    keep.Add(i);
                }
            }

            brushes = keep.Select(i => brushes[i]).ToList();
            identity = keep.Select(i => identity[i]).ToList();
            if (merged == 0)
            {
                break;
            }
        }

        foreach (Cell b in brushes)
        {
            b.Members.Sort();
        }

        return (brushes, new GrowStats(grown, absorbed, stopped));
    }

    /// <summary>The region the faces of <paramref name="a"/> and <paramref name="b"/> that support the other brush close
    /// around both, or null when they do not close it.</summary>
    private static List<CellFace>? GrowRegion(Cell a, Cell b, Aabb box, Work work)
    {
        var planes = new List<CellFace>();
        foreach ((Cell self, Cell other) in new[] { (a, b), (b, a) })
        {
            foreach (CellFace f in self.Faces)
            {
                double d = f.Distance;
                if (other.Faces.TrueForAll(o => o.Points.TrueForAll(p => Vec3.Dot(f.Normal, p) - d <= Epsilon)))
                {
                    planes.Add(f);
                }
            }
        }

        return work.Spend(planes.Count) ? Hull(box, planes, work) : null;
    }

    /// <summary>A cheap test before measuring volumes: points of the region pulled towards its centre must each lie in
    /// some reference cell, or the region reaches solid space.</summary>
    private static bool Plausible(List<CellFace> hull, Func<Vec3, bool> inside)
    {
        Vec3 centre = Vec3.Zero;
        int corners = 0;
        foreach (CellFace f in hull)
        {
            foreach (Vec3 p in f.Points)
            {
                centre += p;
                corners++;
            }
        }

        centre /= corners;
        if (!inside(centre))
        {
            return false;
        }

        ReadOnlySpan<double> depths = [1e-3, 0.25, 0.5, 0.75];
        foreach (CellFace f in hull)
        {
            Vec3 middle = Vec3.Zero;
            for (int i = 0; i < f.Points.Count; i++)
            {
                Vec3 p = f.Points[i];
                Vec3 edge = (p + f.Points[(i + 1) % f.Points.Count]) * 0.5;
                middle += p;
                foreach (double depth in depths)
                {
                    if (!inside(p + (centre - p) * depth) || !inside(edge + (centre - edge) * depth))
                    {
                        return false;
                    }
                }
            }

            middle /= f.Points.Count;
            foreach (double depth in depths)
            {
                if (!inside(middle + (centre - middle) * depth))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
