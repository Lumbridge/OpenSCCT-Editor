using System.Globalization;
using System.Security.Cryptography;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Project;

/// <summary>One kept copy of a project's files from before a save.</summary>
public sealed record ProjectBackup(string Name, string Folder, DateTime SavedUtc);

/// <summary>
/// Rotating save backups. Each save that replaces a project's files first moves the old ones to
/// <c>&lt;map&gt;.osmap/.backups/&lt;UTC time&gt;/</c> (the save journal does the move, so it costs no copy), and only the
/// newest few are kept. A backup holds the map's own JSON files (map, world, layers, workspace) from before the save.
/// <para>The map's <c>assets/</c> and <c>prefabs/</c> change outside saves (an import writes its files at once), so each
/// save that keeps backups also lists them in <see cref="FilesList"/> (path, size, time, hash) and puts every file
/// version not kept yet into one shared store, <c>.backups/files/&lt;hash&gt;</c>. The list is one of the save's own
/// files, so the journal moves it into the next backup with the JSON it was saved with: a backup's assets and prefabs
/// are the ones its map was saved with. A file whose size and time are unchanged is not read again, so a save that
/// changed no asset copies nothing, and each version of a file is stored once however many backups name it.</para>
/// <para>How many are kept is one per-user setting (<see cref="Kept"/>) that the editor's Preferences, <c>ose</c> and
/// the API host all use. Saves in quick succession (a script's edits, a burst of Ctrl+S) keep only the first and the
/// last backup of the run, so they never push out the older restore points.</para>
/// </summary>
public static class ProjectBackups
{
    public const string FolderName = ".backups";

    /// <summary>The project file listing its assets and prefabs at the save that wrote it.</summary>
    public const string FilesList = ".backup-files.json";

    /// <summary>The store of listed file versions, under <see cref="FolderName"/>, one file per hash.</summary>
    private const string StoreName = "files";

    private const string ListFormat = "osmap.backupfiles/1";

    /// <summary>How many backups a save keeps when the user has not chosen (RE+ kept the last five).</summary>
    public const int DefaultKeep = 5;

    public const int MaxKeep = 50;

    /// <summary>Backups closer together than this count as one run of saves.</summary>
    public static readonly TimeSpan RunGap = TimeSpan.FromMinutes(1);

    private const string NameFormat = "yyyyMMdd-HHmmss-fff";
    private const string SettingsFormat = "osmap.backups/1";

    /// <summary>The per-user file holding <see cref="Kept"/>.</summary>
    public static string SettingsFile => Path.Combine(UserFolders.Settings, "backups.json");

    /// <summary>How many backups each save keeps, for this user (0 keeps none). Read on every save, so a change in
    /// Preferences or <c>ose backups --keep</c> applies to every editor and command at once.</summary>
    public static int Kept
    {
        get
        {
            try
            {
                return File.Exists(SettingsFile) && SvJson.Parse(File.ReadAllBytes(SettingsFile), SettingsFile) is SvJsonObject o
                    && o["keep"] is SvJsonNumber n && n.TryGetInt32(out int keep) ? Math.Clamp(keep, 0, MaxKeep) : DefaultKeep;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenScct.Content.SvMapFormatException)
            {
                return DefaultKeep;
            }
        }
        set
        {
            var o = new SvJsonObject();
            o.Add("format", SvJsonNode.String(SettingsFormat));
            o.Add("keep", SvJsonNode.Integer(Math.Clamp(value, 0, MaxKeep)));
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllBytes(SettingsFile + ".tmp", CanonicalJson.ToFileBytes(o));
            File.Move(SettingsFile + ".tmp", SettingsFile, overwrite: true);
        }
    }

    /// <summary>The backups of a project, newest first.</summary>
    public static IReadOnlyList<ProjectBackup> List(string projectFolder)
    {
        string root = Path.Combine(projectFolder, FolderName);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetDirectories(root)
            .Select(d => (Folder: d, Name: Path.GetFileName(d)))
            .Where(d => ProjectStore.IsProject(d.Folder))
            .Select(d => new ProjectBackup(d.Name, d.Folder, ParseTime(d.Name) ?? Directory.GetLastWriteTimeUtc(d.Folder)))
            .OrderByDescending(b => b.SavedUtc)
            .ThenByDescending(b => b.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The project folders whose files each backup can bring back.</summary>
    public static IReadOnlyList<string> ListedFolders { get; } = [Assets.ProjectAssets.Folder, ProjectStore.PrefabsFolder];

    /// <summary>One listed file: its path in the project, size, last write time (UTC ticks) and SHA-256.</summary>
    private sealed record ListedFile(string Path, long Size, long Time, string Hash);

    /// <summary>
    /// The <see cref="FilesList"/> for a save: the project's assets and prefabs as they are now, each version the store
    /// lacks put into it first. A file whose size and time match the project's current list keeps its hash unread.
    /// </summary>
    internal static byte[] ListFiles(string projectFolder)
    {
        Dictionary<string, ListedFile> last = ReadList(Path.Combine(projectFolder, FilesList)).ToDictionary(f => f.Path, StringComparer.Ordinal);
        string store = StoreFolder(projectFolder);
        var files = new List<ListedFile>();
        foreach (string name in ListedFolders)
        {
            string root = Path.Combine(projectFolder, name);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(projectFolder, file).Replace('\\', '/');
                var info = new FileInfo(file);
                long time = info.LastWriteTimeUtc.Ticks;
                string hash = last.TryGetValue(rel, out ListedFile? known) && known.Size == info.Length && known.Time == time
                    && File.Exists(Path.Combine(store, known.Hash))
                    ? known.Hash
                    : Keep(file, store);
                files.Add(new ListedFile(rel, info.Length, time, hash));
            }
        }

        var items = new SvJsonArray();
        foreach (ListedFile f in files)
        {
            var o = new SvJsonObject();
            o.Add("path", SvJsonNode.String(f.Path));
            o.Add("size", SvJsonNode.Integer(f.Size));
            o.Add("time", SvJsonNode.Integer(f.Time));
            o.Add("sha256", SvJsonNode.String(f.Hash));
            items.Add(o);
        }

        var list = new SvJsonObject();
        list.Add("format", SvJsonNode.String(ListFormat));
        list.Add("files", items);
        return CanonicalJson.ToFileBytes(list);
    }

    /// <summary>
    /// Checks that a backup's assets and prefabs can be put back: true when it lists them and the store holds every
    /// file, false when it lists none (made before backups listed them, or by a save that could not list them; a
    /// restore then leaves the project's own alone). Throws <see cref="InvalidOperationException"/> naming a listed
    /// file the store lacks.
    /// </summary>
    public static bool CanRestoreFolders(string backupFolder, string projectFolder)
    {
        string listFile = Path.Combine(backupFolder, FilesList);
        if (!File.Exists(listFile))
        {
            return false;
        }

        string store = StoreFolder(projectFolder);
        if (ReadList(listFile).FirstOrDefault(f => !File.Exists(Path.Combine(store, f.Hash))) is { } missing)
        {
            throw new InvalidOperationException($"The backup's copy of {missing.Path} is missing from {store}, so it cannot be restored.");
        }

        return true;
    }

    /// <summary>
    /// Puts a backup's listed assets and prefabs back into the project (check <see cref="CanRestoreFolders"/> first).
    /// Each folder is built beside the project's and then swapped in, so a failure part way leaves the project's own.
    /// The files get their listed times back, so the next save finds them unchanged.
    /// </summary>
    public static void RestoreFolders(string backupFolder, string projectFolder)
    {
        List<ListedFile> files = ReadList(Path.Combine(backupFolder, FilesList));
        string store = StoreFolder(projectFolder);
        foreach (string name in ListedFolders)
        {
            string target = Path.Combine(projectFolder, name);
            string staging = target + ".restoring";
            string old = target + ".replaced";
            DeleteFolder(staging);
            DeleteFolder(old);
            List<ListedFile> mine = files.Where(f => f.Path.StartsWith(name + "/", StringComparison.Ordinal)).ToList();
            foreach (ListedFile f in mine)
            {
                string to = Path.Combine(staging, f.Path[(name.Length + 1)..]);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(store, f.Hash), to);
                File.SetLastWriteTimeUtc(to, new DateTime(f.Time, DateTimeKind.Utc));
            }

            if (Directory.Exists(target))
            {
                Directory.Move(target, old);
            }

            if (mine.Count > 0)
            {
                Directory.Move(staging, target);
            }

            DeleteFolder(old);
        }
    }

    /// <summary>The store folder of a project's listed file versions.</summary>
    private static string StoreFolder(string projectFolder) => Path.Combine(projectFolder, FolderName, StoreName);

    /// <summary>Puts a file's bytes into the store (once per hash) and returns the hash.</summary>
    private static string Keep(string file, string store)
    {
        byte[] bytes = File.ReadAllBytes(file);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string path = Path.Combine(store, hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(store);
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, overwrite: true);
        }

        return hash;
    }

    /// <summary>The files a list names; none for a missing or unreadable list.</summary>
    private static List<ListedFile> ReadList(string listFile)
    {
        try
        {
            if (!File.Exists(listFile) || SvJson.Parse(File.ReadAllBytes(listFile), listFile) is not SvJsonObject o || o["files"] is not SvJsonArray items)
            {
                return [];
            }

            var files = new List<ListedFile>();
            foreach (SvJsonObject f in items.OfType<SvJsonObject>())
            {
                if (f["path"] is SvJsonString path && f["sha256"] is SvJsonString hash && f["size"] is SvJsonNumber size && size.TryGetInt64(out long length)
                    && f["time"] is SvJsonNumber time && time.TryGetInt64(out long ticks))
                {
                    files.Add(new ListedFile(path.Value, length, ticks, hash.Value));
                }
            }

            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenScct.Content.SvMapFormatException)
        {
            return [];
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A fresh backup folder for a save made now (not created yet).</summary>
    public static string NewFolder(string projectFolder)
    {
        string root = Path.Combine(projectFolder, FolderName);
        string name = DateTime.UtcNow.ToString(NameFormat, CultureInfo.InvariantCulture);
        string path = Path.Combine(root, name);
        for (int i = 1; Directory.Exists(path); i++)
        {
            path = Path.Combine(root, $"{name}-{i}");
        }

        return path;
    }

    /// <summary>Thins each run of saves to its first and last backup, then deletes all but the newest
    /// <paramref name="keep"/>.</summary>
    public static void Prune(string projectFolder, int keep)
    {
        IReadOnlyList<ProjectBackup> all = List(projectFolder);
        var kept = new List<ProjectBackup>();
        for (int i = 0; i < all.Count; i++)
        {
            // Newest first: a backup with a close neighbour on both sides is in the middle of a run.
            bool middleOfRun = i > 0 && i < all.Count - 1
                && all[i - 1].SavedUtc - all[i].SavedUtc < RunGap && all[i].SavedUtc - all[i + 1].SavedUtc < RunGap;
            if (!middleOfRun)
            {
                kept.Add(all[i]);
            }
        }

        foreach (ProjectBackup old in all.Except(kept).Concat(kept.Skip(Math.Max(keep, 0))))
        {
            Directory.Delete(old.Folder, recursive: true);
        }

        DropUnlisted(projectFolder);
    }

    /// <summary>Deletes the stored file versions that neither a kept backup nor the project's own list names.</summary>
    private static void DropUnlisted(string projectFolder)
    {
        string store = StoreFolder(projectFolder);
        if (!Directory.Exists(store))
        {
            return;
        }

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string listFile in List(projectFolder).Select(b => Path.Combine(b.Folder, FilesList)).Append(Path.Combine(projectFolder, FilesList)))
        {
            named.UnionWith(ReadList(listFile).Select(f => f.Hash));
        }

        foreach (string file in Directory.EnumerateFiles(store))
        {
            if (!named.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>The backups as the <c>backups</c> query and <c>ose backups --json</c> give them, newest first.</summary>
    public static SvJsonObject ToJson(string? projectFolder)
    {
        IReadOnlyList<ProjectBackup> all = projectFolder is null ? [] : List(projectFolder);
        var items = new SvJsonArray();
        for (int i = 0; i < all.Count; i++)
        {
            var b = new SvJsonObject();
            b.Add("n", SvJsonNode.Integer(i + 1));
            b.Add("name", SvJsonNode.String(all[i].Name));
            b.Add("saved", SvJsonNode.String(all[i].SavedUtc.ToString("O", CultureInfo.InvariantCulture)));
            b.Add("folder", SvJsonNode.String(all[i].Folder));
            items.Add(b);
        }

        var o = new SvJsonObject();
        o.Add("keep", SvJsonNode.Integer(Kept));
        o.Add("backups", items);
        return o;
    }

    /// <summary>Finds a backup by name (as <see cref="List"/> gives it), or by its place in the list (1 = newest).</summary>
    public static ProjectBackup? Find(string projectFolder, string nameOrIndex)
    {
        IReadOnlyList<ProjectBackup> all = List(projectFolder);
        if (int.TryParse(nameOrIndex, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= all.Count)
        {
            return all[n - 1];
        }

        return all.FirstOrDefault(b => string.Equals(b.Name, nameOrIndex, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime? ParseTime(string name) =>
        DateTime.TryParseExact(name.Length >= NameFormat.Length ? name[..NameFormat.Length] : name, NameFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime t) ? t : null;
}
