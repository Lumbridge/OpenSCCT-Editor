using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using OpenScct.Launcher.GitHub;
using OpenScct.Launcher.Install;
using OpenScct.Launcher.Update;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Core.Updates;

/// <summary>An update that failed for a reason the user can act on; the message is written for them.</summary>
public class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class UpdateBusyException(string message) : UpdateException(message);

/// <summary>A new build unpacked and checked in the install's <c>.update</c> folder, ready to swap in.</summary>
public sealed record StagedUpdate(SemVersion Version, string Folder, InstallInfo Info);

/// <summary>
/// Gets a new build ready while the editor keeps running: takes the updates lock, checks the install folder can be
/// written and has room, downloads the archive (resuming an earlier partial), checks it against the published
/// checksum, unpacks it into <c>&lt;install&gt;\.update\&lt;version&gt;</c> with every entry checked, and checks the
/// unpacked build, every file against the size and SHA-256 its <c>version.json</c> lists. The folder is then marked
/// <c>.staged</c>; another editor that finds the mark uses it (after checking its files again).
/// </summary>
public sealed class UpdateStager
{
    public const int MaxEntries = 20_000;
    public const long MaxUnpackedBytes = 2L << 30;

    public const string ReadOnlyMessage =
        "The editor's folder is read-only for your account. Extract the new version over it yourself, or move the editor " +
        "to a folder you own (for example Documents).";

    private const int MaxChecksumBytes = 64 << 10;
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    private static readonly HashSet<string> s_reservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] s_invalidNameChars = ['<', '>', ':', '"', '|', '?', '*'];

    private readonly HttpClient _http;
    private readonly UpdatePaths _paths;
    private readonly string _install;
    private readonly UpdateLog _log;

    public UpdateStager(HttpClient http, UpdatePaths paths, string install, UpdateLog log)
    {
        _http = http;
        _paths = paths;
        _install = Path.GetFullPath(install);
        _log = log;
    }

    /// <summary>The newest build staged in the install that is newer than <paramref name="current"/>, or null.</summary>
    public static StagedUpdate? FindStaged(string install, SemVersion current)
    {
        string work = UpdatePaths.WorkFolder(install);
        if (!Directory.Exists(work))
        {
            return null;
        }

        StagedUpdate? best = null;
        foreach (string dir in Directory.EnumerateDirectories(work))
        {
            if (!SemVersion.TryParse(Path.GetFileName(dir), out SemVersion? v) || v <= current || (best is not null && v <= best.Version)
                || !File.Exists(Path.Combine(dir, UpdatePaths.StagedMarkName)))
            {
                continue;
            }

            InstallInfo? info = InstallInfo.TryRead(dir);
            if (info is not null && info.ParsedVersion == v && Problem(dir, info, v) is null)
            {
                best = new StagedUpdate(v, dir, info);
            }
        }

        return best;
    }

    /// <summary>Whether the install folder can be written (an update can swap it).</summary>
    public static bool CanWrite(string install) => new LauncherSwap(Path.Combine(install, EditorRelease.ExeName)).CanWriteFolder();

    public async Task<StagedUpdate> StageAsync(UpdateOffer offer, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(offer);
        using UpdateLock held = UpdateLock.TryTake(_paths)
            ?? throw new UpdateBusyException("Another OpenSCCT Editor window is downloading or installing an update. It will finish it.");
        using UpdateLock swap = UpdateLock.TryTake(UpdatePaths.SwapLock(_install))
            ?? throw new UpdateBusyException("Another updater is changing this editor installation. Try again when it finishes.");
        cancel.ThrowIfCancellationRequested();
        bool managed = LauncherIntegration.IsManaged(_install);

        string staging = UpdatePaths.Staged(_install, offer.Version);
        if (FindStaged(_install, SemVersion.Parse("0.0.0")) is { } ready && ready.Version == offer.Version)
        {
            if ((CheckFiles(ready.Folder, ready.Info) ?? (managed ? LauncherIntegration.Problem(ready.Info) : null)) is not { } damaged)
            {
                _log.Write($"{offer.Version} is already staged in {ready.Folder}");
                return ready;
            }

            _log.Write($"the staged {offer.Version} is damaged ({damaged}); unpacking it again");
            TryDelete(ready.Folder);
        }

        if (!CanWrite(_install))
        {
            throw new UpdateException(ReadOnlyMessage);
        }

        long size = Math.Max(offer.Archive.Size, 0);
        RequireSpace(_paths.Root, size);
        RequireSpace(_install, size * 3);

        var client = new GitHubReleaseClient(_http, EditorRelease.Source, _paths.Cache);
        if (managed) await LauncherIntegration.ValidateOfferAsync(_http, client, offer, cancel).ConfigureAwait(false);
        string expected = await ExpectedHashAsync(client, offer, cancel).ConfigureAwait(false);
        string zip = _paths.Download(offer.Archive.Name);
        _log.Write($"downloading {offer.Archive.Name} ({offer.Archive.Size} bytes) to {zip}");
        try
        {
            await new ResumableDownloader(_http).DownloadAsync(() => client.CreateAssetRequest(offer.Archive), zip,
                new DownloadIdentity(offer.Archive.Id, offer.Archive.Size, offer.Archive.UpdatedAt), progress, cancel).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            _log.Write($"download stopped: {ex.Message}");
            throw new UpdateException(SpaceMessage(_paths.Root, size), ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            _log.Write($"download stopped: {ex.Message}");
            throw new UpdateException("The download was interrupted. It resumes where it stopped next time.", ex);
        }

        if (!await Checksums.VerifyAsync(zip, expected, cancel).ConfigureAwait(false))
        {
            _log.Write($"checksum mismatch for {zip}; deleted");
            File.Delete(zip);
            throw new UpdateException("The download was damaged or is not the published file. Try again later.");
        }

        _log.Write($"checksum ok; unpacking into {staging}");
        try
        {
            Directory.CreateDirectory(UpdatePaths.WorkFolder(_install));
            HideWorkFolder();
            SafeDelete.Tree(staging, UpdatePaths.WorkFolder(_install));
            Unpack(zip, staging, cancel);
            InstallInfo info = InstallInfo.Read(staging);
            if ((Problem(staging, info, offer.Version) ?? (managed ? LauncherIntegration.Problem(info) : null) ?? CheckFiles(staging, info)) is { } problem)
            {
                throw new InvalidDataException(problem);
            }

            cancel.ThrowIfCancellationRequested();
            AtomicFile.WriteAllText(Path.Combine(staging, UpdatePaths.StagedMarkName), offer.Version.ToString());
            _log.Write($"staged {offer.Version} in {staging}");
            return new StagedUpdate(offer.Version, staging, info);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            _log.Write($"unpacking failed: {ex.Message}");
            if (IsDiskFull(ex))
            {
                throw new UpdateException(SpaceMessage(_install, size * 3), ex);
            }

            if (ex is InvalidDataException)
            {
                File.Delete(zip);
                throw new UpdateException($"The downloaded update is not a valid OpenSCCT Editor build ({ex.Message}). It was deleted.", ex);
            }

            throw new UpdateException($"The update could not be unpacked: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Unpacks a release archive into <paramref name="target"/>. Every entry must sit under the archive's single top
    /// folder and be a plain relative path (no root, drive, "..", alternate stream, reserved device name or name Windows
    /// cannot hold), resolve inside the target, and the archive must stay under <see cref="MaxEntries"/> entries and
    /// <see cref="MaxUnpackedBytes"/> unpacked. Each file is written under a temporary name first. Throws
    /// <see cref="InvalidDataException"/> for an archive that breaks a rule.
    /// </summary>
    public static void Unpack(string zip, string target, CancellationToken cancel = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)) + Path.DirectorySeparatorChar;
        string prefix = EditorRelease.FolderName + "/";
        using ZipArchive archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"the archive has {archive.Entries.Count} entries (at most {MaxEntries})");
        }

        Directory.CreateDirectory(root);
        long total = 0;
        byte[] buffer = new byte[1 << 16];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"'{entry.FullName}' is outside the '{EditorRelease.FolderName}' folder");
            }

            string relative = name[prefix.Length..];
            bool folder = relative.EndsWith('/');
            relative = relative.TrimEnd('/');
            if (relative.Length == 0)
            {
                continue;
            }

            if (!IsSafeEntryPath(relative))
            {
                throw new InvalidDataException($"'{entry.FullName}' is not a safe path");
            }

            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{entry.FullName}' leads outside the folder");
            }

            if (folder)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            total += entry.Length;
            if (total > MaxUnpackedBytes)
            {
                throw new InvalidDataException($"the archive unpacks to more than {MaxUnpackedBytes >> 30} GB");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            using (Stream source = entry.Open())
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                long written = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    written += read;
                    if (written > entry.Length)
                    {
                        throw new InvalidDataException($"'{entry.FullName}' unpacks to more than its stated size");
                    }

                    file.Write(buffer, 0, read);
                }

                if (written != entry.Length)
                {
                    throw new InvalidDataException($"'{entry.FullName}' unpacks to less than its stated size");
                }
            }

            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>A relative path an archive may hold: <see cref="BuildFiles.IsSafeRelativePath"/>, no part that is a
    /// reserved device name, ends in a dot or space or has a character file names cannot have, and not under the update
    /// work folder.</summary>
    public static bool IsSafeEntryPath(string relative)
    {
        if (!BuildFiles.IsSafeRelativePath(relative))
        {
            return false;
        }

        string[] parts = relative.Split('/');
        if (string.Equals(parts[0], UpdatePaths.WorkFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string part in parts)
        {
            string stem = part.Split('.')[0];
            if (s_reservedNames.Contains(stem) || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(s_invalidNameChars) >= 0
                || part.Any(c => c < ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Why an unpacked build cannot be installed as <paramref name="version"/>, or null when it can.</summary>
    public static string? Problem(string folder, InstallInfo info, SemVersion version)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!string.Equals(info.Product, EditorRelease.Product, StringComparison.Ordinal))
        {
            return $"it is '{info.Product}', not {EditorRelease.Product}";
        }

        if (info.ParsedVersion != version)
        {
            return $"it says it is version {info.Version}, not {version}";
        }

        if (!string.Equals(info.Platform, EditorRelease.Platform, StringComparison.OrdinalIgnoreCase))
        {
            return $"it is for {info.Platform}, not {EditorRelease.Platform}";
        }

        if (info.Format > InstallInfo.CurrentFormat)
        {
            return $"it needs a newer updater (format {info.Format})";
        }

        foreach (string e in info.Entries)
        {
            if (!File.Exists(Path.Combine(folder, e)) && !Directory.Exists(Path.Combine(folder, e)))
            {
                return $"'{e}' is missing";
            }
        }

        if (!info.Entries.Contains(EditorRelease.ExeName, StringComparer.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(folder, EditorRelease.CliFolder, EditorRelease.CliExeName)))
        {
            return $"it has no {EditorRelease.ExeName} or {EditorRelease.CliFolder}/{EditorRelease.CliExeName}";
        }

        return null;
    }

    /// <summary>
    /// Why the files of an unpacked build do not match its <c>version.json</c>, or null when every listed file is there
    /// with its size and SHA-256. Catches a short or damaged file, and one an antivirus removed or quarantined.
    /// </summary>
    public static string? CheckFiles(string folder, InstallInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Files.Count == 0)
        {
            return "it has no file list";
        }

        var entries = new HashSet<string>(info.Entries, StringComparer.OrdinalIgnoreCase);
        foreach (InstallFile f in info.Files)
        {
            if (!IsSafeEntryPath(f.Path) || !entries.Contains(f.Path.Split('/')[0]) || !Checksums.IsSha256Hex(f.Sha256))
            {
                return $"its file list has a bad entry ('{f.Path}')";
            }

            var file = new FileInfo(Path.Combine(folder, f.Path.Replace('/', Path.DirectorySeparatorChar)));
            try
            {
                if (!file.Exists)
                {
                    return $"'{f.Path}' is missing";
                }

                if (file.Length != f.Size)
                {
                    return $"'{f.Path}' has {file.Length} bytes, not {f.Size}";
                }

                using FileStream stream = file.OpenRead();
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return $"'{f.Path}' is damaged";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"'{f.Path}' cannot be read ({ex.Message})";
            }
        }

        return null;
    }

    private async Task<string> ExpectedHashAsync(GitHubReleaseClient client, UpdateOffer offer, CancellationToken cancel)
    {
        string text;
        try
        {
            using HttpRequestMessage request = client.CreateAssetRequest(offer.Checksum);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            byte[] bytes = new byte[MaxChecksumBytes];
            int length = 0;
            int read;
            while (length < bytes.Length && (read = await body.ReadAsync(bytes.AsMemory(length), cancel).ConfigureAwait(false)) > 0)
            {
                length += read;
            }

            text = Encoding.UTF8.GetString(bytes, 0, length);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            _log.Write($"checksum download failed: {ex.Message}");
            throw new UpdateException("The update's checksum could not be downloaded. Check your connection and try again.", ex);
        }

        return Checksums.FindExpected(text, offer.Archive.Name)
            ?? throw new UpdateException($"The release has no checksum for {offer.Archive.Name}, so it was not installed.");
    }

    private static void RequireSpace(string folder, long bytes)
    {
        if (bytes <= 0 || FreeSpace(folder) is not { } free || free >= bytes)
        {
            return;
        }

        throw new UpdateException(SpaceMessage(folder, bytes));
    }

    private static long? FreeSpace(string folder)
    {
        try
        {
            string? drive = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(drive) ? null : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string SpaceMessage(string folder, long bytes)
    {
        string drive = Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\', '/') ?? folder;
        return $"Not enough disk space on {drive}: the update needs about {UpdateChecker.Size(bytes)}.";
    }

    private static bool IsDiskFull(Exception ex) => ex is IOException { HResult: DiskFull or HandleDiskFull };

    private void HideWorkFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            var dir = new DirectoryInfo(UpdatePaths.WorkFolder(_install));
            dir.Attributes |= FileAttributes.Hidden;
        }
    }

    private void TryDelete(string staging)
    {
        try
        {
            SafeDelete.Tree(staging, UpdatePaths.WorkFolder(_install));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write($"could not delete {staging}: {ex.Message}");
        }
    }
}
