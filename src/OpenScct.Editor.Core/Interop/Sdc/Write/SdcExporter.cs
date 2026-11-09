using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// Exports a project as an original map package (.sdc) without the stock editor: the editor copy for
/// Packages\MapsEd (with brushes, as the stock editor saves a map) or the cooked copy the game loads from
/// Packages\Maps. The round-trip lint runs first; the written file is then read back and checked
/// (<see cref="SdcRoundTripLint"/>).
/// </summary>
[EditorService]
public sealed class SdcExporter : IMapExporter
{
    public const string LayoutChoice = "layout";

    public string Name => "sdc";

    public string Extension => ".sdc";

    public TargetGame Target => TargetGame.Original;

    public IReadOnlyList<ExportChoice> Choices { get; } =
    [
        new(LayoutChoice, "Export for", ["cooked", "editor"], "cooked", ["Play in Reloaded (Packages\\Maps)", "Edit in RE+ (Packages\\MapsEd)"]),
    ];

    public static SdcLayout ParseLayout(string value) => value.ToLowerInvariant() switch
    {
        "editor" => SdcLayout.Editor,
        "cooked" => SdcLayout.Cooked,
        _ => throw new ArgumentException($"unknown .sdc layout '{value}' (editor or cooked)"),
    };

    /// <summary>Applies known replacements, checks the complete package, then publishes it only without errors.</summary>
    public ExportResult Export(MapProject project, string path, ExportOptions options)
    {
        ExportResult result;
        try
        {
            result = ExportChecked(project, path, options);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or SdcWriteRefusedException)
        {
            result = new ExportResult(false, [], [Finding.Error("sdc.export", ex.Message
                + " Resolve the reported file, install or geometry problem and export again; the previous SDC is preserved.")]);
        }
        result = result with { Findings = result.Findings.Select(SdcExportReport.WithWorkaround).ToArray() };
        // Keep a readable report next to successful and refused exports, including for CLI callers.
        try
        {
            string report = Path.GetFullPath(path) + ".export-report.md";
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, SdcExportReport.Text(result));
            result = result with { Files = result.Files.Append(report).ToArray() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result = result with { Findings = result.Findings.Append(Finding.Warning("sdc.report",
                $"Could not save the export report: {ex.Message}. The complete findings are available in Map Check and the export result.")).ToArray() };
        }
        return result;
    }

    private ExportResult ExportChecked(MapProject project, string path, ExportOptions options)
    {
        if (project.Actors.Any(a => a.Door is not null))
            return new ExportResult(false, [], [Finding.Error("door.originalUnsupported", "Editable doors currently require an OpenSCCT build")]);
        if (project.Actors.Any(a => MoverBase.IsMover(a) && OpenScct.Editor.Core.Gameplay.MoverSetup.Read(a).Keys.Count > 8))
            return new ExportResult(false, [], [Finding.Error("mover.original-limit", "This map uses mover paths longer than eight stops. Build for OpenSCCT, or shorten those paths before exporting to the original game.")]);

        var findings = new List<Finding>();
        string? gameRoot = GameRoot(project, path, options, findings);
        options.Progress?.Invoke("applying original-game compatible replacements");
        project = SdcCompatibility.Prepare(project, options.ContentRoot ?? EditorServices.ContentRoot, gameRoot, findings);
        try
        {
            project = Stairs.StairGeometry.Lower(OpenScct.Editor.Core.Gameplay.GeGeometry.Expand(project));
        }
        catch (ArgumentException ex)
        {
            findings.Add(Finding.Error("ge.geometry", ex.Message + " Correct the listed traversal or stair geometry and export again."));
            return new ExportResult(false, [], findings);
        }
        SdcLayout layout = ParseLayout(options.Choice(this, LayoutChoice));
        var destination = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (layout == SdcLayout.Editor && destination.Name.Equals("Maps", StringComparison.OrdinalIgnoreCase)
            && destination.Parent?.Name.Equals("Packages", StringComparison.OrdinalIgnoreCase) == true)
        {
            findings.Add(Finding.Error("sdc.editorInMaps",
                "An editor copy cannot be played by Reloaded. Choose Play in Reloaded (cooked), or save the editor copy in Packages\\MapsEd."));
        }
        UeSchema? schema = UeSchema.Load(options.ContentRoot ?? EditorServices.ContentRoot);
        options.Progress?.Invoke("checking round-trip compatibility");
        bool fromSource = project.Source.Kind == "sdc";
        string packageName = Path.GetFileNameWithoutExtension(path);
        Lint(project, schema, fromSource, packageName, findings);
        if (findings.Any(f => f.Severity == Severity.Error)) return new ExportResult(false, [], findings);

        options.Progress?.Invoke("packaging assets for the destination installation");
        SdcAssetBundle assets = SdcAssetBundle.Prepare(project, path, gameRoot, options, schema, findings);
        if (findings.Any(f => f.Severity == Severity.Error)) return new ExportResult(false, [], findings);

        findings.Add(Finding.Info("sdc.mapSettings",
            "map settings per mode, interface pictures and briefing texts live in the <Map>-i.utc package, which this export does not write"));

        if (!packageName.Equals(project.Id, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding.Info("sdc.packageName",
                $"the package is named {packageName} after its file; the game and the stock editor find a map by that name, not by the project's id {project.Id}"));
        }

        options.Progress?.Invoke($"writing {Path.GetFileName(path)} ({layout.ToString().ToLowerInvariant()} copy)");
        var writeOptions = new SdcWriteOptions
        {
            ProjectFolder = options.ProjectFolder,
            Layout = layout,
            Schema = schema,
            ContentRoot = options.ContentRoot,
            GameRoot = gameRoot,
            Assets = assets,
            Progress = options.Progress,
        };
        SdcWriteResult result;
        try
        {
            result = SdcWriter.Write(project, packageName, writeOptions);
        }
        catch (SdcWriteRefusedException ex)
        {
            findings.Add(Finding.Error("sdc.refused", ex.Message));
            return new ExportResult(false, [], findings);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            findings.Add(Finding.Error("sdc.materialFailed", "Material export failed: " + ex.Message));
            return new ExportResult(false, [], findings);
        }

        findings.AddRange(result.Findings);
        if (fromSource && !result.WorldRebuilt) StaleWorld(project, findings);
        options.Progress?.Invoke("checking the generated package before saving");
        findings.AddRange(SdcRoundTripLint.Check(project, result, packageName, layout, schema));
        if (!result.WorldRebuilt && result.Stats.SourceUsed && result.Stats.Source is { } source)
        {
            StaleLighting(project, packageName, writeOptions, result, source, schema, findings);
        }

        bool ok = !findings.Any(f => f.Severity == Severity.Error);
        if (!ok) return new ExportResult(false, [], findings);
        string fullPath = Path.GetFullPath(path);
        if (assets.FilePath is { } assetFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(assetFile)!);
            if (File.Exists(assetFile))
            {
                if (!File.ReadAllBytes(assetFile).AsSpan().SequenceEqual(assets.Package))
                    throw new IOException($"The asset package {assetFile} already exists with different data. It was not overwritten.");
            }
            else
            {
                string staged = assetFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(staged, assets.Package);
                    try { File.Move(staged, assetFile); }
                    catch (IOException) when (File.Exists(assetFile) && File.ReadAllBytes(assetFile).AsSpan().SequenceEqual(assets.Package)) { }
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, UPackageBuilder.Pack(result.Package));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new ExportResult(true, assets.FilePath is { } bundled ? [fullPath, bundled] : [fullPath], findings);
    }

    /// <summary>
    /// The round-trip lint as it applies to a package. Some of it is about what T3D cannot carry: the package holds GE
    /// ids and platforms, material paths need no quoting, and an imported map's baked world and own classes are written
    /// back from its .sdc. The package stores face corners as floats, so a face a little off flat is a warning, and the
    /// faces of brushes unedited since the import are the stock map's own, written back as stored. The map name rule
    /// applies to the package (the file's name).
    /// </summary>
    private static void Lint(MapProject project, UeSchema? schema, bool fromSource, string packageName, List<Finding> findings)
    {
        var unedited = new HashSet<string>(project.Brushes.Where(b => !BakedContent.IsRecovered(b) && BakedContent.ImportedHash(b) is { } h && h == BakedContent.Hash(b))
            .Select(b => b.Id),
            StringComparer.OrdinalIgnoreCase);
        int stockFaces = 0;
        foreach (Finding f in RoundTripLint.Check(project, schema))
        {
            // The package carries the map's own classes (an imported map's are written back from its .sdc), and the writer
            // says itself how it writes conveyors (sdc.conveyorBelts, sdc.conveyorsLeftOut).
            bool t3dOnly = f.Code is "roundtrip.geIds" or "roundtrip.mapSettings" or "roundtrip.materialName" or "roundtrip.mapName"
                or "roundtrip.conveyorClasses"
                || (fromSource && f.Code is "roundtrip.bakedWorld" or "roundtrip.mapClass");
            if (f.Code == "roundtrip.liftedData")
            {
                // The .sdc writer writes RE+'s outfit and player-count actors as the project holds them (as imported).
                findings.Add(f with
                {
                    Severity = Severity.Warning,
                    Message = f.Message.Replace("the export writes them into", "the .sdc export does not write them yet: it keeps", StringComparison.Ordinal)
                        + " as imported (the export for the original editor writes the changes)",
                });
                continue;
            }
            bool faces = f.Code is "roundtrip.nonPlanarFace" or "roundtrip.nonConvexFace" or "roundtrip.degenerateFace" or "roundtrip.sliver";
            if (t3dOnly)
            {
                continue;
            }

            if (faces && f.Objects.Count > 0 && f.Objects.All(o => unedited.Contains(o.Id)))
            {
                stockFaces++;
            }
            else
            {
                findings.Add(f.Code == "roundtrip.nonPlanarFace" && f.Severity == Severity.Error ? f with { Severity = Severity.Warning } : f);
            }
        }

        if (stockFaces > 0)
        {
            findings.Add(Finding.Info("sdc.stockFaces",
                $"{stockFaces} brushes unedited since the import have faces the lint flags (off flat, not convex or with tiny edges): they are the stock map's own, written back as stored"));
        }

        if (!Limits.IsMapName(packageName))
        {
            findings.Add(Finding.Warning("sdc.mapName",
                $"the original game lists maps by package name, and the stock maps' names are 5 letters or digits (e.g. ShipD); this file names the package '{packageName}'"));
        }
    }

    /// <summary>A written-back map keeps its source's world (BSP, collision, lightmaps), so brushes added or edited since the
    /// import are in the file as brushes only until the map is rebuilt.</summary>
    private static void StaleWorld(MapProject p, List<Finding> findings)
    {
        Brush[] changed = p.Brushes.Where(b => !BakedContent.IsBaked(b) || BakedContent.ImportedHash(b) != BakedContent.Hash(b)).ToArray();
        if (changed.Length > 0)
        {
            findings.Add(Finding.Warning("sdc.worldNotRebuilt",
                $"{changed.Length} brushes were added or edited since the import: the world (BSP, collision, lightmaps) is written back as it was, "
                + "so they show in the game only after a rebuild in the stock editor",
                changed.Take(50).Select(b => ObjectRef.ForBrush(b)).ToArray()));
        }
    }

    /// <summary>
    /// A written-back map keeps its source's lightmaps and its meshes' baked vertex light (StaticMeshInstances), so
    /// lights added, moved, edited or deleted since the import, and meshes moved, show the old lighting until the map is
    /// rebuilt. The lights are compared on an editor copy (a cooked copy leaves static lights out): the written one,
    /// else one written in memory for the check.
    /// </summary>
    private static void StaleLighting(MapProject p, string packageName, SdcWriteOptions options, SdcWriteResult result, string source,
        UeSchema? schema, List<Finding> findings)
    {
        UPackage src = UPackage.Load(source, p.Id);
        bool editorSource = SdcLayoutDetector.Detect(src) == SdcLayout.Editor;
        byte[] package = options.Layout == SdcLayout.Editor || !editorSource
            ? result.Package
            : SdcWriter.Write(p, packageName, options with { Layout = SdcLayout.Editor, Progress = null }).Package;
        if (SdcRoundTripLint.StaleLighting(src, UPackage.Parse(p.Id, package, 1), schema) is { } stale)
        {
            findings.Add(stale);
        }
    }

    /// <summary>The install the map's new references are looked up in: given, else the one the source .sdc sits in,
    /// else SVM_GAME_ROOT or the default install; null (with a warning) when there is none.</summary>
    private static string? GameRoot(MapProject p, string path, ExportOptions options, List<Finding> findings)
    {
        try
        {
            string? destination = SdcAssetBundle.DestinationRoot(path);
            if (destination is not null && File.Exists(Path.Combine(destination, "System", "_PC_", "Core.u")))
                return SdcImporter.GameRoot(null, destination);
            return SdcImporter.GameRoot(p.Source.Kind == "sdc" ? p.Source.File : null, options.GameRoot);
        }
        catch (DirectoryNotFoundException)
        {
            findings.Add(Finding.Warning("sdc.noInstall",
                "no original install found (give it with --game or SVM_GAME_ROOT): materials, meshes and sounds the map did not "
                + "already use are written as None"));
            return null;
        }
    }
}
