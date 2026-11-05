namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// A StaticMeshInstance export (baked vertex light, SDC-FORMAT.md section 11), read and written in either layout:
/// "None", the BGRA colour stream, its revision and, in an editor copy, the lights that reached it.
/// </summary>
public sealed class USmiData
{
    public byte[] Colors = [];
    public int ColorCount;
    public int Revision;
    public List<(int Light, byte[] Bits, int Value)> Lights = new();

    public static USmiData Read(UPackage pkg, UExport e, SdcLayout layout)
    {
        UReader r = pkg.Reader(e);
        return UPolysReader.Exact(pkg, e, r, () =>
        {
            UPropertyReader.SkipStateFrame(r, e.Flags);
            if (r.Name() != "None")
            {
                throw new InvalidDataException("the StaticMeshInstance has tagged properties");
            }

            var s = new USmiData { ColorCount = r.Count(4, "ColorStream") };
            s.Colors = r.Span(4 * s.ColorCount).ToArray();
            s.Revision = r.I32();
            if (layout == SdcLayout.Editor)
            {
                int n = r.Count(6, "instance lights");
                for (int i = 0; i < n; i++)
                {
                    s.Lights.Add((r.ObjRef(), r.Span(r.Count(1, "visibility bits")).ToArray(), r.I32()));
                }
            }

            return s;
        });
    }

    /// <summary>Writes the instance; light references go through <paramref name="map"/>, and lights it no longer
    /// has (deleted) are dropped. A cooked copy has no light list.</summary>
    public void Write(UWriter w, SdcLayout layout, Func<int, int> map)
    {
        w.Name("None");
        w.Compact(ColorCount);
        w.Bytes(Colors);
        w.I32(Revision);
        if (layout == SdcLayout.Editor)
        {
            var kept = Lights.Select(l => (Light: l.Light == 0 ? 0 : map(l.Light), l.Bits, l.Value, Was: l.Light)).Where(l => l.Light != 0 || l.Was == 0).ToList();
            w.Compact(kept.Count);
            foreach ((int light, byte[] bits, int value, int _) in kept)
            {
                w.ObjRef(light);
                w.Compact(bits.Length);
                w.Bytes(bits);
                w.I32(value);
            }
        }
    }
}
