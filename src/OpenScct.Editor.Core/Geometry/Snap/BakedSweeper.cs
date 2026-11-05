using OpenScct.Core;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;
using OpenScct.Physics.Collision;

namespace OpenScct.Editor.Core.Geometry.Snap;

/// <summary>
/// An imported map's baked collision (OpenSCCT's <see cref="CollisionWorld"/>) as a neighbour snap target. The BSP is
/// swept as a box. Static meshes and movers are not: the game sweeps a vertical cylinder against them (radius
/// Extent.X), which reaches the wrong distance along Y, so they are found by rays from points on the box's leading
/// face (exact contacts). Every query holds <see cref="WorldGeometry.BakedQueryLock"/>, as the editor's own rays on
/// the shared collision world do (a Map Check can be reading it on a worker while a drag sticks).
/// </summary>
public sealed class BakedSweeper(CollisionWorld world) : ISnapSweeper
{
    /// <summary>The rays start this far behind the leading face, so a surface lying on it is still hit.</summary>
    private const double Back = 0.5;

    /// <summary>Rays per side of the leading face (3 × 3: corners, edge middles and centre).</summary>
    private const int RaysPerSide = 3;

    private const ETraceFlags Bsp = ETraceFlags.TRACE_Level;
    private const ETraceFlags Actors = ETraceFlags.TRACE_Movers | ETraceFlags.TRACE_LevelGeometry;

    public SweepHit? Sweep(Vec3 from, Vec3 to, Vec3 extent)
    {
        lock (WorldGeometry.BakedQueryLock)
        {
            SweepHit? best = null;
            if (world.LineCheck(out FCheckResult hit, null, F(to), F(from), F(extent), Bsp) && !hit.bStartPenetrating)
            {
                best = new SweepHit(hit.Time, V(hit.Normal), V(hit.Location));
            }

            return Rays(from, to, extent, best);
        }
    }

    public bool Overlaps(Vec3 at, Vec3 extent)
    {
        // Only used to refine a BSP box sweep's stop (mesh contacts from the rays are exact already).
        lock (WorldGeometry.BakedQueryLock)
        {
            return world.PointCheck(out _, null, F(at), F(extent), Bsp);
        }
    }

    /// <summary>The nearer of <paramref name="best"/> and the first mesh or mover hit by rays from the box's leading
    /// face along an axis sweep.</summary>
    private SweepHit? Rays(Vec3 from, Vec3 to, Vec3 extent, SweepHit? best)
    {
        Vec3 d = to - from;
        double len = d.Length;
        int a = Math.Abs(d.X) >= Math.Abs(d.Y) ? (Math.Abs(d.X) >= Math.Abs(d.Z) ? 0 : 2) : Math.Abs(d.Y) >= Math.Abs(d.Z) ? 1 : 2;
        if (len < 1e-9 || Math.Abs(d[a]) < len * 0.999)
        {
            return best;
        }

        int s = Math.Sign(d[a]);
        int u = (a + 1) % 3, v = (a + 2) % 3;
        Vec3 dir = d / len;
        double reach = len + Back;
        for (int i = 0; i < RaysPerSide; i++)
        {
            for (int j = 0; j < RaysPerSide; j++)
            {
                double fu = RaysPerSide == 1 ? 0 : -1 + 2.0 * i / (RaysPerSide - 1);
                double fv = RaysPerSide == 1 ? 0 : -1 + 2.0 * j / (RaysPerSide - 1);
                Vec3 start = from.With(a, from[a] + s * (extent[a] - Back)).With(u, from[u] + fu * extent[u]).With(v, from[v] + fv * extent[v]);
                Vec3 end = start + dir * reach;
                if (!world.LineCheck(out FCheckResult hit, null, F(end), F(start), default, Actors) || hit.bStartPenetrating)
                {
                    continue;
                }

                // How far the box moves before its leading face reaches the hit; a surface already inside the box is
                // not in its way.
                double moved = hit.Time * reach - Back;
                if (moved < -1e-6)
                {
                    continue;
                }

                double t = Math.Max(moved, 0) / len;
                if (best is not { } b || t < b.Time)
                {
                    best = new SweepHit(t, V(hit.Normal), from + dir * (t * len), Exact: true);
                }
            }
        }

        return best;
    }

    private static FVector F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static Vec3 V(FVector v) => new(v.X, v.Y, v.Z);
}
