using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Snap;

/// <summary>
/// The faces a drag can stick to, in a uniform grid (256-unit cells) built once when the drag starts: nothing outside
/// the selection changes during a drag (any other edit ends it), so the grid never goes stale. Brush faces go in at
/// build time; actors are only bucketed by location (in coarser cells) and turned into faces the first time a query
/// comes near them, then kept. A face spanning more than <see cref="BigCells"/> cells goes on a list every query scans.
/// Queries allocate nothing: duplicates are dropped with a per-query stamp.
/// </summary>
public sealed class SnapNeighbours
{
    /// <summary>Faces spanning more cells than this are kept on the big list.</summary>
    public const int BigCells = 64;

    /// <summary>How far an actor's box can reach from its location (the largest mesh half-size the index holds, about).</summary>
    public const double ActorReach = 2048;

    private const double ActorCell = 1024;

    /// <summary>A query spanning more cells than this scans every face instead.</summary>
    private const long MaxQueryCells = 4096;

    private readonly double _cell;
    private readonly List<SnapFace> _faces = new();
    private readonly List<int> _stamps = new();
    private readonly Dictionary<(int, int, int), List<int>> _grid = new();
    private readonly List<int> _big = new();
    private readonly Dictionary<(int, int, int), List<int>> _actorGrid = new();
    private readonly List<(ObjectRef Ref, Vec3 Location)> _actors = new();
    private readonly List<IReadOnlyList<SnapFace>?> _actorFaces = new();
    private readonly List<int> _actorStamps = new();
    private readonly Func<ObjectRef, IReadOnlyList<SnapFace>> _resolve;
    private Vec3 _min = new(double.MaxValue, double.MaxValue, double.MaxValue);
    private Vec3 _max = new(double.MinValue, double.MinValue, double.MinValue);
    private int _stamp;

    private SnapNeighbours(double cell, Func<ObjectRef, IReadOnlyList<SnapFace>> resolve, ISnapSweeper? baked)
    {
        _cell = cell;
        _resolve = resolve;
        Baked = baked;
    }

    /// <summary>The baked world, when the map has one loaded and it is a target.</summary>
    public ISnapSweeper? Baked { get; }

    /// <summary>The faces in the grid (brush faces and actor faces made so far).</summary>
    public int FaceCount => _faces.Count;

    public int ActorCount => _actors.Count;

    /// <summary>Builds the grid from neighbour faces and actors. <paramref name="actorFaces"/> makes an actor's faces
    /// the first time a query comes near its location.</summary>
    public static SnapNeighbours Build(IEnumerable<SnapFace> faces, IEnumerable<(ObjectRef Ref, Vec3 Location)> actors,
        Func<ObjectRef, IReadOnlyList<SnapFace>> actorFaces, ISnapSweeper? baked, double cell = 256)
    {
        var n = new SnapNeighbours(cell, actorFaces, baked);
        foreach (SnapFace f in faces)
        {
            n.Add(f);
        }

        foreach ((ObjectRef r, Vec3 at) in actors)
        {
            int i = n._actors.Count;
            n._actors.Add((r, at));
            n._actorFaces.Add(null);
            n._actorStamps.Add(0);
            (int, int, int) key = (Cell(at.X, ActorCell), Cell(at.Y, ActorCell), Cell(at.Z, ActorCell));
            if (!n._actorGrid.TryGetValue(key, out List<int>? list))
            {
                n._actorGrid[key] = list = new List<int>();
            }

            list.Add(i);
        }

        return n;
    }

    /// <summary>Builds the grid from a project: the brushes <paramref name="include"/> takes (their faces from
    /// <paramref name="faces"/>, else <see cref="SnapFaces.OfBrush"/>) and the actors, boxed by <paramref name="box"/>.</summary>
    public static SnapNeighbours Build(MapProject p, Func<Brush, bool> include, Func<Brush, IReadOnlyList<SnapFace>>? faces,
        IEnumerable<Actor> actors, Func<Actor, SnapBox?> box, ISnapSweeper? baked, double cell = 256)
    {
        Func<Brush, IReadOnlyList<SnapFace>> of = faces ?? SnapFaces.OfBrush;
        var byId = new Dictionary<string, Actor>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in actors)
        {
            byId[a.Id] = a;
        }

        return Build(p.Brushes.Where(include).SelectMany(of), byId.Values.Select(a => (ObjectRef.Actor(a.Id), a.Location)),
            r => byId.TryGetValue(r.Id, out Actor? a) && box(a) is { } b ? SnapFaces.OfActor(a, b) : [], baked, cell);
    }

    /// <summary>Adds to <paramref name="into"/> each face whose bounds meet the box, once.</summary>
    public void Near(Vec3 min, Vec3 max, List<SnapFace> into)
    {
        // Actors near the box become faces first (their boxes reach up to ActorReach from their locations).
        if (_actors.Count > 0)
        {
            ResolveActors(min - Vec3.One * ActorReach, max + Vec3.One * ActorReach);
        }

        if (_faces.Count == 0)
        {
            return;
        }

        int stamp = ++_stamp;
        Vec3 lo = Vec3.Max(min, _min), hi = Vec3.Min(max, _max);
        if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z)
        {
            ScanBig(min, max, into, stamp);
            return;
        }

        (int x0, int y0, int z0) = (Cell(lo.X, _cell), Cell(lo.Y, _cell), Cell(lo.Z, _cell));
        (int x1, int y1, int z1) = (Cell(hi.X, _cell), Cell(hi.Y, _cell), Cell(hi.Z, _cell));
        long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
        if (cells > MaxQueryCells || cells > _grid.Count)
        {
            // Wider than the map's own cells: walking every face is cheaper than walking the cells.
            for (int i = 0; i < _faces.Count; i++)
            {
                Take(i, min, max, into, stamp);
            }

            return;
        }

        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (_grid.TryGetValue((x, y, z), out List<int>? list))
                    {
                        foreach (int i in list)
                        {
                            Take(i, min, max, into, stamp);
                        }
                    }
                }
            }
        }

        ScanBig(min, max, into, stamp);
    }

    private void ScanBig(Vec3 min, Vec3 max, List<SnapFace> into, int stamp)
    {
        foreach (int i in _big)
        {
            Take(i, min, max, into, stamp);
        }
    }

    private void Take(int i, Vec3 min, Vec3 max, List<SnapFace> into, int stamp)
    {
        if (_stamps[i] == stamp)
        {
            return;
        }

        _stamps[i] = stamp;
        SnapFace f = _faces[i];
        if (f.Min.X <= max.X && f.Max.X >= min.X && f.Min.Y <= max.Y && f.Max.Y >= min.Y && f.Min.Z <= max.Z && f.Max.Z >= min.Z)
        {
            into.Add(f);
        }
    }

    private void Add(SnapFace f)
    {
        int i = _faces.Count;
        _faces.Add(f);
        _stamps.Add(0);
        _min = Vec3.Min(_min, f.Min);
        _max = Vec3.Max(_max, f.Max);
        (int x0, int y0, int z0) = (Cell(f.Min.X, _cell), Cell(f.Min.Y, _cell), Cell(f.Min.Z, _cell));
        (int x1, int y1, int z1) = (Cell(f.Max.X, _cell), Cell(f.Max.Y, _cell), Cell(f.Max.Z, _cell));
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > BigCells)
        {
            _big.Add(i);
            return;
        }

        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (!_grid.TryGetValue((x, y, z), out List<int>? list))
                    {
                        _grid[(x, y, z)] = list = new List<int>();
                    }

                    list.Add(i);
                }
            }
        }
    }

    private void ResolveActors(Vec3 min, Vec3 max)
    {
        int stamp = ++_stamp;
        (int x0, int y0, int z0) = (Cell(min.X, ActorCell), Cell(min.Y, ActorCell), Cell(min.Z, ActorCell));
        (int x1, int y1, int z1) = (Cell(max.X, ActorCell), Cell(max.Y, ActorCell), Cell(max.Z, ActorCell));
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > _actorGrid.Count)
        {
            for (int i = 0; i < _actors.Count; i++)
            {
                ResolveActor(i, min, max, stamp);
            }

            return;
        }

        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (_actorGrid.TryGetValue((x, y, z), out List<int>? list))
                    {
                        foreach (int i in list)
                        {
                            ResolveActor(i, min, max, stamp);
                        }
                    }
                }
            }
        }
    }

    private void ResolveActor(int i, Vec3 min, Vec3 max, int stamp)
    {
        if (_actorFaces[i] is not null || _actorStamps[i] == stamp)
        {
            return;
        }

        _actorStamps[i] = stamp;
        Vec3 at = _actors[i].Location;
        if (at.X < min.X || at.X > max.X || at.Y < min.Y || at.Y > max.Y || at.Z < min.Z || at.Z > max.Z)
        {
            return;
        }

        IReadOnlyList<SnapFace> faces = _resolve(_actors[i].Ref);
        _actorFaces[i] = faces;
        foreach (SnapFace f in faces)
        {
            Add(f);
        }
    }

    private static int Cell(double v, double size) => (int)Math.Floor(Math.Clamp(v / size, -1e6, 1e6));
}
