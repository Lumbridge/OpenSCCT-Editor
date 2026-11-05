using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>
/// GE marks in each kind's colour: the 24.5 hand margins at ledge ends (35 on hand-over-hand pipes), the downhill
/// arrow of zip lines, the climber side of ladders and pipes (with rungs), the way up a pole, GE IDs, the problems
/// of selected GEs, and the ledge detector's proposals (dashed).
/// </summary>
public sealed class GeOverlay : IViewOverlay
{
    private const double Tick = 8;

    public string Id => "ge";

    public string Title => "GE Directions";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        foreach (Ge g in c.Project.Ges)
        {
            if (g.Points.Count < 2 || !c.IsVisible(g.Editor))
            {
                continue;
            }

            Color col = WorldScene.GeColor(g.Kind);
            switch (g.Kind)
            {
                case GeKinds.LedgeGrab:
                    Margins(batch, g.Points[0], g.Points[1], 24.5, col);
                    break;
                case GeKinds.HandOverHand:
                    Margins(batch, g.Points[0], g.Points[1], 35, col);
                    break;
                case GeKinds.ZipLine:
                    ZipArrow(batch, g.Points[0], g.Points[1], col);
                    break;
                case GeKinds.Ladder or GeKinds.Pipe when g.Points.Count >= 3:
                    Climb(batch, g, col);
                    break;
                case GeKinds.Pole:
                    (Vec3 top, Vec3 bottom) = g.Points[0].Z >= g.Points[1].Z ? (g.Points[0], g.Points[1]) : (g.Points[1], g.Points[0]);
                    batch.Arrow(Vec3.Lerp(bottom, top, 0.8), top, col, 2);
                    break;
                case GeKinds.Fence:
                    Margins(batch, g.Points[0], g.Points[1], 0, col);
                    break;
            }

            Vec3 mid = (g.Points[0] + g.Points[1]) / 2;
            if (g.Mask != 0)
            {
                batch.Label(mid, $"ID {g.Mask}", col, c.IsSelected(ObjectRef.Ge(g.Id)));
            }

            if (c.IsSelected(ObjectRef.Ge(g.Id)))
            {
                foreach (GeProblem p in GeShapes.Problems(g.Kind, g.Points))
                {
                    batch.Label(mid + Vec3.UnitZ * 12, p.Message, EditorTheme.Warning, pinned: true);
                }
            }
        }

        Color proposal = WorldScene.GeColor(GeKinds.LedgeGrab).Lightened(0.3f);
        foreach (Lip lip in GameplayState.Instance.Proposals)
        {
            batch.Line(lip.A, lip.B, proposal, 2, dashed: true);
            batch.Line(lip.Mid, lip.Mid + lip.Out * 16, proposal, 1);
        }
    }

    /// <summary>Short marks across the line where the hands can reach no further (<paramref name="margin"/> from each end).</summary>
    private static void Margins(OverlayBatch batch, Vec3 a, Vec3 b, double margin, Color c)
    {
        Vec3 d = new Vec3(b.X - a.X, b.Y - a.Y, 0);
        double length = d.Length;
        if (length < 1)
        {
            return;
        }

        d /= length;
        var n = new Vec3(d.Y, -d.X, 0);
        double[] at = margin > 0 && length > margin * 2 ? [margin, length - margin] : [length / 2];
        foreach (double t in at)
        {
            Vec3 p = Vec3.Lerp(a, b, t / length);
            batch.Line(p - n * Tick, p + n * Tick, c, 1.5f);
        }
    }

    private static void ZipArrow(OverlayBatch batch, Vec3 a, Vec3 b, Color c)
    {
        (Vec3 high, Vec3 low) = a.Z >= b.Z ? (a, b) : (b, a);
        Vec3 mid = Vec3.Lerp(high, low, 0.5);
        batch.Arrow(Vec3.Lerp(high, low, 0.35), mid, c, 2);
        batch.Arrow(Vec3.Lerp(high, low, 0.85), low, c, 2);
    }

    /// <summary>Rungs every 32 up a ladder (a pipe gets none) and arrows from the line toward the climber.</summary>
    private static void Climb(OverlayBatch batch, Ge g, Color c)
    {
        Vec3 bottom = g.Points[0], top = g.Points[1];
        Vec3 n = new Vec3(g.Points[2].X - bottom.X, g.Points[2].Y - bottom.Y, 0).Normalized();
        if (n == Vec3.Zero)
        {
            return;
        }

        var side = new Vec3(-n.Y, n.X, 0);
        double height = top.Z - bottom.Z;
        if (g.Kind == GeKinds.Ladder)
        {
            for (double z = 16; z < height; z += 32)
            {
                Vec3 p = bottom + Vec3.UnitZ * z;
                batch.Line(p - side * 10, p + side * 10, c.Darkened(0.2f), 1);
            }
        }

        batch.Arrow(bottom, bottom + n * 20, c, 1.5f);
        batch.Arrow(top, top + n * 20, c, 1.5f);
    }
}
