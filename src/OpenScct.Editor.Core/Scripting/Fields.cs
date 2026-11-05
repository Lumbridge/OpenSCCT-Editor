using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// Named fields of an object for query output. Model fields: id, kind, class, short, loc, rot, scale, scale3d,
/// postScale, prePivot, tag, event, props, layer, folder, hidden, locked, note, author, op, src, bounds, size, center,
/// csg, flags, builder, faces, mats, shape, points, mask, platform, appearance, refs. Any other name (or <c>props.Name</c>) is a
/// property: the stored value, else the class default.
/// </summary>
internal static class Fields
{
    /// <summary>The columns of <c>"*"</c> in rows mode.</summary>
    public static readonly string[] Common = ["id", "class", "loc", "rot", "tag", "event", "layer"];

    /// <summary>The fields <c>"*"</c> shows for one object in object mode: everything not at its default.</summary>
    public static IEnumerable<string> All(MapProject p, ObjectRef r)
    {
        yield return "id";
        yield return "class";
        switch (r.Kind)
        {
            case ObjectKind.Actor:
                Actor a = p.FindActor(r.Id)!;
                yield return "loc";
                if (a.Rotation != Rot.Zero) yield return "rot";
                if (a.DrawScale != 1) yield return "scale";
                if (a.DrawScale3D != Vec3.One) yield return "scale3d";
                if (a.PrePivot != Vec3.Zero) yield return "prePivot";
                if (a.Props.Count > 0) yield return "props";
                if (a.Match is { IsEmpty: false }) yield return "match";
                if (a.Legs is not null) yield return "legs";
                if (a.Shape is not null)
                {
                    yield return "builder";
                    yield return "bounds";
                }

                break;
            case ObjectKind.Brush:
                Brush b = p.FindBrush(r.Id)!;
                yield return "csg";
                if (b.PolyFlags != 0) yield return "flags";
                yield return "loc";
                if (b.Rotation != Rot.Zero) yield return "rot";
                if (b.MainScale != Vec3.One) yield return "scale";
                if (b.PostScale != Vec3.One) yield return "postScale";
                if (b.PrePivot != Vec3.Zero) yield return "prePivot";
                yield return "builder";
                yield return "faces";
                yield return "mats";
                yield return "bounds";
                if (b.Props.Count > 0) yield return "props";
                break;
            default:
                Ge g = p.FindGe(r.Id)!;
                yield return "points";
                if (g.Mask != 0) yield return "mask";
                if (g.Platform != 0) yield return "platform";
                if (g.Flags != 0) yield return "flags";
                if (g.Appearance is not null) yield return "appearance";
                break;
        }

        EditorMeta m = p.MetaOf(r)!;
        if (m.Layer.Length > 0) yield return "layer";
        if (m.Folder.Length > 0) yield return "folder";
        if (m.Hidden) yield return "hidden";
        if (m.Locked) yield return "locked";
        if (m.Note is not null) yield return "note";
        if (m.Author is not null) yield return "author";
        if (m.Op is not null) yield return "op";
        if (m.PrefabInstance is not null) yield return "prefab";
    }

    public static SvJsonNode? Get(MapProject p, ObjectRef r, string field)
    {
        Actor? a = r.Kind == ObjectKind.Actor ? p.FindActor(r.Id) : null;
        Brush? b = r.Kind == ObjectKind.Brush ? p.FindBrush(r.Id) : null;
        Ge? g = r.Kind == ObjectKind.Ge ? p.FindGe(r.Id) : null;
        EditorMeta? m = p.MetaOf(r);
        if (m is null)
        {
            return null;
        }

        switch (field)
        {
            case "id": return ApiJson.Str(r.Id);
            case "kind": return ApiJson.Str(MapObjects.KindName(r.Kind));
            case "class": return ApiJson.Str(a?.Class ?? (b is not null ? "Brush" : "GE." + g!.Kind));
            case "short": return ApiJson.Str(a?.ShortClass ?? (b is not null ? "Brush" : g!.Kind));
            case "loc": return ApiJson.Vec(MapObjects.Origin(p, r));
            case "rot": return a is not null ? ApiJson.Rotator(a.Rotation) : b is not null ? ApiJson.Rotator(b.Rotation) : null;
            case "scale": return a is not null ? ApiJson.Num(a.DrawScale) : b is not null ? ApiJson.Vec(b.MainScale) : null;
            case "scale3d": return a is null ? null : ApiJson.Vec(a.DrawScale3D);
            case "postScale": return b is null ? null : ApiJson.Vec(b.PostScale);
            case "prePivot": return a is not null ? ApiJson.Vec(a.PrePivot) : b is not null ? ApiJson.Vec(b.PrePivot) : null;
            case "tag": return MapObjects.Tag(p, r) is { } t ? ApiJson.Str(t) : null;
            case "event": return MapObjects.Event(p, r) is { } e ? ApiJson.Str(e) : null;
            case "props": return MapObjects.Props(p, r)?.ToJson();
            case "layer": return ApiJson.Str(m.Layer.Length > 0 ? m.Layer : "Default");
            case "folder": return ApiJson.Str(m.Folder);
            case "hidden": return ApiJson.Bool(m.Hidden);
            case "locked": return ApiJson.Bool(m.Locked);
            case "note": return m.Note is null ? null : ApiJson.Str(m.Note);
            case "author": return m.Author is null ? null : ApiJson.Str(m.Author);
            case "op": return m.Op is null ? null : ApiJson.Str(m.Op);
            case "prefab": return m.PrefabInstance is null ? null : ApiJson.Str(m.PrefabInstance);
            case "src": return (a?.Source ?? b?.Source ?? g?.Source) is { } src ? ApiJson.Str(src) : null;
            case "bounds":
            {
                (Vec3 min, Vec3 max) = MapObjects.Bounds(p, r);
                return ApiJson.Arr([ApiJson.Vec(min), ApiJson.Vec(max)]);
            }

            case "size":
            {
                (Vec3 min, Vec3 max) = MapObjects.Bounds(p, r);
                return ApiJson.Vec(max - min);
            }

            case "center": return ApiJson.Vec(ChangeContext.Center(MapObjects.Bounds(p, r)));
            case "csg": return b is null ? null : ApiJson.Str(b.Oper == CsgOper.Add ? "add" : "subtract");
            case "flags":
                return b is not null ? ApiJson.Strings(BrushSpecs.FlagNames(b.PolyFlags)) : g is not null ? SvJsonNode.Integer(g.Flags) : null;
            case "builder":
                BrushShape? shape = b?.Shape ?? a?.Shape;
                return shape?.Builder is { } spec ? ChangeDiff.Compact(ModelJson.BuilderToJson(spec)) : null;
            case "faces": return (b?.Shape ?? a?.Shape) is { } fs ? SvJsonNode.Integer(fs.Faces.Count) : null;
            case "mats":
                return (b?.Shape ?? a?.Shape) is { } ms ? ApiJson.Strings(ms.Faces.Select(f => f.Material ?? "").Distinct(StringComparer.Ordinal)) : null;
            case "shape": return (b?.Shape ?? a?.Shape) is { } full ? ChangeDiff.Compact(ModelJson.ShapeToJson(full)) : null;
            case "points": return g is null ? null : ApiJson.Arr(g.Points.Select(ApiJson.Vec));
            case "mask": return g is null ? null : SvJsonNode.Integer(g.Mask);
            case "platform": return g is null ? null : SvJsonNode.Integer(g.Platform);
            case "appearance": return g is null ? null : ModelJson.GeToJson(g)["appearance"];
            case "match": return a?.Match is { IsEmpty: false } match ? Project.MatchJson.ToJson(match) : null;
            case "legs": return a?.Legs is { } legs ? Project.MoverLegsJson.ToJson(legs) : null;
            case "refs": return a is null ? null : ApiJson.Strings(ChangeApplier.References(a.Props).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        string prop = field.StartsWith("props.", StringComparison.Ordinal) ? field[6..] : field;
        return MapObjects.EffectiveProp(p, r, prop);
    }

    public static string SortKey(MapProject p, ObjectRef r, string field) =>
        Get(p, r, field) switch
        {
            null => "",
            SvJsonString s => s.Value,
            SvJsonNumber n when ApiJson.TryNumber(n, out double d) => (d + 1e9).ToString("000000000000.000", System.Globalization.CultureInfo.InvariantCulture),
            { } other => ApiJson.Write(other),
        };
}
