using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    private static string[] StoreyDuplicateKeys => ["storey", "by", "below"];

    /// <summary>
    /// storey.duplicate: copies everything on a storey (the middle of its bounds inside the storey's range) to a new
    /// storey on top of the highest one, under the lowest one (<c>below</c>), or <c>by</c> units up. <c>storey</c> is
    /// a name or a number (1 for the lowest); without it, the storey the views show. A storey's height is its range,
    /// or for the open top of the highest detected storey what stands on it. When the map has storeys of its own, the
    /// copy's range is added to them. Copied player starts and an imported map's baked world (which is not copied)
    /// come back as warnings.
    /// </summary>
    private static void DuplicateStorey(ChangeContext ctx, SvJsonObject op)
    {
        MapProject p = ctx.Project;
        IReadOnlyList<Storey> storeys = Geometry.Storeys.Of(p, ctx.Folder);
        int index = ApiJson.GetString(op, "storey") is { } spec
            ? FindStorey(storeys, spec, "storey")
            : Geometry.Storeys.Shown(storeys, p.Workspace.ShownStorey);
        if (index < 0)
        {
            throw new ApiException("name the storey to copy (no storey is shown)", "storey");
        }

        Storey storey = storeys[index];
        List<ObjectRef> targets = p.AllRefs().Where(r =>
        {
            (Vec3 min, Vec3 max) = MapObjects.Bounds(p, r);
            return storey.Contains((min.Z + max.Z) / 2);
        }).ToList();
        if (targets.Count == 0)
        {
            throw new ApiException($"nothing is on {storey.Name}", "storey");
        }

        bool below = ApiJson.GetBool(op, "below", false);
        double by;
        if (op["by"] is { } b)
        {
            if (below)
            {
                throw new ApiException("give by or below, not both", "below");
            }

            by = ApiJson.RequireNumber(b, "by");
        }
        else if (below)
        {
            double height = Geometry.Storeys.StackHeight(storeys, index, Geometry.Storeys.ContentTop(p, storey));
            by = storeys[0].Bottom - height - storey.Bottom;
        }
        else
        {
            int top = storeys.Count - 1;
            by = storeys[top].Bottom + Geometry.Storeys.StackHeight(storeys, top, Geometry.Storeys.ContentTop(p, storeys[top])) - storey.Bottom;
        }

        if (Math.Abs(by) < 1)
        {
            throw new ApiException("by must move the copy at least a unit", "by");
        }

        int starts = targets.Count(r => r.Kind == ObjectKind.Actor && p.FindActor(r.Id) is { } a && MapObjects.ClassMatches(a.Class, "PlayerStart", subclasses: true));
        if (starts > 0)
        {
            ctx.Warn("storey", $"the copy has {starts} more player start{(starts == 1 ? "" : "s")}; delete them if players should not start on it");
        }

        if (BakedContent.BakedWorldKept(p))
        {
            ctx.Warn("storey", "the imported map's baked world is not copied, only the objects on the storey");
        }

        CopyObjects(ctx, targets, new Vec3(0, 0, by), 1);
        if (ctx.Project.Workspace.Storeys.Count > 0)
        {
            var range = new StoreyRange
            {
                Name = Geometry.Storeys.NewName(ctx.Project.Workspace.Storeys.Select(s => s.Name).ToList()),
                Bottom = storey.Bottom + by,
                Top = storey.Top + by,
            };
            ctx.SnapshotProject();
            ctx.Run(WorkspaceEdits.Edit("Add storey", w => w.Storeys.Add(range.Clone())), touchesBrushes: false);
        }
    }

    /// <summary>The storey a spec names (see <see cref="Geometry.Storeys.Find"/>), as an API error when it names none.</summary>
    private static int FindStorey(IReadOnlyList<Storey> storeys, string spec, string field)
    {
        try
        {
            return Geometry.Storeys.Find(storeys, spec);
        }
        catch (ArgumentException e)
        {
            throw new ApiException(e.Message, field);
        }
    }
}
