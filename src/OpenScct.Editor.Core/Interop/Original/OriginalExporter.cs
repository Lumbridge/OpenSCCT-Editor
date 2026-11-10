using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>
/// A Reloaded install overlay with cooked and editable SDCs, native map-specific asset packages and interface files.
/// A complete staged export replaces only an earlier export's owned files.
/// </summary>
[EditorService]
public sealed class OriginalExporter : IMapExporter
{
    /// <summary>The list of files an export wrote, so the next export into the folder removes only those.</summary>
    public const string ManifestFile = "openscct-export.json";

    public string Name => "original";

    public string Extension => "";

    public TargetGame Target => TargetGame.Original;

    public bool WritesFolder => true;

    public ExportResult Export(MapProject project, string path, ExportOptions options)
    {
        if (project.Actors.Any(a => MoverBase.IsMover(a) && OpenScct.Editor.Core.Gameplay.MoverSetup.Read(a).Keys.Count > 8))
            return new ExportResult(false, [], [Finding.Error("mover.original-limit", "This map uses mover paths longer than eight stops. Build for OpenSCCT, or shorten those paths before exporting to the original game.")]);
        if (project.Actors.Any(a => a.Door is not null))
            return new ExportResult(false, [], [Finding.Error("door.originalUnsupported", "Editable doors currently require an OpenSCCT build")]);

        string folder = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (CheckFolder(folder) is { } refused)
        {
            return new ExportResult(false, [], [refused]);
        }

        string stage = folder + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            var writer = new ReloadedBundleWriter(project, folder, options, stage);
            writer.Run();
            if (writer.Findings.Any(f => f.Severity == Severity.Error)) return new ExportResult(false, [], writer.Findings);
            if (CheckFolder(folder) is { } conflict) return new ExportResult(false, [], [conflict]);
            File.WriteAllBytes(Path.Combine(stage, ManifestFile), CanonicalJson.ToFileBytes(Manifest(folder, writer.Files)));
            string backup = folder + ".previous-" + Guid.NewGuid().ToString("N");
            Directory.Move(folder, backup);
            try { Directory.Move(stage, folder); }
            catch
            {
                Directory.Move(backup, folder);
                throw;
            }
            try
            {
                if (Path.GetFullPath(backup).StartsWith(folder + ".previous-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(backup, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { writer.Findings.Add(Finding.Warning("export.previousFolder", $"The new export is complete. Its previous revision remains at {backup}: {ex.Message}")); }
            return new ExportResult(true, writer.Files, writer.Findings);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return new ExportResult(false, [], [Finding.Error("export.reloaded", ex.Message + " The previous export is preserved.")]);
        }
        finally
        {
            if (Path.GetFullPath(stage).StartsWith(folder + ".stage-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>Accepts only an empty folder or a previous export containing exactly its owned files and no links.</summary>
    private static Finding? CheckFolder(string folder)
    {
        if (File.Exists(folder))
        {
            return Finding.Error("export.folder", $"{folder} is a file: the export for the original editor writes a folder");
        }

        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            return null;
        }

        if (IsLink(folder))
        {
            return Finding.Error("export.folder", $"{folder} is a link: export into a real folder");
        }

        if (!Directory.EnumerateFileSystemEntries(folder).Any())
        {
            return null;
        }

        string manifest = Path.Combine(folder, ManifestFile);
        SvJsonArray? files = null;
        try
        {
            files = File.Exists(manifest) && SvJson.Parse(File.ReadAllBytes(manifest), manifest) is SvJsonObject o ? o["files"] as SvJsonArray : null;
        }
        catch (SvMapFormatException)
        {
            // A damaged list is no list: the folder is refused below rather than guessed at.
        }

        if (files is null)
        {
            return Finding.Error("export.folder", $"{folder} is not empty and holds no earlier export (or its {ManifestFile} is damaged): pick an empty folder");
        }

        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in files.OfType<SvJsonString>().Select(s => s.Value))
        {
            string full = Path.GetFullPath(Path.Combine(folder, relative));
            if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || ThroughLink(folder, full))
                return Finding.Error("export.folder", "The earlier export's file list contains an unsafe path; choose an empty folder.");
            owned.Add(full);
        }
        var pending = new Stack<string>(); pending.Push(folder);
        while (pending.TryPop(out string? directory))
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsLink(entry)) return Finding.Error("export.folder", "The export folder contains a link; choose an empty folder.");
                if (Directory.Exists(entry)) pending.Push(entry);
                else if (!entry.Equals(manifest, StringComparison.OrdinalIgnoreCase) && !owned.Contains(entry))
                    return Finding.Error("export.folder", $"{entry} is not owned by the previous export; choose an empty folder.");
            }
        return null;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ThroughLink(string root, string file)
    {
        for (string? d = Path.GetDirectoryName(file); d is not null && d.Length > root.Length; d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(d) && IsLink(d))
            {
                return true;
            }
        }

        return File.Exists(file) && IsLink(file);
    }

    private static SvJsonObject Manifest(string folder, IEnumerable<string> files)
    {
        var o = new SvJsonObject();
        o.Add("format", SvJsonNode.String("osmap.export/1"));
        o.Add("files", new SvJsonArray(files.Select(f => SvJsonNode.String(Path.GetRelativePath(folder, f).Replace('\\', '/')))));
        return o;
    }

}
