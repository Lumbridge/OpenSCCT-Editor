using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenScct.Editor.Core.Interop.Sdc.Lights;

/// <summary>The baked lightmap atlases of a map's world model, as SHA-1 hashes in sorted order. One stock Save writes
/// the editor copy and the cooked copy from the same bake, so their atlases are byte-identical: the fingerprint finds
/// the editor copy a cooked map came from, under any file name.</summary>
public sealed record AtlasFingerprint(IReadOnlyList<string> Hashes)
{
    public static readonly AtlasFingerprint None = new([]);

    public bool IsEmpty => Hashes.Count == 0;

    public bool SameAs(AtlasFingerprint other) => !IsEmpty && Hashes.SequenceEqual(other.Hashes, StringComparer.Ordinal);

    /// <summary>How many atlases the two share (a twin edited after the cook keeps the atlases of rooms it did not relight).</summary>
    public int Shared(AtlasFingerprint other) => Hashes.Intersect(other.Hashes, StringComparer.Ordinal).Count();

    /// <summary>The world model's atlases (and, for an editor copy, the lights its lightmaps list) of a read package.</summary>
    public static (AtlasFingerprint Fingerprint, UModel? World, SdcLayout Layout) Of(UPackage pkg)
    {
        SdcLayout layout = SdcLayoutDetector.Detect(pkg);
        ULevel level = ULevelReader.Read(pkg, ULevelReader.Find(pkg));
        if (level.WorldModel <= 0)
        {
            return (None, null, layout);
        }

        UModel world = new UModelReader(layout).Read(pkg, pkg.Export(level.WorldModel), summary: true, lighting: true);
        return (new AtlasFingerprint(world.Atlases.Select(a => a.Hash).Order(StringComparer.Ordinal).ToList()), world, layout);
    }
}

/// <summary>An editor copy the library knows: where it is, the package name and its atlas fingerprint.</summary>
public sealed record TwinEntry(string Path, long Size, DateTime Modified, string Package, AtlasFingerprint Fingerprint, string? Error);

/// <summary>
/// The editor copies (<c>Packages\MapsEd\*.sdc</c>, autosaves included) of every install the editor can find, by atlas
/// fingerprint: Tier 1a of the light recovery. The installs searched are the import's own, the game install, the
/// installs next to those (siblings and their children, as a folder of installs keeps them), and the folders in
/// <see cref="FoldersVariable"/> or given by the caller. The fingerprints are cached in
/// <c>&lt;editor data&gt;\twins.json</c>, keyed by file size and write time, so only new or changed files are read.
/// </summary>
public sealed class TwinLibrary
{
    /// <summary>More folders to search (editor copies, or installs), separated by the path separator.</summary>
    public const string FoldersVariable = "OSE_TWIN_FOLDERS";

    private const int CacheVersion = 1;

    private TwinLibrary(IReadOnlyList<TwinEntry> entries) => Entries = entries;

    public IReadOnlyList<TwinEntry> Entries { get; }

    public static string CacheFile => System.IO.Path.Combine(SvmbuildRunner.DataRoot, "twins.json");

    /// <summary>The editor copies whose atlases are the cooked map's exactly: those of the same package name first, then
    /// the newest.</summary>
    public IReadOnlyList<TwinEntry> FindExact(AtlasFingerprint cooked, string package) =>
        cooked.IsEmpty ? [] : Entries.Where(e => e.Fingerprint.SameAs(cooked))
            .OrderByDescending(e => e.Package.Equals(package, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Modified)
            .ToList();

    /// <summary>Editor copies that are not the cooked map's bake but may be its source edited since: the same package
    /// name, or any atlas in common; most atlases in common first.</summary>
    public IReadOnlyList<TwinEntry> Stale(AtlasFingerprint cooked, string package) =>
        Entries.Where(e => !e.Fingerprint.IsEmpty && !e.Fingerprint.SameAs(cooked)
                && (e.Package.Equals(package, StringComparison.OrdinalIgnoreCase) || e.Fingerprint.Shared(cooked) > 0))
            .OrderByDescending(e => e.Fingerprint.Shared(cooked))
            .ThenByDescending(e => e.Package.Equals(package, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Modified)
            .ToList();

    /// <summary>The folders searched for a map at <paramref name="sdc"/> with the game install <paramref name="gameRoot"/>.</summary>
    public static IReadOnlyList<string> DefaultFolders(string? sdc, string? gameRoot, IEnumerable<string>? extra = null)
    {
        var installs = new List<string>();
        void AddInstall(string? root)
        {
            if (root is not null && IsInstall(root) && !installs.Contains(System.IO.Path.GetFullPath(root), StringComparer.OrdinalIgnoreCase))
            {
                installs.Add(System.IO.Path.GetFullPath(root));
            }
        }

        string? own = sdc is null ? null : new FileInfo(sdc).Directory?.Parent?.Parent?.FullName;
        string? env = Environment.GetEnvironmentVariable("SVM_GAME_ROOT");
        foreach (string? root in new[] { own, gameRoot, string.IsNullOrEmpty(env) ? null : env, SdcImporter.DefaultGameRoot })
        {
            AddInstall(root);
        }

        foreach (string root in installs.ToList())
        {
            foreach (string near in InstallsNear(root))
            {
                AddInstall(near);
            }
        }

        var folders = installs.Select(i => System.IO.Path.Combine(i, "Packages", "MapsEd")).Where(Directory.Exists).ToList();
        string? variable = Environment.GetEnvironmentVariable(FoldersVariable);
        IEnumerable<string> more = (variable ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(extra ?? []);
        foreach (string f in more)
        {
            string full = System.IO.Path.GetFullPath(f);
            string mapsEd = System.IO.Path.Combine(full, "Packages", "MapsEd");
            string folder = Directory.Exists(mapsEd) ? mapsEd : full;
            if (Directory.Exists(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    /// <summary>Installs beside <paramref name="root"/>: its siblings, and their children (a folder of installs).</summary>
    private static IEnumerable<string> InstallsNear(string root)
    {
        string? parent = Directory.GetParent(root)?.FullName;
        if (parent is null)
        {
            yield break;
        }

        var walk = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, IgnoreInaccessible = true };
        foreach (string sibling in Directory.EnumerateDirectories(parent, "*", walk))
        {
            if (IsInstall(sibling))
            {
                yield return sibling;
                continue;
            }

            foreach (string child in Directory.EnumerateDirectories(sibling, "*", walk))
            {
                if (IsInstall(child))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool IsInstall(string root) =>
        Directory.Exists(System.IO.Path.Combine(root, "Packages")) && File.Exists(System.IO.Path.Combine(root, "System", "_PC_", "Core.u"));

    /// <summary>Fingerprints every <c>.sdc</c> in <paramref name="folders"/> (cached ones are not read again) and keeps the
    /// editor copies. Maps rebuilt by RE+ map recovery (<c>*_Recovered*</c>) are never a reference, so they are left out;
    /// a folder that cannot be listed is skipped.</summary>
    public static TwinLibrary Scan(IReadOnlyList<string> folders, Action<string>? progress = null)
    {
        Dictionary<string, TwinEntry> cached = ReadCache();
        var files = folders.SelectMany(SdcFiles)
            .Where(f => !System.IO.Path.GetFileName(f).Contains("_Recovered", StringComparison.OrdinalIgnoreCase))
            .Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new ConcurrentBag<TwinEntry>();
        var todo = new List<FileInfo>();
        foreach (string f in files)
        {
            var info = new FileInfo(f);
            if (!info.Exists)
            {
                continue;
            }

            if (cached.TryGetValue(f, out TwinEntry? e) && e.Size == info.Length && e.Modified == info.LastWriteTimeUtc)
            {
                result.Add(e);
            }
            else
            {
                todo.Add(info);
            }
        }

        if (todo.Count > 0)
        {
            progress?.Invoke($"light recovery: fingerprinting {todo.Count} editor copies in {folders.Count} folders (once; cached in {CacheFile})");
            // A package is read whole and decompressed: a few at a time keeps memory in check.
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6) },
                info => result.Add(Fingerprint(info)));
        }

        var entries = result.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (todo.Count > 0)
        {
            WriteCache(entries, cached);
        }

        return new TwinLibrary(entries.Where(e => e.Error is null).ToList());
    }

    private static IEnumerable<string> SdcFiles(string folder)
    {
        try
        {
            return Directory.GetFiles(folder, "*.sdc", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Reads one file's fingerprint; cooked copies and files that do not read are kept with an empty one, so the
    /// cache does not read them again. Any file of any install may be damaged or of another format, so no failure of
    /// one file stops the scan.</summary>
    private static TwinEntry Fingerprint(FileInfo info)
    {
        string package = System.IO.Path.GetFileNameWithoutExtension(info.Name);
        try
        {
            (AtlasFingerprint fp, _, SdcLayout layout) = AtlasFingerprint.Of(UPackage.Load(info.FullName));
            return new TwinEntry(info.FullName, info.Length, info.LastWriteTimeUtc, package,
                layout == SdcLayout.Editor ? fp : AtlasFingerprint.None, layout == SdcLayout.Editor ? null : "a cooked copy");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            return new TwinEntry(info.FullName, info.Length, info.LastWriteTimeUtc, package, AtlasFingerprint.None, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Dictionary<string, TwinEntry> ReadCache()
    {
        var map = new Dictionary<string, TwinEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(CacheFile) || JsonNode.Parse(File.ReadAllBytes(CacheFile)) is not JsonObject root
                || root["version"]?.GetValue<int>() != CacheVersion || root["files"] is not JsonArray list)
            {
                return map;
            }

            foreach (JsonNode? n in list)
            {
                if (n is not JsonObject o || o["path"]?.GetValue<string>() is not { } path)
                {
                    continue;
                }

                var hashes = (o["atlases"] as JsonArray ?? []).Select(h => h!.GetValue<string>()).ToList();
                map[path] = new TwinEntry(path, o["size"]!.GetValue<long>(), DateTime.FromBinary(o["modified"]!.GetValue<long>()),
                    o["package"]?.GetValue<string>() ?? System.IO.Path.GetFileNameWithoutExtension(path), new AtlasFingerprint(hashes),
                    o["error"]?.GetValue<string>());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            // A damaged cache is rebuilt.
        }

        return map;
    }

    /// <summary>Writes the cache: this scan's entries plus earlier ones from folders this scan did not search.</summary>
    private static void WriteCache(List<TwinEntry> entries, Dictionary<string, TwinEntry> earlier)
    {
        var all = new Dictionary<string, TwinEntry>(earlier, StringComparer.OrdinalIgnoreCase);
        foreach (TwinEntry e in entries)
        {
            all[e.Path] = e;
        }

        var list = new JsonArray();
        foreach (TwinEntry e in all.Values.Where(e => File.Exists(e.Path)).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            var o = new JsonObject
            {
                ["path"] = e.Path,
                ["size"] = e.Size,
                ["modified"] = e.Modified.ToBinary(),
                ["package"] = e.Package,
                ["atlases"] = new JsonArray(e.Fingerprint.Hashes.Select(h => (JsonNode)JsonValue.Create(h)).ToArray()),
            };
            if (e.Error is not null)
            {
                o["error"] = e.Error;
            }

            list.Add(o);
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CacheFile)!);
            string temp = CacheFile + ".tmp";
            File.WriteAllText(temp, new JsonObject { ["version"] = CacheVersion, ["files"] = list }.ToJsonString());
            File.Move(temp, CacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The library still works this time; the next scan reads the files again.
        }
    }
}
