using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop;

/// <summary>Value and naming rules the importers and exporters share.</summary>
public static class InteropValues
{
    /// <summary>The actor properties the model keeps as typed fields instead of in <see cref="Actor.Props"/>.</summary>
    public static readonly IReadOnlyList<string> TransformProps = ["Location", "Rotation", "DrawScale", "DrawScale3D", "PrePivot"];

    /// <summary>The id rule of SVMap actor ids and UE2 object names.</summary>
    public static bool IsValidName(string id) =>
        id.Length is > 0 and <= 63 && (char.IsAsciiLetter(id[0]) || id[0] == '_') && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Moves the transform properties of <paramref name="props"/> into the actor's typed fields.</summary>
    public static void TakeTransform(PropSet props, Actor a)
    {
        if (PropValues.TryVector(props["Location"], out Vec3 loc))
        {
            a.Location = loc;
        }

        if (PropValues.TryRotator(props["Rotation"], out Rot rot))
        {
            a.Rotation = rot;
        }

        if (props["DrawScale"] is SvJsonNumber s)
        {
            a.DrawScale = PropValues.Num(s);
        }

        if (props["DrawScale3D"] is SvJsonObject s3)
        {
            a.DrawScale3D = VectorOr(s3, Vec3.One);
        }

        if (PropValues.TryVector(props["PrePivot"], out Vec3 pivot))
        {
            a.PrePivot = pivot;
        }

        foreach (string key in TransformProps)
        {
            props.Remove(key);
        }
    }

    /// <summary>A vector from a possibly partial <c>{"X","Y","Z"}</c> struct, missing members from <paramref name="fallback"/>.</summary>
    public static Vec3 VectorOr(SvJsonObject o, Vec3 fallback) => new(
        o["X"] is { } x ? PropValues.Num(x) : fallback.X,
        o["Y"] is { } y ? PropValues.Num(y) : fallback.Y,
        o["Z"] is { } z ? PropValues.Num(z) : fallback.Z);

    /// <summary>
    /// Fills the members a partial struct leaves out from its default (UE2 export text writes only the members that
    /// differ from the property's default), recursively, so the value means what it meant in the editor.
    /// </summary>
    public static SvJsonNode Complete(SvJsonNode value, SvJsonNode? defaults)
    {
        if (value is not SvJsonObject o || defaults is not SvJsonObject d || o.ContainsKey("$actor") || o.ContainsKey("$asset")
            || o.ContainsKey("$class") || o.ContainsKey("$elements"))
        {
            return value;
        }

        var result = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            result.Add(kv.Key, Complete(kv.Value, d[kv.Key]));
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in d)
        {
            result.TryAdd(kv.Key, kv.Value);
        }

        return result;
    }

    /// <summary>The full class name: from the schema, else a guess from SCCT's naming (S... classes are SBase).</summary>
    public static string ResolveClass(UeSchema? schema, string name)
    {
        if (schema?.ResolveClass(name) is { } full)
        {
            return full;
        }

        if (name.Contains('.'))
        {
            return name;
        }

        bool sbase = name.Length > 1 && name[0] == 'S' && char.IsAsciiLetterUpper(name[1]);
        bool softBody = name.StartsWith("ESB", StringComparison.Ordinal);
        return (softBody ? "SoftBody." : sbase ? "SBase." : "Engine.") + name;
    }

    public static string ShortClass(string className) => className[(className.LastIndexOf('.') + 1)..];

    /// <summary>A value with its <c>{"$actor": id}</c> references renamed; ids not in <paramref name="renames"/> stay.</summary>
    public static SvJsonNode RenameActorRefs(SvJsonNode node, IReadOnlyDictionary<string, string> renames)
    {
        switch (node)
        {
            case SvJsonObject o when o["$actor"] is SvJsonString id:
                return renames.TryGetValue(id.Value, out string? to) ? PropValues.ActorRef(to) : node;
            case SvJsonObject o:
                var copy = new SvJsonObject();
                foreach (KeyValuePair<string, SvJsonNode> kv in o)
                {
                    copy.Add(kv.Key, RenameActorRefs(kv.Value, renames));
                }

                return copy;
            case SvJsonArray a:
                return new SvJsonArray(a.Select(x => RenameActorRefs(x, renames)));
            default:
                return node;
        }
    }

    /// <summary>Renames the <c>{"$actor": id}</c> references of every value of a property set in place.</summary>
    public static void RenameActorRefs(PropSet props, IReadOnlyDictionary<string, string> renames)
    {
        foreach (string key in props.Keys.ToList())
        {
            props.Set(key, RenameActorRefs(props[key]!, renames));
        }
    }

    /// <summary>A free id from <paramref name="id"/> (Stem0, Stem1... as <see cref="MapProject.NewId"/>), added to
    /// <paramref name="used"/>; the id itself when it is free.</summary>
    public static string FreeId(string id, HashSet<string> used)
    {
        if (used.Add(id))
        {
            return id;
        }

        string stem = id.TrimEnd("0123456789".ToCharArray());
        stem = stem.Length == 0 ? "Object" : stem;
        for (int i = 0; ; i++)
        {
            string candidate = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Compact canonical JSON text, for provenance strings.</summary>
    public static string Compact(SvJsonNode node) => CanonicalJson.ToCanonicalString(node);
}

/// <summary>Object references that ignore the case of the id, as UE2 names do.</summary>
public sealed class ObjectRefComparer : IEqualityComparer<ObjectRef>
{
    public static readonly ObjectRefComparer Instance = new();

    public bool Equals(ObjectRef x, ObjectRef y) => x.Kind == y.Kind && string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(ObjectRef r) => HashCode.Combine(r.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(r.Id));
}
