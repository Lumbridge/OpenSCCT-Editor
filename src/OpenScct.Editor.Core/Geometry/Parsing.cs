using System.Globalization;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>Parses the compact number lists the CLI and change documents use (<c>x,y,z</c>, invariant culture).</summary>
public static class Parsing
{
    public static double[] Numbers(string text, int count, string what)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != count)
        {
            throw new ArgumentException($"{what}: expected {count} comma-separated numbers, got '{text}'");
        }

        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
            {
                throw new ArgumentException($"{what}: '{parts[i]}' is not a number");
            }
        }

        return values;
    }

    public static double Number(string text, string what) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? v
            : throw new ArgumentException($"{what}: '{text}' is not a number");

    public static Vec3 Vector(string text, string what = "vector (x,y,z)")
    {
        double[] v = Numbers(text, 3, what);
        return new Vec3(v[0], v[1], v[2]);
    }

    /// <summary>A rotator as <c>pitch,yaw,roll</c> in rotator units (65536 per turn).</summary>
    public static Rot Rotator(string text)
    {
        double[] v = Numbers(text, 3, "rotation (pitch,yaw,roll)");
        return new Rot((int)Math.Round(v[0]), (int)Math.Round(v[1]), (int)Math.Round(v[2]));
    }

    /// <summary>Integer indexes as <c>0,2,5</c> or <c>all</c> (returns null for all).</summary>
    public static IReadOnlyList<int>? Indexes(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0
                ? i
                : throw new ArgumentException($"'{s}' is not a face index"))
            .ToList();
    }
}
