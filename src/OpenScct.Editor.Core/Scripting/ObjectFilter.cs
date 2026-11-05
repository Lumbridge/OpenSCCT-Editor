using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// Selects objects by any mix of: <c>kind</c>, <c>ids</c>, <c>class</c> (with subclasses unless <c>exact</c>), <c>tag</c>,
/// <c>event</c>, <c>near</c> + <c>radius</c>, <c>box</c>, <c>layer</c>, <c>folder</c> (prefix), <c>text</c>,
/// <c>has</c> (stored property names), <c>props</c> (stored values), <c>csg</c>, <c>author</c>, <c>prefab</c> (the parts of
/// an instance, or <c>true</c>/<c>false</c> for any instance or none). Every given test must pass.
/// </summary>
public sealed class ObjectFilter
{
    private static readonly string[] s_keys =
        ["kind", "ids", "class", "exact", "tag", "event", "near", "radius", "box", "layer", "folder", "text", "has", "props", "csg", "author", "prefab"];

    private readonly HashSet<ObjectKind>? _kinds;
    private readonly HashSet<string>? _ids;
    private readonly List<string>? _classes;
    private readonly bool _exact;
    private readonly string? _tag;
    private readonly string? _event;
    private readonly Vec3? _near;
    private readonly double _radius;
    private readonly (Vec3 Min, Vec3 Max)? _box;
    private readonly string? _layer;
    private readonly string? _folder;
    private readonly string? _text;
    private readonly List<string>? _has;
    private readonly SvJsonObject? _props;
    private readonly string? _csg;
    private readonly string? _author;
    private readonly string? _prefab;
    private readonly bool? _inPrefab;

    private ObjectFilter(SvJsonObject o, Func<SvJsonNode, string, Vec3> point, Func<string, string> resolveId, string field, bool query)
    {
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            // A query's own keys (paging, fields) sit beside its filters; a where filter has no paging.
            if (Array.IndexOf(s_keys, kv.Key) < 0 && !(query && kv.Key is "q" or "fields" or "limit" or "offset" or "rows" or "sort" or "by"))
            {
                throw new ApiException($"unknown filter key '{kv.Key}' (filters: {string.Join(", ", s_keys)})", $"{field}.{kv.Key}");
            }
        }

        if (ApiJson.StringList(o["kind"], field + ".kind") is { } kinds)
        {
            _kinds = kinds.Select(k => k.ToLowerInvariant() switch
            {
                "actor" or "actors" => ObjectKind.Actor,
                "brush" or "brushes" => ObjectKind.Brush,
                "ge" or "ges" => ObjectKind.Ge,
                _ => throw new ApiException($"unknown kind '{k}' (actor, brush, ge)", field + ".kind"),
            }).ToHashSet();
        }

        if (ApiJson.StringList(o["ids"], field + ".ids") is { } ids)
        {
            _ids = ids.Select(resolveId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        _classes = ApiJson.StringList(o["class"], field + ".class");
        _exact = ApiJson.GetBool(o, "exact", false);
        _tag = ApiJson.GetString(o, "tag");
        _event = ApiJson.GetString(o, "event");
        if (o["near"] is { } near)
        {
            _near = point(near, field + ".near");
            _radius = ApiJson.Number(o, "radius", 512);
        }

        if (o["box"] is { } box)
        {
            if (box is not SvJsonArray { Count: 2 } b)
            {
                throw new ApiException("box is [[minX,minY,minZ],[maxX,maxY,maxZ]]", field + ".box");
            }

            Vec3 a = point(b[0], field + ".box[0]"), c = point(b[1], field + ".box[1]");
            _box = (Vec3.Min(a, c), Vec3.Max(a, c));
        }

        _layer = ApiJson.GetString(o, "layer");
        _folder = ApiJson.GetString(o, "folder");
        _text = ApiJson.GetString(o, "text");
        _has = ApiJson.StringList(o["has"], field + ".has");
        _props = o["props"] as SvJsonObject;
        _csg = ApiJson.GetString(o, "csg");
        _author = ApiJson.GetString(o, "author");
        switch (o["prefab"])
        {
            case null:
                break;
            case SvJsonString inst:
                _prefab = resolveId(inst.Value);
                break;
            case { Kind: SvJsonKind.True or SvJsonKind.False } flag:
                _inPrefab = flag.Kind == SvJsonKind.True;
                break;
            default:
                throw new ApiException("prefab is an instance id, or true/false (part of any instance or of none)", field + ".prefab");
        }
    }

    /// <summary>Parses a filter. <paramref name="point"/> resolves point specs (near, box); <paramref name="resolveId"/>
    /// turns symbolic ids into real ones.</summary>
    /// <summary>Reads a filter. <paramref name="query"/>: the object is a find/count query, so its paging and field
    /// keys are allowed beside the filters.</summary>
    public static ObjectFilter Parse(SvJsonObject o, Func<SvJsonNode, string, Vec3> point, Func<string, string>? resolveId = null,
        string field = "where", bool query = false) => new(o, point, resolveId ?? (s => s), field, query);

    public IEnumerable<ObjectRef> Select(MapProject p) => p.AllRefs().Where(r => Matches(p, r));

    public bool Matches(MapProject p, ObjectRef r)
    {
        if (_kinds is not null && !_kinds.Contains(r.Kind))
        {
            return false;
        }

        if (_ids is not null && !_ids.Contains(r.Id))
        {
            return false;
        }

        if (_classes is not null)
        {
            string cls = MapObjects.ClassOf(p, r);
            if (!_classes.Any(c => MapObjects.ClassMatches(cls, c, !_exact) || (r.Kind == ObjectKind.Ge && string.Equals(c, "GE", StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }
        }

        if (_tag is not null && !string.Equals(MapObjects.Tag(p, r), _tag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_event is not null && !string.Equals(MapObjects.Event(p, r), _event, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_near is Vec3 c0 && (MapObjects.Origin(p, r) - c0).Length > _radius)
        {
            return false;
        }

        if (_box is (Vec3 min, Vec3 max))
        {
            Vec3 o = MapObjects.Origin(p, r);
            if (o.X < min.X || o.Y < min.Y || o.Z < min.Z || o.X > max.X || o.Y > max.Y || o.Z > max.Z)
            {
                return false;
            }
        }

        EditorMeta? meta = p.MetaOf(r);
        if (_layer is not null && !LayerIs(meta?.Layer ?? "", _layer))
        {
            return false;
        }

        if (_folder is not null && !(meta?.Folder ?? "").StartsWith(_folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_author is not null && !string.Equals(meta?.Author, _author, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((_prefab is not null && !string.Equals(meta?.PrefabInstance, _prefab, StringComparison.OrdinalIgnoreCase))
            || (_inPrefab is bool inPrefab && (meta?.PrefabInstance is not null) != inPrefab))
        {
            return false;
        }

        if (_csg is not null && (r.Kind != ObjectKind.Brush || !string.Equals(p.FindBrush(r.Id)!.Oper == CsgOper.Add ? "add" : "subtract", _csg, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        PropSet? props = MapObjects.Props(p, r);
        if (_has is not null && (props is null || !_has.All(h => props.Contains(h))))
        {
            return false;
        }

        if (_props is not null && !PropsMatch(p, r))
        {
            return false;
        }

        return _text is null || TextMatches(p, r, meta, props);
    }

    /// <summary>The default layer is stored as "" and named "Default".</summary>
    public static bool LayerIs(string have, string want) =>
        string.Equals(have, want, StringComparison.OrdinalIgnoreCase)
        || (have.Length == 0 && string.Equals(want, "Default", StringComparison.OrdinalIgnoreCase));

    /// <summary>Compares effective values (stored, else the class default). With neither known, the UE2 zero default
    /// is assumed, so <c>{"TeamNumber":0}</c> also finds starts that never stored it.</summary>
    private bool PropsMatch(MapProject p, ObjectRef r)
    {
        foreach (KeyValuePair<string, SvJsonNode> kv in _props!)
        {
            SvJsonNode? have = MapObjects.EffectiveProp(p, r, kv.Key);
            if (have is null)
            {
                if (!IsZero(kv.Value))
                {
                    return false;
                }

                continue;
            }

            bool same = ApiJson.TryNumber(have, out double a) && ApiJson.TryNumber(kv.Value, out double b)
                ? Math.Abs(a - b) < 1e-4
                : have is SvJsonString hs && kv.Value is SvJsonString ws
                    ? string.Equals(hs.Value, ws.Value, StringComparison.OrdinalIgnoreCase)
                    : ApiJson.Write(have) == ApiJson.Write(kv.Value);
            if (!same)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsZero(SvJsonNode v) =>
        v.Kind is SvJsonKind.Null or SvJsonKind.False
        || (ApiJson.TryNumber(v, out double d) && d == 0)
        || v is SvJsonString { Value: "" or "None" };

    private bool TextMatches(MapProject p, ObjectRef r, EditorMeta? meta, PropSet? props)
    {
        bool Has(string? s) => s is not null && s.Contains(_text!, StringComparison.OrdinalIgnoreCase);
        if (Has(r.Id) || Has(MapObjects.ClassOf(p, r)) || Has(meta?.Note) || Has(meta?.Folder))
        {
            return true;
        }

        return props is not null && props.Any(kv => kv.Value is SvJsonString s && Has(s.Value)
            || PropValues.ActorId(kv.Value) is { } id && Has(id) || PropValues.AssetPath(kv.Value) is { } asset && Has(asset));
    }
}
