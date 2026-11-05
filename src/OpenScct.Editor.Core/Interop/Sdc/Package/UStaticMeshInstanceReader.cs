namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// A StaticMeshInstance export (baked vertex light, SDC-FORMAT.md section 11): props, the colour stream, its revision,
/// and in an editor copy the lights that reached it. Read only to confirm a package's layout; not imported.
/// </summary>
public static class UStaticMeshInstanceReader
{
    /// <summary>Walks the export and returns its vertex colour count; throws unless it ends exactly.</summary>
    public static int Read(UPackage pkg, UExport e, SdcLayout layout)
    {
        UReader r = pkg.Reader(e);
        return UPolysReader.Exact(pkg, e, r, () =>
        {
            UPropertyReader.SkipStateFrame(r, e.Flags);
            UPropertyReader.SkipTagged(r);
            int colors = r.Count(4, "ColorStream");
            r.Skip(4 * colors + 4);
            if (layout == SdcLayout.Editor)
            {
                int lights = r.Count(6, "instance lights");
                for (int i = 0; i < lights; i++)
                {
                    r.ObjRef();
                    r.Skip(r.Count(1, "visibility bits"));
                    r.I32();
                }
            }

            return colors;
        });
    }
}
