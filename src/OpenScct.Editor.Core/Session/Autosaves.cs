using OpenScct.Content.Json;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Session;

/// <summary>Where an autosave copy came from: the project folder it was taken of (null for a map never saved), the
/// map, and when.</summary>
public sealed record AutosaveInfo(string? ProjectFolder, string MapId, string Title, DateTime SavedUtc);

/// <summary>One autosave copy on disk.</summary>
public sealed record AutosaveCopy(string Folder, AutosaveInfo Info);

/// <summary>
/// The autosave copies in an autosave folder. Each copy is a whole project folder (<c>&lt;MapId&gt;.auto&lt;n&gt;.osmap</c>)
/// with an <c>autosave.json</c> naming the project it was taken of, so a recovered copy saves back to that project.
/// </summary>
public static class Autosaves
{
    public const string InfoFile = "autosave.json";
    private const string Format = "osmap.autosave/1";

    public static void WriteInfo(string copyFolder, AutosaveInfo info)
    {
        var o = new SvJsonObject();
        o.Add("format", SvJsonNode.String(Format));
        o.Add("id", SvJsonNode.String(info.MapId));
        o.Add("title", SvJsonNode.String(info.Title));
        o.Add("saved", SvJsonNode.String(info.SavedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        if (info.ProjectFolder is not null)
        {
            o.Add("project", SvJsonNode.String(Path.GetFullPath(info.ProjectFolder)));
        }

        string path = Path.Combine(copyFolder, InfoFile);
        File.WriteAllBytes(path + ".tmp", CanonicalJson.ToFileBytes(o));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>The note of an autosave copy, or null when it has none (an autosave from an older editor).</summary>
    public static AutosaveInfo? ReadInfo(string copyFolder)
    {
        string path = Path.Combine(copyFolder, InfoFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (SvJson.Parse(File.ReadAllBytes(path), InfoFile) is not SvJsonObject o)
            {
                return null;
            }

            DateTime saved = o["saved"] is SvJsonString s && DateTime.TryParse(s.Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t) ? t.ToUniversalTime() : Directory.GetLastWriteTimeUtc(copyFolder);
            return new AutosaveInfo((o["project"] as SvJsonString)?.Value, (o["id"] as SvJsonString)?.Value ?? "", (o["title"] as SvJsonString)?.Value ?? "", saved);
        }
        catch (Exception ex) when (ex is IOException or OpenScct.Content.SvMapFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The autosave copies under <paramref name="root"/>, newest first.</summary>
    public static IReadOnlyList<AutosaveCopy> List(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetDirectories(root, "*.auto*" + ProjectStore.Extension)
            .Where(ProjectStore.IsProject)
            .Select(d => new AutosaveCopy(d, ReadInfo(d) ?? new AutosaveInfo(null, IdOf(d), "", Directory.GetLastWriteTimeUtc(d))))
            .OrderByDescending(c => c.Info.SavedUtc)
            .ToList();
    }

    /// <summary>The newest autosave of one map that holds work its project lacks, or null. A saved map's copies are
    /// matched by project folder (a Save As copy keeps the map id, so the id alone could find another project's
    /// autosave); a map never saved matches unsaved copies of the same id.</summary>
    public static AutosaveCopy? Latest(string root, string? projectFolder, string mapId)
    {
        string? full = projectFolder is null ? null : Path.GetFullPath(projectFolder);
        return List(root).FirstOrDefault(c => !IsOlderThanProject(c.Info) && (full is null
            ? c.Info.ProjectFolder is null && string.Equals(c.Info.MapId, mapId, StringComparison.OrdinalIgnoreCase)
            : c.Info.ProjectFolder is { } f && string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when the project a copy was taken of has been saved since: opening the copy and saving it would
    /// put older work over newer.</summary>
    public static bool IsOlderThanProject(AutosaveInfo info)
    {
        if (info.ProjectFolder is null)
        {
            return false;
        }

        string map = Path.Combine(info.ProjectFolder, ProjectStore.MapFile);
        return File.Exists(map) && File.GetLastWriteTimeUtc(map) > info.SavedUtc;
    }

    /// <summary>The map id an autosave slot's folder name carries (<c>ShipD.auto3.osmap</c> is ShipD).</summary>
    private static string IdOf(string folder)
    {
        string name = Path.GetFileName(folder);
        int auto = name.LastIndexOf(".auto", StringComparison.Ordinal);
        return auto > 0 ? name[..auto] : name;
    }
}
