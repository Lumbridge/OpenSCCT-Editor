using System.Net.Http;
using System.Security.Cryptography;
using OpenScct.Maps;

namespace OpenScct.Editor.Core.Services;

public enum ContentPackState
{
    Available,
    Installed,
    UpdateAvailable,
    Local,
}

public sealed record ContentPackEntry(string Id, string Code, string Title, string Category, string Version, long Size, ContentPackState State);

public readonly record struct ContentPackProgress(string Title, long Received, long Total, bool Extracting);

public sealed record ContentPackInstall(ContentPackEntry Pack, bool Changed, IReadOnlyList<string> Warnings);

/// <summary>Official converted asset packs, using the same index and verified installer as OpenSCCT.</summary>
public sealed class ContentPacks
{
    private static readonly HttpClient s_http = new();
    private readonly ContentStore _store;
    private int _revision;

    public ContentPacks(string? root = null, HttpClient? http = null)
    {
        string? explicitContent = GameExe.ManagedLauncherRoot is null ? Environment.GetEnvironmentVariable("OSE_CONTENT") : null;
        string destination = root ?? (string.IsNullOrWhiteSpace(explicitContent) ? null : explicitContent)
            ?? EditorServices.ContentRoot ?? Path.Combine(GameExe.LauncherRoot, "packs");
        _store = new ContentStore(destination, http ?? s_http);
    }

    public string Root => _store.Root;

    /// <summary>Changes after extraction starts. A dependency can change it even if a later download fails.</summary>
    public int Revision => Volatile.Read(ref _revision);

    public async Task<IReadOnlyList<ContentPackEntry>> ListAsync(CancellationToken cancel)
    {
        using FileStream guard = await LockAsync(cancel).ConfigureAwait(false);
        ContentIndex index = await ReadIndexAsync(cancel).ConfigureAwait(false);
        IReadOnlyList<InstalledContent> installed = Installed();
        return index.Shared.Where(HasSafeContents).Select(pack => SharedEntry(pack, installed))
            .Concat(index.Maps.Where(HasSafeMap).Select(map => MapEntry(map, installed))).ToList();
    }

    public async Task<ContentPackInstall> InstallAsync(string id, IProgress<ContentPackProgress>? progress, CancellationToken cancel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using FileStream guard = await LockAsync(cancel).ConfigureAwait(false);
        ContentIndex index = await ReadIndexAsync(cancel).ConfigureAwait(false);
        IReadOnlyList<InstalledContent> installed = Installed();
        int before = Revision;
        if (id.StartsWith("shared/", StringComparison.OrdinalIgnoreCase))
        {
            ContentSharedPack shared = index.FindShared(id["shared/".Length..])
                ?? throw new ArgumentException($"No content pack named '{id}'.", nameof(id));
            ContentPackEntry entry = SharedEntry(shared, installed);
            if (entry.State != ContentPackState.Installed)
            {
                EnsureWritable(entry);
                await InstallSharedAsync(shared, progress, cancel).ConfigureAwait(false);
            }

            return new ContentPackInstall(entry with { State = ContentPackState.Installed }, before != Revision, []);
        }

        ContentMap map = index.Maps.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? index.FindMap(id) ?? throw new ArgumentException($"No content pack named '{id}'.", nameof(id));
        if (!HasSafeMap(map))
        {
            throw new InvalidDataException($"{map.DisplayTitle} does not have a valid install path or checksum.");
        }

        ContentPackEntry mapEntry = MapEntry(map, installed);
        EnsureWritable(mapEntry);
        var warnings = new List<string>();
        foreach (ContentSharedPack dependency in Dependencies(index, map, installed, warnings))
        {
            await InstallSharedAsync(dependency, progress, cancel).ConfigureAwait(false);
        }

        if (mapEntry.State != ContentPackState.Installed)
        {
            InstalledContent written = await InstallAsync(mapEntry.Title, progress, report => _store.InstallMapAsync(map, ContentKind.Full, report, cancel))
                .ConfigureAwait(false);
            if (written.Kind != ContentKind.Full)
            {
                throw new InvalidDataException($"{map.DisplayTitle} did not install as a full map pack.");
            }
        }

        if (!File.Exists(Path.Combine(_store.SvMapRoot, map.Code, "manifest.json")))
        {
            throw new InvalidDataException($"The installed {map.DisplayTitle} pack is missing its map manifest.");
        }

        return new ContentPackInstall(mapEntry with { State = ContentPackState.Installed }, before != Revision, warnings);
    }

    private IReadOnlyList<ContentSharedPack> Dependencies(ContentIndex index, ContentMap map, IReadOnlyList<InstalledContent> installed,
        List<string> warnings)
    {
        var result = new List<ContentSharedPack>();
        var packs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ContentRequirement requirement in map.Requires)
        {
            string? file = RequirementFile(requirement.Pack);
            bool localFile = file is not null && File.Exists(file);
            if (localFile && SameHash(FileHash(file!), requirement.Sha256))
            {
                continue;
            }

            ContentSharedPack? shared = index.Shared.FirstOrDefault(pack =>
                pack.Hashes.TryGetValue(requirement.Pack, out string? hash) && SameHash(hash, requirement.Sha256));
            if (shared is null)
            {
                if (localFile)
                {
                    warnings.Add($"{map.DisplayTitle} will use the local {requirement.Pack} data. Its listed version is unavailable.");
                    continue;
                }

                throw new InvalidDataException($"{map.DisplayTitle} requires {requirement.Pack}, which the content index does not provide.");
            }

            ContentPackEntry entry = SharedEntry(shared, installed);
            if (entry.State == ContentPackState.Local && localFile)
            {
                warnings.Add($"{map.DisplayTitle} will use the local {requirement.Pack} data. It differs from the published pack.");
                continue;
            }

            EnsureWritable(entry);
            if ((file is not null || entry.State != ContentPackState.Installed) && packs.Add(shared.Id))
            {
                result.Add(shared);
            }
        }

        return result;
    }

    private ContentPackEntry MapEntry(ContentMap map, IReadOnlyList<InstalledContent> installed)
    {
        string folder = Path.Combine(_store.SvMapRoot, map.Code);
        InstalledContent? entry = installed.FirstOrDefault(c => !c.IsShared && string.Equals(c.Code, map.Code, StringComparison.OrdinalIgnoreCase));
        bool present = Directory.Exists(folder);
        ContentPackState state;
        if (File.Exists(folder) || IsLink(folder) || (present && entry is null))
        {
            state = ContentPackState.Local;
        }
        else if (!present)
        {
            state = ContentPackState.Available;
        }
        else
        {
            state = entry?.Kind == ContentKind.Full && SameHash(entry.Sha256, map.Full.Sha256) && File.Exists(Path.Combine(folder, "manifest.json"))
                ? ContentPackState.Installed : ContentPackState.UpdateAvailable;
        }

        return new ContentPackEntry(map.Id, map.Code, map.DisplayTitle, map.Category, map.Version, map.Full.Size, state);
    }

    private ContentPackEntry SharedEntry(ContentSharedPack pack, IReadOnlyList<InstalledContent> installed)
    {
        if (!HasSafeContents(pack))
        {
            throw new InvalidDataException($"The {pack.Id} shared pack does not list valid install paths.");
        }

        InstalledContent? entry = installed.FirstOrDefault(c => c.IsShared && string.Equals(c.Code, pack.Id, StringComparison.OrdinalIgnoreCase));
        string[] files = pack.Contents.Select(name => Path.Combine(_store.SharedRoot, name)).ToArray();
        ContentPackState state;
        if (files.Any(IsLink) || (entry is null && files.Any(Exists)))
        {
            state = ContentPackState.Local;
        }
        else if (entry is null)
        {
            state = ContentPackState.Available;
        }
        else
        {
            state = SameHash(entry.Sha256, pack.Download.Sha256) && SharedProblem(pack) is null
                ? ContentPackState.Installed : ContentPackState.UpdateAvailable;
        }

        string title = pack.Title.Length > 0 ? pack.Title : pack.Id;
        return new ContentPackEntry("shared/" + pack.Id, pack.Id, title, "shared", pack.Version, pack.Download.Size, state);
    }

    private IReadOnlyList<InstalledContent> Installed() => _store.Installed()
        .Where(entry => entry is { Id.Length: > 0, Code.Length: > 0 }).ToList();

    private static bool HasSafeMap(ContentMap map) => SafeName(map.Code) && SafeHash(map.Full.Sha256);

    private static bool HasSafeContents(ContentSharedPack pack) => SafeName(pack.Id) && SafeHash(pack.Download.Sha256) &&
        pack.Contents.Count > 0 && pack.Contents.All(SafeName);

    private static bool SafeName(string name) => ContentIndex.IsSafeCode(name) && !name.EndsWith('.');

    private static bool SafeHash(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigit);

    private static void EnsureWritable(ContentPackEntry entry)
    {
        if (entry.State == ContentPackState.Local)
        {
            throw new InvalidOperationException($"{entry.Title} ({entry.Code}) has local files at the install location.");
        }
    }

    private async Task<ContentIndex> ReadIndexAsync(CancellationToken cancel)
    {
        ContentIndex index = await _store.FetchIndexAsync(cancel).ConfigureAwait(false);
        return index.Problem is null ? index : throw new InvalidDataException(index.Problem);
    }

    private string? RequirementFile(string key) => key.ToLowerInvariant() switch
    {
        "classes" => Path.Combine(_store.SharedRoot, "classes.json"),
        "audio" => Path.Combine(_store.SharedRoot, "audio", "audio.json"),
        _ => null,
    };

    private async Task InstallSharedAsync(ContentSharedPack pack, IProgress<ContentPackProgress>? progress, CancellationToken cancel)
    {
        await InstallAsync(pack.Title.Length > 0 ? pack.Title : pack.Id, progress, report => _store.InstallSharedAsync(pack, report, cancel))
            .ConfigureAwait(false);
        if (SharedProblem(pack) is { } problem)
        {
            throw new InvalidDataException(problem);
        }
    }

    private string? SharedProblem(ContentSharedPack pack)
    {
        foreach (string name in pack.Contents)
        {
            if (!Exists(Path.Combine(_store.SharedRoot, name)))
            {
                return $"The {pack.Id} pack is missing {name}.";
            }
        }

        foreach ((string key, string hash) in pack.Hashes)
        {
            if (RequirementFile(key) is { } file && (!File.Exists(file) || !SameHash(FileHash(file), hash)))
            {
                return $"The installed {pack.Id} pack's {key} data differs from the content index.";
            }
        }

        return null;
    }

    private async Task<InstalledContent> InstallAsync(string title, IProgress<ContentPackProgress>? progress,
        Func<IProgress<PackProgress>, Task<InstalledContent>> install)
    {
        bool extracting = false;
        try
        {
            return await install(new PackReporter(p =>
            {
                extracting |= p.Extracting;
                progress?.Report(new ContentPackProgress(title, p.Received, p.Total, p.Extracting));
            })).ConfigureAwait(false);
        }
        finally
        {
            if (extracting)
            {
                Interlocked.Increment(ref _revision);
            }
        }
    }

    // ContentStore guards its own instance. An exclusive handle also serializes editor and ose processes
    // that use this service and share the same root, including their updates to installed.json.
    private async Task<FileStream> LockAsync(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Root);
        const string lockName = ".openscct-editor-packs.lock";
        string path = Path.Combine(Root, lockName);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (IsLink(path))
            {
                throw new IOException($"The content pack lock is a link: {path}");
            }

            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33)
            {
                await Task.Delay(150, cancel).ConfigureAwait(false);
                continue;
            }

            try
            {
                foreach (string name in new[] { "svmap", "shared", "downloads", ".staging", "cache", "installed.json", "installed.json.tmp",
                             "cache/index.json", "cache/index.etag" })
                {
                    if (IsLink(Path.Combine(Root, name)))
                    {
                        throw new IOException($"The content pack location contains a link: {name}");
                    }
                }

                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool SameHash(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string FileHash(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private sealed class PackReporter(Action<PackProgress> report) : IProgress<PackProgress>
    {
        public void Report(PackProgress value) => report(value);
    }
}
