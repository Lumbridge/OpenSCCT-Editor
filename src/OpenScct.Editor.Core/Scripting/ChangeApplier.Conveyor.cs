using OpenScct.Content.Json;
using OpenScct.Core.Uc;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    // A property: static fields of the other parts of this class may not be set up yet.
    private static string[] ConveyorKeys =>
        ["id", "points", "floor", "loop", "look", "walls", "width", "speed", "on", "trigger", "items", "scroll", "drive", "tile", "axis", "next",
            "props", "set", .. MetaKeys];

    /// <summary>The default uu of travel per texture repeat for <c>drive</c>.</summary>
    public const double DefaultScrollTile = 128;

    /// <summary>
    /// conveyor: creates a native conveyor (<c>Svm.Conveyor</c>) along <c>points</c>, or edits the conveyor <c>id</c>
    /// names. Points are on the riding surface; <c>floor:true</c> drops each to the floor under it and lifts it as the
    /// conveyor tool does for the look. A new conveyor's location is its first point. A new conveyor with <c>drive</c>
    /// and no points is a driver fitted to the top of those mesh actors (<see cref="ConveyorDriver.Fit"/>). <c>next</c>
    /// names the conveyor its end hands items to (null clears it).
    /// </summary>
    private static void ConveyorOp(ChangeContext ctx, SvJsonObject op)
    {
        string? id = ApiJson.GetString(op, "id");
        string? real = id is null ? null : id.StartsWith('$') ? ctx.Symbols.GetValueOrDefault(id) : id;
        Actor? existing = real is null ? null : ctx.Project.FindActor(real);
        if (existing is not null && !ConveyorDesign.Is(existing))
        {
            throw new ApiException($"{existing.Id} is a {existing.ShortClass}, not a conveyor", "id");
        }

        if (existing is not null)
        {
            ctx.Edit([ObjectRef.Actor(existing.Id)], $"Edit {existing.Id}", actor: a => SetConveyor(ctx, a, op, placed: true));
            return;
        }

        Actor actor;
        if (op["points"] is not null)
        {
            actor = new Actor("", ConveyorDesign.ClassName);
        }
        else if (DriveMeshes(ctx, op) is { } meshes)
        {
            actor = ConveyorDriver.Fit(ctx.Folder, meshes, ScrollTile(op), ScrollAxis(op, "axis"));
        }
        else
        {
            throw new ApiException("give points (two or more along the riding surface), or drive (mesh actors to fit a driver to)", "points");
        }

        SetConveyor(ctx, actor, op, placed: false);
        SetMeta(op, actor.Editor);
        ctx.Stamp(actor.Editor);
        string newId = ctx.NewId(id, "Conveyor", "id");
        ctx.Add($"Create {newId}", actors: [actor.Clone(newId)]);
    }

    /// <param name="placed">False for a new conveyor: it is moved to its first point.</param>
    private static void SetConveyor(ChangeContext ctx, Actor a, SvJsonObject op, bool placed)
    {
        // The look first: it decides how high a point dropped to the floor goes.
        SetChoice(a, op, "look", "Look", ConveyorDesign.Looks);
        SetChoice(a, op, "walls", "Walls", ConveyorDesign.WallNames);
        SetChoice(a, op, "trigger", "TriggerAction", ConveyorDesign.Triggers);
        SetFlag(a, op, "loop", "bLoop");
        SetFlag(a, op, "on", "bOn");
        if (op["width"] is { } width)
        {
            a.Props.Set("Width", SvJsonNode.Float((float)ApiJson.RequireNumber(width, "width")));
        }

        if (op["speed"] is { } speed)
        {
            a.Props.Set("Speed", SvJsonNode.Float((float)ApiJson.RequireNumber(speed, "speed")));
        }

        if (ApiJson.StringList(op["items"], "items") is { } items)
        {
            a.Props.Set("ItemMeshes", PropValues.Array(items.Select(PropValues.AssetRef)));
        }

        if (op["scroll"] is { } scroll)
        {
            a.Props.Set("ScrollTargets", ConveyorDesign.ScrollValue(ScrollTargets(ctx, scroll)));
        }

        if (DriveMeshes(ctx, op) is { } driven)
        {
            double tile = ScrollTile(op);
            byte axis = ScrollAxis(op, "axis");
            a.Props.Set("ScrollTargets", ConveyorDesign.ScrollValue(driven.Select(m => new FConveyorScroll
            {
                Target = m.Id, UnitsPerTile = (float)tile, Axis = axis,
            })));
        }

        if (op["next"] is { } next)
        {
            SetNext(ctx, a, next);
        }

        SetProps(ctx, a, op, "props");
        SetProps(ctx, a, op, "set");
        if (op["points"] is not null)
        {
            List<ConveyorPoint> points = ConveyorPoints(ctx, op, ConveyorDesign.Read(a).Look);
            if (!placed)
            {
                a.Location = points[0].At;
            }

            a.Props.Set("Path", ConveyorDesign.PathValue(a, points));
        }
    }

    /// <summary><c>next</c>: the conveyor (id or symbol) this one's end hands its items to; null clears the link.</summary>
    private static void SetNext(ChangeContext ctx, Actor a, SvJsonNode next)
    {
        if (ApiJson.IsNull(next))
        {
            a.Props.Remove("Next");
            return;
        }

        string name = next is SvJsonString s ? s.Value : throw new ApiException("next is a conveyor id, or null to clear it", "next");
        Actor target = ctx.Project.FindActor(ctx.ResolveId(name, "next")) ?? throw new ApiException($"no actor '{name}'", "next");
        if (!ConveyorDesign.Is(target))
        {
            throw new ApiException($"{target.Id} is a {target.ShortClass}, not a conveyor", "next");
        }

        a.Props.Set("Next", PropValues.ActorRef(target.Id));
    }

    /// <summary><c>drive</c>: the mesh actors named, or null when the op has none.</summary>
    private static List<Actor>? DriveMeshes(ChangeContext ctx, SvJsonObject op)
    {
        if (ApiJson.StringList(op["drive"], "drive") is not { } ids)
        {
            return null;
        }

        if (ids.Count == 0)
        {
            throw new ApiException("drive is one or more mesh actor ids", "drive");
        }

        return ids.Select(id => ctx.Project.FindActor(ctx.ResolveId(id, "drive")) ?? throw new ApiException($"no actor '{id}'", "drive")).ToList();
    }

    /// <summary>
    /// <c>scroll</c>: <c>[{"target": id, "tile": uu, "axis": "u"|"v", "material": name}]</c>, the whole list of scroll
    /// targets; <c>material</c> scrolls only that material of the mesh.
    /// </summary>
    private static List<FConveyorScroll> ScrollTargets(ChangeContext ctx, SvJsonNode scroll)
    {
        if (scroll is not SvJsonArray list)
        {
            throw new ApiException("scroll is a list of {\"target\": id, \"tile\": uu, \"axis\": \"u\" or \"v\"}", "scroll");
        }

        var targets = new List<FConveyorScroll>();
        for (int i = 0; i < list.Count; i++)
        {
            string field = $"scroll[{i}]";
            SvJsonObject o = list[i] as SvJsonObject ?? throw new ApiException("a scroll target is {\"target\": id, \"tile\": uu, \"axis\": \"u\" or \"v\"}", field);
            string target = ApiJson.GetString(o, "target") ?? throw new ApiException("target is the mesh actor's id", field);
            string id = ctx.Project.FindActor(ctx.ResolveId(target, field))?.Id ?? throw new ApiException($"no actor '{target}'", field);
            targets.Add(new FConveyorScroll
            {
                Target = id, UnitsPerTile = (float)ScrollTile(o), Axis = ScrollAxis(o, field),
                Material = new AssetRef(ApiJson.GetString(o, "material")),
            });
        }

        return targets;
    }

    /// <summary><c>tile</c>: uu of travel per texture repeat; negative scrolls the texture the other way.</summary>
    private static double ScrollTile(SvJsonObject o)
    {
        double tile = ApiJson.Number(o, "tile", DefaultScrollTile);
        return tile != 0 ? tile : throw new ApiException("tile is the uu of travel one texture repeat covers (negative scrolls the other way), not 0", "tile");
    }

    /// <summary><c>axis</c>: the texture axis that scrolls, <c>"u"</c> or <c>"v"</c> (the default).</summary>
    private static byte ScrollAxis(SvJsonObject o, string field) => ApiJson.GetString(o, "axis")?.ToLowerInvariant() switch
    {
        null or "v" => 1,
        "u" => 0,
        _ => throw new ApiException("axis is \"u\" or \"v\"", field),
    };

    private static void SetFlag(Actor a, SvJsonObject op, string key, string prop)
    {
        if (op[key] is not null)
        {
            a.Props.Set(prop, SvJsonNode.Bool(ApiJson.RequireBool(op, key) ?? throw new ApiException($"{key} is true or false", key)));
        }
    }

    private static void SetChoice(Actor a, SvJsonObject op, string key, string prop, IReadOnlyList<string> names)
    {
        if (ApiJson.GetString(op, key) is not { } name)
        {
            return;
        }

        int index = names.ToList().FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new ApiException($"{key} is one of {string.Join(", ", names)}", key);
        }

        a.Props.Set(prop, SvJsonNode.Integer(index));
    }

    /// <summary>Points as POINT or <c>{"at": POINT, "radius": r}</c>; with <c>floor</c>, dropped to the floor under each.</summary>
    private static List<ConveyorPoint> ConveyorPoints(ChangeContext ctx, SvJsonObject op, EConveyorLook look)
    {
        if (op["points"] is not SvJsonArray list || list.Count < 2)
        {
            throw new ApiException("points is a list of two or more points ([x,y,z] or {\"at\":[x,y,z],\"radius\":r})", "points");
        }

        bool floor = ApiJson.GetBool(op, "floor", false);
        var points = new List<ConveyorPoint>();
        for (int i = 0; i < list.Count; i++)
        {
            string field = $"points[{i}]";
            SvJsonNode spec = list[i];
            double radius = 0;
            if (spec is SvJsonObject o)
            {
                radius = ApiJson.Number(o, "radius", 0);
                spec = o["at"] ?? throw new ApiException("a rounded point is {\"at\":[x,y,z],\"radius\":r}", field);
            }

            Vec3 at = ctx.Point(spec, field);
            if (floor)
            {
                RayHit hit = ctx.Geometry().Raycast(at + new Vec3(0, 0, 1), new Vec3(0, 0, -1), 4096)
                    ?? throw new ApiException("no floor under this point within 4096", field);
                at = hit.Point + new Vec3(0, 0, ConveyorDesign.SurfaceLift(look));
            }

            points.Add(new ConveyorPoint(at, Math.Max(0, radius)));
        }

        return points;
    }
}
