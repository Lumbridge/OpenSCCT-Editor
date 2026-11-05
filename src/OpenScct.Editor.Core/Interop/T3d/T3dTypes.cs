using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>
/// Turns T3D property text into SVMap-typed values (spec/svmap conventions: float32 numbers with a point, integers,
/// enums as their index, structs as objects, <c>{"$actor"}</c>/<c>{"$asset"}</c>/<c>{"$class"}</c> references, static
/// arrays as <c>{"$elements"}</c>, dynamic arrays as arrays) and back. Types come from the class schema when there is
/// one; otherwise from the text (a number with a point is a float, <c>True</c> a bool...).
/// <para>Level-local references (<c>MyLevel.X</c>) become actor references when X is an actor, else assets of the
/// map's own package (<c>&lt;MapId&gt;.X</c>), as the SVMap converter writes them.</para>
/// </summary>
public sealed class T3dTypes
{
    public const string LevelPackage = "MyLevel";
    public const string ElementsKey = "$elements";

    public T3dTypes(UeSchema? schema, string mapId)
    {
        Schema = schema;
        MapId = mapId;
    }

    public UeSchema? Schema { get; }

    public string MapId { get; }

    /// <summary>Import: the names of the level's actors, to tell actor references from level-local assets.</summary>
    public HashSet<string> ActorNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The class each referenced asset was written with (<c>Pkg.Grp.Name</c> to <c>Texture</c>): filled on
    /// import, used on export so references keep their exact class.</summary>
    public Dictionary<string, string> RefClasses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Export: the short class of an actor id, or null when the map has no such actor.</summary>
    public Func<string, string?> ActorClass { get; set; } = _ => null;

    /// <summary>Export: a path in the map's own package (<c>&lt;MapId&gt;.X</c>) as the editor names it while the map is
    /// open (<c>MyLevel.X</c>); other paths unchanged.</summary>
    public string ToLevelPath(string path) =>
        path.StartsWith(MapId + ".", StringComparison.OrdinalIgnoreCase) ? LevelPackage + path[MapId.Length..] : path;

    /// <summary>Import: a level-local path (<c>MyLevel.X</c>) as a path in the map's own package; others unchanged.</summary>
    public string FromLevelPath(string path) =>
        path.StartsWith(LevelPackage + ".", StringComparison.OrdinalIgnoreCase) ? MapId + path[LevelPackage.Length..] : path;

    // ---- text to typed values ----------------------------------------------------------------------------------

    /// <summary>The typed property set of an actor's property lines (one line per array element is folded).</summary>
    public PropSet ToProps(string className, IEnumerable<T3dField> fields)
    {
        var set = new PropSet();
        foreach ((string name, SvJsonNode value) in Fold(fields, n => Schema?.FindProperty(className, n)))
        {
            set.Set(name, Normalize(value, Schema?.FindProperty(className, name), DefaultOf(className, name)));
        }

        return set;
    }

    /// <summary>
    /// Export text leaves out struct members equal to the property's default; SVMap writes whole structs. Completes a
    /// struct from the class default, then gives members still missing their zero value (from the struct schema).
    /// </summary>
    private SvJsonNode Normalize(SvJsonNode value, UeProp? type, SvJsonNode? classDefault)
    {
        SvJsonNode v = InteropValues.Complete(value, classDefault);
        if (type is null || Schema is null)
        {
            return v;
        }

        string? structName = type.IsDynamicArray ? type.Inner?.Struct : type.Struct;
        if (structName is null)
        {
            return v;
        }

        return v switch
        {
            SvJsonArray arr => new SvJsonArray(arr.Select(e => ZeroFill(e, structName))),
            SvJsonObject o when o[ElementsKey] is SvJsonObject elements => Wrap(elements.Select(kv => (kv.Key, ZeroFill(kv.Value, structName)))),
            _ => ZeroFill(v, structName),
        };
    }

    private SvJsonNode ZeroFill(SvJsonNode value, string structName)
    {
        if (value is not SvJsonObject o || o.ContainsKey("$actor") || o.ContainsKey("$asset") || o.ContainsKey("$class"))
        {
            return value;
        }

        var result = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            UeProp? member = Schema!.FindMember(structName, kv.Key);
            result.Add(kv.Key, member is null ? kv.Value : Normalize(kv.Value, member, null));
        }

        foreach (UeProp m in Schema!.StructMembers(structName))
        {
            if (!result.ContainsKey(m.Name) && !m.IsStaticArray && Zero(m) is { } zero)
            {
                result.Add(m.Name, zero);
            }
        }

        return result;
    }

    private SvJsonNode? Zero(UeProp m) => m.Type switch
    {
        "float" => SvJsonNode.Float(0),
        "int" or "byte" => SvJsonNode.Integer(0),
        "bool" => SvJsonNode.False,
        "string" or "name" => SvJsonNode.String(m.Type == "name" ? "None" : ""),
        "object" or "class" => SvJsonNode.Null,
        "array" => new SvJsonArray(),
        "struct" when m.Struct is { } s => ZeroFill(new SvJsonObject(), s),
        _ => null,
    };

    private static SvJsonObject Wrap(IEnumerable<(string Key, SvJsonNode Value)> elements)
    {
        var el = new SvJsonObject();
        foreach ((string key, SvJsonNode value) in elements)
        {
            el.Add(key, value);
        }

        var wrapper = new SvJsonObject();
        wrapper.Add(ElementsKey, el);
        return wrapper;
    }

    /// <summary>The class default of a property; without a schema, the UE2 defaults of the scale structs.</summary>
    private SvJsonNode? DefaultOf(string className, string property)
    {
        if (Schema is not null)
        {
            return Schema.DefaultOf(className, property);
        }

        return property switch
        {
            "DrawScale3D" => PropValues.Vector(Vec3.One),
            "MainScale" or "PostScale" or "TempScale" => UnitScale(),
            _ => null,
        };
    }

    private static SvJsonObject UnitScale()
    {
        var o = new SvJsonObject();
        o.Add("Scale", PropValues.Vector(Vec3.One));
        o.Add("SheerAxis", SvJsonNode.Integer(0));
        o.Add("SheerRate", SvJsonNode.Float(0));
        return o;
    }

    public SvJsonNode ToJson(T3dValue value, UeProp? type)
    {
        if (type is null)
        {
            return Untyped(value);
        }

        switch (type.Type)
        {
            case "float":
                return value is T3dAtom f && T3dFormat.TryParseFloat(f.Text, out float fv) ? SvJsonNode.Float(fv) : Untyped(value);
            case "int":
                return value is T3dAtom i && T3dFormat.TryParseInt(i.Text, out long iv) ? SvJsonNode.Integer(iv) : Untyped(value);
            case "byte":
                if (value is T3dAtom b)
                {
                    if (T3dFormat.TryParseInt(b.Text, out long bv))
                    {
                        return SvJsonNode.Integer(bv);
                    }

                    if (type.Enum is { } e && Schema?.EnumMembers(e) is { } members)
                    {
                        int index = IndexOf(members, b.Text);
                        if (index >= 0)
                        {
                            return SvJsonNode.Integer(index);
                        }
                    }

                    return SvJsonNode.String(b.Text);
                }

                return Untyped(value);
            case "bool":
                return value is T3dAtom t && (t.Is("True") || t.Is("False")) ? SvJsonNode.Bool(t.Is("True")) : Untyped(value);
            case "string" or "name" or "delegate":
                return value switch
                {
                    T3dText s => SvJsonNode.String(s.Value),
                    T3dAtom a => SvJsonNode.String(a.Text),
                    _ => Untyped(value),
                };
            case "object" or "class":
                return value switch
                {
                    T3dAtom a when a.Is("None") || a.Text.Length == 0 => SvJsonNode.Null,
                    T3dRef r => Ref(r.Class, r.Path),
                    T3dAtom a => Ref(type.Type == "class" ? "Class" : null, a.Text),
                    _ => Untyped(value),
                };
            case "struct":
                return value switch
                {
                    T3dStruct s => Struct(s.Fields, type.Struct),
                    T3dList { Items.Count: 0 } => new SvJsonObject(),
                    _ => Untyped(value),
                };
            case "array":
                return value is T3dList list ? new SvJsonArray(list.Items.Select(v => ToJson(v, type.Inner))) : Untyped(value);
            default:
                return Untyped(value);
        }
    }

    /// <summary>A value typed by its text alone.</summary>
    public SvJsonNode Untyped(T3dValue value) => value switch
    {
        T3dText s => SvJsonNode.String(s.Value),
        T3dRef r => Ref(r.Class, r.Path),
        T3dStruct s => Struct(s.Fields, null),
        T3dList l => new SvJsonArray(l.Items.Select(Untyped)),
        T3dAtom a when a.Is("True") => SvJsonNode.True,
        T3dAtom a when a.Is("False") => SvJsonNode.False,
        T3dAtom a when a.Is("None") => SvJsonNode.Null,
        T3dAtom a when T3dFormat.IsInteger(a.Text) && T3dFormat.TryParseInt(a.Text, out long l) => SvJsonNode.Integer(l),
        T3dAtom a when T3dFormat.IsDecimal(a.Text) && T3dFormat.TryParseFloat(a.Text, out float f) => SvJsonNode.Float(f),
        // A bare dotted path (MyLevel.X, Pkg.Group.Name) is an object: a string would be quoted, a name has no dots.
        T3dAtom a when IsObjectPath(a.Text) => Ref(null, a.Text),
        T3dAtom a => SvJsonNode.String(a.Text),
        _ => SvJsonNode.Null,
    };

    /// <summary>Two or more identifier-like parts joined by dots, none empty.</summary>
    private static bool IsObjectPath(string text)
    {
        string[] parts = text.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && !p.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '(' or ')' or ','))
            && !char.IsAsciiDigit(parts[0][0]) && parts[0][0] is not ('-' or '+');
    }

    private SvJsonObject Struct(IEnumerable<T3dField> fields, string? structName)
    {
        var o = new SvJsonObject();
        foreach ((string name, SvJsonNode value) in Fold(fields, n => structName is null ? null : Schema?.FindMember(structName, n)))
        {
            o.TryAdd(name, value);
        }

        return o;
    }

    /// <summary>Folds <c>Name(i)=</c> lines into one value per name: an array for dynamic arrays, else
    /// <c>{"$elements": {"i": v}}</c>. Keeps the first-seen order of names.</summary>
    private List<(string Name, SvJsonNode Value)> Fold(IEnumerable<T3dField> fields, Func<string, UeProp?> typeOf)
    {
        var order = new List<string>();
        var plain = new Dictionary<string, SvJsonNode>(StringComparer.OrdinalIgnoreCase);
        var indexed = new Dictionary<string, SortedDictionary<int, SvJsonNode>>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, UeProp?>(StringComparer.OrdinalIgnoreCase);
        foreach (T3dField f in fields)
        {
            if (!types.TryGetValue(f.Name, out UeProp? type))
            {
                type = typeOf(f.Name);
                types[f.Name] = type;
                order.Add(f.Name);
            }

            if (f.Index is int i)
            {
                UeProp? elementType = type is { IsDynamicArray: true } ? type.Inner : type;
                if (!indexed.TryGetValue(f.Name, out SortedDictionary<int, SvJsonNode>? elements))
                {
                    elements = new SortedDictionary<int, SvJsonNode>();
                    indexed[f.Name] = elements;
                }

                elements[i] = ToJson(f.Value, elementType);
            }
            else
            {
                plain[f.Name] = ToJson(f.Value, type);
            }
        }

        var result = new List<(string, SvJsonNode)>();
        foreach (string name in order)
        {
            if (indexed.TryGetValue(name, out SortedDictionary<int, SvJsonNode>? elements))
            {
                if (types[name] is { IsDynamicArray: true })
                {
                    var arr = new SvJsonArray();
                    int max = elements.Keys.Max();
                    for (int i = 0; i <= max; i++)
                    {
                        arr.Add(elements.GetValueOrDefault(i) ?? SvJsonNode.Null);
                    }

                    result.Add((name, arr));
                }
                else
                {
                    var el = new SvJsonObject();
                    foreach (KeyValuePair<int, SvJsonNode> kv in elements)
                    {
                        el.Add(kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value);
                    }

                    var wrapper = new SvJsonObject();
                    wrapper.Add(ElementsKey, el);
                    result.Add((name, wrapper));
                }
            }
            else
            {
                result.Add((name, plain[name]));
            }
        }

        return result;
    }

    private SvJsonNode Ref(string? cls, string path)
    {
        var o = new SvJsonObject();
        if (cls is not null && cls.Equals("Class", StringComparison.OrdinalIgnoreCase))
        {
            o.Add("$class", SvJsonNode.String(path));
            return o;
        }

        if (path.StartsWith(LevelPackage + ".", StringComparison.OrdinalIgnoreCase))
        {
            string local = path[(LevelPackage.Length + 1)..];
            if (ActorNames.Contains(local))
            {
                o.Add("$actor", SvJsonNode.String(local));
                return o;
            }

            path = MapId + "." + local;
        }

        if (cls is not null)
        {
            RefClasses[path] = cls;
        }

        o.Add("$asset", SvJsonNode.String(path));
        return o;
    }

    // ---- typed values to text ----------------------------------------------------------------------------------

    /// <summary>The property lines of a property set (arrays as one line per element, as UE2 exports them).</summary>
    public IEnumerable<T3dField> FromProps(string className, IEnumerable<KeyValuePair<string, SvJsonNode>> props) =>
        Unfold(props, n => Schema?.FindProperty(className, n), topLevel: true);

    public T3dValue FromJson(SvJsonNode node, UeProp? type)
    {
        switch (node)
        {
            case SvJsonLiteral { Kind: SvJsonKind.Null }:
                return T3dAtom.None;
            case SvJsonLiteral l:
                return l.Kind == SvJsonKind.True ? T3dAtom.True : T3dAtom.False;
            case SvJsonNumber { IsFloat: true } f:
                return type?.Type is "int" or "byte" ? T3dAtom.Int((long)Math.Round(f.FloatValue)) : T3dAtom.Float(f.FloatValue);
            case SvJsonNumber n:
                n.TryGetInt64(out long v);
                if (type?.Type == "float")
                {
                    return T3dAtom.Float(v);
                }

                if (type is { Type: "byte", Enum: { } e } && Schema?.EnumMembers(e) is { } members && v >= 0 && v < members.Count)
                {
                    return new T3dAtom(members[(int)v]);
                }

                return T3dAtom.Int(v);
            case SvJsonString s:
                return type?.Type == "byte" ? new T3dAtom(s.Value) : new T3dText(s.Value);
            case SvJsonArray a:
                return new T3dList(a.Select(x => FromJson(x, type is { IsDynamicArray: true } ? type.Inner : null)).ToList());
            case SvJsonObject o:
                if (o["$actor"] is SvJsonString actor)
                {
                    string cls = ActorClass(actor.Value) ?? ShortName(type?.ObjectClass) ?? "Actor";
                    return new T3dRef(cls, LevelPackage + "." + actor.Value);
                }

                if (o["$class"] is SvJsonString klass)
                {
                    return new T3dRef("Class", klass.Value);
                }

                if (o["$asset"] is SvJsonString asset)
                {
                    string? cls = RefClasses.GetValueOrDefault(asset.Value) ?? ShortName(type?.ObjectClass);
                    string path = ToLevelPath(asset.Value);
                    return cls is null || cls.Equals("Object", StringComparison.OrdinalIgnoreCase) ? new T3dAtom(path) : new T3dRef(cls, path);
                }

                string? structName = type?.Struct;
                return new T3dStruct(Unfold(o, m => structName is null ? null : Schema?.FindMember(structName, m), topLevel: false).ToList());
            default:
                return T3dAtom.None;
        }
    }

    private IEnumerable<T3dField> Unfold(IEnumerable<KeyValuePair<string, SvJsonNode>> members, Func<string, UeProp?> typeOf, bool topLevel)
    {
        foreach ((string name, SvJsonNode value) in members)
        {
            UeProp? type = typeOf(name);
            if (value is SvJsonObject o && o[ElementsKey] is SvJsonObject elements)
            {
                foreach ((string key, SvJsonNode element) in elements)
                {
                    if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int i))
                    {
                        yield return new T3dField(name, i, FromJson(element, type));
                    }
                }
            }
            else if (topLevel && value is SvJsonArray arr && (type is null || type.IsDynamicArray))
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    yield return new T3dField(name, i, FromJson(arr[i], type?.Inner));
                }
            }
            else
            {
                yield return new T3dField(name, null, FromJson(value, type));
            }
        }
    }

    private static string? ShortName(string? className) => className is null ? null : InteropValues.ShortClass(className);

    private static int IndexOf(IReadOnlyList<string> members, string name)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (string.Equals(members[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
