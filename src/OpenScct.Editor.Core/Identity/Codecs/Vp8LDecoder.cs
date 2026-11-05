using System.Numerics;

namespace OpenScct.Editor.Core.Identity.Codecs;

/// <summary>
/// The WebP lossless bitstream (VP8L, "WebP Lossless Bitstream Specification"): transforms, prefix codes, backward
/// references and the colour cache. It decodes whole images and the headerless alpha planes of lossy WebP files.
/// Pixels are ARGB, one <see cref="uint"/> each.
/// </summary>
internal sealed class Vp8LDecoder
{
    private const int Literals = 256, LengthCodes = 24, DistanceCodes = 40;
    private const int Predictor = 0, CrossColor = 1, SubtractGreen = 2, ColorIndexing = 3;

    private readonly byte[] _data;
    private int _pos;
    private ulong _bits;
    private int _bitCount;
    private int _padding;

    private Vp8LDecoder(ReadOnlySpan<byte> data) => _data = data.ToArray();

    /// <summary>The size held in a VP8L chunk's header, or null when it is not one.</summary>
    public static (int Width, int Height)? SizeOf(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < 5 || chunk[0] != 0x2F)
        {
            return null;
        }

        uint v = (uint)(chunk[1] | chunk[2] << 8 | chunk[3] << 16 | chunk[4] << 24);
        return ((int)(v & 0x3FFF) + 1, (int)((v >> 14) & 0x3FFF) + 1);
    }

    /// <summary>A VP8L chunk's image.</summary>
    public static (uint[] Pixels, int Width, int Height) DecodeImage(ReadOnlySpan<byte> chunk)
    {
        var d = new Vp8LDecoder(chunk);
        if (d.ReadBits(8) != 0x2F)
        {
            throw new InvalidDataException("the WebP lossless signature is missing");
        }

        int width = (int)d.ReadBits(14) + 1, height = (int)d.ReadBits(14) + 1;
        d.ReadBits(1);
        if (d.ReadBits(3) != 0)
        {
            throw new InvalidDataException("the WebP lossless version is not supported");
        }

        Check(width, height);
        return (d.DecodeStream(width, height, true), width, height);
    }

    /// <summary>An alpha plane coded as a headerless lossless image (WebP ALPH chunk, compression 1): green holds alpha.</summary>
    public static uint[] DecodeAlpha(ReadOnlySpan<byte> data, int width, int height) => new Vp8LDecoder(data).DecodeStream(width, height, true);

    private static void Check(int width, int height)
    {
        if ((long)width * height > RgbaImage.MaxPixels)
        {
            throw new InvalidDataException($"a {width}x{height} WebP is not supported");
        }
    }

    private uint ReadBits(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        Fill();
        uint v = (uint)(_bits & ((1UL << n) - 1));
        _bits >>= n;
        _bitCount -= n;
        return v;
    }

    /// <summary>Tops the buffer up to at least 32 bits; past the end it adds zero bytes, up to a limit that stops a
    /// broken file from running on.</summary>
    private void Fill()
    {
        while (_bitCount <= 56)
        {
            if (_pos < _data.Length)
            {
                _bits |= (ulong)_data[_pos++] << _bitCount;
            }
            else if (++_padding > 64)
            {
                throw new InvalidDataException("the WebP lossless data is cut short");
            }

            _bitCount += 8;
        }
    }

    private static int SubSample(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    /// <summary>One image stream: (at the top level) its transforms and meta prefix codes, then its pixels.</summary>
    private uint[] DecodeStream(int width, int height, bool topLevel)
    {
        var transforms = new List<Transform>();
        int codedWidth = width;
        while (topLevel && ReadBits(1) == 1)
        {
            int type = (int)ReadBits(2);
            if (transforms.Any(t => t.Type == type))
            {
                throw new InvalidDataException("a WebP lossless transform is repeated");
            }

            var t = new Transform { Type = type, Width = codedWidth };
            if (type is Predictor or CrossColor)
            {
                t.Bits = (int)ReadBits(3) + 2;
                t.Data = DecodeStream(SubSample(codedWidth, t.Bits), SubSample(height, t.Bits), false);
            }
            else if (type == ColorIndexing)
            {
                int colors = (int)ReadBits(8) + 1;
                t.Bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
                uint[] palette = DecodeStream(colors, 1, false);
                // The palette is coded as differences from the colour before, and indexes past it are transparent black.
                t.Data = new uint[1 << (8 >> t.Bits)];
                for (int i = 0; i < colors; i++)
                {
                    t.Data[i] = i == 0 ? palette[0] : AddPixels(palette[i], t.Data[i - 1]);
                }

                codedWidth = SubSample(codedWidth, t.Bits);
            }

            transforms.Add(t);
        }

        int cacheBits = 0;
        if (ReadBits(1) == 1)
        {
            cacheBits = (int)ReadBits(4);
            if (cacheBits is < 1 or > 11)
            {
                throw new InvalidDataException("the WebP lossless colour cache size is broken");
            }
        }

        int metaBits = 0, metaWidth = 0;
        uint[]? meta = null;
        int groups = 1;
        if (topLevel && ReadBits(1) == 1)
        {
            metaBits = (int)ReadBits(3) + 2;
            metaWidth = SubSample(codedWidth, metaBits);
            meta = DecodeStream(metaWidth, SubSample(height, metaBits), false);
            for (int i = 0; i < meta.Length; i++)
            {
                meta[i] = (meta[i] >> 8) & 0xFFFF;
                groups = Math.Max(groups, (int)meta[i] + 1);
            }
        }

        var codes = new PrefixCode[groups][];
        for (int g = 0; g < groups; g++)
        {
            codes[g] =
            [
                ReadCode(Literals + LengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0)), ReadCode(Literals), ReadCode(Literals),
                ReadCode(Literals), ReadCode(DistanceCodes),
            ];
        }

        uint[] pixels = DecodePixels(codedWidth, height, codes, meta, metaBits, metaWidth, cacheBits);
        for (int i = transforms.Count - 1; i >= 0; i--)
        {
            pixels = transforms[i].Undo(pixels, height);
        }

        return pixels;
    }

    private uint[] DecodePixels(int width, int height, PrefixCode[][] codes, uint[]? meta, int metaBits, int metaWidth, int cacheBits)
    {
        int total = width * height;
        var pixels = new uint[total];
        uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        int cacheShift = 32 - cacheBits;
        void Remember(uint argb)
        {
            if (cache is not null)
            {
                cache[(0x1E35A7BDu * argb) >> cacheShift] = argb;
            }
        }

        for (int i = 0; i < total;)
        {
            int x = i % width, y = i / width;
            PrefixCode[] group = meta is null ? codes[0] : codes[meta[(y >> metaBits) * metaWidth + (x >> metaBits)]];
            int green = group[0].Read(this);
            if (green < Literals)
            {
                int red = group[1].Read(this), blue = group[2].Read(this), alpha = group[3].Read(this);
                uint argb = (uint)(alpha << 24 | red << 16 | green << 8 | blue);
                pixels[i++] = argb;
                Remember(argb);
            }
            else if (green < Literals + LengthCodes)
            {
                int length = PrefixValue(green - Literals);
                int distance = PlaneDistance(width, PrefixValue(group[4].Read(this)));
                if (distance > i || length > total - i)
                {
                    throw new InvalidDataException("a WebP lossless back reference is out of range");
                }

                for (int k = 0; k < length; k++, i++)
                {
                    pixels[i] = pixels[i - distance];
                    Remember(pixels[i]);
                }
            }
            else
            {
                int index = green - Literals - LengthCodes;
                if (cache is null || index >= cache.Length)
                {
                    throw new InvalidDataException("a WebP lossless colour cache index is out of range");
                }

                pixels[i++] = cache[index];
            }
        }

        return pixels;
    }

    /// <summary>A length or distance from its prefix symbol and extra bits.</summary>
    private int PrefixValue(int symbol)
    {
        if (symbol < 4)
        {
            return symbol + 1;
        }

        int extra = (symbol - 2) >> 1;
        return ((2 + (symbol & 1)) << extra) + (int)ReadBits(extra) + 1;
    }

    /// <summary>The first 120 distance codes name nearby pixels by their offset; the rest are distances plus 120.</summary>
    private static int PlaneDistance(int width, int code)
    {
        if (code > 120)
        {
            return code - 120;
        }

        byte v = Vp8Tables.DistancePlane[code - 1];
        int distance = (v >> 4) * width + 8 - (v & 15);
        return Math.Max(1, distance);
    }

    private static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private PrefixCode ReadCode(int alphabet)
    {
        var lengths = new byte[alphabet];
        if (ReadBits(1) == 1)
        {
            // A simple code: one or two symbols.
            int count = (int)ReadBits(1) + 1;
            int first = (int)ReadBits(ReadBits(1) == 0 ? 1 : 8);
            Set(first);
            if (count == 2)
            {
                Set((int)ReadBits(8));
            }

            void Set(int symbol)
            {
                if (symbol >= alphabet)
                {
                    throw new InvalidDataException("a WebP lossless prefix code is broken");
                }

                lengths[symbol] = 1;
            }

            return new PrefixCode(lengths);
        }

        var lengthLengths = new byte[19];
        int codes = (int)ReadBits(4) + 4;
        for (int i = 0; i < codes; i++)
        {
            lengthLengths[CodeLengthOrder[i]] = (byte)ReadBits(3);
        }

        var lengthCode = new PrefixCode(lengthLengths);
        int max = alphabet;
        if (ReadBits(1) == 1)
        {
            max = 2 + (int)ReadBits(2 + 2 * (int)ReadBits(3));
            if (max > alphabet)
            {
                throw new InvalidDataException("a WebP lossless prefix code is broken");
            }
        }

        int previous = 8;
        for (int symbol = 0; symbol < alphabet && max-- > 0;)
        {
            int length = lengthCode.Read(this);
            if (length < 16)
            {
                lengths[symbol++] = (byte)length;
                if (length != 0)
                {
                    previous = length;
                }

                continue;
            }

            int repeat = length switch { 16 => 3 + (int)ReadBits(2), 17 => 3 + (int)ReadBits(3), _ => 11 + (int)ReadBits(7) };
            if (symbol + repeat > alphabet)
            {
                throw new InvalidDataException("a WebP lossless prefix code is broken");
            }

            lengths.AsSpan(symbol, repeat).Fill((byte)(length == 16 ? previous : 0));
            symbol += repeat;
        }

        return new PrefixCode(lengths);
    }

    private static uint AddPixels(uint a, uint b) =>
        (((a & 0xFF00FF00) + (b & 0xFF00FF00)) & 0xFF00FF00) | (((a & 0x00FF00FF) + (b & 0x00FF00FF)) & 0x00FF00FF);

    /// <summary>A canonical prefix code. Codes of up to 8 bits come from a table indexed by the next 8 bits as read
    /// (the first bit read is the code's first bit); longer ones are read a bit at a time.</summary>
    private sealed class PrefixCode
    {
        private const int FastBits = 8;
        private readonly int _single = -1;
        private readonly int[] _counts = new int[16];
        private readonly int[] _symbols;
        private readonly ushort[] _fast = new ushort[1 << FastBits];
        private readonly ushort[] _fastSymbols = new ushort[1 << FastBits];

        public PrefixCode(byte[] lengths)
        {
            int used = 0, last = 0;
            for (int s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] != 0)
                {
                    _counts[lengths[s]]++;
                    used++;
                    last = s;
                }
            }

            _symbols = new int[used];
            if (used == 0)
            {
                throw new InvalidDataException("a WebP lossless prefix code has no symbols");
            }

            if (used == 1)
            {
                // One symbol takes no bits.
                _single = last;
                return;
            }

            var offsets = new int[16];
            for (int len = 1; len < 15; len++)
            {
                offsets[len + 1] = offsets[len] + _counts[len];
            }

            var next = new int[16];
            int code = 0;
            for (int len = 1; len < 16; len++)
            {
                code = (code + _counts[len - 1]) << 1;
                next[len] = code;
            }

            for (int s = 0; s < lengths.Length; s++)
            {
                int len = lengths[s];
                if (len == 0)
                {
                    continue;
                }

                _symbols[offsets[len]++] = s;
                int c = next[len]++;
                if (len <= FastBits)
                {
                    int reversed = (int)(BitReverse((uint)c) >> (32 - len));
                    for (int k = reversed; k < 1 << FastBits; k += 1 << len)
                    {
                        _fast[k] = (ushort)len;
                        _fastSymbols[k] = (ushort)s;
                    }
                }
            }
        }

        private static uint BitReverse(uint v)
        {
            v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
            v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
            v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
            return BinaryReverseBytes(v);
        }

        private static uint BinaryReverseBytes(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);

        public int Read(Vp8LDecoder d)
        {
            if (_single >= 0)
            {
                return _single;
            }

            d.Fill();
            int peek = (int)(d._bits & ((1 << FastBits) - 1));
            if (_fast[peek] != 0)
            {
                int len = _fast[peek];
                d._bits >>= len;
                d._bitCount -= len;
                return _fastSymbols[peek];
            }

            // Bit by bit, as zlib's puff reads canonical codes.
            int code = 0, first = 0, index = 0;
            for (int len = 1; len < 16; len++)
            {
                code |= (int)d.ReadBits(1);
                int count = _counts[len];
                if (code - count < first)
                {
                    return _symbols[index + code - first];
                }

                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }

            throw new InvalidDataException("the WebP lossless data has a bad prefix code");
        }
    }

    private sealed class Transform
    {
        public int Type, Width, Bits;
        public uint[] Data = [];

        /// <summary>The pixels before this transform, from <paramref name="pixels"/> after it.</summary>
        public uint[] Undo(uint[] pixels, int height)
        {
            switch (Type)
            {
                case Predictor:
                    UndoPredictor(pixels, height);
                    return pixels;
                case CrossColor:
                    UndoCrossColor(pixels, height);
                    return pixels;
                case SubtractGreen:
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        uint green = (pixels[i] >> 8) & 0xFF;
                        pixels[i] = AddPixels(pixels[i], green << 16 | green);
                    }

                    return pixels;
                default:
                    return UndoColorIndexing(pixels, height);
            }
        }

        private void UndoPredictor(uint[] p, int height)
        {
            int w = Width, tiles = SubSample(w, Bits);
            p[0] = AddPixels(p[0], 0xFF000000);
            for (int x = 1; x < w; x++)
            {
                p[x] = AddPixels(p[x], p[x - 1]);
            }

            for (int y = 1; y < height; y++)
            {
                int row = y * w;
                p[row] = AddPixels(p[row], p[row - w]);
                for (int x = 1; x < w; x++)
                {
                    int i = row + x;
                    int mode = (int)(Data[(y >> Bits) * tiles + (x >> Bits)] >> 8) & 15;
                    // The pixel above and to the right of the last column is the row's first pixel, as stored.
                    uint l = p[i - 1], t = p[i - w], tl = p[i - w - 1], tr = p[i - w + 1];
                    uint predicted = mode switch
                    {
                        1 => l,
                        2 => t,
                        3 => tr,
                        4 => tl,
                        5 => Average(Average(l, tr), t),
                        6 => Average(l, tl),
                        7 => Average(l, t),
                        8 => Average(tl, t),
                        9 => Average(t, tr),
                        10 => Average(Average(l, tl), Average(t, tr)),
                        11 => Select(l, t, tl),
                        12 => ClampFull(l, t, tl),
                        13 => ClampHalf(Average(l, t), tl),
                        _ => 0xFF000000,
                    };
                    p[i] = AddPixels(p[i], predicted);
                }
            }
        }

        private static uint Average(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

        private static int Channel(uint v, int shift) => (int)(v >> shift) & 0xFF;

        private static uint Select(uint l, uint t, uint tl)
        {
            int toLeft = 0, toTop = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                toLeft += Math.Abs(Channel(t, shift) - Channel(tl, shift));
                toTop += Math.Abs(Channel(l, shift) - Channel(tl, shift));
            }

            return toLeft < toTop ? l : t;
        }

        private static uint ClampFull(uint a, uint b, uint c)
        {
            uint v = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                v |= (uint)Math.Clamp(Channel(a, shift) + Channel(b, shift) - Channel(c, shift), 0, 255) << shift;
            }

            return v;
        }

        private static uint ClampHalf(uint a, uint b)
        {
            uint v = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                int x = Channel(a, shift);
                v |= (uint)Math.Clamp(x + (x - Channel(b, shift)) / 2, 0, 255) << shift;
            }

            return v;
        }

        private void UndoCrossColor(uint[] p, int height)
        {
            int w = Width, tiles = SubSample(w, Bits);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    uint m = Data[(y >> Bits) * tiles + (x >> Bits)];
                    int greenToRed = (sbyte)m, greenToBlue = (sbyte)(m >> 8), redToBlue = (sbyte)(m >> 16);
                    int i = y * w + x;
                    uint argb = p[i];
                    int green = (sbyte)(argb >> 8);
                    int red = (int)(argb >> 16) & 0xFF, blue = (int)argb & 0xFF;
                    red = (red + ((greenToRed * green) >> 5)) & 0xFF;
                    blue = (blue + ((greenToBlue * green) >> 5)) & 0xFF;
                    blue = (blue + ((redToBlue * (sbyte)red) >> 5)) & 0xFF;
                    p[i] = (argb & 0xFF00FF00) | (uint)red << 16 | (uint)blue;
                }
            }
        }

        private uint[] UndoColorIndexing(uint[] packed, int height)
        {
            int w = Width, packedWidth = SubSample(w, Bits);
            int bitsPerIndex = 8 >> Bits, perByte = 1 << Bits, mask = (1 << bitsPerIndex) - 1;
            var pixels = new uint[w * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    uint p = packed[y * packedWidth + (x >> Bits)];
                    int index = (int)(p >> (8 + (x & (perByte - 1)) * bitsPerIndex)) & mask;
                    pixels[y * w + x] = Data[index];
                }
            }

            return pixels;
        }
    }
}
