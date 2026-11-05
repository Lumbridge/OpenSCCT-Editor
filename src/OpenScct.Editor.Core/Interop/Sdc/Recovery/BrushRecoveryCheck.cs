using System.Diagnostics;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc.Recovery;

/// <summary>How a set of brushes built by the editor's compiler compares with a compiled map's BSP: probes beside every
/// original surface, a grid of points through the map (solid or empty in both), and the drawn area per material (of
/// the original's polygons with empty space in front: the BSP also keeps polygons inside solid space, which no one
/// sees).
/// <para><see cref="Covered"/>, <see cref="WrongMaterial"/> and <see cref="Uncovered"/> are shares of the original's
/// drawn area (sampled at points of each polygon pulled a little towards its centre, where empty space is in front): drawn again with the same
/// material, drawn with another, not drawn.</para></summary>
public sealed record RebuildComparison(int Brushes, int Probes, int Misses, int Samples, int SamplesDiffer, double CookedArea, double RebuiltArea,
    double MaterialAreaOff, double Covered, double WrongMaterial, double Uncovered, int Leaves, double Seconds);

public static class BrushRecoveryCheck
{
    /// <summary>Builds <paramref name="brushes"/> (in CSG order) and compares the result with <paramref name="bsp"/>.
    /// <paramref name="grid"/> points per axis are sampled through the bounds of the model's points.</summary>
    public static RebuildComparison Compare(CookedBsp bsp, IReadOnlyList<Brush> brushes, int grid = 48)
    {
        var clock = Stopwatch.StartNew();
        WorldModel w = WorldModel.Build(brushes, [], worldSolid: !bsp.RootOutside, numbering: null);
        double seconds = clock.Elapsed.TotalSeconds;
        List<BrushRecovery.Probe> probes = BrushRecovery.Probes(bsp);
        int misses = probes.Count(p => w.Tree.IsEmpty(p.Point) != p.Outside);

        int samples = 0, differ = 0;
        Aabb box = bsp.PointBounds;
        if (!box.IsEmpty && grid > 0)
        {
            Vec3 size = box.Max - box.Min;
            for (int i = 0; i < grid; i++)
            {
                for (int j = 0; j < grid; j++)
                {
                    for (int k = 0; k < grid; k++)
                    {
                        // Off the half-unit lattice maps are built on, so no sample sits on a wall.
                        var p = new Vec3(box.Min.X + size.X * (i + 0.5) / grid + 0.137, box.Min.Y + size.Y * (j + 0.5) / grid + 0.291,
                            box.Min.Z + size.Z * (k + 0.5) / grid + 0.413);
                        samples++;
                        if (w.Tree.IsEmpty(p) != bsp.IsOutside(p))
                        {
                            differ++;
                        }
                    }
                }
            }
        }

        var cooked = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (CookedFace f in bsp.Faces)
        {
            CookedSurf s = bsp.Surfs[f.Surf];
            // Polygons the BSP left inside solid space are never seen.
            if ((s.Flags & (PolyFlags.Invisible | CookedBsp.PortalFlag)) != 0 || !bsp.IsOutside(f.Centre() + f.Normal * BrushRecovery.ProbeOffset))
            {
                continue;
            }

            string m = s.Material ?? "";
            cooked[m] = cooked.GetValueOrDefault(m) + Math.Abs(PolygonOps.Area(f.Points, f.Normal));
        }

        // Each drawn original polygon sampled at its centre and at its corners pulled a quarter of the way in.
        var byPlane = new Dictionary<(long, long, long, long), List<Poly>>();
        foreach (Poly p in w.Visible)
        {
            (long, long, long, long) key = Key(p.Plane.N, p.Plane.D);
            if (!byPlane.TryGetValue(key, out List<Poly>? list))
            {
                byPlane[key] = list = [];
            }

            list.Add(p);
        }

        double same = 0, other = 0, none = 0;
        foreach (CookedFace f in bsp.Faces)
        {
            CookedSurf s = bsp.Surfs[f.Surf];
            Vec3 c = f.Centre();
            if ((s.Flags & (PolyFlags.Invisible | CookedBsp.PortalFlag)) != 0 || !bsp.IsOutside(c + f.Normal * BrushRecovery.ProbeOffset))
            {
                continue;
            }

            double weight = Math.Abs(PolygonOps.Area(f.Points, f.Normal)) / (f.Points.Count + 1);
            foreach (Vec3 q in f.Points.Select(v => v + (c - v) * 0.25).Append(c))
            {
                // Part of a polygon can face into solid space (the BSP does not cut every polygon along every wall).
                if (!bsp.IsOutside(q + f.Normal * BrushRecovery.ProbeOffset))
                {
                    continue;
                }

                Poly? hit = Hit(byPlane, q, f.Normal);
                if (hit is null)
                {
                    none += weight;
                }
                else if (string.Equals(hit.Surface.Material, s.Material, StringComparison.OrdinalIgnoreCase))
                {
                    same += weight;
                }
                else
                {
                    other += weight;
                }
            }
        }

        double sampled = same + other + none;
        var rebuilt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (Poly p in w.Visible)
        {
            string m = p.Surface.Material ?? "";
            rebuilt[m] = rebuilt.GetValueOrDefault(m) + p.Area();
        }

        double cookedTotal = cooked.Values.Sum(), rebuiltTotal = rebuilt.Values.Sum();
        double off = cooked.Keys.Union(rebuilt.Keys, StringComparer.OrdinalIgnoreCase)
            .Sum(m => Math.Abs(cooked.GetValueOrDefault(m) - rebuilt.GetValueOrDefault(m)));
        return new RebuildComparison(brushes.Count, probes.Count, misses, samples, differ, cookedTotal, rebuiltTotal,
            cookedTotal > 0 ? off / cookedTotal : 0, sampled > 0 ? same / sampled : 1, sampled > 0 ? other / sampled : 0, sampled > 0 ? none / sampled : 0,
            w.Tree.Leaves.Count, seconds);
    }

    private static (long, long, long, long) Key(Vec3 n, double d) =>
        ((long)Math.Round(n.X * 100), (long)Math.Round(n.Y * 100), (long)Math.Round(n.Z * 100), (long)Math.Floor(d));

    /// <summary>A drawn polygon facing along <paramref name="n"/> whose plane passes within 0.3 units of the point and
    /// whose outline holds it.</summary>
    private static Poly? Hit(Dictionary<(long, long, long, long), List<Poly>> byPlane, Vec3 q, Vec3 n)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    var nk = ((long)Math.Round(n.X * 100) + dx, (long)Math.Round(n.Y * 100) + dy, (long)Math.Round(n.Z * 100) + dz);
                    long dk = (long)Math.Floor(Vec3.Dot(n, q));
                    for (long dd = -1; dd <= 1; dd++)
                    {
                        if (!byPlane.TryGetValue((nk.Item1, nk.Item2, nk.Item3, dk + dd), out List<Poly>? list))
                        {
                            continue;
                        }

                        foreach (Poly p in list)
                        {
                            if (Vec3.Dot(p.Plane.N, n) >= 0.999 && Math.Abs(p.Plane.Dist(q)) <= 0.3 && Inside(p, q))
                            {
                                return p;
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    private static bool Inside(Poly p, Vec3 q)
    {
        Vec3[] v = p.Verts;
        for (int i = 0; i < v.Length; i++)
        {
            if (Vec3.Dot(Vec3.Cross(v[(i + 1) % v.Length] - v[i], q - v[i]), p.Plane.N) < -1e-6)
            {
                return false;
            }
        }

        return true;
    }
}
