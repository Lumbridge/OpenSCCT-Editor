using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;

namespace OpenScct.Editor.Core.Prefabs;

/// <summary>
/// <c>${expr}</c> substitution in prefab JSON. A string that is one whole <c>${expr}</c> becomes the value (a number
/// stays a number); <c>${...}</c> inside longer text is replaced by its text. Object keys are left alone.
/// </summary>
public static class PrefabTemplate
{
    public static SvJsonNode Substitute(SvJsonNode node, IReadOnlyDictionary<string, object> values, string field) => node switch
    {
        SvJsonString s when s.Value.Contains("${", StringComparison.Ordinal) => String(s.Value, values, field),
        SvJsonObject o => Object(o, values, field),
        SvJsonArray a => new SvJsonArray(a.Select((x, i) => Substitute(x, values, $"{field}[{i}]"))),
        _ => node,
    };

    /// <summary>Runs a template op's <c>"if"</c> (already substituted): null when it is false, else the op without it.</summary>
    public static SvJsonObject? Keep(SvJsonObject op)
    {
        if (op["if"] is not { } cond)
        {
            return op;
        }

        if (!PrefabExpression.Truthy(ToValue(cond)))
        {
            return null;
        }

        var copy = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in op)
        {
            if (kv.Key != "if")
            {
                copy.Add(kv.Key, kv.Value);
            }
        }

        return copy;
    }

    public static object ToValue(SvJsonNode node) => node switch
    {
        SvJsonString s => s.Value,
        { Kind: SvJsonKind.True } => true,
        { Kind: SvJsonKind.False } => false,
        _ when ApiJson.TryNumber(node, out double d) => d,
        _ => "",
    };

    public static SvJsonNode ToNode(object value) => value switch
    {
        double d => ApiJson.Num(d),
        bool b => ApiJson.Bool(b),
        _ => ApiJson.Str(PrefabExpression.Text(value)),
    };

    private static SvJsonObject Object(SvJsonObject o, IReadOnlyDictionary<string, object> values, string field)
    {
        var copy = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            copy.Add(kv.Key, Substitute(kv.Value, values, field.Length == 0 ? kv.Key : $"{field}.{kv.Key}"));
        }

        return copy;
    }

    private static SvJsonNode String(string text, IReadOnlyDictionary<string, object> values, string field)
    {
        // One whole ${expr} (its first closing brace is the last character) keeps the value's type.
        string trimmed = text.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.IndexOf('}') == trimmed.Length - 1)
        {
            return ToNode(PrefabExpression.Evaluate(trimmed[2..^1], values, field));
        }

        var sb = new System.Text.StringBuilder();
        int pos = 0;
        while (pos < text.Length)
        {
            int start = text.IndexOf("${", pos, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(text, pos, text.Length - pos);
                break;
            }

            int end = text.IndexOf('}', start + 2);
            if (end < 0)
            {
                throw new ApiException($"unclosed ${{ in \"{text}\"", field);
            }

            sb.Append(text, pos, start - pos);
            sb.Append(PrefabExpression.Text(PrefabExpression.Evaluate(text[(start + 2)..end], values, field)));
            pos = end + 1;
        }

        return ApiJson.Str(sb.ToString());
    }
}
