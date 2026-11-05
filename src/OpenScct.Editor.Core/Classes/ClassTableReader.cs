using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Classes;

/// <summary>UE2 class and property flag bits the catalog reads.</summary>
internal static class UeFlags
{
    public const uint ClassAbstract = 0x00000001;
    public const uint ClassPlaceable = 0x00000200;
    public const uint PropEdit = 0x00000001;
    public const uint PropEditConst = 0x00020000;
}

/// <summary>Reads OpenSCCT's shared class table (<c>shared/classes.json</c>, format <c>svm.classes/1</c>).</summary>
internal static class ClassTableReader
{
    /// <summary>Adds every class, enum and struct of the file; returns the number of classes.</summary>
    public static int Read(string path, CatalogData data)
    {
        if (SvJson.Parse(File.ReadAllBytes(path), path) is not SvJsonObject root || root["classes"] is not SvJsonObject classes)
        {
            throw new InvalidDataException($"{path}: not a class table (no \"classes\" object)");
        }

        if (root["enums"] is SvJsonObject enums)
        {
            foreach (KeyValuePair<string, SvJsonNode> e in enums)
            {
                if (e.Value is SvJsonArray values)
                {
                    data.Enums[e.Key] = values.OfType<SvJsonString>().Select(s => s.Value).ToList();
                }
            }
        }

        if (root["structs"] is SvJsonObject structs)
        {
            foreach (KeyValuePair<string, SvJsonNode> s in structs)
            {
                if (s.Value is SvJsonObject st && st["properties"] is SvJsonArray props)
                {
                    data.Structs[s.Key] = props.OfType<SvJsonObject>().Select(p => Prop(p, s.Key)).ToList();
                }
            }
        }

        int count = 0;
        foreach (KeyValuePair<string, SvJsonNode> kv in classes)
        {
            if (kv.Value is not SvJsonObject c)
            {
                continue;
            }

            uint flags = Hex(c["flags"]);
            List<PropInfo> props = c["properties"] is SvJsonArray arr ? arr.OfType<SvJsonObject>().Select(p => Prop(p, kv.Key)).ToList() : [];
            data.Add(new ClassInfo(kv.Key, Str(c["super"]), (flags & UeFlags.ClassPlaceable) != 0, (flags & UeFlags.ClassAbstract) != 0,
                props, c["defaults"] as SvJsonObject ?? new SvJsonObject()));
            count++;
        }

        return count;
    }

    private static PropInfo Prop(SvJsonObject p, string declaring)
    {
        uint flags = Hex(p["flags"]);
        string type = Str(p["type"]) ?? "unknown";
        SvJsonObject? inner = p["inner"] as SvJsonObject;
        SvJsonObject detail = type == "array" && inner is not null ? inner : p;
        string? objectClass = Str(detail["class"]) ?? Str(detail["metaClass"]);
        if (Str(detail["type"]) == "class" && Str(detail["metaClass"]) is { } meta)
        {
            objectClass = meta;
        }

        int dim = p["arrayDim"] is SvJsonNumber n && n.TryGetInt32(out int d) ? d : 1;
        return new PropInfo(
            Name: Str(p["name"]) ?? "",
            Type: type,
            Category: Str(p["category"]),
            Editable: (flags & UeFlags.PropEdit) != 0 && (flags & UeFlags.PropEditConst) == 0,
            Enum: Str(detail["enum"]),
            Struct: Str(detail["struct"]),
            ObjectClass: objectClass,
            ArrayDim: dim,
            Inner: inner is null ? null : Str(inner["type"]),
            DeclaringClass: declaring);
    }

    private static string? Str(SvJsonNode? node) => node is SvJsonString s ? s.Value : null;

    private static uint Hex(SvJsonNode? node)
    {
        string? s = Str(node);
        if (s is null)
        {
            return node is SvJsonNumber n && n.TryGetInt64(out long l) ? (uint)l : 0;
        }

        return uint.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out uint v) ? v : 0;
    }
}
