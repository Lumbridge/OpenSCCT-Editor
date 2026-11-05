using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Services;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose classes</c> and <c>ose schema</c>: the class catalog for the inspector, scripts.</summary>
internal static class ClassesCommands
{
    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("classes", "classes [--placeable] [--filter text] [--isa Class] [--json]",
            "Lists the actor classes (name, super, placeable/abstract, editable property count).", ClassesAsync),
        new("schema", "schema <Class> [--all] [--own] [--json]",
            "Prints a class's properties with type, category, enum values and defaults (editable unless --all; --own: declared by the class itself).", SchemaAsync),
    ];

    private static IClassCatalog Catalog()
    {
        EnsureServices();
        return EditorServices.Classes ?? ClassCatalog.FromContent(EditorServices.ContentRoot);
    }

    private static Task<int> ClassesAsync(CliArgs a)
    {
        IClassCatalog cat = Catalog();
        string? filter = a.Get("filter");
        string? isa = a.Get("isa");
        List<ClassInfo> list = cat.All
            .Where(c => !a.Has("placeable") || (c.Placeable && !c.Abstract))
            .Where(c => filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(c => isa is null || cat.IsA(c.Name, isa))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        if (a.Has("json"))
        {
            var o = new SvJsonObject();
            o.Add("source", SvJsonNode.String(cat.SourceDescription));
            o.Add("classes", new SvJsonArray(list.Select(c =>
            {
                var j = new SvJsonObject();
                j.Add("name", SvJsonNode.String(c.Name));
                if (c.Super is not null)
                {
                    j.Add("super", SvJsonNode.String(c.Super));
                }

                j.Add("placeable", SvJsonNode.Bool(c.Placeable && !c.Abstract));
                if (c.Abstract)
                {
                    j.Add("abstract", SvJsonNode.True);
                }

                j.Add("editable", SvJsonNode.Integer(cat.AllProperties(c.Name).Count(p => p.Editable)));
                return j;
            })));
            PrintJson(o, a);
            return Task.FromResult(0);
        }

        Console.WriteLine($"{list.Count} classes from {cat.SourceDescription}");
        foreach (ClassInfo c in list)
        {
            string tag = c.Abstract ? "abstract" : c.Placeable ? "placeable" : "";
            Console.WriteLine($"  {c.Name,-44} {c.Super ?? "",-32} {tag}");
        }

        return Task.FromResult(0);
    }

    private static Task<int> SchemaAsync(CliArgs a)
    {
        IClassCatalog cat = Catalog();
        string name = a.Positional(0) ?? throw new ArgumentException("give a class name");
        ClassInfo info = cat.Find(name) ?? throw new ArgumentException($"no class '{name}' in {cat.SourceDescription}");
        IEnumerable<PropInfo> props = a.Has("own") ? info.Properties : cat.AllProperties(info.Name);
        List<PropInfo> list = props.Where(p => a.Has("all") || p.Editable).ToList();
        List<string> chain = cat.Chain(info.Name).Select(c => c.Name).ToList();
        if (a.Has("json"))
        {
            var o = new SvJsonObject();
            o.Add("name", SvJsonNode.String(info.Name));
            o.Add("chain", Array(chain));
            o.Add("placeable", SvJsonNode.Bool(info.Placeable && !info.Abstract));
            o.Add("abstract", SvJsonNode.Bool(info.Abstract));
            o.Add("properties", new SvJsonArray(list.Select(p => PropJson(cat, info.Name, p))));
            PrintJson(o, a);
            return Task.FromResult(0);
        }

        Console.WriteLine($"{info.Name}  ({string.Join(" < ", chain)}){(info.Abstract ? "  abstract" : info.Placeable ? "  placeable" : "")}");
        foreach (IGrouping<string, PropInfo> g in list.GroupBy(p => p.Category ?? "(none)").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"[{g.Key}]");
            foreach (PropInfo p in g.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                string def = cat.Default(info.Name, p.Name) is { } d ? " = " + CanonicalJson.ToCanonicalString(d) : "";
                string values = p.Enum is { } e && cat.EnumValues(e) is { } ev ? $" {{{string.Join(", ", ev)}}}" : "";
                Console.WriteLine($"  {p.Name,-32} {TypeText(p)}{values}{def}{(p.Editable ? "" : "  (read-only)")}");
            }
        }

        return Task.FromResult(0);
    }

    private static string TypeText(PropInfo p)
    {
        string t = p.Type switch
        {
            "array" => $"array<{p.Inner}{Detail(p)}>",
            _ => p.Type + Detail(p),
        };

        return p.ArrayDim > 1 ? $"{t}[{p.ArrayDim}]" : t;
    }

    private static string Detail(PropInfo p) =>
        p.Struct is { } s ? $" {s}" : p.Enum is { } e ? $" {e}" : p.ObjectClass is { } c ? $" {c}" : "";

    private static SvJsonObject PropJson(IClassCatalog cat, string className, PropInfo p)
    {
        var o = new SvJsonObject();
        o.Add("name", SvJsonNode.String(p.Name));
        o.Add("type", SvJsonNode.String(p.Type));
        if (p.Inner is not null)
        {
            o.Add("inner", SvJsonNode.String(p.Inner));
        }

        if (p.Category is not null)
        {
            o.Add("category", SvJsonNode.String(p.Category));
        }

        if (!p.Editable)
        {
            o.Add("readOnly", SvJsonNode.True);
        }

        if (p.ArrayDim > 1)
        {
            o.Add("arrayDim", SvJsonNode.Integer(p.ArrayDim));
        }

        if (p.Struct is not null)
        {
            o.Add("struct", SvJsonNode.String(p.Struct));
        }

        if (p.ObjectClass is not null)
        {
            o.Add("class", SvJsonNode.String(p.ObjectClass));
        }

        if (p.Enum is not null)
        {
            o.Add("enum", SvJsonNode.String(p.Enum));
            if (cat.EnumValues(p.Enum) is { } values)
            {
                o.Add("values", Array(values));
            }
        }

        if (cat.Default(className, p.Name) is { } d)
        {
            o.Add("default", d);
        }

        if (p.DeclaringClass is not null && !string.Equals(p.DeclaringClass, className, StringComparison.OrdinalIgnoreCase))
        {
            o.Add("from", SvJsonNode.String(p.DeclaringClass));
        }

        return o;
    }
}
