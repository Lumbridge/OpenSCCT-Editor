using System.Buffers.Binary;

namespace OpenScct.Editor.Core.Identity.Codecs;

/// <summary>
/// JPEG files: baseline, extended and progressive Huffman coding, 8-bit samples, greyscale, YCbCr or RGB, any sampling
/// factors and restart intervals. Chroma is upsampled with libjpeg's triangle filter, so pictures look as other
/// viewers show them. Not read: arithmetic coding, lossless and 12-bit JPEG, CMYK, and the EXIF orientation.
/// </summary>
internal sealed class JpegDecoder
{
    /// <summary>Coefficient positions in zigzag order (ITU T.81 figure 5).</summary>
    private static ReadOnlySpan<byte> Zigzag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly float[] s_cosines = Cosines();

    private readonly byte[] _data;
    private readonly ushort[]?[] _quant = new ushort[4][];
    private readonly Huffman?[] _dcTables = new Huffman[4];
    private readonly Huffman?[] _acTables = new Huffman[4];
    private Component[] _components = [];
    private int _width, _height, _hMax, _vMax, _mcusX, _mcusY, _restartInterval, _eobRun;
    private bool _progressive, _jfif;
    private int _adobeTransform = -1;

    // Entropy-coded data: bits left-aligned in _bits; a marker ends the data and is read as zero bits.
    private int _at;
    private uint _bits;
    private int _bitCount;
    private bool _atMarker;

    private JpegDecoder(byte[] data) => _data = data;

    public static bool Is(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static RgbaImage Decode(ReadOnlySpan<byte> data)
    {
        if (!Is(data))
        {
            throw new InvalidDataException("not a JPEG file");
        }

        return new JpegDecoder(data.ToArray()).Run();
    }

    private RgbaImage Run()
    {
        _at = 2;
        bool frame = false;
        while (true)
        {
            int marker = NextMarker();
            if (marker == 0xD9)
            {
                break;
            }

            if (marker < 0)
            {
                // The scans read zeros past the end, which would give a picture grey where the file stops.
                throw new InvalidDataException("the JPEG file is cut short (it has no end marker)");
            }

            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_at));
            int start = _at + 2, end = _at + length;
            if (length < 2 || end > _data.Length)
            {
                throw new InvalidDataException("a JPEG segment runs past the end of the file");
            }

            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    ReadFrame(start, marker == 0xC2);
                    frame = true;
                    break;
                case 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF):
                    throw new InvalidDataException(marker >= 0xC9 ? "arithmetic-coded JPEG is not supported" : "lossless and hierarchical JPEG is not supported");
                case 0xC4:
                    ReadHuffmanTables(start, end);
                    break;
                case 0xDB:
                    ReadQuantTables(start, end);
                    break;
                case 0xDD:
                    _restartInterval = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(start));
                    break;
                case 0xE0:
                    _jfif |= length >= 7 && _data.AsSpan(start, 5).SequenceEqual("JFIF\0"u8);
                    break;
                case 0xEE:
                    if (length >= 14 && _data.AsSpan(start, 5).SequenceEqual("Adobe"u8))
                    {
                        _adobeTransform = _data[start + 11];
                    }

                    break;
                case 0xDA:
                    if (!frame)
                    {
                        throw new InvalidDataException("the JPEG scan comes before its frame header");
                    }

                    _at = end;
                    ReadScan(start);
                    continue;
            }

            _at = end;
        }

        return frame ? Output() : throw new InvalidDataException("the JPEG file has no frame");
    }

    /// <summary>The next marker code after <see cref="_at"/> (moving past it and its fill bytes), or -1 at the end.</summary>
    private int NextMarker()
    {
        while (_at + 1 < _data.Length)
        {
            if (_data[_at] == 0xFF && _data[_at + 1] is not 0x00 and not 0xFF)
            {
                _at += 2;
                return _data[_at - 1];
            }

            _at++;
        }

        return -1;
    }

    private void ReadFrame(int at, bool progressive)
    {
        if (_data[at] != 8)
        {
            throw new InvalidDataException($"{_data[at]}-bit JPEG is not supported");
        }

        _progressive = progressive;
        _height = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at + 1));
        _width = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at + 3));
        int count = _data[at + 5];
        if (_width == 0 || _height == 0 || (long)_width * _height > RgbaImage.MaxPixels)
        {
            throw new InvalidDataException($"a {_width}x{_height} JPEG is not supported");
        }

        if (count is not (1 or 3))
        {
            throw new InvalidDataException(count == 4 ? "CMYK JPEG is not supported: save it as RGB" : $"JPEG with {count} components is not supported");
        }

        _components = new Component[count];
        for (int i = 0; i < count; i++)
        {
            int c = at + 6 + i * 3;
            int h = _data[c + 1] >> 4, v = _data[c + 1] & 15;
            if (h is < 1 or > 4 || v is < 1 or > 4 || _data[c + 2] > 3)
            {
                throw new InvalidDataException("a JPEG component header is broken");
            }

            _components[i] = new Component { Id = _data[c], H = h, V = v, QuantTable = _data[c + 2] };
        }

        _hMax = _components.Max(c => c.H);
        _vMax = _components.Max(c => c.V);
        _mcusX = (_width + 8 * _hMax - 1) / (8 * _hMax);
        _mcusY = (_height + 8 * _vMax - 1) / (8 * _vMax);
        foreach (Component c in _components)
        {
            c.Width = (_width * c.H + _hMax - 1) / _hMax;
            c.Height = (_height * c.V + _vMax - 1) / _vMax;
            c.BlocksX = _mcusX * c.H;
            c.BlocksY = _mcusY * c.V;
            c.Coefficients = new short[c.BlocksX * c.BlocksY * 64];
        }
    }

    private void ReadHuffmanTables(int at, int end)
    {
        while (at + 17 <= end)
        {
            int kind = _data[at] >> 4, id = _data[at] & 15;
            ReadOnlySpan<byte> counts = _data.AsSpan(at + 1, 16);
            int total = 0;
            foreach (byte n in counts)
            {
                total += n;
            }

            if (id > 3 || at + 17 + total > end)
            {
                throw new InvalidDataException("a JPEG Huffman table is broken");
            }

            var table = new Huffman(counts, _data.AsSpan(at + 17, total));
            (kind == 0 ? _dcTables : _acTables)[id] = table;
            at += 17 + total;
        }
    }

    private void ReadQuantTables(int at, int end)
    {
        while (at < end)
        {
            bool wide = _data[at] >> 4 != 0;
            int id = _data[at] & 15;
            if (id > 3 || at + 1 + (wide ? 128 : 64) > end)
            {
                throw new InvalidDataException("a JPEG quantisation table is broken");
            }

            var table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                table[Zigzag[i]] = wide ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at + 1 + i * 2)) : _data[at + 1 + i];
            }

            _quant[id] = table;
            at += 1 + (wide ? 128 : 64);
        }
    }

    private void ReadScan(int at)
    {
        int count = _data[at];
        var scan = new Component[count];
        for (int i = 0; i < count; i++)
        {
            int id = _data[at + 1 + i * 2], tables = _data[at + 2 + i * 2];
            Component c = _components.FirstOrDefault(k => k.Id == id) ?? throw new InvalidDataException("a JPEG scan names a missing component");
            c.Dc = _dcTables[(tables >> 4) & 3];
            c.Ac = _acTables[tables & 3];
            scan[i] = c;
        }

        int p = at + 1 + count * 2;
        int start = _data[p], end = _data[p + 1], high = _data[p + 2] >> 4, low = _data[p + 2] & 15;
        if (!_progressive)
        {
            (start, end, high, low) = (0, 63, 0, 0);
        }

        // Progressive DC scans hold no AC coefficients, and AC scans hold one component.
        if (start > end || end > 63 || (_progressive && (start == 0 ? end != 0 : count != 1)))
        {
            throw new InvalidDataException("a JPEG scan's spectral range is broken");
        }

        BlockDecoder decode = !_progressive ? DecodeBaseline
            : start == 0 ? (high == 0 ? DecodeDcFirst : DecodeDcRefine)
            : high == 0 ? DecodeAcFirst : DecodeAcRefine;
        foreach (Component c in scan)
        {
            c.Prediction = 0;
            bool needsDc = !_progressive || (start == 0 && high == 0), needsAc = !_progressive || start > 0;
            if ((needsDc && c.Dc is null) || (needsAc && c.Ac is null))
            {
                throw new InvalidDataException("a JPEG scan uses a missing Huffman table");
            }
        }

        _eobRun = 0;
        ResetBits();
        int mcus = count == 1 ? ((scan[0].Width + 7) / 8) * ((scan[0].Height + 7) / 8) : _mcusX * _mcusY;
        for (int m = 0; m < mcus; m++)
        {
            if (_restartInterval > 0 && m > 0 && m % _restartInterval == 0)
            {
                Restart(scan);
            }

            if (count == 1)
            {
                // A scan of one component covers its own blocks only, not the frame's padded MCUs.
                Component c = scan[0];
                int across = (c.Width + 7) / 8;
                decode(c, (m / across * c.BlocksX + m % across) * 64, start, end, low);
                continue;
            }

            int mx = m % _mcusX, my = m / _mcusX;
            foreach (Component c in scan)
            {
                for (int v = 0; v < c.V; v++)
                {
                    for (int h = 0; h < c.H; h++)
                    {
                        decode(c, ((my * c.V + v) * c.BlocksX + mx * c.H + h) * 64, start, end, low);
                    }
                }
            }
        }

        // The scan's data ends at the next marker.
        while (_at + 1 < _data.Length && !(_data[_at] == 0xFF && _data[_at + 1] is not 0x00 and not (>= 0xD0 and <= 0xD7) and not 0xFF))
        {
            _at++;
        }
    }

    private delegate void BlockDecoder(Component c, int block, int start, int end, int low);

    private void Restart(Component[] scan)
    {
        ResetBits();
        while (_at + 1 < _data.Length && !(_data[_at] == 0xFF && _data[_at + 1] is >= 0xD0 and <= 0xD7))
        {
            _at++;
        }

        _at += 2;
        _eobRun = 0;
        foreach (Component c in scan)
        {
            c.Prediction = 0;
        }
    }

    private void DecodeBaseline(Component c, int block, int start, int end, int low)
    {
        short[] k = c.Coefficients;
        c.Prediction += Receive(c.Dc!.Decode(this));
        k[block] = (short)c.Prediction;
        for (int i = 1; i < 64;)
        {
            int rs = c.Ac!.Decode(this), s = rs & 15, r = rs >> 4;
            if (s == 0)
            {
                if (r < 15)
                {
                    break;
                }

                i += 16;
                continue;
            }

            i += r;
            if (i > 63)
            {
                break;
            }

            k[block + Zigzag[i]] = (short)Receive(s);
            i++;
        }
    }

    private void DecodeDcFirst(Component c, int block, int start, int end, int low)
    {
        c.Prediction += Receive(c.Dc!.Decode(this));
        c.Coefficients[block] = (short)(c.Prediction << low);
    }

    private void DecodeDcRefine(Component c, int block, int start, int end, int low)
    {
        if (Bits(1) != 0)
        {
            c.Coefficients[block] |= (short)(1 << low);
        }
    }

    private void DecodeAcFirst(Component c, int block, int start, int end, int low)
    {
        if (_eobRun > 0)
        {
            _eobRun--;
            return;
        }

        for (int i = start; i <= end;)
        {
            int rs = c.Ac!.Decode(this), s = rs & 15, r = rs >> 4;
            if (s == 0)
            {
                if (r < 15)
                {
                    _eobRun = (1 << r) - 1 + (r > 0 ? Bits(r) : 0);
                    break;
                }

                i += 16;
                continue;
            }

            i += r;
            if (i > end)
            {
                break;
            }

            c.Coefficients[block + Zigzag[i]] = (short)(Receive(s) * (1 << low));
            i++;
        }
    }

    /// <summary>A refinement scan of AC coefficients (libjpeg's decode_mcu_AC_refine): one more bit of each coefficient
    /// already known, and new coefficients of magnitude one placed among the zero ones.</summary>
    private void DecodeAcRefine(Component c, int block, int start, int end, int low)
    {
        short[] k = c.Coefficients;
        int plus = 1 << low, minus = -1 << low;
        int i = start;
        if (_eobRun == 0)
        {
            for (; i <= end; i++)
            {
                int rs = c.Ac!.Decode(this), s = rs & 15, r = rs >> 4;
                int value = 0;
                if (s != 0)
                {
                    value = Bits(1) != 0 ? plus : minus;
                }
                else if (r != 15)
                {
                    _eobRun = (1 << r) + (r > 0 ? Bits(r) : 0);
                    break;
                }

                for (; i <= end; i++)
                {
                    int z = block + Zigzag[i];
                    if (k[z] != 0)
                    {
                        Refine(k, z, plus, minus);
                    }
                    else if (--r < 0)
                    {
                        break;
                    }
                }

                if (value != 0 && i <= end)
                {
                    k[block + Zigzag[i]] = (short)value;
                }
            }
        }

        if (_eobRun > 0)
        {
            for (; i <= end; i++)
            {
                int z = block + Zigzag[i];
                if (k[z] != 0)
                {
                    Refine(k, z, plus, minus);
                }
            }

            _eobRun--;
        }
    }

    private void Refine(short[] k, int z, int plus, int minus)
    {
        if (Bits(1) != 0 && (k[z] & plus) == 0)
        {
            k[z] += (short)(k[z] >= 0 ? plus : minus);
        }
    }

    /// <summary>A value of <paramref name="bits"/> bits, the upper half positive and the lower half negative.</summary>
    private int Receive(int bits)
    {
        if (bits is 0 or > 16)
        {
            return bits == 0 ? 0 : throw new InvalidDataException("the JPEG data has a bad coefficient size");
        }

        int value = Bits(bits);
        return value < 1 << (bits - 1) ? value - (1 << bits) + 1 : value;
    }

    private void ResetBits()
    {
        _bits = 0;
        _bitCount = 0;
        _atMarker = false;
    }

    /// <summary>Tops the bit buffer up to more than 24 bits, undoing byte stuffing; past a marker it adds zeros.</summary>
    private void Fill()
    {
        while (_bitCount <= 24)
        {
            int b = 0;
            if (!_atMarker && _at < _data.Length)
            {
                b = _data[_at];
                if (b == 0xFF)
                {
                    int next = _at + 1 < _data.Length ? _data[_at + 1] : 0xD9;
                    if (next == 0)
                    {
                        _at += 2;
                    }
                    else
                    {
                        _atMarker = true;
                        b = 0;
                    }
                }
                else
                {
                    _at++;
                }
            }

            _bits |= (uint)b << (24 - _bitCount);
            _bitCount += 8;
        }
    }

    private int Peek(int n)
    {
        if (_bitCount < n)
        {
            Fill();
        }

        return (int)(_bits >> (32 - n));
    }

    private void Skip(int n)
    {
        _bits <<= n;
        _bitCount -= n;
    }

    private int Bits(int n)
    {
        int v = Peek(n);
        Skip(n);
        return v;
    }

    private RgbaImage Output()
    {
        Span<float> block = stackalloc float[64];
        foreach (Component c in _components)
        {
            ushort[] q = _quant[c.QuantTable] ?? throw new InvalidDataException("a JPEG component uses a missing quantisation table");
            c.Samples = new byte[c.BlocksX * 8 * c.BlocksY * 8];
            for (int by = 0; by < c.BlocksY; by++)
            {
                for (int bx = 0; bx < c.BlocksX; bx++)
                {
                    int o = (by * c.BlocksX + bx) * 64;
                    for (int i = 0; i < 64; i++)
                    {
                        block[i] = c.Coefficients[o + i] * q[i];
                    }

                    InverseDct(block, c.Samples, (by * 8 * c.BlocksX + bx) * 8, c.BlocksX * 8);
                }
            }
        }

        var image = new RgbaImage(_width, _height);
        byte[] px = image.Pixels;
        bool rgb = _components.Length == 3
            && (_adobeTransform == 0 || (_adobeTransform < 0 && !_jfif && _components[0].Id == 'R' && _components[1].Id == 'G' && _components[2].Id == 'B'));
        var rows = new byte[_components.Length][];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new byte[_width];
        }

        for (int y = 0; y < _height; y++)
        {
            for (int i = 0; i < _components.Length; i++)
            {
                Upsample(_components[i], y, rows[i]);
            }

            int o = y * _width * 4;
            for (int x = 0; x < _width; x++, o += 4)
            {
                if (_components.Length == 1)
                {
                    px[o] = px[o + 1] = px[o + 2] = rows[0][x];
                }
                else if (rgb)
                {
                    px[o] = rows[0][x];
                    px[o + 1] = rows[1][x];
                    px[o + 2] = rows[2][x];
                }
                else
                {
                    float luma = rows[0][x], cb = rows[1][x] - 128f, cr = rows[2][x] - 128f;
                    px[o] = Clamp(luma + 1.402f * cr);
                    px[o + 1] = Clamp(luma - 0.344136f * cb - 0.714136f * cr);
                    px[o + 2] = Clamp(luma + 1.772f * cb);
                }

                px[o + 3] = 255;
            }
        }

        return image;
    }

    /// <summary>One output row of a component: as stored at full size, else libjpeg's triangle filter for a factor of
    /// two (3/4 of the nearest sample, 1/4 of the next one), else the nearest sample.</summary>
    private void Upsample(Component c, int y, byte[] row)
    {
        int stride = c.BlocksX * 8;
        byte[] s = c.Samples;
        int fx = _hMax / c.H, fy = _vMax / c.V;
        bool fancyX = fx == 2 && _hMax % c.H == 0, fancyY = fy == 2 && _vMax % c.V == 0;
        int sy = fancyY ? y >> 1 : y * c.V / _vMax;
        int ny = Math.Clamp(fancyY ? ((y & 1) == 0 ? sy - 1 : sy + 1) : sy, 0, c.Height - 1);
        sy = Math.Min(sy, c.Height - 1);
        int near = sy * stride, far = ny * stride;
        for (int x = 0; x < row.Length; x++)
        {
            int sx = fancyX ? x >> 1 : x * c.H / _hMax;
            sx = Math.Min(sx, c.Width - 1);
            if (!fancyX && !fancyY)
            {
                row[x] = s[near + sx];
                continue;
            }

            int nx = fancyX ? Math.Clamp((x & 1) == 0 ? sx - 1 : sx + 1, 0, c.Width - 1) : sx;
            if (fancyX && fancyY)
            {
                int here = 3 * s[near + sx] + s[far + sx], there = 3 * s[near + nx] + s[far + nx];
                row[x] = (byte)((3 * here + there + ((x & 1) == 0 ? 8 : 7)) >> 4);
            }
            else if (fancyX)
            {
                row[x] = (byte)((3 * s[near + sx] + s[near + nx] + ((x & 1) == 0 ? 1 : 2)) >> 2);
            }
            else
            {
                row[x] = (byte)((3 * s[near + sx] + s[far + sx] + ((y & 1) == 0 ? 1 : 2)) >> 2);
            }
        }
    }

    private static byte Clamp(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    /// <summary>cos((2x + 1) u pi / 16) scaled by C(u) / 2, at [u * 8 + x].</summary>
    private static float[] Cosines()
    {
        var c = new float[64];
        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < 8; x++)
            {
                c[u * 8 + x] = (float)((u == 0 ? 1 / Math.Sqrt(2) : 1) / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
            }
        }

        return c;
    }

    /// <summary>The 8x8 inverse DCT of dequantised coefficients (natural order), written as samples with the level shift.</summary>
    private static void InverseDct(Span<float> block, byte[] samples, int at, int stride)
    {
        bool dcOnly = true;
        for (int i = 1; i < 64 && dcOnly; i++)
        {
            dcOnly = block[i] == 0;
        }

        if (dcOnly)
        {
            byte v = Clamp(block[0] / 8 + 128);
            for (int y = 0; y < 8; y++)
            {
                samples.AsSpan(at + y * stride, 8).Fill(v);
            }

            return;
        }

        Span<float> rows = stackalloc float[64];
        for (int v = 0; v < 8; v++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    sum += s_cosines[u * 8 + x] * block[v * 8 + u];
                }

                rows[v * 8 + x] = sum;
            }
        }

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    sum += s_cosines[v * 8 + y] * rows[v * 8 + x];
                }

                samples[at + y * stride + x] = Clamp(sum + 128);
            }
        }
    }

    private sealed class Component
    {
        public int Id, H, V, QuantTable, Width, Height, BlocksX, BlocksY, Prediction;
        public short[] Coefficients = [];
        public byte[] Samples = [];
        public Huffman? Dc, Ac;
    }

    /// <summary>A Huffman table: codes of up to 8 bits from a lookup table, longer ones from the code ranges per length.</summary>
    private sealed class Huffman
    {
        private readonly ushort[] _fast = new ushort[256];
        private readonly int[] _maxCode = new int[18];
        private readonly int[] _offset = new int[17];
        private readonly byte[] _values;

        public Huffman(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
        {
            _values = values.ToArray();
            int code = 0, k = 0;
            for (int length = 1; length <= 16; length++)
            {
                _offset[length] = k - code;
                for (int i = 0; i < counts[length - 1]; i++, k++, code++)
                {
                    if (length <= 8)
                    {
                        int first = code << (8 - length);
                        for (int j = 0; j < 1 << (8 - length); j++)
                        {
                            _fast[first + j] = (ushort)(length << 8 | _values[k]);
                        }
                    }
                }

                _maxCode[length] = counts[length - 1] > 0 ? code - 1 : -1;
                code <<= 1;
            }

            _maxCode[17] = int.MaxValue;
        }

        public int Decode(JpegDecoder d)
        {
            int peek = d.Peek(16);
            ushort fast = _fast[peek >> 8];
            if (fast != 0)
            {
                d.Skip(fast >> 8);
                return fast & 0xFF;
            }

            for (int length = 9; length <= 16; length++)
            {
                int code = peek >> (16 - length);
                if (code <= _maxCode[length])
                {
                    d.Skip(length);
                    return _values[code + _offset[length]];
                }
            }

            throw new InvalidDataException("the JPEG data has a bad Huffman code");
        }
    }
}
