using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// Turns the loose values a script writes into the stored SVMap value form, checked against the class schema when the
/// class catalog is loaded: <c>1</c> for a float becomes <c>1.0</c>, <c>[x,y,z]</c> for a vector struct becomes
/// <c>{"X":..}</c>, an enum value name becomes its index, an actor id becomes <c>{"$actor": id}</c>. Unknown property
/// names are errors with "did you mean" suggestions, so a typo never silently does nothing.
/// </summary>
public sealed class PropCoercion
{
    private readonly IClassCatalog? _catalog;
    private readonly Func<string, string?> _actorId;

    /// <param name="actorId">Resolves a string to an actor id (symbols included), or null when it is not an actor.</param>
    public PropCoercion(IClassCatalog? catalog, Func<string, string?> actorId)
    {
        _catalog = catalog;
        _actorId = actorId;
    }

    /// <summary>The stored name and value of <paramref name="key"/> (which may be <c>Name[i]</c> for a static array
    /// element) on <paramref name="className"/>. Returns the value to store (null = remove).</summary>
    public (string Name, SvJsonNode? Value) Coerce(string className, string key, SvJsonNode value, PropSet current, string field,
        List<string> warnings)
    {
        (string name, int? index) = SplitIndex(key, field);
        PropInfo? info = Lookup(className, name, field, warnings);
        string stored = info?.Name ?? name;
        if (index is int i)
        {
            if (info is not null && info.ArrayDim <= 1)
            {
                throw new ApiException($"{stored} is not a static array", field);
            }

            var elements = new SvJsonObject();
            SvJsonObject? old = current[stored] is SvJsonObject o && o["$elements"] is SvJsonObject e ? e : null;
            string idx = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (KeyValuePair<string, SvJsonNode> kv in old ?? new SvJsonObject())
            {
                if (kv.Key != idx)
                {
                    elements.Add(kv.Key, kv.Value);
                }
            }

            if (!ApiJson.IsNull(value))
            {
                elements.Add(idx, Convert(info, value, field, elementOfArray: true));
            }

            return (stored, elements.Count == 0 ? null : new SvJsonObject { { "$elements", elements } });
        }

        if (ApiJson.IsNull(value))
        {
            return (stored, null);
        }

        SvJsonNode converted = Convert(info, value, field, elementOfArray: false);
        return (stored, converted.Kind == SvJsonKind.Null ? null : converted);
    }

    /// <summary>The schema of a property, or null without a catalog / for an unknown class (a warning is added then).</summary>
    public PropInfo? Lookup(string className, string name, string field, List<string> warnings)
    {
        if (_catalog is null || _catalog.Find(className) is null)
        {
            return null;
        }

        IReadOnlyList<PropInfo> all = _catalog.AllProperties(className);
        PropInfo? info = all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (info is null)
        {
            IEnumerable<string> close = all.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => (Name: n, Score: Distance(n.ToLowerInvariant(), name.ToLowerInvariant())))
                .Where(x => x.Score <= Math.Max(2, name.Length / 3) || x.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Score).ThenBy(x => x.Name, StringComparer.Ordinal).Take(3).Select(x => x.Name);
            string hint = string.Join(", ", close);
            throw new ApiException($"{MapObjects.Short(className)} has no property '{name}'" + (hint.Length > 0 ? $"; did you mean {hint}?" : ""), field);
        }

        if (!info.Editable)
        {
            warnings.Add($"{field}: {info.Name} is not editable in the original editor");
        }

        if (info.Type == "delegate")
        {
            throw new ApiException($"{info.Name} is a delegate and cannot be set", field);
        }

        return info;
    }

    private SvJsonNode Convert(PropInfo? info, SvJsonNode value, string field, bool elementOfArray)
    {
        if (info is null)
        {
            return Loose(value);
        }

        string type = info.Type;
        if (info.ArrayDim > 1 && !elementOfArray)
        {
            // A whole static array: {"0": v, "2": v} or [v0, v1, ...].
            var elements = new SvJsonObject();
            if (value is SvJsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    if (!ApiJson.IsNull(arr[i]))
                    {
                        elements.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture), Scalar(info, type, arr[i], $"{field}[{i}]"));
                    }
                }
            }
            else if (value is SvJsonObject o)
            {
                SvJsonObject src = o["$elements"] as SvJsonObject ?? o;
                foreach (KeyValuePair<string, SvJsonNode> kv in src)
                {
                    elements.Add(kv.Key, Scalar(info, type, kv.Value, $"{field}[{kv.Key}]"));
                }
            }
            else
            {
                throw new ApiException($"{info.Name} is a static array of {info.ArrayDim}: give [v0, v1, ...] or set \"{info.Name}[i]\"", field);
            }

            return new SvJsonObject { { "$elements", elements } };
        }

        if (type == "array")
        {
            if (value is not SvJsonArray list)
            {
                throw new ApiException($"{info.Name} is a dynamic array: give a JSON array", field);
            }

            var inner = info with { Type = info.Inner ?? "", ArrayDim = 1 };
            return new SvJsonArray(list.Select((v, i) => inner.Type.Length == 0 ? Loose(v) : Scalar(inner, inner.Type, v, $"{field}[{i}]")));
        }

        return Scalar(info, type, value, field);
    }

    private SvJsonNode Scalar(PropInfo info, string type, SvJsonNode value, string field)
    {
        switch (type)
        {
            case "bool":
                return value.Kind switch
                {
                    SvJsonKind.True or SvJsonKind.False => value,
                    _ when ApiJson.TryNumber(value, out double b) => SvJsonNode.Bool(b != 0),
                    _ => throw new ApiException($"{info.Name} is a bool", field),
                };
            case "int":
            case "byte":
                if (ApiJson.TryNumber(value, out double n))
                {
                    if (Math.Abs(n - Math.Round(n)) > 1e-6)
                    {
                        throw new ApiException($"{info.Name} is an integer ({type})", field);
                    }

                    if (type == "byte" && (n < 0 || n > 255))
                    {
                        throw new ApiException($"{info.Name} is a byte (0..255)", field);
                    }

                    return SvJsonNode.Integer((long)Math.Round(n));
                }

                if (value is SvJsonString es && info.Enum is { } enumName)
                {
                    IReadOnlyList<string>? names = _catalog?.EnumValues(enumName);
                    int index = names is null ? -1 : FindIndex(names, es.Value);
                    if (index < 0)
                    {
                        throw new ApiException(names is null
                            ? $"{info.Name} is the enum {enumName}; give its number"
                            : $"'{es.Value}' is not a value of {enumName} ({string.Join(", ", names.Take(12))}{(names.Count > 12 ? ", ..." : "")})", field);
                    }

                    return SvJsonNode.Integer(index);
                }

                throw new ApiException($"{info.Name} is {(info.Enum is null ? "an integer" : "the enum " + info.Enum)}", field);
            case "float":
                return ApiJson.TryNumber(value, out double f)
                    ? SvJsonNode.Float((float)f)
                    : throw new ApiException($"{info.Name} is a float", field);
            case "name":
            case "string":
                return value is SvJsonString ? value : throw new ApiException($"{info.Name} is a {type}: give a string", field);
            case "struct":
                return Struct(info, value, field);
            case "object":
                return ObjectRef(info, value, field);
            case "class":
                return value is SvJsonString cs
                    ? new SvJsonObject { { "$class", SvJsonNode.String(_catalog?.Find(cs.Value)?.Name ?? cs.Value) } }
                    : value is SvJsonObject ? value : throw new ApiException($"{info.Name} is a class reference: give a class name", field);
            default:
                return Loose(value);
        }
    }

    private static SvJsonNode Struct(PropInfo info, SvJsonNode value, string field)
    {
        string s = info.Struct is null ? "" : MapObjects.Short(info.Struct);
        if (value is SvJsonArray a)
        {
            switch (s)
            {
                case "Vector" when ApiJson.TryVec(a, out Vec3 v):
                    return PropValues.Vector(v);
                case "Rotator" when ApiJson.TryRot(a, out Rot r):
                    return PropValues.Rotator(r);
                case "Color" when a.Count is 3 or 4:
                    var c = new SvJsonObject();
                    string[] keys = ["R", "G", "B", "A"];
                    for (int i = 0; i < 4; i++)
                    {
                        double ch = i < a.Count ? ApiJson.RequireNumber(a[i], $"{field}[{i}]") : 255;
                        c.Add(keys[i], SvJsonNode.Integer((long)Math.Clamp(Math.Round(ch), 0, 255)));
                    }

                    return c;
            }

            throw new ApiException($"{info.Name} is a {s} struct: give an object" + (s is "Vector" or "Rotator" ? " or [a,b,c]" : s == "Color" ? " or [r,g,b,a]" : ""), field);
        }

        return value is SvJsonObject ? value : throw new ApiException($"{info.Name} is a {s} struct: give an object", field);
    }

    private SvJsonNode ObjectRef(PropInfo info, SvJsonNode value, string field)
    {
        if (value is SvJsonObject)
        {
            return value;
        }

        if (value is not SvJsonString s)
        {
            throw new ApiException($"{info.Name} is an object reference: give an actor id or an asset path", field);
        }

        if (s.Value is "None" or "")
        {
            return SvJsonNode.Null;
        }

        bool actorClass = info.ObjectClass is { } oc && _catalog is not null && _catalog.Find(oc) is not null && _catalog.IsA(oc, "Engine.Actor");
        if (_actorId(s.Value) is { } id)
        {
            return PropValues.ActorRef(id);
        }

        if (actorClass)
        {
            throw new ApiException($"{info.Name} references an actor ({MapObjects.Short(info.ObjectClass!)}); '{s.Value}' is not an actor in the map", field);
        }

        return PropValues.AssetRef(s.Value);
    }

    /// <summary>Without a schema: numbers, strings and bools are kept; a "$id" string becomes an actor reference.</summary>
    private SvJsonNode Loose(SvJsonNode value) =>
        value is SvJsonString s && s.Value.StartsWith('$') && _actorId(s.Value) is { } id ? PropValues.ActorRef(id) : value;

    private static int FindIndex(IReadOnlyList<string> names, string value)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.TryParse(value, out int n) && n >= 0 && n < names.Count ? n : -1;
    }

    private static (string Name, int? Index) SplitIndex(string key, string field)
    {
        int open = key.IndexOf('[', StringComparison.Ordinal);
        if (open < 0)
        {
            return (key, null);
        }

        if (!key.EndsWith(']') || !int.TryParse(key[(open + 1)..^1], out int i) || i < 0)
        {
            throw new ApiException($"bad element key '{key}' (use Name[index])", field);
        }

        return (key[..open], i);
    }

    private static int Distance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            d[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }

        return d[b.Length];
    }
}
