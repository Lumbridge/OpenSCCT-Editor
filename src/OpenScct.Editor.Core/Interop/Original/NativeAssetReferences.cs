using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>Object references in native asset payloads. Geometry, mip/audio bytes and animation keys are not objrefs.</summary>
internal static class NativeAssetReferences
{
    public static IReadOnlyCollection<int> Read(UPackage pkg, UExport e, UeSchema? schema, List<Finding> findings)
    {
        var refs = new HashSet<int> { e.ClassRef, e.SuperRef, e.Outer };
        if (e.Size == 0) return refs;
        string cls = pkg.ClassName(e);
        var trace = new UPropertyTrace();
        UReader r = pkg.Reader(e);
        UPropertyReader.SkipStateFrame(r, e.Flags);
        try
        {
            new UPropertyReader(schema, (_, _) => null) { Trace = trace,
                TaggedStructArrays = cls == "StaticMesh" ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Materials" } : null }
                .ReadObject(r, string.Join('.', pkg.ClassOf(e).Package, cls), e.Name);
        }
        catch (InvalidDataException ex)
        { throw new InvalidDataException($"{pkg.Name}.{e.Name} ({cls}): {ex.Message}", ex); }
        foreach (var span in trace.Refs) refs.Add(new UReader(pkg.Data, span.At, span.End, pkg).ObjRef());
        bool known = !trace.Opaque;
        if (r.Remaining != 0)
        {
            switch (cls)
            {
                case "SkeletalMesh": Skeletal(r, refs); break;
                case "MeshAnimation": Animation(r, refs); break;
                case "StaticMesh": case "Texture": case "Palette": case "Sound": break;
                default: known = false; break;
            }
        }
        if (!known)
        {
            // Unknown native tails may hold references. Never guess at arbitrary compact-looking bytes.
            // Keep the package closure and disclose the conservative exception in the export report.
            foreach (UExport item in pkg.Exports) refs.Add(item.Index + 1);
            for (int i = 0; i < pkg.Imports.Count; i++) refs.Add(-(i + 1));
            if (!findings.Any(f => f.Code == "original.assetClosure" && f.Message.StartsWith(pkg.Name + ":", StringComparison.Ordinal)))
                findings.Add(Finding.Warning("original.assetClosure", $"{pkg.Name}: retained its complete object dependency closure because {cls} has native data that cannot be safely pruned."));
        }
        return refs;
    }

    private static void Skeletal(UReader r, HashSet<int> refs)
    {
        r.Skip(25 + 16); r.I32(); r.I32();
        r.Skip(4 * r.Count(4, "skeletal vertices"));
        int materials = r.Count(1, "skeletal materials");
        for (int i = 0; i < materials; i++) refs.Add(r.ObjRef());
        r.Skip(36);
        if (r.Package!.Version <= 174) r.Skip(2 * r.Count(2, "skeletal face values"));
        // SCCT has several LOD headers preceding RefSkeleton. Validate the complete bone chain, as the
        // shared native reader does, then read the animation reference immediately after it.
        for (int at = r.Pos; at < r.End; at++)
        {
            var scan = new UReader(r.Package.Data, at, r.End, r.Package) { NotesNames = false };
            try
            {
                int count = scan.Count(64, "bones");
                if (count is < 1 or > 512) continue;
                for (int i = 0; i < count; i++)
                {
                    scan.Name(); scan.U32();
                    float x = scan.F32(), y = scan.F32(), z = scan.F32(), w = scan.F32();
                    double norm = x*x + y*y + z*z + w*w;
                    if (!(norm > .95 && norm < 1.05)) throw new InvalidDataException("Invalid bone orientation.");
                    scan.Skip(28);
                    int children = scan.I32(), parent = scan.I32();
                    if (children is < 0 or >= 512 || parent < -1 || parent >= count) throw new InvalidDataException("Invalid bone hierarchy.");
                }
                int animation = scan.ObjRef();
                if (animation > r.Package.Exports.Count || -animation > r.Package.Imports.Count) continue;
                refs.Add(animation); return;
            }
            catch (InvalidDataException) { }
        }
        throw new InvalidDataException($"{r.Package.Name}: cannot locate the skeletal animation dependency.");
    }

    private static void Animation(UReader r, HashSet<int> refs)
    {
        r.I32();
        int bones = r.Count(10, "animation bones");
        for (int i=0; i<bones; i++) { r.Name(); r.Skip(8); }
        int moves = r.Count(24, "animation moves");
        for (int i=0; i<moves; i++)
        {
            r.Skip(24); r.Skip(4 * r.Count(4, "bone indices"));
            int tracks = r.Count(7, "animation tracks");
            for (int j=0; j<=tracks; j++) Track(r);
        }
        int sequences = r.Count(16, "animation sequences");
        for (int i=0; i<sequences; i++)
        {
            r.F32(); r.Name();
            int groups = r.Count(2, "animation groups"); for (int j=0; j<groups; j++) r.Name();
            r.Skip(8);
            int notifies = r.Count(7, "animation notifies");
            for (int j=0; j<notifies; j++) { r.Skip(6); refs.Add(r.ObjRef()); }
            r.F32();
        }
        if (r.Remaining != 0) throw new InvalidDataException("Unrecognised animation tail.");
    }

    private static void Track(UReader r)
    {
        r.U32();
        r.Skip(8 * r.Count(8, "rotation keys"));
        r.Skip(6 * r.Count(6, "position keys"));
        r.Skip(2 * r.Count(2, "time keys"));
    }
}
