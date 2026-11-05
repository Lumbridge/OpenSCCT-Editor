using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Cli.Commands;

/// <summary>Helpers the geometry, class and check commands share: JSON output and builder parameters from options.</summary>
internal static class GeometryCliSupport
{
    /// <summary>Compact canonical JSON (sorted keys, one line), or pretty with <c>--pretty</c>.</summary>
    public static void PrintJson(SvJsonNode node, CliArgs a) =>
        Console.WriteLine(a.Has("pretty") ? CanonicalJson.ToPrettyString(node) : CanonicalJson.ToCanonicalString(node));

    public static SvJsonArray Array(IEnumerable<string> items) => new(items.Select(SvJsonNode.String));

    public static SvJsonNode Num(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15 ? SvJsonNode.Integer((long)Math.Round(v)) : SvJsonNode.Float((float)v);

    public static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static void EnsureServices() => EditorServices.Discover(typeof(GeometryCliSupport).Assembly);

    /// <summary>
    /// A builder spec from <c>--name value</c> options: every option a parameter of the kind (ignoring case) is read
    /// into the spec on top of <paramref name="start"/> (defaults when null). Options in <paramref name="reserved"/>
    /// belong to the command; anything else is an error that lists the kind's parameters.
    /// </summary>
    public static BuilderSpec SpecFromOptions(BuilderKind kind, CliArgs a, IReadOnlySet<string> reserved, BuilderSpec? start = null)
    {
        BuilderSpec spec = start?.Clone() ?? new BuilderSpec(kind.Kind);
        foreach (string key in a.OptionKeys.Order(StringComparer.Ordinal))
        {
            if (reserved.Contains(key))
            {
                continue;
            }

            BuilderParam p = kind.Params.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{kind.Kind} has no parameter '{key}' (parameters: {string.Join(", ", kind.Params.Select(x => x.Name))})");
            string value = a.Get(key)!;
            if (p.IsText)
            {
                spec.Strings[p.Name] = value;
            }
            else if (p.Type == ParamType.Flag)
            {
                spec.Numbers[p.Name] = value.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "on" => 1,
                    "false" or "0" or "no" or "off" => 0,
                    _ => throw new ArgumentException($"{kind.Kind}: {p.Name} is a flag (true/false), not '{value}'"),
                };
            }
            else
            {
                spec.Numbers[p.Name] = Parsing.Number(value, $"{kind.Kind}: {p.Name}");
            }
        }

        return spec;
    }

    /// <summary>The <c>--csg</c> option, or null when it is not given.</summary>
    public static CsgOper? ParseCsg(string? text) => text?.ToLowerInvariant() switch
    {
        null => null,
        "add" or "solid" => CsgOper.Add,
        "subtract" or "sub" or "carve" => CsgOper.Subtract,
        _ => throw new ArgumentException($"--csg must be add or subtract, not '{text}'"),
    };

    public static SvJsonObject ParamToJson(BuilderParam p)
    {
        var o = new SvJsonObject();
        o.Add("name", SvJsonNode.String(p.Name));
        o.Add("type", SvJsonNode.String(p.Type.ToString().ToLowerInvariant()));
        o.Add("summary", SvJsonNode.String(p.Summary));
        if (p.IsText)
        {
            o.Add("default", SvJsonNode.String(p.DefaultText ?? ""));
        }
        else
        {
            o.Add("default", Num(p.Default));
            if (p.Type is ParamType.Number or ParamType.Integer && double.IsFinite(p.Min) && double.IsFinite(p.Max))
            {
                o.Add("min", Num(p.Min));
                o.Add("max", Num(p.Max));
            }
        }

        if (p.Choices.Count > 0)
        {
            o.Add("choices", Array(p.Choices));
        }

        return o;
    }
}
