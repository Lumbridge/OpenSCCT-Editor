using System.Diagnostics;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose import</c> and <c>ose export</c>: maps in and out of other formats (T3D, SVMap, .sdc).</summary>
public static class InteropCommands
{
    [CliProvider]
    public static IEnumerable<CliCommand> Commands() =>
    [
        new CliCommand("import",
            "import <source> <out.osmap> [--map Name] [--format t3d|svmap|sdc] [--content <.content>] [--game <install>] "
            + "[--lights off|exact|full] [--twins <folder;...>] [--fit-seconds N] [--brushes off|cells|merge] [--add] [--json]",
            "Imports a map into a new project: a T3D file (stock editor File > Export), a converted SVMap folder, or an .sdc "
            + "(converted through OpenSCCT's svmbuild first, needs the game install). A cooked .sdc gets the static lights its "
            + "cook stripped back (--lights, default full: from its editor copy in any install, from the file, else fitted "
            + "stand-ins; see sdc-lights), and its brushes recovered from the compiled BSP as editable brushes in the Recovered "
            + "brushes layer (--brushes, default merge: cells joined and grown over their neighbours, as RE+'s map recovery; cells "
            + "joins only cells that share a whole face; off keeps the baked world only; see sdc-brushes). With --add, a T3D file (a whole map or "
            + "copied objects) is added to the existing project instead, as one undo step, with clashing names renamed. "
            + "Prints the import report (what came in, cooked out, lifted, recovered, dropped, warnings); --json prints the "
            + "result, findings and report as JSON.",
            ImportAsync),
        new CliCommand("export",
            "export <project.osmap> <out> [--format t3d|sdc|original] [--layout editor|cooked] [--game <install>] "
            + "[--only Kind:Id,...] [--content <.content>] [--json]",
            "Exports a project for the original game after the round-trip lint: T3D for the stock editor's File > Import, a "
            + "map package (.sdc) written natively, or a folder for RE+. An .sdc is the editor copy for Packages\\MapsEd "
            + "(--layout editor, the default) or the cooked copy the game loads from Packages\\Maps (--layout cooked); it is "
            + "read back and checked, and a map imported from an .sdc keeps that file's objects it does not edit. --game is the "
            + "install new materials and meshes are looked up in. --format original writes the folder for RE+ to <out> instead: "
            + "the T3D, the actors of the map's own classes and their sources, the map's own textures, meshes and sounds with a "
            + "script that imports them, the map's texts (.int) and a README of the steps in RE+. --only writes just those "
            + "objects (Brush:Id, Actor:Id, Ge:Id or a bare id) in the T3D Edit > Copy form, without LevelInfo and builder "
            + "brush. Writes even with lint errors; the exit code is 1 then.",
            ExportAsync),
        new CliCommand("export-t3d",
            "export-t3d <project.osmap> <out.t3d> [--only Kind:Id,...] [--content <.content>] [--json]",
            "The same as export --format t3d.",
            ExportAsync),
    ];

    private static Task<int> ImportAsync(CliArgs a)
    {
        string source = a.Positional(0) ?? throw new ArgumentException("give the map to import");
        string output = a.Positional(1) ?? throw new ArgumentException("give the project folder to write (<Map>.osmap)");
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException($"'{source}' does not exist");
        }

        Prepare(a);
        bool json = a.Has("json");
        var options = new ImportOptions
        {
            MapId = a.Get("map"),
            ContentRoot = a.Get("content") ?? EditorServices.ContentRoot,
            GameRoot = a.Get("game"),
            LightRecovery = SdcCommands.Mode(a.Get("lights")),
            TwinFolders = SdcCommands.Folders(a.Get("twins")),
            LightFitTime = a.Has("fit-seconds") ? TimeSpan.FromSeconds(a.GetInt("fit-seconds", 0)) : null,
            BrushRecovery = BrushRecoveryImport.ParseMode(a.Get("brushes")),
            Progress = json ? null : line => Console.Error.WriteLine("  " + line),
        };
        if (a.Has("add"))
        {
            return ImportAdd(source, output, options, a.Get("format"), json);
        }

        string? format = a.Get("format");
        IMapImporter importer = (format is null
            ? EditorServices.ImporterFor(source)
            : EditorServices.Importers.Find(i => string.Equals(i.Name, format, StringComparison.OrdinalIgnoreCase)))
            ?? throw new ArgumentException(format is null
                ? $"no importer reads '{source}' (T3D files, SVMap folders and .sdc files are supported)"
                : $"unknown format '{format}' (one of: {string.Join(", ", EditorServices.Importers.Select(i => i.Name))})");
        var watch = Stopwatch.StartNew();
        ImportResult result = EditorServices.Import(importer, source, options);
        ProjectPictures.Adopt(result.Project, output);
        // An export for the original editor brings the project's own assets back with it.
        Core.Interop.Original.HandoffFile.AdoptAssets(result.Project, output);
        // The source map's outfit files are copied in, so the project does not depend on the map it came from.
        result.Project.Outfits = ProjectOutfits.Localize(result.Project.Outfits, output);
        ProjectStore.Save(result.Project, output);
        watch.Stop();
        MapProject p = result.Project;
        if (json)
        {
            var o = new SvJsonObject();
            o.Add("ok", SvJsonNode.True);
            o.Add("format", SvJsonNode.String(importer.Name));
            o.Add("project", SvJsonNode.String(Path.GetFullPath(output)));
            o.Add("map", SvJsonNode.String(p.Id));
            o.Add("stats", Stats(p));
            o.Add("findings", FindingsJson(result.Findings));
            o.Add("report", ImportReport.Build(p, result.Findings).ToJson());
            Console.WriteLine(CanonicalJson.ToCanonicalString(o));
        }
        else
        {
            Console.WriteLine($"Imported {source} ({importer.Name}) to {Path.GetFullPath(output)} in {watch.Elapsed.TotalSeconds:0.0} s");
            Console.WriteLine($"  {p.Id} \"{p.Title}\": {p.Brushes.Count} brushes, {p.Actors.Count} actors, {p.Ges.Count} GEs"
                + (p.Baked is not null ? ", baked world" : ""));
            Console.WriteLine();
            Console.Write(ImportReport.Build(p, result.Findings).ToText());
        }

        return Task.FromResult(0);
    }

    /// <summary>Adds a T3D file to an existing project through its command stack (one undo step) and saves it.</summary>
    private static Task<int> ImportAdd(string source, string project, ImportOptions options, string? format, bool json)
    {
        if (format is not null && !format.Equals("t3d", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--add takes T3D only (a whole map or copied objects)");
        }

        if (!ProjectStore.IsProject(project))
        {
            throw new ArgumentException($"'{project}' is not a project folder: --add adds to an existing project");
        }

        EditorSession session = EditorSession.Open(project);
        T3dMergeResult merge = T3dImporter.ImportAdd(session.Project, source, options);
        session.Execute(merge.Command);
        session.Save();
        if (json)
        {
            var o = new SvJsonObject();
            o.Add("ok", SvJsonNode.True);
            o.Add("format", SvJsonNode.String("t3d"));
            o.Add("project", SvJsonNode.String(Path.GetFullPath(project)));
            o.Add("added", new SvJsonArray(merge.Added.Select(r => SvJsonNode.String(r.ToString()))));
            o.Add("findings", FindingsJson(merge.Findings));
            Console.WriteLine(CanonicalJson.ToCanonicalString(o));
        }
        else
        {
            Console.WriteLine($"Added {merge.Added.Count} objects from {source} to {Path.GetFullPath(project)} ({merge.Command.Name})");
            PrintFindings(merge.Findings);
        }

        return Task.FromResult(0);
    }

    private static Task<int> ExportAsync(CliArgs a)
    {
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        string output = a.Positional(1) ?? throw new ArgumentException("give the file (or, for --format original, the folder) to write");
        Prepare(a);
        string format = a.Get("format") ?? "t3d";
        IMapExporter exporter = EditorServices.Exporter(format)
            ?? throw new ArgumentException($"unknown format '{format}' (one of: {string.Join(", ", EditorServices.Exporters.Select(e => e.Name))})");
        bool json = a.Has("json");
        MapProject p = ProjectStore.Load(folder);
        var choices = new Dictionary<string, string>();
        foreach (ExportChoice c in exporter.Choices)
        {
            if (a.Get(c.Name) is { } value)
            {
                if (!c.Values.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"--{c.Name} is one of: {string.Join(", ", c.Values)}");
                }

                choices[c.Name] = value.ToLowerInvariant();
            }
        }

        var options = new ExportOptions
        {
            ContentRoot = a.Get("content") ?? EditorServices.ContentRoot,
            GameRoot = a.Get("game"),
            Choices = choices,
            ProjectFolder = Path.GetFullPath(folder),
            Progress = json ? null : line => Console.Error.WriteLine("  " + line),
        };
        ExportResult result;
        if (a.Get("only") is { } only)
        {
            if (exporter is not T3dExporter)
            {
                throw new ArgumentException("--only works with the t3d format");
            }

            result = T3dExporter.ExportSelection(p, ParseRefs(p, only), output, options);
        }
        else
        {
            result = exporter.Export(p, output, options);
        }

        if (json)
        {
            var o = new SvJsonObject();
            o.Add("ok", SvJsonNode.Bool(result.Ok));
            o.Add("format", SvJsonNode.String(exporter.Name));
            o.Add("files", new SvJsonArray(result.Files.Select(SvJsonNode.String)));
            o.Add("findings", FindingsJson(result.Findings));
            Console.WriteLine(CanonicalJson.ToCanonicalString(o));
        }
        else
        {
            Console.WriteLine(result.Files.Count == 0
                ? $"Did not export {p.Id} ({exporter.Name})"
                : $"Exported {p.Id} ({exporter.Name}) to {string.Join(", ", result.Files)}" + (result.Ok ? "" : " with errors"));
            PrintFindings(result.Findings);
        }

        return Task.FromResult(result.Ok ? 0 : 1);
    }

    /// <summary>Objects from <c>Kind:Id</c> or bare ids (looked up among brushes, actors and GEs), comma separated.</summary>
    private static List<ObjectRef> ParseRefs(MapProject p, string text)
    {
        var refs = new List<ObjectRef>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);
            ObjectRef r = colon > 0 && Enum.TryParse(part[..colon], ignoreCase: true, out ObjectKind kind)
                ? new ObjectRef(kind, part[(colon + 1)..])
                : p.AllRefs().FirstOrDefault(x => string.Equals(x.Id, part, StringComparison.OrdinalIgnoreCase));
            if (r.Id is null || !p.Exists(r))
            {
                throw new ArgumentException($"no object '{part}' in {p.Id}");
            }

            refs.Add(r);
        }

        return refs;
    }

    private static void Prepare(CliArgs a)
    {
        if (a.Get("content") is { } content)
        {
            EditorServices.ContentRoot = Path.GetFullPath(content);
        }

        EditorServices.Discover(typeof(InteropCommands).Assembly);
    }

    private static SvJsonObject Stats(MapProject p)
    {
        var o = new SvJsonObject();
        o.Add("brushes", SvJsonNode.Integer(p.Brushes.Count));
        o.Add("actors", SvJsonNode.Integer(p.Actors.Count));
        o.Add("ges", SvJsonNode.Integer(p.Ges.Count));
        o.Add("baked", SvJsonNode.Bool(p.Baked is not null));
        return o;
    }

    private static SvJsonArray FindingsJson(IEnumerable<Finding> findings) => new(findings.Select(f =>
    {
        var o = new SvJsonObject();
        o.Add("severity", SvJsonNode.String(f.Severity.ToString().ToLowerInvariant()));
        o.Add("code", SvJsonNode.String(f.Code));
        o.Add("message", SvJsonNode.String(f.Message));
        if (f.Objects.Count > 0)
        {
            o.Add("objects", new SvJsonArray(f.Objects.Select(r => SvJsonNode.String(r.ToString()))));
        }

        return (SvJsonNode)o;
    }));

    private static void PrintFindings(IReadOnlyList<Finding> findings)
    {
        foreach (Finding f in findings.OrderBy(f => f.Severity))
        {
            Console.WriteLine("  " + f);
        }
    }
}
