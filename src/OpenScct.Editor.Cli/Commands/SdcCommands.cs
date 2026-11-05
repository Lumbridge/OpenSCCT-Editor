using OpenScct.Content.Json;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Interop.Sdc.Lights;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose sdc-info</c>: how the native .sdc reader sees a file, to trace an import; <c>ose sdc-lights</c>: the
/// light recovery of a cooked copy; <c>ose sdc-brushes</c>: the brush recovery of a compiled map.</summary>
public static class SdcCommands
{
    [CliProvider]
    public static IEnumerable<CliCommand> Commands() =>
    [
        new CliCommand("sdc-info",
            "sdc-info <file.sdc> [--content <.content>] [--json]",
            "Reads an original map package natively and reports its layout (editor copy or cooked copy), version and zlib "
            + "chunks, actors per class, brushes and faces, whether every Model, Polys and StaticMeshInstance export reads to "
            + "its last byte, the world model and the GE count.",
            InfoAsync),
        new CliCommand("sdc-lights",
            "sdc-lights <cooked.sdc | project.osmap> [--mode off|exact|full] [--twins <folder;...>] [--no-twin] [--force-fit] [--fit-seconds N] "
            + "[--truth <editor copy.sdc>] [--out <project.osmap>] [--save] [--content <.content>] [--game <install>] [--json]",
            "Brings back the static lights the stock cook stripped from a cooked .sdc (an import, or a project imported from one), "
            + "best source first: the editor copy whose lightmap atlases are the map's byte for byte (MapsEd and autosaves of "
            + "every install found, cached by fingerprint), lights the file still holds outside the level; in full mode (the "
            + "default) also an editor copy of another bake checked light by light against the cooked lightmaps, then stand-in "
            + "lights fitted to the bake when what is left is more than noise (the fit gate; --force-fit fits anyway). Prints "
            + "what came back, the rebake error against the cooked atlases before and after, and each light's source and "
            + "confidence. --no-twin ignores editor copies (to measure the fit); --truth compares the result with an editor "
            + "copy's lost lights (recall, precision, position error, stand-in precision by peak). --fit-seconds caps the fit "
            + "(none by default: a cap makes the result depend on the machine). --out writes the project; --save writes a "
            + "project given back. A cooked .sdc is imported without brush recovery (see sdc-brushes).",
            LightsAsync),
        new CliCommand("sdc-brushes",
            "sdc-brushes <file.sdc | project.osmap> [--mode cells|merge] [--no-check] [--truth <editor copy.sdc;...>] [--grid N] [--save] "
            + "[--content <.content>] [--json]",
            "Recovers editable brushes from a map package's compiled BSP (the brush recovery a cooked import runs) without "
            + "importing the rest of the map, and prints what it made: cells, joined and grown brushes, split-backs, sheets "
            + "and portals, thin brushes, faces no surface covers, and how the brushes built by the editor's compiler compare "
            + "with the compiled BSP (probes beside every original surface, a grid of points through the map, drawn area per "
            + "material). --mode cells joins only cells that share a whole face (merge, the default, also grows brushes over "
            + "their neighbours). --no-check skips the rebuild check during the recovery. --truth builds the brushes of other "
            + "copies of the map (an editor copy of the same save, or another tool's recovery) and compares them the same "
            + "way. --grid sets the sample points per axis (48). Given a project imported from a cooked .sdc (its map.json "
            + "source.file still there), recovers its brushes again from that file, in place of the ones recovered before (for a "
            + "project imported with brush recovery off or by an older version); --save writes the project.",
            BrushesAsync),
    ];

    private static Task<int> BrushesAsync(CliArgs a)
    {
        string file = a.Positional(0) ?? throw new ArgumentException("give the .sdc file or a project imported from one");
        if (ProjectStore.IsProject(file))
        {
            return RecoverIntoProject(a, Path.GetFullPath(file));
        }

        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"'{file}' does not exist");
        }

        BrushRecoveryMode mode = BrushRecoveryImport.ParseMode(a.Get("mode"));
        if (mode == BrushRecoveryMode.Off)
        {
            throw new ArgumentException("--mode is cells or merge");
        }

        bool json = a.Has("json");
        int grid = a.GetInt("grid", 48);
        UPackage pkg = UPackage.Load(Path.GetFullPath(file));
        SdcLayout layout = SdcLayoutDetector.Detect(pkg);
        int model = ULevelReader.Read(pkg, ULevelReader.Find(pkg)).WorldModel;
        if (model <= 0)
        {
            throw new InvalidDataException($"{file} has no world model");
        }

        CookedBsp bsp = CookedBsp.Read(pkg, pkg.Export(model), layout);
        RecoveredBrushes recovered = BrushRecovery.Recover(bsp, new BrushRecoveryOptions
        {
            Mode = mode,
            Verify = !a.Has("no-check"),
            Progress = json ? null : line => Console.Error.WriteLine("  " + line),
        });
        var result = new SvJsonObject();
        result.Add("file", SvJsonNode.String(Path.GetFullPath(file)));
        result.Add("layout", SvJsonNode.String(layout.ToString().ToLowerInvariant()));
        result.Add("report", recovered.Report.ToJson(times: true));
        result.Add("rebuild", Describe(BrushRecoveryCheck.Compare(bsp, recovered.Brushes, grid)));
        result.Add("shapes", Shapes(recovered.Brushes));
        if (a.Get("truth") is { } truths)
        {
            string? content = a.Get("content") is { } c ? Path.GetFullPath(c) : EditorServices.ContentRoot ?? ContentLocator.FindContentRoot();
            UeSchema? schema = UeSchema.Load(content);
            var list = new SvJsonArray();
            foreach (string truth in Folders(truths))
            {
                UPackage tp = UPackage.Load(Path.GetFullPath(truth));
                SdcMap map = SdcMapReader.Read(tp, schema);
                List<Brush> brushes = SdcOverlay.ReadBrushes(map, []).Select(x => x.Brush).ToList();
                SvJsonObject t = Describe(BrushRecoveryCheck.Compare(bsp, brushes, grid));
                t.Add("file", SvJsonNode.String(Path.GetFullPath(truth)));
                list.Add(t);
            }

            result.Add("truth", list);
        }

        if (json)
        {
            Console.WriteLine(CanonicalJson.ToCanonicalString(result));
            return Task.FromResult(0);
        }

        BrushRecoveryReport report = recovered.Report;
        Console.WriteLine($"{report.Summary()} ({report.TotalSeconds:0.0} s: "
            + string.Join(", ", report.Seconds.Select(kv => $"{kv.Key} {kv.Value:0.0}")) + ")");
        Console.WriteLine($"  cells: {report.EmptyLeaves} empty and {report.SolidLeaves} solid leaves; {report.UnboundedCells} open to the outside, "
            + $"{report.DegenerateCells} degenerate, {report.SliversDropped} slivers left out; {report.Collapsed} brushes collapsed at float precision");
        Console.WriteLine($"  {report.Thin.Count} thin brushes, {report.Unsurfaced.Count} faces with no surface; probes differing: first build "
            + $"{report.FirstMisses}, last {report.Misses} after {report.Attempts} builds");
        foreach (string note in report.Notes)
        {
            Console.WriteLine("  " + note);
        }

        Console.WriteLine("  rebuilt: " + Line((SvJsonObject)result["rebuild"]!));
        if (result["truth"] is SvJsonArray ts)
        {
            foreach (SvJsonObject t in ts.OfType<SvJsonObject>())
            {
                Console.WriteLine($"  {Path.GetFileName(((SvJsonString)t["file"]!).Value)}: " + Line(t));
            }
        }

        return Task.FromResult(0);
    }

    /// <summary>Recovers a project's brushes again from the cooked .sdc it was imported from (the import's step, with
    /// its findings), replacing those recovered before.</summary>
    private static Task<int> RecoverIntoProject(CliArgs a, string folder)
    {
        BrushRecoveryMode mode = BrushRecoveryImport.ParseMode(a.Get("mode"));
        if (mode == BrushRecoveryMode.Off)
        {
            throw new ArgumentException("--mode is cells or merge");
        }

        bool json = a.Has("json");
        MapProject p = ProjectStore.Load(folder);
        if (p.Source is not { Kind: "sdc", File: { } sdc } || !File.Exists(sdc))
        {
            throw new InvalidOperationException("the project was not imported from an .sdc file that is still there (map.json source.file)");
        }

        UPackage pkg = UPackage.Load(sdc);
        SdcLayout layout = SdcLayoutDetector.Detect(pkg);
        if (layout != SdcLayout.Cooked)
        {
            throw new InvalidOperationException($"{sdc} is an editor copy: the project has the map's own brushes");
        }

        int removed = BrushRecoveryImport.Remove(p);
        var findings = new List<Finding>();
        BrushRecoveryImport.Run(p, pkg, layout, mode, json ? null : line => Console.Error.WriteLine("  " + line), findings);
        if (a.Has("save"))
        {
            ProjectStore.Save(p, folder);
        }

        if (json)
        {
            var o = new SvJsonObject();
            o.Add("project", SvJsonNode.String(folder));
            o.Add("source", SvJsonNode.String(sdc));
            o.Add("removed", SvJsonNode.Integer(removed));
            o.Add("brushes", SvJsonNode.Integer(p.Brushes.Count(BrushRecoveryImport.IsRecovered)));
            o.Add("saved", SvJsonNode.Bool(a.Has("save")));
            o.Add("findings", new SvJsonArray(findings.Select(f =>
            {
                var x = new SvJsonObject();
                x.Add("severity", SvJsonNode.String(f.Severity.ToString().ToLowerInvariant()));
                x.Add("code", SvJsonNode.String(f.Code));
                x.Add("message", SvJsonNode.String(f.Message));
                return (SvJsonNode)x;
            })));
            Console.WriteLine(CanonicalJson.ToCanonicalString(o));
            return Task.FromResult(0);
        }

        Console.WriteLine($"{folder}: {removed} recovered brushes replaced from {sdc}{(a.Has("save") ? "; saved" : " (not saved: --save writes the project)")}");
        foreach (Finding f in findings)
        {
            Console.WriteLine($"  {f.Severity.ToString().ToLowerInvariant()} {f.Code}: {f.Message}");
        }

        return Task.FromResult(0);
    }

    /// <summary>What Map Check's shape rules would say of the brushes: faces with no area, not flat or not convex,
    /// shapes not closed, not convex or turned inside out.</summary>
    private static SvJsonObject Shapes(IReadOnlyList<Brush> brushes)
    {
        int zero = 0, nonPlanar = 0, nonConvexFaces = 0, open = 0, nonConvex = 0, inward = 0;
        foreach (Brush b in brushes)
        {
            ShapeReport r = ShapeAnalysis.Analyze(b.Shape);
            zero += r.ZeroAreaFaces.Count;
            nonPlanar += r.NonPlanarFaces.Count;
            nonConvexFaces += r.NonConvexFaces.Count;
            if (!r.IsSheet)
            {
                open += r.Closed ? 0 : 1;
                nonConvex += r.Convex ? 0 : 1;
                inward += r.Outward ? 0 : 1;
            }
        }

        var o = new SvJsonObject();
        o.Add("zeroAreaFaces", SvJsonNode.Integer(zero));
        o.Add("nonPlanarFaces", SvJsonNode.Integer(nonPlanar));
        o.Add("nonConvexFaces", SvJsonNode.Integer(nonConvexFaces));
        o.Add("open", SvJsonNode.Integer(open));
        o.Add("nonConvex", SvJsonNode.Integer(nonConvex));
        o.Add("inward", SvJsonNode.Integer(inward));
        return o;
    }

    private static SvJsonObject Describe(RebuildComparison c)
    {
        var o = new SvJsonObject();
        o.Add("brushes", SvJsonNode.Integer(c.Brushes));
        o.Add("probes", SvJsonNode.Integer(c.Probes));
        o.Add("misses", SvJsonNode.Integer(c.Misses));
        o.Add("samples", SvJsonNode.Integer(c.Samples));
        o.Add("samplesDiffer", SvJsonNode.Integer(c.SamplesDiffer));
        o.Add("cookedArea", SvJsonNode.Integer((long)Math.Round(c.CookedArea)));
        o.Add("rebuiltArea", SvJsonNode.Integer((long)Math.Round(c.RebuiltArea)));
        o.Add("materialAreaOff", SvJsonNode.Float((float)c.MaterialAreaOff));
        o.Add("covered", SvJsonNode.Float((float)c.Covered));
        o.Add("wrongMaterial", SvJsonNode.Float((float)c.WrongMaterial));
        o.Add("uncovered", SvJsonNode.Float((float)c.Uncovered));
        o.Add("leaves", SvJsonNode.Integer(c.Leaves));
        o.Add("seconds", SvJsonNode.Float((float)c.Seconds));
        return o;
    }

    private static string Line(SvJsonObject o) => string.Join(", ", o.Where(kv => kv.Key != "file").Select(kv => $"{kv.Key} {CanonicalJson.ToCanonicalString(kv.Value)}"));

    internal static LightRecoveryMode Mode(string? value) => value?.ToLowerInvariant() switch
    {
        null or "full" => LightRecoveryMode.Full,
        "exact" => LightRecoveryMode.Exact,
        "off" => LightRecoveryMode.Off,
        _ => throw new ArgumentException($"--lights/--mode '{value}' is off, exact or full"),
    };

    internal static IReadOnlyList<string> Folders(string? value) =>
        value is null ? [] : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Task<int> LightsAsync(CliArgs a)
    {
        string input = a.Positional(0) ?? throw new ArgumentException("give the cooked .sdc or a project imported from one");
        if (a.Get("content") is { } content)
        {
            EditorServices.ContentRoot = Path.GetFullPath(content);
        }

        EditorServices.Discover(typeof(SdcCommands).Assembly);
        bool json = a.Has("json");
        Action<string>? progress = json ? null : line => Console.Error.WriteLine("  " + line);
        MapProject p;
        string? folder = null;
        if (ProjectStore.IsProject(input))
        {
            folder = Path.GetFullPath(input);
            p = ProjectStore.Load(folder);
        }
        else if (File.Exists(input))
        {
            // Imported with the recovery off; it runs below with this command's options.
            ImportResult imported = EditorServices.Import(new SdcImporter(), input, new ImportOptions
            {
                ContentRoot = EditorServices.ContentRoot,
                GameRoot = a.Get("game"),
                LightRecovery = LightRecoveryMode.Off,
                BrushRecovery = BrushRecoveryMode.Off,
                Progress = progress,
            });
            p = imported.Project;
        }
        else
        {
            throw new FileNotFoundException($"'{input}' is neither a project nor a file");
        }

        var findings = new List<Finding>();
        var options = new LightRecoveryOptions
        {
            Mode = Mode(a.Get("mode")),
            GameRoot = a.Get("game"),
            TwinFolders = Folders(a.Get("twins")),
            IgnoreTwins = a.Has("no-twin"),
            ForceFit = a.Has("force-fit"),
            Fit = new LightFitSettings { TimeLimit = a.Has("fit-seconds") ? TimeSpan.FromSeconds(a.GetInt("fit-seconds", 0)) : null },
            ContentRoot = EditorServices.ContentRoot,
            Progress = progress,
        };
        LightRecoveryReport report = LostLightRecovery.Rerun(p, folder, options, findings)
            ?? throw new InvalidOperationException("the map is an editor copy: it holds every light, nothing was lost");
        SvJsonObject result = LostLightRecovery.Describe(p);
        if (a.Get("truth") is { } truth)
        {
            result.Add("truth", RecoveryComparison.Compare(p, Path.GetFullPath(truth), UeSchema.Load(EditorServices.ContentRoot)));
        }

        string? output = a.Get("out") ?? (a.Has("save") ? folder : null);
        if (output is not null)
        {
            ProjectStore.Save(p, output);
        }

        if (json)
        {
            Console.WriteLine(CanonicalJson.ToCanonicalString(result));
            return Task.FromResult(0);
        }

        Console.WriteLine(report.Summary());
        foreach (string note in report.Notes)
        {
            Console.WriteLine("  " + note);
        }

        if (report.RadiusTrial is not null)
        {
            Console.WriteLine("  " + report.RadiusTrial);
        }

        foreach (Finding f in findings.Where(f => f.Severity <= Severity.Warning))
        {
            Console.WriteLine("  " + f);
        }

        if (result["truth"] is SvJsonObject t)
        {
            Console.WriteLine($"against {t["editorCopy"]}: " + string.Join(", ", t.Where(kv => kv.Key != "editorCopy")
                .Select(kv => $"{kv.Key} {CanonicalJson.ToCanonicalString(kv.Value)}")));
        }

        if (output is not null)
        {
            Console.WriteLine($"project written to {Path.GetFullPath(output)}");
        }

        return Task.FromResult(0);
    }

    private static Task<int> InfoAsync(CliArgs a)
    {
        string file = a.Positional(0) ?? throw new ArgumentException("give the .sdc file");
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"'{file}' does not exist");
        }

        // The content folder as the other commands find it: without the class data most maps do not decode.
        string? content = a.Get("content") is { } c ? Path.GetFullPath(c) : EditorServices.ContentRoot ?? ContentLocator.FindContentRoot();
        SvJsonObject info = SdcInfo.Inspect(file, UeSchema.Load(content));
        if (a.Has("json"))
        {
            Console.WriteLine(CanonicalJson.ToCanonicalString(info));
            return Task.FromResult(0);
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in info)
        {
            Console.WriteLine($"{kv.Key}: {CanonicalJson.ToCanonicalString(kv.Value)}");
        }

        return Task.FromResult(0);
    }
}
