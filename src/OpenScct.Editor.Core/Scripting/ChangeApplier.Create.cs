using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    /// <summary>create: an actor (<c>class</c>, optionally with a <c>brush</c> shape for volumes), a brush
    /// (<c>brush</c>/<c>shape</c>) or a GE (<c>ge</c>: kind). <c>count</c> + <c>step</c> make a row of copies.</summary>
    private static void Create(ChangeContext ctx, SvJsonObject op)
    {
        if (op["class"] is not null)
        {
            CreateActor(ctx, op);
        }
        else if (op["brush"] is not null || op["shape"] is not null)
        {
            CreateBrush(ctx, op);
        }
        else if (op["ge"] is not null)
        {
            CreateGe(ctx, op);
        }
        else
        {
            throw new ApiException("create needs class (an actor), brush (a brush) or ge (a GE kind)", "class");
        }
    }

    /// <summary>brush: creates a brush, or reshapes/edits the brush <c>id</c> names when it exists.</summary>
    private static void BrushOp(ChangeContext ctx, SvJsonObject op)
    {
        if (ApiJson.GetString(op, "id") is { } id && !id.StartsWith('$') && ctx.Project.FindBrush(id) is not null
            || ApiJson.GetString(op, "id") is { } sym && sym.StartsWith('$') && ctx.Symbols.ContainsKey(sym))
        {
            Update(ctx, op);
            return;
        }

        if (op["class"] is not null || op["ge"] is not null)
        {
            throw new ApiException("the brush op makes brushes; use create for actors and GEs", "class");
        }

        if (op["brush"] is null && op["shape"] is null)
        {
            throw new ApiException("give the shape (e.g. {\"size\":[512,512,256]})", "shape");
        }

        CreateBrush(ctx, op);
    }

    /// <summary>ge: creates a GE, or edits the GE <c>id</c> names when it exists.</summary>
    private static void GeOp(ChangeContext ctx, SvJsonObject op)
    {
        string? id = ApiJson.GetString(op, "id");
        string? real = id is null ? null : id.StartsWith('$') ? ctx.Symbols.GetValueOrDefault(id) : id;
        if (real is not null && ctx.Project.FindGe(real) is not null)
        {
            Update(ctx, op);
            return;
        }

        CreateGe(ctx, op);
    }

    private static void CreateActor(ChangeContext ctx, SvJsonObject op)
    {
        string cls = ResolveClass(ctx, ApiJson.GetString(op, "class") ?? throw new ApiException("class is a class name", "class"));
        var actor = new Actor("", cls);
        Gameplay.NewLights.Place(actor);
        SetProps(ctx, actor, op, "props");
        SetProps(ctx, actor, op, "set");
        if ((op["brush"] ?? op["shape"]) is { } shapeSpec)
        {
            (List<ShapePart> parts, Vec3? _) = BrushSpecs.Build(shapeSpec, op["brush"] is null ? "shape" : "brush", ctx.Body);
            if (parts.Count != 1)
            {
                throw new ApiException("a volume takes one brush shape", "brush");
            }

            actor.Shape = parts[0].Shape;
            BrushSpecs.Materials(op, actor.Shape);
        }

        SetActorTransform(op, actor);
        actor.Rotation = ExplicitRotation(op) ?? actor.Rotation;
        Placed placed = Placement.Resolve(ctx, op, actor.Location, ActorExtent(ctx, op, actor), null);
        actor.Location = placed.Location;
        actor.Rotation = Facing(ctx, op, actor.Rotation, actor.Location, actor.Shape is null ? placed.WallNormal : null);
        SetMeta(op, actor.Editor);
        ctx.Stamp(actor.Editor);

        Repeat(ctx, op, MapObjects.Short(cls), (id, _, delta) =>
        {
            Actor a = actor.Clone(id);
            a.Location += delta;
            if (op["door"] is SvJsonObject door)
            {
                if (!MoverBase.IsMover(a)) throw new ApiException("A door needs class Engine.Mover", "door");
                a.Door = Doors.DoorDesign.FromJson(ApiJson.Write(door));
                Doors.DoorCommands.Configure(a, a.Location, a.Rotation);
                a.Props["Tag"] ??= SvJsonNode.String(id);
            }
            // A mover starts the game at BasePos (+ its first key), so a new one starts where it was put.
            if (a.Door is null) MoverBase.Shift(a, delta);
            MoverBase.Place(a);

            ctx.Add($"Create {id}", actors: [a]);
        });
    }

    private static void CreateBrush(ChangeContext ctx, SvJsonObject op)
    {
        string field = op["brush"] is null ? "shape" : "brush";
        (List<ShapePart> parts, Vec3? center) = BrushSpecs.Build(op[field]!, field, ctx.Body);
        CsgOper oper = BrushSpecs.Csg(op["csg"], CsgOper.Subtract, "csg");
        int flags = op["flags"] is { } f ? BrushSpecs.Flags(f, "flags") : 0;
        var brushes = new List<Brush>();
        foreach (ShapePart part in parts)
        {
            var b = new Brush("") { Oper = part.Oper ?? oper, PolyFlags = flags, Shape = part.Shape };
            BrushSpecs.Materials(op, b.Shape);
            SetBrushTransform(op, b);
            SetMeta(op, b.Editor);
            ctx.Stamp(b.Editor);
            brushes.Add(b);
        }

        bool located = false;
        if (op["props"] is not null || op["set"] is not null)
        {
            foreach (Brush b in brushes)
            {
                located |= SetBrushProps(ctx, b, op);
            }
        }

        // Place the piece as a whole, turned as asked: its bounds are the union of its brushes at their offsets.
        // A Location or Rotation given as a prop is where it starts.
        Vec3 start = located ? brushes[0].Location : center ?? Vec3.Zero;
        Rot rot = ExplicitRotation(op) ?? brushes[0].Rotation;
        Vec3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        for (int i = 0; i < brushes.Count; i++)
        {
            brushes[i].Rotation = rot;
            brushes[i].Location = rot.Rotate(parts[i].Offset);
            (Vec3 lo, Vec3 hi) = MapObjects.BrushBounds(brushes[i]);
            min = Vec3.Min(min, lo);
            max = Vec3.Max(max, hi);
        }

        // Brushes keep their rotation against a wall (turning one would change its footprint); face still turns them.
        Placed placed = Placement.Resolve(ctx, op, start, (min, max), null);
        rot = Facing(ctx, op, rot, placed.Location, null);
        Repeat(ctx, op, "Brush", (id, symbol, delta) =>
        {
            // Each part is added at once so the next part's new id sees it. Extra parts of $piece bind $piece#p1...
            for (int i = 0; i < brushes.Count; i++)
            {
                string partId = i == 0 ? id : ctx.NewId(symbol is null ? null : $"{symbol}#p{i}", id + "_", "id");
                Brush b = brushes[i].Clone(partId);
                b.Location = placed.Location + rot.Rotate(parts[i].Offset) + delta;
                b.Rotation = rot;
                if (brushes.Count > 1 && b.Shape.Builder is { } spec)
                {
                    // The piece is named by its first brush, so reshaping any part rebuilds the whole piece.
                    spec.Strings[Geometry.Builders.BuilderArgs.PieceKey] = id;
                }

                ctx.Add($"Create {b.Id}", brushes: [b]);
            }
        });
    }

    private static void CreateGe(ChangeContext ctx, SvJsonObject op)
    {
        string kind = GeKind(ApiJson.GetString(op, "ge") ?? ApiJson.GetString(op, "kind")
            ?? throw new ApiException("give the GE kind (LG, ZL, PIPE, LADDER, HOH, POLE, FENCE)", "ge"), op["ge"] is null ? "kind" : "ge");
        var ge = new Ge("", kind) { Points = GePoints(ctx, op, kind) };
        SetGeFields(op, ge);
        SetMeta(op, ge.Editor);
        ctx.Stamp(ge.Editor);
        Repeat(ctx, op, kind, (id, _, delta) =>
        {
            Ge g = ge.Clone(id);
            g.Points = g.Points.Select(p => p + delta).ToList();
            ctx.Add($"Create {id}", ges: [g]);
        });
    }

    /// <summary>Adds the first object and its <c>count</c>-1 copies along <c>step</c>. Ids: the op's id (or a new one
    /// from <paramref name="stem"/>); copies of <c>$sym</c> bind <c>$sym#1</c>, <c>$sym#2</c>... The callback gets the
    /// id, the symbol bound to it (or null) and the copy's offset.</summary>
    private static void Repeat(ChangeContext ctx, SvJsonObject op, string stem, Action<string, string?, Vec3> add)
    {
        int count = (int)ApiJson.Number(op, "count", 1);
        if (count is < 1 or > 1000)
        {
            throw new ApiException("count is 1..1000", "count");
        }

        Vec3 step = op["step"] is { } s ? ApiJson.RequireVec(s, "step") : Vec3.Zero;
        if (count > 1 && op["step"] is null)
        {
            throw new ApiException("give step [dx,dy,dz] with count", "step");
        }

        string? requested = ApiJson.GetString(op, "id");
        for (int i = 0; i < count; i++)
        {
            string? want = i == 0 ? requested : requested is not null && requested.StartsWith('$') ? $"{requested}#{i}" : null;
            bool literal = requested is not null && !requested.StartsWith('$');
            string id = ctx.NewId(want, i > 0 && literal ? requested! : stem, "id");
            add(id, want is not null && want.StartsWith('$') ? want : null, step * i);
        }
    }

    // ---- shared field setters ------------------------------------------------------------------------------------

    private static string ResolveClass(ChangeContext ctx, string name)
    {
        IClassCatalog? cat = EditorServices.Classes;
        if (cat is not null)
        {
            ClassInfo info = cat.Find(name) ?? throw new ApiException($"unknown class '{name}'" + SuggestClass(cat, name), "class");
            // Movers and brush volumes are not placeable from the class list in UE2 but are made from a shape.
            bool shaped = cat.IsA(info.Name, "Engine.Mover") || cat.IsA(info.Name, "Engine.Brush");
            if (info.Abstract || !info.Placeable && !shaped)
            {
                throw new ApiException($"{info.Name} is {(info.Abstract ? "abstract" : "not placeable")}", "class");
            }

            return info.Name;
        }

        if (name.Contains('.'))
        {
            return name;
        }

        return ctx.Project.Actors.FirstOrDefault(a => string.Equals(a.ShortClass, name, StringComparison.OrdinalIgnoreCase))?.Class
            ?? throw new ApiException($"the class catalog is not loaded: give the full class name (e.g. Engine.{name} or SBase.{name})", "class");
    }

    private static string SuggestClass(IClassCatalog cat, string name)
    {
        string shortName = MapObjects.Short(name);
        string[] close = cat.All.Where(c => c.Placeable && !c.Abstract && c.ShortName.Contains(shortName, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name).Order(StringComparer.Ordinal).Take(5).ToArray();
        return close.Length > 0 ? $"; did you mean {string.Join(", ", close)}?" : "";
    }

    private static readonly string[] s_transformProps = ["Location", "Rotation", "DrawScale", "DrawScale3D", "PrePivot"];

    /// <summary>Sets props from <c>op[key]</c> (an object; null values remove). Transform names are routed to the
    /// typed fields.</summary>
    private static void SetProps(ChangeContext ctx, Actor actor, SvJsonObject op, string key)
    {
        if (op[key] is null)
        {
            return;
        }

        if (op[key] is not SvJsonObject props)
        {
            throw new ApiException($"{key} is an object of property values", key);
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in props)
        {
            string field = $"{key}.{kv.Key}";
            switch (s_transformProps.FirstOrDefault(t => t.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
            {
                case "Location":
                    actor.Location = ctx.Point(kv.Value, field);
                    continue;
                case "Rotation":
                    actor.Rotation = ApiJson.TryRot(kv.Value, out Rot r) ? r : throw new ApiException("expected [pitch,yaw,roll]", field);
                    continue;
                case "DrawScale":
                    actor.DrawScale = ApiJson.RequireNumber(kv.Value, field);
                    continue;
                case "DrawScale3D":
                    actor.DrawScale3D = ApiJson.RequireVec(kv.Value, field);
                    continue;
                case "PrePivot":
                    actor.PrePivot = ApiJson.RequireVec(kv.Value, field);
                    continue;
            }

            (string name, SvJsonNode? value) = ctx.Coercion.Coerce(actor.Class, kv.Key, kv.Value, actor.Props, field, ctx.Warnings);
            actor.Props[name] = value;
        }
    }

    private static readonly string[] s_brushTransformProps = ["Location", "Rotation", "PrePivot"];

    /// <summary>Sets a brush's props from <c>props</c> and <c>set</c>. Location, Rotation and PrePivot go to the typed
    /// fields; returns whether a Location was given.</summary>
    private static bool SetBrushProps(ChangeContext ctx, Brush brush, SvJsonObject op)
    {
        bool located = false;
        foreach (string key in new[] { "props", "set" })
        {
            if (op[key] is SvJsonObject props)
            {
                foreach (KeyValuePair<string, SvJsonNode> kv in props)
                {
                    string field = $"{key}.{kv.Key}";
                    switch (s_brushTransformProps.FirstOrDefault(t => t.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        case "Location":
                            brush.Location = ctx.Point(kv.Value, field);
                            located = true;
                            continue;
                        case "Rotation":
                            brush.Rotation = ApiJson.TryRot(kv.Value, out Rot r) ? r : throw new ApiException("expected [pitch,yaw,roll]", field);
                            continue;
                        case "PrePivot":
                            brush.PrePivot = ApiJson.RequireVec(kv.Value, field);
                            continue;
                    }

                    (string name, SvJsonNode? value) = ctx.Coercion.Coerce("Engine.Brush", kv.Key, kv.Value, brush.Props, field, ctx.Warnings);
                    brush.Props[name] = value;
                }
            }
        }

        return located;
    }

    /// <summary>The first of Location, Rotation and PrePivot given in <c>props</c> or <c>set</c>, as <c>key.Name</c>.</summary>
    private static string? BrushTransformProp(SvJsonObject op)
    {
        foreach (string key in new[] { "props", "set" })
        {
            if (op[key] is SvJsonObject o && o.Select(kv => kv.Key).FirstOrDefault(k => s_brushTransformProps.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } name)
            {
                return $"{key}.{name}";
            }
        }

        return null;
    }

    private static void SetActorTransform(SvJsonObject op, Actor a)
    {
        if (op["scale"] is { } s)
        {
            if (ApiJson.TryNumber(s, out double uniform))
            {
                a.DrawScale = uniform;
            }
            else
            {
                a.DrawScale3D = ApiJson.RequireVec(s, "scale");
            }
        }

        if (op["scale3d"] is { } s3)
        {
            a.DrawScale3D = ApiJson.RequireVec(s3, "scale3d");
        }

        if (op["prePivot"] is { } pp)
        {
            a.PrePivot = ApiJson.RequireVec(pp, "prePivot");
        }

        if (op["postScale"] is not null)
        {
            throw new ApiException("postScale is for brushes", "postScale");
        }
    }

    private static void SetBrushTransform(SvJsonObject op, Brush b)
    {
        if (op["scale"] is { } s)
        {
            b.MainScale = ApiJson.TryNumber(s, out double u) ? Vec3.One * u : ApiJson.RequireVec(s, "scale");
        }

        if (op["postScale"] is { } ps)
        {
            b.PostScale = ApiJson.TryNumber(ps, out double u) ? Vec3.One * u : ApiJson.RequireVec(ps, "postScale");
        }

        if (op["prePivot"] is { } pp)
        {
            b.PrePivot = ApiJson.RequireVec(pp, "prePivot");
        }

        if (op["scale3d"] is not null)
        {
            throw new ApiException("scale3d is for actors; brushes use scale (MainScale) and postScale", "scale3d");
        }
    }

    /// <summary>The rotation an op gives outright: <c>rot</c> (units) or <c>rotDeg</c> (degrees). Applied before
    /// placement, so a turned object's bounds are what gets placed.</summary>
    private static Rot? ExplicitRotation(SvJsonObject op)
    {
        if (op["rot"] is { } r)
        {
            return ApiJson.TryRot(r, out Rot rot) ? RotatorMath.Tidy(rot) : throw new ApiException("rot is [pitch,yaw,roll] in rotator units (65536 = one turn)", "rot");
        }

        if (op["rotDeg"] is { } rd)
        {
            return ApiJson.TryRot(rd, out Rot rot, Rot.UnitsPerTurn / 360.0) ? RotatorMath.Tidy(rot) : throw new ApiException("rotDeg is [pitch,yaw,roll] in degrees", "rotDeg");
        }

        return null;
    }

    /// <summary>The rotation after placement: <paramref name="current"/> when <c>rot</c>/<c>rotDeg</c> gave it,
    /// else looking at <c>face</c>, else facing out of the wall it was snapped to (unless <c>keepRot</c>).</summary>
    private static Rot Facing(ChangeContext ctx, SvJsonObject op, Rot current, Vec3 location, Vec3? wallNormal)
    {
        if (op["rot"] is not null || op["rotDeg"] is not null)
        {
            return current;
        }

        if (op["face"] is { } face)
        {
            return RotatorMath.LookAt(location, ctx.Point(face, "face"));
        }

        if (wallNormal is Vec3 n && !ApiJson.GetBool(op, "keepRot", false))
        {
            return current with { Yaw = RotatorMath.YawOf(n) };
        }

        return current;
    }

    /// <summary>An actor's bounds relative to its location, for placement. Warns when <c>on</c> cannot know how tall
    /// it is (no class catalog to give its CollisionHeight).</summary>
    private static (Vec3 Min, Vec3 Max) ActorExtent(ChangeContext ctx, SvJsonObject op, Actor a)
    {
        (Vec3 min, Vec3 max) = MapObjects.ActorBounds(a);
        WarnUnknownHeight(ctx, op, a, max.Z - min.Z);
        return (min - a.Location, max - a.Location);
    }

    private static void WarnUnknownHeight(ChangeContext ctx, SvJsonObject op, Actor a, double height)
    {
        if (op["on"] is { Kind: not SvJsonKind.False } && a.Shape is null && height <= 0 && EditorServices.Classes is null)
        {
            ctx.Warn("on", $"no class catalog, so the CollisionHeight of {a.ShortClass} is unknown: its origin stands on the floor (set CollisionHeight or add lift)");
        }
    }

    private static void SetMeta(SvJsonObject op, EditorMeta m)
    {
        if (ApiJson.GetString(op, "layer") is { } layer)
        {
            m.Layer = string.Equals(layer, "Default", StringComparison.OrdinalIgnoreCase) ? "" : layer;
        }

        if (ApiJson.GetString(op, "folder") is { } folder)
        {
            m.Folder = folder.Trim('/');
        }

        if (op["note"] is { } note)
        {
            m.Note = note is SvJsonString s ? s.Value : null;
        }

        if (op["hidden"] is { } h)
        {
            m.Hidden = h.Kind == SvJsonKind.True;
        }

        if (op["locked"] is { } l)
        {
            m.Locked = l.Kind == SvJsonKind.True;
        }
    }

    private static string GeKind(string kind, string field) =>
        GeKinds.All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase))
        ?? throw new ApiException($"unknown GE kind '{kind}' ({string.Join(", ", GeKinds.All)})", field);

    private static List<Vec3> GePoints(ChangeContext ctx, SvJsonObject op, string kind)
    {
        if (op["points"] is not SvJsonArray pts)
        {
            throw new ApiException("points is a list of 2 points (3 for LADDER and PIPE)", "points");
        }

        int want = GeKinds.HasThirdPoint(kind) ? 3 : 2;
        if (pts.Count != want)
        {
            throw new ApiException($"a {kind} has {want} points", "points");
        }

        return pts.Select((p, i) => ctx.Point(p, $"points[{i}]")).ToList();
    }

    private static void SetGeFields(SvJsonObject op, Ge ge)
    {
        if (op["mask"] is { } mask)
        {
            int m = (int)ApiJson.RequireNumber(mask, "mask");
            if (m is not (0 or 1 or 2 or 4 or 8 or 16 or 32 or 64 or 128))
            {
                throw new ApiException("mask is 0 (always on) or one ID bit: 1, 2, 4 ... 128", "mask");
            }

            ge.Mask = m;
        }

        if (op["platform"] is { } platform)
        {
            int p = (int)ApiJson.RequireNumber(platform, "platform");
            ge.Platform = p is >= 0 and <= 2 ? p : throw new ApiException("platform is 0 (all), 1 (PS2) or 2 (Xbox)", "platform");
        }

        SetGeAppearance(op, ge);
    }
}
