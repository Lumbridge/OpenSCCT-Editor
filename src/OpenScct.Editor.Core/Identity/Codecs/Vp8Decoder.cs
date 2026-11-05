using System.Buffers.Binary;

namespace OpenScct.Editor.Core.Identity.Codecs;

/// <summary>
/// VP8 key frames, the lossy picture of a WebP file (RFC 6386): the boolean entropy decoder, intra prediction, the
/// inverse transforms and both loop filters, then libwebp's "fancy" chroma upsampling to RGB, so a picture decodes as
/// libwebp decodes it (to within a step of rounding in the chroma at the edges). libwebp's licence is in
/// THIRD-PARTY-NOTICES.md.
/// </summary>
internal sealed class Vp8Decoder
{
    private const int Dc = 0, Tm = 1, Ve = 2, He = 3, Rd = 4, Vr = 5, Ld = 6, Vl = 7, Hd = 8, Hu = 9;

    /// <summary>The work buffer of one macroblock: rows of 32 bytes, the block at row 1 and column 8 so the row above,
    /// the column to the left and the four pixels above and to the right fit around it.</summary>
    private const int Bps = 32, Origin = Bps + 8;

    private static ReadOnlySpan<byte> Bands => [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];

    private static ReadOnlySpan<byte> Zigzag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    private static readonly byte[][] s_categories =
    [
        [173, 148, 140],
        [176, 155, 140, 135],
        [180, 157, 141, 134, 130],
        [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129],
    ];

    private readonly int _width, _height, _mbw, _mbh;
    private readonly byte[] _y, _u, _v;
    private readonly int _yStride, _uvStride;
    private readonly byte[] _probs = new byte[4 * 8 * 3 * 11];
    private readonly int[,] _y1 = new int[4, 2], _y2 = new int[4, 2], _uv = new int[4, 2];
    private readonly byte[] _segmentProbs = [255, 255, 255];
    private readonly int[] _segmentQuant = new int[4], _segmentFilter = new int[4];
    private readonly int[] _refDelta = new int[4], _modeDelta = new int[4];
    private bool _useSegment, _updateMap, _absoluteDelta, _simple, _useLfDelta, _useSkip;
    private int _filterLevel, _sharpness, _skipProb;

    private Vp8Decoder(int width, int height)
    {
        _width = width;
        _height = height;
        _mbw = (width + 15) / 16;
        _mbh = (height + 15) / 16;
        _yStride = _mbw * 16;
        _uvStride = _mbw * 8;
        _y = new byte[_yStride * _mbh * 16];
        _u = new byte[_uvStride * _mbh * 8];
        _v = new byte[_uvStride * _mbh * 8];
    }

    /// <summary>The size held in a VP8 chunk's frame header, or null when it is not a key frame.</summary>
    public static (int Width, int Height)? SizeOf(ReadOnlySpan<byte> chunk) =>
        chunk.Length >= 10 && (chunk[0] & 1) == 0 && chunk[3] == 0x9D && chunk[4] == 0x01 && chunk[5] == 0x2A
            ? (BinaryPrimitives.ReadUInt16LittleEndian(chunk[6..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(chunk[8..]) & 0x3FFF)
            : null;

    public static RgbaImage Decode(ReadOnlySpan<byte> chunk)
    {
        if (SizeOf(chunk) is not { } size || size.Width == 0 || size.Height == 0)
        {
            throw new InvalidDataException("the WebP picture is not a VP8 key frame");
        }

        if ((long)size.Width * size.Height > RgbaImage.MaxPixels)
        {
            throw new InvalidDataException($"a {size.Width}x{size.Height} WebP is not supported");
        }

        var d = new Vp8Decoder(size.Width, size.Height);
        d.DecodeFrame(chunk.ToArray());
        return d.ToRgba();
    }

    private void DecodeFrame(byte[] data)
    {
        int tag = data[0] | data[1] << 8 | data[2] << 16;
        int firstSize = tag >> 5;
        if (10 + firstSize > data.Length)
        {
            throw new InvalidDataException("the WebP picture is cut short");
        }

        var header = new BoolDecoder(data, 10, firstSize);
        ReadHeader(header);
        BoolDecoder[] partitions = Partitions(header, data, 10 + firstSize);
        ReadQuant(header);
        header.Bit(128); // refresh_entropy_probs: a single picture has no next frame
        ReadProbabilities(header);

        var mb = new Macroblock();
        var topModes = new byte[_mbw * 4];
        var leftModes = new byte[4];
        var topNz = new byte[_mbw * 9];
        var leftNz = new byte[9];
        var filters = new (int Limit, int Interior, int Hev, bool Inner)[_mbw * _mbh];
        var strengths = FilterStrengths();
        for (int mby = 0; mby < _mbh; mby++)
        {
            Array.Clear(leftModes);
            Array.Clear(leftNz);
            BoolDecoder tokens = partitions[mby & (partitions.Length - 1)];
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                ReadModes(header, mb, topModes.AsSpan(mbx * 4, 4), leftModes);
                Array.Clear(mb.Coeffs);
                bool any = false;
                if (!mb.Skip)
                {
                    any = ReadResiduals(tokens, mb, topNz.AsSpan(mbx * 9, 9), leftNz);
                }
                else
                {
                    topNz.AsSpan(mbx * 9, 8).Clear();
                    leftNz.AsSpan(0, 8).Clear();
                    if (!mb.IsI4)
                    {
                        topNz[mbx * 9 + 8] = leftNz[8] = 0;
                    }
                }

                Reconstruct(mb, mbx, mby);
                var s = strengths[mb.Segment, mb.IsI4 ? 1 : 0];
                filters[mby * _mbw + mbx] = (s.Limit, s.Interior, s.Hev, mb.IsI4 || any);
            }
        }

        if (_filterLevel > 0)
        {
            for (int mby = 0; mby < _mbh; mby++)
            {
                for (int mbx = 0; mbx < _mbw; mbx++)
                {
                    Filter(mbx, mby, filters[mby * _mbw + mbx]);
                }
            }
        }
    }

    private void ReadHeader(BoolDecoder br)
    {
        br.Bit(128); // colour space: the only one defined is YUV
        br.Bit(128); // clamping: the decoder always clamps
        _useSegment = br.Bit(128) == 1;
        if (_useSegment)
        {
            _updateMap = br.Bit(128) == 1;
            if (br.Bit(128) == 1)
            {
                _absoluteDelta = br.Bit(128) == 1;
                for (int s = 0; s < 4; s++)
                {
                    _segmentQuant[s] = br.Bit(128) == 1 ? br.Signed(7) : 0;
                }

                for (int s = 0; s < 4; s++)
                {
                    _segmentFilter[s] = br.Bit(128) == 1 ? br.Signed(6) : 0;
                }
            }

            if (_updateMap)
            {
                for (int s = 0; s < 3; s++)
                {
                    _segmentProbs[s] = (byte)(br.Bit(128) == 1 ? br.Literal(8) : 255);
                }
            }
        }

        _simple = br.Bit(128) == 1;
        _filterLevel = br.Literal(6);
        _sharpness = br.Literal(3);
        _useLfDelta = br.Bit(128) == 1;
        if (_useLfDelta && br.Bit(128) == 1)
        {
            for (int i = 0; i < 4; i++)
            {
                if (br.Bit(128) == 1)
                {
                    _refDelta[i] = br.Signed(6);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if (br.Bit(128) == 1)
                {
                    _modeDelta[i] = br.Signed(6);
                }
            }
        }
    }

    private static BoolDecoder[] Partitions(BoolDecoder br, byte[] data, int at)
    {
        int count = 1 << br.Literal(2);
        int sizes = at, start = at + 3 * (count - 1);
        if (start > data.Length)
        {
            throw new InvalidDataException("the WebP picture's partitions are cut short");
        }

        var parts = new BoolDecoder[count];
        for (int p = 0; p < count; p++)
        {
            int size = p < count - 1 ? data[sizes + p * 3] | data[sizes + p * 3 + 1] << 8 | data[sizes + p * 3 + 2] << 16 : data.Length - start;
            size = Math.Clamp(size, 0, data.Length - start);
            parts[p] = new BoolDecoder(data, start, size);
            start += size;
        }

        return parts;
    }

    private void ReadQuant(BoolDecoder br)
    {
        int baseQ = br.Literal(7);
        int Delta() => br.Bit(128) == 1 ? br.Signed(4) : 0;
        int y1Dc = Delta(), y2Dc = Delta(), y2Ac = Delta(), uvDc = Delta(), uvAc = Delta();
        for (int s = 0; s < 4; s++)
        {
            int q = !_useSegment ? baseQ : _absoluteDelta ? _segmentQuant[s] : _segmentQuant[s] + baseQ;
            static int At(int q, int max) => Math.Clamp(q, 0, max);
            _y1[s, 0] = Vp8Tables.DcQuant[At(q + y1Dc, 127)];
            _y1[s, 1] = Vp8Tables.AcQuant[At(q, 127)];
            _y2[s, 0] = Vp8Tables.DcQuant[At(q + y2Dc, 127)] * 2;
            // AC of the second-order block is scaled by 155/100, at least 8 (RFC 6386 14.1).
            _y2[s, 1] = Math.Max(8, Vp8Tables.AcQuant[At(q + y2Ac, 127)] * 101581 >> 16);
            _uv[s, 0] = Vp8Tables.DcQuant[At(q + uvDc, 117)];
            _uv[s, 1] = Vp8Tables.AcQuant[At(q + uvAc, 127)];
        }
    }

    private void ReadProbabilities(BoolDecoder br)
    {
        for (int i = 0; i < _probs.Length; i++)
        {
            _probs[i] = br.Bit(Vp8Tables.CoeffUpdateProbs[i]) == 1 ? (byte)br.Literal(8) : Vp8Tables.CoeffProbs[i];
        }

        _useSkip = br.Bit(128) == 1;
        _skipProb = _useSkip ? br.Literal(8) : 0;
    }

    private void ReadModes(BoolDecoder br, Macroblock mb, Span<byte> top, Span<byte> left)
    {
        mb.Segment = !_updateMap ? 0 : br.Bit(_segmentProbs[0]) == 0 ? br.Bit(_segmentProbs[1]) : 2 + br.Bit(_segmentProbs[2]);
        mb.Skip = _useSkip && br.Bit(_skipProb) == 1;
        mb.IsI4 = br.Bit(145) == 0;
        if (!mb.IsI4)
        {
            int mode = br.Bit(156) == 1 ? (br.Bit(128) == 1 ? Tm : He) : (br.Bit(163) == 1 ? Ve : Dc);
            mb.YMode = mode;
            top.Fill((byte)mode);
            left.Fill((byte)mode);
        }
        else
        {
            for (int y = 0; y < 4; y++)
            {
                int mode = left[y];
                for (int x = 0; x < 4; x++)
                {
                    ReadOnlySpan<byte> p = Vp8Tables.SubblockModeProbs.Slice((top[x] * 10 + mode) * 9, 9);
                    mode = br.Bit(p[0]) == 0 ? Dc
                        : br.Bit(p[1]) == 0 ? Tm
                        : br.Bit(p[2]) == 0 ? Ve
                        : br.Bit(p[3]) == 0 ? (br.Bit(p[4]) == 0 ? He : br.Bit(p[5]) == 0 ? Rd : Vr)
                        : br.Bit(p[6]) == 0 ? Ld
                        : br.Bit(p[7]) == 0 ? Vl
                        : br.Bit(p[8]) == 0 ? Hd : Hu;
                    top[x] = (byte)mode;
                    mb.Modes[y * 4 + x] = (byte)mode;
                }

                left[y] = (byte)mode;
            }
        }

        mb.UvMode = br.Bit(142) == 0 ? Dc : br.Bit(114) == 0 ? Ve : br.Bit(183) == 1 ? Tm : He;
    }

    /// <summary>The macroblock's coefficients, dequantised; true when a block has coefficients (libwebp's test: past the
    /// first, or a first that is not zero), which turns on the inner loop filter. The non-zero flags of the blocks above
    /// and to the left are the token contexts: 4 luma, 2 + 2 chroma and the second-order block.</summary>
    private bool ReadResiduals(BoolDecoder br, Macroblock mb, Span<byte> top, Span<byte> left)
    {
        short[] c = mb.Coeffs;
        int s = mb.Segment;
        int first, type;
        if (!mb.IsI4)
        {
            Span<short> dc = stackalloc short[16];
            int nz = ReadCoeffs(br, 1, top[8] + left[8], _y2[s, 0], _y2[s, 1], 0, dc);
            top[8] = left[8] = (byte)(nz > 0 ? 1 : 0);
            if (nz > 1)
            {
                InverseWht(dc, c);
            }
            else
            {
                short dc0 = (short)((dc[0] + 3) >> 3);
                for (int i = 0; i < 16; i++)
                {
                    c[i * 16] = dc0;
                }
            }

            first = 1;
            type = 0;
        }
        else
        {
            first = 0;
            type = 3;
        }

        bool any = false;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int at = (y * 4 + x) * 16;
                int nz = ReadCoeffs(br, type, top[x] + left[y], _y1[s, 0], _y1[s, 1], first, c.AsSpan(at, 16));
                top[x] = left[y] = (byte)(nz > first ? 1 : 0);
                any |= nz > 1 || c[at] != 0;
            }
        }

        for (int ch = 4; ch < 8; ch += 2)
        {
            int at = ch == 4 ? 256 : 320;
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    int block = at + (y * 2 + x) * 16;
                    int nz = ReadCoeffs(br, 2, top[ch + x] + left[ch + y], _uv[s, 0], _uv[s, 1], 0, c.AsSpan(block, 16));
                    top[ch + x] = left[ch + y] = (byte)(nz > 0 ? 1 : 0);
                    any |= nz > 1 || c[block] != 0;
                }
            }
        }

        return any;
    }

    /// <summary>One block's tokens from coefficient <paramref name="n"/> on; returns the index past the last one read
    /// (0 when the block ends at once).</summary>
    private int ReadCoeffs(BoolDecoder br, int type, int context, int dc, int ac, int n, Span<short> output)
    {
        int p = Probs(type, Bands[n], context);
        for (; n < 16; n++)
        {
            if (br.Bit(_probs[p]) == 0)
            {
                return n; // end of block
            }

            while (br.Bit(_probs[p + 1]) == 0)
            {
                // A zero; the next token cannot be the end of the block.
                p = Probs(type, Bands[++n], 0);
                if (n == 16)
                {
                    return 16;
                }
            }

            int v;
            if (br.Bit(_probs[p + 2]) == 0)
            {
                v = 1;
                p = Probs(type, Bands[n + 1], 1);
            }
            else
            {
                v = LargeValue(br, p);
                p = Probs(type, Bands[n + 1], 2);
            }

            output[Zigzag[n]] = (short)((br.Bit(128) == 1 ? -v : v) * (n > 0 ? ac : dc));
        }

        return 16;
    }

    private static int Probs(int type, int band, int context) => ((type * 8 + band) * 3 + context) * 11;

    private int LargeValue(BoolDecoder br, int p)
    {
        if (br.Bit(_probs[p + 3]) == 0)
        {
            return br.Bit(_probs[p + 4]) == 0 ? 2 : 3 + br.Bit(_probs[p + 5]);
        }

        if (br.Bit(_probs[p + 6]) == 0)
        {
            return br.Bit(_probs[p + 7]) == 0 ? 5 + br.Bit(159) : 7 + 2 * br.Bit(165) + br.Bit(145);
        }

        int bit1 = br.Bit(_probs[p + 8]);
        int bit0 = br.Bit(_probs[p + 9 + bit1]);
        int category = 2 * bit1 + bit0;
        int v = 0;
        foreach (byte prob in s_categories[category])
        {
            v += v + br.Bit(prob);
        }

        return v + 3 + (8 << category);
    }

    private static void InverseWht(ReadOnlySpan<short> input, short[] output)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[i] + input[12 + i], a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i], a3 = input[i] - input[12 + i];
            t[i] = a0 + a1;
            t[8 + i] = a0 - a1;
            t[4 + i] = a3 + a2;
            t[12 + i] = a3 - a2;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = t[i * 4] + 3;
            int a0 = dc + t[i * 4 + 3], a1 = t[i * 4 + 1] + t[i * 4 + 2];
            int a2 = t[i * 4 + 1] - t[i * 4 + 2], a3 = dc - t[i * 4 + 3];
            output[(i * 4) * 16] = (short)((a0 + a1) >> 3);
            output[(i * 4 + 1) * 16] = (short)((a3 + a2) >> 3);
            output[(i * 4 + 2) * 16] = (short)((a0 - a1) >> 3);
            output[(i * 4 + 3) * 16] = (short)((a3 - a2) >> 3);
        }
    }

    private static int Mul1(int a) => ((a * 20091) >> 16) + a;

    private static int Mul2(int a) => (a * 35468) >> 16;

    /// <summary>Adds the inverse DCT of a 4x4 block to the prediction in the work buffer.</summary>
    private static void AddTransform(ReadOnlySpan<short> c, byte[] dst, int at)
    {
        bool any = false;
        foreach (short v in c)
        {
            any |= v != 0;
        }

        if (!any)
        {
            return;
        }

        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = c[i] + c[8 + i], b = c[i] - c[8 + i];
            int cc = Mul2(c[4 + i]) - Mul1(c[12 + i]), dd = Mul1(c[4 + i]) + Mul2(c[12 + i]);
            t[i * 4] = a + dd;
            t[i * 4 + 1] = b + cc;
            t[i * 4 + 2] = b - cc;
            t[i * 4 + 3] = a - dd;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = t[i] + 4;
            int a = dc + t[8 + i], b = dc - t[8 + i];
            int cc = Mul2(t[4 + i]) - Mul1(t[12 + i]), dd = Mul1(t[4 + i]) + Mul2(t[12 + i]);
            int o = at + i * Bps;
            dst[o] = Clip(dst[o] + ((a + dd) >> 3));
            dst[o + 1] = Clip(dst[o + 1] + ((b + cc) >> 3));
            dst[o + 2] = Clip(dst[o + 2] + ((b - cc) >> 3));
            dst[o + 3] = Clip(dst[o + 3] + ((a - dd) >> 3));
        }
    }

    private static byte Clip(int v) => (byte)Math.Clamp(v, 0, 255);

    private readonly byte[] _yb = new byte[Bps * 17], _ub = new byte[Bps * 9], _vb = new byte[Bps * 9];

    /// <summary>Predicts the macroblock from its unfiltered neighbours (the loop filter runs once the frame is done, and
    /// prediction never sees its output), adds the residuals and stores it in the planes.</summary>
    private void Reconstruct(Macroblock mb, int mbx, int mby)
    {
        Load(_yb, _y, _yStride, mbx, mby, 16);
        Load(_ub, _u, _uvStride, mbx, mby, 8);
        Load(_vb, _v, _uvStride, mbx, mby, 8);
        if (mb.IsI4)
        {
            // Above-right of the right column's lower blocks is the macroblock's own above-right.
            for (int r = 3; r < 16; r += 4)
            {
                Array.Copy(_yb, Origin - Bps + 16, _yb, Origin + r * Bps + 16, 4);
            }

            for (int n = 0; n < 16; n++)
            {
                int at = Origin + (n >> 2) * 4 * Bps + (n & 3) * 4;
                Predict4(_yb, at, mb.Modes[n]);
                AddTransform(mb.Coeffs.AsSpan(n * 16, 16), _yb, at);
            }
        }
        else
        {
            PredictBlock(_yb, 16, mb.YMode, mbx, mby);
            for (int n = 0; n < 16; n++)
            {
                AddTransform(mb.Coeffs.AsSpan(n * 16, 16), _yb, Origin + (n >> 2) * 4 * Bps + (n & 3) * 4);
            }
        }

        PredictBlock(_ub, 8, mb.UvMode, mbx, mby);
        PredictBlock(_vb, 8, mb.UvMode, mbx, mby);
        for (int n = 0; n < 4; n++)
        {
            int at = Origin + (n >> 1) * 4 * Bps + (n & 1) * 4;
            AddTransform(mb.Coeffs.AsSpan(256 + n * 16, 16), _ub, at);
            AddTransform(mb.Coeffs.AsSpan(320 + n * 16, 16), _vb, at);
        }

        Store(_yb, _y, _yStride, mbx, mby, 16);
        Store(_ub, _u, _uvStride, mbx, mby, 8);
        Store(_vb, _v, _uvStride, mbx, mby, 8);
    }

    /// <summary>Fills the work buffer's edges as libwebp does: 127 above the top row (and above-left of it), 129 left
    /// of the left column, else the neighbouring pixels; above-right of the last column repeats the last pixel above.</summary>
    private void Load(byte[] b, byte[] plane, int stride, int mbx, int mby, int size)
    {
        int x0 = mbx * size, y0 = mby * size;
        bool lastColumn = mbx == _mbw - 1;
        for (int r = 0; r < size; r++)
        {
            b[Origin + r * Bps - 1] = mbx > 0 ? plane[(y0 + r) * stride + x0 - 1] : (byte)129;
        }

        int above = Origin - Bps;
        if (mby == 0)
        {
            b.AsSpan(above - 1, size + 5).Fill(127);
            return;
        }

        int row = (y0 - 1) * stride + x0;
        b[above - 1] = mbx > 0 ? plane[row - 1] : (byte)129;
        Array.Copy(plane, row, b, above, size);
        for (int i = 0; i < 4 && size == 16; i++)
        {
            b[above + 16 + i] = lastColumn ? plane[row + 15] : plane[row + 16 + i];
        }
    }

    private static void Store(byte[] b, byte[] plane, int stride, int mbx, int mby, int size)
    {
        for (int r = 0; r < size; r++)
        {
            Array.Copy(b, Origin + r * Bps, plane, (mby * size + r) * stride + mbx * size, size);
        }
    }

    /// <summary>16x16 luma or 8x8 chroma prediction; DC uses only the edges that exist.</summary>
    private static void PredictBlock(byte[] b, int size, int mode, int mbx, int mby)
    {
        int above = Origin - Bps;
        switch (mode)
        {
            case Dc:
                int sum = 0, count = 0;
                if (mby > 0)
                {
                    for (int i = 0; i < size; i++)
                    {
                        sum += b[above + i];
                    }

                    count += size;
                }

                if (mbx > 0)
                {
                    for (int i = 0; i < size; i++)
                    {
                        sum += b[Origin + i * Bps - 1];
                    }

                    count += size;
                }

                byte dc = count == 0 ? (byte)128 : (byte)((sum + count / 2) / count);
                for (int r = 0; r < size; r++)
                {
                    b.AsSpan(Origin + r * Bps, size).Fill(dc);
                }

                break;
            case Tm:
                for (int r = 0; r < size; r++)
                {
                    int left = b[Origin + r * Bps - 1] - b[above - 1];
                    for (int i = 0; i < size; i++)
                    {
                        b[Origin + r * Bps + i] = Clip(b[above + i] + left);
                    }
                }

                break;
            case Ve:
                for (int r = 0; r < size; r++)
                {
                    Array.Copy(b, above, b, Origin + r * Bps, size);
                }

                break;
            default:
                for (int r = 0; r < size; r++)
                {
                    b.AsSpan(Origin + r * Bps, size).Fill(b[Origin + r * Bps - 1]);
                }

                break;
        }
    }

    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    private static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);

    /// <summary>The ten 4x4 predictions (RFC 6386 12.3), from the pixels above (A..H, with above-right), to the left
    /// (I..L) and above-left (X).</summary>
    private static void Predict4(byte[] b, int at, int mode)
    {
        int top = at - Bps;
        int x = b[top - 1], a = b[top], bb = b[top + 1], c = b[top + 2], d = b[top + 3];
        int e = b[top + 4], f = b[top + 5], g = b[top + 6], h = b[top + 7];
        int i = b[at - 1], j = b[at + Bps - 1], k = b[at + 2 * Bps - 1], l = b[at + 3 * Bps - 1];
        void Set(int col, int row, byte v) => b[at + row * Bps + col] = v;
        switch (mode)
        {
            case Dc:
                byte dc = (byte)((a + bb + c + d + i + j + k + l + 4) >> 3);
                for (int r = 0; r < 4; r++)
                {
                    b.AsSpan(at + r * Bps, 4).Fill(dc);
                }

                break;
            case Tm:
                for (int r = 0; r < 4; r++)
                {
                    int left = b[at + r * Bps - 1] - x;
                    for (int col = 0; col < 4; col++)
                    {
                        Set(col, r, Clip(b[top + col] + left));
                    }
                }

                break;
            case Ve:
                byte v0 = Avg3(x, a, bb), v1 = Avg3(a, bb, c), v2 = Avg3(bb, c, d), v3 = Avg3(c, d, e);
                for (int r = 0; r < 4; r++)
                {
                    Set(0, r, v0);
                    Set(1, r, v1);
                    Set(2, r, v2);
                    Set(3, r, v3);
                }

                break;
            case He:
                byte h0 = Avg3(x, i, j), h1 = Avg3(i, j, k), h2 = Avg3(j, k, l), h3 = Avg3(k, l, l);
                b.AsSpan(at, 4).Fill(h0);
                b.AsSpan(at + Bps, 4).Fill(h1);
                b.AsSpan(at + 2 * Bps, 4).Fill(h2);
                b.AsSpan(at + 3 * Bps, 4).Fill(h3);
                break;
            case Rd:
                Set(0, 3, Avg3(j, k, l));
                Set(1, 3, Set2(0, 2, Avg3(i, j, k)));
                Set(2, 3, Set2(1, 2, Set2(0, 1, Avg3(x, i, j))));
                Set(3, 3, Set2(2, 2, Set2(1, 1, Set2(0, 0, Avg3(a, x, i)))));
                Set(3, 2, Set2(2, 1, Set2(1, 0, Avg3(bb, a, x))));
                Set(3, 1, Set2(2, 0, Avg3(c, bb, a)));
                Set(3, 0, Avg3(d, c, bb));
                break;
            case Vr:
                Set(0, 0, Set2(1, 2, Avg2(x, a)));
                Set(1, 0, Set2(2, 2, Avg2(a, bb)));
                Set(2, 0, Set2(3, 2, Avg2(bb, c)));
                Set(3, 0, Avg2(c, d));
                Set(0, 3, Avg3(k, j, i));
                Set(0, 2, Avg3(j, i, x));
                Set(0, 1, Set2(1, 3, Avg3(i, x, a)));
                Set(1, 1, Set2(2, 3, Avg3(x, a, bb)));
                Set(2, 1, Set2(3, 3, Avg3(a, bb, c)));
                Set(3, 1, Avg3(bb, c, d));
                break;
            case Ld:
                Set(0, 0, Avg3(a, bb, c));
                Set(1, 0, Set2(0, 1, Avg3(bb, c, d)));
                Set(2, 0, Set2(1, 1, Set2(0, 2, Avg3(c, d, e))));
                Set(3, 0, Set2(2, 1, Set2(1, 2, Set2(0, 3, Avg3(d, e, f)))));
                Set(3, 1, Set2(2, 2, Set2(1, 3, Avg3(e, f, g))));
                Set(3, 2, Set2(2, 3, Avg3(f, g, h)));
                Set(3, 3, Avg3(g, h, h));
                break;
            case Vl:
                Set(0, 0, Avg2(a, bb));
                Set(1, 0, Set2(0, 2, Avg2(bb, c)));
                Set(2, 0, Set2(1, 2, Avg2(c, d)));
                Set(3, 0, Set2(2, 2, Avg2(d, e)));
                Set(0, 1, Avg3(a, bb, c));
                Set(1, 1, Set2(0, 3, Avg3(bb, c, d)));
                Set(2, 1, Set2(1, 3, Avg3(c, d, e)));
                Set(3, 1, Set2(2, 3, Avg3(d, e, f)));
                Set(3, 2, Avg3(e, f, g));
                Set(3, 3, Avg3(f, g, h));
                break;
            case Hd:
                Set(0, 0, Set2(2, 1, Avg2(i, x)));
                Set(0, 1, Set2(2, 2, Avg2(j, i)));
                Set(0, 2, Set2(2, 3, Avg2(k, j)));
                Set(0, 3, Avg2(l, k));
                Set(3, 0, Avg3(a, bb, c));
                Set(2, 0, Avg3(x, a, bb));
                Set(1, 0, Set2(3, 1, Avg3(i, x, a)));
                Set(1, 1, Set2(3, 2, Avg3(j, i, x)));
                Set(1, 2, Set2(3, 3, Avg3(k, j, i)));
                Set(1, 3, Avg3(l, k, j));
                break;
            default:
                Set(0, 0, Avg2(i, j));
                Set(2, 0, Set2(0, 1, Avg2(j, k)));
                Set(2, 1, Set2(0, 2, Avg2(k, l)));
                Set(1, 0, Avg3(i, j, k));
                Set(3, 0, Set2(1, 1, Avg3(j, k, l)));
                Set(3, 1, Set2(1, 2, Avg3(k, l, l)));
                Set(3, 2, Set2(2, 2, (byte)l));
                for (int col = 0; col < 4; col++)
                {
                    Set(col, 3, (byte)l);
                }

                break;
        }

        // Writes one pixel and passes its value on, for the pixels that share a prediction.
        byte Set2(int col, int row, byte v)
        {
            b[at + row * Bps + col] = v;
            return v;
        }
    }

    /// <summary>Per segment and per macroblock kind (16x16 or 4x4), the edge limit, interior limit and high edge
    /// variance threshold of the loop filter (libwebp's PrecomputeFilterStrengths).</summary>
    private (int Limit, int Interior, int Hev)[,] FilterStrengths()
    {
        var strengths = new (int, int, int)[4, 2];
        for (int s = 0; s < 4; s++)
        {
            int baseLevel = !_useSegment ? _filterLevel : _absoluteDelta ? _segmentFilter[s] : _segmentFilter[s] + _filterLevel;
            for (int i4 = 0; i4 < 2; i4++)
            {
                int level = baseLevel;
                if (_useLfDelta)
                {
                    level += _refDelta[0] + (i4 == 1 ? _modeDelta[0] : 0);
                }

                level = Math.Clamp(level, 0, 63);
                if (level == 0)
                {
                    continue;
                }

                int interior = level;
                if (_sharpness > 0)
                {
                    interior >>= _sharpness > 4 ? 2 : 1;
                    interior = Math.Min(interior, 9 - _sharpness);
                }

                interior = Math.Max(interior, 1);
                strengths[s, i4] = (2 * level + interior, interior, level >= 40 ? 2 : level >= 15 ? 1 : 0);
            }
        }

        return strengths;
    }

    /// <summary>The loop filter of one macroblock: its left edge, inner vertical edges, top edge, inner horizontal edges.</summary>
    private void Filter(int mbx, int mby, (int Limit, int Interior, int Hev, bool Inner) f)
    {
        if (f.Limit == 0)
        {
            return;
        }

        int y = mby * 16 * _yStride + mbx * 16, uv = mby * 8 * _uvStride + mbx * 8;
        if (_simple)
        {
            if (mbx > 0)
            {
                SimpleEdge(_y, y, 1, _yStride, f.Limit + 4);
            }

            for (int k = 4; f.Inner && k < 16; k += 4)
            {
                SimpleEdge(_y, y + k, 1, _yStride, f.Limit);
            }

            if (mby > 0)
            {
                SimpleEdge(_y, y, _yStride, 1, f.Limit + 4);
            }

            for (int k = 4; f.Inner && k < 16; k += 4)
            {
                SimpleEdge(_y, y + k * _yStride, _yStride, 1, f.Limit);
            }

            return;
        }

        if (mbx > 0)
        {
            MacroblockEdge(_y, y, 1, _yStride, 16, f);
            MacroblockEdge(_u, uv, 1, _uvStride, 8, f);
            MacroblockEdge(_v, uv, 1, _uvStride, 8, f);
        }

        if (f.Inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                InnerEdge(_y, y + k, 1, _yStride, 16, f);
            }

            InnerEdge(_u, uv + 4, 1, _uvStride, 8, f);
            InnerEdge(_v, uv + 4, 1, _uvStride, 8, f);
        }

        if (mby > 0)
        {
            MacroblockEdge(_y, y, _yStride, 1, 16, f);
            MacroblockEdge(_u, uv, _uvStride, 1, 8, f);
            MacroblockEdge(_v, uv, _uvStride, 1, 8, f);
        }

        if (f.Inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                InnerEdge(_y, y + k * _yStride, _yStride, 1, 16, f);
            }

            InnerEdge(_u, uv + 4 * _uvStride, _uvStride, 1, 8, f);
            InnerEdge(_v, uv + 4 * _uvStride, _uvStride, 1, 8, f);
        }
    }

    // In the filters, step crosses the edge and along moves along it; p[o - step] and p[o] are the pixels either side.
    private static void SimpleEdge(byte[] p, int o, int step, int along, int limit)
    {
        for (int i = 0; i < 16; i++, o += along)
        {
            if (Needs(p, o, step, 2 * limit + 1))
            {
                Filter2(p, o, step);
            }
        }
    }

    private static void MacroblockEdge(byte[] p, int o, int step, int along, int count, (int Limit, int Interior, int Hev, bool Inner) f)
    {
        for (int i = 0; i < count; i++, o += along)
        {
            if (NeedsNormal(p, o, step, 2 * (f.Limit + 4) + 1, f.Interior))
            {
                if (HighVariance(p, o, step, f.Hev))
                {
                    Filter2(p, o, step);
                }
                else
                {
                    Filter6(p, o, step);
                }
            }
        }
    }

    private static void InnerEdge(byte[] p, int o, int step, int along, int count, (int Limit, int Interior, int Hev, bool Inner) f)
    {
        for (int i = 0; i < count; i++, o += along)
        {
            if (NeedsNormal(p, o, step, 2 * f.Limit + 1, f.Interior))
            {
                if (HighVariance(p, o, step, f.Hev))
                {
                    Filter2(p, o, step);
                }
                else
                {
                    Filter4(p, o, step);
                }
            }
        }
    }

    private static bool Needs(byte[] p, int o, int s, int t) => 4 * Math.Abs(p[o - s] - p[o]) + Math.Abs(p[o - 2 * s] - p[o + s]) <= t;

    private static bool NeedsNormal(byte[] p, int o, int s, int t, int interior)
    {
        if (!Needs(p, o, s, t))
        {
            return false;
        }

        int p3 = p[o - 4 * s], p2 = p[o - 3 * s], p1 = p[o - 2 * s], p0 = p[o - s];
        int q0 = p[o], q1 = p[o + s], q2 = p[o + 2 * s], q3 = p[o + 3 * s];
        return Math.Abs(p3 - p2) <= interior && Math.Abs(p2 - p1) <= interior && Math.Abs(p1 - p0) <= interior
            && Math.Abs(q3 - q2) <= interior && Math.Abs(q2 - q1) <= interior && Math.Abs(q1 - q0) <= interior;
    }

    private static bool HighVariance(byte[] p, int o, int s, int threshold) =>
        Math.Abs(p[o - 2 * s] - p[o - s]) > threshold || Math.Abs(p[o + s] - p[o]) > threshold;

    private static int Signed8(int v) => Math.Clamp(v, -128, 127);

    private static int Signed5(int v) => Math.Clamp(v, -16, 15);

    private static void Filter2(byte[] p, int o, int s)
    {
        int p1 = p[o - 2 * s], p0 = p[o - s], q0 = p[o], q1 = p[o + s];
        int a = 3 * (q0 - p0) + Signed8(p1 - q1);
        int a1 = Signed5((a + 4) >> 3), a2 = Signed5((a + 3) >> 3);
        p[o - s] = Clip(p0 + a2);
        p[o] = Clip(q0 - a1);
    }

    private static void Filter4(byte[] p, int o, int s)
    {
        int p1 = p[o - 2 * s], p0 = p[o - s], q0 = p[o], q1 = p[o + s];
        int a = 3 * (q0 - p0);
        int a1 = Signed5((a + 4) >> 3), a2 = Signed5((a + 3) >> 3), a3 = (a1 + 1) >> 1;
        p[o - 2 * s] = Clip(p1 + a3);
        p[o - s] = Clip(p0 + a2);
        p[o] = Clip(q0 - a1);
        p[o + s] = Clip(q1 - a3);
    }

    private static void Filter6(byte[] p, int o, int s)
    {
        int p2 = p[o - 3 * s], p1 = p[o - 2 * s], p0 = p[o - s], q0 = p[o], q1 = p[o + s], q2 = p[o + 2 * s];
        int a = Signed8(3 * (q0 - p0) + Signed8(p1 - q1));
        int a1 = (27 * a + 63) >> 7, a2 = (18 * a + 63) >> 7, a3 = (9 * a + 63) >> 7;
        p[o - 3 * s] = Clip(p2 + a3);
        p[o - 2 * s] = Clip(p1 + a2);
        p[o - s] = Clip(p0 + a1);
        p[o] = Clip(q0 - a1);
        p[o + s] = Clip(q1 - a2);
        p[o + 2 * s] = Clip(q2 - a3);
    }

    /// <summary>RGB with libwebp's fancy upsampling: each pixel's chroma weighs the nearest sample 9, its two other
    /// neighbours 3 each and the diagonal 1, with libwebp's two-step rounding.</summary>
    private RgbaImage ToRgba()
    {
        var image = new RgbaImage(_width, _height);
        byte[] px = image.Pixels;
        int cw = (_width + 1) >> 1, ch = (_height + 1) >> 1;
        for (int y = 0; y < _height; y++)
        {
            int ny = y >> 1, oy = Math.Clamp((y & 1) == 1 ? ny + 1 : ny - 1, 0, ch - 1);
            for (int x = 0; x < _width; x++)
            {
                int nx = x >> 1, ox = Math.Clamp((x & 1) == 1 ? nx + 1 : nx - 1, 0, cw - 1);
                int u = Upsample(_u, nx, ny, ox, oy), v = Upsample(_v, nx, ny, ox, oy);
                int luma = MultHi(_y[y * _yStride + x], 19077);
                int o = (y * _width + x) * 4;
                px[o] = Clip8(luma + MultHi(v, 26149) - 14234);
                px[o + 1] = Clip8(luma - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
                px[o + 2] = Clip8(luma + MultHi(u, 33050) - 17685);
                px[o + 3] = 255;
            }
        }

        return image;
    }

    private int Upsample(byte[] plane, int nx, int ny, int ox, int oy)
    {
        int near = plane[ny * _uvStride + nx], vertical = plane[oy * _uvStride + nx];
        if (ox == nx)
        {
            return (3 * near + vertical + 2) >> 2;
        }

        int across = plane[ny * _uvStride + ox], diagonal = plane[oy * _uvStride + ox];
        return (((near + 3 * across + 3 * vertical + diagonal + 8) >> 3) + near) >> 1;
    }

    private static int MultHi(int v, int coefficient) => (v * coefficient) >> 8;

    private static byte Clip8(int v) => (v & ~16383) == 0 ? (byte)(v >> 6) : v < 0 ? (byte)0 : (byte)255;

    private sealed class Macroblock
    {
        public int Segment, YMode, UvMode;
        public bool Skip, IsI4;
        public readonly byte[] Modes = new byte[16];
        public readonly short[] Coeffs = new short[384];
    }

    /// <summary>The boolean entropy decoder (RFC 6386 7.3); past the end of its data it reads zeros.</summary>
    private sealed class BoolDecoder
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _pos, _bitCount;
        private uint _value, _range = 255;

        public BoolDecoder(byte[] data, int start, int length)
        {
            _data = data;
            _pos = start;
            _end = Math.Min(data.Length, start + length);
            _value = (uint)(Next() << 8 | Next());
        }

        private int Next() => _pos < _end ? _data[_pos++] : 0;

        public int Bit(int probability)
        {
            uint split = 1 + (((_range - 1) * (uint)probability) >> 8);
            uint big = split << 8;
            int bit;
            if (_value >= big)
            {
                bit = 1;
                _range -= split;
                _value -= big;
            }
            else
            {
                bit = 0;
                _range = split;
            }

            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _value |= (uint)Next();
                }
            }

            return bit;
        }

        public int Literal(int bits)
        {
            int v = 0;
            while (bits-- > 0)
            {
                v = v << 1 | Bit(128);
            }

            return v;
        }

        public int Signed(int bits)
        {
            int v = Literal(bits);
            return Bit(128) == 1 ? -v : v;
        }
    }
}
