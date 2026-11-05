using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// The .sdc container (SDC-FORMAT.md section 1): a raw package, or a run of <c>{u32 unpacked, u32 packed, zlib}</c>
/// chunks joined in order. A normal save writes one chunk; autosaves write 15 MiB chunks.
/// </summary>
public static class SdcContainer
{
    public const uint PackageTag = 0x9E2A83C1;

    /// <summary>The most a file may unpack to (RE+ uses the same limit).</summary>
    public const long MaxUnpacked = 1L << 30;

    /// <summary>The largest inflate ratio zlib can reach; anything above it is a corrupt or hostile size field.</summary>
    public const long MaxRatio = 1032;

    /// <summary>The package bytes of a file, and how many zlib chunks it held (0 for a raw package).</summary>
    public static (byte[] Package, int Chunks) Unwrap(byte[] file)
    {
        if (file.Length < 8)
        {
            throw new InvalidDataException("not an Unreal package: the file is too small");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(file) == PackageTag)
        {
            return (file, 0);
        }

        var parts = new List<(int Offset, int Packed, int Unpacked)>();
        long total = 0;
        for (int p = 0; p < file.Length;)
        {
            if (file.Length - p < 8)
            {
                throw new InvalidDataException($"zlib container: {file.Length - p} stray bytes at the end (offset {p})");
            }

            uint unpacked = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p));
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 4));
            if (packed == 0 || packed > file.Length - p - 8)
            {
                throw new InvalidDataException($"zlib container: chunk {parts.Count} at offset {p} claims {packed} bytes, past the end of the file");
            }

            if (unpacked == 0 || unpacked > packed * MaxRatio)
            {
                throw new InvalidDataException($"zlib container: chunk {parts.Count} at offset {p} claims to unpack {packed} bytes to {unpacked}");
            }

            total += unpacked;
            if (total > MaxUnpacked)
            {
                throw new InvalidDataException($"zlib container: unpacks to more than {MaxUnpacked} bytes");
            }

            parts.Add((p + 8, (int)packed, (int)unpacked));
            p += 8 + (int)packed;
        }

        byte[] package = new byte[total];
        int at = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            (int offset, int packedSize, int unpackedSize) = parts[i];
            using var input = new MemoryStream(file, offset, packedSize, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            bool exact;
            try
            {
                exact = zlib.ReadAtLeast(package.AsSpan(at, unpackedSize), unpackedSize, throwOnEndOfStream: false) == unpackedSize
                    && zlib.ReadByte() < 0;
            }
            catch (IOException ex)
            {
                // A damaged stream can fail inside zlib (ZLibException) rather than as bad data.
                throw new InvalidDataException($"zlib container: chunk {i} does not inflate ({ex.Message})", ex);
            }

            if (!exact)
            {
                throw new InvalidDataException($"zlib container: chunk {i} does not inflate to the {unpackedSize} bytes it claims");
            }

            at += unpackedSize;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(package) != PackageTag)
        {
            throw new InvalidDataException("not an Unreal package: no package tag after unpacking");
        }

        return (package, parts.Count);
    }
}
