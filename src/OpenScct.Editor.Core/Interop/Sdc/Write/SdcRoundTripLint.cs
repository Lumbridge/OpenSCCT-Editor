using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// Reads a written .sdc back with the native reader, as the import would, and checks it holds the project: the layout
/// it was written in, every brush and actor by name (the cooked copy without the ones the cook leaves out), the GEs,
/// and properties the class data types. For a map imported from an .sdc, the written package is also compared with
/// that file object by object (<see cref="SdcCompare"/>): what differs should be what was edited.
/// </summary>
public static class SdcRoundTripLint
{
    public static List<Finding> Check(MapProject p, SdcWriteResult written, string packageName, SdcLayout layout, UeSchema? schema)
    {
        p = GeGeometry.Expand(p);
        var f = new List<Finding>();
        UPackage pkg;
        SdcMap map;
        try
        {
            pkg = UPackage.Parse(packageName, written.Package, 1);
            SdcLayout read = SdcLayoutDetector.Detect(pkg);
            if (read != layout)
            {
                f.Add(Finding.Error("sdc.readBack", $"the file was written as the {Name(layout)} but reads back as the {Name(read)}"));
            }

            map = SdcMapReader.Read(pkg, schema);
        }
        catch (InvalidDataException e)
        {
            f.Add(Finding.Error("sdc.readBack", $"the written file does not read back: {e.Message}"));
            return f;
        }

        var names = new HashSet<string>(map.Actors.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string exportName) in written.Renamed)
        {
            // Written under another name (the package already held one of its own): found by that name.
            if (names.Contains(exportName))
            {
                names.Add(id);
            }
        }

        var missing = new List<ObjectRef>();
        if (layout == SdcLayout.Editor)
        {
            missing.AddRange(p.Brushes.Where(b => !names.Contains(b.Id)).Select(b => ObjectRef.ForBrush(b)));
        }

        missing.AddRange(p.Actors.Where(a => !names.Contains(a.Id) && !written.HandedOff.Contains(a.Id) && (layout == SdcLayout.Editor || !SdcPackagePlan.CookedOut(a, schema)))
            .Select(a => ObjectRef.Actor(a.Id)));
        if (missing.Count > 0)
        {
            f.Add(Finding.Error("sdc.missing",
                $"{missing.Count} objects are not in the written level: {string.Join(", ", missing.Take(10).Select(r => r.Id))}", missing.Take(50).ToArray()));
        }

        // The cooked copy leaves PS2-only GEs out.
        int ges = layout == SdcLayout.Cooked ? p.Ges.Count(g => g.Platform != 1) : p.Ges.Count;
        if (map.LevelRecord.Ges.Count != ges)
        {
            f.Add(Finding.Error("sdc.ges", $"the level holds {map.LevelRecord.Ges.Count} GEs, the map {ges}"));
        }

        foreach ((string id, string exportName) in written.Renamed)
        {
            f.Add(Finding.Info("sdc.renamed", $"{id} is written as {exportName}: the package already holds an object named {id} (one the import leaves out, such as the builder brush)",
                ObjectRef.Actor(id)));
        }

        var untyped = map.Actors.Where(a => a.Problem is not null && (schema?.ResolveClass(a.Class) is not null)).ToList();
        if (untyped.Count > 0)
        {
            f.Add(Finding.Warning("sdc.untyped",
                $"{untyped.Count} written actors do not read back by the class data: {string.Join("; ", untyped.Take(5).Select(a => $"{a.Name}: {a.Problem}"))}",
                untyped.Take(50).Select(a => ObjectRef.Actor(a.Name)).ToArray()));
        }

        if (written.Stats.SourceUsed && written.Stats.Source is { } source && File.Exists(source))
        {
            CompareWithSource(source, pkg, p.Id, layout, schema, f);
        }

        return f;
    }

    /// <summary>What differs from the imported .sdc, by kind (an unedited map writes back the same objects). A cooked
    /// copy written from an editor copy differs by design, so it is not compared.</summary>
    private static void CompareWithSource(string source, UPackage written, string id, SdcLayout layout, UeSchema? schema, List<Finding> f)
    {
        UPackage src = UPackage.Load(source, id);
        if (SdcLayoutDetector.Detect(src) != layout)
        {
            return;
        }

        SdcComparison c = SdcCompare.Compare(src, UPackage.Parse(id, written.Data, 1), schema);
        string summary = $"{c.SameObjects} of {c.Exports} objects of {Path.GetFileName(source)} are written back unchanged ({c.SameBytes} byte for byte)";
        f.Add(c.Identical
            ? Finding.Info("sdc.unchanged", summary + ": the file reads the same as the one imported")
            : Finding.Info("sdc.changes", summary + "; differences: " + string.Join("; ", c.Differences.Take(12))));
    }

    /// <summary>
    /// Lights added, edited (moved, retuned) or deleted, and static meshes added, moved or removed, between a source
    /// package and an editor copy written from it: the written-back lightmaps and lighting instances are the source's,
    /// so they light the map as it was. Null when nothing that bakes changed.
    /// </summary>
    public static Finding? StaleLighting(UPackage source, UPackage written, UeSchema? schema)
    {
        SdcMap before = SdcMapReader.Read(source, schema), after = SdcMapReader.Read(written, schema);
        bool IsLight(SdcActor a) => schema?.IsA(a.Class, "Engine.Light") ?? a.Class.EndsWith("Light", StringComparison.OrdinalIgnoreCase);
        bool IsMesh(SdcActor a) => !IsLight(a) && a.Props["StaticMesh"] is not null;
        Dictionary<string, SdcActor> Baking(SdcMap m) => m.Actors.Where(a => (IsLight(a) || IsMesh(a)) && !SdcMapReader.IsDeleted(a, schema))
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SdcActor> a0 = Baking(before), a1 = Baking(after);
        string Baked(SdcActor a) => a.Class + "#" + BakedState(a, IsLight(a) ? [] : s_meshBakedKeys);

        // A renamed actor is gone under its old name and new under the other; with the same class and baked state it
        // still lights the same, so as many as pair up are left out.
        ILookup<string, SdcActor> gone = a0.Values.Where(x => !a1.ContainsKey(x.Name)).ToLookup(Baked);
        ILookup<string, SdcActor> added = a1.Values.Where(y => !a0.ContainsKey(y.Name)).ToLookup(Baked);
        IEnumerable<SdcActor> Unpaired(ILookup<string, SdcActor> these, ILookup<string, SdcActor> others) =>
            these.SelectMany(g => g.Skip(others[g.Key].Count()));

        var changed = Unpaired(gone, added).Concat(Unpaired(added, gone))
            .Concat(a1.Values.Where(y => a0.TryGetValue(y.Name, out SdcActor? x) && Baked(x) != Baked(y)))
            .ToList();
        var lights = changed.Where(IsLight).Select(a => a.Name).ToList();
        var meshes = changed.Where(a => !IsLight(a)).Select(a => a.Name).ToList();

        if (lights.Count + meshes.Count == 0)
        {
            return null;
        }

        return Finding.Warning("sdc.lightingNotRebuilt",
            $"{lights.Count} lights and {meshes.Count} static meshes were added, edited, moved or deleted since the import: the map's lightmaps and its "
            + "meshes' baked vertex light are written back as they were, so it shows the old lighting until it is rebuilt in the stock editor",
            lights.Concat(meshes).Take(50).Select(ObjectRef.Actor).ToArray());
    }

    /// <summary>What of an actor its baked light depends on: every property of a light, the placement and look of a
    /// mesh (its lighting instance references left out).</summary>
    private static string BakedState(SdcActor a, string[] keys) => string.Join("|", a.Props
        .Where(kv => keys.Length == 0
            ? !kv.Key.StartsWith("StaticMeshInstance", StringComparison.OrdinalIgnoreCase)
            : keys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kv => kv.Key + "=" + OpenScct.Content.Json.CanonicalJson.ToCanonicalString(kv.Value)));

    private static readonly string[] s_meshBakedKeys = ["Location", "Rotation", "DrawScale", "DrawScale3D", "PrePivot", "StaticMesh", "Skins"];

    private static string Name(SdcLayout layout) => layout == SdcLayout.Editor ? "editor copy" : "cooked copy";
}
