using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// The write side of <see cref="UReader"/>: a growing buffer that is the whole unpacked package, so <see cref="Pos"/>
/// is the absolute offset the name and string obfuscation keys on (SDC-FORMAT.md sections 3 and 4). Names are written
/// through the package's name table.
/// </summary>
public sealed class UWriter
{
    private byte[] _buf = new byte[1 << 16];

    public UWriter(UPackageBuilder package) => Package = package;

    public UPackageBuilder Package { get; }

    /// <summary>Bytes written so far (also the absolute offset of the next byte).</summary>
    public int Pos { get; private set; }

    /// <summary>Bytes taken by the second compact of every name written so far: property tag sizes leave them out.</summary>
    public int NameExtra { get; private set; }

    public byte[] ToArray() => _buf.AsSpan(0, Pos).ToArray();

    public void U8(byte v) => Take(1)[0] = v;

    public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);

    public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);

    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);

    public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);

    public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);

    public void Bytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Take(bytes.Length));

    /// <summary>Zero bytes up to the next offset that is <paramref name="residue"/> modulo 256 (a copied export keeps
    /// the low byte of its offset, so the strings inside it, obfuscated by offset, still read).</summary>
    public void AlignTo256(int residue)
    {
        int pad = ((residue - Pos) % 256 + 256) % 256;
        Take(pad).Clear();
    }

    public void PatchI32(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(at, 4), v);

    /// <summary>UE2 compact index (the inverse of <see cref="UReader.Compact"/>).</summary>
    public void Compact(int v)
    {
        long a = Math.Abs((long)v);
        byte b = (byte)((v < 0 ? 0x80 : 0) | (int)(a & 0x3F));
        a >>= 6;
        if (a != 0)
        {
            b |= 0x40;
        }

        U8(b);
        while (a != 0)
        {
            b = (byte)(a & 0x7F);
            a >>= 7;
            if (a != 0)
            {
                b |= 0x80;
            }

            U8(b);
        }
    }

    /// <summary>The bytes <see cref="Compact"/> takes for a value.</summary>
    public static int CompactSize(int v)
    {
        long a = Math.Abs((long)v) >> 6;
        int n = 1;
        for (; a != 0; a >>= 7)
        {
            n++;
        }

        return n;
    }

    /// <summary>An SCCT name reference: the name's index, then the second compact the loader reads and drops (the
    /// stock editor writes its session's global name index there; the source's value is kept where known).</summary>
    public void Name(string name)
    {
        int index = Package.Name(name);
        Compact(index);
        int before = Pos;
        Compact(Package.NameHash(index));
        NameExtra += Pos - before;
    }

    public void ObjRef(int objref) => Compact(objref);

    /// <summary>An FString: Latin-1 when every character fits, else UTF-16 (negative length), with the trailing NUL,
    /// obfuscated by offset like the names. The empty string is a bare 0.</summary>
    public void FString(string s)
    {
        if (s.Length == 0)
        {
            Compact(0);
            return;
        }

        bool wide = s.Any(c => c > 0xFF);
        byte[] bytes = wide ? Encoding.Unicode.GetBytes(s + "\0") : Encoding.Latin1.GetBytes(s + "\0");
        Compact(wide ? -(s.Length + 1) : s.Length + 1);
        int start = Pos;
        Span<byte> dst = Take(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            dst[i] = Package.Obfuscated ? (byte)(bytes[i] ^ (byte)(start + i)) : bytes[i];
        }
    }

    public void Vector(Vector3 v)
    {
        F32(v.X);
        F32(v.Y);
        F32(v.Z);
    }

    public void Vector4(Vector4 v)
    {
        F32(v.X);
        F32(v.Y);
        F32(v.Z);
        F32(v.W);
    }

    /// <summary>FBox: Min, Max, u8 IsValid.</summary>
    public void Box(Vector3 min, Vector3 max, bool valid)
    {
        Vector(min);
        Vector(max);
        U8(valid ? (byte)1 : (byte)0);
    }

    private Span<byte> Take(int length)
    {
        if (Pos + length > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Pos + length));
        }

        Span<byte> span = _buf.AsSpan(Pos, length);
        Pos += length;
        return span;
    }
}
