using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    /// <summary>update: props (<c>set</c>/<c>unset</c>), transform and placement, meta, brush shape/csg/flags/materials,
    /// GE points/mask, and <c>rename</c> (references to the old id are rewritten).</summary>
    private static void Update(ChangeContext ctx, SvJsonObject op)
    {
        List<ObjectRef> targets = ctx.Targets(op);
        if (targets.Count > 1 && (op["at"] is not null || op["near"] is not null || op["rename"] is not null))
        {
            throw new ApiException("at, near and rename take one object; use move for groups", op["rename"] is null ? "at" : "rename");
        }

        bool place = Placement.Requested(op);
        List<string>? unset = ApiJson.StringList(op["unset"], "unset");
        var rebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectRef r in targets)
        {
            if (rebuilt.Contains(r.Id))
            {
                continue; // a part of a piece another target already rebuilt
            }

            if (r.Kind == ObjectKind.Brush && (op["shape"] ?? op["brush"]) is { } spec && IsPiece(ctx, r.Id, spec))
            {
                rebuilt.UnionWith(UpdatePiece(ctx, op, r.Id, spec, unset));
                continue;
            }

            switch (r.Kind)
            {
                case ObjectKind.Actor:
                    ctx.Edit([r], $"Edit {r.Id}", actor: a => UpdateActor(ctx, op, a, place, unset));
                    break;
                case ObjectKind.Brush:
                    ctx.Edit([r], $"Edit {r.Id}", brush: b => UpdateBrush(ctx, op, b, place, unset));
                    break;
                default:
                    ctx.Edit([r], $"Edit {r.Id}", ge: g => UpdateGe(ctx, op, g, place));
                    break;
            }
        }

        if (ApiJson.GetString(op, "rename") is { } newId)
        {
            if (targets.Count == 0)
            {
                throw new ApiException("rename takes one object; nothing matched", "rename");
            }

            Rename(ctx, targets[0], newId);
        }
    }

    private static void UpdateActor(ChangeContext ctx, SvJsonObject op, Actor a, bool place, List<string>? unset)
    {
        SetProps(ctx, a, op, "props");
        SetProps(ctx, a, op, "set");
        Unset(a.Props, unset);
        SetActorTransform(op, a);
        if ((op["shape"] ?? op["brush"]) is { } spec)
        {
            a.Shape = Reshape(ctx, spec, op["shape"] is null ? "brush" : "shape", a.Shape, out _);
        }

        if (a.Shape is not null)
        {
            BrushSpecs.Materials(op, a.Shape);
        }

        a.Rotation = ExplicitRotation(op) ?? a.Rotation;
        Vec3? wall = null;
        if (place)
        {
            Placed p = Placement.Resolve(ctx, op, a.Location, ActorExtent(ctx, op, a), null);
            a.Location = p.Location;
            wall = p.WallNormal;
        }

        a.Rotation = Facing(ctx, op, a.Rotation, a.Location, a.Shape is null ? wall : null);
        SetMeta(op, a.Editor);
    }

    private static void UpdateBrush(ChangeContext ctx, SvJsonObject op, Brush b, bool place, List<string>? unset)
    {
        SetBrushProps(ctx, b, op);
        Unset(b.Props, unset);
        b.Oper = BrushSpecs.Csg(op["csg"], b.Oper, "csg");
        if (op["flags"] is { } flags)
        {
            b.PolyFlags = BrushSpecs.Flags(flags, "flags");
        }

        bool keepTextures = ApiJson.GetBool(op, "keepTextures", true);
        if ((op["shape"] ?? op["brush"]) is { } spec)
        {
            Brush? old = keepTextures ? b.Clone() : null;
            b.Shape = Reshape(ctx, spec, op["shape"] is null ? "brush" : "shape", b.Shape, out Vec3? center)!;
            if (center is Vec3 c && !place)
            {
                b.Location = c;
            }

            if (old is not null)
            {
                // New builder parameters (a resized cube): the faces keep their surfaces and texture size.
                Geometry.TextureLock.Reshaped(old, b);
            }
        }

        BrushSpecs.Materials(op, b.Shape);
        Brush? unscaled = (op["scale"] ?? op["postScale"]) is not null && keepTextures ? b.Clone() : null;
        SetBrushTransform(op, b);
        if (unscaled is not null)
        {
            // Textures keep their size through the new scale; a new pre-pivot then moves them with the brush.
            Vec3 prePivot = b.PrePivot;
            b.PrePivot = unscaled.PrePivot;
            Geometry.TextureLock.Keep(unscaled, b);
            b.PrePivot = prePivot;
        }

        b.Rotation = ExplicitRotation(op) ?? b.Rotation;
        if (place)
        {
            (Vec3 min, Vec3 max) = MapObjects.BrushBounds(b);
            b.Location = Placement.Resolve(ctx, op, b.Location, (min - b.Location, max - b.Location), b.Id).Location;
        }

        b.Rotation = Facing(ctx, op, b.Rotation, b.Location, null);
        SetMeta(op, b.Editor);
    }

    private static void UpdateGe(ChangeContext ctx, SvJsonObject op, Ge g, bool place)
    {
        string oldKind = g.Kind;
        if ((ApiJson.GetString(op, "kind") ?? ApiJson.GetString(op, "ge")) is { } kind)
        {
            g.Kind = GeKind(kind, "kind");
        }

        if (op["points"] is not null)
        {
            g.Points = GePoints(ctx, op, g.Kind);
        }
        else if (g.Kind != oldKind && g.Points.Count != (GeKinds.HasThirdPoint(g.Kind) ? 3 : 2))
        {
            throw new ApiException($"a {g.Kind} has {(GeKinds.HasThirdPoint(g.Kind) ? 3 : 2)} points: give points", "points");
        }

        if (place && g.Points.Count > 0)
        {
            Vec3 origin = g.Points[0];
            (Vec3 min, Vec3 max) = (g.Points.Aggregate(Vec3.Min), g.Points.Aggregate(Vec3.Max));
            Vec3 delta = Placement.Resolve(ctx, op, origin, (min - origin, max - origin), null).Location - origin;
            g.Points = g.Points.Select(p => p + delta).ToList();
        }

        SetGeFields(op, g);
        SetMeta(op, g.Editor);
    }

    /// <summary>A new shape for an existing one: builder parameters merge into its builder spec.</summary>
    private static BrushShape? Reshape(ChangeContext ctx, SvJsonNode spec, string field, BrushShape? current, out Vec3? center)
    {
        bool explicitShape = spec is SvJsonString || spec is SvJsonObject o && (o["kind"] ?? o["size"] ?? o["min"] ?? o["faces"]) is not null;
        if (!explicitShape && current?.Builder is null)
        {
            throw new ApiException("this brush has no builder to change; give a full shape (kind, size, min/max or faces)", field);
        }

        (List<ShapePart> parts, center) = BrushSpecs.Build(spec, field, ctx.Body, current?.Builder);
        if (parts.Count != 1)
        {
            throw new ApiException("this builder makes several brushes: reshape a brush made by it (its piece is rebuilt), or create a new piece", field);
        }

        BrushShape shape = parts[0].Shape;
        string?[] mats = current?.Faces.Select(f => f.Material).Distinct().ToArray() ?? [];
        if (mats.Length == 1)
        {
            foreach (BrushFace f in shape.Faces)
            {
                f.Material = mats[0];
            }
        }

        return shape;
    }

    /// <summary>True when a new shape for this brush means rebuilding a multi-brush piece: the brush is part of one,
    /// or the new parameters of its own builder make several brushes.</summary>
    private static bool IsPiece(ChangeContext ctx, string brushId, SvJsonNode spec)
    {
        BuilderSpec? current = ctx.Project.FindBrush(brushId)?.Shape.Builder;
        if (current is null || Geometry.Builders.BuilderCatalog.Find(current.Kind) is null)
        {
            return false;
        }

        string? kind = spec is SvJsonObject o ? ApiJson.GetString(o, "kind") : spec is SvJsonString k ? k.Value : null;
        if (kind is not null && !string.Equals(kind, current.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return false; // a different builder replaces this brush's shape
        }

        if (spec is SvJsonObject so && (so["size"] ?? so["min"] ?? so["faces"]) is not null)
        {
            return false;
        }

        return current.Strings.ContainsKey(Geometry.Builders.BuilderArgs.PieceKey)
            || BrushSpecs.Build(spec, "shape", ctx.Body, current).Parts.Count > 1;
    }

    /// <summary>Rebuilds the multi-brush piece a brush belongs to from changed builder parameters, in place in the CSG
    /// order: its parts keep their ids in part order, its transform, CSG choice, flags, props and editor state. Materials,
    /// flags, props and meta from the op go to every part; placement does not (move the parts with move). Returns the
    /// ids of the old and new parts.</summary>
    private static IEnumerable<string> UpdatePiece(ChangeContext ctx, SvJsonObject op, string brushId, SvJsonNode spec, List<string>? unset)
    {
        string field = op["shape"] is null ? "brush" : "shape";
        foreach (string k in new[] { "rot", "rotDeg", "face", "scale", "postScale", "prePivot", "csg", "rename" }.Concat(Placement.Keys))
        {
            if (op[k] is not null)
            {
                throw new ApiException($"'{k}' does not apply when a piece is rebuilt; rebuild it, then move or rotate all its parts", k);
            }
        }

        if (BrushTransformProp(op) is { } transform)
        {
            throw new ApiException($"'{transform}' does not apply when a piece is rebuilt; rebuild it, then move or rotate all its parts", transform);
        }

        Brush hit = ctx.Project.FindBrush(brushId)!;
        BuilderSpec next = BrushSpecs.Build(spec, field, ctx.Body, hit.Shape.Builder).Parts[0].Shape.Builder!.Clone();
        Geometry.Builders.PieceEdit edit;
        try
        {
            edit = Geometry.Builders.PiecePlacer.Regenerate(ctx.Project, brushId, next);
        }
        catch (ArgumentException ex)
        {
            throw new ApiException(ex.Message, field);
        }

        foreach (Brush b in edit.Removed)
        {
            ctx.Touch(ObjectRef.Brush(b.Id));
        }

        foreach (Brush b in edit.Added)
        {
            BrushSpecs.Materials(op, b.Shape);
            if (op["flags"] is { } flags)
            {
                b.PolyFlags = BrushSpecs.Flags(flags, "flags");
            }

            SetBrushProps(ctx, b, op);
            Unset(b.Props, unset);
            SetMeta(op, b.Editor);
            ctx.Stamp(b.Editor);
            ctx.Created(ObjectRef.Brush(b.Id));
        }

        ctx.Run(edit);

        // A piece made with "$name" names its extra parts $name#p1, $name#p2...: bind the symbols of new parts.
        if (ctx.Symbols.FirstOrDefault(kv => string.Equals(kv.Value, edit.Added[0].Id, StringComparison.OrdinalIgnoreCase)).Key is { } symbol)
        {
            for (int i = 1; i < edit.Added.Count; i++)
            {
                ctx.Symbols.TryAdd($"{symbol}#p{i}", edit.Added[i].Id);
            }
        }

        return edit.Removed.Select(b => b.Id).Concat(edit.Added.Select(b => b.Id));
    }

    private static void Unset(PropSet props, List<string>? names)
    {
        foreach (string n in names ?? [])
        {
            string? key = props.Keys.FirstOrDefault(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase));
            if (key is not null)
            {
                props.Remove(key);
            }
        }
    }

    private static void Delete(ChangeContext ctx, SvJsonObject op)
    {
        List<ObjectRef> targets = ctx.Targets(op);
        if (targets.Count == 0)
        {
            return;
        }

        var gone = new HashSet<string>(targets.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        int baked = BakedContent.BakedWorldKept(ctx.Project)
            ? targets.Count(r => r.Kind == ObjectKind.Brush && ctx.Project.FindBrush(r.Id) is { } b && BakedContent.IsBaked(b))
            : 0;
        if (baked > 0)
        {
            ctx.Warn("ids", $"{baked} of the deleted brushes came in with the baked world, which still holds them while it is kept "
                + "(build it from the brushes instead: Map Settings, Baked world)");
        }

        ctx.Delete(targets);
        foreach (Actor a in ctx.Project.Actors)
        {
            foreach (string id in References(a.Props).Where(gone.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ctx.Warn("ids", $"{a.Id} still references deleted {id}");
            }
        }
    }

    /// <summary>move: <c>by</c> [dx,dy,dz], or <c>to</c>/<c>at</c> (the first object goes there, the rest keep their
    /// offsets), plus the placement fields (on, wall, pivot...) applied to each object, then <c>stick</c> (pull them
    /// onto the nearest neighbour faces, <see cref="Stick"/>).</summary>
    private static void Move(ChangeContext ctx, SvJsonObject op)
    {
        List<ObjectRef> targets = ctx.Targets(op);
        if (targets.Count == 0)
        {
            return;
        }

        if (op["to"] is not null && op["at"] is not null)
        {
            throw new ApiException("give to or at, not both", "to");
        }

        if ((op["to"] ?? op["at"]) is not null && op["by"] is not null)
        {
            throw new ApiException("give by (relative) or to (absolute), not both; use offset to shift a target point", "by");
        }

        Vec3 by = op["by"] is { } b ? ApiJson.RequireVec(b, "by") : Vec3.Zero;
        SvJsonNode? to = op["to"] ?? op["at"];
        bool single = targets.Count == 1;
        if (!single && (op["near"] is not null || op["pivot"] is not null))
        {
            throw new ApiException("near and pivot take one object", op["near"] is null ? "pivot" : "near");
        }

        Vec3 groupDelta = by + (!single && op["offset"] is { } off ? ApiJson.RequireVec(off, "offset") : Vec3.Zero);
        if (to is not null && !single)
        {
            groupDelta += ctx.Point(to, op["to"] is null ? "at" : "to") - MapObjects.Origin(ctx.Project, targets[0]);
        }

        // One object: the whole placement applies to it. Several: they move together, then each snaps (on, wall).
        SvJsonObject placement = single
            ? Subset(op, ["near", "dist", "offset", "pivot", "wall", "gap", "reach", "on", "lift"], to is null ? null : ("at", to))
            : Subset(op, ["wall", "gap", "reach", "on", "lift"], null);
        // The axes the move changed (all three for to/at): stick's default; and where the objects were (their own baked
        // copies stay there).
        bool[] moved = to is null ? new bool[3] : [true, true, true];
        SvJsonNode? stickNode = op["stick"] is { } sn && !ApiJson.IsNull(sn) ? sn : null;
        (Vec3 Min, Vec3 Max)? before = stickNode is null ? null : StickStart(ctx, targets);
        foreach (ObjectRef r in targets)
        {
            Vec3 origin = MapObjects.Origin(ctx.Project, r);
            (Vec3 min, Vec3 max) = MapObjects.Bounds(ctx.Project, r);
            Vec3 start = origin + (single ? by : groupDelta);
            Placed p = Placement.Resolve(ctx, placement, start, (min - origin, max - origin), r.Kind == ObjectKind.Brush ? r.Id : null);

            Vec3 delta = p.Location - origin;
            for (int a = 0; a < 3; a++)
            {
                moved[a] |= Math.Abs(delta[a]) > 1e-9;
            }

            // A placed actor turns to face out of the wall it was put on; brushes and volumes keep their footprint.
            Actor? actor = r.Kind == ObjectKind.Actor ? ctx.Project.FindActor(r.Id) : null;
            if (actor is not null)
            {
                WarnUnknownHeight(ctx, placement, actor, max.Z - min.Z);
            }

            bool turns = actor is { Shape: null } && !ApiJson.GetBool(op, "keepRot", false);
            int? yaw = turns && p.WallNormal is Vec3 n ? RotatorMath.YawOf(n) : null;
            ctx.Edit([r], $"Move {r.Id}",
                brush: x => x.Location += delta,
                actor: x =>
                {
                    x.Location += delta;
                    if (yaw is int y)
                    {
                        x.Rotation = x.Rotation with { Yaw = y };
                    }
                },
                ge: x => x.Points = x.Points.Select(pt => pt + delta).ToList());
        }

        if (stickNode is not null)
        {
            Stick(ctx, stickNode, targets, moved, before);
        }
    }

    /// <summary>rotate: <c>to</c>/<c>toDeg</c> (absolute), <c>by</c>/<c>byDeg</c> (world-space delta, optionally
    /// <c>around</c> "center" of the group or a point, which also swings positions), or <c>face</c> a point.</summary>
    private static void Rotate(ChangeContext ctx, SvJsonObject op)
    {
        List<ObjectRef> targets = ctx.Targets(op);
        if (targets.Count == 0)
        {
            return;
        }

        const double PerDegree = Rot.UnitsPerTurn / 360.0;
        Rot? to = op["to"] is { } t ? Read(t, "to", 1) : op["toDeg"] is { } td ? Read(td, "toDeg", PerDegree) : null;
        Rot? by = op["by"] is { } b ? Read(b, "by", 1) : op["byDeg"] is { } bd ? Read(bd, "byDeg", PerDegree) : null;
        Vec3? face = op["face"] is { } f ? ctx.Point(f, "face") : null;
        if ((to is null ? 0 : 1) + (by is null ? 0 : 1) + (face is null ? 0 : 1) != 1)
        {
            throw new ApiException("give one of to, toDeg, by, byDeg or face", "by");
        }

        Vec3? pivot = null;
        if (op["around"] is { } around)
        {
            if (by is null)
            {
                throw new ApiException("around goes with by/byDeg", "around");
            }

            pivot = around is SvJsonString { Value: "center" }
                ? ChangeContext.Center(targets.Select(r => MapObjects.Bounds(ctx.Project, r)).Aggregate((x, y) => (Vec3.Min(x.Min, y.Min), Vec3.Max(x.Max, y.Max))))
                : ctx.Point(around, "around");
        }

        foreach (ObjectRef r in targets)
        {
            Vec3 Swing(Vec3 p) => pivot is Vec3 c && by is Rot d ? c + d.Rotate(p - c) : p;
            Rot Turn(Rot current, Vec3 at) => RotatorMath.Tidy(to ?? (by is Rot d ? RotatorMath.Compose(current, d) : RotatorMath.LookAt(at, face!.Value)));
            ctx.Edit([r], $"Rotate {r.Id}",
                brush: x =>
                {
                    x.Location = Swing(x.Location);
                    x.Rotation = Turn(x.Rotation, x.Location);
                },
                actor: x =>
                {
                    x.Location = Swing(x.Location);
                    x.Rotation = Turn(x.Rotation, x.Location);
                },
                ge: x =>
                {
                    if (by is not Rot d)
                    {
                        throw new ApiException("GEs turn with by/byDeg only", "to");
                    }

                    Vec3 c = pivot ?? ChangeContext.Center((x.Points.Aggregate(Vec3.Min), x.Points.Aggregate(Vec3.Max)));
                    x.Points = x.Points.Select(p => c + d.Rotate(p - c)).ToList();
                });
        }

        static Rot Read(SvJsonNode node, string field, double scale) =>
            ApiJson.TryRot(node, out Rot r, scale) ? r : throw new ApiException("expected [pitch,yaw,roll]", field);
    }

    /// <summary>scale: <c>to</c> (a number = uniform DrawScale / MainScale, or [x,y,z]) or <c>by</c> (multiplies). Brushes
    /// keep their texture size unless <c>keepTextures</c> is false (then textures stretch with the brush).</summary>
    private static void Scale(ChangeContext ctx, SvJsonObject op)
    {
        List<ObjectRef> targets = ctx.Targets(op);
        SvJsonNode? to = op["to"], by = op["by"];
        if ((to is null) == (by is null))
        {
            throw new ApiException("give to or by", "to");
        }

        string field = to is null ? "by" : "to";
        SvJsonNode value = (to ?? by)!;
        bool uniform = ApiJson.TryNumber(value, out double u);
        Vec3 v = uniform ? Vec3.One * u : ApiJson.RequireVec(value, field);
        bool keepTextures = ApiJson.GetBool(op, "keepTextures", true);
        foreach (ObjectRef r in targets)
        {
            if (r.Kind == ObjectKind.Ge)
            {
                ctx.Warn(field, $"{r.Id} is a GE and does not scale");
                continue;
            }

            ctx.Edit([r], $"Scale {r.Id}",
                brush: x => Geometry.TextureLock.Rescale(x, y => y.MainScale = to is null ? y.MainScale.Scale(v) : v, keepTextures),
                actor: x =>
                {
                    if (uniform)
                    {
                        x.DrawScale = to is null ? x.DrawScale * u : u;
                    }
                    else
                    {
                        x.DrawScale3D = to is null ? x.DrawScale3D.Scale(v) : v;
                    }
                });
        }
    }

    /// <summary>duplicate: <c>count</c> copies of each object, the k-th shifted by k*<c>by</c>. <c>as</c> binds
    /// symbols: a list (one per copy, in order) or one <c>$name</c> (then <c>$name#1</c>, <c>$name#2</c>...).</summary>
    private static void Duplicate(ChangeContext ctx, SvJsonObject op)
    {
        List<ObjectRef> targets = ctx.Targets(op);
        Vec3 by = op["by"] is { } b ? ApiJson.RequireVec(b, "by") : Vec3.Zero;
        int count = (int)ApiJson.Number(op, "count", 1);
        if (count is < 1 or > 1000)
        {
            throw new ApiException("count is 1..1000", "count");
        }

        List<string>? asList = op["as"] is SvJsonArray ? ApiJson.StringList(op["as"], "as") : null;
        string? asOne = op["as"] is SvJsonString s ? s.Value : null;
        CopyObjects(ctx, targets, by, count, asList, asOne);
    }

    /// <summary><paramref name="count"/> copies of each target, the k-th shifted by k*<paramref name="by"/>, with ids
    /// from <paramref name="asList"/> or <paramref name="asOne"/> (see <c>duplicate</c>).</summary>
    private static void CopyObjects(ChangeContext ctx, List<ObjectRef> targets, Vec3 by, int count, List<string>? asList = null, string? asOne = null)
    {
        int made = 0, bakedCopies = 0, carving = 0;
        for (int k = 1; k <= count; k++)
        {
            var copyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectRef r in targets)
            {
                string? symbol = asList is not null ? (made < asList.Count ? asList[made] : null) : asOne is null ? null : made == 0 ? asOne : $"{asOne}#{made}";
                string id = ctx.NewId(symbol, r.Id, "as");
                copyOf[r.Id] = id;
                Vec3 delta = by * k;
                switch (r.Kind)
                {
                    case ObjectKind.Actor:
                        Actor a = ctx.Project.FindActor(r.Id)!.Clone(id);
                        a.Location += delta;
                        MoverBase.Shift(a, delta);
                        a.Editor.UnlinkPrefab();
                        ctx.Stamp(a.Editor);
                        ctx.Add($"Duplicate {r.Id}", actors: [a]);
                        break;
                    case ObjectKind.Brush:
                        Brush br = ctx.Project.FindBrush(r.Id)!.Clone(id);
                        br.Location += delta;
                        br.Editor.UnlinkPrefab();
                        ctx.Stamp(br.Editor);
                        (int baked, int carves) = BakedContent.Unmark([br]);
                        bakedCopies += baked;
                        carving += carves;
                        ctx.Add($"Duplicate {r.Id}", brushes: [br]);
                        break;
                    default:
                        Ge g = ctx.Project.FindGe(r.Id)!.Clone(id);
                        g.Points = g.Points.Select(p => p + delta).ToList();
                        g.Editor.UnlinkPrefab();
                        ctx.Stamp(g.Editor);
                        ctx.Add($"Duplicate {r.Id}", ges: [g]);
                        break;
                }

                made++;
            }

            RepointPieces(ctx, copyOf);
        }

        if (BakedContent.CopyNote(ctx.Project, bakedCopies, carving) is { } note)
        {
            ctx.Warn("ids", note.Message);
        }
    }

    /// <summary>Copies of the brushes of a multi-brush piece form a piece of their own (named by the copy of its first
    /// brush); a copied part without its first brush becomes a standalone brush.</summary>
    private static void RepointPieces(ChangeContext ctx, Dictionary<string, string> copyOf)
    {
        string pieceKey = Geometry.Builders.BuilderArgs.PieceKey;
        List<ObjectRef> parts = copyOf.Values.Select(id => ctx.Project.FindBrush(id))
            .Where(b => b?.Shape.Builder?.Strings.ContainsKey(pieceKey) == true).Select(b => ObjectRef.Brush(b!.Id)).ToList();
        ctx.Edit(parts, "Duplicate piece", brush: b =>
        {
            Dictionary<string, string> strings = b.Shape.Builder!.Strings;
            if (copyOf.TryGetValue(strings[pieceKey], out string? piece))
            {
                strings[pieceKey] = piece;
            }
            else
            {
                strings.Remove(pieceKey);
            }
        });
    }

    private static void Rename(ChangeContext ctx, ObjectRef r, string newId)
    {
        if (string.Equals(r.Id, newId, StringComparison.Ordinal))
        {
            return;
        }

        if (newId.StartsWith('$'))
        {
            throw new ApiException("rename takes a real id, not a $symbol", "rename");
        }

        if (!string.Equals(r.Id, newId, StringComparison.OrdinalIgnoreCase))
        {
            _ = ctx.NewId(newId, newId, "rename"); // validates the name and that it is free
        }

        string old = r.Id;
        ctx.Edit([r], $"Rename {old}", brush: b => b.Id = newId, actor: a => a.Id = newId, ge: g => g.Id = newId);
        ctx.Renamed(old, newId);
        if (r.Kind == ObjectKind.Brush)
        {
            ctx.RekeyPieces(new HashSet<string>([old], StringComparer.OrdinalIgnoreCase));
        }
        foreach (string sym in ctx.Symbols.Where(kv => string.Equals(kv.Value, old, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
        {
            ctx.Symbols[sym] = newId;
        }

        List<ObjectRef> referencing = ctx.Project.Actors.Where(a => References(a.Props).Any(id => string.Equals(id, old, StringComparison.OrdinalIgnoreCase)))
            .Select(a => ObjectRef.Actor(a.Id)).ToList();
        ctx.Edit(referencing, $"Retarget {old}", actor: a =>
        {
            foreach (string key in a.Props.Keys.ToList())
            {
                a.Props[key] = Retarget(a.Props[key]!, old, newId);
            }
        });
    }

    /// <summary>Every <c>{"$actor": id}</c> inside a property set.</summary>
    internal static IEnumerable<string> References(PropSet props) => props.SelectMany(kv => References(kv.Value));

    internal static IEnumerable<string> References(SvJsonNode node)
    {
        switch (node)
        {
            case SvJsonObject o when PropValues.ActorId(o) is { } id:
                yield return id;
                break;
            case SvJsonObject o:
                foreach (KeyValuePair<string, SvJsonNode> kv in o)
                {
                    foreach (string id in References(kv.Value))
                    {
                        yield return id;
                    }
                }

                break;
            case SvJsonArray a:
                foreach (SvJsonNode item in a)
                {
                    foreach (string id in References(item))
                    {
                        yield return id;
                    }
                }

                break;
        }
    }

    private static SvJsonNode Retarget(SvJsonNode node, string old, string now)
    {
        switch (node)
        {
            case SvJsonObject o when PropValues.ActorId(o) is { } id:
                return string.Equals(id, old, StringComparison.OrdinalIgnoreCase) ? PropValues.ActorRef(now) : o;
            case SvJsonObject o:
                var copy = new SvJsonObject();
                foreach (KeyValuePair<string, SvJsonNode> kv in o)
                {
                    copy.Add(kv.Key, Retarget(kv.Value, old, now));
                }

                return copy;
            case SvJsonArray a:
                return new SvJsonArray(a.Select(x => Retarget(x, old, now)));
            default:
                return node;
        }
    }

    /// <summary>A copy of some members of an op (plus one extra member), for running placement with fewer fields.</summary>
    private static SvJsonObject Subset(SvJsonObject op, string[] keys, (string Key, SvJsonNode Value)? extra)
    {
        var o = new SvJsonObject();
        foreach (string k in keys)
        {
            if (op[k] is { } v)
            {
                o.Add(k, v);
            }
        }

        if (extra is (string key, SvJsonNode value))
        {
            o.Add(key, value);
        }

        return o;
    }
}
