using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Compile;

/// <summary>A zone actor (ZoneInfo, SkyZoneInfo...) that names the zone it stands in.</summary>
public sealed record ZoneSeed(string ActorId, Vec3 Location, bool Sky);

/// <summary>A leaf portal: a piece of the boundary between two empty leaves, on a plane facing out of
/// <see cref="From"/> into <see cref="To"/> (leaf indices). Zone portal sheets do not stop leaf portals.</summary>
public sealed record LeafPortal(int From, int To, Poly Piece);

/// <summary>One zone of the result. Zone 0 is the LevelInfo's default zone.</summary>
public sealed record ZoneEntry(int Index, string? ActorId, bool Sky, Aabb Bounds);

/// <summary>A zone actor the zones could not give a zone of its own, and why.</summary>
public sealed record ZoneProblem(string ActorId, string Message);

/// <summary>A source map's zones: its zone at a point, and its zone actors by zone number.</summary>
public sealed record SourceZones(Func<Vec3, int> ZoneAt, IReadOnlyList<string?> Actors);

/// <summary>How the sealed spaces get zone numbers.</summary>
public sealed record ZoneNumbering
{
    /// <summary>The highest zone number of the original engine (FBspNode::MAX_ZONES is 64, zone 0 included).</summary>
    public const int OriginalMaxZone = 63;

    /// <summary>The highest zone number of an SVMap (zone numbers are bytes, at most 255 zones).</summary>
    public const int SvMapMaxZone = 254;

    /// <summary>Spaces past this many share zone numbers.</summary>
    public required int MaxZone { get; init; }

    /// <summary>When set, each space takes the number of the source map's zone it lies in, and the zones are the
    /// source's: a map that keeps the baked render keeps its lighting, which names the zones by the source's numbers
    /// (fog, ambient, the sky zone, own-zone lights), so the collision rebuilt from the brushes must number them alike.</summary>
    public SourceZones? Source { get; init; }

    public static ZoneNumbering For(TargetGame target) => new() { MaxZone = target == TargetGame.Original ? OriginalMaxZone : SvMapMaxZone };
}

public sealed class ZoneResult
{
    public required IReadOnlyList<ZoneEntry> Zones { get; init; }

    /// <summary>Portal hops between zones (ULevel ZoneDist), 255 when unreachable; rows and columns by zone index.</summary>
    public required byte[,] Distance { get; init; }

    /// <summary>The sealed spaces (empty space parted by zone portals).</summary>
    public int Spaces { get; init; }

    /// <summary>True when there were more spaces than zone numbers, so some share one.</summary>
    public bool Wrapped { get; init; }

    /// <summary>Zone actors in solid space, or with no zone of their own.</summary>
    public List<ZoneProblem> Problems { get; } = new();

    /// <summary>Zone actors whose space shares its number with an earlier zone actor's space (more spaces than numbers).</summary>
    public List<(string ActorId, string OtherId, int Zone)> Shared { get; } = new();

    /// <summary>Zone actors that share a zone with an earlier one: the space between them is not sealed by portals.</summary>
    public List<ZoneLeak> Leaks { get; } = new();

    /// <summary>Zone portal brushes that seal nothing.</summary>
    public List<ZonePortalProblem> Portals { get; } = new();

    /// <summary>No zones: a world built only to be drawn (its collision and zones are another world's).</summary>
    public static ZoneResult None() => new() { Zones = [new ZoneEntry(0, null, false, new Aabb(Vec3.Zero, Vec3.Zero))], Distance = new byte[1, 1] };

    /// <summary>Every leaf's portals into the empty leaves next to it, from each leaf's side.</summary>
    public IReadOnlyList<LeafPortal> LeafPortals { get; init; } = [];
}

/// <summary>
/// UE2-style zones over a solid BSP: portal sheets split the empty leaves they cross (NotCsg nodes), the empty leaves
/// are connected through the faces they share, connections lying on a portal sheet are cut, and every connected empty
/// space is a zone, whether or not a zone actor stands in it (one without is the LevelInfo's), as the stock editor
/// builds them.
/// </summary>
public static class ZoneBuilder
{
    private const double MinArea = 0.5;

    /// <summary>A sealed space thinner than this (its volume over its largest face), uu, is a sliver, not a zone.</summary>
    private const double SliverThickness = 0.25;

    public static ZoneResult Build(BspTree tree, IReadOnlyList<Poly> portals, IReadOnlyList<ZoneSeed> seeds, Aabb world, ZoneNumbering numbering)
    {
        foreach (Poly sheet in portals)
        {
            InsertPortal(tree, tree.Root, sheet);
        }

        tree.Reindex();
        Aabb box = world.IsEmpty ? new Aabb(new Vec3(-1024, -1024, -1024), new Vec3(1024, 1024, 1024)) : world.Expand(512);
        int n = tree.Leaves.Count;
        var cells = new List<CellFace>?[n];
        Parallel.For(0, n, i =>
        {
            if (tree.Leaves[i].Empty)
            {
                cells[i] = Polytope(tree.Leaves[i], box);
            }
        });

        // Connect empty leaves through shared faces; faces on a portal sheet connect zones only for ZoneDist.
        var parent = Enumerable.Range(0, n).ToArray();
        // Leaf pairs a portal sheet stands between, and pairs a sheet covers part of the face between, with the sheet's index.
        var portalPairs = new List<(int A, int B, int Sheet)>();
        var sheetTouches = new List<(int A, int B, int Sheet)>();
        // Open area between two leaves (past any portal), its area-weighted centre and the faces, from each leaf's faces.
        var openings = new Dictionary<(int From, int To), (double Area, Vec3 Sum, List<Poly> Pieces)>();
        var leafPortals = new List<LeafPortal>();
        for (int i = 0; i < n; i++)
        {
            if (cells[i] is not { } faces)
            {
                continue;
            }

            foreach ((Poly face, BspNode? node, bool leafInFront) in faces)
            {
                if (node is null)
                {
                    continue;
                }

                var hits = new List<(BspLeaf, Poly)>();
                Filter(leafInFront ? node.Back : node.Front, face, face.Plane.N, hits);
                foreach ((BspLeaf leaf, Poly piece) in hits)
                {
                    if (!leaf.Empty || leaf.Index == i || piece.Area() < MinArea)
                    {
                        continue;
                    }

                    leafPortals.Add(new LeafPortal(i, leaf.Index, piece));

                    double open = OpenArea(piece, portals, out List<int> covering);
                    foreach (int sheet in covering)
                    {
                        sheetTouches.Add((i, leaf.Index, sheet));
                    }

                    if (open > 1.0)
                    {
                        Union(parent, i, leaf.Index);
                        (double area, Vec3 sum, List<Poly>? pieces) = openings.GetValueOrDefault((i, leaf.Index));
                        pieces ??= [];
                        pieces.Add(piece);
                        openings[(i, leaf.Index)] = (area + open, sum + piece.Centroid() * open, pieces);
                    }
                    else
                    {
                        portalPairs.Add((i, leaf.Index, covering.Count > 0 ? covering[^1] : -1));
                    }
                }
            }
        }

        var sliverOf = new int[n];
        HashSet<int> slivers = JoinSlivers(cells, parent, portalPairs, sliverOf);

        var cellBounds = new Aabb[n];
        for (int i = 0; i < n; i++)
        {
            cellBounds[i] = cells[i] is { } faces ? faces.Aggregate(Aabb.Empty, (b, f) => b.Union(f.Poly.Bounds())) : Aabb.Empty;
        }

        var leakSearch = new ZoneLeaks(openings, cellBounds);

        // Every sealed space is a zone, whether or not a zone actor stands in it, as the stock editor makes them. Zone 0
        // only holds solid space, what is outside the world and the slivers that border no space.
        var spaces = new List<int>();
        var isSpace = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(parent, i);
            if (tree.Leaves[i].Empty && !slivers.Contains(root) && isSpace.Add(root))
            {
                spaces.Add(root);
            }
        }

        // The space each zone actor stands in, or -1 (solid, or a sliver that borders no space).
        var seedSpace = new int[seeds.Count];
        var seedLeaf = new int[seeds.Count];
        for (int k = 0; k < seeds.Count; k++)
        {
            BspLeaf leaf = tree.LeafAt(seeds[k].Location);
            seedLeaf[k] = leaf.Index;
            int root = Find(parent, leaf.Index);
            seedSpace[k] = leaf.Empty && isSpace.Contains(root) ? root : -1;
        }

        SourceZones? source = numbering.Source;
        Dictionary<int, int> zoneOf = source is not null
            ? NumberBySource(source, spaces, cells, parent, isSpace)
            : Number(spaces, seeds, seedSpace, numbering.MaxZone);
        int count = source is not null ? Math.Max(source.Actors.Count, 1) : Math.Min(spaces.Count, numbering.MaxZone) + 1;

        // Each zone takes the first zone actor in it (actor order); a zone without one is the LevelInfo's. Numbered by
        // a source map, the zones are the source's and keep its zone actors.
        var entries = new (string? Id, Vec3 At, bool Sky)[count];
        var sourceIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            Dictionary<string, ZoneSeed> byId = seeds.GroupBy(z => z.ActorId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            for (int z = 1; z < count; z++)
            {
                if (source.Actors[z] is { } id && byId.TryGetValue(id, out ZoneSeed? seed))
                {
                    entries[z] = (seed.ActorId, seed.Location, seed.Sky);
                    sourceIndex.TryAdd(id, z);
                }
            }
        }

        string Holder(int zone) => zone > 0 && entries[zone].Id is { } holder ? $" ({holder})" : "";
        var firstIn = new Dictionary<int, int>();
        var problems = new List<ZoneProblem>();
        var shared = new List<(string, string, int)>();
        var leaks = new List<ZoneLeak>();
        for (int k = 0; k < seeds.Count; k++)
        {
            ZoneSeed seed = seeds[k];
            int root = seedSpace[k];
            if (root < 0)
            {
                problems.Add(new ZoneProblem(seed.ActorId, tree.Leaves[seedLeaf[k]].Empty
                    ? $"zone actor {seed.ActorId} stands in a hair-thin gap against a portal sheet, which is no zone; move it into the room"
                    : $"zone actor {seed.ActorId} is in solid space; it has no zone"));
                continue;
            }

            int zone = zoneOf[root];
            if (firstIn.TryGetValue(root, out int first))
            {
                ZoneSeed other = seeds[first];
                leaks.Add(leakSearch.Leak(seed, other.ActorId, other.Location, seedLeaf[k], seedLeaf[first]));
                continue;
            }

            firstIn[root] = k;
            if (source is not null)
            {
                if (!sourceIndex.TryGetValue(seed.ActorId, out int own))
                {
                    problems.Add(new ZoneProblem(seed.ActorId, $"zone actor {seed.ActorId} is not one of the zones of the kept baked render, which keeps "
                        + $"the source map's zones: its space is zone {zone}{Holder(zone)}; build the render from the brushes (Map Settings, Baked "
                        + "world) to give it a zone of its own"));
                }
                else if (own != zone)
                {
                    problems.Add(new ZoneProblem(seed.ActorId, $"zone actor {seed.ActorId} no longer stands in its zone of the kept baked render "
                        + $"(zone {own}): its space is zone {zone}{Holder(zone)}, whose fog and lighting it gets"));
                }

                continue;
            }

            if (entries[zone].Id is { } taken)
            {
                shared.Add((seed.ActorId, taken, zone));
                continue;
            }

            entries[zone] = (seed.ActorId, seed.Location, seed.Sky);
        }

        var bounds = new Aabb[count];
        Array.Fill(bounds, Aabb.Empty);
        for (int i = 0; i < n; i++)
        {
            BspLeaf leaf = tree.Leaves[i];
            leaf.Zone = leaf.Empty ? zoneOf.GetValueOrDefault(Find(parent, i)) : 0;
            if (leaf.Empty && cells[i] is { } faces)
            {
                foreach (CellFace f in faces)
                {
                    bounds[leaf.Zone] = bounds[leaf.Zone].Union(f.Poly.Bounds());
                }
            }
        }

        bounds[0] = world;
        var dist = new byte[count, count];
        var adjacency = new HashSet<(int, int)>();
        foreach ((int a, int b, _) in portalPairs)
        {
            int za = tree.Leaves[a].Zone, zb = tree.Leaves[b].Zone;
            if (za != zb)
            {
                adjacency.Add((za, zb));
                adjacency.Add((zb, za));
            }
        }

        for (int s = 0; s < count; s++)
        {
            for (int t = 0; t < count; t++)
            {
                dist[s, t] = 255;
            }

            dist[s, s] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int z = queue.Dequeue();
                for (int t = 0; t < count; t++)
                {
                    if (dist[s, t] == 255 && adjacency.Contains((z, t)))
                    {
                        dist[s, t] = (byte)Math.Min(254, dist[s, z] + 1);
                        queue.Enqueue(t);
                    }
                }
            }
        }

        var zones = new List<ZoneEntry>(count);
        for (int z = 0; z < count; z++)
        {
            Aabb bb = bounds[z].IsEmpty ? new Aabb(Vec3.Zero, Vec3.Zero) : bounds[z];
            zones.Add(new ZoneEntry(z, z == 0 ? null : entries[z].Id, z != 0 && entries[z].Sky, bb));
        }

        var final = new ZoneResult
        {
            Zones = zones,
            Distance = dist,
            Spaces = spaces.Count,
            Wrapped = source is null && spaces.Count > numbering.MaxZone,
            LeafPortals = leafPortals,
        };
        final.Problems.AddRange(problems);
        final.Shared.AddRange(shared);
        final.Leaks.AddRange(leaks);
        final.Portals.AddRange(leakSearch.UnsealedPortals(portals, sheetTouches, leaf => tree.Leaves[leaf].Zone,
            SidesJoined(parent, portalPairs, sliverOf)));

        return final;
    }

    /// <summary>
    /// Numbers the spaces 1, 2... in leaf order, as the stock editor does (FEditorVisibility::FormZonesFromLeaves). With
    /// more spaces than numbers the stock editor wraps them in leaf order (space % 63 + 1), so two zone actors can land
    /// on one number and the second loses its zone. Our leaf order is not the stock editor's, so which spaces share a
    /// number differs from the original anyway: here the spaces with a zone actor are numbered first, each its own
    /// number while they last, and the others wrap over the numbers left, else over the zones that are not sky zones (a
    /// space sharing a sky zone's number would be drawn as sky).
    /// </summary>
    private static Dictionary<int, int> Number(List<int> spaces, IReadOnlyList<ZoneSeed> seeds, int[] seedSpace, int max)
    {
        var zone = new Dictionary<int, int>();
        if (spaces.Count <= max)
        {
            for (int i = 0; i < spaces.Count; i++)
            {
                zone[spaces[i]] = i + 1;
            }

            return zone;
        }

        // The first zone actor in a space says whether it is a sky zone.
        var sky = new Dictionary<int, bool>();
        for (int k = 0; k < seeds.Count; k++)
        {
            if (seedSpace[k] >= 0)
            {
                sky.TryAdd(seedSpace[k], seeds[k].Sky);
            }
        }

        int next = 0;
        foreach (int space in spaces.Where(sky.ContainsKey))
        {
            zone[space] = (next++ % max) + 1;
        }

        List<int> spare = Enumerable.Range(next + 1, Math.Max(0, max - next)).ToList();
        if (spare.Count == 0)
        {
            spare = spaces.Where(s => sky.TryGetValue(s, out bool isSky) && !isSky).Select(s => zone[s]).Distinct().Order().ToList();
        }

        if (spare.Count == 0)
        {
            spare = Enumerable.Range(1, max).ToList();
        }

        int wrapped = 0;
        foreach (int space in spaces.Where(s => !sky.ContainsKey(s)))
        {
            zone[space] = spare[wrapped++ % spare.Count];
        }

        return zone;
    }

    /// <summary>Numbers each space by the source map's zone that holds most of its volume; zone 0 for a space wholly
    /// outside the source's zones (one added since).</summary>
    private static Dictionary<int, int> NumberBySource(SourceZones source, List<int> spaces, List<CellFace>?[] cells, int[] parent, HashSet<int> isSpace)
    {
        var votes = new Dictionary<int, Dictionary<int, double>>();
        for (int i = 0; i < cells.Length; i++)
        {
            int root = Find(parent, i);
            if (cells[i] is not { Count: > 0 } faces || !isSpace.Contains(root))
            {
                continue;
            }

            // The mean of a convex cell's corners lies inside it.
            Vec3 sum = Vec3.Zero;
            int corners = 0;
            foreach (CellFace f in faces)
            {
                foreach (Vec3 v in f.Poly.Verts)
                {
                    sum += v;
                    corners++;
                }
            }

            int z = source.ZoneAt(sum / corners);
            if (z <= 0 || z >= source.Actors.Count)
            {
                continue;
            }

            if (!votes.TryGetValue(root, out Dictionary<int, double>? tally))
            {
                votes[root] = tally = new Dictionary<int, double>();
            }

            tally[z] = tally.GetValueOrDefault(z) + Math.Max(Volume(faces), 1e-6);
        }

        return spaces.ToDictionary(s => s, s => votes.TryGetValue(s, out Dictionary<int, double>? tally) ? tally.MaxBy(kv => kv.Value).Key : 0);
    }

    /// <summary>
    /// Whether the two leaves a portal sheet stands between are one space. A sliver (a hair-thin gap the float CSG
    /// leaves against a sheet, or between two sheets a hair apart) stands for the spaces it borders through sheets: a
    /// sheet with a sliver on one side parts the space when what lies past the sliver is another space, and does not
    /// when the space only flows round it.
    /// </summary>
    private static Func<int, int, bool> SidesJoined(int[] parent, List<(int A, int B, int Sheet)> portalPairs, int[] sliverOf)
    {
        var past = new Dictionary<int, HashSet<int>>();
        foreach ((int a, int b, _) in portalPairs)
        {
            foreach ((int s, int other) in new[] { (a, b), (b, a) })
            {
                if (sliverOf[s] >= 0 && sliverOf[other] < 0)
                {
                    if (!past.TryGetValue(sliverOf[s], out HashSet<int>? set))
                    {
                        past[sliverOf[s]] = set = new HashSet<int>();
                    }

                    set.Add(Find(parent, other));
                }
            }
        }

        IEnumerable<int> Sides(int leaf) => sliverOf[leaf] < 0 ? [Find(parent, leaf)] : past.GetValueOrDefault(sliverOf[leaf]) ?? [];
        return (a, b) => Sides(a).Concat(Sides(b)).Distinct().Count() <= 1;
    }

    /// <summary>Splits every empty leaf the sheet crosses by the sheet's plane (a NotCsg node, both sides empty).</summary>
    private static void InsertPortal(BspTree tree, BspElement e, Poly piece)
    {
        if (e is BspLeaf leaf)
        {
            if (leaf.Empty && piece.Area() >= MinArea)
            {
                var node = new BspNode(piece.Plane, csg: false) { IsPortalSplit = true, PortalPiece = piece };
                node.Front = new BspLeaf(true) { Parent = node };
                node.Back = new BspLeaf(true) { Parent = node };
                tree.Replace(leaf, node);
            }

            return;
        }

        var n = (BspNode)e;
        switch (piece.Classify(n.Plane))
        {
            case PlaneSide.Front:
                InsertPortal(tree, n.Front, piece);
                break;
            case PlaneSide.Back:
                InsertPortal(tree, n.Back, piece);
                break;
            case PlaneSide.Split:
                (Poly? f, Poly? b) = piece.Split(n.Plane);
                if (f is not null)
                {
                    InsertPortal(tree, n.Front, f);
                }

                if (b is not null)
                {
                    InsertPortal(tree, n.Back, b);
                }

                break;
        }

        // On the plane: the tree already separates the two sides there.
    }

    /// <summary>The corners of a leaf's convex region inside <paramref name="box"/> (with repeats where faces meet).</summary>
    internal static List<Vec3> CellCorners(BspLeaf leaf, Aabb box) => Polytope(leaf, box).SelectMany(f => f.Poly.Verts).ToList();

    /// <summary>The faces of a leaf's convex region, each a polygon on one bounding plane, wound outwards.</summary>
    private static List<CellFace> Polytope(BspLeaf leaf, Aabb box)
    {
        var planes = new List<(Plane3 Plane, BspNode? Node, bool InFront)>();
        foreach ((Plane3 p, BspNode node, bool front) in BspTree.BoundsOf(leaf))
        {
            planes.Add((p, node, front));
        }

        planes.Add((new Plane3(new Vec3(1, 0, 0), box.Max.X), null, false));
        planes.Add((new Plane3(new Vec3(-1, 0, 0), -box.Min.X), null, false));
        planes.Add((new Plane3(new Vec3(0, 1, 0), box.Max.Y), null, false));
        planes.Add((new Plane3(new Vec3(0, -1, 0), -box.Min.Y), null, false));
        planes.Add((new Plane3(new Vec3(0, 0, 1), box.Max.Z), null, false));
        planes.Add((new Plane3(new Vec3(0, 0, -1), -box.Min.Z), null, false));
        double half = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z)) * 2;
        var faces = new List<CellFace>();
        for (int i = 0; i < planes.Count; i++)
        {
            (Plane3 plane, BspNode? node, bool front) = planes[i];
            Vec3[]? verts = Poly.HugeQuad(plane, box.Center, half);
            for (int j = 0; j < planes.Count && verts is not null; j++)
            {
                if (j == i)
                {
                    continue;
                }

                Plane3 other = planes[j].Plane;
                if (other.Coincident(plane))
                {
                    // A repeated plane (or its flip, for a flat cell): keep only the first.
                    if (j < i)
                    {
                        verts = null;
                    }

                    continue;
                }

                verts = Poly.ClassifyPoints(verts, other) switch
                {
                    PlaneSide.Front => null,
                    PlaneSide.Split => Poly.SplitPoints(verts, other).Back,
                    _ => verts,
                };
            }

            if (verts is null)
            {
                continue;
            }

            // Two coincident walls facing the same way (two brushes on one plane) leave a slab of no thickness between
            // their nodes: the nearer node's far side is that slab, so what lies across the face is behind the outermost.
            for (int j = i + 1; j < planes.Count; j++)
            {
                if (planes[j].Node is not null && Vec3.Dot(planes[j].Plane.N, plane.N) > 0 && planes[j].Plane.Coincident(plane))
                {
                    (_, node, front) = planes[j];
                }
            }

            faces.Add(new CellFace(new Poly(verts, plane, s_cellSurface), node, front));
        }

        return faces;
    }

    /// <summary>The empty leaves a polygon borders on the side <paramref name="toward"/> points to.</summary>
    public static IEnumerable<BspLeaf> LeavesBeside(BspTree tree, Poly face, Vec3 toward)
    {
        var hits = new List<(BspLeaf, Poly)>();
        Filter(tree.Root, face, toward, hits);
        return hits.Select(h => h.Item1).Where(l => l.Empty).Distinct();
    }

    /// <summary>Sends a polygon down a subtree; points are taken just in front of it along <paramref name="toward"/>.</summary>
    private static void Filter(BspElement e, Poly piece, Vec3 toward, List<(BspLeaf, Poly)> hits)
    {
        while (true)
        {
            if (e is BspLeaf leaf)
            {
                hits.Add((leaf, piece));
                return;
            }

            var n = (BspNode)e;
            switch (piece.Classify(n.Plane))
            {
                case PlaneSide.Front:
                    e = n.Front;
                    break;
                case PlaneSide.Back:
                    e = n.Back;
                    break;
                case PlaneSide.On:
                    e = Vec3.Dot(n.Plane.N, toward) > 0 ? n.Front : n.Back;
                    break;
                default:
                    (Poly? f, Poly? b) = piece.Split(n.Plane);
                    if (f is not null)
                    {
                        Filter(n.Front, f, toward, hits);
                    }

                    if (b is null)
                    {
                        return;
                    }

                    piece = b;
                    e = n.Back;
                    break;
            }
        }
    }

    /// <summary>The area of the piece not covered by portal sheets on its plane, and each sheet that covers part of it
    /// (every one of them: two portal brushes in one doorway both seal it).</summary>
    private static double OpenArea(Poly piece, IReadOnlyList<Poly> portals, out List<int> covering)
    {
        covering = [];
        var open = new List<Poly> { piece };
        for (int s = 0; s < portals.Count; s++)
        {
            Poly sheet = portals[s];
            if (!OnSheetPlane(piece, sheet) || !sheet.Bounds().Overlaps(piece.Bounds(), 1))
            {
                continue;
            }

            if (Cut(piece, sheet).Inside > 1.0)
            {
                covering.Add(s);
            }

            open = open.SelectMany(q => Cut(q, sheet).Outside).ToList();
        }

        return open.Sum(p => p.Area());
    }

    /// <summary>True when the piece lies on the sheet's plane. Looser than <see cref="Plane3.Coincident"/>: a sheet a
    /// hair off the axis (a stored float rotation) still seals the face on the axis plane a coplanar sheet split.</summary>
    private static bool OnSheetPlane(Poly piece, Poly sheet) =>
        Math.Abs(Vec3.Dot(piece.Plane.N, sheet.Plane.N)) > 1 - 1e-4 && piece.Verts.All(v => Math.Abs(sheet.Plane.Dist(v)) < SheetSlop);

    /// <summary>How far off a sheet's plane a face may lie and still be covered by it, uu.</summary>
    private const double SheetSlop = 0.1;

    /// <summary>The parts of <paramref name="q"/> outside the convex <paramref name="sheet"/> on its plane, and the area
    /// inside it.</summary>
    private static (List<Poly> Outside, double Inside) Cut(Poly q, Poly sheet)
    {
        var outside = new List<Poly>();
        Poly? rest = q;
        for (int k = 0; k < sheet.Verts.Length && rest is not null; k++)
        {
            Vec3 a = sheet.Verts[k], b = sheet.Verts[(k + 1) % sheet.Verts.Length];
            Vec3 en = Vec3.Cross(b - a, sheet.Plane.N).Normalized();
            var edge = new Plane3(en, Vec3.Dot(en, a));
            switch (rest.Classify(edge))
            {
                case PlaneSide.Front:
                    outside.Add(rest);
                    rest = null;
                    break;
                case PlaneSide.Split:
                    (Poly? f, Poly? bk) = rest.Split(edge);
                    if (f is not null)
                    {
                        outside.Add(f);
                    }

                    rest = bk;
                    break;
            }
        }

        return (outside, rest?.Area() ?? 0);
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }

    /// <summary>
    /// Joins each sliver (a sealed space with next to no volume: a hair-thin gap the float CSG leaves between a portal
    /// sheet and a face, or two sheets a hair apart) to the largest space it borders through a portal. The stock
    /// editor's BSP has no such leaves, so it makes no zone of them; counted, they would push the zone numbers past
    /// the original's. Gives each leaf of a sliver the sliver it was in <paramref name="sliverOf"/> (-1 for the other
    /// leaves) and returns the slivers that border no space (gaps closed off by solid alone): they get no zone.
    /// </summary>
    private static HashSet<int> JoinSlivers(List<CellFace>?[] cells, int[] parent, List<(int A, int B, int Sheet)> portalPairs, int[] sliverOf)
    {
        var volume = new Dictionary<int, double>();
        var largestFace = new Dictionary<int, double>();
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } faces)
            {
                continue;
            }

            // A leaf the cell clipping left no faces has no volume either.
            int root = Find(parent, i);
            volume[root] = volume.GetValueOrDefault(root) + (faces.Count == 0 ? 0 : Volume(faces));
            largestFace[root] = Math.Max(largestFace.GetValueOrDefault(root), faces.Count == 0 ? 0 : faces.Max(f => f.Poly.Area()));
        }

        // Decided once, before any joins: a real space a sliver joins stays a real space, however large the sliver's face.
        var slivers = volume.Keys.Where(r => volume[r] <= SliverThickness * largestFace[r]).ToHashSet();
        for (int i = 0; i < cells.Length; i++)
        {
            int root = Find(parent, i);
            sliverOf[i] = cells[i] is not null && slivers.Contains(root) ? root : -1;
        }

        // Chains of slivers join one step at a time, each to a space that is not one (or no longer one).
        for (bool joined = true; joined;)
        {
            joined = false;
            var best = new Dictionary<int, int>();
            foreach ((int a, int b, _) in portalPairs)
            {
                foreach ((int from, int to) in new[] { (a, b), (b, a) })
                {
                    int rf = Find(parent, from), rt = Find(parent, to);
                    if (rf != rt && slivers.Contains(rf) && !slivers.Contains(rt)
                        && (!best.TryGetValue(rf, out int current) || volume[rt] > volume[current]))
                    {
                        best[rf] = rt;
                    }
                }
            }

            foreach ((int sliver, int into) in best)
            {
                int rs = Find(parent, sliver), ri = Find(parent, into);
                if (rs == ri)
                {
                    continue;
                }

                double v = volume[rs] + volume[ri];
                slivers.Remove(rs);
                slivers.Remove(ri);
                Union(parent, rs, ri);
                volume[Find(parent, rs)] = v;
                joined = true;
            }
        }

        return slivers;
    }

    /// <summary>The volume of a convex cell from its faces.</summary>
    private static double Volume(List<CellFace> faces)
    {
        Vec3 origin = faces[0].Poly.Verts[0];
        return faces.Sum(f => Math.Abs(Vec3.Dot(f.Poly.Plane.N, f.Poly.Verts[0] - origin)) * f.Poly.Area()) / 3;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb)
        {
            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }

    /// <summary>A face of a leaf's cell: its polygon, the node it lies on (null for the outer box) and the side the
    /// leaf is on.</summary>
    private sealed record CellFace(Poly Poly, BspNode? Node, bool LeafInFront);

    private static readonly Surface s_cellSurface = new() { BrushId = "", FaceIndex = -1, Kind = SurfaceKind.NonSolid };
}
