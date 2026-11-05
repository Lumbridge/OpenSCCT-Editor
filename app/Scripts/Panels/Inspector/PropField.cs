using System.Globalization;
using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Panels;

/// <summary>How the inspector edits a property.</summary>
internal enum PropKind
{
    Bool,
    Int,
    Float,
    Text,
    Enum,
    Vector,
    Rotator,
    Color,
    ActorRef,
    AssetRef,
    ClassRef,

    /// <summary>Anything else (arrays, other structs): edited as compact JSON.</summary>
    Raw,
}

/// <summary>A row of the property grid: the property, how to edit it, and where it is grouped.</summary>
internal sealed record PropField(string Name, PropKind Kind, string Category, PropInfo? Info, IReadOnlyList<string>? EnumValues)
{
    public string TypeText => Info is null
        ? Kind.ToString().ToLowerInvariant()
        : Info.Type + (Info.Enum ?? Info.Struct ?? Info.ObjectClass ?? Info.Inner) switch
        {
            null => "",
            { } detail => " " + detail,
        } + (Info.ArrayDim > 1 ? $"[{Info.ArrayDim}]" : "");

    /// <summary>The kind of a schema property, or null when the inspector does not show it (delegates).</summary>
    public static PropKind? KindOf(PropInfo info, IClassCatalog? catalog)
    {
        string type = info.Type.ToLowerInvariant();
        if (info.ArrayDim > 1 || type == "array")
        {
            return PropKind.Raw;
        }

        return type switch
        {
            "delegate" => null,
            "bool" => PropKind.Bool,
            "int" => PropKind.Int,
            "float" => PropKind.Float,
            "byte" => EnumTable.Values(info.Enum) is { Count: > 0 } ? PropKind.Enum : PropKind.Int,
            "string" or "str" or "name" => PropKind.Text,
            "struct" => StructKind(info.Struct),
            "class" => PropKind.ClassRef,
            "object" => IsActorClass(info.ObjectClass, catalog) ? PropKind.ActorRef : PropKind.AssetRef,
            _ => PropKind.Raw,
        };
    }

    /// <summary>The kind of a value with no schema, from its JSON shape.</summary>
    public static PropKind KindOf(SvJsonNode? value) => value switch
    {
        SvJsonLiteral { Kind: SvJsonKind.True or SvJsonKind.False } => PropKind.Bool,
        SvJsonNumber { IsFloat: true } => PropKind.Float,
        SvJsonNumber => PropKind.Int,
        SvJsonString => PropKind.Text,
        SvJsonObject o when o.ContainsKey("$actor") => PropKind.ActorRef,
        SvJsonObject o when o.ContainsKey("$asset") => PropKind.AssetRef,
        SvJsonObject o when o.ContainsKey("$class") => PropKind.ClassRef,
        SvJsonObject { Count: 3 } o when o.ContainsKey("X") && o.ContainsKey("Y") && o.ContainsKey("Z") => PropKind.Vector,
        SvJsonObject { Count: 3 } o when o.ContainsKey("Pitch") && o.ContainsKey("Yaw") && o.ContainsKey("Roll") => PropKind.Rotator,
        SvJsonObject { Count: 4 } o when o.ContainsKey("R") && o.ContainsKey("G") && o.ContainsKey("B") && o.ContainsKey("A") => PropKind.Color,
        _ => PropKind.Raw,
    };

    private static PropKind StructKind(string? name)
    {
        string s = name is null ? "" : name[(name.LastIndexOf('.') + 1)..];
        return s switch
        {
            "Vector" => PropKind.Vector,
            "Rotator" => PropKind.Rotator,
            "Color" => PropKind.Color,
            _ => PropKind.Raw,
        };
    }

    private static bool IsActorClass(string? cls, IClassCatalog? catalog) =>
        cls is not null && catalog is not null && catalog.Find(cls) is not null && catalog.IsA(cls, "Engine.Actor");
}

/// <summary>Text forms of property values for the editors, and parsing back.</summary>
internal static class PropText
{
    /// <summary>The component names of a struct kind, in display order.</summary>
    public static string[] Axes(PropKind kind) => kind switch
    {
        PropKind.Vector => ["X", "Y", "Z"],
        PropKind.Rotator => ["Pitch", "Yaw", "Roll"],
        PropKind.Color => ["R", "G", "B", "A"],
        _ => [],
    };

    /// <summary>A component for display (rotators in degrees).</summary>
    public static double Component(SvJsonNode? value, PropKind kind, int axis)
    {
        if (value is not SvJsonObject o)
        {
            return 0;
        }

        double raw = PropValues.Num(o[Axes(kind)[axis]]);
        return kind == PropKind.Rotator ? Rot.ToDegrees((int)raw) : raw;
    }

    /// <summary>The value with one component replaced (input in display units).</summary>
    public static SvJsonNode WithComponent(SvJsonNode? value, PropKind kind, int axis, double v)
    {
        string[] axes = Axes(kind);
        var parts = new double[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            parts[i] = value is SvJsonObject o ? PropValues.Num(o[axes[i]]) : 0;
        }

        parts[axis] = kind switch
        {
            PropKind.Rotator => Rot.FromDegrees(v),
            PropKind.Color => Math.Clamp(Math.Round(v), 0, 255),
            _ => v,
        };
        return kind switch
        {
            PropKind.Vector => PropValues.Vector(new Vec3(parts[0], parts[1], parts[2])),
            PropKind.Rotator => PropValues.Rotator(new Rot((int)parts[0], (int)parts[1], (int)parts[2])),
            _ => Color((int)parts[0], (int)parts[1], (int)parts[2], (int)parts[3]),
        };
    }

    /// <summary>A Color struct with new red, green and blue, keeping the value's alpha (0 when it has none).</summary>
    public static SvJsonNode WithRgb(SvJsonNode? value, int r, int g, int b) =>
        Color(r, g, b, value is SvJsonObject o ? (int)Math.Clamp(PropValues.Num(o["A"]), 0, 255) : 0);

    private static SvJsonObject Color(int r, int g, int b, int a)
    {
        var o = new SvJsonObject();
        o.Add("A", SvJsonNode.Integer(a));
        o.Add("B", SvJsonNode.Integer(b));
        o.Add("G", SvJsonNode.Integer(g));
        o.Add("R", SvJsonNode.Integer(r));
        return o;
    }

    /// <summary>The UE2 zero value of a kind (what a property the class defaults do not list holds), or null for raw
    /// values.</summary>
    public static SvJsonNode? Zero(PropKind kind) => kind switch
    {
        PropKind.Bool => SvJsonNode.False,
        PropKind.Int or PropKind.Enum => SvJsonNode.Integer(0),
        PropKind.Float => SvJsonNode.Float(0),
        PropKind.Text => SvJsonNode.String(""),
        PropKind.ActorRef or PropKind.AssetRef or PropKind.ClassRef => SvJsonNode.Null,
        PropKind.Vector => PropValues.Vector(Vec3.Zero),
        PropKind.Rotator => PropValues.Rotator(Rot.Zero),
        PropKind.Color => Color(0, 0, 0, 0),
        _ => null,
    };

    public static string Display(SvJsonNode? value, PropKind kind) => value switch
    {
        null => "",
        SvJsonLiteral { Kind: SvJsonKind.Null } => kind is PropKind.ActorRef or PropKind.AssetRef or PropKind.ClassRef ? "" : "null",
        SvJsonString s when kind != PropKind.Raw => s.Value,
        SvJsonNumber n when kind is PropKind.Float or PropKind.Int => PanelUi.Num(PropValues.Num(n)),
        SvJsonObject o when kind == PropKind.ActorRef && o["$actor"] is SvJsonString s => s.Value,
        SvJsonObject o when kind == PropKind.AssetRef && o["$asset"] is SvJsonString s => s.Value,
        SvJsonObject o when kind == PropKind.ClassRef && o["$class"] is SvJsonString s => s.Value,
        _ => value.ToString(),
    };

    /// <summary>Parses editor text into a value of the kind, or false when the text is not one yet.</summary>
    public static bool TryParse(string text, PropKind kind, out SvJsonNode? value)
    {
        value = null;
        string t = text.Trim();
        bool none = t.Length == 0 || string.Equals(t, "None", StringComparison.OrdinalIgnoreCase);
        switch (kind)
        {
            case PropKind.Int:
                if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                {
                    value = SvJsonNode.Integer(l);
                }

                break;
            case PropKind.Float:
                if (PanelUi.TryNum(t, out double d))
                {
                    value = SvJsonNode.Float((float)d);
                }

                break;
            case PropKind.Text:
                value = SvJsonNode.String(text);
                break;
            case PropKind.ActorRef:
                value = none ? SvJsonNode.Null : PropValues.ActorRef(t);
                break;
            case PropKind.AssetRef:
                value = none ? SvJsonNode.Null : PropValues.AssetRef(t);
                break;
            case PropKind.ClassRef:
                if (none)
                {
                    value = SvJsonNode.Null;
                }
                else
                {
                    var o = new SvJsonObject();
                    o.Add("$class", SvJsonNode.String(t));
                    value = o;
                }

                break;
            default:
                value = ParseJson(t);
                break;
        }

        return value is not null;
    }

    /// <summary>JSON text as a value, or null when it does not parse.</summary>
    public static SvJsonNode? ParseJson(string text)
    {
        try
        {
            return text.Trim().Length == 0 ? null : SvJson.Parse(text);
        }
        catch (SvMapFormatException)
        {
            return null;
        }
    }

    public static string Canon(SvJsonNode? v) => v?.ToString() ?? "";

    /// <summary>True for a struct or array value: its box shows <see cref="Summary"/> until it is clicked to edit the JSON.</summary>
    public static bool IsStructured(SvJsonNode? v) => v is SvJsonObject or SvJsonArray;

    /// <summary>A struct or array in UnrealEd's form, (Zone=ZoneInfo392, ZoneNumber=13), with references by name.</summary>
    public static string Summary(SvJsonNode? v) => v switch
    {
        null or SvJsonLiteral { Kind: SvJsonKind.Null } => "None",
        SvJsonString s => s.Value,
        SvJsonNumber n => PanelUi.Num(PropValues.Num(n)),
        SvJsonObject o when o.Count == 1 && o[0].Key is "$actor" or "$asset" or "$class" => Summary(o[0].Value),
        SvJsonObject o => "(" + string.Join(", ", o.Select(kv => $"{kv.Key}={Summary(kv.Value)}")) + ")",
        SvJsonArray a => "(" + string.Join(", ", a.Select(Summary)) + ")",
        _ => v.ToString(),
    };
}
