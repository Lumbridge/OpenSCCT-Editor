using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop;

/// <summary>One line of an import report: what it says, how much it matters, and the objects it is about.</summary>
public sealed record ImportReportLine(string Text, Severity Severity, IReadOnlyList<ObjectRef> Objects);

/// <summary>One part of an import report (what came in, what was set apart, recovered or dropped, the warnings).</summary>
public sealed record ImportReportSection(string Key, string Title, IReadOnlyList<ImportReportLine> Lines);

/// <summary>
/// What an import did, in parts a mapper can read at a glance instead of a run of log lines: what came in, what the
/// cook leaves out (cooked-out actors), what the conversion made native data (lifted RE+ actors), what was recovered
/// or rebuilt, what was dropped, and the warnings. Built from the imported project and the import's findings, which the
/// project keeps (<see cref="ProjectSource.Findings"/>), so a saved and reopened map, and the script's
/// <c>importReport</c> query, show the same report. A map RE+ recovered from a compiled map says so first: it can carry
/// defects of its own, and its warnings are what was wrong in the file.
/// </summary>
public sealed class ImportReport
{
    public const string CameIn = "in", CookedOut = "cookedOut", Lifted = "lifted", Recovered = "recovered", Dropped = "dropped",
        Warnings = "warnings", Notes = "notes";

    /// <summary>Import finding codes by the part of the report they belong to (warnings and other notes go by severity).</summary>
    private static readonly Dictionary<string, string> s_sectionOf = new(StringComparer.Ordinal)
    {
        ["import.summary"] = CameIn,
        ["import.sdc"] = CameIn,
        ["import.sdcNative"] = CameIn,
        ["import.sdcRenderActors"] = CameIn,
        ["import.sdcConvertedOnly"] = CameIn,
        ["import.sdcCookedOut"] = CookedOut,
        ["import.sdcLifted"] = Lifted,
        ["import.volumeShapes"] = Recovered,
        ["import.sdcRecovered"] = Recovered,
        ["import.sdcGameplayMissing"] = Recovered,
        ["import.sdcBakedFromGameCopy"] = Recovered,
        ["import.sdcGes"] = Recovered,
        ["import.sdcLights"] = Recovered,
        ["import.sdcLightsReview"] = Recovered,
        ["import.sdcRelightUnsafe"] = Recovered,
        ["import.sdcLightsFailed"] = Recovered,
        [BrushRecoveryImport.RecoveredCode] = Recovered,
        [BrushRecoveryImport.ThinCode] = Recovered,
        [BrushRecoveryImport.UnsurfacedCode] = Recovered,
        [BrushRecoveryImport.DiffersCode] = Recovered,
        [BrushRecoveryImport.FailedCode] = Recovered,
        [BrushRecoveryImport.OffCode] = Recovered,
        [BrushRecoveryImport.KeptCode] = Recovered,
        [BrushRecoveryImport.EditorCopyCode] = Recovered,
        [ReRecoveredCode] = Recovered,
        ["import.sdcDropped"] = Dropped,
        ["import.blocksSkipped"] = Dropped,
        ["import.emptyBrush"] = Dropped,
        ["import.sdcAudio"] = Dropped,
        ["import.match"] = Dropped,
        ["import.movers"] = Dropped,
        ["import.outfits"] = Dropped,
        ["import.sdcClass"] = Warnings,
    };

    /// <summary>The finding code that says a map was recovered by RE+ from a compiled map.</summary>
    public const string ReRecoveredCode = "import.reRecovered";

    private static readonly (string Key, string Title)[] s_titles =
    [
        (CameIn, "What came in"),
        (CookedOut, "Cooked out (kept in the project, left out of builds as the cook does)"),
        (Lifted, "Lifted (RE+ actors whose settings became native data)"),
        (Recovered, "Recovered or rebuilt"),
        (Dropped, "Dropped"),
        (Warnings, "Warnings"),
        (Notes, "Other notes"),
    ];

    private ImportReport(string mapId, string kind, string? file, IReadOnlyList<ImportReportSection> sections)
    {
        MapId = mapId;
        Kind = kind;
        File = file;
        Sections = sections;
    }

    public string MapId { get; }

    /// <summary>What the map was imported from: sdc, svmap or t3d (authored for a map that was not imported).</summary>
    public string Kind { get; }

    public string? File { get; }

    public IReadOnlyList<ImportReportSection> Sections { get; }

    /// <summary>The report of an import: the project it made and its findings (null: those the project kept).</summary>
    public static ImportReport Build(MapProject p, IReadOnlyList<Finding>? findings)
    {
        findings ??= Kept(p);
        var lines = s_titles.ToDictionary(t => t.Key, _ => new List<ImportReportLine>());
        void Add(string section, string text, Severity severity = Severity.Info, IReadOnlyList<ObjectRef>? objects = null) =>
            lines[section].Add(new ImportReportLine(text, severity, objects ?? []));

        // A map imported before the importer said so is still told by its file name.
        bool recovered = findings.Any(f => f.Code == ReRecoveredCode);
        if (!recovered && p.Source.Kind == "sdc" && p.Source.File is { } file && SdcImporter.IsReRecovered(file))
        {
            Add(Recovered, SdcImporter.ReRecoveredNote, Severity.Warning);
            recovered = true;
        }

        // What the project holds, counted from it.
        int bakedBrushes = p.Brushes.Count(BakedContent.IsBaked);
        int shapes = p.Actors.Count(a => a.Shape is not null);
        Add(CameIn, $"{p.Brushes.Count} brushes{(bakedBrushes > 0 ? $" ({bakedBrushes} held by the baked world)" : "")}, {p.Actors.Count} actors "
            + $"({shapes} with a brush shape), {p.Ges.Count} GEs");
        if (p.Baked is { } baked)
        {
            string kept = (baked.KeepRender, baked.KeepCollision) switch
            {
                (true, true) => "render and collision kept",
                (true, false) => bakedBrushes > 0 ? "render kept, collision rebuilt from the brushes" : "render kept, no collision",
                (false, true) => bakedBrushes > 0 ? "collision kept, render rebuilt from the brushes" : "collision kept, no render",
                _ => "neither kept: the map is rebuilt from its brushes",
            };
            Add(CameIn, $"baked world from {baked.SourceMap} ({kept})");
        }

        List<Actor> cookedOut = p.Actors.Where(BakedContent.IsCookedOut).ToList();
        if (cookedOut.Count > 0)
        {
            foreach (IGrouping<string, Actor> g in cookedOut.GroupBy(a => a.ShortClass, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
            {
                Add(CookedOut, $"{g.Count()} {g.Key}", objects: g.Select(a => ObjectRef.Actor(a.Id)).ToList());
            }
        }

        List<Actor> lifted = p.Actors.Where(BakedContent.IsLifted).ToList();
        if (lifted.Count > 0)
        {
            Add(Lifted, string.Join(", ", lifted.Select(a => $"{a.Id} ({a.ShortClass})")), objects: lifted.Select(a => ObjectRef.Actor(a.Id)).ToList());
        }

        // The static lights the cook stripped that the import brought back, by where they came from.
        foreach (IGrouping<string, Actor> g in p.Actors.Where(a => BakedContent.Recovered(a) is not null)
            .GroupBy(a => BakedContent.RecoveredSource(a) ?? "fitted", StringComparer.Ordinal).OrderBy(g => RecoveredOrder(g.Key)))
        {
            int n = g.Count();
            Add(Recovered, $"{n} {(n == 1 ? "light" : "lights")} {RecoveredFrom(g.Key)}", objects: g.Select(a => ObjectRef.Actor(a.Id)).ToList());
        }

        foreach (Finding f in findings.OrderBy(f => f.Code == ReRecoveredCode ? 0 : 1))
        {
            string section = s_sectionOf.TryGetValue(f.Code, out string? s) && !(f.Severity != Severity.Info && s == CameIn) ? s
                : f.Severity == Severity.Info ? Notes
                : Warnings;
            Add(section, f.Message, f.Severity, f.Objects);
        }

        var sections = s_titles.Where(t => lines[t.Key].Count > 0)
            .Select(t => new ImportReportSection(t.Key, recovered && t.Key == Warnings ? "Warnings (what was wrong in the file)" : t.Title, lines[t.Key]))
            .ToList();
        return new ImportReport(p.Id, p.Source.Kind, p.Source.File, sections);
    }

    private static int RecoveredOrder(string source) => source switch { "twin" => 0, "orphan" => 1, "staleTwin" => 2, _ => 3 };

    private static string RecoveredFrom(string source) => source switch
    {
        "twin" => "brought back from the map's editor copy",
        "orphan" => "kept in the cooked file outside the level",
        "staleTwin" => "brought back from an editor copy of another bake",
        _ => "fitted as stand-ins to the cooked lightmaps",
    };

    /// <summary>Keeps an import's findings in the project it made, for its report after a save and reopen.</summary>
    public static void Keep(MapProject p, IReadOnlyList<Finding> findings)
    {
        var kept = new SvJsonArray();
        foreach (Finding f in findings)
        {
            kept.Add(ApiJson.Obj(("sev", ApiJson.Str(f.Severity.ToString().ToLowerInvariant())), ("code", ApiJson.Str(f.Code)),
                ("msg", ApiJson.Str(f.Message)), ("objects", Refs(f.Objects))));
        }

        p.Source.Findings = kept;
    }

    /// <summary>The import's findings the project kept (none for a map imported before they were kept).</summary>
    public static IReadOnlyList<Finding> Kept(MapProject p)
    {
        var findings = new List<Finding>();
        foreach (SvJsonObject o in p.Source.Findings?.OfType<SvJsonObject>() ?? [])
        {
            Severity severity = Enum.TryParse(ModelJson.Str(o, "sev"), ignoreCase: true, out Severity s) ? s : Severity.Info;
            List<ObjectRef> objects = (o["objects"] as SvJsonArray)?.OfType<SvJsonString>().Select(n => ParseRef(n.Value)).OfType<ObjectRef>().ToList() ?? [];
            findings.Add(new Finding(severity, ModelJson.Str(o, "code") ?? "", ModelJson.Str(o, "msg") ?? "", objects));
        }

        return findings;
    }

    /// <summary>Objects as <c>Kind:Id</c> (as Map Check's JSON gives them), or null for none.</summary>
    private static SvJsonArray? Refs(IReadOnlyList<ObjectRef> objects) =>
        objects.Count == 0 ? null : ApiJson.Arr(objects.Select(o => (SvJsonNode)ApiJson.Str(o.ToString())));

    private static ObjectRef? ParseRef(string text) =>
        text.IndexOf(':') is var colon and > 0 && Enum.TryParse(text[..colon], out ObjectKind kind) ? new ObjectRef(kind, text[(colon + 1)..]) : null;

    /// <summary>The report as plain text, one part after another.</summary>
    public string ToText()
    {
        var text = new System.Text.StringBuilder($"Import of {MapId} ({Kind}{(File is null ? "" : $", {File}")})\n");
        foreach (ImportReportSection s in Sections)
        {
            text.Append('\n').Append(s.Title).Append('\n');
            foreach (ImportReportLine l in s.Lines)
            {
                text.Append(l.Severity == Severity.Info ? "  - " : $"  [{l.Severity}] ").Append(l.Text).Append('\n');
            }
        }

        return text.ToString();
    }

    public SvJsonObject ToJson()
    {
        var sections = new SvJsonArray();
        foreach (ImportReportSection s in Sections)
        {
            var lines = new SvJsonArray();
            foreach (ImportReportLine l in s.Lines)
            {
                lines.Add(ApiJson.Obj(("text", ApiJson.Str(l.Text)), ("severity", ApiJson.Str(l.Severity.ToString().ToLowerInvariant())),
                    ("objects", Refs(l.Objects))));
            }

            sections.Add(ApiJson.Obj(("key", ApiJson.Str(s.Key)), ("title", ApiJson.Str(s.Title)), ("lines", lines)));
        }

        return ApiJson.Obj(("map", ApiJson.Str(MapId)), ("kind", ApiJson.Str(Kind)), ("file", File is null ? null : ApiJson.Str(File)),
            ("sections", sections));
    }
}
