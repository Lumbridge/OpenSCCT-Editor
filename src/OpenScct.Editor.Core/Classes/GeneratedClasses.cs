using System.Reflection;
using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Content.Loading;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Core.Uc;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.SBase;
using OpenScct.Game.Svm;

namespace OpenScct.Editor.Core.Classes;

/// <summary>The catalog's fallback: OpenSCCT's generated class metadata (Core, Engine, SBase) and its own Svm classes,
/// with defaults taken from a new object of each concrete class. It has no English default text and no GUI, IpDrv or
/// SoftBody classes.</summary>
internal static class GeneratedClasses
{
    /// <summary>Adds the generated classes the data does not have yet; returns how many were added.</summary>
    public static int AddMissing(CatalogData data)
    {
        var index = new UcClassIndex(CorePackage.Info, EnginePackage.Info, SBasePackage.Info, SvmPackage.Info);
        int added = 0;
        foreach (UcClassInfo c in index.Classes)
        {
            if (data.ByPath.ContainsKey(c.Path))
            {
                continue;
            }

            data.Add(new ClassInfo(c.Path, c.Super?.Path, c.IsPlaceable, c.IsAbstract, c.Properties.Select(p => Prop(c, p, data)).ToList(),
                DefaultsOf(c)));
            added++;
        }

        return added;
    }

    private static SvJsonObject DefaultsOf(UcClassInfo c)
    {
        try
        {
            return ClassTableBuilder.Defaults(c) is { } bag ? ToJson(bag) : new SvJsonObject();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or TargetInvocationException
            or NullReferenceException)
        {
            // A class whose constructor needs a running world has no defaults here.
            return new SvJsonObject();
        }
    }

    private static PropInfo Prop(UcClassInfo c, UcPropertyInfo p, CatalogData data)
    {
        bool editable = (p.Flags & UcPropertyFlags.CPF_Edit) != 0 && (p.Flags & UcPropertyFlags.CPF_EditConst) == 0;
        Type? member = MemberType(c, p);
        string type = p.Kind switch
        {
            UcPropertyKind.Bool => "bool",
            UcPropertyKind.Byte or UcPropertyKind.Enum => "byte",
            UcPropertyKind.Int => "int",
            UcPropertyKind.Float => "float",
            UcPropertyKind.Name => "name",
            UcPropertyKind.String => "string",
            UcPropertyKind.Struct => "struct",
            UcPropertyKind.Object or UcPropertyKind.Asset => "object",
            UcPropertyKind.Class => "class",
            UcPropertyKind.Delegate => "delegate",
            UcPropertyKind.Array => "array",
            _ => "unknown",
        };

        // Static arrays are C# arrays and dynamic arrays Lists; the element type says what the inner value is.
        Type? element = member is null ? null : member.IsArray ? member.GetElementType() : member.IsGenericType ? member.GetGenericArguments()[0] : member;
        string? inner = p.Kind == UcPropertyKind.Array ? (element is null ? "unknown" : Classify(element)) : null;
        string declared = p.Kind == UcPropertyKind.Array ? Between(p.Type, "array<", ">") ?? p.Type : p.Type;

        string? enumName = null;
        if (element is { IsEnum: true })
        {
            enumName = element.Name;
            data.Enums.TryAdd(element.Name, Enum.GetNames(element));
        }

        string kind = inner ?? type;
        string? objectClass = kind switch
        {
            "object" => declared,
            "class" => Between(declared, "class<", ">") ?? "Core.Object",
            _ => null,
        };

        return new PropInfo(p.Name, type, p.Category, editable, enumName, kind == "struct" ? declared : null, objectClass,
            Math.Max(1, p.ArrayDim), inner, c.Path);
    }

    private static Type? MemberType(UcClassInfo c, UcPropertyInfo p)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return c.Type.GetField(p.CSharpName, flags)?.FieldType ?? c.Type.GetProperty(p.CSharpName, flags)?.PropertyType;
    }

    /// <summary>A C# element type in the catalog's type words.</summary>
    private static string Classify(Type t) => t switch
    {
        _ when t == typeof(bool) => "bool",
        _ when t == typeof(byte) || t.IsEnum => "byte",
        _ when t == typeof(int) => "int",
        _ when t == typeof(float) => "float",
        _ when t == typeof(string) => "string",
        _ when t == typeof(Name) => "name",
        _ when t == typeof(ClassRef) => "class",
        _ when t == typeof(DelegateRef) => "delegate",
        _ when t == typeof(OpenScct.Core.Uc.AssetRef) || typeof(UObject).IsAssignableFrom(t) => "object",
        _ when t.IsValueType => "struct",
        _ => "unknown",
    };

    private static string? Between(string s, string open, string close) =>
        s.StartsWith(open, StringComparison.Ordinal) && s.EndsWith(close, StringComparison.Ordinal) ? s[open.Length..^close.Length] : null;

    private static SvJsonObject ToJson(PropBag bag)
    {
        var obj = new SvJsonObject();
        foreach (KeyValuePair<string, PropValue> item in bag)
        {
            obj.Add(item.Key, ToJson(item.Value));
        }

        return obj;
    }

    /// <summary>A prop value in SVMap JSON form (as OpenSCCT's PropJson writes it).</summary>
    private static SvJsonNode ToJson(PropValue v)
    {
        static SvJsonObject Single(string key, SvJsonNode value)
        {
            var o = new SvJsonObject();
            o.Add(key, value);
            return o;
        }

        switch (v.Kind)
        {
            case PropKind.Null:
                return SvJsonNode.Null;
            case PropKind.Bool:
                return SvJsonNode.Bool(v.AsBool());
            case PropKind.Int:
                return SvJsonNode.Integer(v.AsInt());
            case PropKind.Float:
                return SvJsonNode.Float(v.AsFloat());
            case PropKind.String:
                return SvJsonNode.String(v.AsString());
            case PropKind.Actor:
                return Single("$actor", SvJsonNode.String(v.AsActor().Id));
            case PropKind.Asset:
                return Single("$asset", SvJsonNode.String(v.AsAsset().Path));
            case PropKind.Class:
                return Single("$class", SvJsonNode.String(v.AsClass()));
            case PropKind.Struct:
                return ToJson(v.AsStruct());
            case PropKind.Array:
                return new SvJsonArray(v.AsArray().Select(ToJson));
            default:
                var elements = new SvJsonObject();
                foreach (KeyValuePair<int, PropValue> e in v.AsStaticArray())
                {
                    elements.Add(e.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), ToJson(e.Value));
                }

                return Single("$elements", elements);
        }
    }
}
