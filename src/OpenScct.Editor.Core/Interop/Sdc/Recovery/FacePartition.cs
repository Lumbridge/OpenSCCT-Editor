using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc.Recovery;

/// <summary>One part of a recovered brush face and the surf that covers it (<see cref="FacePartition"/>); a face no
/// surf touches has <see cref="Covered"/> false and takes a stand-in surf.</summary>
public sealed record FacePiece(List<Vec3> Points, int Surf, bool Covered);

/// <summary>
/// Finds which surfs cover a recovered brush face (after RE+'s RecoveredSurfacePartition, in doubles): the structural
/// node polygons on the face's plane that face the way its surface does (into the empty side: against a subtract
/// brush's face, along an add brush's), normals within 1e-5. On the plane means within 0.02 units first, then, while
/// those leave part of the face uncovered, within the BSP's 0.25-unit coplanar band (RE+ asks 0.02 of every point and
/// leaves such faces to a stand-in), measured at the face: the node's plane at every corner of the face, or the
/// polygon's own points. The surf with the most polygon area on the face takes it; the polygons of other surfs cut
/// their parts out of it, earlier polygons first, and parts of one surf are joined back where their union is convex.
/// Surfs that map a texture the same way (material, flags, axes, offsets within 0.01 texels, lightmap scales) count as
/// one, so a face covered by several of them stays whole. A cut that would leave a part thinner than
/// <see cref="SplitBand"/> is not made: such a sliver keeps the material of what it borders, as the native importer's
/// split band would have it. A face no polygon touches is left for a stand-in.
/// </summary>
public sealed class FacePartition
{
    /// <summary>How far off the face's plane a covering polygon's node plane may lie.</summary>
    public const double PlaneTolerance = 0.02;

    /// <summary>The BSP's coplanar band: how far a node polygon's points may stray from its node's plane.</summary>
    public const double CoplanarBand = 0.25;

    /// <summary>How far from parallel a covering polygon's normal may be (1 - |cos|).</summary>
    public const double NormalTolerance = 1e-5;

    /// <summary>The last tier of a thorough look (<see cref="Partition"/>): how far off (units) and how far from parallel
    /// (1 - |cos|, about 2.5 degrees) a polygon may lie.</summary>
    public const double LooseTolerance = 1, LooseNormalTolerance = 1e-3;

    /// <summary>The thinnest part a material cut may leave.</summary>
    public const double SplitBand = 0.1;

    private readonly CookedBsp _bsp;
    private readonly Dictionary<long, List<int>> _byDistance = new();
    private readonly List<int> _structural = new();

    /// <summary>Which way a covering polygon faces the faces' outward normal: a recovered cell is empty space in a world
    /// that starts solid (its walls' polygons face into it, against its faces) and solid in one that starts empty.</summary>
    private readonly int _facing;
    private readonly int[] _mapping;

    public FacePartition(CookedBsp bsp, bool unify)
    {
        _bsp = bsp;
        _facing = bsp.RootOutside ? 1 : -1;
        for (int i = 0; i < bsp.Faces.Count; i++)
        {
            CookedFace f = bsp.Faces[i];
            if (!f.Structural)
            {
                continue;
            }

            _structural.Add(i);
            long key = (long)Math.Floor(Math.Abs(f.PlaneDistance));
            if (!_byDistance.TryGetValue(key, out List<int>? list))
            {
                _byDistance[key] = list = [];
            }

            list.Add(i);
        }

        _mapping = Enumerable.Range(0, bsp.Surfs.Count).ToArray();
        if (unify)
        {
            Unify(bsp.Surfs);
        }
    }

    /// <summary>Surfs that were made one with another (their pieces join).</summary>
    public int Unified { get; private set; }

    /// <summary>The surf standing for <paramref name="surf"/> (itself unless a matching earlier surf maps the same).</summary>
    public int Canonical(int surf) => surf >= 0 && surf < _mapping.Length ? _mapping[surf] : surf;

    private void Unify(IReadOnlyList<CookedSurf> surfs)
    {
        var groups = new Dictionary<(string, int, Vec3, Vec3, double), List<int>>();
        for (int i = 0; i < surfs.Count; i++)
        {
            CookedSurf s = surfs[i];
            var key = ((s.Material ?? "").ToLowerInvariant(), s.Flags, s.TextureU, s.TextureV, s.LightMapScale);
            if (!groups.TryGetValue(key, out List<int>? list))
            {
                groups[key] = list = [];
            }

            bool found = false;
            foreach (int j in list)
            {
                CookedSurf o = surfs[j];
                Vec3 delta = s.TextureBase - o.TextureBase;
                if (Math.Abs(Vec3.Dot(delta, s.TextureU)) <= 0.01 && Math.Abs(Vec3.Dot(delta, s.TextureV)) <= 0.01
                    && s.LightMapScalePsx2 == o.LightMapScalePsx2)
                {
                    _mapping[i] = j;
                    Unified++;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                list.Add(i);
            }
        }
    }

    /// <summary>The structural node polygons that can cover a face on this plane: their node's plane within
    /// <paramref name="tolerance"/> of every corner of the face, or their own points within it of the face's plane
    /// (planes a hair from parallel part far from the origin, so the distance is measured at the face). <paramref name="thorough"/> looks at every polygon, not only those whose
    /// plane, at the origin, lies within reach of the face's.</summary>
    private List<int> Candidates(Vec3 n, double d, List<Vec3> face, Aabb box, double tolerance, double normalTolerance, bool thorough)
    {
        var found = new List<int>();
        void Consider(int i)
        {
            // The node's plane decides: the polygon's own points may stray within the BSP's coplanar band.
            CookedFace c = _bsp.Faces[i];
            double cos = Vec3.Dot(c.PlaneNormal, n);
            if (Math.Sign(Vec3.Dot(c.Normal, n)) != _facing || Math.Abs(cos) < 1 - normalTolerance || !c.Bounds.Overlaps(box, Math.Max(tolerance, CoplanarBand)))
            {
                return;
            }

            // On the face's plane: its node's plane passes within the tolerance of every corner of the face, or its own
            // points lie within it of the face's plane (a node plane tilted a hair parts from a large face's far corners).
            if (face.TrueForAll(p => Math.Abs(Vec3.Dot(c.PlaneNormal, p) - c.PlaneDistance) <= tolerance)
                || c.Points.TrueForAll(p => Math.Abs(Vec3.Dot(n, p) - d) <= tolerance))
            {
                found.Add(i);
            }
        }

        if (thorough)
        {
            _structural.ForEach(Consider);
            return found;
        }

        // A polygon's plane within the normal tolerance of the face's can lie this much further from the origin than the
        // face's, measured there, and still pass through the face.
        double reach = Math.Sqrt(2 * normalTolerance) * face.Max(p => p.Length) + tolerance;
        long key = (long)Math.Floor(Math.Abs(d)), span = (long)Math.Ceiling(reach);
        for (long k = Math.Max(0, key - span); k <= key + span; k++)
        {
            if (_byDistance.TryGetValue(k, out List<int>? list))
            {
                list.ForEach(Consider);
            }
        }

        found.Sort();
        return found;
    }

    /// <summary>
    /// The parts of a convex face (outward normal <paramref name="n"/>) and the canonical surf covering each. The surf
    /// covering the most of the face takes it whole; polygons of other surfs cut their parts out of it, earlier
    /// polygons first. One part with the face's own outline when one surf covers it all; a face no polygon touches comes
    /// back whole and not covered. <paramref name="thorough"/> considers every polygon of the map (slow: for a face found
    /// uncovered that lies between solid and empty space).
    /// </summary>
    public List<FacePiece> Partition(List<Vec3> face, Vec3 n, bool thorough = false)
    {
        double d = Vec3.Dot(n, face[0]);
        // Each polygon on the face's plane facing the right way, wound about the face's normal, with the area it shares
        // with the face. Polygons within the plane tolerance come first; while they leave part of the face uncovered,
        // those within the BSP's coplanar band join them (a face cut by a splitter just off a wall, a portal 0.03 units
        // from it, or by a split plane tilted a hair from the wall's).
        var masks = new List<(int Surf, List<Vec3> Mask)>();
        var areas = new Dictionary<int, double>();
        var used = new HashSet<int>();
        double faceArea = PolygonOps.Area(face, n), shares = 0;
        // A thorough look ends with polygons a little further off and tilted (within a unit and LooseNormalTolerance),
        // for a wall whose split plane is not its polygon's: better its own neighbour's surface than none.
        ReadOnlySpan<(double Distance, double Normal)> tiers = thorough
            ? [(PlaneTolerance, NormalTolerance), (CoplanarBand, NormalTolerance), (LooseTolerance, LooseNormalTolerance)]
            : [(PlaneTolerance, NormalTolerance), (CoplanarBand, NormalTolerance)];
        foreach ((double tolerance, double normalTolerance) in tiers)
        {
            foreach (int ci in Candidates(n, d, face, Aabb.Of(face), tolerance, normalTolerance, thorough))
            {
                if (!used.Add(ci))
                {
                    continue;
                }

                CookedFace c = _bsp.Faces[ci];
                List<Vec3> mask = c.Points.Select(p => p - n * (Vec3.Dot(n, p) - d)).ToList();
                if (Vec3.Dot(c.Normal, n) < 0)
                {
                    mask.Reverse();
                }

                double shared = OverlapArea(face, mask, n);
                if (shared <= SplitBand * SplitBand)
                {
                    continue;
                }

                int surf = Canonical(c.Surf);
                masks.Add((surf, mask));
                areas[surf] = areas.GetValueOrDefault(surf) + shared;
                shares += shared;
            }

            if (shares >= faceArea - Math.Max(SplitBand * SplitBand, 1e-6 * faceArea))
            {
                break;
            }
        }

        if (masks.Count == 0)
        {
            return [new FacePiece(face, -1, false)];
        }

        // The surf sharing the most area with the face is the face's own; only the others are cut out of it.
        int main = areas.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        var remaining = new List<List<Vec3>> { face };
        var covered = new List<(List<Vec3> Points, int Surf)>();
        foreach ((int surf, List<Vec3> mask) in masks)
        {
            if (surf == main)
            {
                continue;
            }

            var next = new List<List<Vec3>>();
            foreach (List<Vec3> rem in remaining)
            {
                (List<Vec3>? inside, List<List<Vec3>> outside) = Subtract(rem, mask, n);
                if (inside is null)
                {
                    next.Add(rem);
                    continue;
                }

                covered.Add((inside, surf));
                next.AddRange(outside);
            }

            remaining = next;
            if (remaining.Count == 0)
            {
                break;
            }
        }

        if (covered.Count == 0)
        {
            return [new FacePiece(face, main, true)];
        }

        covered.AddRange(remaining.Select(r => (r, main)));
        // Pieces of one surf join where they stay convex.
        var result = new List<FacePiece>();
        foreach (IGrouping<int, (List<Vec3> Points, int Surf)> g in covered.GroupBy(p => p.Surf).OrderBy(g => g.Key))
        {
            foreach (List<Vec3> joined in PolygonOps.Coalesce(g.Select(p => p.Points).ToList(), n))
            {
                result.Add(new FacePiece(joined, g.Key, true));
            }
        }

        return result;
    }

    /// <summary>The area two convex polygons on one plane share.</summary>
    private static double OverlapArea(List<Vec3> face, List<Vec3> mask, Vec3 n)
    {
        List<Vec3>? part = mask;
        for (int i = 0; i < face.Count && part is not null; i++)
        {
            Vec3 a = face[i], edge = face[(i + 1) % face.Count] - a;
            Vec3 outward = Vec3.Cross(edge, n);
            double length = outward.Length;
            if (length < 1e-9)
            {
                continue;
            }

            outward /= length;
            part = PolygonOps.Split(part, outward, Vec3.Dot(outward, a)).Back;
        }

        return part is null ? 0 : Math.Max(0, PolygonOps.Area(part, n));
    }

    /// <summary>The part of a convex polygon inside a convex mask (null when they do not overlap by more than the split
    /// band) and the convex parts outside it.</summary>
    private static (List<Vec3>? Inside, List<List<Vec3>> Outside) Subtract(List<Vec3> poly, List<Vec3> mask, Vec3 n)
    {
        var outside = new List<List<Vec3>>();
        List<Vec3> current = poly;
        for (int i = 0; i < mask.Count; i++)
        {
            Vec3 a = mask[i], edge = mask[(i + 1) % mask.Count] - a;
            Vec3 outward = Vec3.Cross(edge, n);
            double length = outward.Length;
            if (length < 1e-9)
            {
                continue;
            }

            outward /= length;
            double dist = Vec3.Dot(outward, a);
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (Vec3 p in current)
            {
                double s = Vec3.Dot(outward, p) - dist;
                min = Math.Min(min, s);
                max = Math.Max(max, s);
            }

            if (min >= -SplitBand)
            {
                return (null, [poly]); // wholly outside this edge (or by less than the band inside)
            }

            if (max <= SplitBand)
            {
                continue; // wholly inside this edge (or by less than the band outside)
            }

            (List<Vec3>? front, List<Vec3>? back) = PolygonOps.Split(current, outward, dist);
            if (front is not null)
            {
                outside.Add(front);
            }

            if (back is null)
            {
                return (null, [poly]);
            }

            current = back;
        }

        return (current, outside);
    }
}

/// <summary>Convex polygon helpers for recovered brush faces.</summary>
public static class PolygonOps
{
    /// <summary>Splits a convex polygon by a plane (<c>n.p = d</c>): the part in front and the part behind, null for an
    /// empty part. Exact zero decisions, one intersection point shared by both halves.</summary>
    public static (List<Vec3>? Front, List<Vec3>? Back) Split(List<Vec3> poly, Vec3 n, double d)
    {
        var front = new List<Vec3>();
        var back = new List<Vec3>();
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3 a = poly[i], b = poly[(i + 1) % poly.Count];
            double da = Vec3.Dot(n, a) - d, db = Vec3.Dot(n, b) - d;
            if (da >= 0)
            {
                front.Add(a);
            }

            if (da <= 0)
            {
                back.Add(a);
            }

            if ((da < 0 && db > 0) || (da > 0 && db < 0))
            {
                Vec3 x = a + (b - a) * (da / (da - db));
                front.Add(x);
                back.Add(x);
            }
        }

        return (Clean(front), Clean(back));
    }

    /// <summary>The polygon without repeated corners, or null when fewer than three remain or it has no area.</summary>
    public static List<Vec3>? Clean(List<Vec3> poly)
    {
        var o = new List<Vec3>(poly.Count);
        foreach (Vec3 p in poly)
        {
            if (o.Count == 0 || !CellGeometry.Near(o[^1], p, 1e-9))
            {
                o.Add(p);
            }
        }

        while (o.Count > 1 && CellGeometry.Near(o[0], o[^1], 1e-9))
        {
            o.RemoveAt(o.Count - 1);
        }

        return o.Count >= 3 && CookedBsp.Newell(o).Length > 1e-9 ? o : null;
    }

    public static double Area(List<Vec3> poly, Vec3 n) => Vec3.Dot(CookedBsp.Newell(poly), n) / 2;

    /// <summary>Removes corners lying on the straight line between their neighbours.</summary>
    public static List<Vec3> RemoveCollinear(List<Vec3> poly, double tolerance = 1e-6)
    {
        var o = new List<Vec3>(poly);
        bool changed = true;
        while (changed && o.Count > 3)
        {
            changed = false;
            for (int i = 0; i < o.Count && o.Count > 3; i++)
            {
                Vec3 a = o[(i + o.Count - 1) % o.Count], b = o[i], c = o[(i + 1) % o.Count];
                Vec3 ab = b - a, bc = c - b;
                double lab = ab.Length, lbc = bc.Length;
                if (lab < 1e-9 || lbc < 1e-9 || (Vec3.Cross(ab, bc).Length <= tolerance * lab * lbc && Vec3.Dot(ab, bc) > 0))
                {
                    o.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }

        return o;
    }

    /// <summary>Removes corners within <paramref name="distance"/> units of the straight line between their neighbours
    /// (going on, not doubling back).</summary>
    public static List<Vec3> RemoveStraight(List<Vec3> poly, double distance)
    {
        var o = new List<Vec3>(poly);
        bool changed = true;
        while (changed && o.Count > 3)
        {
            changed = false;
            for (int i = 0; i < o.Count && o.Count > 3; i++)
            {
                Vec3 a = o[(i + o.Count - 1) % o.Count], b = o[i], c = o[(i + 1) % o.Count];
                Vec3 ac = c - a;
                double lac = ac.Length;
                if (lac < 1e-9 || Vec3.Dot(b - a, c - b) <= 0 || Vec3.Cross(b - a, ac).Length / lac > distance)
                {
                    continue;
                }

                o.RemoveAt(i);
                changed = true;
                i--;
            }
        }

        return o;
    }

    /// <summary>True when every turn of the polygon goes the same way about <paramref name="n"/> (straight on allowed,
    /// doubling back not: a join along one of two shared edges leaves a spike out and back along the other).</summary>
    public static bool IsConvex(List<Vec3> poly, Vec3 n)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3 a = poly[i], b = poly[(i + 1) % poly.Count], c = poly[(i + 2) % poly.Count];
            Vec3 ab = b - a, bc = c - b;
            double scale = ab.Length * bc.Length;
            double turn = Vec3.Dot(Vec3.Cross(ab, bc), n);
            if (turn < -1e-7 * scale || (Math.Abs(turn) <= 1e-7 * scale && Vec3.Dot(ab, bc) < 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Inserts into the polygon's edges the given points that lie on them (between the ends, within
    /// <paramref name="tolerance"/> of the edge), so a neighbour's corner on an edge becomes a corner here too.</summary>
    public static List<Vec3> Subdivide(List<Vec3> poly, IEnumerable<Vec3> points, double tolerance)
    {
        List<Vec3> candidates = points.ToList();
        var o = new List<Vec3>(poly.Count);
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3 a = poly[i], b = poly[(i + 1) % poly.Count];
            o.Add(a);
            Vec3 ab = b - a;
            double len2 = ab.LengthSquared;
            if (len2 < 1e-12)
            {
                continue;
            }

            var on = new List<(double T, Vec3 P)>();
            foreach (Vec3 p in candidates)
            {
                double t = Vec3.Dot(p - a, ab) / len2;
                if (t <= 0 || t >= 1)
                {
                    continue;
                }

                Vec3 foot = a + ab * t;
                if ((p - foot).LengthSquared <= tolerance * tolerance && !CellGeometry.Near(p, a, tolerance) && !CellGeometry.Near(p, b, tolerance))
                {
                    on.Add((t, p));
                }
            }

            foreach ((double _, Vec3 p) in on.OrderBy(x => x.T))
            {
                if (!CellGeometry.Near(o[^1], p, tolerance))
                {
                    o.Add(p);
                }
            }
        }

        return o;
    }

    /// <summary>Joins coplanar convex polygons (wound about <paramref name="n"/>) that share an edge where the union is
    /// convex, until no more join.</summary>
    public static List<List<Vec3>> Coalesce(List<List<Vec3>> polys, Vec3 n)
    {
        var list = polys.Select(p => new List<Vec3>(p)).ToList();
        var boxes = list.Select(p => Aabb.Of(p)).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            for (int j = 0; j < list.Count; j++)
            {
                if (j == i || !boxes[i].Overlaps(boxes[j], 1e-4) || TryJoin(list[i], list[j], n) is not { } joined)
                {
                    continue;
                }

                list[i] = joined;
                boxes[i] = Aabb.Of(joined);
                list.RemoveAt(j);
                boxes.RemoveAt(j);
                if (j < i)
                {
                    i--;
                }

                j = -1; // the joined polygon may now join one tried before
            }
        }

        return list;
    }

    private static List<Vec3>? TryJoin(List<Vec3> p, List<Vec3> q, Vec3 n)
    {
        const double tol = 1e-6;
        List<Vec3> a = Subdivide(p, q, tol), b = Subdivide(q, p, tol);
        for (int i = 0; i < a.Count; i++)
        {
            Vec3 a0 = a[i], a1 = a[(i + 1) % a.Count];
            for (int j = 0; j < b.Count; j++)
            {
                if (!CellGeometry.Near(b[j], a1, tol) || !CellGeometry.Near(b[(j + 1) % b.Count], a0, tol))
                {
                    continue;
                }

                // a from a1 round to a0, then b after a0 round to before a1.
                var joined = new List<Vec3>(a.Count + b.Count);
                for (int k = 0; k < a.Count; k++)
                {
                    joined.Add(a[(i + 1 + k) % a.Count]);
                }

                for (int k = 2; k < b.Count; k++)
                {
                    joined.Add(b[(j + k) % b.Count]);
                }

                List<Vec3> clean = RemoveCollinear(Clean(joined) ?? joined, 1e-9);
                if (clean.Count < 3 || !IsConvex(clean, n))
                {
                    return null;
                }

                double sum = Area(p, n) + Area(q, n), area = Area(clean, n);
                return Math.Abs(area - sum) <= 1e-6 * Math.Max(1, Math.Abs(sum)) ? clean : null;
            }
        }

        return null;
    }

    /// <summary>A convex polygon (straight-on corners allowed) cut into convex parts of at most <paramref name="most"/>
    /// corners (the UE2 FPoly limit is 16), each with area: every cut runs from a real corner to a corner off both
    /// edges through it, halving the polygon as nearly as it can.</summary>
    public static List<List<Vec3>> Limit(List<Vec3> poly, int most)
    {
        if (poly.Count <= most)
        {
            return [poly];
        }

        Vec3 n = CookedBsp.Newell(poly);
        if (n.LengthSquared < 1e-24)
        {
            return [poly];
        }

        n = n.Normalized();
        int count = poly.Count;
        (int A, int B, int Worst) best = (-1, -1, int.MaxValue);
        for (int a = 0; a < count; a++)
        {
            for (int k = 2; k <= count - 2; k++)
            {
                int b = (a + k) % count;
                int worst = Math.Max(k + 1, count - k + 1);
                if (worst >= best.Worst)
                {
                    continue;
                }

                List<Vec3> first = Run(poly, a, b), second = Run(poly, b, a);
                if (Area(first, n) > Geometry.ShapeAnalysis.ZeroArea && Area(second, n) > Geometry.ShapeAnalysis.ZeroArea
                    && !Straight(first) && !Straight(second))
                {
                    best = (a, b, worst);
                }
            }
        }

        if (best.A < 0)
        {
            return [poly];
        }

        var parts = new List<List<Vec3>>();
        parts.AddRange(Limit(Run(poly, best.A, best.B), most));
        parts.AddRange(Limit(Run(poly, best.B, best.A), most));
        return parts;
    }

    /// <summary>The corners from <paramref name="from"/> round to <paramref name="to"/>, both included.</summary>
    private static List<Vec3> Run(List<Vec3> poly, int from, int to)
    {
        var o = new List<Vec3>();
        for (int i = from; ; i = (i + 1) % poly.Count)
        {
            o.Add(poly[i]);
            if (i == to)
            {
                return o;
            }
        }
    }

    /// <summary>True when the part's closing edge (the cut) runs along one of the polygon's own edges: the corners next
    /// to the cut's ends lie on its line.</summary>
    private static bool Straight(List<Vec3> part)
    {
        Vec3 a = part[0], b = part[^1], d = b - a;
        double len = d.Length;
        if (len < 1e-9)
        {
            return true;
        }

        return Vec3.Cross(part[1] - a, d).Length <= 1e-6 * len * (part[1] - a).Length
            || Vec3.Cross(part[^2] - b, d).Length <= 1e-6 * len * (part[^2] - b).Length;
    }
}
