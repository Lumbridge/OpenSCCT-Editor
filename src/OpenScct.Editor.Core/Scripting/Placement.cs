using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>Where placement put an object, plus the wall it was snapped to (so the caller can face away from it).</summary>
internal readonly record struct Placed(Vec3 Location, Vec3? WallNormal);

/// <summary>
/// Placement by reference, so a script never has to know a floor's z:
/// <list type="bullet">
/// <item><c>at</c>: a point (<c>[x,y,z]</c> or <c>"Name@anchor+dx,dy,dz"</c>); <c>offset</c>: added to it.</item>
/// <item><c>near</c>: a free spot <c>dist</c> (96) from an object or point, in open space and in sight of it.</item>
/// <item><c>pivot</c>: which point of the new object goes to the target (an anchor such as <c>xmin_bottom</c>).</item>
/// <item><c>wall</c>: <c>"nearest"</c>/<c>true</c>, <c>"+x"</c>, <c>"-y"</c>... or a direction: cast from the point and
/// put the object against the first wall (its back on the wall, <c>gap</c> off it, facing out).</item>
/// <item><c>on</c>: <c>true</c>/<c>"floor"</c> (drop from the point), an object id (into a room, onto a solid brush or
/// onto an actor's collision top) or a point: casts down to the first floor and stands the object's bottom on it,
/// <c>lift</c> above.</item>
/// </list>
/// Order: at/near + offset, pivot, wall, on.
/// </summary>
internal static class Placement
{
    public static readonly string[] Keys = ["at", "offset", "near", "dist", "pivot", "wall", "gap", "reach", "on", "lift"];

    public static bool Requested(SvJsonObject op) => Keys.Any(k => op[k] is not null && k is not ("dist" or "gap" or "reach"));

    /// <param name="defaultBase">Where the object goes when the op gives no point (its current place, or the origin).</param>
    /// <param name="extent">The object's bounds relative to its location.</param>
    /// <param name="exclude">A brush to leave out of the geometry (the one being moved).</param>
    public static Placed Resolve(ChangeContext ctx, SvJsonObject op, Vec3 defaultBase, (Vec3 Min, Vec3 Max) extent, string? exclude)
    {
        Vec3 loc = op["at"] is { } at ? ctx.Point(at, "at") : defaultBase;
        if (op["near"] is { } near)
        {
            loc = Near(ctx, ctx.Point(near, "near"), ApiJson.Number(op, "dist", 96), exclude);
        }

        if (op["offset"] is { } off)
        {
            loc += ApiJson.RequireVec(off, "offset");
        }

        if (ApiJson.GetString(op, "pivot") is { } pivot)
        {
            loc -= ChangeContext.Anchor(extent, pivot, "pivot");
        }

        Vec3? wallNormal = null;
        if (op["wall"] is { } wall && wall.Kind != SvJsonKind.False)
        {
            (loc, wallNormal) = Wall(ctx, op, wall, loc, extent, exclude);
        }

        if (op["on"] is { } on && on.Kind != SvJsonKind.False)
        {
            bool fromPoint = op["at"] is not null || op["near"] is not null || op["wall"] is not null;
            (Vec3 probe, double? top) = Probe(ctx, on, loc, fromPoint);
            RayHit? hit = ctx.Geometry(exclude).Raycast(probe, new Vec3(0, 0, -1));
            // An actor is not part of the level geometry: standing on one means standing on its top, unless the
            // floor under the point is higher.
            double floor = top is double t && (hit is null || hit.Point.Z < t)
                ? t
                : hit?.Point.Z ?? throw new ApiException($"nothing below {probe} to stand on"
                    + (ctx.Geometry().HasGeometry ? "" : " (the map has no geometry)"), "on");
            // Dropping onto an object with no other point given lands at that object's middle.
            loc = new Vec3(fromPoint ? loc.X : probe.X, fromPoint ? loc.Y : probe.Y, floor - extent.Min.Z + ApiJson.Number(op, "lift", 0));
        }
        else if (op["lift"] is { } lift)
        {
            loc += new Vec3(0, 0, ApiJson.RequireNumber(lift, "lift"));
        }

        return new Placed(loc, wallNormal);
    }

    /// <summary>Where to cast down from for <c>on</c>, and the top to stand on when the target is an actor.</summary>
    private static (Vec3 Probe, double? Top) Probe(ChangeContext ctx, SvJsonNode on, Vec3 loc, bool fromPoint)
    {
        switch (on)
        {
            case { Kind: SvJsonKind.True }:
            case SvJsonString { Value: "floor" or "true" }:
                return (loc, null);
            case SvJsonString s when !ApiJson.TryVec(s, out _) && !s.Value.Contains('@') && !s.Value.Contains('+'):
            {
                // Onto an object: from just under a room's ceiling, or just over a solid thing's top.
                ObjectRef target = ctx.Resolve(s.Value, "on");
                (Vec3 min, Vec3 max) = MapObjects.Bounds(ctx.Project, target);
                bool carves = target.Kind == ObjectKind.Brush && ctx.Project.FindBrush(target.Id)!.Oper == CsgOper.Subtract;
                Vec3 xy = fromPoint ? loc : ChangeContext.Center((min, max));
                // A placed thing (not a volume, which is a space) with a known size is stood on.
                bool solidActor = target.Kind == ObjectKind.Actor && ctx.Project.FindActor(target.Id)!.Shape is null && max.Z > min.Z;
                double? top = solidActor ? max.Z : null;
                return (new Vec3(xy.X, xy.Y, carves ? max.Z - 1 : max.Z + 1), top);
            }

            default:
                return (ctx.Point(on, "on"), null);
        }
    }

    private static (Vec3 Location, Vec3 Normal) Wall(ChangeContext ctx, SvJsonObject op, SvJsonNode wall, Vec3 loc, (Vec3 Min, Vec3 Max) extent,
        string? exclude)
    {
        double reach = ApiJson.Number(op, "reach", 4096);
        Vec3[] dirs = wall switch
        {
            { Kind: SvJsonKind.True } => Horizontal,
            SvJsonString { Value: "nearest" or "true" } => Horizontal,
            SvJsonString s => [Axis(s.Value)],
            _ => [ApiJson.RequireVec(wall, "wall").Normalized()],
        };

        WorldGeometry geo = ctx.Geometry(exclude);
        RayHit? best = null;
        foreach (Vec3 d in dirs)
        {
            if (geo.Raycast(loc, d, reach) is { } hit && (best is null || hit.Distance < best.Distance))
            {
                best = hit;
            }
        }

        if (best is null)
        {
            throw new ApiException($"no wall within {reach} of {loc}" + (dirs.Length == 1 ? $" towards {dirs[0]}" : ""), "wall");
        }

        Vec3 n = best.Normal;
        // How far the object reaches from its location towards the wall, so its back sits on the wall.
        double depth = Corners(extent).Max(c => Vec3.Dot(c, -n));
        double gap = ApiJson.Number(op, "gap", 0);
        return (best.Point + n * (Math.Max(depth, 0) + gap), n);
    }

    private static Vec3 Axis(string s) => s.ToLowerInvariant() switch
    {
        "+x" or "x" => Vec3.UnitX,
        "-x" => -Vec3.UnitX,
        "+y" or "y" => Vec3.UnitY,
        "-y" => -Vec3.UnitY,
        "+z" or "z" or "up" => Vec3.UnitZ,
        "-z" or "down" => -Vec3.UnitZ,
        _ => throw new ApiException($"unknown wall direction '{s}' (nearest, +x, -x, +y, -y, or [dx,dy,dz])", "wall"),
    };

    private static readonly Vec3[] Horizontal = [Vec3.UnitX, -Vec3.UnitX, Vec3.UnitY, -Vec3.UnitY];

    private static IEnumerable<Vec3> Corners((Vec3 Min, Vec3 Max) b)
    {
        for (int i = 0; i < 8; i++)
        {
            yield return new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
        }
    }

    /// <summary>A spot <paramref name="dist"/> from <paramref name="target"/>: the first of 8 directions (from +x,
    /// turning left) that is in open space, in sight of the target and not on another actor.</summary>
    private static Vec3 Near(ChangeContext ctx, Vec3 target, double dist, string? exclude)
    {
        WorldGeometry geo = ctx.Geometry(exclude);
        for (int k = 0; k < 8; k++)
        {
            double a = k * Math.PI / 4;
            var dir = new Vec3(Math.Cos(a), Math.Sin(a), 0);
            Vec3 c = target + dir * dist;
            if (geo.HasGeometry && geo.IsSolid(c) && !geo.HasBaked)
            {
                continue;
            }

            if (geo.Raycast(target, dir, dist) is not null)
            {
                continue;
            }

            if (ctx.Project.Actors.Any(x => (x.Location - c).Length < 32))
            {
                continue;
            }

            return c;
        }

        ctx.Warn("near", $"no open spot {dist} from {target}; placed at +x");
        return target + new Vec3(dist, 0, 0);
    }
}
