using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Interop;

/// <summary>The declared type of a UnrealScript property or struct member (from <c>shared/classes.json</c>).</summary>
/// <param name="Type">bool, byte, int, float, string, name, object, class, struct, array or delegate.</param>
/// <param name="ObjectClass">For object and class properties, the referenced class (<c>Engine.Texture</c>).</param>
/// <param name="Inner">For dynamic arrays, the element type.</param>
public sealed record UeProp(string Name, string Type, int ArrayDim, string? Enum, string? Struct, string? ObjectClass, UeProp? Inner)
{
    public bool IsStaticArray => ArrayDim > 1;

    public bool IsDynamicArray => Type == "array";

    /// <summary>CPF_Localized: the game takes the value from the language's text file, not from the package.</summary>
    public bool Localized { get; init; }

    /// <summary>CPF_ExportObject: the objects it refers to are exported inside the owner's export text (Begin Object
    /// blocks), as an Emitter's particle emitters are.</summary>
    public bool ExportObject { get; init; }
}

/// <summary>
/// The UE2 type information interop needs to turn export text into typed SVMap values and back: property types per
/// class, struct members and enum names. Read from the content's <c>shared/classes.json</c> (svm.classes/1, built from
/// the player's game files); without it the T3D reader falls back to typing values by their text.
/// </summary>
public sealed class UeSchema
{
    private const uint CpfLocalized = 0x8000;
    private const uint CpfExportObject = 0x8;

    private static readonly Dictionary<string, UeSchema> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_lock = new();

    private readonly Dictionary<string, ClassEntry> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shortNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StructEntry> _structs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _enums = new(StringComparer.OrdinalIgnoreCase);

    private UeSchema(string source) => Source = source;

    /// <summary>The file the schema was read from.</summary>
    public string Source { get; }

    public int ClassCount => _classes.Count;

    /// <summary>The schema of a content folder (cached), or null when it has no <c>shared/classes.json</c>.</summary>
    public static UeSchema? Load(string? contentRoot)
    {
        if (string.IsNullOrEmpty(contentRoot))
        {
            return null;
        }

        string path = Path.GetFullPath(Path.Combine(contentRoot, "shared", "classes.json"));
        lock (s_lock)
        {
            if (s_cache.TryGetValue(path, out UeSchema? cached))
            {
                return cached;
            }

            if (!File.Exists(path))
            {
                return null;
            }

            UeSchema schema = Parse(SvJson.Parse(File.ReadAllBytes(path), path), path);
            s_cache[path] = schema;
            return schema;
        }
    }

    internal static void Invalidate(string contentRoot)
    {
        lock (s_lock)
        {
            s_cache.Remove(Path.GetFullPath(Path.Combine(contentRoot, "shared", "classes.json")));
        }
    }

    public static UeSchema Parse(SvJsonNode root, string source)
    {
        var s = new UeSchema(source);
        if (root is not SvJsonObject o)
        {
            return s;
        }

        if (o["classes"] is SvJsonObject classes)
        {
            foreach (KeyValuePair<string, SvJsonNode> kv in classes)
            {
                if (kv.Value is not SvJsonObject c)
                {
                    continue;
                }

                var entry = new ClassEntry(kv.Key, Str(c["super"]), Props(c["properties"]), c["defaults"] as SvJsonObject);
                s._classes[kv.Key] = entry;
                string shortName = kv.Key[(kv.Key.LastIndexOf('.') + 1)..];
                // Short names are unique in SCCT's packages except for GUI duplicates; keep the first non-GUI one.
                if (!s._shortNames.TryGetValue(shortName, out string? existing) || existing.StartsWith("GUI.", StringComparison.Ordinal))
                {
                    s._shortNames[shortName] = kv.Key;
                }
            }
        }

        if (o["structs"] is SvJsonObject structs)
        {
            foreach (KeyValuePair<string, SvJsonNode> kv in structs)
            {
                if (kv.Value is SvJsonObject st)
                {
                    s._structs[kv.Key] = new StructEntry(Str(st["super"]), Props(st["properties"]));
                }
            }
        }

        if (o["enums"] is SvJsonObject enums)
        {
            foreach (KeyValuePair<string, SvJsonNode> kv in enums)
            {
                if (kv.Value is SvJsonArray members)
                {
                    s._enums[kv.Key] = members.Select(m => m is SvJsonString ms ? ms.Value : "").ToList();
                }
            }
        }

        return s;
    }

    /// <summary>The full <c>Package.Class</c> name of a short or full class name, or null when unknown.</summary>
    public string? ResolveClass(string name)
    {
        if (_classes.ContainsKey(name))
        {
            return _classes[name].Name;
        }

        return _shortNames.GetValueOrDefault(name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name);
    }

    public bool IsA(string className, string ancestor)
    {
        string? full = ResolveClass(className);
        string? target = ResolveClass(ancestor) ?? ancestor;
        for (int guard = 0; full is not null && guard < 64; guard++)
        {
            if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            full = _classes.TryGetValue(full, out ClassEntry? c) ? c.Super : null;
        }

        return false;
    }

    /// <summary>A property of a class or of its supers.</summary>
    public UeProp? FindProperty(string className, string property)
    {
        string? full = ResolveClass(className);
        for (int guard = 0; full is not null && guard < 64; guard++)
        {
            if (!_classes.TryGetValue(full, out ClassEntry? c))
            {
                return null;
            }

            if (c.Props.TryGetValue(property, out UeProp? p))
            {
                return p;
            }

            full = c.Super;
        }

        return null;
    }

    /// <summary>A member of a struct or of its supers.</summary>
    public UeProp? FindMember(string structName, string member)
    {
        string? name = structName;
        for (int guard = 0; name is not null && guard < 32; guard++)
        {
            if (!_structs.TryGetValue(name, out StructEntry? s))
            {
                return null;
            }

            if (s.Props.TryGetValue(member, out UeProp? p))
            {
                return p;
            }

            name = s.Super;
        }

        return null;
    }

    public IReadOnlyList<string>? EnumMembers(string enumName) => _enums.GetValueOrDefault(enumName);

    /// <summary>All members of a struct, inherited ones first; empty when the struct is unknown.</summary>
    public IReadOnlyList<UeProp> StructMembers(string structName)
    {
        var chain = new List<StructEntry>();
        string? name = structName;
        for (int guard = 0; name is not null && guard < 32 && _structs.TryGetValue(name, out StructEntry? s); guard++)
        {
            chain.Add(s);
            name = s.Super;
        }

        chain.Reverse();
        return chain.SelectMany(s => s.Props.Values).ToList();
    }

    /// <summary>The properties a class declares itself (not its supers'), in declaration order.</summary>
    public IReadOnlyList<UeProp> OwnProperties(string className) =>
        ResolveClass(className) is { } full && _classes.TryGetValue(full, out ClassEntry? c) ? c.Props.Values.ToList() : [];

    /// <summary>The full name of a class's super class, or null.</summary>
    public string? SuperOf(string className) =>
        ResolveClass(className) is { } full && _classes.TryGetValue(full, out ClassEntry? c) ? c.Super : null;

    /// <summary>All members of a struct in UE2 serialization order: its own first, then each super's, each in
    /// declaration order (the order of a stored nested list).</summary>
    public IReadOnlyList<UeProp> StructMembersInFieldOrder(string structName)
    {
        var members = new List<UeProp>();
        string? name = structName;
        for (int guard = 0; name is not null && guard < 32 && _structs.TryGetValue(name, out StructEntry? s); guard++)
        {
            members.AddRange(s.Props.Values);
            name = s.Super;
        }

        return members;
    }

    /// <summary>The class default of a property (its value in the class default object), or null when unset.</summary>
    public SvJsonNode? DefaultOf(string className, string property)
    {
        string? full = ResolveClass(className);
        for (int guard = 0; full is not null && guard < 64; guard++)
        {
            if (!_classes.TryGetValue(full, out ClassEntry? c))
            {
                return null;
            }

            if (c.Defaults?[property] is { } value)
            {
                return value;
            }

            full = c.Super;
        }

        return null;
    }

    private static Dictionary<string, UeProp> Props(SvJsonNode? node)
    {
        var props = new Dictionary<string, UeProp>(StringComparer.OrdinalIgnoreCase);
        if (node is SvJsonArray list)
        {
            foreach (SvJsonNode n in list)
            {
                if (n is SvJsonObject p && Prop(p) is { } prop)
                {
                    props.TryAdd(prop.Name, prop);
                }
            }
        }

        return props;
    }

    private static UeProp? Prop(SvJsonObject p)
    {
        string? name = Str(p["name"]);
        string? type = Str(p["type"]);
        if (name is null || type is null)
        {
            return null;
        }

        int dim = p["arrayDim"] is SvJsonNumber n && n.TryGetInt32(out int d) ? d : 1;
        UeProp? inner = p["inner"] is SvJsonObject i ? Prop(i) : null;
        uint flags = Str(p["flags"]) is { } text && text.StartsWith("0x", StringComparison.Ordinal)
            && uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint f)
            ? f : 0;
        return new UeProp(name, type, dim, Str(p["enum"]), Str(p["struct"]), Str(p["class"]), inner)
        {
            Localized = (flags & CpfLocalized) != 0,
            ExportObject = (flags & CpfExportObject) != 0,
        };
    }

    private static string? Str(SvJsonNode? n) => n is SvJsonString s ? s.Value : null;

    private sealed record ClassEntry(string Name, string? Super, Dictionary<string, UeProp> Props, SvJsonObject? Defaults);

    private sealed record StructEntry(string? Super, Dictionary<string, UeProp> Props);
}
