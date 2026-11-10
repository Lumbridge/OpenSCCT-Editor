using System.Globalization;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>Counts of what the property decoder met, for import findings.</summary>
public sealed class PropertyStats
{
    public int Tags { get; set; }

    /// <summary>Properties the class data does not declare (map-local classes, removed properties).</summary>
    public int Unknown { get; set; }

    /// <summary>Values kept as <c>{"$raw"}</c> because nothing says how to read them.</summary>
    public int Raw { get; set; }

    /// <summary>Tags whose stored size differs from the bytes the declared type took (struct sizes are often wrong).</summary>
    public int SizeMismatch { get; set; }

    /// <summary>Tags whose type id differs from the declared type (the declared type wins).</summary>
    public int TypeMismatch { get; set; }

    /// <summary>Floats that were NaN or infinite (written as null, as the converter does).</summary>
    public int NonFinite { get; set; }

    public List<string> Notes { get; } = new();

    public void Note(string text)
    {
        if (Notes.Count < 50)
        {
            Notes.Add(text);
        }
    }
}

/// <summary>
/// Reads tagged property lists (SDC-FORMAT.md section 4.1) into SVMap property JSON, typed by the declared types of
/// <see cref="UeSchema"/>, exactly as OpenSCCT's converter does (svmbuild <c>sources/typedprops.py</c> over
/// scctformats <c>classdefaults.Decoder</c>): ints, bytes and enums as integers, floats, bools, strings and names as
/// strings, structs as objects (Vector, Rotator and Color native, every other struct a nested list read to its None),
/// dynamic arrays as arrays, static arrays as <c>{"$elements"}</c>, and references through the given resolver.
/// <para>A property the class data does not declare is typed by its tag when the tag says enough (bool, int, float,
/// name, string, object, vector, rotator, or a nested struct list); anything else is kept as
/// <c>{"$raw":{"type","hex"}}</c> so nothing stored is lost.</para>
/// </summary>
public sealed class UPropertyReader
{
    private static readonly string[] s_tagTypes =
    [
        "", "ByteProperty", "IntProperty", "BoolProperty", "FloatProperty", "ObjectProperty", "NameProperty", "StringProperty",
        "ClassProperty", "ArrayProperty", "StructProperty", "VectorProperty", "RotatorProperty", "StrProperty", "MapProperty",
        "FixedArrayProperty",
    ];

    private const int TagByte = 1, TagInt = 2, TagBool = 3, TagFloat = 4, TagObject = 5, TagName = 6, TagString = 7,
        TagClass = 8, TagArray = 9, TagStruct = 10, TagVector = 11, TagRotator = 12, TagStr = 13;

    private readonly UeSchema? _schema;
    private readonly Func<int, bool, SvJsonNode?> _resolve;
    private readonly Dictionary<string, bool> _knownStructs = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="resolve">An object reference (objref, declared as a class property) to its JSON, or null for none.</param>
    public UPropertyReader(UeSchema? schema, Func<int, bool, SvJsonNode?> resolve, PropertyStats? stats = null)
    {
        _schema = schema;
        _resolve = resolve;
        Stats = stats ?? new PropertyStats();
    }

    public PropertyStats Stats { get; }

    /// <summary>When set, records where the references, strings and tag sizes of what is read sit, so the bytes can be
    /// written again with their references moved (<see cref="UPropertyTrace"/>).</summary>
    public UPropertyTrace? Trace { get; set; }

    /// <summary>Native object arrays of tagged structs absent from the script schema (UStaticMesh.Materials).</summary>
    public IReadOnlySet<string>? TaggedStructArrays { get; init; }

    /// <summary>Skips an FStateFrame when the export has one (RF_HasStack).</summary>
    public static void SkipStateFrame(UReader r, uint objectFlags)
    {
        if ((objectFlags & UExport.HasStack) == 0)
        {
            return;
        }

        int node = r.ObjRef();
        r.ObjRef();
        r.U64();
        r.I32();
        if (node != 0)
        {
            r.Compact();
        }
    }

    /// <summary>Skips a tagged list without typing it (sizes only; struct sizes are trusted here, so use it only for
    /// objects whose lists hold no structs, such as models and polys, which store just "None").</summary>
    public static void SkipTagged(UReader r)
    {
        while (r.Name() != "None")
        {
            byte info = r.U8();
            if ((info & 0x0F) == TagStruct)
            {
                r.NameIndex();
            }

            int size = Size(r, (info >> 4) & 7);
            if ((info & 0x80) != 0 && (info & 0x0F) != TagBool)
            {
                ArrayIndex(r);
            }

            r.Skip(size);
        }
    }

    /// <summary>The tagged properties of an object of class <paramref name="className"/> (full or short name; null
    /// or unknown for a class the schema lacks).</summary>
    public SvJsonObject ReadObject(UReader r, string? className, string where) =>
        Tagged(r, name => className is null ? null : _schema?.FindProperty(className, name), where, 0);

    private SvJsonObject Tagged(UReader r, Func<string, UeProp?> find, string where, int depth)
    {
        if (depth > 16)
        {
            throw new InvalidDataException($"{where}: struct nesting too deep at {r.Pos}");
        }

        var order = new List<string>();
        var plain = new Dictionary<string, SvJsonNode>(StringComparer.Ordinal);
        var indexed = new Dictionary<string, SortedDictionary<int, SvJsonNode>>(StringComparer.Ordinal);
        while (true)
        {
            int tagAt = r.Pos;
            string name = r.Name();
            if (name == "None")
            {
                break;
            }

            int infoAt = r.Pos;
            byte info = r.U8();
            int type = info & 0x0F;
            bool flag = (info & 0x80) != 0;
            string? structName = type == TagStruct ? r.Name() : null;
            int sizeAt = r.Pos;
            int size = Size(r, (info >> 4) & 7);
            int sizeEnd = r.Pos;
            int index = flag && type != TagBool ? ArrayIndex(r) : 0;
            Stats.Tags++;
            UeProp? prop = find(name);
            int start = r.Pos, extra0 = r.NameExtra;
            SvJsonNode value;
            if (type == TagArray && depth == 0 && TaggedStructArrays?.Contains(name) == true)
            {
                int count = r.Count(2, "tagged struct elements");
                var elements = new SvJsonArray();
                for (int i = 0; i < count; i++) elements.Add(Tagged(r, _ => null, $"{where}.{name}[{i}]", depth + 1));
                value = elements;
            }
            else if (type == TagBool)
            {
                value = SvJsonNode.Bool(flag);
                if (prop is not null && prop.Type != "bool")
                {
                    Stats.TypeMismatch++;
                }
            }
            else if (prop is null || !CanRead(prop))
            {
                Stats.Unknown++;
                Stats.Note($"{where}: {name} is {(prop is null ? "not declared" : $"a {prop.Type} the decoder cannot type")} ({TagType(type)}, {size} bytes)");
                value = ByTag(r, type, structName, size, $"{where}.{name}", depth);
                prop = null;
            }
            else
            {
                if (!TagFits(type, prop.Type))
                {
                    Stats.TypeMismatch++;
                    Stats.Note($"{where}.{name}: tag {TagType(type)} for a {prop.Type} property");
                }

                value = prop.IsDynamicArray ? Array(r, prop, $"{where}.{name}", depth) : Item(r, prop, $"{where}.{name}", depth);
                int used = r.Pos - start - (r.NameExtra - extra0);
                if (used != size)
                {
                    Stats.SizeMismatch++;
                }
            }

            if (r.Pos > r.End)
            {
                throw new InvalidDataException($"{where}: property {name} at {tagAt} runs past the end");
            }

            Trace?.Tags.Add(new UTagSpan(infoAt, sizeAt, sizeEnd, start, r.Pos));

            string key = prop?.Name ?? name;
            if (!plain.ContainsKey(key) && !indexed.ContainsKey(key))
            {
                order.Add(key);
            }

            if ((prop?.ArrayDim ?? 1) > 1 || index != 0)
            {
                if (!indexed.TryGetValue(key, out SortedDictionary<int, SvJsonNode>? slot))
                {
                    slot = new SortedDictionary<int, SvJsonNode>();
                    indexed[key] = slot;
                    // An undeclared property stored element 0 first, then another element: element 0 joins the list.
                    if (plain.Remove(key, out SvJsonNode? first))
                    {
                        slot[0] = first;
                    }
                }

                slot[index] = value;
            }
            else
            {
                plain[key] = value;
            }
        }

        var o = new SvJsonObject();
        foreach (string key in order)
        {
            if (indexed.TryGetValue(key, out SortedDictionary<int, SvJsonNode>? elements))
            {
                var el = new SvJsonObject();
                foreach (KeyValuePair<int, SvJsonNode> kv in elements)
                {
                    el.Add(kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value);
                }

                var wrapper = new SvJsonObject();
                wrapper.Add(Interop.T3d.T3dTypes.ElementsKey, el);
                o.Add(key, wrapper);
            }
            else
            {
                o.Add(key, plain[key]);
            }
        }

        return o;
    }

    /// <summary>One value of a declared property (UE2 SerializeItem).</summary>
    private SvJsonNode Item(UReader r, UeProp prop, string where, int depth)
    {
        switch (prop.Type)
        {
            case "byte":
                return SvJsonNode.Integer(r.U8());
            case "int":
                return SvJsonNode.Integer(r.I32());
            case "float":
                return Float(r.F32());
            case "bool":
                return SvJsonNode.Bool(r.U8() != 0);
            case "object":
                return Ref(ObjRef(r), isClass: false);
            case "class":
                return Ref(ObjRef(r), isClass: true);
            case "name":
                return SvJsonNode.String(r.Name());
            case "string":
                return SvJsonNode.String(FString(r));
            case "delegate":
                var d = new SvJsonObject();
                d.Add("object", Ref(ObjRef(r), isClass: false));
                d.Add("function", SvJsonNode.String(r.Name()));
                return d;
            case "struct":
                return Struct(r, prop.Struct, where, depth);
            case "array":
                return Array(r, prop, where, depth);
            default:
                throw new InvalidDataException($"{where}: no reader for a {prop.Type} property");
        }
    }

    private SvJsonNode Array(UReader r, UeProp prop, string where, int depth)
    {
        UeProp inner = prop.Inner ?? throw new InvalidDataException($"{where}: the class data has no element type for this array");
        int n = r.Count(1, $"{where} array");
        var a = new SvJsonArray();
        for (int i = 0; i < n; i++)
        {
            a.Add(Item(r, inner, where, depth + 1));
        }

        return a;
    }

    /// <summary>A struct value: Vector, Rotator and Color are native; every other struct is a nested tagged list.</summary>
    private SvJsonNode Struct(UReader r, string? structName, string where, int depth)
    {
        string shortName = structName is null ? "" : structName[(structName.LastIndexOf('.') + 1)..];
        if (NativeStruct(r, shortName) is { } native)
        {
            return native;
        }

        if (structName is null || _schema is null || !IsKnownStruct(structName))
        {
            throw new InvalidDataException($"{where}: struct {structName ?? "?"} is not in the class data");
        }

        return Tagged(r, m => _schema.FindMember(structName, m), where, depth + 1);
    }

    private SvJsonNode? NativeStruct(UReader r, string shortName)
    {
        if (shortName.Equals("Vector", StringComparison.OrdinalIgnoreCase))
        {
            return Vector(r.F32(), r.F32(), r.F32());
        }

        if (shortName.Equals("Rotator", StringComparison.OrdinalIgnoreCase))
        {
            var o = new SvJsonObject();
            o.Add("Pitch", SvJsonNode.Integer(r.I32()));
            o.Add("Yaw", SvJsonNode.Integer(r.I32()));
            o.Add("Roll", SvJsonNode.Integer(r.I32()));
            return o;
        }

        if (shortName.Equals("Color", StringComparison.OrdinalIgnoreCase))
        {
            var o = new SvJsonObject();
            o.Add("B", SvJsonNode.Integer(r.U8()));
            o.Add("G", SvJsonNode.Integer(r.U8()));
            o.Add("R", SvJsonNode.Integer(r.U8()));
            o.Add("A", SvJsonNode.Integer(r.U8()));
            return o;
        }

        return null;
    }

    /// <summary>
    /// A value of a property the class data does not declare, typed by its tag where the tag is unambiguous and the
    /// bytes read equal the tag's size; otherwise its raw bytes.
    /// </summary>
    private SvJsonNode ByTag(UReader r, int type, string? structName, int size, string where, int depth)
    {
        int start = r.Pos, extra0 = r.NameExtra;
        if (size < 0 || size > r.Remaining)
        {
            throw new InvalidDataException($"{where}: tag size {size} at {start} runs past the end");
        }

        // A struct's stored size is unreliable: read its nested list to None (natives have fixed sizes).
        if (type == TagStruct && structName is not null)
        {
            if (NativeStruct(r, structName) is { } native)
            {
                return native;
            }

            return Tagged(r, _ => null, where, depth + 1);
        }

        SvJsonNode? value = null;
        int refs = Trace?.Refs.Count ?? 0, strings = Trace?.Strings.Count ?? 0;
        try
        {
            value = type switch
            {
                TagInt when size == 4 => SvJsonNode.Integer(r.I32()),
                TagFloat when size == 4 => Float(r.F32()),
                TagByte when size == 1 => SvJsonNode.Integer(r.U8()),
                TagName => SvJsonNode.String(r.Name()),
                TagObject or TagClass => Ref(ObjRef(r), type == TagClass),
                TagStr => SvJsonNode.String(FString(r)),
                TagVector when size == 12 => Vector(r.F32(), r.F32(), r.F32()),
                TagRotator when size == 12 => NativeStruct(r, "Rotator"),
                _ => null,
            };
        }
        catch (InvalidDataException)
        {
            value = null;
        }

        if (value is not null && r.Pos - start - (r.NameExtra - extra0) == size)
        {
            return value;
        }

        r.Pos = start;
        Stats.Raw++;
        if (Trace is not null)
        {
            // Raw bytes may hold references nothing can find again.
            Trace.Refs.RemoveRange(refs, Trace.Refs.Count - refs);
            Trace.Strings.RemoveRange(strings, Trace.Strings.Count - strings);
            Trace.Opaque = true;
        }

        var raw = new SvJsonObject();
        raw.Add("type", SvJsonNode.String(TagType(type)));
        if (structName is not null)
        {
            raw.Add("struct", SvJsonNode.String(structName));
        }

        raw.Add("hex", SvJsonNode.String(Convert.ToHexStringLower(r.Span(size))));
        var o = new SvJsonObject();
        o.Add(Model.PropValues.RawKey, raw);
        return o;
    }

    private int ObjRef(UReader r)
    {
        int at = r.Pos;
        int objref = r.ObjRef();
        Trace?.Refs.Add((at, r.Pos));
        return objref;
    }

    private string FString(UReader r)
    {
        int at = r.Pos;
        string s = r.FString();
        Trace?.Strings.Add((at, r.Pos));
        return s;
    }

    private SvJsonNode Ref(int objref, bool isClass) => objref == 0 ? SvJsonNode.Null : _resolve(objref, isClass) ?? SvJsonNode.Null;

    private SvJsonNode Float(float f)
    {
        if (float.IsFinite(f))
        {
            return SvJsonNode.Float(f);
        }

        Stats.NonFinite++;
        return SvJsonNode.Null;
    }

    private SvJsonObject Vector(float x, float y, float z)
    {
        var o = new SvJsonObject();
        o.Add("X", Float(x));
        o.Add("Y", Float(y));
        o.Add("Z", Float(z));
        return o;
    }

    /// <summary>True when the declared type says how to read the value: a known kind, an array with its element type,
    /// a struct the class data holds (or a native one).</summary>
    private bool CanRead(UeProp prop) => prop.Type switch
    {
        "byte" or "int" or "float" or "bool" or "object" or "class" or "name" or "string" or "delegate" => true,
        "struct" => IsKnownStruct(prop.Struct),
        "array" => prop.Inner is { } inner && inner.Type != "array" && CanRead(inner),
        _ => false,
    };

    private bool IsKnownStruct(string? structName)
    {
        if (structName is null)
        {
            return false;
        }

        if (!_knownStructs.TryGetValue(structName, out bool known))
        {
            string shortName = structName[(structName.LastIndexOf('.') + 1)..];
            known = shortName.Equals("Vector", StringComparison.OrdinalIgnoreCase) || shortName.Equals("Rotator", StringComparison.OrdinalIgnoreCase)
                || shortName.Equals("Color", StringComparison.OrdinalIgnoreCase) || (_schema?.StructMembers(structName).Count ?? 0) > 0;
            _knownStructs[structName] = known;
        }

        return known;
    }

    /// <summary>True when a tag type is the one UE2 writes for a declared type (class properties are tagged as objects,
    /// delegates with the old string id).</summary>
    private static bool TagFits(int tag, string declared) => declared switch
    {
        "byte" => tag == TagByte,
        "int" => tag == TagInt,
        "bool" => tag == TagBool,
        "float" => tag == TagFloat,
        "object" or "class" => tag is TagObject or TagClass,
        "name" => tag == TagName,
        "string" => tag is TagStr or TagString,
        "delegate" => tag == TagString,
        "array" => tag == TagArray,
        "struct" => tag is TagStruct or TagVector or TagRotator,
        _ => false,
    };

    private static string TagType(int type) => type < s_tagTypes.Length && s_tagTypes[type].Length > 0 ? s_tagTypes[type] : type.ToString(CultureInfo.InvariantCulture);

    private static int Size(UReader r, int code) => code switch
    {
        0 => 1,
        1 => 2,
        2 => 4,
        3 => 12,
        4 => 16,
        5 => r.U8(),
        6 => r.U16(),
        _ => r.I32(),
    };

    private static int ArrayIndex(UReader r)
    {
        byte b = r.U8();
        if (b < 0x80)
        {
            return b;
        }

        if ((b & 0xC0) == 0x80)
        {
            return ((b & 0x7F) << 8) | r.U8();
        }

        return ((b & 0x3F) << 24) | (r.U8() << 16) | (r.U8() << 8) | r.U8();
    }
}

/// <summary>One tag of a traced list (absolute offsets): its info byte, its size field and its value.</summary>
public readonly record struct UTagSpan(int Info, int SizeAt, int SizeEnd, int ValueStart, int ValueEnd);

/// <summary>Where a read tagged list keeps its object references, strings and tag sizes (absolute offsets).
/// <see cref="Opaque"/> is set when a value was kept as raw bytes, which may hold references.</summary>
public sealed class UPropertyTrace
{
    public List<(int At, int End)> Refs { get; } = new();

    public List<(int At, int End)> Strings { get; } = new();

    public List<UTagSpan> Tags { get; } = new();

    public bool Opaque { get; set; }
}
