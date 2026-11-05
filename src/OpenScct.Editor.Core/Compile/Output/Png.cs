using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OpenScct.Editor.Core.Compile.Output;

/// <summary>A small PNG writer (8-bit RGB, or RGBA when a pixel is not opaque; each row with the filter that suits it
/// best, as encoders commonly pick it), a reader (8-bit, not interlaced) and a size reader.</summary>
public static class Png
{
    private static readonly uint[] s_crc = MakeCrcTable();

    public static byte[] EncodeRgba(int width, int height, byte[] rgba)
    {
        bool opaque = true;
        for (int i = 3; i < rgba.Length && opaque; i += 4)
        {
            opaque = rgba[i] == 255;
        }

        // Photos and renders are opaque: three bytes a pixel make the file a quarter smaller before compression.
        int bpp = opaque ? 3 : 4;
        int stride = width * bpp;
        byte[] previous = new byte[stride], row = new byte[stride], best = new byte[stride], trial = new byte[stride];
        using var raw = new MemoryStream(height * (stride + 1));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0, o = 0, i = y * width * 4; x < width; x++, i += 4, o += bpp)
            {
                row[o] = rgba[i];
                row[o + 1] = rgba[i + 1];
                row[o + 2] = rgba[i + 2];
                if (bpp == 4)
                {
                    row[o + 3] = rgba[i + 3];
                }
            }

            byte bestFilter = 0;
            long bestScore = long.MaxValue;
            for (byte filter = 0; filter <= 4; filter++)
            {
                long score = Filter(filter, row, previous, trial, bpp);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = filter;
                    (best, trial) = (trial, best);
                }
            }

            raw.WriteByte(bestFilter);
            raw.Write(best);
            (previous, row) = (row, previous);
        }

        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zs);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(opaque ? 2 : 6);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>Filters one row into <paramref name="output"/> and returns the sum of its bytes taken as signed values,
    /// the usual measure of how well a row will compress.</summary>
    private static long Filter(byte filter, byte[] row, byte[] previous, byte[] output, int bpp)
    {
        long score = 0;
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0, up = previous[i], upLeft = i >= bpp ? previous[i - bpp] : 0;
            int predicted = filter switch
            {
                1 => left,
                2 => up,
                3 => (left + up) >> 1,
                4 => Paeth(left, up, upLeft),
                _ => 0,
            };
            byte value = (byte)(row[i] - predicted);
            output[i] = value;
            score += value < 128 ? value : 256 - value;
        }

        return score;
    }

    /// <summary>
    /// Decodes an 8-bit, non-interlaced PNG (grey, RGB, palette, grey + alpha or RGBA) to RGBA bytes, for lightmap
    /// checks and material colours; null for anything else.
    /// </summary>
    public static (int Width, int Height, byte[] Rgba)? Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || png[0] != 0x89 || png[1] != (byte)'P')
        {
            return null;
        }

        int width = 0, height = 0, colourType = 0;
        byte[]? palette = null, alpha = null;
        using var idat = new MemoryStream();
        for (int at = 8; at + 12 <= png.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[at..]);
            ReadOnlySpan<byte> type = png.Slice(at + 4, 4), data = png.Slice(at + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                colourType = data[9];
                if (data[8] != 8 || data[12] != 0)
                {
                    return null;
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                alpha = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }

            at += 12 + length;
        }

        int channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || width <= 0 || height <= 0 || (colourType == 3 && palette is null))
        {
            return null;
        }

        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            z.ReadExactly(raw);
        }

        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            Unfilter(raw[y * (stride + 1)], raw.AsSpan(y * (stride + 1) + 1, stride), y == 0 ? default : pixels.AsSpan((y - 1) * stride, stride),
                pixels.AsSpan(y * stride, stride), channels);
        }

        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            ReadOnlySpan<byte> p = pixels.AsSpan(i * channels, channels);
            (byte r, byte g, byte b, byte a) = colourType switch
            {
                0 => (p[0], p[0], p[0], (byte)255),
                2 => (p[0], p[1], p[2], (byte)255),
                3 => (palette![p[0] * 3], palette[(p[0] * 3) + 1], palette[(p[0] * 3) + 2], alpha is not null && p[0] < alpha.Length ? alpha[p[0]] : (byte)255),
                4 => (p[0], p[0], p[0], p[1]),
                _ => (p[0], p[1], p[2], p[3]),
            };
            rgba[i * 4] = r;
            rgba[(i * 4) + 1] = g;
            rgba[(i * 4) + 2] = b;
            rgba[(i * 4) + 3] = a;
        }

        return (width, height, rgba);
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> line, ReadOnlySpan<byte> above, Span<byte> into, int bpp)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? into[i - bpp] : 0, b = above.IsEmpty ? 0 : above[i], c = i >= bpp && !above.IsEmpty ? above[i - bpp] : 0;
            int predictor = filter switch
            {
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            into[i] = (byte)(line[i] + predictor);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>The width and height of a PNG file, or null when it is not one.</summary>
    public static (int Width, int Height)? SizeOf(string path)
    {
        try
        {
            using FileStream f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[24];
            if (f.Read(head) < 24 || head[0] != 0x89 || head[1] != (byte)'P')
            {
                return null;
            }

            return ((int)BinaryPrimitives.ReadUInt32BigEndian(head[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(head[20..]));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        byte[] t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        crc = Update(crc, t);
        crc = Update(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc ^ 0xFFFFFFFF);
        s.Write(len);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>Stand-in textures for materials no converted map supplies: a grid in a colour picked from the name.</summary>
public static class GridTexture
{
    public const int Size = 64;

    /// <summary>The texture's size in texels as UV maths sees it (one tile per 256 uu, like the stock editor grid).</summary>
    public const int UvSize = 256;

    public static byte[] Make(string materialName)
    {
        uint h = 2166136261;
        foreach (char c in materialName.ToLowerInvariant())
        {
            h = (h ^ c) * 16777619;
        }

        // A muted colour: grid lines darker than the cell, a brighter line every quarter.
        byte r = (byte)(96 + (h & 0x5F)), g = (byte)(96 + ((h >> 8) & 0x5F)), b = (byte)(96 + ((h >> 16) & 0x5F));
        if (materialName.Equals(Geometry.Starter.DefaultMaterial, StringComparison.OrdinalIgnoreCase))
        {
            (r, g, b) = (150, 150, 150);
        }

        var rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool major = x == 0 || y == 0;
                bool minor = x % 16 == 0 || y % 16 == 0;
                double k = major ? 0.55 : minor ? 0.8 : 1.0;
                int i = (y * Size + x) * 4;
                rgba[i] = (byte)(r * k);
                rgba[i + 1] = (byte)(g * k);
                rgba[i + 2] = (byte)(b * k);
                rgba[i + 3] = 255;
            }
        }

        return Png.EncodeRgba(Size, Size, rgba);
    }
}
