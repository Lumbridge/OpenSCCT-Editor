using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>How a property the class data does not declare was tagged in the file it came from (its tag type id and
/// struct name), so it is written back with the same tag.</summary>
public readonly record struct UTagHint(int Type, string? Struct);

/// <summary>
/// Writes tagged property lists (SDC-FORMAT.md section 4.1) from SVMap property JSON: the inverse of
/// <see cref="UPropertyReader"/>. Properties are typed by the class data and written as UE2 writes them: in the order
/// of the class's fields (the class's own first, then each super's, each in declaration order), one tag per stored
/// element of a static array, bools in the tag, structs other than Vector, Rotator and Color as nested lists, and the
/// tag's size without the second compact of any name. A value the reader kept as <c>{"$raw"}</c> is written back as
/// stored.
/// </summary>
public sealed class UPropertyWriter
{
    private const int TagByte = 1, TagInt = 2, TagBool = 3, TagFloat = 4, TagObject = 5, TagName = 6, TagString = 7,
        TagArray = 9, TagStruct = 10, TagStr = 13;

    private static readonly Dictionary<string, int> s_tagIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ByteProperty"] = 1, ["IntProperty"] = 2, ["BoolProperty"] = 3, ["FloatProperty"] = 4, ["ObjectProperty"] = 5,
        ["NameProperty"] = 6, ["StringProperty"] = 7, ["ClassProperty"] = 8, ["ArrayProperty"] = 9, ["StructProperty"] = 10,
        ["VectorProperty"] = 11, ["RotatorProperty"] = 12, ["StrProperty"] = 13, ["MapProperty"] = 14, ["FixedArrayProperty"] = 15,
    };

    private readonly UeSchema? _schema;
    private readonly Func<SvJsonNode, int> _resolve;
    private readonly Dictionary<string, List<string>> _fieldOrder = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="resolve">A reference value (<c>{"$actor"}</c>, <c>{"$asset"}</c>, <c>{"$class"}</c>) to its
    /// objref; 0 for none.</param>
    /// <param name="hints">Tags of undeclared properties by <c>(class, property)</c>, from the source package.</param>
    public UPropertyWriter(UeSchema? schema, Func<SvJsonNode, int> resolve, IReadOnlyDictionary<(string, string), UTagHint>? hints = null)
    {
        _schema = schema;
        _resolve = resolve;
        Hints = hints ?? new Dictionary<(string, string), UTagHint>();
    }

    public IReadOnlyDictionary<(string, string), UTagHint> Hints { get; }

    /// <summary>Per class, the order a source package stores properties the class data does not declare in (a class
    /// defined in the map declares its own fields first, in an order only its script knows).</summary>
    public Dictionary<string, List<string>> StoredOrder { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Problems met (a value of the wrong kind), for findings.</summary>
    public List<string> Problems { get; } = new();

    /// <summary>An actor's or object's state frame (RF_HasStack): its class as node and state, every probe, no latent
    /// action, no code offset, as the stock editor writes every actor of a map. <paramref name="latent"/> keeps the
    /// stray value a source file holds.</summary>
    public static void StateFrame(UWriter w, int classRef, int latent = 0)
    {
        w.ObjRef(classRef);
        w.ObjRef(classRef);
        w.U64(ulong.MaxValue);
        w.I32(latent);
        if (classRef != 0)
        {
            w.Compact(-1);
        }
    }

    /// <summary>Writes an object's tagged properties and the closing "None". <paramref name="typingClass"/> is the class
    /// whose declared properties type them (null: every value is typed by its hint or its JSON kind);
    /// <paramref name="hintClass"/> keys the hints of undeclared ones; <paramref name="storedOrder"/> is the order a
    /// source object stored its properties in.</summary>
    public void WriteObject(UWriter w, string? typingClass, string hintClass, IEnumerable<KeyValuePair<string, SvJsonNode>> props, string where,
        IReadOnlyList<string>? storedOrder = null)
    {
        var declared = new List<(int Order, string Key, SvJsonNode Value, UeProp Prop)>();
        var undeclared = new List<(string Key, SvJsonNode Value)>();
        List<string> order = typingClass is null ? [] : FieldOrder(typingClass);
        foreach ((string key, SvJsonNode value) in props)
        {
            UeProp? prop = typingClass is null ? null : _schema?.FindProperty(typingClass, key);
            if (prop is not null && CanWrite(prop) && !PropValues.IsRaw(value))
            {
                int at = order.FindIndex(n => n.Equals(prop.Name, StringComparison.OrdinalIgnoreCase));
                declared.Add((at < 0 ? int.MaxValue : at, prop.Name, value, prop));
            }
            else
            {
                undeclared.Add((key, value));
            }
        }

        // A class defined in the map declares its own fields first: they are the ones the class data lacks.
        IReadOnlyList<string>? stored = storedOrder ?? (StoredOrder.TryGetValue(hintClass, out List<string>? classOrder) ? classOrder : null);
        if (stored is not null)
        {
            int At(string key)
            {
                for (int i = 0; i < stored.Count; i++)
                {
                    if (stored[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                return int.MaxValue;
            }

            undeclared = undeclared.OrderBy(u => At(u.Key)).ToList();
        }

        foreach ((string key, SvJsonNode value) in undeclared)
        {
            WriteUndeclared(w, key, value, Hints.TryGetValue((hintClass, key), out UTagHint h) ? h : null, $"{where}.{key}");
        }

        foreach ((int _, string key, SvJsonNode value, UeProp prop) in declared.OrderBy(d => d.Order))
        {
            WriteDeclared(w, key, value, prop, $"{where}.{key}", depth: 0);
        }

        w.Name("None");
    }

    /// <summary>A class's fields in UE2 serialization order: its own, then each super's, in declaration order.</summary>
    private List<string> FieldOrder(string cls)
    {
        if (_fieldOrder.TryGetValue(cls, out List<string>? cached))
        {
            return cached;
        }

        var names = new List<string>();
        string? c = _schema?.ResolveClass(cls);
        for (int guard = 0; c is not null && guard < 64; guard++)
        {
            names.AddRange(_schema!.OwnProperties(c).Select(p => p.Name));
            c = _schema.SuperOf(c);
        }

        _fieldOrder[cls] = names;
        return names;
    }

    private void WriteDeclared(UWriter w, string key, SvJsonNode value, UeProp prop, string where, int depth)
    {
        if (value is SvJsonObject o && o[T3d.T3dTypes.ElementsKey] is SvJsonObject elements)
        {
            foreach ((string idx, SvJsonNode v) in elements.OrderBy(kv => int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0))
            {
                int index = int.TryParse(idx, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0;
                WriteTag(w, key, index, prop, v, where, depth);
            }

            return;
        }

        WriteTag(w, key, 0, prop, value, where, depth);
    }

    private void WriteTag(UWriter w, string key, int index, UeProp prop, SvJsonNode value, string where, int depth)
    {
        if (prop.Type == "bool")
        {
            w.Name(key);
            w.U8((byte)(TagBool | (5 << 4) | (Bool(value) ? 0x80 : 0)));
            w.U8(0);
            return;
        }

        int type = TagOf(prop);
        string? structName = prop.Type == "struct" ? ShortName(prop.Struct) : null;
        Action<UWriter> body = prop.IsDynamicArray ? x => Array(x, prop, value, where, depth) : x => Item(x, prop, value, where, depth);
        Tag(w, key, type, structName, index, body);
    }

    /// <summary>A tag and its value: the size is measured on a scratch writer first (string bytes do not depend on
    /// the offset), leaving out the names' second compacts as the engine's sizes do.</summary>
    private void Tag(UWriter w, string key, int type, string? structName, int index, Action<UWriter> body)
    {
        var scratch = new UWriter(w.Package);
        body(scratch);
        int size = scratch.Pos - scratch.NameExtra;
        int code = size switch
        {
            1 => 0,
            2 => 1,
            4 => 2,
            12 => 3,
            16 => 4,
            <= 0xFF => 5,
            <= 0xFFFF => 6,
            _ => 7,
        };
        w.Name(key);
        w.U8((byte)(type | (code << 4) | (index != 0 ? 0x80 : 0)));
        if (structName is not null)
        {
            w.Name(structName);
        }

        switch (code)
        {
            case 5:
                w.U8((byte)size);
                break;
            case 6:
                w.U16((ushort)size);
                break;
            case 7:
                w.I32(size);
                break;
        }

        if (index != 0)
        {
            ArrayIndex(w, index);
        }

        body(w);
    }

    private static void ArrayIndex(UWriter w, int index)
    {
        if (index < 0x80)
        {
            w.U8((byte)index);
        }
        else if (index < 0x4000)
        {
            w.U8((byte)((index >> 8) | 0x80));
            w.U8((byte)index);
        }
        else
        {
            w.U8((byte)((index >> 24) | 0xC0));
            w.U8((byte)(index >> 16));
            w.U8((byte)(index >> 8));
            w.U8((byte)index);
        }
    }

    private void Array(UWriter w, UeProp prop, SvJsonNode value, string where, int depth)
    {
        UeProp inner = prop.Inner ?? throw new InvalidOperationException($"{where}: the class data has no element type for this array");
        IReadOnlyList<SvJsonNode> items = value as SvJsonArray ?? (IReadOnlyList<SvJsonNode>)[];
        if (value is not SvJsonArray && value.Kind != SvJsonKind.Null)
        {
            Problems.Add($"{where}: an array property holds {value.Kind}; written empty");
        }

        w.Compact(items.Count);
        foreach (SvJsonNode item in items)
        {
            Item(w, inner, item, where, depth + 1);
        }
    }

    /// <summary>One value of a declared type (UE2 SerializeItem).</summary>
    private void Item(UWriter w, UeProp prop, SvJsonNode value, string where, int depth)
    {
        switch (prop.Type)
        {
            case "byte":
                w.U8((byte)EnumOrInt(value, prop.Enum));
                break;
            case "int":
                w.I32((int)Int(value));
                break;
            case "float":
                w.F32(Float(value));
                break;
            case "bool":
                w.U8(Bool(value) ? (byte)1 : (byte)0);
                break;
            case "object":
            case "class":
                w.ObjRef(_resolve(value));
                break;
            case "name":
                w.Name(Text(value) is { Length: > 0 } n ? n : "None");
                break;
            case "string":
                w.FString(Text(value) ?? "");
                break;
            case "delegate":
                SvJsonObject? d = value as SvJsonObject;
                w.ObjRef(d?["object"] is { } obj ? _resolve(obj) : 0);
                w.Name(Text(d?["function"]) is { Length: > 0 } fn ? fn : "None");
                break;
            case "struct":
                Struct(w, prop.Struct, value, where, depth);
                break;
            case "array":
                Array(w, prop, value, where, depth);
                break;
            default:
                throw new InvalidOperationException($"{where}: no writer for a {prop.Type} property");
        }
    }

    private void Struct(UWriter w, string? structName, SvJsonNode value, string where, int depth)
    {
        if (depth > 16)
        {
            throw new InvalidOperationException($"{where}: struct nesting too deep");
        }

        var o = value as SvJsonObject ?? new SvJsonObject();
        if (NativeStruct(w, ShortName(structName), o))
        {
            return;
        }

        IReadOnlyList<UeProp> members = structName is null || _schema is null ? [] : _schema.StructMembersInFieldOrder(structName);
        foreach (UeProp m in members)
        {
            if (o[m.Name] is { } v && !PropValues.IsRaw(v) && CanWrite(m))
            {
                WriteDeclared(w, m.Name, v, m, $"{where}.{m.Name}", depth + 1);
            }
        }

        foreach ((string key, SvJsonNode v) in o)
        {
            if (!members.Any(m => m.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && !PropValues.IsRaw(v) && CanWrite(m)))
            {
                WriteUndeclared(w, key, v, null, $"{where}.{key}");
            }
        }

        w.Name("None");
    }

    private static bool NativeStruct(UWriter w, string shortName, SvJsonObject o)
    {
        if (shortName.Equals("Vector", StringComparison.OrdinalIgnoreCase))
        {
            w.F32(Float(o["X"]));
            w.F32(Float(o["Y"]));
            w.F32(Float(o["Z"]));
            return true;
        }

        if (shortName.Equals("Rotator", StringComparison.OrdinalIgnoreCase))
        {
            w.I32((int)Int(o["Pitch"]));
            w.I32((int)Int(o["Yaw"]));
            w.I32((int)Int(o["Roll"]));
            return true;
        }

        if (shortName.Equals("Color", StringComparison.OrdinalIgnoreCase))
        {
            w.U8((byte)Int(o["B"]));
            w.U8((byte)Int(o["G"]));
            w.U8((byte)Int(o["R"]));
            w.U8((byte)Int(o["A"]));
            return true;
        }

        return false;
    }

    /// <summary>A property the class data does not declare: as stored when raw, else by its tag in the source (the
    /// hint), else by its JSON kind.</summary>
    private void WriteUndeclared(UWriter w, string key, SvJsonNode value, UTagHint? hint, string where)
    {
        if (value is SvJsonObject ro && ro[PropValues.RawKey] is SvJsonObject raw)
        {
            int type = Text(raw["type"]) is { } t && s_tagIds.TryGetValue(t, out int id) ? id : TagByte;
            byte[] bytes = Convert.FromHexString(Text(raw["hex"]) ?? "");
            Tag(w, key, type, Text(raw["struct"]), 0, x => x.Bytes(bytes));
            return;
        }

        if (value is SvJsonObject eo && eo[T3d.T3dTypes.ElementsKey] is SvJsonObject elements)
        {
            foreach ((string idx, SvJsonNode v) in elements)
            {
                int index = int.TryParse(idx, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0;
                WriteUndeclaredTag(w, key, index, v, hint, where);
            }

            return;
        }

        WriteUndeclaredTag(w, key, 0, value, hint, where);
    }

    private void WriteUndeclaredTag(UWriter w, string key, int index, SvJsonNode value, UTagHint? hint, string where)
    {
        int type = hint?.Type ?? value.Kind switch
        {
            SvJsonKind.True or SvJsonKind.False => TagBool,
            SvJsonKind.Integer => TagInt,
            SvJsonKind.Float or SvJsonKind.Null => TagFloat,
            SvJsonKind.String => TagStr,
            SvJsonKind.Object when IsRef(value) => TagObject,
            _ => TagStruct,
        };
        if (hint is null && type != TagBool)
        {
            // Nothing says the stored type: a byte, an enum or a name written this way does not load (the engine drops
            // a value whose tag type is not the declared one).
            Problems.Add($"{where}.{key}: the class data does not declare it and no stored actor tags it; written as {s_tagIds.FirstOrDefault(kv => kv.Value == type).Key ?? "a struct"} by its value");
        }

        switch (type)
        {
            case TagBool:
                w.Name(key);
                w.U8((byte)(TagBool | (5 << 4) | (Bool(value) ? 0x80 : 0)));
                w.U8(0);
                return;
            case TagByte:
                Tag(w, key, type, null, index, x => x.U8((byte)Int(value)));
                return;
            case TagInt:
                Tag(w, key, type, null, index, x => x.I32((int)Int(value)));
                return;
            case TagFloat:
                Tag(w, key, type, null, index, x => x.F32(Float(value)));
                return;
            case TagName:
                Tag(w, key, type, null, index, x => x.Name(Text(value) is { Length: > 0 } n ? n : "None"));
                return;
            case TagStr or TagString:
                Tag(w, key, type, null, index, x => x.FString(Text(value) ?? ""));
                return;
            case TagObject or 8:
                Tag(w, key, type, null, index, x => x.ObjRef(_resolve(value)));
                return;
            case TagStruct or 11 or 12:
            {
                var o = value as SvJsonObject ?? new SvJsonObject();
                string structName = hint?.Struct ?? (o.ContainsKey("Pitch") ? "Rotator" : o.Count == 3 && o.ContainsKey("X") ? "Vector"
                    : o.Count == 4 && o.ContainsKey("R") && o.ContainsKey("A") ? "Color" : "");
                if (structName.Length == 0)
                {
                    Problems.Add($"{where}: a struct the class data does not declare and the source did not tag; left out");
                    return;
                }

                Tag(w, key, TagStruct, structName, index, x =>
                {
                    if (!NativeStruct(x, structName, o))
                    {
                        foreach ((string k, SvJsonNode v) in o)
                        {
                            WriteUndeclared(x, k, v, null, $"{where}.{k}");
                        }

                        x.Name("None");
                    }
                });
                return;
            }

            default:
                Problems.Add($"{where}: tag type {type} cannot be written from {value.Kind}; left out");
                return;
        }
    }

    /// <summary>The tag type UE2 writes for a declared type: class properties are object tags, delegates the old
    /// string id, every struct a struct tag.</summary>
    private static int TagOf(UeProp prop) => prop.Type switch
    {
        "byte" => TagByte,
        "int" => TagInt,
        "float" => TagFloat,
        "object" or "class" => TagObject,
        "name" => TagName,
        "string" => TagStr,
        "delegate" => TagString,
        "array" => TagArray,
        "struct" => TagStruct,
        _ => throw new InvalidOperationException($"no tag for a {prop.Type} property"),
    };

    private bool CanWrite(UeProp prop) => prop.Type switch
    {
        "byte" or "int" or "float" or "bool" or "object" or "class" or "name" or "string" or "delegate" => true,
        "struct" => prop.Struct is { } s && (IsNative(s) || (_schema?.StructMembers(s).Count ?? 0) > 0),
        "array" => prop.Inner is { } inner && inner.Type != "array" && CanWrite(inner),
        _ => false,
    };

    private static bool IsNative(string structName) => ShortName(structName) is var s
        && (s.Equals("Vector", StringComparison.OrdinalIgnoreCase) || s.Equals("Rotator", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Color", StringComparison.OrdinalIgnoreCase));

    private static string ShortName(string? structName) => structName is null ? "" : structName[(structName.LastIndexOf('.') + 1)..];

    private static bool IsRef(SvJsonNode v) => v is SvJsonObject o && (o.ContainsKey("$actor") || o.ContainsKey("$asset") || o.ContainsKey("$class"));

    private long EnumOrInt(SvJsonNode value, string? enumName)
    {
        if (value is SvJsonString s && enumName is not null && _schema?.EnumMembers(enumName) is { } members)
        {
            int i = members.ToList().FindIndex(m => m.Equals(s.Value, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                return i;
            }
        }

        return Int(value);
    }

    private static long Int(SvJsonNode? v) => v switch
    {
        SvJsonNumber n when n.TryGetInt64(out long i) => i,
        SvJsonNumber n => (long)Math.Round(PropValues.Num(n)),
        SvJsonString s when long.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i) => i,
        { Kind: SvJsonKind.True } => 1,
        _ => 0,
    };

    /// <summary>A float as stored; null (a NaN or infinity the reader could not keep) becomes NaN.</summary>
    private static float Float(SvJsonNode? v) => v switch
    {
        SvJsonNumber { IsFloat: true } n => n.FloatValue,
        SvJsonNumber n => (float)PropValues.Num(n),
        SvJsonString s when float.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) => f,
        { Kind: SvJsonKind.Null } => float.NaN,
        _ => 0,
    };

    private static bool Bool(SvJsonNode? v) => v switch
    {
        { Kind: SvJsonKind.True } => true,
        SvJsonString s => s.Value.Equals("True", StringComparison.OrdinalIgnoreCase) || s.Value == "1",
        SvJsonNumber n => PropValues.Num(n) != 0,
        _ => false,
    };

    private static string? Text(SvJsonNode? v) => v switch
    {
        SvJsonString s => s.Value,
        SvJsonNumber n => n.ToString(),
        _ => null,
    };
}
