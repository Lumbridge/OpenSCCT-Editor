using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>The <c>classes</c> and <c>schema</c> queries, over the class catalog.</summary>
internal static class ClassQueries
{
    /// <summary>
    /// <c>{"q":"classes","under":"SObjective","text":"Laser","all":false}</c>: placeable class names (with
    /// <c>all</c>, abstract and non-placeable ones too). Without the catalog: the classes the map uses, with counts.
    /// </summary>
    public static SvJsonNode Classes(MapProject p, SvJsonObject q)
    {
        string? text = ApiJson.GetString(q, "text");
        string? under = ApiJson.GetString(q, "under");
        if (EditorServices.Classes is not { } cat)
        {
            var used = p.Actors.GroupBy(a => a.Class, StringComparer.OrdinalIgnoreCase)
                .Where(g => text is null || g.Key.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
            var counts = new SvJsonObject();
            foreach (IGrouping<string, Actor> g in used)
            {
                counts.Add(g.Key, SvJsonNode.Integer(g.Count()));
            }

            return ApiJson.Obj(("catalog", SvJsonNode.False), ("used", counts));
        }

        bool all = ApiJson.GetBool(q, "all", false);
        string? root = under is null ? null : (cat.Find(under) ?? throw new ApiException($"unknown class '{under}'", "under")).Name;
        List<string> names = cat.All
            .Where(c => all || (c.Placeable && !c.Abstract))
            .Where(c => root is null || cat.IsA(c.Name, root))
            .Where(c => text is null || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
        int limit = (int)ApiJson.Number(q, "limit", 200);
        return ApiJson.Obj(("count", SvJsonNode.Integer(names.Count)), ("classes", ApiJson.Strings(names.Take(limit))),
            ("more", names.Count > limit ? SvJsonNode.True : null));
    }

    /// <summary>
    /// <c>{"q":"schema","class":"PlayerStart","text":"Team","all":false,"own":false}</c>: the class chain and its
    /// editable properties (with <c>all</c>, every property; with <c>own</c>, only the class's own) as rows of
    /// [name, type, default, declaring class]. Enum value names are listed when the result is short.
    /// </summary>
    public static SvJsonNode Schema(SvJsonObject q)
    {
        string name = ApiJson.GetString(q, "class") ?? throw new ApiException("class is the class name", "class");
        IClassCatalog cat = EditorServices.Classes ?? throw new ApiException("the class catalog is not loaded (no shared/classes.json found)", "class");
        ClassInfo info = cat.Find(name) ?? throw new ApiException($"unknown class '{name}'", "class");
        string? text = ApiJson.GetString(q, "text");
        bool all = ApiJson.GetBool(q, "all", false);
        bool own = ApiJson.GetBool(q, "own", false);
        List<PropInfo> props = (own ? info.Properties : cat.AllProperties(info.Name))
            .Where(pi => all || pi.Editable)
            .Where(pi => text is null || pi.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (pi.Category?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .GroupBy(pi => pi.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();
        bool withEnums = props.Count <= 24;
        var rows = props.Select(pi => (SvJsonNode)ApiJson.Arr([
            ApiJson.Str(pi.Name),
            ApiJson.Str(TypeText(cat, pi, withEnums)),
            cat.Default(info.Name, pi.Name) ?? SvJsonNode.Null,
            ApiJson.Str(MapObjects.Short(pi.DeclaringClass ?? info.Name)),
        ]));
        return ApiJson.Obj(
            ("class", ApiJson.Str(info.Name)),
            ("chain", ApiJson.Strings(cat.Chain(info.Name).Select(c => c.ShortName))),
            ("placeable", ApiJson.Bool(info.Placeable && !info.Abstract)),
            ("cols", ApiJson.Strings(["name", "type", "default", "from"])),
            ("rows", ApiJson.Arr(rows)));
    }

    private static string TypeText(IClassCatalog cat, PropInfo pi, bool withEnums)
    {
        string t = pi.Type switch
        {
            "struct" when pi.Struct is not null => MapObjects.Short(pi.Struct),
            "object" when pi.ObjectClass is not null => "ref:" + MapObjects.Short(pi.ObjectClass),
            "array" => $"array<{pi.Inner ?? "?"}>",
            _ => pi.Type,
        };
        if (pi.Enum is { } e)
        {
            IReadOnlyList<string>? values = withEnums ? cat.EnumValues(e) : null;
            t = values is null ? $"enum:{MapObjects.Short(e)}" : $"enum:{MapObjects.Short(e)}({string.Join("|", values)})";
        }

        return pi.ArrayDim > 1 ? $"{t}[{pi.ArrayDim}]" : t;
    }
}
