using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    private static readonly string[] s_stickKeys = ["within", "axes", "targets"];
    private static readonly string[] s_stickTargets = ["brushes", "meshes", "actors", "baked"];

    /// <summary>
    /// move's <c>stick</c>: after the move and its placement, pulls the moved objects (together, as one box) onto the
    /// nearest neighbour faces within <c>within</c> units (default 16) on <c>axes</c> (default: the axes the move
    /// changed, all three for <c>to</c>/<c>at</c>), touching them or in line with them, against <c>targets</c>
    /// (brushes, meshes and baked by default; actors too when named). The editor's Neighbour Snap solver, with units
    /// for pixels. Notes what each axis stuck to. <paramref name="start"/> is the objects' box before the move: their
    /// own baked copies are there, so the baked world does not pull them back onto them.
    /// </summary>
    private static void Stick(ChangeContext ctx, SvJsonNode node, List<ObjectRef> targets, bool[] moved, (Vec3 Min, Vec3 Max)? start)
    {
        double within = 16;
        string? axesText = null;
        var kinds = new HashSet<string>(["brushes", "meshes", "baked"], StringComparer.OrdinalIgnoreCase);
        switch (node)
        {
            case { Kind: SvJsonKind.False }:
                return;
            case { Kind: SvJsonKind.True }:
                break;
            case SvJsonObject o:
                if (o.Select(kv => kv.Key).FirstOrDefault(k => !s_stickKeys.Contains(k, StringComparer.Ordinal)) is { } unknown)
                {
                    throw new ApiException($"stick does not take '{unknown}' (it takes {string.Join(", ", s_stickKeys)})", "stick");
                }

                within = ApiJson.Number(o, "within", 16);
                if (within <= 0)
                {
                    throw new ApiException("stick.within is a distance in units, more than 0", "stick");
                }

                axesText = ApiJson.GetString(o, "axes");
                if (ApiJson.StringList(o["targets"], "stick.targets") is { } list)
                {
                    if (list.FirstOrDefault(t => !s_stickTargets.Contains(t, StringComparer.OrdinalIgnoreCase)) is { } bad)
                    {
                        throw new ApiException($"stick.targets: '{bad}' is not one of {string.Join(", ", s_stickTargets)}", "stick");
                    }

                    kinds = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                }

                break;
            default:
                throw new ApiException("stick is true or { within, axes, targets }", "stick");
        }

        int[] free = axesText is null ? [.. Enumerable.Range(0, 3).Where(a => moved[a])] : StickAxes(axesText);
        string label = targets.Count == 1 ? targets[0].Id : $"{targets.Count} objects";
        if (free.Length == 0)
        {
            ctx.Warn("stick", $"{label}: nothing to stick, the move changed no axis (give stick.axes)");
            return;
        }

        MapProject p = ctx.Project;
        SnapBoxes boxes = StickBoxes(ctx);
        if (StickShape(p, targets, boxes) is not { } shape)
        {
            ctx.Warn("stick", $"{label}: has no faces to stick (GEs, or an actor with no collision size)");
            return;
        }

        var ids = new HashSet<string>(targets.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        bool brushes = kinds.Contains("brushes"), meshes = kinds.Contains("meshes"), actors = kinds.Contains("actors");
        ISnapSweeper? baked = kinds.Contains("baked") && ctx.Geometry().BakedWorld is { } world ? new BakedSweeper(world) : null;
        SnapNeighbours n = SnapNeighbours.Build(p,
            b => brushes && !ids.Contains(b.Id) && !b.Editor.Hidden && !b.IsNotSolid && !b.IsPortal && (b.PolyFlags & PolyFlags.AntiPortal) == 0,
            null,
            p.Actors.Where(a => (meshes || actors) && a.Shape is null && !ids.Contains(a.Id) && !a.Editor.Hidden),
            a => boxes.Of(a) is { } box && (box.Source == SnapSource.Mesh ? meshes : actors) ? box : null,
            baked);
        MoveSnap snap = NeighbourSnap.Move(shape, Vec3.Zero, free, n, SnapView.World(within), null, own: start ?? (shape.Min, shape.Max));
        if (!snap.Any)
        {
            ctx.Warn("stick", $"{label}: nothing within {NeighbourSnap.Number(within)}");
            return;
        }

        Vec3 delta = Vec3.Zero;
        for (int a = 0; a < 3; a++)
        {
            if (snap.Stuck(a))
            {
                delta = delta.With(a, snap.Offset[a]);
            }
        }

        ctx.Edit(targets, $"Stick {label}",
            brush: x => x.Location += delta,
            actor: x => x.Location += delta,
            ge: x => x.Points = x.Points.Select(pt => pt + delta).ToList());
        foreach (SnapMatch m in snap.Matches)
        {
            bool rough = m.Target.Approximate || m.Moving?.Approximate == true;
            ctx.Warn("stick", $"{label} {NeighbourSnap.Describe(m)}{(rough ? ", approximate box" : "")}");
        }
    }

    /// <summary>The objects' box before the move (for <see cref="Stick"/>), or null when they have none.</summary>
    private static (Vec3 Min, Vec3 Max)? StickStart(ChangeContext ctx, List<ObjectRef> targets) =>
        StickShape(ctx.Project, targets, StickBoxes(ctx)) is { } shape ? (shape.Min, shape.Max) : null;

    private static SnapBoxes StickBoxes(ChangeContext ctx) =>
        new(new ActorRules(ClassDefaults.Create(EditorServices.ContentRoot, EditorServices.Classes)),
            AssetCatalog.For(EditorServices.ContentRoot, ctx.Folder));

    private static int[] StickAxes(string text)
    {
        var axes = new SortedSet<int>();
        foreach (char c in text.ToLowerInvariant())
        {
            axes.Add(c switch
            {
                'x' => 0,
                'y' => 1,
                'z' => 2,
                _ => throw new ApiException($"stick.axes is letters from xyz, not '{text}'", "stick"),
            });
        }

        return [.. axes];
    }

    /// <summary>The moved objects as one shape: one brush or shaped actor by its faces, one actor by its box, several by
    /// their combined bounds. Null when nothing has faces.</summary>
    private static MovingShape? StickShape(MapProject p, List<ObjectRef> targets, SnapBoxes boxes)
    {
        List<ObjectRef> solid = targets.Where(r => r.Kind != ObjectKind.Ge).ToList();
        if (solid.Count == 1)
        {
            ObjectRef r = solid[0];
            if (r.Kind is (ObjectKind.Brush or ObjectKind.Stair) && p.FindBrush(r.Id) is { } b)
            {
                return SnapFaces.Shape(SnapFaces.OfBrush(b), r);
            }

            if (p.FindActor(r.Id) is { } a)
            {
                if (a.Shape is not null)
                {
                    return SnapFaces.Shape(SnapFaces.OfShape(a), r);
                }

                if (boxes.Of(a) is { } box)
                {
                    (Vec3 min, Vec3 max) = box.Box.Bounds();
                    return new MovingShape(min, max, box.Box.Faces(r, box.Source, box.Approximate), []);
                }
            }

            return null;
        }

        var lo = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var hi = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (ObjectRef r in solid)
        {
            (Vec3 Min, Vec3 Max)? b = r.Kind is (ObjectKind.Brush or ObjectKind.Stair) && p.FindBrush(r.Id) is { } brush ? MapObjects.BrushBounds(brush)
                : p.FindActor(r.Id) is { } actor ? (actor.Shape is null && boxes.Of(actor) is { } box ? box.Box.Bounds() : MapObjects.ActorBounds(actor))
                : null;
            if (b is { } bb)
            {
                lo = Vec3.Min(lo, bb.Min);
                hi = Vec3.Max(hi, bb.Max);
            }
        }

        return lo.X > hi.X ? null : MovingShape.Box(lo, hi);
    }
}
