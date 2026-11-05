using System.Globalization;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Snap;

/// <summary>
/// Neighbour snap: while a selection is moved, resized or turned, its faces stick to nearby neighbour faces, either
/// touching them or in line with them, when they come within the snap distance (pixels on screen, so it follows the
/// zoom). Every solve starts from the raw (unsnapped) value of the drag and the selection as it was when the drag
/// started; grid snap applies wherever nothing stuck. A face the drag stuck to last step holds on a little longer
/// (<see cref="HoldReach"/>), so two planes a few units apart do not flicker.
/// </summary>
public static class NeighbourSnap
{
    /// <summary>The stuck face holds up to this many times the snap distance, and wins ties at half its gap.</summary>
    public const double HoldReach = 1.5;

    /// <summary>"In line" scores worse than "touching" by this factor.</summary>
    public const double InLineWeight = 3;

    /// <summary>In line reaches at least this far (units) beside the moving face, and at most <see cref="MaxReach"/>.</summary>
    public const double MinReach = 64;

    /// <summary>Candidates whose scores are within this many pixels are taken as level: touching beats in line, then
    /// brush, mesh, actor, baked, then an exact box beats an approximate one, and only then the nearer wins. A face a
    /// unit off the real contact (a sign on a wall) no longer beats the wall.</summary>
    public const double TiePx = 1;

    public const double MaxReach = 2048;

    /// <summary>How much nearer, in pixels, a baked surface must be than an object's face to win.</summary>
    public const double BakedPenaltyPx = 1;

    /// <summary>Baked sweeps are skipped for selections wider than this (they would sweep half the map).</summary>
    public const double MaxBakedSize = 8192;

    /// <summary>Directions lining up in a turn: the projection onto the turn's plane must be this long.</summary>
    private const double MinTurnProjection = 0.3;

    /// <summary>A baked hit counts when its normal is within about 11° of the sweep axis.</summary>
    private const double BakedNormal = 0.98;

    /// <summary>A view axis showing fewer pixels per unit than this is a 2D view's depth: nothing sticks along it.</summary>
    private const double MinPixelsPerUnit = 1e-6;

    /// <summary>How far beside the moving face "in line" reaches for a selection of this size.</summary>
    public static double Reach(Vec3 size) => Math.Clamp(2 * Math.Max(size.X, Math.Max(size.Y, size.Z)), MinReach, MaxReach);

    // ---- move ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sticks a move: for each free axis, the moving shape's faces on that axis against the neighbours' faces on it
    /// (exact coordinates), the best pair within the snap distance setting that axis; oblique faces of the same
    /// direction stick along their normal, projected onto the free axes, when they beat the axis matches; the baked
    /// world is swept as a box along each free axis (touching only). <paramref name="own"/> is where the moved objects'
    /// own baked copies are (default: the shape's box at the start of the drag): a baked contact there is not a neighbour.
    /// </summary>
    public static MoveSnap Move(MovingShape shape, Vec3 rawOffset, IReadOnlyList<int> free, SnapNeighbours n, SnapView view,
        IReadOnlyList<SnapMatch>? stuck, bool baked = true, (Vec3 Min, Vec3 Max)? own = null)
    {
        if (free.Count == 0 || shape.Faces.Count == 0)
        {
            return MoveSnap.None with { Offset = rawOffset };
        }

        double reach = Reach(shape.Size);
        double far = 2 * HoldReach * view.WorldThreshold;
        Vec3 grow = Vec3.One * Math.Max(reach, far);
        Vec3 qMin = shape.Min + rawOffset - grow, qMax = shape.Max + rawOffset + grow;
        var near = new List<SnapFace>();
        n.Near(qMin, qMax, near);

        var best = new Candidate?[3];
        foreach (int a in free)
        {
            best[a] = BestOnAxis(shape, rawOffset, a, near, view, reach, stuck);
            if (baked && n.Baked is { } sweeper && Math.Max(shape.Size.X, Math.Max(shape.Size.Y, shape.Size.Z)) <= MaxBakedSize
                && BakedOnAxis(shape, rawOffset, a, sweeper, view, stuck, own ?? (shape.Min, shape.Max)) is { } b && Better(b, best[a]))
            {
                best[a] = b;
            }
        }

        Candidate? oblique = BestOblique(shape, rawOffset, free, near, view, reach, stuck);
        Vec3 offset = rawOffset;
        bool[] isStuck = new bool[3];
        var matches = new List<SnapMatch>();
        if (oblique is { } ob && ob.Push is { } push
            && free.Where(a => Math.Abs(push[a]) > 1e-12).All(a => best[a] is not { } c || ob.Score < c.Score))
        {
            foreach (int a in free.Where(a => Math.Abs(push[a]) > 1e-12))
            {
                best[a] = null;
                isStuck[a] = true;
                offset = offset.With(a, rawOffset[a] + push[a]);
            }

            matches.Add(ob.Match);
        }

        foreach (int a in free)
        {
            if (best[a] is { } c)
            {
                offset = offset.With(a, c.Value);
                isStuck[a] = true;
                matches.Add(c.Match);
            }
        }

        return new MoveSnap(offset, isStuck[0], isStuck[1], isStuck[2], matches);
    }

    private static Candidate? BestOnAxis(MovingShape shape, Vec3 raw, int a, List<SnapFace> near, SnapView view, double reach,
        IReadOnlyList<SnapMatch>? stuck)
    {
        Candidate? best = null;
        double ppu = view.PixelsPerUnit[a];
        if (ppu < MinPixelsPerUnit)
        {
            return null;
        }

        foreach (SnapFace mf in shape.Faces)
        {
            if (mf.Axis != a)
            {
                continue;
            }

            Vec3 mMin = mf.Min + raw, mMax = mf.Max + raw;
            foreach (SnapFace f in near)
            {
                if (f.Axis != a)
                {
                    continue;
                }

                double gap = f.Coord - (mf.Coord + raw[a]);
                double px = Math.Abs(gap) * ppu;
                if (!(px <= HoldReach * view.ThresholdPx) || !(Math.Abs(gap) <= 2 * HoldReach * view.WorldThreshold))
                {
                    continue;
                }

                if (Classify(f.Min, f.Max, mMin, mMax, a, view, reach, out double overlap) is not { } rel)
                {
                    continue;
                }

                var match = new SnapMatch(a, rel, f, mf, gap);
                bool held = stuck?.Any(s => s.SameAs(match)) == true;
                double limit = held ? HoldReach : 1;
                if (!(px <= limit * view.ThresholdPx) || !(Math.Abs(gap) <= 2 * limit * view.WorldThreshold))
                {
                    continue;
                }

                var c = new Candidate(match, Score(px, rel, held), SnapFace.Tidy(f.Coord - mf.Coord), overlap, null);
                if (Better(c, best))
                {
                    best = c;
                }
            }
        }

        return best;
    }

    private static Candidate? BestOblique(MovingShape shape, Vec3 raw, IReadOnlyList<int> free, List<SnapFace> near, SnapView view,
        double reach, IReadOnlyList<SnapMatch>? stuck)
    {
        Candidate? best = null;
        foreach (SnapFace mf in shape.Faces)
        {
            if (mf.IsAxis)
            {
                continue;
            }

            foreach (SnapFace f in near)
            {
                if (f.IsAxis || Math.Abs(Vec3.Dot(f.Normal, mf.Normal)) < SnapFace.ParallelCos)
                {
                    continue;
                }

                Vec3 nrm = f.Normal;
                double gap = f.PlaneD - Vec3.Dot(nrm, mf.Center) - Vec3.Dot(nrm, raw);
                Vec3 p = Vec3.Zero;
                foreach (int a in free)
                {
                    p = p.With(a, nrm[a]);
                }

                double pp = p.LengthSquared;
                if (pp < 0.25)
                {
                    continue;
                }

                Vec3 push = p * (gap / pp);
                double len = push.Length;
                double px = len < 1e-12 ? 0 : len * view.PixelsPerUnitAlong(push / len);
                if (!(px <= HoldReach * view.ThresholdPx) || !(len <= 2 * HoldReach * view.WorldThreshold))
                {
                    continue;
                }

                if (Classify(f.Min, f.Max, mf.Min + raw + push, mf.Max + raw + push, -1, view, reach, out double overlap) is not { } rel)
                {
                    continue;
                }

                var match = new SnapMatch(-1, rel, f, mf, gap);
                bool held = stuck?.Any(s => s.SameAs(match)) == true;
                double limit = held ? HoldReach : 1;
                if (!(px <= limit * view.ThresholdPx) || !(len <= 2 * limit * view.WorldThreshold))
                {
                    continue;
                }

                var c = new Candidate(match, Score(px, rel, held), 0, overlap, push);
                if (Better(c, best))
                {
                    best = c;
                }
            }
        }

        return best;
    }

    /// <summary>Sweeps the moving box along an axis through the baked world, each way the shape has a face on that side
    /// of its box (the builder box moves only its dragged sides); the nearer hit within the snap distance, its contact
    /// found exactly (a box sweep stops a hair short of the surface). A contact inside <paramref name="own"/> (the moved
    /// objects' own baked copies) is skipped.</summary>
    private static Candidate? BakedOnAxis(MovingShape shape, Vec3 raw, int a, ISnapSweeper sweeper, SnapView view,
        IReadOnlyList<SnapMatch>? stuck, (Vec3 Min, Vec3 Max) own)
    {
        double ppu = view.PixelsPerUnit[a];
        if (ppu < MinPixelsPerUnit)
        {
            return null;
        }

        Vec3 half = shape.Size / 2;
        Vec3 center = shape.Center + raw;
        // Slimmer across the sweep, so a crate sliding along a floor is not stopped by the floor.
        Vec3 extent = Vec3.Zero;
        for (int i = 0; i < 3; i++)
        {
            extent = extent.With(i, i == a ? half[i] : Math.Max(half[i] - Math.Min(1, half[i] * 0.1), 0));
        }

        double w = 2 * HoldReach * view.WorldThreshold;
        Candidate? best = null;
        for (int s = -1; s <= 1; s += 2)
        {
            if (!HasSide(shape, a, s))
            {
                continue;
            }

            Vec3 dir = Vec3.Zero.With(a, s);
            if (sweeper.Sweep(center, center + dir * w, extent) is not { } hit || -hit.Normal[a] * s < BakedNormal)
            {
                continue;
            }

            double dist = hit.Exact ? hit.Time * w : Contact(sweeper, center, extent, dir, hit.Time * w);
            double face = s > 0 ? shape.Max[a] + raw[a] : shape.Min[a] + raw[a];
            double coord = Round(face + s * dist);

            // The moved object's own baked copy (a baked brush, a baked mesh) is where the drag started: not a neighbour.
            Vec3 contact = (center + dir * (dist + half[a])).With(a, coord);
            if (Inside(contact, own.Min - Vec3.One, own.Max + Vec3.One))
            {
                continue;
            }

            double gap = coord - face;
            double px = Math.Abs(gap) * ppu;
            SnapFace target = BakedFace(shape, raw, a, s, coord);
            SnapFace moving = OrientedBox.Aligned(shape.Min, shape.Max).Faces(null, SnapSource.Brush, false)[a * 2 + (s > 0 ? 1 : 0)];
            var match = new SnapMatch(a, SnapRelation.Touching, target, moving, gap);
            bool held = stuck?.Any(x => x.SameAs(match)) == true;
            if (!(px <= (held ? HoldReach : 1) * view.ThresholdPx))
            {
                continue;
            }

            double value = coord - (s > 0 ? shape.Max[a] : shape.Min[a]);
            var c = new Candidate(match, Score(px, SnapRelation.Touching, held), value, 0, null);
            if (Better(c, best))
            {
                best = c;
            }
        }

        return best;
    }

    /// <summary>True when the shape has a face on side <paramref name="s"/> of its box along axis <paramref name="a"/>
    /// (facing that way and reaching that side of the box).</summary>
    private static bool HasSide(MovingShape shape, int a, int s) =>
        shape.Faces.Any(f => f.Normal[a] * s > 0.5 && (s > 0 ? f.Max[a] >= shape.Max[a] - 1e-3 : f.Min[a] <= shape.Min[a] + 1e-3));

    /// <summary>The distance a box can move along <paramref name="dir"/> before it touches the baked world, refined
    /// from a sweep's stop (pulled back up to a unit) by halving between free and overlapping.</summary>
    private static double Contact(ISnapSweeper sweeper, Vec3 center, Vec3 extent, Vec3 dir, double stop)
    {
        double lo = stop, hi = stop + 1.5;
        if (!sweeper.Overlaps(center + dir * hi, extent) || sweeper.Overlaps(center + dir * lo, extent))
        {
            return stop;
        }

        for (int i = 0; i < 14; i++)
        {
            double mid = (lo + hi) / 2;
            if (sweeper.Overlaps(center + dir * mid, extent))
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return lo;
    }

    /// <summary>A baked contact coordinate: float data, rounded to a thousandth, and to a whole unit within an eighth
    /// (compiled BSP surfaces stop a box about a tenth of a unit short of the plane the brush had).</summary>
    private static double Round(double v)
    {
        double r = Math.Round(v, 3);
        return Math.Abs(r - Math.Round(r)) <= 0.125 ? Math.Round(r) : r;
    }

    /// <summary>The baked surface's stand-in face: the moving box's footprint on the hit plane.</summary>
    private static SnapFace BakedFace(MovingShape shape, Vec3 raw, int a, int s, double coord)
    {
        Vec3 min = (shape.Min + raw).With(a, coord), max = (shape.Max + raw).With(a, coord);
        int u = (a + 1) % 3, v = (a + 2) % 3;
        Vec3 P(double x, double y) => min.With(u, x).With(v, y);
        Vec3[] poly = [P(min[u], min[v]), P(max[u], min[v]), P(max[u], max[v]), P(min[u], max[v])];
        if (s > 0)
        {
            Array.Reverse(poly);
        }

        return SnapFace.Of(null, 0, SnapSource.Baked, poly, normal: Vec3.Zero.With(a, -s))
            ?? new SnapFace(null, 0, SnapSource.Baked, Vec3.Zero.With(a, -s), -s * coord, poly, min, max, a, coord, false);
    }

    // ---- scale ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sticks a scale about <paramref name="pivot"/> along world <paramref name="axis"/> (-1: uniform): the factor that
    /// puts one of the moving box's faces on a neighbour's plane, when that face comes within the snap distance.
    /// The caller checks the selection is aligned to the axes it scales.
    /// </summary>
    public static ValueSnap? Scale(MovingShape shape, Vec3 pivot, int axis, double rawFactor, SnapNeighbours n, SnapView view,
        SnapMatch? stuck, bool baked = true)
    {
        int[] axes = axis >= 0 ? [axis] : [0, 1, 2];
        Vec3 ScaleBy(Vec3 p, double f)
        {
            foreach (int k in axes)
            {
                p = p.With(k, pivot[k] + (p[k] - pivot[k]) * f);
            }

            return p;
        }

        Vec3 sMin = Vec3.Min(ScaleBy(shape.Min, rawFactor), ScaleBy(shape.Max, rawFactor));
        Vec3 sMax = Vec3.Max(ScaleBy(shape.Min, rawFactor), ScaleBy(shape.Max, rawFactor));
        double reach = Reach(sMax - sMin);
        Vec3 grow = Vec3.One * Math.Max(reach, 2 * HoldReach * view.WorldThreshold);
        var near = new List<SnapFace>();
        n.Near(sMin - grow, sMax + grow, near);

        Candidate? best = null;
        foreach (int k in axes)
        {
            // A 2D view's depth axis (a uniform scale there): nothing to see, so nothing sticks along it.
            double ppu = view.PixelsPerUnit[k];
            if (ppu < MinPixelsPerUnit)
            {
                continue;
            }

            foreach (SnapFace mf in shape.Faces)
            {
                double rel = mf.Coord - pivot[k];
                if (mf.Axis != k || Math.Abs(rel) <= 0.5)
                {
                    continue;
                }

                double faceRaw = pivot[k] + rel * rawFactor;
                Vec3 mMin = Vec3.Min(ScaleBy(mf.Min, rawFactor), ScaleBy(mf.Max, rawFactor));
                Vec3 mMax = Vec3.Max(ScaleBy(mf.Min, rawFactor), ScaleBy(mf.Max, rawFactor));
                foreach (SnapFace f in near)
                {
                    if (f.Axis != k)
                    {
                        continue;
                    }

                    double factor = (f.Coord - pivot[k]) / rel;
                    double gap = f.Coord - faceRaw;
                    if (factor < 0.125 || Classify(f.Min, f.Max, mMin, mMax, k, view, reach, out double overlap) is not { } relation)
                    {
                        continue;
                    }

                    var match = new SnapMatch(k, relation, f, mf, gap);
                    Consider(ref best, match, Math.Abs(gap) * ppu, Math.Abs(gap), factor, overlap, view, stuck);
                }
            }

            // The baked world: the box's two sides on this axis, each swept as a thin slab.
            if (!baked || n.Baked is not { } sweeper || (sMax - sMin).Length > MaxBakedSize)
            {
                continue;
            }

            IReadOnlyList<SnapFace> box = OrientedBox.Aligned(shape.Min, shape.Max).Faces(null, SnapSource.Brush, false);
            for (int side = 0; side < 2; side++)
            {
                double start = side == 0 ? shape.Min[k] : shape.Max[k];
                double rel = start - pivot[k];
                if (Math.Abs(rel) <= 0.5)
                {
                    continue;
                }

                // A hit on the side's own starting plane is the object's own baked copy, not a neighbour.
                double faceRaw = pivot[k] + rel * rawFactor;
                Vec3 fMin = sMin.With(k, faceRaw), fMax = sMax.With(k, faceRaw);
                if (BakedFace(sweeper, fMin, fMax, k, view) is { } hit && Math.Abs(hit.Coord - start) > 0.5
                    && (hit.Coord - pivot[k]) / rel is var factor && factor >= 0.125)
                {
                    var match = new SnapMatch(k, SnapRelation.Touching, hit.Face, box[k * 2 + side], hit.Coord - faceRaw);
                    Consider(ref best, match, Math.Abs(hit.Coord - faceRaw) * ppu, Math.Abs(hit.Coord - faceRaw), factor, 0, view, stuck);
                }
            }
        }

        return best is { } b ? new ValueSnap(b.Value, b.Match) : null;
    }

    // ---- extrude -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sticks a face extrude: the distance along the grabbed face's normal that puts it on a parallel neighbour plane
    /// (either facing), when it comes within the snap distance of the raw distance. Distances at or below
    /// <paramref name="minDistance"/> (the face pushed through its own brush, which the extrude refuses) are not offered.
    /// </summary>
    public static ValueSnap? Extrude(SnapFace grabbed, double rawDistance, SnapNeighbours n, SnapView view, SnapMatch? stuck,
        IReadOnlySet<string>? excludeBrushes = null, bool baked = true, double minDistance = double.NegativeInfinity)
    {
        Vec3 nrm = grabbed.Normal;
        double ppu = view.PixelsPerUnitAlong(nrm);
        if (ppu < MinPixelsPerUnit)
        {
            // The face moves along a 2D view's depth: nothing on screen to stick to.
            return null;
        }

        SnapFace moved = grabbed.Offset(nrm * rawDistance);
        double reach = Reach(grabbed.Max - grabbed.Min);
        Vec3 grow = Vec3.One * Math.Max(reach, 2 * HoldReach * view.WorldThreshold);
        var near = new List<SnapFace>();
        n.Near(moved.Min - grow, moved.Max + grow, near);
        double dGrab = grabbed.IsAxis ? nrm[grabbed.Axis] * grabbed.Coord : grabbed.PlaneD;

        Candidate? best = null;
        foreach (SnapFace f in near)
        {
            if (Math.Abs(Vec3.Dot(f.Normal, nrm)) < SnapFace.ParallelCos
                || (excludeBrushes is not null && f.Owner is { Kind: ObjectKind.Brush } o && excludeBrushes.Contains(o.Id)))
            {
                continue;
            }

            double dTarget = grabbed.IsAxis && f.Axis == grabbed.Axis ? nrm[grabbed.Axis] * f.Coord : Vec3.Dot(nrm, f.Center);
            double dist = SnapFace.Tidy(dTarget - dGrab);
            double gap = dist - rawDistance;
            Vec3 by = nrm * dist;
            if (dist <= minDistance
                || Classify(f.Min, f.Max, grabbed.Min + by, grabbed.Max + by, grabbed.Axis, view, reach, out double overlap) is not { } relation)
            {
                continue;
            }

            var match = new SnapMatch(grabbed.Axis, relation, f, grabbed, gap);
            Consider(ref best, match, Math.Abs(gap) * ppu, Math.Abs(gap), dist, overlap, view, stuck);
        }

        if (baked && grabbed.IsAxis && n.Baked is { } sweeper && (grabbed.Max - grabbed.Min).Length <= MaxBakedSize
            && BakedFace(sweeper, moved.Min, moved.Max, grabbed.Axis, view) is { } hit
            // A hit on the grabbed face's starting plane is its own brush's baked copy.
            && Math.Abs(hit.Coord - grabbed.Coord) > 0.5
            && nrm[grabbed.Axis] * hit.Coord - dGrab > minDistance)
        {
            double dist = nrm[grabbed.Axis] * hit.Coord - dGrab;
            var match = new SnapMatch(grabbed.Axis, SnapRelation.Touching, hit.Face, grabbed, dist - rawDistance);
            Consider(ref best, match, Math.Abs(dist - rawDistance) * ppu, Math.Abs(dist - rawDistance), dist, 0, view, stuck);
        }

        return best is { } b ? new ValueSnap(b.Value, b.Match) : null;
    }

    /// <summary>
    /// The baked surface nearest a face moving along an axis (a slab one unit thick over the face's footprint, swept
    /// both ways over the snap reach): the plane's coordinate and a stand-in face, or null.
    /// </summary>
    private static (double Coord, SnapFace Face)? BakedFace(ISnapSweeper sweeper, Vec3 faceMin, Vec3 faceMax, int a, SnapView view)
    {
        Vec3 center = (faceMin + faceMax) / 2;
        Vec3 extent = Vec3.Zero;
        for (int i = 0; i < 3; i++)
        {
            double h = (faceMax[i] - faceMin[i]) / 2;
            extent = extent.With(i, i == a ? 0.5 : Math.Max(h - Math.Min(1, h * 0.1), 0.5));
        }

        double w = 2 * HoldReach * view.WorldThreshold;
        (double Coord, SnapFace Face)? best = null;
        double bestGap = double.MaxValue;
        for (int s = -1; s <= 1; s += 2)
        {
            Vec3 dir = Vec3.Zero.With(a, s);
            if (sweeper.Sweep(center, center + dir * w, extent) is not { } hit || -hit.Normal[a] * s < BakedNormal)
            {
                continue;
            }

            double dist = hit.Exact ? hit.Time * w : Contact(sweeper, center, extent, dir, hit.Time * w);
            double coord = Round(center[a] + s * (dist + extent[a]));
            double gap = Math.Abs(coord - center[a]);
            if (gap >= bestGap)
            {
                continue;
            }

            bestGap = gap;
            Vec3 min = faceMin.With(a, coord), max = faceMax.With(a, coord);
            int u = (a + 1) % 3, v = (a + 2) % 3;
            Vec3 P(double x, double y) => min.With(u, x).With(v, y);
            Vec3[] poly = [P(min[u], min[v]), P(max[u], min[v]), P(max[u], max[v]), P(min[u], max[v])];
            SnapFace face = SnapFace.Of(null, 0, SnapSource.Baked, poly, normal: Vec3.Zero.With(a, -s))
                ?? new SnapFace(null, 0, SnapSource.Baked, Vec3.Zero.With(a, -s), -s * coord, poly, min, max, a, coord, false);
            best = (coord, face);
        }

        return best;
    }

    // ---- turn ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sticks a turn about world <paramref name="axis"/>: the turn (rotator units) nearest the raw turn that lines one of
    /// the selection's own directions (<paramref name="own"/>, at the drag's start) up with a neighbour face's normal
    /// (either way, so every 90° for a box), within <paramref name="threshold"/> units. <paramref name="sense"/> is +1
    /// when a positive turn carries the axis after <paramref name="axis"/> towards the one after that. A neighbour
    /// actor turned purely about the same axis also offers its own rotation, matched exactly
    /// (<paramref name="rotationOf"/>, against <paramref name="ownRotation"/>). With a rotation grid of
    /// <paramref name="grid"/> units (0: none), turns that land on the grid are left to it, so a stick never hides the
    /// grid steps beside it.
    /// </summary>
    public static TurnSnap? Turn(IReadOnlyList<Vec3> own, Rot? ownRotation, int axis, int sense, Vec3 pivot, double radius, int rawUnits,
        int threshold, SnapNeighbours n, SnapMatch? stuck, Func<ObjectRef, Rot?>? rotationOf = null, int grid = 0)
    {
        int u = (axis + 1) % 3, v = (axis + 2) % 3;
        var alphas = new List<double>();
        foreach (Vec3 d in own)
        {
            if (Math.Sqrt(d[u] * d[u] + d[v] * d[v]) >= MinTurnProjection)
            {
                alphas.Add(Math.Atan2(d[v], d[u]));
            }
        }

        int? ownUnits = ownRotation is { } r0 && PureAbout(r0, axis) is int o ? o : null;
        if (alphas.Count == 0 && ownUnits is null)
        {
            return null;
        }

        double area = radius + MinReach;
        var near = new List<SnapFace>();
        n.Near(pivot - Vec3.One * area, pivot + Vec3.One * area, near);
        const double half = Rot.UnitsPerTurn / 2.0;
        const double quarter = Rot.UnitsPerTurn / 4.0;
        (TurnSnap Snap, double Score, double Distance, double Area)? best = null;
        var seen = new HashSet<ObjectRef>();

        void Offer(int units, double diff, SnapFace f, Vec3 dir, bool exact)
        {
            if (grid > 0 && units % grid == 0)
            {
                return;
            }

            var match = new SnapMatch(axis, SnapRelation.InLine, f, null, diff);
            bool held = stuck is not null && stuck.Target.Owner == f.Owner && stuck.Target.Face == f.Face;
            if (diff > (held ? HoldReach : 1) * threshold)
            {
                return;
            }

            double score = (held ? 0.5 : 1) * diff - (exact ? 2 : 0);
            double dist = (f.Center - pivot).Length;
            if (best is not { } b || score < b.Score - 1e-9 || (Math.Abs(score - b.Score) <= 1e-9 && (dist < b.Distance - 1e-6
                    || (Math.Abs(dist - b.Distance) <= 1e-6 && f.Area > b.Area))))
            {
                best = (new TurnSnap(units, match, dir, exact), score, dist, f.Area);
            }
        }

        foreach (SnapFace f in near)
        {
            if (f.Area < 16 || (f.Center - pivot).Length > area)
            {
                continue;
            }

            Vec3 nrm = f.Normal;
            double len = Math.Sqrt(nrm[u] * nrm[u] + nrm[v] * nrm[v]);
            if (len >= MinTurnProjection)
            {
                double beta = Math.Atan2(nrm[v], nrm[u]);
                Vec3 dir = Vec3.Zero.With(u, nrm[u] / len).With(v, nrm[v] / len);
                foreach (double alpha in alphas)
                {
                    double t = (beta - alpha) * Rot.UnitsPerTurn / (2 * Math.PI) * sense;
                    t += half * Math.Round((rawUnits - t) / half);
                    int units = (int)Math.Round(t);
                    Offer(units, Math.Abs(t - rawUnits), f, dir, false);
                }
            }

            if (ownUnits is int mine && rotationOf is not null && f.Owner is { Kind: ObjectKind.Actor } owner && seen.Add(owner)
                && rotationOf(owner) is { } theirs && PureAbout(theirs, axis) is int their)
            {
                double t = their - mine;
                t += quarter * Math.Round((rawUnits - t) / quarter);
                int units = (int)Math.Round(t);
                Rot turned = axis switch { 0 => new Rot(0, 0, their), 1 => new Rot(their, 0, 0), _ => new Rot(0, their, 0) };
                Vec3 fwd = turned.Axes().Forward;
                double fl = Math.Sqrt(fwd[u] * fwd[u] + fwd[v] * fwd[v]);
                Vec3 dir = fl > 1e-9 ? Vec3.Zero.With(u, fwd[u] / fl).With(v, fwd[v] / fl) : Vec3.Zero.With(u, 1);
                Offer(units, Math.Abs(t - rawUnits), f, dir, true);
            }
        }

        return best?.Snap;
    }

    /// <summary>The rotator component of a rotation that turns about one world axis only, or null.</summary>
    public static int? PureAbout(Rot r, int axis)
    {
        Rot w = r.Normalized();
        return axis switch
        {
            0 when w.Pitch == 0 && w.Yaw == 0 => r.Roll,
            1 when w.Yaw == 0 && w.Roll == 0 => r.Pitch,
            2 when w.Pitch == 0 && w.Roll == 0 => r.Yaw,
            _ => null,
        };
    }

    // ---- text ----------------------------------------------------------------------------------------------------

    /// <summary>"stuck X to Kerb1 +X face (touching)".</summary>
    public static string Describe(SnapMatch m)
    {
        string axis = m.Axis >= 0 ? "XYZ"[m.Axis] + " " : "";
        string face = m.Target.Source == SnapSource.Baked ? "" : " " + m.Target.Side;
        return $"stuck {axis}to {m.Target.Name}{face} ({Relation(m.Relation)})";
    }

    /// <summary>"lines up with Brush214 (wall)".</summary>
    public static string DescribeTurn(TurnSnap t) =>
        $"lines up with {t.Match.Target.Name} ({(t.Exact ? "its rotation" : Surface(t.Match.Target.Normal))})";

    public static string Relation(SnapRelation r) => r == SnapRelation.Touching ? "touching" : "in line";

    /// <summary>"wall", "floor" or "ceiling" for a face normal (as the face is stored).</summary>
    public static string Surface(Vec3 normal) => normal.Z > 0.7 ? "floor" : normal.Z < -0.7 ? "ceiling" : "wall";

    /// <summary>A number for notes: up to three decimals, invariant.</summary>
    public static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- shared --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Touching when the two footprints overlap on every axis but <paramref name="skip"/> (widened by the snap
    /// distance); in line when they do not but lie within <paramref name="reach"/> on every axis, a 2D view's depth
    /// included (a wall on another storey is not "in line"), and the view takes in-line matches; otherwise null.
    /// </summary>
    private static SnapRelation? Classify(Vec3 tMin, Vec3 tMax, Vec3 mMin, Vec3 mMax, int skip, SnapView view, double reach,
        out double overlap)
    {
        double tw = view.WorldThreshold;
        bool touching = true;
        overlap = 1;
        for (int b = 0; b < 3; b++)
        {
            if (b == skip)
            {
                continue;
            }

            double apart = Math.Max(tMin[b] - mMax[b], mMin[b] - tMax[b]);
            if (apart > tw)
            {
                touching = false;
                if (!view.InLine || apart > reach)
                {
                    overlap = 0;
                    return null;
                }
            }
            else
            {
                overlap *= Math.Max(Math.Min(tMax[b], mMax[b]) - Math.Max(tMin[b], mMin[b]), 0) + 1;
            }
        }

        return touching ? SnapRelation.Touching : SnapRelation.InLine;
    }

    private static double Score(double px, SnapRelation rel, bool held) =>
        px * (rel == SnapRelation.Touching ? 1 : InLineWeight) * (held ? 0.5 : 1);

    private static void Consider(ref Candidate? best, SnapMatch match, double px, double gap, double value, double overlap, SnapView view,
        SnapMatch? stuck)
    {
        bool held = match.SameAs(stuck);
        double limit = held ? HoldReach : 1;
        if (!(px <= limit * view.ThresholdPx) || !(gap <= 2 * limit * view.WorldThreshold))
        {
            return;
        }

        var c = new Candidate(match, Score(px, match.Relation, held), value, overlap, null);
        if (Better(c, best))
        {
            best = c;
        }
    }

    /// <summary>A clearly lower score wins (more than <see cref="TiePx"/> apart; a baked surface counts a pixel further,
    /// so a brush and its own compiled copy stick to the brush). Scores within that band are level: touching first,
    /// then brush before mesh before actor before baked, then an exact box before an approximate one, then the lower
    /// score, then the larger overlap.</summary>
    private static bool Better(Candidate c, Candidate? best)
    {
        if (best is not { } b)
        {
            return true;
        }

        double cs = c.Score + (c.Match.Target.Source == SnapSource.Baked ? BakedPenaltyPx : 0);
        double bs = b.Score + (b.Match.Target.Source == SnapSource.Baked ? BakedPenaltyPx : 0);
        if (Math.Abs(cs - bs) > TiePx)
        {
            return cs < bs;
        }

        if (c.Match.Relation != b.Match.Relation)
        {
            return c.Match.Relation == SnapRelation.Touching;
        }

        if (c.Match.Target.Source != b.Match.Target.Source)
        {
            return c.Match.Target.Source < b.Match.Target.Source;
        }

        if (c.Match.Target.Approximate != b.Match.Target.Approximate)
        {
            return !c.Match.Target.Approximate;
        }

        if (Math.Abs(cs - bs) > 1e-9)
        {
            return cs < bs;
        }

        return c.Overlap > b.Overlap;
    }

    private static bool Inside(Vec3 p, Vec3 min, Vec3 max) =>
        p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;

    /// <summary>A candidate stick: its match, score (lower wins), the value it sets (an axis offset, a factor, a
    /// distance), its footprint overlap (ties) and, for an oblique plane, the push it adds to the raw offset.</summary>
    private readonly record struct Candidate(SnapMatch Match, double Score, double Value, double Overlap, Vec3? Push);
}
