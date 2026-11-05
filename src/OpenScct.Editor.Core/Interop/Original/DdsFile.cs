using System.Buffers.Binary;
using OpenScct.Editor.Core.Identity;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>How a texture uses alpha: none (opaque), a cut-out (on or off, drawn masked) or graded (an alpha texture).</summary>
public enum AlphaKind
{
    None,
    Mask,
    Blend,
}

/// <summary>A texture as written for the original editor: its DDS bytes, the DXT format and the size it has.</summary>
public sealed record DdsTexture(byte[] Bytes, string Format, int Width, int Height, int Mips);

/// <summary>
/// Textures as DDS files the stock editor's <c>TEXTURE IMPORT</c> keeps as they are (DXT format and mip chain):
/// sides a power of two, DXT1 for an opaque image, DXT5 for one with alpha (a
/// cut-out imported MASKED, a graded one ALPHATEXTURE), and the whole mip chain
/// down to 1x1, box-filtered in linear light so the original game draws the same texture the editor shows.
/// </summary>
public static class DdsFile
{
    /// <summary>The largest side written (larger ones are scaled down).</summary>
    public const int Largest = 2048;

    /// <summary>How an image uses alpha: none when nothing is below 250; a cut-out when at most 2% of its pixels are
    /// part clear (16..239); graded otherwise.</summary>
    public static AlphaKind AlphaOf(RgbaImage image)
    {
        int low = 255, middle = 0, count = image.Width * image.Height;
        for (int i = 3; i < image.Pixels.Length; i += 4)
        {
            byte a = image.Pixels[i];
            low = Math.Min(low, a);
            if (a is >= 16 and < 240)
            {
                middle++;
            }
        }

        return low >= 250 ? AlphaKind.None : middle <= 0.02 * count ? AlphaKind.Mask : AlphaKind.Blend;
    }

    /// <summary>The power of two nearest <paramref name="n"/> (at least 1, at most <see cref="Largest"/>): UE2
    /// textures have power-of-two sides.</summary>
    public static int PowerOfTwo(int n)
    {
        int p = 1;
        while (p * 2 <= n)
        {
            p *= 2;
        }

        if (p * 2 <= Largest && p * 2 - n < n - p)
        {
            p *= 2;
        }

        return Math.Min(p, Largest);
    }

    public static DdsTexture Encode(RgbaImage image, AlphaKind alpha)
    {
        RgbaImage level = image.Resize(PowerOfTwo(image.Width), PowerOfTwo(image.Height));
        if (alpha == AlphaKind.Mask)
        {
            Snap(level.Pixels);
        }

        bool dxt5 = alpha != AlphaKind.None;
        var levels = new List<RgbaImage> { level };
        while (level.Width > 1 || level.Height > 1)
        {
            level = Half(level);
            if (alpha == AlphaKind.Mask)
            {
                Snap(level.Pixels);
            }

            levels.Add(level);
        }

        int top = Blocks(levels[0].Width) * Blocks(levels[0].Height) * (dxt5 ? 16 : 8);
        using var ms = new MemoryStream();
        ms.Write(Header(levels[0].Width, levels[0].Height, levels.Count, top, dxt5 ? "DXT5" : "DXT1"));
        foreach (RgbaImage l in levels)
        {
            ms.Write(Compress(l, dxt5));
        }

        return new DdsTexture(ms.ToArray(), dxt5 ? "DXT5" : "DXT1", levels[0].Width, levels[0].Height, levels.Count);
    }

    private static int Blocks(int side) => Math.Max(1, (side + 3) / 4);

    /// <summary>A cut-out's alpha back to on or off (the resample and the mips grade its edges).</summary>
    private static void Snap(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i] >= 128 ? (byte)255 : (byte)0;
        }
    }

    // ---- the mip chain ---------------------------------------------------------------------------------------------

    private static readonly float[] s_toLinear = Enumerable.Range(0, 256).Select(i =>
    {
        double c = i / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    }).ToArray();

    private static byte ToSrgb(double linear)
    {
        double c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(c * 255), 0, 255);
    }

    /// <summary>The next mip: each 2x2 box averaged in linear light, colour weighted by alpha so clear pixels do not
    /// darken the edges of what shows.</summary>
    private static RgbaImage Half(RgbaImage src)
    {
        int w = Math.Max(1, src.Width / 2), h = Math.Max(1, src.Height / 2);
        var dst = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0, a = 0, weight = 0;
                int n = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(2 * x + dx, src.Width - 1), sy = Math.Min(2 * y + dy, src.Height - 1);
                        int i = (sy * src.Width + sx) * 4;
                        double wgt = (src.Pixels[i + 3] + 1) / 256.0;
                        r += s_toLinear[src.Pixels[i]] * wgt;
                        g += s_toLinear[src.Pixels[i + 1]] * wgt;
                        b += s_toLinear[src.Pixels[i + 2]] * wgt;
                        a += src.Pixels[i + 3];
                        weight += wgt;
                        n++;
                    }
                }

                int o = (y * w + x) * 4;
                dst.Pixels[o] = ToSrgb(r / weight);
                dst.Pixels[o + 1] = ToSrgb(g / weight);
                dst.Pixels[o + 2] = ToSrgb(b / weight);
                dst.Pixels[o + 3] = (byte)Math.Round(a / n);
            }
        }

        return dst;
    }

    // ---- DDS --------------------------------------------------------------------------------------------------------

    private static byte[] Header(int width, int height, int mips, int topSize, string fourCc)
    {
        var h = new byte[128];
        "DDS "u8.CopyTo(h);
        Span<byte> d = h.AsSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(d[0..], 124);
        // CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT | LINEARSIZE
        BinaryPrimitives.WriteInt32LittleEndian(d[4..], 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        BinaryPrimitives.WriteInt32LittleEndian(d[8..], height);
        BinaryPrimitives.WriteInt32LittleEndian(d[12..], width);
        BinaryPrimitives.WriteInt32LittleEndian(d[16..], topSize);
        BinaryPrimitives.WriteInt32LittleEndian(d[24..], mips);
        Span<byte> pf = d[72..];
        BinaryPrimitives.WriteInt32LittleEndian(pf[0..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(pf[4..], 0x4); // FOURCC
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(pf[8..]);
        // TEXTURE | MIPMAP | COMPLEX
        BinaryPrimitives.WriteInt32LittleEndian(d[104..], 0x1000 | (mips > 1 ? 0x400000 | 0x8 : 0));
        return h;
    }

    // ---- DXT blocks --------------------------------------------------------------------------------------------------

    private static byte[] Compress(RgbaImage image, bool dxt5)
    {
        int bw = Blocks(image.Width), bh = Blocks(image.Height);
        var output = new byte[bw * bh * (dxt5 ? 16 : 8)];
        Span<byte> block = stackalloc byte[64];
        int o = 0;
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                // A level smaller than a block repeats its edge pixels.
                for (int i = 0; i < 16; i++)
                {
                    int x = Math.Min(bx * 4 + (i & 3), image.Width - 1), y = Math.Min(by * 4 + (i >> 2), image.Height - 1);
                    image.Pixels.AsSpan((y * image.Width + x) * 4, 4).CopyTo(block[(i * 4)..]);
                }

                if (dxt5)
                {
                    AlphaBlock(block, output.AsSpan(o, 8));
                    o += 8;
                }

                ColorBlock(block, output.AsSpan(o, 8));
                o += 8;
            }
        }

        return output;
    }

    /// <summary>DXT5 alpha: the block's lowest and highest alpha as the ends, eight steps between, nearest per pixel.</summary>
    private static void AlphaBlock(ReadOnlySpan<byte> rgba, Span<byte> dst)
    {
        int lo = 255, hi = 0;
        for (int i = 0; i < 16; i++)
        {
            lo = Math.Min(lo, rgba[i * 4 + 3]);
            hi = Math.Max(hi, rgba[i * 4 + 3]);
        }

        dst[0] = (byte)hi;
        dst[1] = (byte)lo;
        Span<int> palette = stackalloc int[8];
        palette[0] = hi;
        palette[1] = lo;
        for (int i = 2; i < 8; i++)
        {
            palette[i] = ((8 - i) * hi + (i - 1) * lo) / 7;
        }

        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = rgba[i * 4 + 3], best = 0;
            if (hi > lo)
            {
                for (int k = 1; k < 8; k++)
                {
                    if (Math.Abs(palette[k] - a) < Math.Abs(palette[best] - a))
                    {
                        best = k;
                    }
                }
            }

            bits |= (ulong)best << (3 * i);
        }

        for (int i = 0; i < 6; i++)
        {
            dst[2 + i] = (byte)(bits >> (8 * i));
        }
    }

    /// <summary>
    /// A DXT colour block, four-colour mode: the ends from the colours' main axis (inset a sixteenth), then one
    /// least-squares refit of the ends to the chosen steps, kept when it is closer.
    /// </summary>
    private static void ColorBlock(ReadOnlySpan<byte> rgba, Span<byte> dst)
    {
        Span<float> c = stackalloc float[48];
        for (int i = 0; i < 16; i++)
        {
            c[i * 3] = rgba[i * 4];
            c[i * 3 + 1] = rgba[i * 4 + 1];
            c[i * 3 + 2] = rgba[i * 4 + 2];
        }

        (ushort e0, ushort e1) = AxisEnds(c);
        Span<int> idx = stackalloc int[16];
        long err = Indices(c, e0, e1, idx);
        if (Refit(c, idx) is { } refit && refit.A != refit.B)
        {
            Span<int> idx2 = stackalloc int[16];
            long err2 = Indices(c, refit.A, refit.B, idx2);
            if (err2 < err)
            {
                (e0, e1, err) = (refit.A, refit.B, err2);
                idx2.CopyTo(idx);
            }
        }

        // Four-colour mode needs the first end above the second; swapping them swaps the steps.
        if (e0 < e1)
        {
            (e0, e1) = (e1, e0);
            for (int i = 0; i < 16; i++)
            {
                idx[i] = idx[i] switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 };
            }
        }
        else if (e0 == e1)
        {
            idx.Clear();
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dst, e0);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[2..], e1);
        uint bits = 0;
        for (int i = 0; i < 16; i++)
        {
            bits |= (uint)idx[i] << (2 * i);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], bits);
    }

    private static (ushort, ushort) AxisEnds(ReadOnlySpan<float> c)
    {
        float mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < 16; i++)
        {
            mr += c[i * 3];
            mg += c[i * 3 + 1];
            mb += c[i * 3 + 2];
        }

        mr /= 16;
        mg /= 16;
        mb /= 16;
        Span<float> cov = stackalloc float[6];
        for (int i = 0; i < 16; i++)
        {
            float r = c[i * 3] - mr, g = c[i * 3 + 1] - mg, b = c[i * 3 + 2] - mb;
            cov[0] += r * r;
            cov[1] += r * g;
            cov[2] += r * b;
            cov[3] += g * g;
            cov[4] += g * b;
            cov[5] += b * b;
        }

        // The main axis by power iteration, from the widest channel.
        float vr = cov[0], vg = cov[3], vb = cov[5];
        (float ar, float ag, float ab) = vr >= vg && vr >= vb ? (1f, 0f, 0f) : vg >= vb ? (0f, 1f, 0f) : (0f, 0f, 1f);
        for (int k = 0; k < 6; k++)
        {
            float r = ar * cov[0] + ag * cov[1] + ab * cov[2];
            float g = ar * cov[1] + ag * cov[3] + ab * cov[4];
            float b = ar * cov[2] + ag * cov[4] + ab * cov[5];
            float len = MathF.Max(MathF.Max(MathF.Abs(r), MathF.Abs(g)), MathF.Abs(b));
            if (len < 1e-6f)
            {
                break;
            }

            (ar, ag, ab) = (r / len, g / len, b / len);
        }

        float lo = float.MaxValue, hi = float.MinValue;
        int iLo = 0, iHi = 0;
        for (int i = 0; i < 16; i++)
        {
            float d = c[i * 3] * ar + c[i * 3 + 1] * ag + c[i * 3 + 2] * ab;
            if (d < lo)
            {
                (lo, iLo) = (d, i);
            }

            if (d > hi)
            {
                (hi, iHi) = (d, i);
            }
        }

        Span<float> a = stackalloc float[3], b2 = stackalloc float[3];
        for (int k = 0; k < 3; k++)
        {
            float x = c[iHi * 3 + k], y = c[iLo * 3 + k], inset = (x - y) / 16;
            a[k] = x - inset;
            b2[k] = y + inset;
        }

        return (To565(a[0], a[1], a[2]), To565(b2[0], b2[1], b2[2]));
    }

    /// <summary>The nearest of the four steps for each pixel; returns the summed squared error.</summary>
    private static long Indices(ReadOnlySpan<float> c, ushort e0, ushort e1, Span<int> idx)
    {
        Span<int> pal = stackalloc int[12];
        From565(e0, pal[0..3]);
        From565(e1, pal[3..6]);
        for (int k = 0; k < 3; k++)
        {
            pal[6 + k] = (2 * pal[k] + pal[3 + k]) / 3;
            pal[9 + k] = (pal[k] + 2 * pal[3 + k]) / 3;
        }

        long total = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            long bestErr = long.MaxValue;
            for (int s = 0; s < 4; s++)
            {
                long err = 0;
                for (int k = 0; k < 3; k++)
                {
                    long d = (long)c[i * 3 + k] - pal[s * 3 + k];
                    err += d * d;
                }

                if (err < bestErr)
                {
                    (bestErr, best) = (err, s);
                }
            }

            idx[i] = best;
            total += bestErr;
        }

        return total;
    }

    /// <summary>The ends that fit the pixels best for the chosen steps (least squares), or null when the steps do not
    /// tell the ends apart.</summary>
    private static (ushort A, ushort B)? Refit(ReadOnlySpan<float> c, ReadOnlySpan<int> idx)
    {
        // Step s puts a pixel at t of the way from end A to end B: 0, 1, 1/3, 2/3.
        ReadOnlySpan<float> t = [0f, 1f, 1f / 3, 2f / 3];
        float aa = 0, bb = 0, ab = 0;
        Span<float> ax = stackalloc float[3], bx = stackalloc float[3];
        for (int i = 0; i < 16; i++)
        {
            float wb = t[idx[i]], wa = 1 - wb;
            aa += wa * wa;
            bb += wb * wb;
            ab += wa * wb;
            for (int k = 0; k < 3; k++)
            {
                ax[k] += wa * c[i * 3 + k];
                bx[k] += wb * c[i * 3 + k];
            }
        }

        float det = aa * bb - ab * ab;
        if (MathF.Abs(det) < 1e-6f)
        {
            return null;
        }

        Span<float> a = stackalloc float[3], b = stackalloc float[3];
        for (int k = 0; k < 3; k++)
        {
            a[k] = (ax[k] * bb - bx[k] * ab) / det;
            b[k] = (bx[k] * aa - ax[k] * ab) / det;
        }

        return (To565(a[0], a[1], a[2]), To565(b[0], b[1], b[2]));
    }

    private static ushort To565(float r, float g, float b)
    {
        int R = Math.Clamp((int)MathF.Round(r * 31 / 255), 0, 31);
        int G = Math.Clamp((int)MathF.Round(g * 63 / 255), 0, 63);
        int B = Math.Clamp((int)MathF.Round(b * 31 / 255), 0, 31);
        return (ushort)((R << 11) | (G << 5) | B);
    }

    private static void From565(ushort v, Span<int> rgb)
    {
        int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
        rgb[0] = (r << 3) | (r >> 2);
        rgb[1] = (g << 2) | (g >> 4);
        rgb[2] = (b << 3) | (b >> 2);
    }
}
