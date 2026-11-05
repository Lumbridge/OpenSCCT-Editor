using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Builders;

/// <summary>
/// A spec read against its builder's parameters: missing values take the defaults, numbers are clamped to their
/// range, whole numbers rounded, flags made 0/1 and choices matched ignoring case. <see cref="Spec"/> is the
/// complete, normalised spec the built shapes carry, so a piece regenerates the same even if defaults change later.
/// </summary>
public sealed class BuilderArgs
{
    /// <summary>Spec keys every builder keeps as they are: the piece a part belongs to and its index in it.</summary>
    public const string PieceKey = "piece";

    public const string PartKey = "part";

    private BuilderArgs(BuilderSpec spec) => Spec = spec;

    public BuilderSpec Spec { get; }

    public static BuilderArgs From(BuilderKind kind, BuilderSpec source)
    {
        var spec = new BuilderSpec(kind.Kind);
        foreach (BuilderParam p in kind.Params)
        {
            if (p.IsText)
            {
                spec.Strings[p.Name] = ReadText(kind, p, source);
            }
            else
            {
                spec.Numbers[p.Name] = ReadNumber(p, source);
            }
        }

        if (source.Strings.TryGetValue(PieceKey, out string? piece))
        {
            spec.Strings[PieceKey] = piece;
        }

        return new BuilderArgs(spec);
    }

    /// <summary>The spec keys <paramref name="source"/> sets that the builder does not know (for error messages).</summary>
    public static IEnumerable<string> UnknownKeys(BuilderKind kind, BuilderSpec source) =>
        source.Numbers.Keys.Concat(source.Strings.Keys)
            .Where(k => k is not PieceKey and not PartKey && !kind.Params.Any(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    public double Num(string name) => Spec.Numbers[name];

    public int Int(string name) => (int)Spec.Numbers[name];

    public bool Flag(string name) => Spec.Numbers[name] != 0;

    public string Text(string name) => Spec.Strings[name];

    public string Material => Spec.Strings[BuilderKind.MaterialParam.Name];

    private static double ReadNumber(BuilderParam p, BuilderSpec source)
    {
        double v = Lookup(source.Numbers, p.Name) ?? p.Default;
        if (!double.IsFinite(v))
        {
            v = p.Default;
        }

        v = Math.Clamp(v, p.Min, p.Max);
        return p.Type switch
        {
            ParamType.Integer => Math.Round(v),
            ParamType.Flag => v != 0 ? 1 : 0,
            _ => v,
        };
    }

    private static string ReadText(BuilderKind kind, BuilderParam p, BuilderSpec source)
    {
        string? v = Lookup(source.Strings, p.Name);
        if (string.IsNullOrEmpty(v))
        {
            return p.DefaultText ?? "";
        }

        if (p.Type != ParamType.Choice)
        {
            return v;
        }

        return p.Choices.FirstOrDefault(c => string.Equals(c, v, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{kind.Kind}: {p.Name} must be one of {string.Join(", ", p.Choices)} (got '{v}')");
    }

    private static double? Lookup(Dictionary<string, double> values, string name) =>
        values.TryGetValue(name, out double v) ? v
        : values.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv2 ? kv2.Value
        : null;

    private static string? Lookup(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? v) ? v
        : values.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
