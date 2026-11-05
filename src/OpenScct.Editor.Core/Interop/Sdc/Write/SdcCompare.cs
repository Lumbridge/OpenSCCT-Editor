using System.Globalization;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>One kind of difference between two map packages, with a count and a few examples.</summary>
public sealed record SdcDifference(string Kind, int Count, IReadOnlyList<string> Examples)
{
    public override string ToString() => $"{Kind}: {Count}" + (Examples.Count > 0 ? $" (e.g. {string.Join("; ", Examples)})" : "");
}

/// <summary>Counts of a comparison, section by section.</summary>
public sealed record SdcComparison(
    int Names, int OtherNames, int Imports, int OtherImports, int Exports, int OtherExports, int SameBytes, int SameObjects,
    IReadOnlyList<SdcDifference> Differences)
{
    public bool Identical => Differences.Count == 0;
}

/// <summary>
/// Compares two map packages object by object, the way the engine sees them: names and imports as sets, exports by
/// path and class, every actor by its decoded properties (references compared by path), brush polys by every field,
/// models by every array, the level by its actor list, URL and GEs, and any other object by its bytes. Used by the
/// .sdc round-trip check and the round-trip lint.
/// </summary>
public static class SdcCompare
{
    private const int MaxExamples = 6;

    public static SdcComparison Compare(UPackage a, UPackage b, UeSchema? schema)
    {
        var diffs = new Dictionary<string, (int Count, List<string> Examples)>();
        void Diff(string kind, string example)
        {
            (int count, List<string> examples) = diffs.GetValueOrDefault(kind, (0, new List<string>()));
            if (examples.Count < MaxExamples)
            {
                examples.Add(example);
            }

            diffs[kind] = (count + 1, examples);
        }

        var namesA = new HashSet<string>(a.Names, StringComparer.OrdinalIgnoreCase);
        var namesB = new HashSet<string>(b.Names, StringComparer.OrdinalIgnoreCase);
        foreach (string n in namesA.Where(n => !namesB.Contains(n)))
        {
            Diff("name only in the first", n);
        }

        foreach (string n in namesB.Where(n => !namesA.Contains(n)))
        {
            Diff("name only in the second", n);
        }

        var importsA = Enumerable.Range(1, a.Imports.Count).Select(i => ImportKey(a, -i)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var importsB = Enumerable.Range(1, b.Imports.Count).Select(i => ImportKey(b, -i)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string i in importsA.Where(i => !importsB.Contains(i)))
        {
            Diff("import only in the first", i);
        }

        foreach (string i in importsB.Where(i => !importsA.Contains(i)))
        {
            Diff("import only in the second", i);
        }

        Dictionary<string, UExport> exportsA = ByPath(a), exportsB = ByPath(b);
        foreach (string p in exportsA.Keys.Where(p => !exportsB.ContainsKey(p)))
        {
            Diff($"export only in the first ({a.ClassName(exportsA[p])})", p);
        }

        foreach (string p in exportsB.Keys.Where(p => !exportsA.ContainsKey(p)))
        {
            Diff($"export only in the second ({b.ClassName(exportsB[p])})", p);
        }

        SdcLayout layoutA = SdcLayoutDetector.Detect(a), layoutB = SdcLayoutDetector.Detect(b);
        if (layoutA != layoutB)
        {
            Diff("layout", $"{layoutA} against {layoutB}");
        }

        SdcMap? mapA = TryRead(a, schema, Diff, "first"), mapB = TryRead(b, schema, Diff, "second");
        var actorsA = mapA?.Actors.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var actorsB = mapB?.Actors.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int sameBytes = 0, sameObjects = 0;
        foreach ((string path, UExport ea) in exportsA)
        {
            if (!exportsB.TryGetValue(path, out UExport? eb))
            {
                continue;
            }

            string cls = a.ClassName(ea);
            if (!cls.Equals(b.ClassName(eb), StringComparison.OrdinalIgnoreCase))
            {
                Diff("export class", $"{path}: {cls} against {b.ClassName(eb)}");
                continue;
            }

            if ((ea.Flags & ~0x2u) != (eb.Flags & ~0x2u))
            {
                Diff($"export flags ({cls})", $"{path}: {ea.Flags:X} against {eb.Flags:X}");
            }

            ReadOnlySpan<byte> da = Data(a, ea), db = Data(b, eb);
            if (da.SequenceEqual(db))
            {
                sameBytes++;
                sameObjects++;
                continue;
            }

            bool same;
            if (actorsA is not null && actorsB is not null && actorsA.TryGetValue(ea.Name, out SdcActor? xa) && actorsB.TryGetValue(eb.Name, out SdcActor? xb)
                && xa.Export.Index == ea.Index && xb.Export.Index == eb.Index)
            {
                same = SameActor(xa, xb, Diff);
            }
            else if (cls.Equals("Model", StringComparison.OrdinalIgnoreCase))
            {
                same = SameModel(a, ea, layoutA, b, eb, layoutB, path, Diff);
            }
            else if (cls.Equals("Polys", StringComparison.OrdinalIgnoreCase))
            {
                same = SamePolys(a, UPolysReader.Read(a, ea), b, UPolysReader.Read(b, eb), path, "polys", Diff);
            }
            else if (cls.Equals("Level", StringComparison.OrdinalIgnoreCase))
            {
                same = SameLevel(a, ea, b, eb, Diff);
            }
            else if (cls.Equals("StaticMeshInstance", StringComparison.OrdinalIgnoreCase))
            {
                same = SameInstance(a, ea, layoutA, b, eb, layoutB, path, Diff);
            }
            else if (Decoded(a, ea, schema) is { } ka && ka == Decoded(b, eb, schema))
            {
                // The same object in other slots: its references point at the same objects.
                same = true;
            }
            else
            {
                same = false;
                Diff($"bytes ({cls})", $"{path}: {da.Length} against {db.Length} bytes");
            }

            if (same)
            {
                sameObjects++;
            }
        }

        var list = diffs.Select(kv => new SdcDifference(kv.Key, kv.Value.Count, kv.Value.Examples)).OrderBy(d => d.Kind, StringComparer.Ordinal).ToList();
        return new SdcComparison(a.Names.Count, b.Names.Count, a.Imports.Count, b.Imports.Count, a.Exports.Count, b.Exports.Count, sameBytes, sameObjects, list);
    }

    private static SdcMap? TryRead(UPackage pkg, UeSchema? schema, Action<string, string> diff, string which)
    {
        try
        {
            return SdcMapReader.Read(pkg, schema);
        }
        catch (InvalidDataException ex)
        {
            diff($"the {which} package does not read as a map", ex.Message);
            return null;
        }
    }

    /// <summary>An object as the engine reads it, with references as paths: the state frame, the tagged properties and
    /// what follows them (a soft body's owner as a path), or null when it does not read.</summary>
    private static string? Decoded(UPackage p, UExport e, UeSchema? schema)
    {
        try
        {
            UReader r = p.Reader(e);
            var key = new System.Text.StringBuilder();
            if ((e.Flags & UExport.HasStack) != 0)
            {
                int node = r.ObjRef();
                key.Append(Local(p, node)).Append('|').Append(Local(p, r.ObjRef())).Append('|').Append(r.U64()).Append('|').Append(r.I32()).Append('|');
                if (node != 0)
                {
                    key.Append(r.Compact()).Append('|');
                }
            }

            (string package, string cls) = p.ClassOf(e);
            var reader = new UPropertyReader(schema, (objref, _) => SvJsonNode.String(Local(p, objref)));
            key.Append(CanonicalJson.ToCanonicalString(reader.ReadObject(r, e.ClassRef == 0 ? null : package + "." + cls, e.Name)));
            if (cls is "ESBPatch" or "ESBStripDoor" && r.Remaining > 49)
            {
                key.Append(Convert.ToHexString(r.Span(49))).Append('|').Append(Local(p, r.ObjRef())).Append('|');
            }

            return key.Append(Convert.ToHexString(r.Span(r.Remaining))).ToString();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static ReadOnlySpan<byte> Data(UPackage p, UExport e) => e.Size > 0 ? p.Data.AsSpan(e.Offset, e.Size) : [];

    private static string ImportKey(UPackage p, int objref) => p.PathOf(objref) + ":" + p.Import(objref).ClassPackage + "." + p.Import(objref).ClassName;

    /// <summary>Exports by path; a second export of the same path gets a #n suffix.</summary>
    private static Dictionary<string, UExport> ByPath(UPackage p)
    {
        var d = new Dictionary<string, UExport>(StringComparer.OrdinalIgnoreCase);
        foreach (UExport e in p.Exports)
        {
            string path = p.PathOf(e.Index + 1);
            path = path[(path.IndexOf('.') + 1)..];
            string key = path;
            for (int i = 2; d.ContainsKey(key); i++)
            {
                key = path + "#" + i.ToString(CultureInfo.InvariantCulture);
            }

            d[key] = e;
        }

        return d;
    }

    private static bool SameActor(SdcActor a, SdcActor b, Action<string, string> diff)
    {
        bool same = true;
        if (!a.Class.Equals(b.Class, StringComparison.OrdinalIgnoreCase))
        {
            diff("actor class", $"{a.Name}: {a.Class} against {b.Class}");
            same = false;
        }

        var keys = a.Props.Select(kv => kv.Key).Union(b.Props.Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
        foreach (string k in keys)
        {
            SvJsonNode? va = a.Props[k], vb = b.Props[k];
            if (va is null)
            {
                diff($"actor property only in the second ({k})", $"{a.Name}.{k} = {Short(vb)}");
                same = false;
            }
            else if (vb is null)
            {
                diff($"actor property only in the first ({k})", $"{a.Name}.{k} = {Short(va)}");
                same = false;
            }
            else if (CanonicalJson.ToCanonicalString(va) != CanonicalJson.ToCanonicalString(vb))
            {
                diff($"actor property value ({k})", $"{a.Name}.{k}: {Short(va)} against {Short(vb)}");
                same = false;
            }
        }

        string order = string.Join(",", a.Props.Select(kv => kv.Key)), orderB = string.Join(",", b.Props.Select(kv => kv.Key));
        if (same && order != orderB)
        {
            diff("actor property order", $"{a.Name}: {order} against {orderB}");
        }

        return same;
    }

    private static string Short(SvJsonNode? v)
    {
        string s = v is null ? "-" : CanonicalJson.ToCanonicalString(v);
        return s.Length > 80 ? s[..77] + "..." : s;
    }

    private static bool SamePolys(UPackage pa, IReadOnlyList<FPoly> a, UPackage pb, IReadOnlyList<FPoly> b, string path, string what, Action<string, string> diff)
    {
        if (a.Count != b.Count)
        {
            diff($"{what} count", $"{path}: {a.Count} against {b.Count}");
            return false;
        }

        bool same = true;
        for (int i = 0; i < a.Count; i++)
        {
            foreach (string field in PolyDiffs(pa, a[i], pb, b[i]))
            {
                diff($"{what} {field}", $"{path}[{i}]");
                same = false;
            }
        }

        return same;
    }

    private static IEnumerable<string> PolyDiffs(UPackage pa, FPoly a, UPackage pb, FPoly b)
    {
        if (!a.Vertices.SequenceEqual(b.Vertices))
        {
            yield return "vertices";
        }

        if (a.Base != b.Base)
        {
            yield return "base";
        }

        if (a.Normal != b.Normal)
        {
            yield return "normal";
        }

        if (a.TextureU != b.TextureU || a.TextureV != b.TextureV)
        {
            yield return "texture axes";
        }

        if (a.PolyFlags != b.PolyFlags)
        {
            yield return "flags";
        }

        if (!pa.PathOf(a.Material).Equals(pb.PathOf(b.Material), StringComparison.OrdinalIgnoreCase))
        {
            yield return "material";
        }

        if (!Local(pa, a.Actor).Equals(Local(pb, b.Actor), StringComparison.OrdinalIgnoreCase) || a.BrushPoly != b.BrushPoly)
        {
            yield return "brush link";
        }

        if (!a.ItemName.Equals(b.ItemName, StringComparison.OrdinalIgnoreCase))
        {
            yield return "item name";
        }

        if (a.Link != b.Link)
        {
            yield return "link";
        }

        if (a.LightMapScalePsx2 != b.LightMapScalePsx2 || a.LightMapScaleXbox != b.LightMapScaleXbox)
        {
            yield return "lightmap scales";
        }
    }

    /// <summary>A path without the package name (two copies of a map may have different file names).</summary>
    private static string Local(UPackage p, int objref)
    {
        if (objref <= 0)
        {
            return p.PathOf(objref);
        }

        string path = p.PathOf(objref);
        return path[(path.IndexOf('.') + 1)..];
    }

    private static bool SameModel(UPackage pa, UExport ea, SdcLayout la, UPackage pb, UExport eb, SdcLayout lb, string path, Action<string, string> diff)
    {
        UModelData a = UModelData.Read(pa, ea, la), b = UModelData.Read(pb, eb, lb);
        bool same = true;
        void Check(string what, bool equal)
        {
            if (!equal)
            {
                diff($"model {what}", path);
                same = false;
            }
        }

        Check("bounds", a.BoxMin == b.BoxMin && a.BoxMax == b.BoxMax && a.BoxValid == b.BoxValid && a.Sphere == b.Sphere);
        Check("vectors", a.Vectors.SequenceEqual(b.Vectors));
        Check("points", a.Points.SequenceEqual(b.Points));
        Check("node count", a.Nodes.Count == b.Nodes.Count);
        if (a.Nodes.Count == b.Nodes.Count)
        {
            Check("nodes", a.Nodes.Zip(b.Nodes).All(n => NodeEqual(n.First, n.Second)));
        }

        Check("surf count", a.Surfs.Count == b.Surfs.Count);
        if (a.Surfs.Count == b.Surfs.Count)
        {
            for (int i = 0; i < a.Surfs.Count; i++)
            {
                FBspSurf sa = a.Surfs[i], sb = b.Surfs[i];
                Check("surf material", Local(pa, sa.Material).Equals(Local(pb, sb.Material), StringComparison.OrdinalIgnoreCase));
                Check("surf fields", sa.PolyFlags == sb.PolyFlags && sa.Base == sb.Base && sa.Normal == sb.Normal && sa.TextureU == sb.TextureU
                    && sa.TextureV == sb.TextureV && sa.Plane == sb.Plane && sa.LightMapScalePsx2 == sb.LightMapScalePsx2 && sa.LightMapScaleXbox == sb.LightMapScaleXbox);
                if (la == lb)
                {
                    Check("surf brush link", Local(pa, sa.Actor).Equals(Local(pb, sb.Actor), StringComparison.OrdinalIgnoreCase) && sa.BrushPoly == sb.BrushPoly);
                    Check("surf poly", (sa.Poly is null) == (sb.Poly is null) && (sa.Poly is null || !PolyDiffs(pa, sa.Poly, pb, sb.Poly!).Any()));
                }
            }
        }

        Check("verts", a.Verts.SequenceEqual(b.Verts));
        Check("shared sides", a.NumSharedSides == b.NumSharedSides);
        Check("zones", a.Zones.Count == b.Zones.Count && a.Zones.Zip(b.Zones).All(z => Local(pa, z.First.Actor).Equals(Local(pb, z.Second.Actor), StringComparison.OrdinalIgnoreCase)
            && z.First.Connectivity == z.Second.Connectivity));
        Check("polys link", Local(pa, a.Polys).Equals(Local(pb, b.Polys), StringComparison.OrdinalIgnoreCase));
        Check("render bounds", a.Bounds.SequenceEqual(b.Bounds));
        Check("leaf hulls", a.LeafHulls.SequenceEqual(b.LeafHulls));
        Check("leaves", a.Leaves.SequenceEqual(b.Leaves));
        Check("lights", a.Lights.Select(l => Local(pa, l)).SequenceEqual(b.Lights.Select(l => Local(pb, l)), StringComparer.OrdinalIgnoreCase));
        Check("root/linked", a.RootOutside == b.RootOutside && a.Linked == b.Linked);
        Check("sections", a.Sections.Count == b.Sections.Count && a.Sections.Zip(b.Sections).All(s => s.First.Vertices.AsSpan().SequenceEqual(s.Second.Vertices)
            && s.First.PolyFlags == s.Second.PolyFlags && s.First.LightMapTexture == s.Second.LightMapTexture && s.First.NumNodes == s.Second.NumNodes
            && Local(pa, s.First.Material).Equals(Local(pb, s.Second.Material), StringComparison.OrdinalIgnoreCase)));
        if (la == lb)
        {
            Check("lightmap records", a.LightMaps.Count == b.LightMaps.Count && a.LightMaps.Zip(b.LightMaps).All(l => l.First.Header.SequenceEqual(l.Second.Header)
                && l.First.Matrix.AsSpan().SequenceEqual(l.Second.Matrix) && l.First.Lights.Count == l.Second.Lights.Count
                && l.First.Lights.Zip(l.Second.Lights).All(x => Local(pa, x.First.Light).Equals(Local(pb, x.Second.Light), StringComparison.OrdinalIgnoreCase)
                    && x.First.Bits.AsSpan().SequenceEqual(x.Second.Bits))));
        }

        Check("lightmap textures", a.LightMapTextures.Count == b.LightMapTextures.Count && a.LightMapTextures.Zip(b.LightMapTextures).All(t =>
            t.First.LightMaps.SequenceEqual(t.Second.LightMaps) && t.First.Data.AsSpan().SequenceEqual(t.Second.Data) && t.First.Format == t.Second.Format
            && t.First.Width == t.Second.Width && t.First.Height == t.Second.Height));
        return same;
    }

    private static bool NodeEqual(UBspNode a, UBspNode b) =>
        a.Plane == b.Plane && a.ZoneMask == b.ZoneMask && a.NodeFlags == b.NodeFlags && a.VertPool == b.VertPool && a.Surf == b.Surf && a.Back == b.Back
        && a.Front == b.Front && a.PlaneLink == b.PlaneLink && a.CollisionBound == b.CollisionBound && a.RenderBound == b.RenderBound
        && a.ExclusiveSphereBound == b.ExclusiveSphereBound && a.Zone0 == b.Zone0 && a.Zone1 == b.Zone1 && a.NumVertices == b.NumVertices
        && a.Leaf0 == b.Leaf0 && a.Leaf1 == b.Leaf1 && a.Section == b.Section && a.FirstVertex == b.FirstVertex && a.LightMap == b.LightMap;

    private static bool SameInstance(UPackage pa, UExport ea, SdcLayout la, UPackage pb, UExport eb, SdcLayout lb, string path, Action<string, string> diff)
    {
        USmiData a = USmiData.Read(pa, ea, la), b = USmiData.Read(pb, eb, lb);
        bool same = a.Colors.AsSpan().SequenceEqual(b.Colors) && a.Revision == b.Revision;
        if (la == lb)
        {
            same &= a.Lights.Count == b.Lights.Count && a.Lights.Zip(b.Lights).All(l =>
                Local(pa, l.First.Light).Equals(Local(pb, l.Second.Light), StringComparison.OrdinalIgnoreCase) && l.First.Bits.AsSpan().SequenceEqual(l.Second.Bits));
        }

        if (!same)
        {
            diff("static mesh instance", path);
        }

        return same;
    }

    private static bool SameLevel(UPackage pa, UExport ea, UPackage pb, UExport eb, Action<string, string> diff)
    {
        ULevel a = ULevelReader.Read(pa, ea), b = ULevelReader.Read(pb, eb);
        bool same = true;
        void Check(string what, bool equal, string example = "")
        {
            if (!equal)
            {
                diff($"level {what}", example);
                same = false;
            }
        }

        List<string> actorsA = a.Actors.Select(r => Local(pa, r)).ToList(), actorsB = b.Actors.Select(r => Local(pb, r)).ToList();
        int firstDiff = actorsA.Zip(actorsB).TakeWhile(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase)).Count();
        Check("actor list", actorsA.Count == actorsB.Count && firstDiff == actorsA.Count,
            $"{actorsA.Count} against {actorsB.Count} actors, first difference at {firstDiff}");
        Check("URL", a.UrlProtocol == b.UrlProtocol && a.UrlHost == b.UrlHost && a.UrlMap == b.UrlMap && a.UrlPortal == b.UrlPortal
            && a.UrlOptions.SequenceEqual(b.UrlOptions) && a.UrlPort == b.UrlPort && a.UrlValid == b.UrlValid);
        Check("world models", Local(pa, a.ModelXbox).Equals(Local(pb, b.ModelXbox), StringComparison.OrdinalIgnoreCase)
            && Local(pa, a.ModelPsx2).Equals(Local(pb, b.ModelPsx2), StringComparison.OrdinalIgnoreCase));
        Check("time and text", a.TimeSeconds == b.TimeSeconds && a.TextBlocks.Select(t => Local(pa, t)).SequenceEqual(b.TextBlocks.Select(t => Local(pb, t))));
        Check("GEs", SdcGePartition.Same(a.Ges, b.Ges), $"{a.Ges.Count} against {b.Ges.Count}");
        Check("GE partition bytes", a.PartitionBytes.AsSpan().SequenceEqual(b.PartitionBytes));
        return same;
    }
}
