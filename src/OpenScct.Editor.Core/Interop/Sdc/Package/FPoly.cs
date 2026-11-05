using System.Numerics;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// An FPoly (SDC-FORMAT.md section 8), the same record in brush Polys and in a model's surfs: a brush face with its
/// texture mapping, flags and links. There are no pans: they are folded into <see cref="Base"/>.
/// </summary>
public sealed record FPoly(
    Vector3 Base,
    Vector3 Normal,
    Vector3 TextureU,
    Vector3 TextureV,
    Vector3[] Vertices,
    uint PolyFlags,
    int Actor,
    int Material,
    string ItemName,
    int Link,
    int BrushPoly,
    float LightMapScalePsx2,
    float LightMapScaleXbox)
{
    /// <summary>The most vertices any poly of the corpus has; a larger count means the reader is misaligned.</summary>
    public const int MaxVertices = 16;

    public static FPoly Read(UReader r)
    {
        int at = r.Pos;
        int n = r.Compact();
        if (n is < 0 or > MaxVertices)
        {
            throw new InvalidDataException($"bad FPoly vertex count {n} at {at}");
        }

        Vector3 b = r.Vector(), normal = r.Vector(), u = r.Vector(), v = r.Vector();
        var vertices = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            vertices[i] = r.Vector();
        }

        return new FPoly(b, normal, u, v, vertices, r.U32(), r.ObjRef(), r.ObjRef(), r.Name(), r.Compact(), r.Compact(), r.F32(), r.F32());
    }
}

/// <summary>A UPolys export (UPolys::Serialize): props, i32 Num, i32 Max, Num FPolys. Same in both layouts.</summary>
public static class UPolysReader
{
    public static List<FPoly> Read(UPackage pkg, UExport e)
    {
        UReader r = pkg.Reader(e);
        return Exact(pkg, e, r, () =>
        {
            UPropertyReader.SkipStateFrame(r, e.Flags);
            UPropertyReader.SkipTagged(r);
            int num = r.I32(), max = r.I32();
            if (num < 0 || max < num || (long)num * 60 > r.Remaining)
            {
                throw new InvalidDataException($"bad Polys count {num}/{max} at {r.Pos - 8}");
            }

            var polys = new List<FPoly>(num);
            for (int i = 0; i < num; i++)
            {
                polys.Add(FPoly.Read(r));
            }

            return polys;
        });
    }

    /// <summary>Runs a native reader and checks it ends exactly on the export's last byte.</summary>
    internal static T Exact<T>(UPackage pkg, UExport e, UReader r, Func<T> read)
    {
        T result;
        try
        {
            result = read();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{pkg.Name}: export {e.Index} {e.Name} ({pkg.ClassName(e)}): {ex.Message}", ex);
        }

        if (r.Pos != r.End)
        {
            throw new InvalidDataException(
                $"{pkg.Name}: export {e.Index} {e.Name} ({pkg.ClassName(e)}) ends at {r.Pos}, not on its last byte ({r.End}; {r.End - r.Pos} bytes left)");
        }

        return result;
    }
}
