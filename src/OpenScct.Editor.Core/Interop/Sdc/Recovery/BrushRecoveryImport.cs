using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc.Recovery;

/// <summary>
/// The brush recovery step of a cooked .sdc import (<see cref="SdcImporter"/>): the brushes the cook threw away are
/// recovered from the compiled BSP (<see cref="BrushRecovery"/>) and added as ordinary editable brushes, in the
/// <see cref="BrushRecovery.Folder"/> layer (sheets and zone portals in folders of their own).
/// <para>
/// The baked world stays as the import made it (render, collision and lightmaps kept): the recovered brushes are the
/// brushes it is made of, marked baked as an editor copy's brushes are, so the map plays as the original until the
/// baked world is set to rebuild from them. Recovered brushes reproduce the original's solid and empty space and its
/// materials closely, but not its lightmaps, and the editor's compiler splits a few places differently (see
/// docs/SDC-FORMAT.md, section 8).
/// </para>
/// <para>A recovery that fails leaves the project as it was (the baked world only) with a warning: it never stops the
/// import.</para>
/// </summary>
public static class BrushRecoveryImport
{
    public const string RecoveredCode = "import.sdcBrushes", ThinCode = "import.sdcBrushSlivers", UnsurfacedCode = "import.sdcBrushUnsurfaced",
        DiffersCode = "import.sdcBrushesDiffer", FailedCode = "import.sdcBrushesFailed", OffCode = "import.sdcBrushesOff",
        KeptCode = "import.sdcBrushesKept", EditorCopyCode = "import.sdcBrushesEditorCopy";

    /// <summary>The finding codes of this step (all in the import report's Recovered section).</summary>
    public static readonly IReadOnlyList<string> Codes = [RecoveredCode, ThinCode, UnsurfacedCode, DiffersCode, FailedCode, OffCode, KeptCode, EditorCopyCode];

    /// <summary>The outliner folders (in the <see cref="BrushRecovery.Folder"/> layer) of recovered sheets and zone
    /// portals; the structural brushes sit in the layer itself.</summary>
    public const string SheetsFolder = "Sheets", PortalsFolder = "Zone portals";

    /// <summary>The share of probes the brushes a cooked file kept may build differently from its compiled BSP and still
    /// count as the map's own brushes (no recovery).</summary>
    public const double KeptMissShare = 0.01;

    /// <summary>A recovery mode by name: off, cells or merge (null: merge, the default).</summary>
    public static BrushRecoveryMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "merge" or "on" => BrushRecoveryMode.Merge,
        "cells" => BrushRecoveryMode.Cells,
        "off" => BrushRecoveryMode.Off,
        _ => throw new ArgumentException($"brush recovery '{value}' is off, cells or merge"),
    };

    /// <summary>True for a brush recovered from a compiled BSP (its source has <c>"recovered"</c>).</summary>
    public static bool IsRecovered(Brush b) => BakedContent.IsRecovered(b);

    /// <summary>Recovers the brushes of a cooked map into <paramref name="p"/>. A cooked file that kept brushes of its
    /// own (a few do) is recovered too unless those brushes, built by the editor's compiler, make up its compiled BSP
    /// (all but <see cref="KeptMissShare"/> of the probes beside its surfaces match); kept brushes stay, after the
    /// recovered ones. Nothing when <paramref name="mode"/> is Off.</summary>
    public static void Run(MapProject p, UPackage pkg, SdcLayout layout, BrushRecoveryMode mode, Action<string>? progress, List<Finding> findings)
    {
        if (mode == BrushRecoveryMode.Off)
        {
            TurnedOff(p, findings);
            return;
        }

        CookedBsp bsp;
        try
        {
            bsp = ReadBsp(pkg, layout);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            Failed(p, ex, findings);
            return;
        }

        Run(p, bsp, mode, progress, findings);
    }

    /// <summary>The compiled BSP of a map package's level.</summary>
    public static CookedBsp ReadBsp(UPackage pkg, SdcLayout layout)
    {
        int model = ULevelReader.Read(pkg, ULevelReader.Find(pkg)).WorldModel;
        if (model <= 0)
        {
            throw new InvalidDataException("the level has no world model");
        }

        return CookedBsp.Read(pkg, pkg.Export(model), layout);
    }

    /// <summary>Recovers the brushes of a compiled BSP into <paramref name="p"/> (see the overload that reads it).</summary>
    public static void Run(MapProject p, CookedBsp bsp, BrushRecoveryMode mode, Action<string>? progress, List<Finding> findings)
    {
        if (mode == BrushRecoveryMode.Off)
        {
            TurnedOff(p, findings);
            return;
        }

        List<Brush> kept = Kept(p);
        RecoveredBrushes recovered;
        string? keptNote = null;
        try
        {
            if (kept.Count > 0)
            {
                progress?.Invoke($"recovering brushes: checking the {kept.Count} brushes the file kept");
                RebuildComparison c = BrushRecoveryCheck.Compare(bsp, kept, grid: 0);
                if (c.Misses <= c.Probes * KeptMissShare)
                {
                    findings.Add(Finding.Info(KeptCode,
                        $"the file kept its {kept.Count} brushes, and the editor's compiler builds them as its compiled BSP ({c.Probes - c.Misses} of "
                        + $"{c.Probes} probes beside the original surfaces match): they are the map's brushes, so none were recovered"));
                    return;
                }

                keptNote = $"the file also kept {kept.Count} {(kept.Count == 1 ? "brush" : "brushes")} of its own, which "
                    + $"{(kept.Count == 1 ? "does" : "do")} not make up its world (built alone, {c.Misses} of {c.Probes} probes beside the original "
                    + $"surfaces differ from the compiled BSP): {(kept.Count == 1 ? "it stays" : "they stay")}, after the recovered brushes";
            }

            recovered = BrushRecovery.Recover(bsp, new BrushRecoveryOptions { Mode = mode, Progress = progress });
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            Failed(p, ex, findings);
            return;
        }

        Add(p, recovered, findings);
        if (keptNote is not null)
        {
            findings.Add(Finding.Info(KeptCode, keptNote, [.. kept.Select(b => ObjectRef.Brush(b.Id))]));
        }
    }

    /// <summary>The brushes the file itself kept (baked, not recovered).</summary>
    private static List<Brush> Kept(MapProject p) => p.Brushes.Where(b => BakedContent.IsBaked(b) && !IsRecovered(b)).ToList();

    private static void TurnedOff(MapProject p, List<Finding> findings)
    {
        int kept = Kept(p).Count;
        findings.Add(Finding.Info(OffCode, kept > 0
            ? $"brush recovery was turned off, so the map has only the {kept} brushes the file kept; import the map again with brush "
              + "recovery on (or run ose sdc-brushes on the project) to recover the rest of its world as brushes"
            : "brush recovery was turned off, so the map has no brushes: its world is the baked layer only; import the map again with "
              + "brush recovery on (or run ose sdc-brushes on the project) to edit it as brushes"));
    }

    private static void Failed(MapProject p, Exception ex, List<Finding> findings)
    {
        int kept = Kept(p).Count;
        findings.Add(Finding.Warning(FailedCode,
            $"the brushes could not be recovered from the compiled BSP ({ex.GetType().Name}: {ex.Message}), so the map has "
            + (kept > 0 ? $"only the {kept} brushes the file kept" : "no brushes")
            + ": its world is the baked layer, which plays as the original but cannot be edited as brushes"));
    }

    /// <summary>Takes the recovered brushes out of a project (to recover them again): their ids, layer and report go.
    /// Returns how many were removed.</summary>
    public static int Remove(MapProject p)
    {
        var ids = new HashSet<string>(p.Brushes.Where(IsRecovered).Select(b => b.Id), StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0 && !p.Extra.ContainsKey(BrushRecovery.ReportKey))
        {
            return 0;
        }

        p.Brushes.RemoveAll(b => ids.Contains(b.Id));
        if (p.Baked?.Brushes is { } listed)
        {
            p.Baked.Brushes = [.. listed.Where(id => !ids.Contains(id))];
        }

        p.Extra = Without(p.Extra, BrushRecovery.ReportKey);
        return ids.Count;
    }

    /// <summary>Points at the map's editor copy when the light recovery found the one of this exact bake: its brushes
    /// are the author's (the original split, semisolid brushes), which a recovery cannot bring back.</summary>
    public static void NoteEditorCopy(MapProject p, string? twin, bool exact, List<Finding> findings)
    {
        if (twin is null || !exact || !p.Brushes.Any(IsRecovered))
        {
            return;
        }

        findings.Add(Finding.Info(EditorCopyCode,
            $"the editor copy of this exact map was found ({twin}): import it instead for the author's own brushes (their original split and "
            + "semisolid brushes, which a recovery from the compiled BSP cannot bring back)"));
    }

    /// <summary>Adds recovered brushes in front of the project's other brushes (CSG order: the world before what was
    /// added to it), with their report.</summary>
    public static void Add(MapProject p, RecoveredBrushes recovered, List<Finding> findings)
    {
        BrushRecoveryReport report = recovered.Report;
        var used = new HashSet<string>(p.Brushes.Select(b => b.Id).Concat(p.Actors.Select(a => a.Id)), StringComparer.OrdinalIgnoreCase);
        var origin = new SvJsonObject();
        origin.Add("mode", SvJsonNode.String(report.Mode.ToString().ToLowerInvariant()));
        string originJson = InteropValues.Compact(origin);
        foreach (Brush b in recovered.Brushes)
        {
            string id = b.Id;
            for (int n = 2; used.Contains(id); n++)
            {
                id = $"{b.Id}_{n}";
            }

            used.Add(id);
            b.Id = id;

            bool sheet = (b.PolyFlags & PolyFlags.NotSolid) != 0;
            b.Editor.Layer = BrushRecovery.Folder;
            b.Editor.Folder = (b.PolyFlags & CookedBsp.PortalFlag) != 0 ? PortalsFolder : sheet ? SheetsFolder : "";
            var sdc = new SvJsonObject();
            sdc.Add("hash", SvJsonNode.String(BakedContent.Hash(b)));
            var src = new SvJsonObject();
            src.Add("recovered", SvJson.Parse(originJson));
            src.Add("sdc", sdc);
            if (p.Baked is not null)
            {
                src.Add("baked", SvJsonNode.True);
            }

            b.Source = InteropValues.Compact(src);
        }

        p.Brushes.InsertRange(0, recovered.Brushes);
        if (p.Baked is not null)
        {
            p.Baked.Brushes = [.. recovered.Brushes.Select(b => b.Id), .. p.Baked.Brushes ?? []];
        }

        if (!p.Layers.Any(l => l.Name.Equals(BrushRecovery.Folder, StringComparison.OrdinalIgnoreCase)))
        {
            p.Layers.Add(new Layer(BrushRecovery.Folder) { Color = "#7A8F5A" });
        }

        p.Extra = Without(p.Extra, BrushRecovery.ReportKey);
        p.Extra.Add(BrushRecovery.ReportKey, report.ToJson());

        findings.Add(Finding.Info(RecoveredCode, $"{report.Summary()}, in the {BrushRecovery.Folder} layer",
            [.. recovered.Brushes.Select(b => ObjectRef.Brush(b.Id))]));
        if (p.Baked is { KeepRender: true } or { KeepCollision: true })
        {
            findings.Add(Finding.Info(RecoveredCode,
                "the baked world (render, collision, lightmaps) is kept and plays as the original; the recovered brushes are what it is made "
                + "of: Map Settings > Baked world > Build both from the brushes builds the map from them (and bakes its lightmaps again)"));
        }

        if (report.Thin.Count > 0)
        {
            findings.Add(Finding.Warning(ThinCode,
                $"{report.Thin.Count} recovered {(report.Thin.Count == 1 ? "brush is" : "brushes are")} thinner than {F(BrushRecovery.ThinWidth)} unit "
                + $"or smaller than {F(BrushRecovery.ThinVolume)} cubic units: slivers of the compiled BSP's splits, which build as they are but are "
                + $"hard to edit{(report.SliversDropped > 0 ? $" ({report.SliversDropped} thinner than {F(BrushRecovery.SliverWidth)} were left out)" : "")}",
                [.. report.Thin.Select(t => ObjectRef.Brush(t.Brush.Id)).Distinct()]));
        }
        else if (report.SliversDropped > 0)
        {
            findings.Add(Finding.Info(ThinCode,
                $"{report.SliversDropped} slivers of the compiled BSP thinner than {F(BrushRecovery.SliverWidth)} units were left out of the recovered brushes"));
        }

        if (report.Unsurfaced.Count > 0)
        {
            List<string> ids = report.Unsurfaced.Select(u => u.Brush.Id).Distinct().ToList();
            int n = report.Unsurfaced.Count;
            Vec3 largest = report.Unsurfaced.MaxBy(u => u.Area).Centre;
            findings.Add(Finding.Info(UnsurfacedCode,
                $"{n} {(n == 1 ? "part" : "parts")} of the faces of {ids.Count} recovered {(ids.Count == 1 ? "brush" : "brushes")} "
                + $"({F(report.Unsurfaced.Sum(u => u.Area), "0.##")} square units in all, the largest at {largest}) {(n == 1 ? "lies" : "lie")} "
                + "between solid and empty space where the compiled map drew no surface: they are invisible, as in the original, with the "
                + "material of their neighbours",
                [.. ids.Select(ObjectRef.Brush)]).At(largest));
        }

        if (report.Misses > 0)
        {
            Finding f = Finding.Warning(DiffersCode,
                $"the editor's compiler builds the recovered brushes slightly differently from the compiled map at {report.Misses} of "
                + $"{report.Probes} probes beside its surfaces (first at {report.MissPoints.FirstOrDefault()}): a rebuild may differ there");
            findings.Add(report.MissPoints.Count > 0 ? f.At(report.MissPoints[0]) : f);
        }

        foreach (string note in report.Notes)
        {
            findings.Add(Finding.Info(RecoveredCode, note));
        }
    }

    private static string F(double v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);

    private static SvJsonObject Without(SvJsonObject o, string key)
    {
        var copy = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            if (kv.Key != key)
            {
                copy.Add(kv.Key, kv.Value);
            }
        }

        return copy;
    }
}
