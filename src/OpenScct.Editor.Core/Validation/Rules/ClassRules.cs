using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>Actor classes and stored properties against the class catalog.</summary>
internal sealed class ClassRules : MapRule
{
    public const string Unknown = "class.unknown";
    public const string Abstract = "class.abstract";
    public const string NotPlaceable = "class.not-placeable";
    public const string PropUnknown = "prop.unknown";
    public const string PropType = "prop.type-mismatch";
    public const string PropEnum = "prop.enum-range";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(Unknown, Severity.Error, "The actor's class is not in the class catalog (not checked on an .sdc import's lifted and cooked-out actors)"),
        new(Abstract, Severity.Error, "The actor's class is abstract and cannot exist in a map"),
        new(NotPlaceable, Severity.Warning, "The class is not placeable (brush-based volumes and movers are fine)"),
        new(PropUnknown, Severity.Warning, "A stored property the class does not have (a typo, or a property of another class)"),
        new(PropType, Severity.Warning, "A stored value of the wrong type for its property"),
        new(PropEnum, Severity.Warning, "An enum value past the end of its enum"),
    ];

    public override void Run(CheckContext c)
    {
        var schemas = new Dictionary<string, Dictionary<string, PropInfo>?>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in c.Project.Actors)
        {
            ObjectRef r = ObjectRef.Actor(a.Id);
            ClassInfo? info = c.Classes.Find(a.Class);
            if (info is null)
            {
                // An .sdc import's lifted RE+ actors and cooked-out actors are of classes the map may define itself; the
                // build leaves them out, and the T3D export (roundtrip.unknownClass) says what the original editor needs.
                if (!BakedContent.IsLifted(a) && !BakedContent.IsCookedOut(a))
                {
                    c.Report(Severity.Error, Unknown, $"{a.Id}: unknown class {a.Class}", a.Location, r);
                }

                continue;
            }

            if (info.Abstract)
            {
                c.Report(Severity.Error, Abstract, $"{a.Id}: {info.Name} is abstract", a.Location, r);
            }
            else if (!info.Placeable && !c.IsA(a, "Engine.Brush") && !c.IsA(a, "Engine.Mover"))
            {
                c.Report(Severity.Warning, NotPlaceable, $"{a.Id}: {info.Name} is not placeable", a.Location, r);
            }

            if (!schemas.TryGetValue(info.Name, out Dictionary<string, PropInfo>? schema))
            {
                schema = new Dictionary<string, PropInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (PropInfo p in c.Classes.AllProperties(info.Name))
                {
                    schema.TryAdd(p.Name, p);
                }

                schemas[info.Name] = schema;
            }

            foreach ((string name, SvJsonNode value) in a.Props.Where(kv => !PropValues.IsRaw(kv.Value)))
            {
                if (!schema!.TryGetValue(name, out PropInfo? p))
                {
                    c.Report(Severity.Warning, PropUnknown, $"{a.Id}: {info.ShortName} has no property {name}", a.Location, r);
                    continue;
                }

                if (TypeProblem(c.Classes, p, value) is { } problem)
                {
                    c.Report(Severity.Warning, problem.StartsWith("enum", StringComparison.Ordinal) ? PropEnum : PropType,
                        $"{a.Id}.{p.Name}: {problem}", a.Location, r);
                }
            }
        }
    }

    /// <summary>Why a value does not fit its property, or null when it does.</summary>
    internal static string? TypeProblem(IClassCatalog classes, PropInfo p, SvJsonNode value)
    {
        if (p.ArrayDim > 1 && value is SvJsonObject o && o["$elements"] is SvJsonObject elements)
        {
            foreach ((string index, SvJsonNode element) in elements)
            {
                if (!int.TryParse(index, out int i) || i < 0 || i >= p.ArrayDim)
                {
                    return $"element {index} is outside 0..{p.ArrayDim - 1}";
                }

                if (Element(classes, p, p.Type, element) is { } problem)
                {
                    return $"[{index}] {problem}";
                }
            }

            return null;
        }

        if (p.Type == "array")
        {
            if (value is not SvJsonArray items)
            {
                return value.Kind == SvJsonKind.Null ? null : "expected an array";
            }

            foreach (SvJsonNode item in items)
            {
                if (Element(classes, p, p.Inner ?? "unknown", item) is { } problem)
                {
                    return problem;
                }
            }

            return null;
        }

        return Element(classes, p, p.Type, value);
    }

    private static string? Element(IClassCatalog classes, PropInfo p, string type, SvJsonNode v)
    {
        switch (type)
        {
            case "bool":
                return v.Kind is SvJsonKind.True or SvJsonKind.False ? null : "expected true or false";
            case "byte":
                if (v is not SvJsonNumber b || !b.TryGetInt32(out int bi) || bi is < 0 or > 255)
                {
                    return "expected a whole number 0..255";
                }

                if (p.Enum is { } e && classes.EnumValues(e) is { } values && bi >= values.Count)
                {
                    return $"enum value {bi} is past the end of {e} ({values.Count} values)";
                }

                return null;
            case "int":
                return v is SvJsonNumber n && !n.IsFloat ? null : "expected a whole number";
            case "float":
                return v is SvJsonNumber ? null : "expected a number";
            case "name" or "string":
                return v is SvJsonString ? null : "expected a string";
            case "object":
                return v.Kind == SvJsonKind.Null || v is SvJsonObject ob && (ob["$actor"] is not null || ob["$asset"] is not null)
                    ? null : "expected {\"$actor\": id}, {\"$asset\": path} or null";
            case "class":
                return v.Kind == SvJsonKind.Null || v is SvJsonObject cl && cl["$class"] is not null ? null : "expected {\"$class\": path} or null";
            case "struct":
                return v is SvJsonObject so && !so.Any(kv => kv.Key.StartsWith('$')) ? null : "expected a struct object";
            default:
                return null;
        }
    }
}
