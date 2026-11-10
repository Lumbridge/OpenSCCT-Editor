using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>A staged install overlay. Nothing is published until both maps and every dependency pass export.</summary>
internal sealed class ReloadedBundleWriter(MapProject project, string folder, ExportOptions options, string stage)
{
    public List<string> Files { get; } = [];
    public List<Finding> Findings { get; } = [];

    public void Run()
    {
        string? game = null;
        try { game = SdcImporter.GameRoot(project.Source.File ?? stage, options.GameRoot); }
        catch (DirectoryNotFoundException) { /* SDC export reports absent install information. */ }
        MapProject prepared = SdcCompatibility.Prepare(project, options.ContentRoot ?? EditorServices.ContentRoot, game, Findings);
        if (Findings.Any(f => f.Severity == Severity.Error)) return;
        prepared = Stairs.StairGeometry.Lower(Gameplay.GeGeometry.Expand(prepared));
        var dependencies = new NativeDependencies();
        MapProject copy = dependencies.Prepare(prepared, stage, options, game);
        Findings.AddRange(dependencies.Findings);
        Finding[] preparationFindings = Findings.ToArray();
        foreach (string name in copy.MapSettings.Keys.ToArray())
            if (!copy.MapSettings[name].Contains("GameModeId"))
                copy.MapSettings[name].Set("GameModeId", SvJsonNode.Integer(ReloadedInterface.ModeId(name)));
        var exporter = new SdcExporter();
        foreach (string layout in new[] { "cooked", "editor" })
        {
            string path = Path.Combine(stage, "Packages", layout == "cooked" ? "Maps" : "MapsEd", copy.Id + ".sdc");
            var choices = new Dictionary<string, string>(options.Choices) { [SdcExporter.LayoutChoice] = layout };
            ExportResult result = exporter.Export(copy, path, options with
            { GameRoot = game, NativePackages = dependencies.Files, PortableAssets = true, Choices = choices });
            string Published(string value) => value.Replace(stage, folder, StringComparison.OrdinalIgnoreCase);
            result = result with { Findings = result.Findings.Where(f => f.Code != "sdc.mapSettings")
                .Select(f => f with { Message = Published(f.Message) }).ToArray() };
            foreach (Finding finding in result.Findings)
                if (!Findings.Any(f => f.Code == finding.Code && f.Message == finding.Message)) Findings.Add(finding);
            if (!result.Ok) return;
            File.WriteAllText(path + ".export-report.md", SdcExportReport.Text(result with
                { Files = result.Files.Select(Published).ToArray(), Findings = preparationFindings.Concat(result.Findings).ToArray() }));
            foreach (string file in result.Files) Add(file);
        }
        foreach (string file in dependencies.Written) Add(file);
        UeSchema? schema = UeSchema.Load(options.ContentRoot ?? EditorServices.ContentRoot);
        Write("Packages/Textures/" + copy.Id + "-i.utc", ReloadedInterface.Build(copy, options, schema, Findings));
        if (InterfaceTexts.SettingsFile(copy) is { } settings) Write("System/_PC_/" + copy.Id + "-i.int", settings);
        if (schema is not null && InterfaceTexts.ActorFile(copy, schema, copy.Actors,
                T3d.T3dExport.LevelInfoName(copy), copy.Title) is { } actors)
            Write("System/_PC_/" + copy.Id + ".int", actors);
        Write("README.txt", System.Text.Encoding.UTF8.GetBytes($"""
            {copy.Title} ({copy.Id}) — Reloaded install folder

            Drag System and Packages into a compatible SCCT Versus Reloaded installation, merging the folders.
            The playable map is Packages/Maps/{copy.Id}.sdc.
            Open Packages/MapsEd/{copy.Id}.sdc in Reloaded Editor to edit it.
            No T3D import or asset import commands are needed.

            Keep the map-specific asset packages with both maps. They do not replace the original stock packages.
            Reloaded's engine and script packages are required; this folder does not include those binaries.
            Review each map's export-report.md for missing-texture replacements and compatibility warnings.
            """));
        // Temporary source import tables have already been consumed by both writers; never ship them.
        string source = Path.GetFullPath(Path.Combine(stage, ".source"));
        if (source.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(source))
            Directory.Delete(source, recursive: true);
    }

    private void Add(string path)
    {
        string destination = Path.Combine(folder, Path.GetRelativePath(stage, path));
        if (!Files.Contains(destination, StringComparer.OrdinalIgnoreCase)) Files.Add(destination);
    }

    private void Write(string relative, byte[] data)
    {
        string file = Path.Combine(stage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, data); Add(file);
    }
}
