using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// A cursor over part of an unpacked package (one export's data, or a table). Positions are absolute offsets in the
/// package, which is what the string and name obfuscation keys on. Reading past <see cref="End"/> throws.
/// </summary>
public sealed class UReader
{
    private readonly byte[] _data;

    public UReader(byte[] data, int start, int end, UPackage? package)
    {
        if (start < 0 || end > data.Length || start > end)
        {
            throw new InvalidDataException($"range {start}..{end} is outside the package ({data.Length} bytes)");
        }

        _data = data;
        Pos = start;
        Start = start;
        End = end;
        Package = package;
    }

    public UPackage? Package { get; }

    public int Start { get; }

    public int End { get; }

    public int Pos { get; set; }

    public int Remaining => End - Pos;

    /// <summary>Bytes taken by the second (global) compact of every name read so far: property tag sizes leave them out.</summary>
    public int NameExtra { get; private set; }

    /// <summary>Whether the names read are noted in the package's second compacts (<see cref="UPackage.SecondCompact"/>).
    /// Off for data that is copied as it is: an asset saved in another editor session stores other values.</summary>
    public bool NotesNames { get; set; } = true;

    public ReadOnlySpan<byte> Span(int length) => _data.AsSpan(Take(length), length);

    public void Skip(int length) => Take(length);

    /// <summary>A copy of bytes already read (absolute offsets within this reader's range).</summary>
    public byte[] Bytes(int start, int length) => start >= Start && start + length <= End
        ? _data.AsSpan(start, length).ToArray()
        : throw new InvalidDataException($"bytes {start}..{start + length} are outside the reader's range");

    public byte U8() => _data[Take(1)];

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Take(2)));

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Take(4)));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Take(4)));

    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(Take(8)));

    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Take(4)));

    /// <summary>UE2 compact index: sign and more bits in the first byte, then 7-bit groups; at most 5 bytes.</summary>
    public int Compact()
    {
        int at = Pos;
        byte b = U8();
        bool negative = (b & 0x80) != 0;
        long value = b & 0x3F;
        if ((b & 0x40) != 0)
        {
            int shift = 6;
            for (int i = 1; ; i++)
            {
                if (i == 5)
                {
                    throw new InvalidDataException($"compact index longer than 5 bytes at {at}");
                }

                b = U8();
                value |= (long)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }
        }

        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"compact index out of range at {at}");
        }

        return negative ? -(int)value : (int)value;
    }

    /// <summary>A count read as a compact, checked against the bytes left (each item takes at least
    /// <paramref name="minBytes"/>).</summary>
    public int Count(int minBytes, string what)
    {
        int at = Pos;
        int n = Compact();
        if (n < 0 || (long)n * minBytes > Remaining)
        {
            throw new InvalidDataException($"bad {what} count {n} at {at}");
        }

        return n;
    }

    /// <summary>An SCCT name reference (two compacts: the package's name index, then an ignored global index).</summary>
    public int NameIndex()
    {
        int at = Pos;
        int index = Compact();
        int before = Pos;
        int second = Compact();
        NameExtra += Pos - before;
        if (Package is null || index < 0 || index >= Package.Names.Count)
        {
            throw new InvalidDataException($"bad name index {index} at {at}");
        }

        if (NotesNames)
        {
            Package.NoteSecondCompact(index, second);
        }

        return index;
    }

    public string Name() => Package!.Names[NameIndex()];

    /// <summary>An object reference: &gt; 0 export index + 1, &lt; 0 -(import index + 1), 0 none.</summary>
    public int ObjRef() => Compact();

    /// <summary>An FString (negative length = UTF-16), de-obfuscated when the package's names are.</summary>
    public string FString()
    {
        int at = Pos;
        int n = Compact();
        if (n == 0)
        {
            return "";
        }

        bool wide = n < 0;
        long bytes = wide ? -(long)n * 2 : n;
        if (bytes > Remaining)
        {
            throw new InvalidDataException($"string of {bytes} bytes at {at} runs past the end");
        }

        int start = Pos;
        ReadOnlySpan<byte> raw = Span((int)bytes);
        byte[] plain = raw.ToArray();
        if (Package?.Obfuscated ?? false)
        {
            for (int i = 0; i < plain.Length; i++)
            {
                plain[i] ^= (byte)(start + i);
            }
        }

        string s = wide ? Encoding.Unicode.GetString(plain) : Encoding.Latin1.GetString(plain);
        return s.TrimEnd('\0');
    }

    public Vector3 Vector() => new(F32(), F32(), F32());

    /// <summary>FRotator: i32 Pitch, Yaw, Roll.</summary>
    public (int Pitch, int Yaw, int Roll) Rotator() => (I32(), I32(), I32());

    /// <summary>FBox: Min, Max, u8 IsValid (25 bytes).</summary>
    public (Vector3 Min, Vector3 Max, bool Valid) Box() => (Vector(), Vector(), U8() != 0);

    /// <summary>FSphere or FPlane: four floats.</summary>
    public Vector4 Vector4() => new(F32(), F32(), F32(), F32());

    /// <summary>FMatrix: 16 floats.</summary>
    public void SkipMatrix() => Skip(64);

    private int Take(int length)
    {
        if (length < 0 || length > End - Pos)
        {
            throw new InvalidDataException($"read of {length} bytes at {Pos} runs past the end ({End})");
        }

        int at = Pos;
        Pos += length;
        return at;
    }
}
