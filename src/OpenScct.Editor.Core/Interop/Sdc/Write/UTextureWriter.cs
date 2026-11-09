using System.Numerics;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>Native UTexture data, including every mip and its lazy-array end offset.</summary>
internal static class UTextureWriter
{
    public static Action<UWriter> Prepare(string file, bool masked, bool alpha, UeSchema? schema)
    {
        RgbaImage image = RgbaImage.Decode(File.ReadAllBytes(file));
        AlphaKind kind = masked ? AlphaKind.Mask : alpha ? AlphaKind.Blend : DdsFile.AlphaOf(image);
        DdsTexture dds = DdsFile.Encode(image, kind);
        var props = new PropSet();
        props.Set("Format", SvJsonNode.Integer(dds.Format == "DXT5" ? 8 : 3));
        props.Set("USize", SvJsonNode.Integer(dds.Width));
        props.Set("VSize", SvJsonNode.Integer(dds.Height));
        props.Set("UClamp", SvJsonNode.Integer(dds.Width));
        props.Set("VClamp", SvJsonNode.Integer(dds.Height));
        props.Set("UBits", SvJsonNode.Integer(BitOperations.Log2((uint)dds.Width)));
        props.Set("VBits", SvJsonNode.Integer(BitOperations.Log2((uint)dds.Height)));
        if (kind == AlphaKind.Mask) props.Set("bMasked", SvJsonNode.Bool(true));
        if (kind != AlphaKind.None) props.Set("bAlphaTexture", SvJsonNode.Bool(true));
        var writer = new UPropertyWriter(schema, _ => 0);
        // Byte properties still need their native tag when class data is unavailable.
        if (schema is null) writer = new UPropertyWriter(null, _ => 0,
            new Dictionary<(string, string), UTagHint> { [("Engine.Texture", "Format")] = new(1, null),
                [("Engine.Texture", "UBits")] = new(1, null), [("Engine.Texture", "VBits")] = new(1, null) });
        return w =>
        {
            writer.WriteObject(w, "Engine.Texture", "Engine.Texture", props, "exported texture");
            w.Compact(dds.Mips);
            int width = dds.Width, height = dds.Height, offset = 128;
            for (int mip = 0; mip < dds.Mips; mip++)
            {
                int size = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * (dds.Format == "DXT5" ? 16 : 8);
                int end = w.Pos; w.I32(0);
                w.Compact(size); w.Bytes(dds.Bytes.AsSpan(offset, size));
                w.PatchI32(end, w.Pos);
                w.I32(width); w.I32(height);
                w.U8((byte)BitOperations.Log2((uint)width)); w.U8((byte)BitOperations.Log2((uint)height));
                offset += size; width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
        };
    }
}
