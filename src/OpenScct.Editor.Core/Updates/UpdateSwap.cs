using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenScct.Launcher.Install;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Core.Updates;

/// <summary>What the editor hands the helper (<c>ose update apply --plan</c>): which build to swap in where, which
/// editor to wait for, and what to start afterwards.</summary>
public sealed class ApplyPlan
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [JsonPropertyName("install")]
    public string Install { get; set; } = string.Empty;

    [JsonPropertyName("staged")]
    public string Staged { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>The editor that asked for the update; the helper waits for it to exit.</summary>
    [JsonPropertyName("editorPid")]
    public int EditorPid { get; set; }

    /// <summary>The exe to start once done (the install's editor); null to start nothing.</summary>
    [JsonPropertyName("restart")]
    public string? Restart { get; set; }

    /// <summary>Its arguments (the engine's, then <c>--</c> and the editor's own).</summary>
    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];

    /// <summary>The helper writes this file once it is running: <see cref="UpdateSwap.ReadyWaiting"/> when it waits for
    /// the editor to quit, <see cref="UpdateSwap.ReadyBusy"/> when another helper already has the install.</summary>
    [JsonPropertyName("ready")]
    public string? Ready { get; set; }

    /// <summary>The downloaded archive, deleted after a successful swap.</summary>
    [JsonPropertyName("download")]
    public string? Download { get; set; }

    public static ApplyPlan Read(string path) =>
        JsonSerializer.Deserialize<ApplyPlan>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty.");

    public void Write(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}

/// <summary>The swap in progress, in <c>&lt;install&gt;\.update\journal.json</c>, written once before the first rename
/// and removed once the new build has started (or the previous one is back). While it exists the install may be half
/// swapped; <see cref="UpdateSwap.Recover"/> puts the previous build back from what it finds on disk.</summary>
public sealed class SwapJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("install")]
    public string Install { get; set; } = string.Empty;

    [JsonPropertyName("staged")]
    public string Staged { get; set; } = string.Empty;

    [JsonPropertyName("old")]
    public string Old { get; set; } = string.Empty;

    [JsonPropertyName("oldEntries")]
    public List<string> OldEntries { get; set; } = [];

    [JsonPropertyName("newEntries")]
    public List<string> NewEntries { get; set; } = [];

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];

    /// <summary>The install's journal; null when there is none. Throws <see cref="InvalidDataException"/> when there is
    /// one that cannot be read.</summary>
    public static SwapJournal? Read(string install)
    {
        string path = UpdatePaths.Journal(install);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            SwapJournal? journal = JsonSerializer.Deserialize<SwapJournal>(File.ReadAllText(path), Json);
            if (journal is null || journal.Staged.Length == 0 || journal.Old.Length == 0
                || journal.OldEntries.Concat(journal.NewEntries).Any(e => !InstallInfo.IsEntryName(e)))
            {
                throw new InvalidDataException($"{path} is incomplete.");
            }

            return journal;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} cannot be read: {ex.Message}", ex);
        }
    }

    public void Write() => AtomicFile.WriteAllText(UpdatePaths.Journal(Install), JsonSerializer.Serialize(this, Json));
}

/// <summary>
/// Swaps a staged build into the install folder after the editor has quit. The editor's exe and the DLLs in Godot's
/// data folder stay mapped while it runs, so this runs in a helper (a copy of the new build's <c>ose</c>, outside the
/// install) that holds the install's swap lock throughout, so no second helper or recovery touches it meanwhile.
/// <para>It waits until nothing runs from the folder, checks the staged build file by file, writes the journal, then
/// moves the previous build's entries out to <c>.update\old-&lt;from&gt;</c> (exe first) and the new ones in from staging
/// (exe last): whenever an exe is in the folder, everything it needs is there too. It then starts the new editor
/// headless once to see that it runs. Any failure puts the previous build back. A RunOnce entry
/// (<see cref="RecoveryEntry"/>) covers the moments no exe is in the folder. The previous build stays in
/// <c>old-&lt;from&gt;</c> until the next start cleans up (<see cref="Cleanup"/>). How it ended goes in
/// <see cref="UpdateResult"/> for the next editor start to report.</para>
/// </summary>
public sealed class UpdateSwap
{
    public const string ProbeFlag = "--update-probe=";
    public const string ReadyWaiting = "waiting";
    public const string ReadyBusy = "busy";

    private readonly UpdateLog _log;
    private readonly UpdatePaths _paths;

    public UpdateSwap(UpdateLog log, UpdatePaths paths)
    {
        _log = log;
        _paths = paths;
    }

    /// <summary>How long to wait for the editor, and then for every other program in the folder, to exit.</summary>
    public TimeSpan ExitWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long one rename is retried while a file is in use (an antivirus scanning new files).</summary>
    public TimeSpan RenameRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the new editor gets to show it starts.</summary>
    public TimeSpan ProbeWait { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Carries out a plan. 0 when the new build is in place, 1 when the install was left on the previous
    /// build, 2 when another helper already has the install (it carries on; this one does nothing).</summary>
    public int Apply(ApplyPlan plan, bool restart)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string install = Path.GetFullPath(plan.Install);
        string work = UpdatePaths.WorkFolder(install);
        using UpdateLock? held = UpdateLock.TryTake(UpdatePaths.SwapLock(install));
        if (held is null)
        {
            _log.Write($"apply {plan.From} -> {plan.To}: another updater already has {install}; leaving it to that one");
            WriteReady(plan, ReadyBusy);
            return 2;
        }

        _log.Write($"apply {plan.From} -> {plan.To} in {install}; waiting for the editor (pid {plan.EditorPid})");
        WriteReady(plan, ReadyWaiting);
        if (plan.EditorPid > 0 && !InstallProcesses.WaitForExit(plan.EditorPid, ExitWait))
        {
            // It still runs, so starting another would only add a second editor.
            return Fail(plan, install, restart: false, UpdateFailure.EditorDidNotClose);
        }

        if (!InstallProcesses.WaitUntilFree(install, ExitWait, out List<FolderProcess> left))
        {
            _log.Write("still running from the folder: " + string.Join(", ", left.Select(p => $"{p.Name} ({p.Pid}) {p.Path}")));
            // Someone opened the editor again meanwhile: it already runs, so start none.
            return Fail(plan, install, restart && !left.Any(IsEditor), UpdateFailure.InUse(left));
        }

        string staged = Path.GetFullPath(plan.Staged);
        if (!SemVersion.TryParse(plan.From, out SemVersion? from) || !SemVersion.TryParse(plan.To, out SemVersion? to)
            || !staged.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(plan, install, restart, UpdateFailure.PlanDamaged);
        }

        // A journal now (the lock is ours) is a swap that was cut off: put that right first.
        if (File.Exists(UpdatePaths.Journal(install)) && !RecoverHeld(install, out _))
        {
            return Fail(plan, install, restart: false, UpdateFailure.RollbackFailed);
        }

        InstallInfo current;
        try
        {
            current = InstallInfo.Read(install);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Write($"the install's own version.json is not usable: {ex.Message}");
            return Fail(plan, install, restart, UpdateFailure.FolderDamaged);
        }

        if (current.ParsedVersion != from)
        {
            return Fail(plan, install, restart, UpdateFailure.OtherVersion(current.Version, plan.From));
        }

        InstallInfo next;
        try
        {
            next = InstallInfo.Read(staged);
            if ((UpdateStager.Problem(staged, next, to) ?? UpdateStager.CheckFiles(staged, next)) is { } problem)
            {
                throw new InvalidDataException(problem);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Write($"the staged build is not usable: {ex.Message}; deleting it");
            TryDeleteTree(staged, work);
            TryDeleteFile(plan.Download);
            return Fail(plan, install, restart, UpdateFailure.Incomplete);
        }

        // Only names the previous build owned move out; a name the new build adds must be free, never the user's.
        var owned = new HashSet<string>(current.Entries, StringComparer.OrdinalIgnoreCase);
        if (next.Entries.FirstOrDefault(e => !owned.Contains(e) && Exists(Path.Combine(install, e))) is { } taken)
        {
            return Fail(plan, install, restart, UpdateFailure.NameTaken(taken));
        }

        var journal = new SwapJournal
        {
            From = plan.From,
            To = plan.To,
            Install = install,
            Staged = staged,
            Old = UpdatePaths.Old(install, from),
            OldEntries = current.Entries,
            NewEntries = next.Entries,
            Args = plan.Args,
        };

        try
        {
            SafeDelete.Tree(journal.Old, work);
            Directory.CreateDirectory(journal.Old);
            journal.Write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Write($"could not prepare the swap: {ex.Message}");
            return Fail(plan, install, restart, ex is UnauthorizedAccessException ? UpdateFailure.AccessDenied : UpdateFailure.Locked);
        }

        RecoveryEntry.Set(install, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, EditorRelease.CliExeName), _paths, _log);
        try
        {
            foreach (string entry in InstallInfo.OutOrder(journal.OldEntries))
            {
                string here = Path.Combine(install, entry);
                if (Exists(here))
                {
                    Move(here, Path.Combine(journal.Old, entry));
                }
            }

            foreach (string entry in InstallInfo.InOrder(journal.NewEntries))
            {
                Move(Path.Combine(staged, entry), Path.Combine(install, entry));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write($"swap failed: {ex.Message}; putting the previous build back");
            string reason = ex is UnauthorizedAccessException ? UpdateFailure.AccessDenied : UpdateFailure.Locked;
            bool back = RollBackPatiently(journal);
            return Fail(plan, install, restart && back, back ? reason : UpdateFailure.RollbackFailed);
        }

        _log.Write($"swapped in {plan.To}; checking that it starts");
        if (!Probe(install, to))
        {
            bool back = RollBackPatiently(journal);
            if (back)
            {
                TryDeleteTree(staged, work);
                TryDeleteFile(plan.Download);
            }

            return Fail(plan, install, restart && back, back ? UpdateFailure.DidNotStart(plan.To) : UpdateFailure.RollbackFailed);
        }

        File.Delete(UpdatePaths.Journal(install));
        RecoveryEntry.Remove(install, _paths, _log);
        _log.Write($"installed {plan.To}; the previous build is in {journal.Old} until the next start");
        UpdateResult.Success(plan.From, plan.To).Write(install, _log);
        if (restart)
        {
            Start(plan.Restart, plan.Args, install);
        }

        TryDeleteTree(staged, work);
        TryDeleteFile(plan.Download);
        return 0;
    }

    /// <summary>
    /// Puts the previous build back after a swap that was cut off (the journal is still there): the sign-in entry of
    /// <see cref="RecoveryEntry"/> runs it. Never leaves an unreadable journal behind to trip every later start. 0 when
    /// the install is whole again, 1 when it is not (the entry is added again for the next sign-in), 2 when a helper
    /// is still at work on the install.
    /// </summary>
    public int Recover(string install, bool restart)
    {
        install = Path.GetFullPath(install);
        using UpdateLock? held = UpdateLock.TryTake(UpdatePaths.SwapLock(install));
        if (held is null)
        {
            _log.Write($"recover: an updater is still at work on {install}");
            return 2;
        }

        if (!File.Exists(UpdatePaths.Journal(install)))
        {
            _log.Write($"recover: nothing to do in {install}");
            RecoveryEntry.Remove(install, _paths, _log);
            return 0;
        }

        if (!InstallProcesses.WaitUntilFree(install, ExitWait, out List<FolderProcess> left))
        {
            _log.Write("recover: programs still run from the folder: " + string.Join(", ", left.Select(p => p.Name)));
            RecoveryEntry.Set(install, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, EditorRelease.CliExeName), _paths, _log);
            return 1;
        }

        bool back = RecoverHeld(install, out SwapJournal? journal);
        if (!back)
        {
            RecoveryEntry.Set(install, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, EditorRelease.CliExeName), _paths, _log);
        }

        if (restart && back)
        {
            Start(Path.Combine(install, EditorRelease.ExeName), journal?.Args ?? [], install);
        }

        return back ? 0 : 1;
    }

    /// <summary>
    /// At an editor's start, before anything else: a journal there while no helper holds the swap lock is a swap that
    /// was cut off. A running editor proves its own build starts, and whenever an exe is in the folder its build is
    /// whole (see <see cref="Apply"/>), so it settles the journal for the build it finds: the new one (the swap had
    /// finished but for its last check) or the previous one (nothing had moved yet). It writes the
    /// <see cref="UpdateResult"/> to report. False when it could not tell, and left everything as it was.
    /// </summary>
    public static bool Settle(string install, SemVersion running, UpdatePaths paths, UpdateLog log)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(log);
        install = Path.GetFullPath(install);
        if (!File.Exists(UpdatePaths.Journal(install)))
        {
            return true;
        }

        using UpdateLock? held = UpdateLock.TryTake(UpdatePaths.SwapLock(install));
        if (held is null)
        {
            log.Write($"settle: an updater is at work on {install}");
            return true;
        }

        SwapJournal? journal;
        try
        {
            journal = SwapJournal.Read(install);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log.Write($"settle: {ex.Message}");
            journal = null;
        }

        InstallInfo? info = InstallInfo.TryRead(install);
        bool whole = info?.ParsedVersion == running && info.Entries.All(e => Exists(Path.Combine(install, e)));
        if (!whole)
        {
            log.Write($"settle: {install} does not hold {running} whole; leaving the journal for the recovery at sign-in");
            return false;
        }

        try
        {
            if (journal is null)
            {
                SetAside(UpdatePaths.Journal(install), log);
            }
            else
            {
                File.Delete(UpdatePaths.Journal(install));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"settle: could not remove the journal: {ex.Message}");
            return false;
        }

        RecoveryEntry.Remove(install, paths, log);
        if (journal is not null)
        {
            bool updated = SemVersion.TryParse(journal.To, out SemVersion? to) && to == running;
            log.Write($"settle: the swap {journal.From} -> {journal.To} was cut off; {running} is in place and runs");
            (updated ? UpdateResult.Success(journal.From, journal.To) : UpdateResult.Failure(journal.From, journal.To, UpdateFailure.Interrupted))
                .Write(install, log);
        }

        return true;
    }

    /// <summary>
    /// Removes what finished updates leave behind, once the editor has started: the previous build, staged builds no
    /// newer than <paramref name="current"/>, the new editor's check file, downloads of other versions, and helper
    /// copies and plans over a day old that no program runs from and no recovery entry needs (another install may be
    /// using its own). Does nothing while a swap's journal is there or a helper holds the install. Never fails.
    /// </summary>
    public static void Cleanup(string install, SemVersion current, UpdatePaths paths, UpdateLog log)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        string work = UpdatePaths.WorkFolder(install);
        if (File.Exists(UpdatePaths.Journal(install)))
        {
            return;
        }

        using UpdateLock? held = UpdateLock.TryTake(paths);
        if (held is null)
        {
            return;
        }

        DateTime dayAgo = DateTime.UtcNow.AddDays(-1);
        try
        {
            if (Directory.Exists(work))
            {
                using UpdateLock? swap = UpdateLock.TryTake(UpdatePaths.SwapLock(install));
                if (swap is null)
                {
                    return;
                }

                foreach (string dir in Directory.EnumerateDirectories(work))
                {
                    string name = Path.GetFileName(dir);
                    bool newer = SemVersion.TryParse(name, out SemVersion? v) && v > current
                        && File.Exists(Path.Combine(dir, UpdatePaths.StagedMarkName));
                    if (!newer)
                    {
                        log.Write($"cleanup: removing {dir}");
                        TryDeleteTree(dir, work, log);
                    }
                }

                foreach (string file in Directory.EnumerateFiles(work, "probe-*.txt"))
                {
                    TryDeleteFile(file, log);
                }
            }

            if (Directory.Exists(paths.Helpers))
            {
                List<string> needed = RecoveryEntry.Pending();
                foreach (string dir in Directory.EnumerateDirectories(paths.Helpers))
                {
                    string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;
                    bool mine = SemVersion.TryParse(Path.GetFileName(dir), out SemVersion? v) && v == current;
                    if (!mine && Directory.GetLastWriteTimeUtc(dir) < dayAgo
                        && !needed.Any(c => c.Contains(full, StringComparison.OrdinalIgnoreCase))
                        && InstallProcesses.Running(dir).Count == 0)
                    {
                        TryDeleteTree(dir, paths.Root, log);
                    }
                }
            }

            if (Directory.Exists(paths.Downloads))
            {
                foreach (string file in Directory.EnumerateFiles(paths.Downloads))
                {
                    if (DownloadVersion(Path.GetFileName(file)) is { } v && v <= current)
                    {
                        TryDeleteFile(file, log);
                    }
                }
            }

            foreach (string file in Directory.EnumerateFiles(paths.Root, "apply-*"))
            {
                if (File.GetLastWriteTimeUtc(file) < dayAgo)
                {
                    TryDeleteFile(file, log);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Write($"cleanup stopped: {ex.Message}");
        }
    }

    /// <summary>The version in a download's name (<c>OpenSCCT-Editor-0.2.0-win-x64.zip</c>, also its .partial files).</summary>
    public static SemVersion? DownloadVersion(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!fileName.StartsWith(EditorRelease.AssetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = fileName[EditorRelease.AssetPrefix.Length..];
        int platform = rest.IndexOf("-" + EditorRelease.Platform, StringComparison.OrdinalIgnoreCase);
        return platform > 0 && SemVersion.TryParse(rest[..platform], out SemVersion? v) ? v : null;
    }

    /// <summary>Recovery with the swap lock already held: reads the journal (one that cannot be read is set aside, and
    /// the folder is left as it is) and rolls back. False when the install is not whole.</summary>
    private bool RecoverHeld(string install, out SwapJournal? journal)
    {
        try
        {
            journal = SwapJournal.Read(install);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Write($"recover: {ex.Message}");
            SetAside(UpdatePaths.Journal(install), _log);
            RecoveryEntry.Remove(install, _paths, _log);
            journal = null;
            bool whole = InstallInfo.TryRead(install) is { } info && info.Entries.All(e => Exists(Path.Combine(install, e)));
            return whole;
        }

        if (journal is null)
        {
            return true;
        }

        _log.Write($"recover: the swap {journal.From} -> {journal.To} in {install} did not finish; putting {journal.From} back");
        journal.Install = install;
        bool back = RollBackPatiently(journal);
        UpdateResult.Failure(journal.From, journal.To, back ? UpdateFailure.Interrupted : UpdateFailure.RollbackFailed).Write(install, _log);
        return back;
    }

    /// <summary><see cref="RollBack"/>, tried a few times over half a minute (a file an antivirus holds for a moment).</summary>
    private bool RollBackPatiently(SwapJournal journal)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (RollBack(journal))
            {
                return true;
            }

            if (attempt == 4)
            {
                return false;
            }

            InstallProcesses.WaitUntilFree(journal.Install, TimeSpan.FromSeconds(10), out _);
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Puts the previous build back from what is on disk: each new entry found in the install (and no longer
    /// in staging) goes back to staging, exe first, then each previous entry comes back from <c>old-</c>, exe last.
    /// False when an entry could not be moved; the journal then stays for another try.</summary>
    private bool RollBack(SwapJournal journal)
    {
        try
        {
            foreach (string entry in InstallInfo.OutOrder(journal.NewEntries))
            {
                string inInstall = Path.Combine(journal.Install, entry);
                string inStaged = Path.Combine(journal.Staged, entry);
                if (!Exists(inStaged) && Exists(inInstall))
                {
                    Directory.CreateDirectory(journal.Staged);
                    Move(inInstall, inStaged);
                }
            }

            foreach (string entry in InstallInfo.InOrder(journal.OldEntries))
            {
                string inInstall = Path.Combine(journal.Install, entry);
                string inOld = Path.Combine(journal.Old, entry);
                if (!Exists(inOld))
                {
                    continue;
                }

                if (Exists(inInstall))
                {
                    _log.Write($"rollback: {inInstall} is in the way of the previous {entry}; leaving both");
                    continue;
                }

                Move(inOld, inInstall);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write($"rollback failed: {ex.Message}");
            return false;
        }

        File.Delete(UpdatePaths.Journal(journal.Install));
        RecoveryEntry.Remove(journal.Install, _paths, _log);
        _log.Write($"rollback done: {journal.From} is back in {journal.Install}");
        return true;
    }

    /// <summary>Starts the new editor headless with <see cref="ProbeFlag"/>: it writes its version and quits before
    /// opening anything. False when it did not, within <see cref="ProbeWait"/>.</summary>
    private bool Probe(string install, SemVersion version)
    {
        string answer = UpdatePaths.Probe(install, version);
        string exe = Path.Combine(install, EditorRelease.ExeName);
        try
        {
            TryDeleteFile(answer);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = install };
            foreach (string a in new[] { "--headless", "--", ProbeFlag + answer })
            {
                start.ArgumentList.Add(a);
            }

            using Process? p = Process.Start(start);
            if (p is null)
            {
                _log.Write("check: the new editor did not start");
                return false;
            }

            if (!p.WaitForExit(ProbeWait))
            {
                _log.Write($"check: the new editor (pid {p.Id}) did not answer within {ProbeWait.TotalSeconds:0} s; stopping it");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(10_000);
                return false;
            }

            string said = File.Exists(answer) ? File.ReadAllText(answer).Trim() : "";
            TryDeleteFile(answer);
            bool ok = p.ExitCode == 0 && SemVersion.TryParse(said, out SemVersion? v) && v == version;
            _log.Write($"check: the new editor exited with {p.ExitCode} and answered '{said}': {(ok ? "it runs" : "it does not run")}");
            return ok;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log.Write($"check: could not start {exe}: {ex.Message}");
            return false;
        }
    }

    private int Fail(ApplyPlan plan, string install, bool restart, string reason)
    {
        _log.Write($"update to {plan.To} not installed: {reason}");
        UpdateResult.Failure(plan.From, plan.To, reason).Write(install, _log);
        if (restart)
        {
            Start(plan.Restart, plan.Args, install);
        }

        return 1;
    }

    private static bool IsEditor(FolderProcess p) =>
        string.Equals(Path.GetFileName(p.Path), EditorRelease.ExeName, StringComparison.OrdinalIgnoreCase);

    private void WriteReady(ApplyPlan plan, string state)
    {
        if (plan.Ready is not { Length: > 0 } ready)
        {
            return;
        }

        try
        {
            AtomicFile.WriteAllText(ready, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write($"could not write {ready}: {ex.Message}");
        }
    }

    private void Start(string? exe, IEnumerable<string> args, string workingDirectory)
    {
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDirectory };
            foreach (string a in args)
            {
                start.ArgumentList.Add(a);
            }

            using Process? p = Process.Start(start);
            _log.Write($"started {exe} {string.Join(' ', start.ArgumentList)} (pid {p?.Id})");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _log.Write($"could not start {exe}: {ex.Message}");
        }
    }

    private void Move(string from, string to)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (Directory.Exists(from))
                {
                    Directory.Move(from, to);
                }
                else
                {
                    File.Move(from, to);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && clock.Elapsed < RenameRetry)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Renames an unreadable journal out of the way (kept for diagnosis), so it never stops another start.</summary>
    private static void SetAside(string journal, UpdateLog log)
    {
        string aside = journal + $".bad-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(journal, aside, overwrite: true);
            log.Write($"set the unreadable journal aside as {aside}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"could not set {journal} aside: {ex.Message}");
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private void TryDeleteTree(string path, string root) => TryDeleteTree(path, root, _log);

    private static void TryDeleteTree(string path, string root, UpdateLog log)
    {
        try
        {
            SafeDelete.Tree(path, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Write($"could not delete {path}: {ex.Message}");
        }
    }

    private void TryDeleteFile(string? path)
    {
        if (path is { Length: > 0 })
        {
            TryDeleteFile(path, _log);
        }
    }

    private static void TryDeleteFile(string path, UpdateLog log)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"could not delete {path}: {ex.Message}");
        }
    }
}
