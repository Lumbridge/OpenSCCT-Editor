using System.Security.Cryptography;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>Imports UE2 T3D map text (the stock editor's File &gt; Export / <c>MAP EXPORT</c>).</summary>
[EditorService]
public sealed class T3dImporter : IMapImporter
{
    public string Name => "t3d";

    public bool CanImport(string path) => File.Exists(path) && Path.GetExtension(path).Equals(".t3d", StringComparison.OrdinalIgnoreCase);

    public ImportResult Import(string path, ImportOptions options)
    {
        options.Progress?.Invoke($"reading {Path.GetFileName(path)}");
        byte[] bytes = File.ReadAllBytes(path);
        T3dDocument doc = T3dReader.Read(T3dReader.Decode(bytes));
        UeSchema? schema = UeSchema.Load(options.ContentRoot ?? EditorServices.ContentRoot);
        var findings = new List<Finding>();
        // An export for the original editor keeps the actors of the map's own classes in a second file beside it. A T3D
        // the original editor wrote over the export's (its Export to T3D, after MAP IMPORTADD) already holds them.
        string classes = Original.HandoffFile.ClassesPathFor(path);
        if (File.Exists(classes) && File.Exists(Original.HandoffFile.PathFor(path)))
        {
            List<T3dActor> classActors = T3dReader.Read(T3dReader.Decode(File.ReadAllBytes(classes))).Actors;
            var names = new HashSet<string>(doc.Actors.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            if (classActors.Any(a => names.Contains(a.Name)))
            {
                findings.Add(Finding.Info("import.mapClasses",
                    $"{Path.GetFileName(path)} already holds the actors of {Path.GetFileName(classes)}, so that file is not read"));
            }
            else
            {
                options.Progress?.Invoke($"reading {Path.GetFileName(classes)}");
                doc.Actors.AddRange(classActors);
            }
        }

        string mapId = options.MapId ?? Path.GetFileNameWithoutExtension(path);
        options.Progress?.Invoke($"converting {doc.Actors.Count} actors");
        MapProject p = T3dImport.ToProject(doc, mapId, schema, findings);
        p.Source = new ProjectSource
        {
            Kind = "t3d",
            File = Path.GetFullPath(path),
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
        Original.HandoffFile.Apply(p, path, findings);
        return new ImportResult(p, findings);
    }

    /// <summary>Adds a T3D file to an open project as one undo step (the stock editor's import into the current map).</summary>
    public static T3dMergeResult ImportAdd(MapProject target, string path, ImportOptions options)
    {
        options.Progress?.Invoke($"reading {Path.GetFileName(path)}");
        return Paste(target, T3dReader.Decode(File.ReadAllBytes(path)), options.ContentRoot);
    }

    /// <summary>Adds T3D text (copied from this or the stock editor) to an open project as one undo step.</summary>
    public static T3dMergeResult Paste(MapProject target, string text, string? contentRoot = null) =>
        T3dMerge.ImportAdd(target, T3dReader.Read(text), UeSchema.Load(contentRoot ?? EditorServices.ContentRoot));
}

/// <summary>Exports a project as T3D for the stock or RE+ editor (File &gt; Import), after the round-trip lint.</summary>
[EditorService]
public sealed class T3dExporter : IMapExporter
{
    public string Name => "t3d";

    public string Extension => ".t3d";

    public TargetGame Target => TargetGame.Original;

    /// <summary>Writes the file even when the lint finds errors (the stock editor may still take most of it);
    /// <see cref="ExportResult.Ok"/> is false then.</summary>
    public ExportResult Export(MapProject project, string path, ExportOptions options)
    {
        try
        {
            project = GeGeometry.Expand(project);
        }
        catch (ArgumentException ex)
        {
            return new ExportResult(false, [], [Finding.Error("ge.geometry", ex.Message)]);
        }

        UeSchema? schema = UeSchema.Load(options.ContentRoot ?? EditorServices.ContentRoot);
        options.Progress?.Invoke("checking round-trip compatibility");
        string? content = options.ContentRoot ?? EditorServices.ContentRoot;
        List<Finding> findings = RoundTripLint.Check(project, schema, assets: AssetCatalog.For(content, options.ProjectFolder),
            resolver: Original.LocalAssets.Resolver(project, content, options.ProjectFolder), projectFolder: options.ProjectFolder);
        options.Progress?.Invoke($"writing {Path.GetFileName(path)}");
        T3dDocument doc = T3dExport.FromProject(project, schema, projectFolder: options.ProjectFolder);
        T3dWriter.WriteFile(doc, path);
        bool ok = !findings.Any(f => f.Severity == Severity.Error);
        return new ExportResult(ok, [Path.GetFullPath(path)], findings);
    }

    /// <summary>Writes only the chosen objects (the Edit &gt; Copy form: no LevelInfo, no builder brush), after the
    /// object checks of the lint.</summary>
    public static ExportResult ExportSelection(MapProject project, IReadOnlyCollection<ObjectRef> objects, string path, ExportOptions options)
    {
        (T3dDocument doc, List<Finding> findings) = Selection(project, objects, options.ContentRoot);
        T3dWriter.WriteFile(doc, path);
        return new ExportResult(!findings.Any(f => f.Severity == Severity.Error), [Path.GetFullPath(path)], findings);
    }

    /// <summary>The T3D text of the chosen objects (for the clipboard), and the lint findings of those objects.</summary>
    public static (string Text, List<Finding> Findings) Copy(MapProject project, IReadOnlyCollection<ObjectRef> objects, string? contentRoot = null)
    {
        (T3dDocument doc, List<Finding> findings) = Selection(project, objects, contentRoot);
        return (T3dWriter.Write(doc), findings);
    }

    private static (T3dDocument Doc, List<Finding> Findings) Selection(MapProject project, IReadOnlyCollection<ObjectRef> objects, string? contentRoot)
    {
        UeSchema? schema = UeSchema.Load(contentRoot ?? EditorServices.ContentRoot);
        var set = new HashSet<ObjectRef>(objects, ObjectRefComparer.Instance);
        var picked = new MapProject
        {
            Id = project.Id,
            Brushes = project.Brushes.Where(b => set.Contains(ObjectRef.ForBrush(b))).ToList(),
            Actors = project.Actors.Where(a => set.Contains(ObjectRef.Actor(a.Id))).ToList(),
            Ges = project.Ges.Where(g => set.Contains(ObjectRef.Ge(g.Id))).ToList(),
        };
        return (T3dExport.FromProject(project, schema, set), RoundTripLint.Check(GeGeometry.Expand(picked), schema, wholeMap: false,
            handoff: ConveyorHandoff.Of(project)));
    }
}
