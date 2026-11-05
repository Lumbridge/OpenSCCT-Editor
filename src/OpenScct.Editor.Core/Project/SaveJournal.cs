using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Project;

/// <summary>
/// Saves a project's files as one unit, so a crash or a power cut never leaves files from two saves mixed.
/// <list type="number">
/// <item>Every new file is written whole into the staging folder <c>.saving/</c> and flushed to disk.</item>
/// <item>The journal <c>.saving.journal</c> (which files to move in, which to remove, where the old ones go) is
/// written to a temp name, flushed and renamed into place. That rename is the moment the save happens.</item>
/// <item>The staged files are moved over the old ones (the old ones go to a backup when one is kept), then the journal
/// and the staging folder are deleted.</item>
/// </list>
/// A crash before step 2 leaves the old project untouched (the staging folder is thrown away on the next open or save).
/// A crash after it is finished by <see cref="Recover"/> on the next open or save, because every staged file is
/// already whole. Replaying a journal is safe however often it runs.
/// <para>A save, a recovery and a read (<see cref="ProjectStore.Load"/>) each hold the project's lock file
/// <c>.saving.lock</c>, so a second program (an <c>ose</c> command while the editor saves) waits for the save to finish
/// instead of reading half-moved files or taking a running save's staging folder for a crashed one. The operating
/// system lets go of the lock when a program dies, so a crash never leaves the project locked.</para>
/// </summary>
public static class SaveJournal
{
    public const string StagingFolder = ".saving";
    public const string JournalFile = ".saving.journal";
    public const string LockFile = ".saving.lock";
    private const string Format = "osmap.savejournal/1";

    /// <summary>How long to wait for another program's save before giving up.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(60);

    /// <summary>Takes the project's lock, waiting while another program holds it. Not re-entrant: a holder must not
    /// take it again. Throws <see cref="IOException"/> when the wait runs out. In a folder nobody may write to (a
    /// read-only copy) there is nothing to lock against, and the lock is a no-op.</summary>
    public static IDisposable Lock(string folder)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, LockFile);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (UnauthorizedAccessException) when (!File.Exists(path))
            {
                return Stream.Null;
            }
            // Held by another program, or (access denied) being let go of by one.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (waited.Elapsed >= LockWait)
                {
                    throw new IOException($"{folder} is being saved by another program; try again when it has finished.", ex);
                }

                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Saves <paramref name="files"/> (paths relative to the project folder) and deletes
    /// <paramref name="remove"/>. When <paramref name="backup"/> is a folder, the files this save replaces or removes
    /// are moved there. The caller holds the project's <see cref="Lock"/>.</summary>
    public static void Commit(string folder, IReadOnlyList<(string Rel, byte[] Bytes)> files, IReadOnlyList<string> remove, string? backup)
    {
        Recover(folder);
        string staging = Path.Combine(folder, StagingFolder);
        try
        {
            foreach ((string rel, byte[] bytes) in files)
            {
                WriteDurable(Path.Combine(staging, rel), bytes);
            }
        }
        catch
        {
            DeleteFolder(staging);
            throw;
        }

        var journal = new SvJsonObject();
        journal.Add("format", SvJsonNode.String(Format));
        journal.Add("files", new SvJsonArray(files.Select(f => SvJsonNode.String(Normal(f.Rel)))));
        journal.Add("remove", new SvJsonArray(remove.Select(r => SvJsonNode.String(Normal(r)))));
        if (backup is not null)
        {
            journal.Add("backup", SvJsonNode.String(Path.GetRelativePath(folder, backup).Replace('\\', '/')));
        }

        string path = Path.Combine(folder, JournalFile);
        WriteDurable(path + ".tmp", CanonicalJson.ToFileBytes(journal));
        File.Move(path + ".tmp", path, overwrite: true);
        Apply(folder, journal);
    }

    /// <summary>Finishes a save a crash interrupted, or throws away one that never reached its journal. The caller holds
    /// the project's <see cref="Lock"/>, so a staging folder without a journal can only be left from a crash. Returns a
    /// line saying what it did, or null when there was nothing to do.</summary>
    public static string? Recover(string folder)
    {
        string path = Path.Combine(folder, JournalFile);
        string staging = Path.Combine(folder, StagingFolder);
        File.Delete(path + ".tmp");
        if (File.Exists(path))
        {
            var journal = SvJson.Parse(File.ReadAllBytes(path), JournalFile) as SvJsonObject
                ?? throw new InvalidDataException($"{path} is not a save journal.");
            Apply(folder, journal);
            return $"Finished a save that was interrupted in {folder}.";
        }

        if (Directory.Exists(staging))
        {
            DeleteFolder(staging);
            return $"Discarded a save that was interrupted before it started in {folder}; the project is as it was before.";
        }

        return null;
    }

    /// <summary>True when an interrupted save is waiting in <paramref name="folder"/>.</summary>
    public static bool IsPending(string folder) => File.Exists(Path.Combine(folder, JournalFile));

    private static void Apply(string folder, SvJsonObject journal)
    {
        string staging = Path.Combine(folder, StagingFolder);
        string? backup = journal["backup"] is SvJsonString b ? Path.Combine(folder, b.Value) : null;
        foreach (string rel in Strings(journal, "files"))
        {
            string staged = Path.Combine(staging, rel);
            if (!File.Exists(staged))
            {
                continue; // moved in before the crash
            }

            string target = Path.Combine(folder, rel);
            KeepOld(target, backup, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(staged, target, overwrite: true);
        }

        foreach (string rel in Strings(journal, "remove"))
        {
            string target = Path.Combine(folder, rel);
            KeepOld(target, backup, rel);
            File.Delete(target);
        }

        File.Delete(Path.Combine(folder, JournalFile));
        DeleteFolder(staging);
    }

    /// <summary>Moves the file a save replaces into the backup (once: a replayed journal finds it there already).</summary>
    private static void KeepOld(string target, string? backup, string rel)
    {
        if (backup is null || !File.Exists(target))
        {
            return;
        }

        string kept = Path.Combine(backup, rel);
        if (File.Exists(kept))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.Move(target, kept);
    }

    /// <summary>Writes a file and flushes it to the disk, not just to the OS cache.</summary>
    private static void WriteDurable(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static IEnumerable<string> Strings(SvJsonObject o, string key) =>
        o[key] is SvJsonArray a ? a.OfType<SvJsonString>().Select(s => s.Value) : [];

    private static string Normal(string rel) => rel.Replace('\\', '/');

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
