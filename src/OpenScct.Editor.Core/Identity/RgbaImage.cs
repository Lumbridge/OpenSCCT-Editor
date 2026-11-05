using System.Buffers.Binary;
using System.IO.Compression;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Identity.Codecs;

namespace OpenScct.Editor.Core.Identity;

/// <summary>
/// An 8-bit RGBA picture in memory, with the few operations map pictures need: decoders for PNG (every colour type and
/// bit depth, not interlaced), JPEG, BMP and WebP (<see cref="Codecs"/>), a crop to a shape around a focal point and an
/// area-averaging downscale. Core has no image library, and pictures must come out the same on every machine.
/// </summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4)
        {
            throw new ArgumentException("the pixel buffer does not match the size", nameof(pixels));
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Rows top to bottom, four bytes a pixel (R, G, B, A).</summary>
    public byte[] Pixels { get; }

    public byte[] EncodePng() => Png.EncodeRgba(Width, Height, Pixels);

    /// <summary>The largest area of the given shape (width over height) inside the picture, centred on the focal point
    /// as far as the edges allow.</summary>
    public RgbaImage Crop(double focusX, double focusY, double aspect)
    {
        (int x0, int y0, int w, int h) = CropArea(Width, Height, focusX, focusY, aspect);
        if (w == Width && h == Height)
        {
            return this;
        }

        var crop = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(Pixels, ((y0 + y) * Width + x0) * 4, crop.Pixels, y * w * 4, w * 4);
        }

        return crop;
    }

    /// <summary>The area <see cref="Crop"/> keeps of a picture of the given size (the app's previews show the same).</summary>
    public static (int X, int Y, int Width, int Height) CropArea(int width, int height, double focusX, double focusY, double aspect)
    {
        int w = width, h = Math.Min(height, Math.Max(1, (int)Math.Round(width / aspect)));
        if (h == height)
        {
            w = Math.Min(width, Math.Max(1, (int)Math.Round(height * aspect)));
        }

        int x0 = Math.Clamp((int)Math.Round(focusX * width - w / 2.0), 0, width - w);
        int y0 = Math.Clamp((int)Math.Round(focusY * height - h / 2.0), 0, height - h);
        return (x0, y0, w, h);
    }

    /// <summary>The picture scaled down to fit <paramref name="maxSide"/> (unchanged when it already fits). Every output
    /// pixel averages the source area it covers, weighted by alpha so transparent pixels do not darken the edges.</summary>
    public RgbaImage FitWithin(int maxSide)
    {
        if (Width <= maxSide && Height <= maxSide)
        {
            return this;
        }

        double scale = (double)maxSide / Math.Max(Width, Height);
        return Resize(Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)));
    }

    /// <summary>The image resampled to <paramref name="w"/> x <paramref name="h"/>: each pixel the area-weighted mean of
    /// the pixels it covers, colours weighted by alpha so clear pixels do not darken edges.</summary>
    public RgbaImage Resize(int w, int h)
    {
        if (w == Width && h == Height)
        {
            return this;
        }

        var result = new RgbaImage(w, h);
        double sx = (double)Width / w, sy = (double)Height / h;
        for (int y = 0; y < h; y++)
        {
            double top = y * sy, bottom = top + sy;
            for (int x = 0; x < w; x++)
            {
                double left = x * sx, right = left + sx;
                double r = 0, g = 0, b = 0, a = 0, area = 0;
                for (int py = (int)top; py < Math.Min(Height, (int)Math.Ceiling(bottom)); py++)
                {
                    double wy = Math.Min(bottom, py + 1) - Math.Max(top, py);
                    for (int px = (int)left; px < Math.Min(Width, (int)Math.Ceiling(right)); px++)
                    {
                        double weight = wy * (Math.Min(right, px + 1) - Math.Max(left, px));
                        int i = (py * Width + px) * 4;
                        double alpha = Pixels[i + 3] * weight;
                        r += Pixels[i] * alpha;
                        g += Pixels[i + 1] * alpha;
                        b += Pixels[i + 2] * alpha;
                        a += alpha;
                        area += weight;
                    }
                }

                int o = (y * w + x) * 4;
                if (a > 0)
                {
                    result.Pixels[o] = (byte)Math.Round(r / a);
                    result.Pixels[o + 1] = (byte)Math.Round(g / a);
                    result.Pixels[o + 2] = (byte)Math.Round(b / a);
                }

                result.Pixels[o + 3] = (byte)Math.Round(a / area);
            }
        }

        return result;
    }

    /// <summary>The most pixels a decoded picture may have (8192 x 8192, 256 MiB of RGBA).</summary>
    public const long MaxPixels = 8192L * 8192;

    /// <summary>The picture formats <see cref="Decode"/> reads, as named in messages.</summary>
    public const string Formats = "PNG, JPEG, BMP or WebP";

    /// <summary>Decodes a PNG, JPEG, BMP or WebP file, told apart by their signatures; throws
    /// <see cref="InvalidDataException"/> for another format or a broken file.</summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> data)
    {
        string? format = FormatOf(data);
        try
        {
            return format switch
            {
                "PNG" => DecodePng(data),
                "JPEG" => JpegDecoder.Decode(data),
                "BMP" => BmpDecoder.Decode(data),
                "WebP" => WebpDecoder.Decode(data),
                _ => throw new InvalidDataException($"not a {Formats} file"),
            };
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException)
        {
            // A broken file can point a decoder outside its data.
            throw new InvalidDataException($"the {format} file is cut short or broken", e);
        }
    }

    /// <summary>"PNG", "JPEG", "BMP" or "WebP" from a file's first bytes, or null.</summary>
    public static string? FormatOf(ReadOnlySpan<byte> head) =>
        head.Length >= 8 && head[..8].SequenceEqual(s_signature) ? "PNG"
        : JpegDecoder.Is(head) ? "JPEG"
        : BmpDecoder.Is(head) ? "BMP"
        : WebpDecoder.Is(head) ? "WebP"
        : null;

    /// <summary>The width and height a picture file states in its header (its first few KiB), or null when it is not
    /// one <see cref="Decode"/> reads.</summary>
    public static (int Width, int Height)? SizeOf(ReadOnlySpan<byte> head) => FormatOf(head) switch
    {
        "PNG" when head.Length >= 24 => ((int)BinaryPrimitives.ReadUInt32BigEndian(head[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(head[20..])),
        "JPEG" => JpegSize(head),
        "BMP" => BmpDecoder.SizeOf(head),
        "WebP" => WebpDecoder.SizeOf(head),
        _ => null,
    };

    /// <summary>Decodes a PNG file; throws <see cref="InvalidDataException"/> for a broken or interlaced one.</summary>
    public static RgbaImage DecodePng(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(s_signature))
        {
            throw new InvalidDataException("not a PNG file");
        }

        int width = 0, height = 0, depth = 0, colour = -1;
        byte[] palette = [];
        byte[] alphas = [];
        var idat = new MemoryStream();
        int at = 8;
        while (at + 8 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[at..]);
            if (length < 0 || at + 12 + length > png.Length)
            {
                throw new InvalidDataException("a PNG chunk runs past the end of the file");
            }

            ReadOnlySpan<byte> type = png.Slice(at + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(at + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                depth = data[8];
                colour = data[9];
                if (data[12] != 0)
                {
                    throw new InvalidDataException("interlaced PNG files are not supported; save it without interlacing");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                alphas = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            at += 12 + length;
        }

        int channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (width <= 0 || height <= 0 || channels == 0 || depth is not (1 or 2 or 4 or 8 or 16) || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException($"unsupported PNG (colour type {colour}, depth {depth}, {width}x{height})");
        }

        int rowBytes = (width * channels * depth + 7) / 8;
        int bpp = Math.Max(1, channels * depth / 8);
        byte[] raw = Inflate(idat.ToArray(), (rowBytes + 1) * height);
        var image = new RgbaImage(width, height);
        byte[] previous = new byte[rowBytes], row = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            int start = y * (rowBytes + 1);
            Unfilter(raw[start], raw.AsSpan(start + 1, rowBytes), previous, row, bpp);
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                switch (colour)
                {
                    case 0:
                        int grey = Sample(row, x, depth);
                        image.Pixels[o] = image.Pixels[o + 1] = image.Pixels[o + 2] = (byte)grey;
                        image.Pixels[o + 3] = alphas.Length >= 2 && Raw(row, x, depth) == BinaryPrimitives.ReadUInt16BigEndian(alphas) ? (byte)0 : (byte)255;
                        break;
                    case 2:
                        image.Pixels[o] = (byte)Sample(row, x * 3, depth);
                        image.Pixels[o + 1] = (byte)Sample(row, x * 3 + 1, depth);
                        image.Pixels[o + 2] = (byte)Sample(row, x * 3 + 2, depth);
                        image.Pixels[o + 3] = alphas.Length >= 6 && Raw(row, x * 3, depth) == BinaryPrimitives.ReadUInt16BigEndian(alphas)
                            && Raw(row, x * 3 + 1, depth) == BinaryPrimitives.ReadUInt16BigEndian(alphas.AsSpan(2))
                            && Raw(row, x * 3 + 2, depth) == BinaryPrimitives.ReadUInt16BigEndian(alphas.AsSpan(4)) ? (byte)0 : (byte)255;
                        break;
                    case 3:
                        int index = Raw(row, x, depth);
                        if (index * 3 + 2 >= palette.Length)
                        {
                            throw new InvalidDataException("a PNG palette index is out of range");
                        }

                        image.Pixels[o] = palette[index * 3];
                        image.Pixels[o + 1] = palette[index * 3 + 1];
                        image.Pixels[o + 2] = palette[index * 3 + 2];
                        image.Pixels[o + 3] = index < alphas.Length ? alphas[index] : (byte)255;
                        break;
                    case 4:
                        image.Pixels[o] = image.Pixels[o + 1] = image.Pixels[o + 2] = (byte)Sample(row, x * 2, depth);
                        image.Pixels[o + 3] = (byte)Sample(row, x * 2 + 1, depth);
                        break;
                    default:
                        for (int c = 0; c < 4; c++)
                        {
                            image.Pixels[o + c] = (byte)Sample(row, x * 4 + c, depth);
                        }

                        break;
                }
            }

            (previous, row) = (row, previous);
        }

        return image;
    }

    /// <summary>The width and height of a baseline or progressive JPEG file, or null when it is not one.</summary>
    public static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return null;
        }

        int at = 2;
        while (at + 9 < jpeg.Length)
        {
            if (jpeg[at] != 0xFF)
            {
                return null;
            }

            byte marker = jpeg[at + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 2)..]);
            // SOF0..SOF15 hold the size, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 5)..]));
            }

            at += 2 + length;
        }

        return null;
    }

    private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Inflate(byte[] data, int expected)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var raw = new byte[expected];
        int read = 0;
        while (read < expected)
        {
            int n = z.Read(raw, read, expected - read);
            if (n == 0)
            {
                throw new InvalidDataException("the PNG image data is shorter than its size");
            }

            read += n;
        }

        return raw;
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> line, byte[] previous, byte[] row, int bpp)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0, up = previous[i], upLeft = i >= bpp ? previous[i - bpp] : 0;
            row[i] = (byte)(line[i] + filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => throw new InvalidDataException($"unknown PNG filter {filter}"),
            });
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>The stored value of sample <paramref name="i"/> of a row.</summary>
    private static int Raw(byte[] row, int i, int depth) => depth switch
    {
        16 => BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(i * 2)),
        8 => row[i],
        _ => (row[i * depth / 8] >> (8 - depth - i * depth % 8)) & ((1 << depth) - 1),
    };

    /// <summary>Sample <paramref name="i"/> of a row scaled to 0..255.</summary>
    private static int Sample(byte[] row, int i, int depth) => depth switch
    {
        16 => row[i * 2],
        8 => row[i],
        _ => Raw(row, i, depth) * 255 / ((1 << depth) - 1),
    };
}
