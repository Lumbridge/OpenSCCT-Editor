using System.Buffers.Binary;
using System.Numerics;

namespace OpenScct.Editor.Core.Identity.Codecs;

/// <summary>
/// Windows bitmaps: every header version, 1, 4 and 8-bit palettes (plain or RLE), 16, 24 and 32-bit pixels with or
/// without bit masks. A 32-bit picture whose alpha is zero everywhere is opaque, as Windows shows it.
/// </summary>
internal static class BmpDecoder
{
    private const int Rgb = 0, Rle8 = 1, Rle4 = 2, BitFields = 3, AlphaBitFields = 6;

    public static bool Is(ReadOnlySpan<byte> data) => data.Length >= 26 && data[0] == 'B' && data[1] == 'M';

    public static (int Width, int Height)? SizeOf(ReadOnlySpan<byte> data)
    {
        if (!Is(data))
        {
            return null;
        }

        int header = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        return header == 12
            ? (BinaryPrimitives.ReadUInt16LittleEndian(data[18..]), BinaryPrimitives.ReadUInt16LittleEndian(data[20..]))
            : (BinaryPrimitives.ReadInt32LittleEndian(data[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..])));
    }

    public static RgbaImage Decode(ReadOnlySpan<byte> data)
    {
        if (!Is(data) || data.Length < 30)
        {
            throw new InvalidDataException("not a BMP file");
        }

        int pixelsAt = BinaryPrimitives.ReadInt32LittleEndian(data[10..]);
        int header = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        bool core = header == 12;
        if ((!core && header < 40) || 14 + header > data.Length)
        {
            throw new InvalidDataException("the BMP header is broken");
        }

        int width = core ? BinaryPrimitives.ReadUInt16LittleEndian(data[18..]) : BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int rawHeight = core ? BinaryPrimitives.ReadUInt16LittleEndian(data[20..]) : BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(data[(core ? 24 : 28)..]);
        int compression = core ? Rgb : BinaryPrimitives.ReadInt32LittleEndian(data[30..]);
        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        if (width <= 0 || height <= 0 || (long)width * height > RgbaImage.MaxPixels)
        {
            throw new InvalidDataException($"a {width}x{height} BMP is not supported");
        }

        if (pixelsAt <= 0 || pixelsAt > data.Length)
        {
            throw new InvalidDataException("the BMP pixel data is missing");
        }

        uint[] masks = Masks(data, header, bits, compression);
        uint[] palette = bits <= 8 ? Palette(data, header, bits, core) : [];
        var image = new RgbaImage(width, height);
        ReadOnlySpan<byte> pixels = data[pixelsAt..];
        if (compression is Rle8 or Rle4)
        {
            if (topDown || bits != (compression == Rle8 ? 8 : 4))
            {
                throw new InvalidDataException("the BMP run-length data is broken");
            }

            DecodeRle(pixels, compression == Rle4, palette, image);
            return image;
        }

        if (compression is not (Rgb or BitFields or AlphaBitFields) || bits is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw new InvalidDataException($"BMP compression {compression} with {bits}-bit pixels is not supported");
        }

        int stride = (int)(((long)width * bits + 31) / 32 * 4);
        if ((long)stride * height > pixels.Length)
        {
            throw new InvalidDataException("the BMP pixel data is cut short");
        }

        bool anyAlpha = false;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(stride * (topDown ? y : height - 1 - y), stride);
            int o = y * width * 4;
            for (int x = 0; x < width; x++, o += 4)
            {
                uint argb;
                if (bits <= 8)
                {
                    int index = bits switch
                    {
                        8 => row[x],
                        4 => (row[x >> 1] >> (x % 2 == 0 ? 4 : 0)) & 0xF,
                        _ => (row[x >> 3] >> (7 - x % 8)) & 1,
                    };
                    argb = index < palette.Length ? palette[index] : 0xFF000000;
                }
                else
                {
                    uint v = bits switch
                    {
                        16 => BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]),
                        24 => (uint)(row[x * 3] | row[x * 3 + 1] << 8 | row[x * 3 + 2] << 16),
                        _ => BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]),
                    };
                    argb = Channel(v, masks[3]) << 24 | Channel(v, masks[0]) << 16 | Channel(v, masks[1]) << 8 | Channel(v, masks[2]);
                    anyAlpha |= masks[3] != 0 && argb >> 24 != 0;
                }

                Put(image.Pixels, o, argb);
            }
        }

        if (bits > 8 && masks[3] != 0 && !anyAlpha)
        {
            for (int i = 3; i < image.Pixels.Length; i += 4)
            {
                image.Pixels[i] = 255;
            }
        }

        return image;
    }

    /// <summary>The red, green, blue and alpha masks; alpha 0 when the picture has none (opaque).</summary>
    private static uint[] Masks(ReadOnlySpan<byte> data, int header, int bits, int compression)
    {
        if (compression is BitFields or AlphaBitFields)
        {
            // Version 3+ headers hold the masks; a 40-byte header is followed by them.
            int count = compression == AlphaBitFields || header >= 56 ? 4 : 3;
            if (14 + 40 + count * 4 > data.Length)
            {
                throw new InvalidDataException("the BMP colour masks are missing");
            }

            var masks = new uint[4];
            for (int i = 0; i < count; i++)
            {
                masks[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(54 + i * 4)..]);
            }

            return masks;
        }

        return bits switch
        {
            16 => [0x7C00, 0x03E0, 0x001F, 0],
            // A plain 32-bit picture leaves its fourth byte unused, unless a version 4+ header gives it an alpha mask.
            32 when header >= 108 && BinaryPrimitives.ReadUInt32LittleEndian(data[66..]) is var alpha && alpha != 0 =>
                [0x00FF0000, 0x0000FF00, 0x000000FF, alpha],
            _ => [0x00FF0000, 0x0000FF00, 0x000000FF, 0],
        };
    }

    private static uint[] Palette(ReadOnlySpan<byte> data, int header, int bits, bool core)
    {
        int entry = core ? 3 : 4;
        int used = core ? 0 : BinaryPrimitives.ReadInt32LittleEndian(data[46..]);
        int count = used is > 0 and <= 256 ? used : 1 << bits;
        int at = 14 + header;
        var palette = new uint[count];
        for (int i = 0; i < count && at + i * entry + 3 <= data.Length; i++)
        {
            ReadOnlySpan<byte> c = data[(at + i * entry)..];
            palette[i] = 0xFF000000 | (uint)c[2] << 16 | (uint)c[1] << 8 | c[0];
        }

        return palette;
    }

    /// <summary>A masked channel scaled to 0..255; a missing alpha mask gives an opaque pixel.</summary>
    private static uint Channel(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 255;
        }

        int shift = BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (uint)(((value & mask) >> shift) * 255 + max / 2) / max;
    }

    private static void Put(byte[] pixels, int o, uint argb)
    {
        pixels[o] = (byte)(argb >> 16);
        pixels[o + 1] = (byte)(argb >> 8);
        pixels[o + 2] = (byte)argb;
        pixels[o + 3] = (byte)(argb >> 24);
    }

    /// <summary>RLE8 and RLE4: rows bottom up, runs and literal groups, with end-of-line, end-of-file and skip codes.
    /// Skipped pixels stay transparent.</summary>
    private static void DecodeRle(ReadOnlySpan<byte> rle, bool four, uint[] palette, RgbaImage image)
    {
        int x = 0, y = image.Height - 1, at = 0;
        void Set(int index)
        {
            if (x < image.Width && y >= 0)
            {
                Put(image.Pixels, (y * image.Width + x) * 4, index < palette.Length ? palette[index] : 0xFF000000);
            }

            x++;
        }

        while (at + 1 < rle.Length && y >= 0)
        {
            int count = rle[at], value = rle[at + 1];
            at += 2;
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    Set(four ? (i % 2 == 0 ? value >> 4 : value & 0xF) : value);
                }
            }
            else if (value == 0)
            {
                x = 0;
                y--;
            }
            else if (value == 1)
            {
                return;
            }
            else if (value == 2)
            {
                if (at + 1 >= rle.Length)
                {
                    break;
                }

                x += rle[at];
                y -= rle[at + 1];
                at += 2;
            }
            else
            {
                int bytes = four ? (value + 1) / 2 : value;
                if (at + bytes > rle.Length)
                {
                    break;
                }

                for (int i = 0; i < value; i++)
                {
                    Set(four ? (i % 2 == 0 ? rle[at + i / 2] >> 4 : rle[at + i / 2] & 0xF) : rle[at + i]);
                }

                at += (bytes + 1) & ~1;
            }
        }
    }
}
