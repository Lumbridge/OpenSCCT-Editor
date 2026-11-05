using System.Buffers.Binary;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Interop.Svmap;

/// <summary>Reads only the JSON chunk of a binary glTF file (the SVMap instance table lives in its extras).</summary>
public static class GlbJson
{
    private const uint Magic = 0x46546C67;    // "glTF"
    private const uint JsonChunk = 0x4E4F534A; // "JSON"

    public static SvJsonNode? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using FileStream fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[20];
        if (fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != JsonChunk)
        {
            throw new InvalidDataException($"{path}: not a binary glTF file");
        }

        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));
        byte[] json = new byte[length];
        fs.ReadExactly(json);
        // The chunk is padded to 4 bytes with spaces, which JSON allows.
        return SvJson.Parse(json, path);
    }
}
