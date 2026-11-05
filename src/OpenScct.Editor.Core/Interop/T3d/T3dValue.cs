using System.Globalization;
using System.Text;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>
/// A property value in UE2 export text (T3D), untyped: what the text says, before the class schema gives it a type.
/// </summary>
public abstract record T3dValue
{
    public abstract void Write(StringBuilder sb);

    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }
}

/// <summary>A bare token: a number, an enum or name token, <c>True</c>, <c>False</c> or <c>None</c>.</summary>
public sealed record T3dAtom(string Text) : T3dValue
{
    public static readonly T3dAtom None = new("None");
    public static readonly T3dAtom True = new("True");
    public static readonly T3dAtom False = new("False");

    public bool Is(string text) => string.Equals(Text, text, StringComparison.OrdinalIgnoreCase);

    public override void Write(StringBuilder sb) => sb.Append(Text);

    public static T3dAtom Float(double v) => new(T3dFormat.Float(v));

    public static T3dAtom Int(long v) => new(v.ToString(CultureInfo.InvariantCulture));
}

/// <summary>A quoted string (strings and names).</summary>
public sealed record T3dText(string Value) : T3dValue
{
    public override void Write(StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in Value)
        {
            if (c is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        sb.Append('"');
    }
}

/// <summary>An object reference <c>Class'Package.Group.Name'</c>.</summary>
public sealed record T3dRef(string Class, string Path) : T3dValue
{
    public override void Write(StringBuilder sb) => sb.Append(Class).Append('\'').Append(Path).Append('\'');
}

/// <summary>A struct <c>(A=1,B(0)=2)</c>.</summary>
public sealed record T3dStruct(IReadOnlyList<T3dField> Fields) : T3dValue
{
    public override void Write(StringBuilder sb)
    {
        sb.Append('(');
        for (int i = 0; i < Fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            Fields[i].Write(sb);
        }

        sb.Append(')');
    }
}

/// <summary>A dynamic array inside a struct <c>(v,v,v)</c>.</summary>
public sealed record T3dList(IReadOnlyList<T3dValue> Items) : T3dValue
{
    public override void Write(StringBuilder sb)
    {
        sb.Append('(');
        for (int i = 0; i < Items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            Items[i].Write(sb);
        }

        sb.Append(')');
    }
}

/// <summary>One <c>Name=Value</c> or <c>Name(i)=Value</c> assignment (an actor property line or a struct member).</summary>
public sealed record T3dField(string Name, int? Index, T3dValue Value)
{
    public void Write(StringBuilder sb)
    {
        sb.Append(Name);
        if (Index is int i)
        {
            sb.Append('(').Append(i.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        sb.Append('=');
        Value.Write(sb);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }
}

/// <summary>Number formats of UE2 export text.</summary>
public static class T3dFormat
{
    /// <summary>A float property as UE2 writes it (<c>%f</c>), with more decimals where six would not read back as the
    /// same single-precision value (see <see cref="Decimals"/>).</summary>
    public static string Float(double v)
    {
        double f = v == 0 ? 0 : v;
        return f.ToString("F" + Decimals(f).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>A polygon vector component as UE2 writes it (<c>%+013.6f</c>), with more decimals where needed.</summary>
    public static string PolyComponent(double v)
    {
        double f = v == 0 ? 0d : (double)(float)v;
        // Sign and digits apart: a custom format with a negative section writes "-+00000.000000" for a negative value
        // that rounds to zero. Such a value is written "+" (the digits say zero either way).
        string digits = Math.Abs(f).ToString("00000." + new string('0', Decimals(f)), CultureInfo.InvariantCulture);
        return (f < 0 && digits.AsSpan().ContainsAnyExcept("0.") ? "-" : "+") + digits;
    }

    /// <summary>
    /// The decimals a value needs: UE2's six, or as many more as it takes to read back as the same single-precision
    /// value (the stock editor's six lose up to 5e-7, so a round trip through its export would move vertices, texture
    /// axes and locations the package stored). Both the stock reader (appAtof) and this one read any number of decimals.
    /// </summary>
    private static int Decimals(double v)
    {
        const int MaxDecimals = 60; // enough for the smallest single-precision values
        float f = (float)v;
        for (int d = 6; d < MaxDecimals; d++)
        {
            if (float.Parse(v.ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) == f)
            {
                return d;
            }
        }

        return MaxDecimals;
    }

    public static string PolyVector(double x, double y, double z) => $"{PolyComponent(x)},{PolyComponent(y)},{PolyComponent(z)}";

    public static bool TryParseFloat(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public static bool TryParseInt(string s, out long v) =>
        long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);

    /// <summary>True for a token that looks like an integer (<c>-12</c>).</summary>
    public static bool IsInteger(string s)
    {
        int i = s.Length > 0 && (s[0] == '-' || s[0] == '+') ? 1 : 0;
        if (i >= s.Length)
        {
            return false;
        }

        for (; i < s.Length; i++)
        {
            if (!char.IsAsciiDigit(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True for a token that looks like a decimal number with a point or exponent (<c>1.500000</c>).</summary>
    public static bool IsDecimal(string s) =>
        !IsInteger(s) && s.Length > 0 && (char.IsAsciiDigit(s[0]) || s[0] is '-' or '+' or '.')
        && s.AsSpan().IndexOfAny('.', 'e', 'E') >= 0 && TryParseFloat(s, out _);
}
