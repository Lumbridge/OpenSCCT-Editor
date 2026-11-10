using OpenScct.Content.Json;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Classes;

/// <summary>
/// The editor's class catalog: the game's own class table (<c>shared/classes.json</c> in the OpenSCCT content folder:
/// 669 classes with categories, enums, structs and English defaults) merged over OpenSCCT's generated class metadata
/// (Core, Engine and SBase <c>UcMeta</c>), so it works without content and knows classes OpenSCCT adds. Loads on first
/// use from <see cref="EditorServices.ContentRoot"/>.
/// </summary>
[EditorService]
public sealed class ClassCatalog : IClassCatalog
{
    private readonly Lazy<CatalogData> _data;

    public ClassCatalog()
        : this(() => Load(EditorServices.ContentRoot))
    {
    }

    private ClassCatalog(Func<CatalogData> load) => _data = new Lazy<CatalogData>(load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A catalog over a content folder's <c>shared/classes.json</c> (null: generated metadata only).</summary>
    public static ClassCatalog FromContent(string? contentRoot) => new(() => Load(contentRoot));

    /// <summary>A catalog of the generated metadata only.</summary>
    public static ClassCatalog GeneratedOnly() => new(() => Load(null));

    public string SourceDescription => _data.Value.Source;

    public IEnumerable<ClassInfo> All => _data.Value.Ordered;

    /// <summary>Placeable, non-abstract classes, sorted by name.</summary>
    public IEnumerable<ClassInfo> Placeable => All.Where(c => c.Placeable && !c.Abstract);

    public ClassInfo? Find(string name)
    {
        CatalogData d = _data.Value;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return d.ByPath.TryGetValue(name, out ClassInfo? c) ? c : d.ByShort.GetValueOrDefault(name);
    }

    public IEnumerable<ClassInfo> Chain(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (ClassInfo? c = Find(name); c is not null && seen.Add(c.Name); c = c.Super is { } s ? Find(s) : null)
        {
            yield return c;
        }
    }

    public IReadOnlyList<PropInfo> AllProperties(string name) => Chain(name).SelectMany(c => c.Properties).ToList();

    /// <summary>One property of a class or its supers (ASCII case ignored), or null.</summary>
    public PropInfo? FindProperty(string className, string property) =>
        Chain(className).SelectMany(c => c.Properties).FirstOrDefault(p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase));

    public SvJsonNode? Default(string className, string property)
    {
        foreach (ClassInfo c in Chain(className))
        {
            if (c.Defaults[property] is { } exact)
            {
                return exact;
            }

            foreach (KeyValuePair<string, SvJsonNode> kv in c.Defaults)
            {
                if (string.Equals(kv.Key, property, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }

            // The class table's defaults are already merged down the chain; a class that has them is final.
            if (c.Defaults.Count > 0)
            {
                return null;
            }
        }

        return null;
    }

    public bool IsA(string className, string ancestor)
    {
        ClassInfo? target = Find(ancestor);
        return target is not null && Chain(className).Any(c => string.Equals(c.Name, target.Name, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string>? EnumValues(string enumName)
    {
        CatalogData d = _data.Value;
        return d.Enums.TryGetValue(enumName, out IReadOnlyList<string>? v) ? v : d.EnumsByShort.GetValueOrDefault(enumName);
    }

    public IReadOnlyList<PropInfo>? StructMembers(string structName)
    {
        CatalogData d = _data.Value;
        return d.Structs.TryGetValue(structName, out IReadOnlyList<PropInfo>? v) ? v : d.StructsByShort.GetValueOrDefault(structName);
    }

    private static CatalogData Load(string? contentRoot)
    {
        var data = new CatalogData();
        string? path = contentRoot is null ? null : Path.Combine(contentRoot, "shared", "classes.json");
        int fromTable = 0;
        if (path is not null && File.Exists(path))
        {
            fromTable = ClassTableReader.Read(path, data);
        }

        int generated = GeneratedClasses.AddMissing(data);
        foreach (string name in new[] { "SBase.SObjective", "SBase.SObjectiveTrigger" })
        {
            if (data.ByPath.TryGetValue(name, out ClassInfo? cls))
                data.ByPath[name] = cls with { Properties = [.. cls.Properties,
                    new PropInfo(Gameplay.ObjectiveVisibility.Property, "bool", "Objective", true, null, null, null, 1, null, name)] };
        }
        data.Finish();
        data.Source = fromTable > 0
            ? $"{Path.GetFullPath(path!)} ({fromTable} classes)" + (generated > 0 ? $" + generated metadata ({generated} more)" : "")
            : $"generated metadata ({generated} classes; no shared/classes.json found)";
        return data;
    }
}

/// <summary>The loaded catalog, keyed for lookups (full and bare names, ASCII case ignored).</summary>
internal sealed class CatalogData
{
    public Dictionary<string, ClassInfo> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ClassInfo?> ByShort { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ClassInfo> Ordered { get; private set; } = new();

    public Dictionary<string, IReadOnlyList<string>> Enums { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, IReadOnlyList<string>?> EnumsByShort { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, IReadOnlyList<PropInfo>> Structs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, IReadOnlyList<PropInfo>?> StructsByShort { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Source { get; set; } = "";

    public void Add(ClassInfo c) => ByPath.TryAdd(c.Name, c);

    /// <summary>Builds the bare-name indexes; a bare name used by two packages resolves to nothing.</summary>
    public void Finish()
    {
        Ordered = ByPath.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        foreach (ClassInfo c in Ordered)
        {
            ByShort[c.ShortName] = ByShort.ContainsKey(c.ShortName) ? null : c;
        }

        foreach (KeyValuePair<string, IReadOnlyList<string>> e in Enums.ToList())
        {
            string bare = Bare(e.Key);
            EnumsByShort[bare] = EnumsByShort.ContainsKey(bare) ? null : e.Value;
        }

        foreach (KeyValuePair<string, IReadOnlyList<PropInfo>> s in Structs.ToList())
        {
            string bare = Bare(s.Key);
            StructsByShort[bare] = StructsByShort.ContainsKey(bare) ? null : s.Value;
        }
    }

    private static string Bare(string path) => path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;
}
