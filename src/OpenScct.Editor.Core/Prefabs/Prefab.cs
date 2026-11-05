using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;

namespace OpenScct.Editor.Core.Prefabs;

/// <summary>Where a prefab was found. A project prefab hides a library one of the same name, which hides a built-in one.</summary>
public enum PrefabSource
{
    Project,
    Library,
    Builtin,
}

/// <summary>A prefab parameter: its default (which also gives its type: number, text or flag), range and help.</summary>
public sealed record PrefabParam(string Name, SvJsonNode Default, double? Min, double? Max, string? Summary, IReadOnlyList<string>? Options)
{
    public SvJsonObject ToJson() => ApiJson.Obj(
        ("default", Default),
        ("min", Min is double lo ? ApiJson.Num(lo) : null),
        ("max", Max is double hi ? ApiJson.Num(hi) : null),
        ("options", Options is null ? null : ApiJson.Strings(Options)),
        ("summary", Summary is null ? null : ApiJson.Str(Summary)));

    /// <summary>Checks a given value against the parameter's type, range and options.</summary>
    public SvJsonNode Check(SvJsonNode value, string field)
    {
        switch (Default)
        {
            case SvJsonNumber:
                double v = ApiJson.TryNumber(value, out double n) ? n : throw new ApiException($"{Name} is a number", field);
                if (v < (Min ?? double.MinValue) || v > (Max ?? double.MaxValue))
                {
                    throw new ApiException($"{Name} is {ApiJson.Inv(Min ?? double.MinValue)}..{ApiJson.Inv(Max ?? double.MaxValue)}", field);
                }

                return ApiJson.Num(v);
            case SvJsonString:
                string s = value is SvJsonString str ? str.Value : throw new ApiException($"{Name} is text", field);
                if (Options is not null && !Options.Contains(s, StringComparer.Ordinal))
                {
                    throw new ApiException($"{Name} is one of {string.Join(", ", Options)}", field);
                }

                return value;
            default:
                return value.Kind is SvJsonKind.True or SvJsonKind.False ? value : throw new ApiException($"{Name} is true or false", field);
        }
    }
}

/// <summary>
/// A prefab (<c>osmap.prefab/1</c>): a saved group of brushes, actors and GEs around a pivot, with parameters. Its parts
/// come from <c>objects</c> (project JSON in prefab space, as saved from a selection) and from <c>ops</c> (a change
/// document template run in an empty scratch map), both with <c>${expr}</c> parameter substitution. Prefab space: the
/// pivot is the origin, usually on the floor, and +X is the front (it faces out of a wall it is put against).
/// </summary>
public sealed partial class PrefabDefinition
{
    public const string Format = "osmap.prefab/1";

    public required string Name { get; init; }

    public string? Title { get; init; }

    public string? Summary { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<PrefabParam> Params { get; init; } = [];

    /// <summary>Saved objects: <c>{"brushes":[..],"actors":[..],"ges":[..]}</c> in project JSON, relative to the pivot.</summary>
    public SvJsonObject? Objects { get; init; }

    /// <summary>A change document template: ops that build the parts (symbols name them).</summary>
    public SvJsonArray? Ops { get; init; }

    public PrefabSource Source { get; init; } = PrefabSource.Project;

    /// <summary>The file it was read from (null for built-ins and unsaved prefabs).</summary>
    public string? Path { get; init; }

    /// <summary>A short hash of the definition, stored on instances to tell when the prefab changed since.</summary>
    public string Hash => _hash ??= ComputeHash();

    private string? _hash;

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public SvJsonObject ToJson() => ApiJson.Obj(
        ("format", ApiJson.Str(Format)),
        ("name", ApiJson.Str(Name)),
        ("title", Title is null ? null : ApiJson.Str(Title)),
        ("summary", Summary is null ? null : ApiJson.Str(Summary)),
        ("category", Category is null ? null : ApiJson.Str(Category)),
        ("params", Params.Count == 0 ? null : ApiJson.Obj(Params.Select(p => (p.Name, (SvJsonNode?)p.ToJson())).ToArray())),
        ("objects", Objects),
        ("ops", Ops));

    /// <summary>Reads a prefab file's JSON. Errors name the file.</summary>
    public static PrefabDefinition FromJson(SvJsonNode node, PrefabSource source, string? path)
    {
        string where = path ?? "prefab";
        if (node is not SvJsonObject o)
        {
            throw new ApiException($"{where}: a prefab is a JSON object", "format");
        }

        if (ApiJson.GetString(o, "format") is { } format && format != Format)
        {
            throw new ApiException($"{where}: unsupported format '{format}' (this editor reads {Format})", "format");
        }

        string name = ApiJson.GetString(o, "name") ?? System.IO.Path.GetFileNameWithoutExtension(path) ?? throw new ApiException($"{where}: missing name", "name");
        if (!IsValidName(name))
        {
            throw new ApiException($"{where}: '{name}' is not a prefab name (letters, digits, _ and -)", "name");
        }

        var parms = new List<PrefabParam>();
        if (o["params"] is SvJsonObject ps)
        {
            foreach (KeyValuePair<string, SvJsonNode> kv in ps)
            {
                parms.Add(ReadParam(kv.Key, kv.Value, where));
            }
        }

        SvJsonObject? objects = o["objects"] as SvJsonObject;
        SvJsonArray? ops = o["ops"] as SvJsonArray;
        if (objects is null && ops is null)
        {
            throw new ApiException($"{where}: a prefab needs objects or ops", "objects");
        }

        return new PrefabDefinition
        {
            Name = name,
            Title = ApiJson.GetString(o, "title"),
            Summary = ApiJson.GetString(o, "summary"),
            Category = ApiJson.GetString(o, "category"),
            Params = parms,
            Objects = objects,
            Ops = ops,
            Source = source,
            Path = path,
        };
    }

    /// <summary>The values to expand with: the defaults, overridden by <paramref name="given"/> (checked), plus
    /// <c>id</c> (the instance id) for templates that name things after their instance.</summary>
    public Dictionary<string, object> Values(IReadOnlyDictionary<string, SvJsonNode> given, string instanceId, string field)
    {
        foreach (string key in given.Keys)
        {
            if (!Params.Any(p => p.Name == key))
            {
                throw new ApiException($"prefab {Name} has no parameter '{key}'"
                    + (Params.Count == 0 ? "" : $" ({string.Join(", ", Params.Select(p => p.Name))})"), $"{field}.{key}");
            }
        }

        var values = new Dictionary<string, object>(StringComparer.Ordinal) { ["id"] = instanceId };
        foreach (PrefabParam p in Params)
        {
            SvJsonNode v = given.TryGetValue(p.Name, out SvJsonNode? g) ? p.Check(g, $"{field}.{p.Name}") : p.Default;
            values[p.Name] = PrefabTemplate.ToValue(v);
        }

        return values;
    }

    private static PrefabParam ReadParam(string name, SvJsonNode node, string where)
    {
        if (!IdLike().IsMatch(name) || name == "id")
        {
            throw new ApiException($"{where}: '{name}' is not a parameter name (letters, digits and _; 'id' is the instance id)", $"params.{name}");
        }

        // A bare value is the default; an object gives default, min, max, options and summary.
        SvJsonObject? o = node as SvJsonObject;
        SvJsonNode def = o is null ? node : o["default"] ?? throw new ApiException($"{where}: parameter {name} needs a default", $"params.{name}.default");
        if (def is not (SvJsonNumber or SvJsonString) && def.Kind is not (SvJsonKind.True or SvJsonKind.False))
        {
            throw new ApiException($"{where}: parameter {name} defaults to a number, text or true/false", $"params.{name}.default");
        }

        if (o is null)
        {
            return new PrefabParam(name, def, null, null, null, null);
        }

        return new PrefabParam(name, def,
            ApiJson.TryNumber(o["min"], out double lo) ? lo : null,
            ApiJson.TryNumber(o["max"], out double hi) ? hi : null,
            ApiJson.GetString(o, "summary"),
            ApiJson.StringList(o["options"], $"params.{name}.options"));
    }

    private string ComputeHash()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ApiJson.Write(ToJson()));
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdLike();
}
