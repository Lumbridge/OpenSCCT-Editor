using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// Small JSON helpers for the scripting API. Output is compact on purpose: whole numbers print as integers, vectors as
/// <c>[x,y,z]</c>, and the writer sorts keys (canonical form), so the same map always gives the same bytes.
/// </summary>
public static class ApiJson
{
    /// <summary>Compact canonical text (sorted keys, no whitespace, one line).</summary>
    public static string Write(SvJsonNode node) => CanonicalJson.ToCanonicalString(node);

    public static SvJsonNode Parse(string text, string? what = null)
    {
        try
        {
            return SvJson.Parse(text, what);
        }
        catch (OpenScct.Content.SvMapFormatException ex)
        {
            throw new ApiException(ex.Message);
        }
    }

    public static SvJsonNode Str(string value) => SvJsonNode.String(value);

    public static SvJsonNode Bool(bool value) => SvJsonNode.Bool(value);

    /// <summary>A number: an integer when it is whole (shorter), else a float32.</summary>
    public static SvJsonNode Num(double value)
    {
        double r = Math.Round(value);
        if (Math.Abs(value - r) < 1e-4 && Math.Abs(r) < 1e12)
        {
            return SvJsonNode.Integer((long)r);
        }

        return SvJsonNode.Float((float)value);
    }

    public static SvJsonNode Vec(Vec3 v) => new SvJsonArray([Num(v.X), Num(v.Y), Num(v.Z)]);

    public static SvJsonNode Rotator(Rot r) => new SvJsonArray([SvJsonNode.Integer(r.Pitch), SvJsonNode.Integer(r.Yaw), SvJsonNode.Integer(r.Roll)]);

    public static SvJsonArray Arr(IEnumerable<SvJsonNode> items) => new(items);

    public static SvJsonArray Strings(IEnumerable<string> items) => new(items.Select(SvJsonNode.String));

    /// <summary>An object from members, skipping null values.</summary>
    public static SvJsonObject Obj(params (string Key, SvJsonNode? Value)[] members)
    {
        var o = new SvJsonObject();
        foreach ((string key, SvJsonNode? value) in members)
        {
            if (value is not null)
            {
                o.Add(key, value);
            }
        }

        return o;
    }

    // ---- readers ---------------------------------------------------------------------------------------------

    public static string? GetString(SvJsonObject? o, string key) => o?[key] is SvJsonString s ? s.Value : null;

    public static bool IsNull(SvJsonNode? node) => node is null || node.Kind == SvJsonKind.Null;

    public static bool TryNumber(SvJsonNode? node, out double value)
    {
        switch (node)
        {
            case SvJsonNumber { IsFloat: true } f:
                value = f.FloatValue;
                return true;
            case SvJsonNumber n when n.TryGetInt64(out long l):
                value = l;
                return true;
            case SvJsonNumber n:
                value = double.Parse(n.IntegerText, CultureInfo.InvariantCulture);
                return true;
            default:
                value = 0;
                return false;
        }
    }

    public static double Number(SvJsonObject o, string key, double fallback) => TryNumber(o[key], out double v) ? v : fallback;

    /// <summary>A number member, or an error naming the field.</summary>
    public static double RequireNumber(SvJsonNode? node, string field) =>
        TryNumber(node, out double v) ? v : throw new ApiException($"expected a number, got {Describe(node)}", field);

    public static bool GetBool(SvJsonObject o, string key, bool fallback) => o[key]?.Kind switch
    {
        SvJsonKind.True => true,
        SvJsonKind.False => false,
        _ => fallback,
    };

    /// <summary>A switch that must be true or false when given (null when absent), for keys where a misread value
    /// would change what happens, such as dryRun.</summary>
    public static bool? RequireBool(SvJsonObject o, string key) => o[key]?.Kind switch
    {
        null => null,
        SvJsonKind.True => true,
        SvJsonKind.False => false,
        _ => throw new ApiException($"{key} is true or false", key),
    };

    /// <summary>A vector from <c>[x,y,z]</c>, <c>{"X":..,"Y":..,"Z":..}</c> or <c>"x,y,z"</c>.</summary>
    public static bool TryVec(SvJsonNode? node, out Vec3 v)
    {
        v = Vec3.Zero;
        switch (node)
        {
            case SvJsonArray { Count: 3 } a when TryNumber(a[0], out double x) && TryNumber(a[1], out double y) && TryNumber(a[2], out double z):
                v = new Vec3(x, y, z);
                return true;
            case SvJsonObject o when o["X"] is not null || o["Y"] is not null || o["Z"] is not null:
                v = new Vec3(Number(o, "X", 0), Number(o, "Y", 0), Number(o, "Z", 0));
                return true;
            case SvJsonString s:
                return TryParseTriple(s.Value, out v);
            default:
                return false;
        }
    }

    public static Vec3 RequireVec(SvJsonNode? node, string field) =>
        TryVec(node, out Vec3 v) ? v : throw new ApiException($"expected [x,y,z], got {Describe(node)}", field);

    /// <summary>A rotator from <c>[pitch,yaw,roll]</c> (65536 units per turn) or <c>{"Pitch":..}</c>.</summary>
    public static bool TryRot(SvJsonNode? node, out Rot r, double unitsPerValue = 1)
    {
        r = Rot.Zero;
        if (node is SvJsonObject o && (o["Pitch"] is not null || o["Yaw"] is not null || o["Roll"] is not null))
        {
            r = new Rot(Units(Number(o, "Pitch", 0)), Units(Number(o, "Yaw", 0)), Units(Number(o, "Roll", 0)));
            return true;
        }

        if (!TryVec(node, out Vec3 v))
        {
            return false;
        }

        r = new Rot(Units(v.X), Units(v.Y), Units(v.Z));
        return true;

        int Units(double value) => (int)Math.Round(value * unitsPerValue);
    }

    /// <summary>Parses "x,y,z" (spaces allowed).</summary>
    public static bool TryParseTriple(string text, out Vec3 v)
    {
        v = Vec3.Zero;
        string[] parts = text.Split(',');
        if (parts.Length != 3)
        {
            return false;
        }

        var n = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]))
            {
                return false;
            }
        }

        v = new Vec3(n[0], n[1], n[2]);
        return true;
    }

    /// <summary>A list of strings from a string or an array of strings.</summary>
    public static List<string>? StringList(SvJsonNode? node, string field)
    {
        switch (node)
        {
            case null:
                return null;
            case SvJsonString s:
                return [s.Value];
            case SvJsonArray a:
                var list = new List<string>(a.Count);
                foreach (SvJsonNode item in a)
                {
                    list.Add(item is SvJsonString str ? str.Value : throw new ApiException($"expected strings, got {Describe(item)}", field));
                }

                return list;
            default:
                throw new ApiException($"expected a string or a list of strings, got {Describe(node)}", field);
        }
    }

    public static string Describe(SvJsonNode? node) => node switch
    {
        null => "nothing",
        SvJsonString s => s.Value.Length > 40 ? "a string" : $"\"{s.Value}\"",
        SvJsonObject => "an object",
        SvJsonArray a => $"an array of {a.Count}",
        _ => Write(node),
    };

    public static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>An error in an API request: what is wrong, and where (op index and field) so the caller can fix it.</summary>
public sealed class ApiException : Exception
{
    public ApiException(string message, string? field = null)
        : base(message) => Field = field;

    public ApiException()
    {
    }

    public ApiException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>An unexpected failure, reported with its type so it can be traced (the request itself may be fine).</summary>
    public static ApiException Internal(Exception ex) => new($"internal error ({ex.GetType().Name}): {ex.Message}", ex);

    /// <summary>The field the error is about (a dotted path such as <c>props.TeamNumber</c>), or null.</summary>
    public string? Field { get; init; }

    /// <summary>The index of the op in the change document, or null outside a change document.</summary>
    public int? Op { get; set; }

    public SvJsonObject ToJson() => ApiJson.Obj(
        ("message", ApiJson.Str(Message)),
        ("op", Op is int i ? SvJsonNode.Integer(i) : null),
        ("field", Field is null ? null : ApiJson.Str(Field)));
}
