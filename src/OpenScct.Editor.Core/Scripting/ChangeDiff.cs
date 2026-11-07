using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// The compact diff of a change document: <c>created</c> (id → class/kind, location, size), <c>updated</c> (id →
/// changed field → [before, after], props and meta as <c>props.X</c>/<c>editor.X</c>, shapes as a face count),
/// <c>deleted</c> (ids), and <c>project</c> (changed map settings). Provenance stamps are left out.
/// </summary>
internal static class ChangeDiff
{
    public static SvJsonObject Build(ChangeContext ctx)
    {
        var created = new SvJsonObject();
        var updated = new SvJsonObject();
        var deleted = new List<string>();
        foreach ((string id, ObjectKind kind, SvJsonObject? before, SvJsonObject? after) in ctx.Changes())
        {
            if (before is null && after is null)
            {
                continue; // made and removed again in the same document
            }

            if (before is null)
            {
                created.TryAdd(id, Created(ctx.Project, new ObjectRef(kind, id)));
            }
            else if (after is null)
            {
                deleted.Add(ApiJson.GetString(before, "id") ?? id);
            }
            else
            {
                var fields = new SvJsonObject();
                Fields("", before, after, fields, 0);
                if (fields.Count > 0)
                {
                    string oldId = ApiJson.GetString(before, "id") ?? id;
                    updated.TryAdd(oldId, fields);
                }
            }
        }

        var o = new SvJsonObject();
        if (created.Count > 0)
        {
            o.Add("created", created);
        }

        if (updated.Count > 0)
        {
            o.Add("updated", updated);
        }

        if (deleted.Count > 0)
        {
            o.Add("deleted", ApiJson.Strings(deleted));
        }

        if (ctx.ProjectBefore is { } pb)
        {
            var fields = new SvJsonObject();
            Fields("", pb, ChangeContext.ProjectJson(ctx.Project), fields, 0);
            if (fields.Count > 0)
            {
                o.Add("project", fields);
            }
        }

        return o;
    }

    /// <summary>A short description of a new object.</summary>
    public static SvJsonObject Created(MapProject p, ObjectRef r)
    {
        switch (r.Kind)
        {
            case ObjectKind.Actor:
            {
                Actor a = p.FindActor(r.Id)!;
                return ApiJson.Obj(("class", ApiJson.Str(a.Class)), ("loc", ApiJson.Vec(a.Location)),
                    ("rot", a.Rotation == Rot.Zero ? null : ApiJson.Rotator(a.Rotation)),
                    ("props", a.Props.Count == 0 ? null : a.Props.ToJson()));
            }

            case ObjectKind.Brush:
                case ObjectKind.Stair:
            {
                Brush b = p.FindBrush(r.Id)!;
                (Vec3 min, Vec3 max) = MapObjects.BrushBounds(b);
                return ApiJson.Obj(("brush", ApiJson.Str(b.Oper == CsgOper.Add ? "add" : "subtract")), ("loc", ApiJson.Vec(b.Location)),
                    ("min", ApiJson.Vec(min)), ("max", ApiJson.Vec(max)),
                    ("builder", b.Shape.Builder is null ? null : ApiJson.Str(b.Shape.Builder.Kind)));
            }

            default:
            {
                Ge g = p.FindGe(r.Id)!;
                return ApiJson.Obj(("ge", ApiJson.Str(g.Kind)), ("points", ApiJson.Arr(g.Points.Select(ApiJson.Vec))),
                    ("appearance", Project.ModelJson.GeToJson(g)["appearance"]));
            }
        }
    }

    private static void Fields(string prefix, SvJsonObject before, SvJsonObject after, SvJsonObject into, int depth)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SvJsonNode> kv in before)
        {
            keys.Add(kv.Key);
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in after)
        {
            keys.Add(kv.Key);
        }

        foreach (string key in keys)
        {
            string path = prefix + key;
            if (path is "editor.author" or "editor.op")
            {
                continue;
            }

            SvJsonNode? a = before[key], b = after[key];
            if (a is not null && b is not null && ApiJson.Write(a) == ApiJson.Write(b))
            {
                continue;
            }

            if (key == "shape" && depth == 0)
            {
                into.Add(path, ApiJson.Arr([ShapeSummary(a), ShapeSummary(b)]));
                continue;
            }

            if ((a is SvJsonObject || b is SvJsonObject) && (a is null or SvJsonObject) && (b is null or SvJsonObject) && depth < 2
                && !IsValueObject(a) && !IsValueObject(b))
            {
                Fields(path + ".", a as SvJsonObject ?? new SvJsonObject(), b as SvJsonObject ?? new SvJsonObject(), into, depth + 1);
                continue;
            }

            // Model fields (loc, rot, points...) print short; stored values keep their exact type (1.0 is a float).
            Func<SvJsonNode, SvJsonNode> show = depth == 0 ? Compact : x => x;
            into.Add(path, ApiJson.Arr([a is null ? SvJsonNode.Null : show(a), b is null ? SvJsonNode.Null : show(b)]));
        }
    }

    /// <summary>Objects that are one value (vectors, references), compared whole rather than member by member.</summary>
    private static bool IsValueObject(SvJsonNode? n) =>
        n is SvJsonObject o && (o["X"] is not null || o["Pitch"] is not null || o["$actor"] is not null || o["$asset"] is not null
            || o["$class"] is not null || o["R"] is not null);

    private static SvJsonNode ShapeSummary(SvJsonNode? shape)
    {
        if (shape is not SvJsonObject s)
        {
            return SvJsonNode.Null;
        }

        int faces = s["faces"] is SvJsonArray f ? f.Count : 0;
        return ApiJson.Obj(("faces", SvJsonNode.Integer(faces)), ("builder", s["builder"] is { } b ? Compact(b) : null));
    }

    /// <summary>The value with whole floats as integers (shorter; the stored form is unchanged).</summary>
    public static SvJsonNode Compact(SvJsonNode node) => node switch
    {
        SvJsonNumber { IsFloat: true } n => ApiJson.Num(n.FloatValue),
        SvJsonArray a => new SvJsonArray(a.Select(Compact)),
        SvJsonObject o => CompactObject(o),
        _ => node,
    };

    private static SvJsonObject CompactObject(SvJsonObject o)
    {
        var c = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            c.Add(kv.Key, Compact(kv.Value));
        }

        return c;
    }
}
