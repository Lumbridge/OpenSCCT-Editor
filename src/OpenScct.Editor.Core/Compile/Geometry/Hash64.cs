using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>A small 64-bit FNV-1a accumulator for in-memory cache keys (never written to disk).</summary>
public struct Hash64
{
    private const ulong Prime = 1099511628211UL;
    private ulong _h;

    public Hash64() => _h = 14695981039346656037UL;

    public readonly ulong Value => _h;

    public void Add(ulong v)
    {
        for (int i = 0; i < 8; i++)
        {
            _h ^= (v >> (i * 8)) & 0xFF;
            _h *= Prime;
        }
    }

    public void Add(long v) => Add(unchecked((ulong)v));

    public void Add(int v) => Add((ulong)(uint)v);

    public void Add(bool v) => Add(v ? 1UL : 0UL);

    public void Add(double v) => Add(BitConverter.DoubleToUInt64Bits(v));

    public void Add(Vec3 v)
    {
        Add(v.X);
        Add(v.Y);
        Add(v.Z);
    }

    public void Add(Rot r)
    {
        Add(r.Pitch);
        Add(r.Yaw);
        Add(r.Roll);
    }

    public void Add(string? s)
    {
        if (s is null)
        {
            Add(0xFFFFFFFFFFFFFFFFUL);
            return;
        }

        Add(s.Length);
        foreach (char c in s)
        {
            Add((ulong)c);
        }
    }
}
