using System.Diagnostics;
using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc.Recovery;

/// <summary>How a compiled map's world is turned back into brushes.</summary>
public enum BrushRecoveryMode
{
    /// <summary>No brushes: the world stays the baked layer only.</summary>
    Off,

    /// <summary>One brush per BSP cell, joined only where cells share a whole face.</summary>
    Cells,

    /// <summary>Cells joined, then grown over their neighbours where the original cells cover the result exactly
    /// (RE+'s Merge Recovered Geometry, its default); grown brushes the editor's build does not reproduce are split
    /// back.</summary>
    Merge,
}

public sealed record BrushRecoveryOptions
{
    public BrushRecoveryMode Mode { get; init; } = BrushRecoveryMode.Merge;

    /// <summary>Build the recovered brushes with the editor's compiler and compare them with the compiled BSP (and split
    /// back grown brushes that differ).</summary>
    public bool Verify { get; init; } = true;

    public Action<string>? Progress { get; init; }

    public CancellationToken Cancel { get; init; }
}

/// <summary>What a brush recovery made and found.</summary>
public sealed class BrushRecoveryReport
{
    public BrushRecoveryMode Mode { get; set; }

    public int Nodes { get; set; }

    public int Polygons { get; set; }

    public int SkippedPolygons { get; set; }

    public int EmptyLeaves { get; set; }

    public int SolidLeaves { get; set; }

    /// <summary>Convex cells taken from the BSP (one CSG kind: the empty cells of a world that starts solid).</summary>
    public int Cells { get; set; }

    /// <summary>Cells the BSP leaves open to the outside of the map (no brush made).</summary>
    public int UnboundedCells { get; set; }

    /// <summary>Cells or splits that are degenerate at the reconstruction tolerance (no brush made).</summary>
    public int DegenerateCells { get; set; }

    /// <summary>Cells thinner than <see cref="BrushRecovery.SliverWidth"/>, left out.</summary>
    public int SliversDropped { get; set; }

    public int Joined { get; set; }

    public int Grown { get; set; }

    public int Absorbed { get; set; }

    public bool GrowthBudgetReached { get; set; }

    public int SplitBack { get; set; }

    public int Brushes { get; set; }

    public int Sheets { get; set; }

    public int Portals { get; set; }

    public int Faces { get; set; }

    public int UnifiedSurfaces { get; set; }

    /// <summary>Brushes that lost their volume at float precision (left out).</summary>
    public int Collapsed { get; set; }

    public int Probes { get; set; }

    public int FirstMisses { get; set; }

    public int Misses { get; set; }

    public int Attempts { get; set; }

    public List<Vec3> MissPoints { get; } = new();

    /// <summary>Recovered brushes thinner than a unit or smaller than a 4-unit cube: slivers of the BSP's splits.</summary>
    public List<(Brush Brush, double Width, double Volume)> Thin { get; } = new();

    /// <summary>Brush face parts between solid and empty space that no surf covers: walls the compiled map drew nothing
    /// on, kept invisible (with a stand-in material), with their area and centre.</summary>
    public List<(Brush Brush, double Area, Vec3 Centre)> Unsurfaced { get; } = new();

    public List<string> Notes { get; } = new();

    public Dictionary<string, double> Seconds { get; } = new(StringComparer.Ordinal);

    public double TotalSeconds => Seconds.Values.Sum();

    private static string F(double v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>One line: what the recovery made.</summary>
    public string Summary()
    {
        string merge = Mode == BrushRecoveryMode.Merge
            ? $", {Grown} grown over neighbours ({Absorbed} more absorbed){(SplitBack > 0 ? $", {SplitBack} split back" : "")}"
            : "";
        string fewer = Cells > 0 ? $" ({F(100.0 * (Cells - Brushes) / Cells, "0")}% fewer)" : "";
        string check = Probes == 0 ? ""
            : Misses == 0 ? $"; rebuilt by the editor's compiler, all {Probes} probes beside the original surfaces match the compiled BSP"
            : $"; rebuilt by the editor's compiler, {Misses} of {Probes} probes beside the original surfaces differ from the compiled BSP";
        return $"{Brushes} brushes recovered from the compiled BSP: {Cells} cells -> {Joined} joined along shared faces{merge} -> {Brushes}{fewer}, "
            + $"plus {Sheets} non-solid sheets and {Portals} zone portals; {Faces} brush faces{(UnifiedSurfaces > 0 ? $", {UnifiedSurfaces} matching surfaces combined" : "")}"
            + check;
    }

    /// <summary>The report as JSON; <paramref name="times"/> adds each stage's seconds (left out of a project, whose
    /// files would otherwise change on every import of the same map).</summary>
    public SvJsonObject ToJson(bool times = false)
    {
        var o = new SvJsonObject();
        void I(string k, long v) => o.Add(k, SvJsonNode.Integer(v));
        o.Add("summary", SvJsonNode.String(Summary()));
        o.Add("mode", SvJsonNode.String(Mode.ToString().ToLowerInvariant()));
        I("nodes", Nodes);
        I("polygons", Polygons);
        I("skippedPolygons", SkippedPolygons);
        I("emptyLeaves", EmptyLeaves);
        I("solidLeaves", SolidLeaves);
        I("cells", Cells);
        I("unboundedCells", UnboundedCells);
        I("degenerateCells", DegenerateCells);
        I("sliversDropped", SliversDropped);
        I("joined", Joined);
        I("grown", Grown);
        I("absorbed", Absorbed);
        o.Add("growthBudgetReached", SvJsonNode.Bool(GrowthBudgetReached));
        I("splitBack", SplitBack);
        I("brushes", Brushes);
        I("sheets", Sheets);
        I("portals", Portals);
        I("faces", Faces);
        I("unifiedSurfaces", UnifiedSurfaces);
        I("collapsed", Collapsed);
        I("probes", Probes);
        I("firstMisses", FirstMisses);
        I("misses", Misses);
        I("attempts", Attempts);
        o.Add("thin", new SvJsonArray(Thin.Select(t =>
        {
            var x = new SvJsonObject();
            x.Add("brush", SvJsonNode.String(t.Brush.Id));
            x.Add("width", SvJsonNode.Float((float)t.Width));
            x.Add("volume", SvJsonNode.Float((float)t.Volume));
            return (SvJsonNode)x;
        })));
        o.Add("unsurfaced", new SvJsonArray(Unsurfaced.GroupBy(u => u.Brush.Id, StringComparer.Ordinal).Select(g =>
        {
            var x = new SvJsonObject();
            x.Add("brush", SvJsonNode.String(g.Key));
            x.Add("area", SvJsonNode.Float((float)g.Sum(u => u.Area)));
            x.Add("at", SvJsonNode.String(g.MaxBy(u => u.Area).Centre.ToString()));
            return (SvJsonNode)x;
        })));
        o.Add("missPoints", new SvJsonArray(MissPoints.Select(p => (SvJsonNode)SvJsonNode.String(p.ToString()))));
        o.Add("notes", new SvJsonArray(Notes.Select(n => (SvJsonNode)SvJsonNode.String(n))));
        if (times)
        {
            var s = new SvJsonObject();
            foreach ((string k, double v) in Seconds)
            {
                s.Add(k, SvJsonNode.Float((float)Math.Round(v, 3)));
            }

            o.Add("seconds", s);
        }

        return o;
    }
}

/// <summary>The brushes a recovery made, in CSG order (structural brushes, largest first, then sheets and portals),
/// and its report.</summary>
public sealed record RecoveredBrushes(List<Brush> Brushes, BrushRecoveryReport Report)
{
    public IEnumerable<Brush> Structural => Brushes.Where(b => (b.PolyFlags & (PolyFlags.NotSolid | PolyFlags.Portal)) == 0);
}

/// <summary>
/// Recovers editable brushes from a compiled map's BSP, as RE+'s Recover Compiled Map does, in the editor and without
/// the native editor: the convex cells of the node tree (empty cells as subtract brushes for a world that starts solid,
/// solid cells as add brushes for one that starts empty), joined where they share a whole face, grown over their
/// neighbours where the original cells cover the grown region exactly, with materials, texture mapping, poly flags and
/// lightmap scales from the surfs on their faces (<see cref="FacePartition"/>); non-solid sheets and zone portals come
/// back as sheet brushes. The result is built with the editor's own compiler and probed beside every original surface
/// against the compiled BSP; grown brushes near a difference are split back into what they were grown from.
/// <para>Unlike RE+ a fault never stops the whole recovery: a cell open to the outside or degenerate at the tolerance is
/// left out and counted, a face no surf covers takes a stand-in material and is reported, and differences left after
/// the split-back are reported. Deterministic: the same file gives the same brushes and ids.</para>
/// </summary>
public static class BrushRecovery
{
    /// <summary>The <see cref="MapProject.Extra"/> key of the last recovery's report.</summary>
    public const string ReportKey = "sdcBrushRecovery";

    /// <summary>The layer and outliner folder recovered brushes go in.</summary>
    public const string Folder = "Recovered brushes";

    /// <summary>Cells thinner than this are left out: the editor's compiler cannot tell their faces apart.</summary>
    public const double SliverWidth = 0.01;

    /// <summary>The extraction box's margin around every point of the model.</summary>
    public const double BoxMargin = 64;

    /// <summary>Thin brush report: narrower than this, or smaller than <see cref="ThinVolume"/>.</summary>
    public const double ThinWidth = 1, ThinVolume = 64;

    /// <summary>How far beside a surface the rebuild is probed.</summary>
    public const double ProbeOffset = 0.5;

    /// <summary>The most corners a face keeps (UE2's FPoly limit); larger faces are cut along diagonals.</summary>
    public const int MaxFaceVertices = 16;

    private const int MostAttempts = 6;

    public static RecoveredBrushes Recover(CookedBsp bsp, BrushRecoveryOptions options)
    {
        var report = new BrushRecoveryReport
        {
            Mode = options.Mode == BrushRecoveryMode.Off ? BrushRecoveryMode.Merge : options.Mode,
            Nodes = bsp.Nodes.Count,
            Polygons = bsp.Faces.Count,
            SkippedPolygons = bsp.SkippedPolygons,
        };
        var clock = Stopwatch.StartNew();
        void Stage(string name)
        {
            report.Seconds[name] = report.Seconds.GetValueOrDefault(name) + clock.Elapsed.TotalSeconds;
            clock.Restart();
        }

        options.Progress?.Invoke("recovering brushes: cells");
        var work = new Work(4_000_000_000, options.Cancel);
        List<Cell> cells = Reconstruct(bsp, report, work);
        report.Cells = cells.Count;
        Stage("cells");

        options.Progress?.Invoke($"recovering brushes: joining {cells.Count} cells");
        List<Cell> joined = CellGeometry.Join(cells, work);
        report.Joined = joined.Count;
        Stage("join");

        List<Cell> brushes;
        if (report.Mode == BrushRecoveryMode.Merge)
        {
            options.Progress?.Invoke($"recovering brushes: growing {joined.Count} brushes");
            (brushes, CellGeometry.GrowStats stats) = CellGeometry.Grow(cells, joined, new Work(4_000_000_000, options.Cancel));
            report.Grown = stats.Grown;
            report.Absorbed = stats.Absorbed;
            report.GrowthBudgetReached = stats.BudgetReached;
            Stage("grow");
        }
        else
        {
            brushes = joined.Select((c, i) => new Cell(c.Faces) { Members = [i] }).ToList();
        }

        var partition = new FacePartition(bsp, unify: report.Mode == BrushRecoveryMode.Merge);
        report.UnifiedSurfaces = partition.Unified;
        int fallback = GlobalFallback(bsp, partition);
        List<Probe> probes = options.Verify ? Probes(bsp) : [];
        report.Probes = probes.Count;
        var cache = new CsgWorld();
        List<Built> built = [];
        // The set with the fewest probes differing wins, then the one with fewer brushes: a difference the split-back
        // does not remove is the compiler's, not the merge's, and the merges stay.
        (List<Cell> Brushes, List<Built> Built, List<Vec3> Misses, int Split)? best = null;
        int splitBack = 0;
        for (int attempt = 0; attempt < MostAttempts; attempt++)
        {
            report.Attempts = attempt + 1;
            brushes = Order(brushes);
            options.Progress?.Invoke($"recovering brushes: building {brushes.Count} brushes{(attempt > 0 ? $" (attempt {attempt + 1})" : "")}");
            built = BuildStructural(brushes, bsp, partition, fallback, report.Mode == BrushRecoveryMode.Cells ? "RecoveredCell" : "RecoveredBrush");
            Stage("faces");
            if (probes.Count == 0)
            {
                best = (brushes, built, [], 0);
                break;
            }

            options.Progress?.Invoke($"recovering brushes: checking against the compiled BSP ({probes.Count} probes)");
            List<Vec3> misses = Check(built, bsp, probes, cache);
            Stage("check");
            if (attempt == 0)
            {
                report.FirstMisses = misses.Count;
            }

            if (best is not { } b || misses.Count < b.Misses.Count || (misses.Count == b.Misses.Count && built.Count < b.Built.Count))
            {
                best = (brushes, built, misses, splitBack);
            }

            if (misses.Count == 0 || report.Mode != BrushRecoveryMode.Merge || !brushes.Exists(c => c.Members.Count > 1))
            {
                break;
            }

            bool last = attempt == MostAttempts - 2;
            int split = 0;
            foreach (double reach in last ? [1e12] : new[] { 1.0, 64.0, 1e12 })
            {
                (brushes, split) = SplitBack(brushes, joined, misses, reach);
                if (split > 0)
                {
                    break;
                }
            }

            splitBack += split;
            Stage("check");
            if (split == 0)
            {
                break;
            }
        }

        (brushes, built, List<Vec3> left, report.SplitBack) = best!.Value;
        report.Misses = left.Count;
        report.MissPoints.AddRange(left.Take(16));
        report.Collapsed = brushes.Count - built.Count;
        var result = new List<Brush>();
        foreach (Built b in built)
        {
            result.Add(b.Brush);
            report.Faces += b.Brush.Shape.Faces.Count;
            double width = b.Cell.MinimumWidth(), volume = b.Cell.Volume();
            if (width < ThinWidth || volume < ThinVolume)
            {
                report.Thin.Add((b.Brush, width, volume));
            }

            foreach (Unsurfaced u in b.Unsurfaced)
            {
                report.Unsurfaced.Add((b.Brush, u.Area, u.Centre));
            }
        }

        report.Brushes = built.Count;
        (List<Brush> sheets, List<Brush> portals) = Sheets(bsp, report);
        result.AddRange(sheets);
        result.AddRange(portals);
        report.Sheets = sheets.Count;
        report.Portals = portals.Count;
        Stage("sheets");
        return new RecoveredBrushes(result, report);
    }

    // ---- cells -----------------------------------------------------------------------------------------------------

    /// <summary>The convex cells of the tree whose state differs from the world's (RE+'s Reconstruct), clipped from a box
    /// around every point of the model. A cell left open by the box, degenerate, or thinner than
    /// <see cref="SliverWidth"/> is counted and left out.</summary>
    private static List<Cell> Reconstruct(CookedBsp bsp, BrushRecoveryReport report, Work work)
    {
        var cells = new List<Cell>();
        if (bsp.PointBounds.IsEmpty)
        {
            return cells;
        }

        var stack = new Stack<(int Node, bool Outside, List<CellFace> Cell)>();
        stack.Push((bsp.Nodes.Count == 0 ? -1 : 0, bsp.RootOutside, CellGeometry.Box(bsp.PointBounds.Expand(BoxMargin))));
        while (stack.Count > 0)
        {
            (int index, bool outside, List<CellFace> cell) = stack.Pop();
            if (cell.Count == 0)
            {
                continue;
            }

            if (index < 0)
            {
                if (outside)
                {
                    report.EmptyLeaves++;
                }
                else
                {
                    report.SolidLeaves++;
                }

                if (outside == bsp.RootOutside)
                {
                    continue;
                }

                if (cell.Exists(f => f.Artificial))
                {
                    report.UnboundedCells++;
                    continue;
                }

                if (!CellGeometry.IsClosed(cell))
                {
                    report.DegenerateCells++;
                    continue;
                }

                var c = new Cell(cell);
                if (c.MinimumWidth() < SliverWidth)
                {
                    report.SliversDropped++;
                    continue;
                }

                c.Members = [cells.Count];
                cells.Add(c);
                continue;
            }

            CookedNode n = bsp.Nodes[index];
            CellGeometry.ClipResult fr = CellGeometry.Clip(cell, -n.Normal, -n.Distance, n.Surf, out List<CellFace> front, work);
            CellGeometry.ClipResult br = CellGeometry.Clip(cell, n.Normal, n.Distance, n.Surf, out List<CellFace> back, work);
            if (fr == CellGeometry.ClipResult.Budget || br == CellGeometry.ClipResult.Budget)
            {
                throw new InvalidOperationException("the BSP is too large to recover within the work budget");
            }

            if (fr == CellGeometry.ClipResult.BadCap || br == CellGeometry.ClipResult.BadCap)
            {
                report.DegenerateCells++;
            }

            if (br == CellGeometry.ClipResult.Ok && back.Count > 0)
            {
                stack.Push((n.Back, outside && !n.Csg, back));
            }

            if (fr == CellGeometry.ClipResult.Ok && front.Count > 0)
            {
                stack.Push((n.Front, outside || n.Csg, front));
            }
        }

        return cells;
    }

    /// <summary>Largest volume first (stable), as RE+ orders them for the rebuild.</summary>
    private static List<Cell> Order(List<Cell> brushes) =>
        brushes.Select((b, i) => (b, i, v: b.Volume())).OrderByDescending(x => x.v).ThenBy(x => x.i).Select(x => x.b).ToList();

    /// <summary>Grown brushes with a probe miss within <paramref name="reach"/> of their bounds go back to the joined
    /// brushes they were grown from.</summary>
    private static (List<Cell> Brushes, int Split) SplitBack(List<Cell> brushes, List<Cell> joined, List<Vec3> misses, double reach)
    {
        var o = new List<Cell>();
        int split = 0;
        foreach (Cell b in brushes)
        {
            if (b.Members.Count > 1)
            {
                Aabb box = b.Bounds().Expand(Math.Min(reach, 1e9));
                if (misses.Exists(box.Contains))
                {
                    split++;
                    foreach (int m in b.Members)
                    {
                        o.Add(new Cell(joined[m].Faces) { Members = [m] });
                    }

                    continue;
                }
            }

            o.Add(b);
        }

        return (o, split);
    }

    // ---- brushes ---------------------------------------------------------------------------------------------------

    private sealed record Built(Cell Cell, Brush Brush, List<Unsurfaced> Unsurfaced);

    private readonly record struct Unsurfaced(double Area, Vec3 Centre);

    /// <summary>A structural surf a stand-in face can take: has a material, is not a portal or a non-solid sheet.</summary>
    private static bool Usable(CookedBsp bsp, int surf) =>
        surf >= 0 && surf < bsp.Surfs.Count && bsp.Surfs[surf].Material is not null
        && (bsp.Surfs[surf].Flags & (CookedBsp.PortalFlag | PolyFlags.NotSolid | PolyFlags.Invisible)) == 0;

    /// <summary>The surf the most structural polygons have, for faces nothing else stands in for.</summary>
    private static int GlobalFallback(CookedBsp bsp, FacePartition partition) =>
        bsp.Faces.Where(f => f.Structural && Usable(bsp, f.Surf)).GroupBy(f => partition.Canonical(f.Surf))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).DefaultIfEmpty(-1).First();

    private static List<Built> BuildStructural(List<Cell> cells, CookedBsp bsp, FacePartition partition, int fallback, string prefix)
    {
        // Corners within 0.002 units weld to the earliest one seen, every structural corner before any material cut, so
        // neighbouring brushes share their corners exactly at float precision.
        var pool = new VertexPool();
        foreach (Cell c in cells)
        {
            foreach (CellFace f in c.Faces)
            {
                foreach (Vec3 p in f.Points)
                {
                    pool.Resolve(p);
                }
            }
        }

        var built = new List<Built>(cells.Count);
        foreach (Cell c in cells)
        {
            var unsurfaced = new List<Unsurfaced>();
            if (MakeBrush(c, bsp, partition, fallback, pool, unsurfaced, $"{prefix}{built.Count + 1}") is { } brush)
            {
                built.Add(new Built(c, brush, unsurfaced));
            }
        }

        return built;
    }

    private static Brush? MakeBrush(Cell cell, CookedBsp bsp, FacePartition partition, int fallback, VertexPool pool, List<Unsurfaced> unsurfaced, string id)
    {
        var pieces = new List<Piece>();
        foreach (CellFace f in cell.Faces)
        {
            List<Vec3> outline = PolygonOps.RemoveCollinear(f.Points, 1e-9);
            List<FacePiece> parts = partition.Partition(outline, f.Normal);
            if (parts is [{ Covered: false }] && Between(bsp, outline, f.Normal))
            {
                // A wall none of the polygons on its plane covers: look at every polygon, ending with ones a little
                // further off and tilted (its split plane may not be its polygon's).
                parts = partition.Partition(outline, f.Normal, thorough: true);
            }

            if (parts.TrueForAll(p => p.Covered))
            {
                pieces.AddRange(parts.Select(p => new Piece(p.Points, f.Normal, p.Surf, true, false)));
                continue;
            }

            // A part no surf covers takes the surf of the biggest covered part of the face, else the surf whose plane
            // made the face, else the map's most common one. One between solid and empty space is a wall the compiled
            // map drew nothing on: it stays invisible, as in the original, and is reported. One between two empty cells
            // is never drawn either way.
            int stand = parts.Where(p => p.Covered).OrderByDescending(p => PolygonOps.Area(p.Points, f.Normal)).Select(p => p.Surf).FirstOrDefault(-1);
            if (stand < 0)
            {
                stand = Usable(bsp, f.Surface) ? partition.Canonical(f.Surface) : fallback;
            }

            var regrouped = new List<Piece>(parts.Count);
            foreach (FacePiece p in parts)
            {
                bool hidden = false;
                if (!p.Covered)
                {
                    hidden = Between(bsp, p.Points, f.Normal);
                    double area = PolygonOps.Area(p.Points, f.Normal);
                    if (hidden && area > 1e-3)
                    {
                        unsurfaced.Add(new Unsurfaced(area, Mid(p.Points)));
                    }
                }

                regrouped.Add(new Piece(p.Points, f.Normal, p.Covered ? p.Surf : stand, p.Covered, hidden));
            }

            // A piece too small to be a face of its own (Map Check's zero-area face) goes with the face's largest piece.
            Piece main = regrouped.MaxBy(p => PolygonOps.Area(p.Points, f.Normal))!;
            for (int i = 0; i < regrouped.Count; i++)
            {
                if (regrouped[i].Key != main.Key && PolygonOps.Area(regrouped[i].Points, f.Normal) < Geometry.ShapeAnalysis.ZeroArea)
                {
                    regrouped[i] = regrouped[i] with { Surf = main.Surf, Hidden = main.Hidden };
                }
            }

            if (regrouped.TrueForAll(p => p.Key == main.Key))
            {
                pieces.Add(new Piece(outline, f.Normal, main.Surf, regrouped.Exists(p => p.Covered && p.Surf == main.Surf), main.Hidden));
                continue;
            }

            foreach (IGrouping<(int, bool), Piece> g in regrouped.GroupBy(p => p.Key).OrderBy(g => g.Key))
            {
                bool covered = g.Any(p => p.Covered);
                foreach (List<Vec3> joined in PolygonOps.Coalesce(g.Select(p => p.Points).ToList(), f.Normal))
                {
                    pieces.Add(new Piece(joined, f.Normal, g.Key.Item1, covered, g.Key.Item2));
                }
            }
        }

        // Weld through the pool, at float precision. The material cuts leave T-junctions on the edges of neighbouring
        // faces of the brush; they stay (inserting the cut's corners there would bend those edges by the weld, and the
        // faces would no longer be convex): the shape checks pair an edge with the run of collinear edges facing it.
        var faces = new List<Piece>();
        foreach (Piece piece in pieces)
        {
            if (PolygonOps.Clean(piece.Points.Select(p => pool.Resolve(p, anchor: false)).ToList()) is { } w && PolygonOps.Area(w, piece.Normal) > 1e-6)
            {
                faces.AddRange(PolygonOps.Limit(w, MaxFaceVertices).Select(part => piece with { Points = part }));
            }
        }

        if (faces.Select(f => f.Normal).Distinct().Count() < 4)
        {
            return null;
        }

        Vec3 centre = cell.Centroid();
        var location = new Vec3(Math.Round(centre.X), Math.Round(centre.Y), Math.Round(centre.Z));
        var brush = new Brush(id)
        {
            Oper = bsp.RootOutside ? CsgOper.Add : CsgOper.Subtract,
            Location = location,
        };
        foreach (Piece face in faces)
        {
            // A wall that took its surf from a sheet, a portal or a mirror must not become one.
            int strip = CookedBsp.PortalFlag | PolyFlags.NotSolid | PolyFlags.Semisolid | (face.Covered ? 0 : CookedBsp.MirrorFlag);
            BrushFace made = Face(face.Points, face.Normal, face.Surf < 0 ? null : bsp.Surfs[face.Surf], location, strip);
            if (face.Hidden)
            {
                made.Flags |= PolyFlags.Invisible;
            }

            brush.Shape.Faces.Add(made);
        }

        return Geometry.ShapeAnalysis.Volume(brush.Shape) > 0 ? brush : null;
    }

    private static Vec3 Mid(List<Vec3> points) => points.Aggregate(Vec3.Zero, (s, v) => s + v) / points.Count;

    /// <summary>True when a face (outward normal <paramref name="n"/>) lies between solid and empty space in the compiled
    /// BSP (probed half a unit either side of its middle).</summary>
    private static bool Between(CookedBsp bsp, List<Vec3> points, Vec3 n)
    {
        Vec3 mid = Mid(points);
        return bsp.IsOutside(mid + n * ProbeOffset) != bsp.IsOutside(mid - n * ProbeOffset);
    }

    /// <summary>A part of a brush face: its surf, whether a surf covers it, and whether it is a wall the compiled map
    /// drew nothing on (no surf, solid behind and empty in front).</summary>
    private sealed record Piece(List<Vec3> Points, Vec3 Normal, int Surf, bool Covered, bool Hidden)
    {
        public (int, bool) Key => (Surf, Hidden);
    }

    /// <summary>A brush face in the brush's local space from world points and the surf that covers it.</summary>
    private static BrushFace Face(List<Vec3> world, Vec3 n, CookedSurf? s, Vec3 location, int strip)
    {
        Vec3 u = s?.TextureU ?? Vec3.Zero, v = s?.TextureV ?? Vec3.Zero;
        if (u.LengthSquared <= 1e-8 || v.LengthSquared <= 1e-8 || !double.IsFinite(u.X + u.Y + u.Z + v.X + v.Y + v.Z))
        {
            Vec3 reference = Math.Abs(n.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitY;
            u = Vec3.Cross(reference, n).Normalized();
            v = Vec3.Cross(n, u);
        }

        double scale = s is { LightMapScale: > 0 } && double.IsFinite(s.LightMapScale) ? s.LightMapScale : BrushFace.DefaultLightMapScale;
        return new BrushFace
        {
            Vertices = world.Select(p => p - location).ToList(),
            Material = s?.Material,
            Flags = (s?.Flags ?? 0) & ~strip,
            TextureBase = (s?.TextureBase ?? world[0]) - location,
            TextureU = u,
            TextureV = v,
            LightMapScale = scale,
            LightMapScalePsx2 = s?.LightMapScalePsx2,
        };
    }

    /// <summary>Non-solid sheets (the polygons of NotCsg nodes, one brush per surf and plane) and zone portals (one per portal surf,
    /// from its authored outline when the file keeps it).</summary>
    private static (List<Brush> Sheets, List<Brush> Portals) Sheets(CookedBsp bsp, BrushRecoveryReport report)
    {
        var pool = new VertexPool();
        var sheets = new List<Brush>();
        var portals = new List<Brush>();
        int notSolidMissing = 0;
        foreach (IGrouping<int, CookedFace> g in bsp.Faces.Where(f => !f.Structural).GroupBy(f => f.Surf).OrderBy(g => g.Key))
        {
            CookedSurf s = bsp.Surfs[g.Key];
            bool portal = (s.Flags & CookedBsp.PortalFlag) != 0;
            if (!portal && (s.Flags & PolyFlags.NotSolid) == 0)
            {
                notSolidMissing++;
            }

            // A portal comes back from its authored outline; a sheet as one brush per plane (a sheet brush is flat), its
            // fragments joined back where they stay convex.
            var groups = new List<List<(List<Vec3> Points, Vec3 Normal)>>();
            CookedFace first = g.First();
            if (portal && s.Outline is { Count: >= 3 } outline && CookedBsp.Newell(outline).Length > 1e-6
                && outline.All(p => Math.Abs(Vec3.Dot(first.PlaneNormal, p) - first.PlaneDistance) <= FacePartition.CoplanarBand))
            {
                // The authored outline, when it lies on the portal's node plane (RE+ refuses one that does not).
                groups.Add([(outline.ToList(), CookedBsp.Newell(outline).Normalized())]);
            }
            else
            {
                foreach (IGrouping<(long, long, long, long), CookedFace> plane in g.GroupBy(f => PlaneKey(f.Normal, f.Distance)).OrderBy(p => p.Key))
                {
                    Vec3 n = plane.First().Normal;
                    groups.Add(PolygonOps.Coalesce(plane.Select(f => f.Points).ToList(), n).Select(joined => (joined, n)).ToList());
                }
            }

            foreach (List<(List<Vec3> Points, Vec3 Normal)> polys in groups)
            {
                var faces = new List<(List<Vec3> Points, Vec3 Normal)>();
                foreach ((List<Vec3> points, Vec3 n) in polys)
                {
                    // Corners the BSP's cuts left a hair (0.01 units) off a straight edge go (no other face of the sheet
                    // needs them), so the face stays convex.
                    if (PolygonOps.Clean(points.Select(p => pool.Resolve(p)).ToList()) is { } w
                        && PolygonOps.RemoveStraight(w, 0.01) is { Count: >= 3 } straight && PolygonOps.Area(straight, n) > 1e-6)
                    {
                        faces.AddRange(PolygonOps.Limit(straight, MaxFaceVertices).Select(part => (part, n)));
                    }
                }

                if (faces.Count == 0)
                {
                    continue;
                }

                Aabb box = Aabb.Of(faces.SelectMany(f => f.Points));
                Vec3 c = box.Center;
                var location = new Vec3(Math.Round(c.X), Math.Round(c.Y), Math.Round(c.Z));
                List<Brush> list = portal ? portals : sheets;
                var brush = new Brush($"{(portal ? "RecoveredPortal" : "RecoveredSheet")}{list.Count + 1}")
                {
                    Oper = CsgOper.Add,
                    PolyFlags = portal ? CookedBsp.PortalFlag | PolyFlags.Invisible | PolyFlags.NotSolid : PolyFlags.NotSolid,
                    Location = location,
                };
                foreach ((List<Vec3> points, Vec3 n) in faces)
                {
                    BrushFace face = Face(points, n, s, location, 0);
                    if (portal)
                    {
                        face.Flags |= CookedBsp.PortalFlag;
                    }

                    brush.Shape.Faces.Add(face);
                }

                list.Add(brush);
            }
        }

        if (notSolidMissing > 0)
        {
            report.Notes.Add($"{notSolidMissing} non-CSG surfaces lack the NotSolid flag; they came back as non-solid sheets");
        }

        return (sheets, portals);
    }

    private static (long, long, long, long) PlaneKey(Vec3 n, double d) =>
        ((long)Math.Round(n.X * 1e5), (long)Math.Round(n.Y * 1e5), (long)Math.Round(n.Z * 1e5), (long)Math.Round(d * 100));

    // ---- verification ----------------------------------------------------------------------------------------------

    /// <summary>A point beside an original surface and whether the compiled BSP has it in empty space.</summary>
    public readonly record struct Probe(Vec3 Point, bool Outside);

    /// <summary>Two probes per structural polygon, half a unit either side of its centre; a probe whose answer changes
    /// within 0.05 units is a tie the build may break either way and is left out.</summary>
    public static List<Probe> Probes(CookedBsp bsp)
    {
        var probes = new List<Probe>();
        foreach (CookedFace f in bsp.Faces)
        {
            if (!f.Structural)
            {
                continue;
            }

            Vec3 c = f.Centre();
            foreach (double side in (ReadOnlySpan<double>)[1, -1])
            {
                Vec3 p = c + f.Normal * (ProbeOffset * side);
                bool outside = bsp.IsOutside(p);
                if (bsp.IsOutside(c + f.Normal * ((ProbeOffset - 0.05) * side)) != outside || bsp.IsOutside(c + f.Normal * ((ProbeOffset + 0.05) * side)) != outside)
                {
                    continue;
                }

                probes.Add(new Probe(p, outside));
            }
        }

        return probes;
    }

    /// <summary>Builds the brushes with the editor's compiler and returns the probes whose state differs.</summary>
    private static List<Vec3> Check(List<Built> built, CookedBsp bsp, List<Probe> probes, CsgWorld cache)
    {
        WorldModel w = WorldModel.Build(built.Select(b => b.Brush).ToList(), [], worldSolid: !bsp.RootOutside, numbering: null, cache);
        return probes.Where(p => w.Tree.IsEmpty(p.Point) != p.Outside).Select(p => p.Point).ToList();
    }

    /// <summary>Corners welded at float precision to the earliest one seen within 0.002 units on every axis (of all the
    /// grid cells around, as RE+'s weld picks its anchor).</summary>
    private sealed class VertexPool
    {
        private const double Weld = 0.002;
        private readonly Dictionary<(long, long, long), List<(Vec3 P, int Order)>> _grid = new();
        private int _count;

        /// <summary>The anchor within the weld of <paramref name="p"/>, else <paramref name="p"/> at float precision, kept as
        /// an anchor itself when <paramref name="anchor"/> (a material cut's corner is not one: welding the next cut to it
        /// would bend a face by the weld).</summary>
        public Vec3 Resolve(Vec3 p, bool anchor = true)
        {
            Vec3 f = p.ToFloat32();
            (long x, long y, long z) = Key(f);
            (Vec3 P, int Order) best = (f, int.MaxValue);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!_grid.TryGetValue((x + dx, y + dy, z + dz), out List<(Vec3 P, int Order)>? list))
                        {
                            continue;
                        }

                        foreach ((Vec3 a, int order) in list)
                        {
                            if (order < best.Order && Math.Abs(a.X - f.X) < Weld && Math.Abs(a.Y - f.Y) < Weld && Math.Abs(a.Z - f.Z) < Weld)
                            {
                                best = (a, order);
                            }
                        }
                    }
                }
            }

            if (best.Order != int.MaxValue)
            {
                return best.P;
            }

            if (!anchor)
            {
                return f;
            }

            if (!_grid.TryGetValue((x, y, z), out List<(Vec3 P, int Order)>? cell))
            {
                _grid[(x, y, z)] = cell = [];
            }

            cell.Add((f, _count++));
            return f;
        }

        private static (long, long, long) Key(Vec3 p) => ((long)Math.Floor(p.X / Weld), (long)Math.Floor(p.Y / Weld), (long)Math.Floor(p.Z / Weld));
    }
}
