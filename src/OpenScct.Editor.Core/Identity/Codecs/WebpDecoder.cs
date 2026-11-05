using System.Buffers.Binary;

namespace OpenScct.Editor.Core.Identity.Codecs;

/// <summary>
/// WebP files ("WebP Container Specification"): a lossy picture (VP8, with an ALPH alpha plane in the extended
/// format) or a lossless one (VP8L). Animated WebP is refused; colour profiles and metadata are ignored.
/// </summary>
internal static class WebpDecoder
{
    public static bool Is(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    public static (int Width, int Height)? SizeOf(ReadOnlySpan<byte> data)
    {
        if (!Is(data) || data.Length < 30)
        {
            return null;
        }

        ReadOnlySpan<byte> payload = data[20..];
        return data.Slice(12, 4) switch
        {
            var t when t.SequenceEqual("VP8X"u8) => (Uint24(payload[4..]) + 1, Uint24(payload[7..]) + 1),
            var t when t.SequenceEqual("VP8L"u8) => Vp8LDecoder.SizeOf(payload),
            var t when t.SequenceEqual("VP8 "u8) => Vp8Decoder.SizeOf(payload),
            _ => null,
        };
    }

    public static RgbaImage Decode(ReadOnlySpan<byte> data)
    {
        if (!Is(data))
        {
            throw new InvalidDataException("not a WebP file");
        }

        ReadOnlySpan<byte> alpha = default;
        bool hasAlpha = false;
        int end = (int)Math.Min(data.Length, 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        for (int at = 12; at + 8 <= end;)
        {
            ReadOnlySpan<byte> type = data.Slice(at, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            if (at + 8 + size > end)
            {
                throw new InvalidDataException("a WebP chunk runs past the end of the file");
            }

            ReadOnlySpan<byte> payload = data.Slice(at + 8, (int)size);
            if (type.SequenceEqual("VP8X"u8) && payload.Length > 0 && (payload[0] & 0x02) != 0)
            {
                throw new InvalidDataException("animated WebP is not supported");
            }

            if (type.SequenceEqual("ALPH"u8))
            {
                alpha = payload;
                hasAlpha = true;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                (uint[] argb, int width, int height) = Vp8LDecoder.DecodeImage(payload);
                var image = new RgbaImage(width, height);
                for (int i = 0; i < argb.Length; i++)
                {
                    uint p = argb[i];
                    image.Pixels[i * 4] = (byte)(p >> 16);
                    image.Pixels[i * 4 + 1] = (byte)(p >> 8);
                    image.Pixels[i * 4 + 2] = (byte)p;
                    image.Pixels[i * 4 + 3] = (byte)(p >> 24);
                }

                return image;
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                RgbaImage image = Vp8Decoder.Decode(payload);
                if (hasAlpha)
                {
                    ApplyAlpha(alpha, image);
                }

                return image;
            }

            at += 8 + (int)size + (int)(size & 1);
        }

        throw new InvalidDataException("the WebP file holds no picture");
    }

    private static int Uint24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;

    /// <summary>An ALPH chunk: one header byte (filter in bits 2-3, compression in bits 0-1), then the plane raw or as
    /// a lossless image, filtered as the spec's horizontal, vertical or gradient predictors undo.</summary>
    private static void ApplyAlpha(ReadOnlySpan<byte> chunk, RgbaImage image)
    {
        if (chunk.IsEmpty)
        {
            throw new InvalidDataException("the WebP alpha plane is empty");
        }

        int w = image.Width, h = image.Height;
        int compression = chunk[0] & 3, filter = (chunk[0] >> 2) & 3;
        var a = new byte[w * h];
        if (compression == 0)
        {
            if (chunk.Length - 1 < a.Length)
            {
                throw new InvalidDataException("the WebP alpha plane is cut short");
            }

            chunk.Slice(1, a.Length).CopyTo(a);
        }
        else if (compression == 1)
        {
            uint[] argb = Vp8LDecoder.DecodeAlpha(chunk[1..], w, h);
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = (byte)(argb[i] >> 8);
            }
        }
        else
        {
            throw new InvalidDataException("the WebP alpha compression is not supported");
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int left = x > 0 ? a[i - 1] : 0, up = y > 0 ? a[i - w] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    // The first row predicts from the left, the first column from above (pixel 0 from nothing).
                    _ when y == 0 => left,
                    _ when x == 0 => up,
                    1 => left,
                    2 => up,
                    _ => Math.Clamp(left + up - a[i - w - 1], 0, 255),
                };
                a[i] = (byte)(a[i] + predicted);
            }
        }

        for (int i = 0; i < a.Length; i++)
        {
            image.Pixels[i * 4 + 3] = a[i];
        }
    }
}
