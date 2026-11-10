using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>Settings of the ledge detector and the ledge checks. Units are Unreal units.</summary>
public sealed record LedgeOptions
{
    /// <summary>Distance between probes along an edge.</summary>
    public double Step { get; init; } = 8;

    /// <summary>A lip needs at least this much drop on its hang side (lower ones are steps the walk climbs).</summary>
    public double MinDrop { get; init; } = 40;

    /// <summary>Shorter lips are dropped unless they join another at a corner.</summary>
    public double MinLength { get; init; } = 32;

    /// <summary>Ledge ends this close to another's end are joined at the corner (the engine's shimmy round a corner
    /// needs the ends within 2 on every axis).</summary>
    public double JoinDistance { get; init; } = 40;

    /// <summary>Room a climber needs on top (the spy's crouch height is 100). 0, the default, also accepts lips one can
    /// only hang and shimmy on: the engine's grab needs no room on top, and many stock ledges have none.</summary>
    public double ClimbHeadroom { get; init; }

    /// <summary>Most rise per unit of length for an edge to count as level.</summary>
    public double MaxSlope { get; init; } = 0.02;

    /// <summary>Also look for lips on static meshes (props placed in the map or baked into it), not only on its architecture.</summary>
    public bool IncludeMeshes { get; init; }

    /// <summary>Only look inside this box (null: the whole map).</summary>
    public (Vec3 Min, Vec3 Max)? Region { get; init; }
}

/// <summary>Why a point on an edge is not a grabbable lip.</summary>
public enum LipProblem
{
    None,

    /// <summary>No surface just inside the edge (not a top).</summary>
    NoTop,

    /// <summary>A wall rises right at the edge.</summary>
    Wall,

    /// <summary>The drop beside it is lower than <see cref="LedgeOptions.MinDrop"/> (a step, not a ledge).</summary>
    Step,

    /// <summary>No room on top to climb up.</summary>
    NoClimbRoom,

    /// <summary>The engine's hang clearance box (35, 35, 87.5) does not fit below the lip.</summary>
    NoHang,
}

/// <summary>
/// Point tests of a ledge lip against the level geometry, with the engine's numbers: the hang spot 39 out and 117
/// below the lip, a (35, 35, 87.5) box there and 50 higher (CheckLedgeArms), and room on top for a crouched spy.
/// </summary>
public static class LedgeProbe
{
    /// <summary>Hang spot offsets from the lip (m_LedgeGrabArmsZone X and Z).</summary>
    public const double HangOut = 39, HangDown = 117, HangRaise = 50;

    /// <summary>Drop below which the hang box cannot fit, so only the waist and hop-up grabs apply.</summary>
    public const double HangDepth = HangDown + Limits.StandHalfHeight + 2;

    private static readonly Vec3 HangExtent = new(Limits.BodyRadius, Limits.BodyRadius, Limits.StandHalfHeight);

    /// <summary>Tests the lip point <paramref name="c"/> whose hang side is along the horizontal unit <paramref name="out"/>.
    /// Returns the problem (or None) and the drop to the floor on the hang side.</summary>
    public static (LipProblem Problem, double Drop) Evaluate(TraversalWorld w, Vec3 c, Vec3 @out, LedgeOptions o)
    {
        Vec3 up = Vec3.UnitZ;
        if (w.Trace(c - @out * 4 + up * 4, c - @out * 4 - up * 6) is null)
        {
            return (LipProblem.NoTop, 0);
        }

        if (w.Trace(c - @out * 2 + up * 6, c + @out * 10 + up * 6) is not null)
        {
            return (LipProblem.Wall, 0);
        }

        double drop = (w.DropBelow(c + @out * 6 + up, 8192) ?? 8192) - 1;
        if (drop < o.MinDrop)
        {
            return (LipProblem.Step, drop);
        }

        if (o.ClimbHeadroom > 0)
        {
            double half = o.ClimbHeadroom / 2;
            Vec3 top = c - @out * (Limits.BodyRadius + 2) + up * (half + 2);
            if (w.BoxOverlaps(top, new Vec3(Limits.BodyRadius - 1, Limits.BodyRadius - 1, half)))
            {
                return (LipProblem.NoClimbRoom, drop);
            }
        }

        if (drop >= HangDepth)
        {
            Vec3 hang = HangSpot(c, @out);
            if (w.BoxOverlaps(hang, HangExtent) || w.BoxOverlaps(hang + up * HangRaise, HangExtent))
            {
                return (LipProblem.NoHang, drop);
            }
        }

        return (LipProblem.None, drop);
    }

    /// <summary>Where a spy hangs from the lip point (CheckLedgeArms: C.XY + 39 N, C.Z - 117).</summary>
    public static Vec3 HangSpot(Vec3 c, Vec3 @out) => c + @out * HangOut - Vec3.UnitZ * HangDown;

    /// <summary>Plain words for a problem.</summary>
    public static string Describe(LipProblem p, double drop) => p switch
    {
        LipProblem.NoTop => "no top surface at the edge",
        LipProblem.Wall => "a wall rises at the edge",
        LipProblem.Step => $"only {drop:0} drop beside it (a step)",
        LipProblem.NoClimbRoom => "no room on top to climb up",
        LipProblem.NoHang => "no room to hang below it",
        _ => "grabbable",
    };

    /// <summary>
    /// Finds the real lip next to a drawn ledge line: scans across the line at its middle for the change from a top
    /// surface to a drop, within <paramref name="reach"/> either side and the same height. Returns the offset to move
    /// the line by (across, in XY), the top height, and the hang side; null when no edge is that close.
    /// </summary>
    public static LipFit? Fit(TraversalWorld w, Vec3 a, Vec3 b, LedgeOptions o, double reach = 32)
    {
        Vec3 d = new Vec3(b.X - a.X, b.Y - a.Y, 0).Normalized();
        if (d == Vec3.Zero)
        {
            return null;
        }

        var n = new Vec3(d.Y, -d.X, 0);
        Vec3 mid = (a + b) / 2;
        double z = mid.Z;
        const double Step = 2;
        int count = (int)(reach / Step);
        var tops = new double?[count * 2 + 1];
        for (int i = -count; i <= count; i++)
        {
            tops[i + count] = TopAt(w, mid + n * (i * Step), z, reach);
        }

        LipFit? best = null;
        for (int i = -count; i < count; i++)
        {
            double? t0 = tops[i + count], t1 = tops[i + count + 1];
            bool top0 = t0 is { } h0 && Math.Abs(h0 - z) <= reach, top1 = t1 is { } h1 && Math.Abs(h1 - z) <= reach;
            if (top0 == top1)
            {
                continue;
            }

            double topZ = (top0 ? t0 : t1)!.Value;
            double other = top0 ? t1 ?? double.NegativeInfinity : t0 ?? double.NegativeInfinity;
            if (topZ - other < o.MinDrop)
            {
                continue;
            }

            // The hang side points from the top towards the drop.
            Vec3 side = top0 ? n : -n;
            double lo = i * Step, hi = (i + 1) * Step;
            for (int k = 0; k < 10; k++)
            {
                double m = (lo + hi) / 2;
                bool isTop = TopAt(w, mid + n * m, z, reach) is { } hm && Math.Abs(hm - topZ) <= 2;
                if (isTop == top0)
                {
                    lo = m;
                }
                else
                {
                    hi = m;
                }
            }

            double across = (lo + hi) / 2;
            if (best is null || Math.Abs(across) < Math.Abs(best.Across))
            {
                best = new LipFit(n * across, across, topZ, side);
            }
        }

        return best;
    }

    /// <summary>The height of the surface under a point, searching from <paramref name="reach"/> above its height.</summary>
    private static double? TopAt(TraversalWorld w, Vec3 p, double z, double reach) =>
        w.Trace(new Vec3(p.X, p.Y, z + reach), new Vec3(p.X, p.Y, z - reach - 8)) is { } hit ? hit.Point.Z : null;
}

/// <summary>Where a drawn ledge's real lip is: move by <see cref="Offset"/>, set the height to <see cref="TopZ"/>.</summary>
public sealed record LipFit(Vec3 Offset, double Across, double TopZ, Vec3 HangSide);
