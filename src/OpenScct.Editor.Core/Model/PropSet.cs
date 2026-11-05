using System.Diagnostics.CodeAnalysis;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Model;

/// <summary>
/// An ordered property bag of typed values, in SVMap's value conventions (spec/svmap/1.1 conventions.md): bools, integers,
/// float32 numbers with a decimal point, strings, structs as objects (<c>{"X":..}</c>), references as
/// <c>{"$actor": id}</c>, <c>{"$asset": "Pkg.Name"}</c> or <c>{"$class": ...}</c>, static arrays as
/// <c>{"$elements": {"i": v}}</c> and dynamic arrays as arrays. Like the original map, it holds only what differs from
/// the class defaults. Keys match exactly; values are treated as immutable.
/// </summary>
public sealed class PropSet : IEnumerable<KeyValuePair<string, SvJsonNode>>
{
    private readonly List<KeyValuePair<string, SvJsonNode>> _items = new();

    public int Count => _items.Count;

    public IEnumerable<string> Keys => _items.Select(p => p.Key);

    public SvJsonNode? this[string key]
    {
        get => TryGet(key, out SvJsonNode? value) ? value : null;
        set
        {
            if (value is null)
            {
                Remove(key);
            }
            else
            {
                Set(key, value);
            }
        }
    }

    public bool Contains(string key) => IndexOf(key) >= 0;

    public bool TryGet(string key, [NotNullWhen(true)] out SvJsonNode? value)
    {
        int i = IndexOf(key);
        value = i >= 0 ? _items[i].Value : null;
        return i >= 0;
    }

    /// <summary>Sets a value, keeping the key's place when it exists.</summary>
    public void Set(string key, SvJsonNode value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        int i = IndexOf(key);
        if (i >= 0)
        {
            _items[i] = new(key, value);
        }
        else
        {
            _items.Add(new(key, value));
        }
    }

    public bool Remove(string key)
    {
        int i = IndexOf(key);
        if (i < 0)
        {
            return false;
        }

        _items.RemoveAt(i);
        return true;
    }

    public void Clear() => _items.Clear();

    public PropSet Clone()
    {
        var copy = new PropSet();
        copy._items.AddRange(_items);
        return copy;
    }

    public static PropSet FromJson(SvJsonNode? node)
    {
        var set = new PropSet();
        if (node is SvJsonObject obj)
        {
            foreach (KeyValuePair<string, SvJsonNode> p in obj)
            {
                set._items.Add(p);
            }
        }

        return set;
    }

    public SvJsonObject ToJson()
    {
        var obj = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> p in _items)
        {
            obj.Add(p.Key, p.Value);
        }

        return obj;
    }

    // Typed helpers. They return the fallback when the value is missing or of another type.

    public string? GetString(string key) => this[key] is SvJsonString s ? s.Value : null;

    public int GetInt(string key, int fallback = 0) => this[key] is SvJsonNumber n && n.TryGetInt32(out int v) ? v : fallback;

    public float GetFloat(string key, float fallback = 0) => this[key] switch
    {
        SvJsonNumber { IsFloat: true } n => n.FloatValue,
        SvJsonNumber n when n.TryGetInt64(out long l) => l,
        _ => fallback,
    };

    public bool GetBool(string key, bool fallback = false) => this[key] switch
    {
        SvJsonLiteral l when l.Kind == SvJsonKind.True => true,
        SvJsonLiteral l when l.Kind == SvJsonKind.False => false,
        _ => fallback,
    };

    public IEnumerator<KeyValuePair<string, SvJsonNode>> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private int IndexOf(string key)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Builds and reads SVMap-style typed JSON values.</summary>
public static class PropValues
{
    /// <summary>The key of a value an .sdc import could not type: <c>{"$raw": {"type", "hex"}}</c>, the stored bytes
    /// kept so nothing is lost. The compile and Map Check leave such values out.</summary>
    public const string RawKey = "$raw";

    public static bool IsRaw(SvJsonNode? value) => value is SvJsonObject { Count: 1 } o && o[0].Key == RawKey;

    public static SvJsonNode Vector(Vec3 v)
    {
        var o = new SvJsonObject();
        o.Add("X", SvJsonNode.Float((float)v.X));
        o.Add("Y", SvJsonNode.Float((float)v.Y));
        o.Add("Z", SvJsonNode.Float((float)v.Z));
        return o;
    }

    public static SvJsonNode Rotator(Rot r)
    {
        var o = new SvJsonObject();
        o.Add("Pitch", SvJsonNode.Integer(r.Pitch));
        o.Add("Roll", SvJsonNode.Integer(r.Roll));
        o.Add("Yaw", SvJsonNode.Integer(r.Yaw));
        return o;
    }

    public static SvJsonNode ActorRef(string id)
    {
        var o = new SvJsonObject();
        o.Add("$actor", SvJsonNode.String(id));
        return o;
    }

    public static SvJsonNode AssetRef(string path)
    {
        var o = new SvJsonObject();
        o.Add("$asset", SvJsonNode.String(path));
        return o;
    }

    public static SvJsonNode Array(IEnumerable<SvJsonNode> items) => new SvJsonArray(items);

    public static bool TryVector(SvJsonNode? node, out Vec3 v)
    {
        v = Vec3.Zero;
        if (node is not SvJsonObject o)
        {
            return false;
        }

        v = new Vec3(Num(o["X"]), Num(o["Y"]), Num(o["Z"]));
        return true;
    }

    public static bool TryRotator(SvJsonNode? node, out Rot r)
    {
        r = Rot.Zero;
        if (node is not SvJsonObject o)
        {
            return false;
        }

        r = new Rot((int)Num(o["Pitch"]), (int)Num(o["Yaw"]), (int)Num(o["Roll"]));
        return true;
    }

    /// <summary>The id of an <c>{"$actor": id}</c> value, else null.</summary>
    public static string? ActorId(SvJsonNode? node) => node is SvJsonObject o && o["$actor"] is SvJsonString s ? s.Value : null;

    /// <summary>The path of an <c>{"$asset": path}</c> value, else null.</summary>
    public static string? AssetPath(SvJsonNode? node) => node is SvJsonObject o && o["$asset"] is SvJsonString s ? s.Value : null;

    // The arms are cast to double: the natural type of float and long arms is float, which rounds large integers
    // (PolyFlags with the portal bit lost bit 0).
    public static double Num(SvJsonNode? node) => node switch
    {
        SvJsonNumber { IsFloat: true } n => (double)n.FloatValue,
        SvJsonNumber n when n.TryGetInt64(out long l) => (double)l,
        _ => 0,
    };
}
