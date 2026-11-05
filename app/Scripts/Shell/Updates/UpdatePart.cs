using System.Diagnostics;
using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Updates;
using OpenScct.Launcher.Update;
using OpenScct.Launcher.Versioning;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell.Updates;

/// <summary>
/// The editor's own updates (docs/RELEASING.md). In a release package it asks GitHub a few seconds after every start
/// (an ETag request, answered from the cache when nothing changed or GitHub cannot be reached; off in Preferences),
/// quietly: a newer version shows as a link in the status bar, never a popup. Help > Check for Updates… always
/// answers. Update now downloads and unpacks the new build in the background (the link cancels it); Restart then hands
/// over to a helper (a copy of the new build's <c>ose</c>) that swaps the folder once the editor has quit and starts it
/// again with the open map. Quitting with an update ready installs it on the way out. Either way the next start says
/// how it went. A run from source never updates itself.
/// <para><c>-- --install-update</c> checks at once and installs a newer version without asking (for scripts).</para>
/// </summary>
[ShellPart(Order = -1000)]
public sealed partial class UpdatePart : Node, IShellPart
{
    public const string InstallFlag = "--install-update";
    private const string DownloadJob = "Downloading update";
    private const string CheckJob = "Checking for updates";
    private static readonly TimeSpan HelperAnswer = TimeSpan.FromSeconds(10);

    private readonly UpdatePaths _paths = UpdatePaths.Default;
    private UpdateLog _log = null!;
    private EditorContext _ctx = null!;
    private MainWindow _window = null!;
    private UpdateEnvironment _env = null!;
    private LinkButton _link = null!;
    private Action? _linkAction;
    private StagedUpdate? _staged;
    private CancellationTokenSource? _download;
    private bool _helperStarted;
    private bool _unattended;

    private enum HelperLaunch
    {
        Started,
        Busy,
        Failed,
    }

    /// <summary>When the helper starts the new editor to check it runs (<see cref="UpdateSwap.ProbeFlag"/>): writes this
    /// build's version where asked and quits before anything else happens. Called first thing at start.</summary>
    public static bool AnswerProbe(SceneTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        string? flag = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(UpdateSwap.ProbeFlag, StringComparison.Ordinal));
        if (flag is null)
        {
            return false;
        }

        try
        {
            File.WriteAllText(flag[UpdateSwap.ProbeFlag.Length..], EditorRelease.Version.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            GD.PrintErr($"Updates: could not answer the check: {ex.Message}");
        }

        tree.Quit(0);
        return true;
    }

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        _window = window;
        _env = UpdateEnvironment.Current;
        _log = new UpdateLog(_paths.Log);
        Name = "Updates";
        window.AddChild(this);
        GD.Print($"Updates: {EditorRelease.Product} {EditorRelease.Display}, updates {_env.Describe()}");

        // A swap cut off before its last step (power cut, killed helper) leaves its journal. This editor runs, so its
        // build is whole: settle the journal for it. Any exported build looks, whatever its version.json says.
        bool exported = OS.HasFeature("template");
        if (exported && !UpdateSwap.Settle(_env.Install, UpdateEnvironment.Version, _paths, _log))
        {
            context.Log("An update of the editor's folder did not finish. If the editor misbehaves, extract the release zip over its folder.",
                Severity.Warning);
        }

        context.AddAction(new EditorAction("help.checkUpdates", "Check for Updates…", "Help", CheckNow)
        {
            Order = 800,
            Description = "Ask GitHub whether a newer OpenSCCT Editor is out",
        });
        _link = new LinkButton
        {
            Name = "UpdateLink",
            Visible = false,
            Underline = LinkButton.UnderlineMode.OnHover,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            FocusMode = Control.FocusModeEnum.None,
        };
        _link.AddThemeFontSizeOverride("font_size", EditorTheme.FontSizeSmall);
        _link.Pressed += () => _linkAction?.Invoke();
        window.StatusRow.AddChild(_link);

        string[] args = OS.GetCmdlineUserArgs();
        if (exported && UpdateResult.Take(_env.Install) is { } result)
        {
            EditorLifetime.Defer(() => Report(result));
        }

        if (!_env.IsRelease)
        {
            if (args.Contains(InstallFlag))
            {
                context.Log($"{InstallFlag}: updates are off ({_env.WhyNot}).", Severity.Warning);
            }

            return;
        }

        _staged = UpdateStager.FindStaged(_env.Install, UpdateEnvironment.Version);
        if (_staged is not null)
        {
            ShowRestartLink();
        }

        After(10, () => BackgroundJobs.Start(_ => UpdateSwap.Cleanup(_env.Install, UpdateEnvironment.Version, _paths, _log)));
        if (args.Contains(InstallFlag))
        {
            _unattended = true;
            After(1, InstallUnattended);
        }
        else if (UpdateSettings.CheckOnStart)
        {
            After(5, CheckQuietly);
        }
    }

    /// <summary>Opens a web page (release notes) or a file (the update log); never from a background run.</summary>
    public static void OpenPage(string url)
    {
        if (!BackgroundMode.Refuse($"opening {url}"))
        {
            OS.ShellOpen(url);
        }
    }

    /// <summary>Quitting with an update ready installs it on the way out (unless another program uses the folder).</summary>
    public override void _ExitTree()
    {
        _download?.Cancel();
        if (_env is null || !_env.IsRelease || _staged is null || _helperStarted)
        {
            return;
        }

        if (InstallProcesses.Running(_env.Install).Count > 0)
        {
            GD.Print($"Updates: {_staged.Version} stays ready; other programs run from {_env.Install}");
            return;
        }

        HelperLaunch started = LaunchHelper(restart: false, waitReady: false);
        GD.Print(started == HelperLaunch.Started ? $"Updates: installing {_staged.Version} after quitting" : "Updates: the updater could not start");
    }

    private void After(double seconds, Action action) =>
        GetTree().CreateTimer(seconds).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() =>
        {
            if (!EditorLifetime.IsQuitting)
            {
                action();
            }
        }));

    /// <summary>The check at start: a cheap ETag request, so it runs every start; a failed one falls back to the cache
    /// and is simply tried again next start.</summary>
    private void CheckQuietly()
    {
        bool prereleases = UpdateSettings.Prereleases;
        BackgroundJobs.Start(async cancel =>
        {
            try
            {
                using System.Net.Http.HttpClient http = UpdateHttp.ForChecks();
                UpdateCheckResult r = await UpdateChecker.CheckAsync(http, _paths, UpdateEnvironment.Version, prereleases, cancel).ConfigureAwait(false);
                BackgroundJobs.Post(() => Found(r, quiet: true));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // The editor is closing.
            }
            catch (Exception ex)
            {
                _log.Write($"the check failed ({ex.Message})");
                EditorLifetime.TryEngine(() => GD.Print($"Updates: the check failed ({ex.Message})"));
            }
        });
    }

    private void CheckNow()
    {
        bool prereleases = UpdateSettings.Prereleases;
        BackgroundJobs.Run(CheckJob, cancel =>
        {
            using System.Net.Http.HttpClient http = UpdateHttp.ForChecks();
            return UpdateChecker.CheckAsync(http, _paths, UpdateEnvironment.Version, prereleases, cancel).GetAwaiter().GetResult();
        }, r => Found(r, quiet: false));
    }

    private void Found(UpdateCheckResult r, bool quiet)
    {
        if (r.Problem is not null)
        {
            GD.Print($"Updates: {r.Problem}");
        }

        if (r.Kind != UpdateCheckKind.Failed && !r.FromCache)
        {
            UpdateSettings.LastCheck = DateTimeOffset.UtcNow;
        }

        if (!_env.IsRelease)
        {
            string latest = r.Latest is { } l ? $" The latest release is {l}." : "";
            _ctx.SetStatus("Ready");
            Dialogs.Inform("Check for Updates", "Running from source",
                $"OpenSCCT Editor {EditorRelease.Display} runs from source, so it does not update itself: update with git pull " +
                $"(Run-Editor.cmd rebuilds).{latest}");
            return;
        }

        if (r.Kind == UpdateCheckKind.Failed)
        {
            if (!quiet)
            {
                _ctx.SetStatus("Could not check for updates");
                Dialogs.Inform("Check for Updates", "Could not check for updates", Why(r), icon: "warning");
            }

            if (_unattended)
            {
                _ctx.Log($"{InstallFlag}: could not check for updates: {Why(r)}", Severity.Error);
            }

            return;
        }

        if (r.Offer is not { } offer)
        {
            if (!quiet)
            {
                _ctx.SetStatus("Up to date");
                string note = r.Problem is null ? "" : $" ({Why(r)} This is the last list the editor saw.)";
                Dialogs.Inform("Check for Updates", "You have the latest version",
                    $"{EditorRelease.Product} {UpdateEnvironment.Version} is the newest release.{note}");
            }

            if (_unattended)
            {
                _ctx.Log($"{InstallFlag}: {UpdateEnvironment.Version} is the newest release.");
            }

            return;
        }

        if (_staged is { } ready && ready.Version >= offer.Version)
        {
            if (!quiet)
            {
                _ctx.SetStatus($"Update {ready.Version} ready");
                AskRestart();
            }

            return;
        }

        bool skipped = UpdateSettings.Skipped is { } s && offer.Version <= s;
        if (_unattended)
        {
            _ctx.Log($"{InstallFlag}: installing {offer.Version}");
            Download(offer);
            return;
        }

        if (!quiet)
        {
            _ctx.SetStatus($"Update {offer.Version} available");
        }

        if (quiet && skipped)
        {
            return;
        }

        if (!BackgroundJobs.IsRunning(DownloadJob))
        {
            ShowOfferLink(offer);
        }

        if (!quiet)
        {
            Offer(offer, skipped);
        }
    }

    private void Offer(UpdateOffer offer, bool skipped) =>
        UpdateDialog.Show(_window, offer, _env.CanInstall, skipped, () =>
        {
            if (_env.CanInstall)
            {
                Download(offer);
            }
            else
            {
                OpenPage(offer.Page);
            }
        }, () =>
        {
            UpdateSettings.Skipped = offer.Version;
            _link.Visible = false;
            _ctx.SetStatus($"Skipped {offer.Version}");
        });

    private void ShowOfferLink(UpdateOffer offer)
    {
        string when = offer.Published is { } p ? $" was released on {p.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture)}" : " is out";
        SetLink($"Update {EditorRelease.Describe(offer.Version)} available", $"{EditorRelease.Product} {offer.Version}{when}. Click for what's new.",
            () => Offer(offer, skipped: false));
    }

    private void ShowRestartLink()
    {
        if (_staged is { } staged)
        {
            SetLink($"Restart to update to {staged.Version}", "The update is downloaded. Restart the editor to install it (quitting installs it too).",
                AskRestart);
        }
    }

    private void SetLink(string text, string tooltip, Action pressed)
    {
        _link.Text = text;
        _link.TooltipText = tooltip;
        _linkAction = pressed;
        _link.Visible = true;
    }

    private void Download(UpdateOffer offer)
    {
        if (BackgroundJobs.IsRunning(DownloadJob))
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        _download = cancel;
        SetLink("Cancel download", $"Stop downloading {offer.Version}. It resumes where it stopped next time.", () => cancel.Cancel());
        var progress = new StatusProgress(_ctx, cancel.Token);
        BackgroundJobs.Run<(StagedUpdate? Staged, string? Error)>(DownloadJob, quit =>
        {
            using System.Net.Http.HttpClient http = UpdateHttp.ForDownloads();
            var stager = new UpdateStager(http, _paths, _env.Install, _log);
            // Quitting stops the download too; it resumes where it stopped next time.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, quit);
            try
            {
                return (stager.StageAsync(offer, progress, stop.Token).GetAwaiter().GetResult(), null);
            }
            catch (UpdateException ex)
            {
                return (null, ex.Message);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                _log.Write($"download of {offer.Version} stopped by the user");
                return (null, null);
            }
        }, result =>
        {
            _download = null;
            cancel.Dispose();
            if (result.Staged is null)
            {
                ShowOfferLink(offer);
                if (result.Error is null)
                {
                    _ctx.SetStatus("Download stopped; it resumes next time");
                    return;
                }

                _ctx.Log($"Update to {offer.Version} not installed: {result.Error}", Severity.Error);
                _ctx.SetStatus("Update not installed");
                if (!_unattended)
                {
                    Dialogs.Inform("Update", $"{EditorRelease.Product} {offer.Version} was not installed",
                        $"{result.Error} You still have {UpdateEnvironment.Version}.", icon: "warning", action: "Open Log",
                        actionChosen: () => OpenPage(_log.Path));
                }

                return;
            }

            _staged = result.Staged;
            _ctx.Log($"Update {_staged.Version} is ready; it is installed when the editor restarts.");
            _ctx.SetStatus($"Update {_staged.Version} ready");
            ShowRestartLink();
            if (_unattended)
            {
                Restart();
            }
            else
            {
                AskRestart();
            }
        });
    }

    private void AskRestart()
    {
        if (_staged is not { } staged)
        {
            return;
        }

        Dialogs.Ask(_window, "Update Ready", $"{EditorRelease.Product} {staged.Version} is ready to install",
            "The editor closes, installs it and opens again with your map. Later installs it when you quit.", "Restart now", Restart,
            cancel: "Later", icon: "info", modal: false);
    }

    private void InstallUnattended()
    {
        if (_staged is not null)
        {
            _ctx.Log($"{InstallFlag}: {_staged.Version} is ready; restarting into it");
            Restart();
            return;
        }

        CheckNow();
    }

    private void Restart()
    {
        if (_staged is null || _helperStarted)
        {
            return;
        }

        if (Documents.Instance is { } documents)
        {
            documents.GuardUnsaved(RestartNow);
        }
        else
        {
            RestartNow();
        }
    }

    private void RestartNow()
    {
        if (_staged is not { } staged)
        {
            return;
        }

        List<FolderProcess> others = InstallProcesses.Running(_env.Install);
        if (others.Count > 0)
        {
            string names = string.Join(", ", others.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            string detail = others.Any(p => p.Name.Equals("ose", StringComparison.OrdinalIgnoreCase))
                ? $"ose from this folder is running (used by another program): {names}. Close it, then try again."
                : $"Close the other OpenSCCT Editor windows from this folder first ({names}).";
            _ctx.Log($"Update {staged.Version} waits: {detail}", Severity.Warning);
            if (!_unattended)
            {
                Dialogs.Ask(_window, "Update Ready", "Other programs use the editor's folder", detail, "Try Again", Restart, icon: "warning", modal: false);
            }

            return;
        }

        switch (LaunchHelper(restart: true, waitReady: true))
        {
            case HelperLaunch.Started:
                _ctx.Log($"Restarting to install {staged.Version}");
                _window.Quit();
                return;
            case HelperLaunch.Busy:
                _ctx.Log($"Update {staged.Version} is already being installed; it finishes once the editor closes.", Severity.Warning);
                Dialogs.Inform("Update", "The update is already being installed",
                    "An earlier restart started it. It finishes once every OpenSCCT Editor window from this folder is closed.");
                return;
            default:
                _ctx.Log("The updater did not start; the editor stays open.", Severity.Error);
                Dialogs.Inform("Update", "The updater did not start",
                    "Nothing was changed and the editor stays open. Try again, or quit the editor: quitting installs the update too.",
                    icon: "warning", action: "Open Log", actionChosen: () => OpenPage(_log.Path));
                return;
        }
    }

    /// <summary>Copies the new build's ose out of the install, writes the plan and starts <c>ose update apply</c>. A helper
    /// that does not answer in time is stopped, so a later try never runs a second one beside it.</summary>
    private HelperLaunch LaunchHelper(bool restart, bool waitReady)
    {
        if (_staged is not { } staged)
        {
            return HelperLaunch.Failed;
        }

        try
        {
            string helper = UpdateHelper.Prepare(_paths, Path.Combine(staged.Folder, EditorRelease.CliFolder), staged.Version.ToString());
            string planFile = _paths.Plan(staged.Version);
            string ready = planFile + ".ready";
            File.Delete(ready);
            var plan = new ApplyPlan
            {
                Install = _env.Install,
                Staged = staged.Folder,
                From = UpdateEnvironment.Version.ToString(),
                To = staged.Version.ToString(),
                EditorPid = System.Environment.ProcessId,
                Restart = restart ? _env.Exe : null,
                Args = RestartArgs(),
                Ready = ready,
                Download = _paths.Download(EditorRelease.AssetName(staged.Version)),
            };
            plan.Write(planFile);
            using Process? process = UpdateHelper.Start(helper, _paths, "apply", "--plan", planFile);
            if (process is null)
            {
                return HelperLaunch.Failed;
            }

            if (!waitReady)
            {
                _helperStarted = true;
                _log.Write($"editor {System.Environment.ProcessId}: helper {process.Id} started for {staged.Version}");
                return HelperLaunch.Started;
            }

            string? answer = UpdateHelper.WaitReady(process, ready, HelperAnswer);
            _log.Write($"editor {System.Environment.ProcessId}: helper {process.Id} for {staged.Version} answered '{answer ?? "nothing"}'");
            if (answer == UpdateSwap.ReadyWaiting || answer == UpdateSwap.ReadyBusy)
            {
                // Busy: an earlier helper waits for this editor to close and installs then; never start another.
                _helperStarted = true;
                return answer == UpdateSwap.ReadyWaiting ? HelperLaunch.Started : HelperLaunch.Busy;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It has exited.
            }

            return HelperLaunch.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            _log.Write($"could not start the updater: {ex.Message}");
            return HelperLaunch.Failed;
        }
    }

    /// <summary>The engine's own arguments as given (a background or headless run stays one), then the editor's: the
    /// open map, and background mode when on.</summary>
    private List<string> RestartArgs()
    {
        // Godot leaves the options it consumed (--headless) out of OS.GetCmdlineArgs(); the process command line has
        // them all: the exe, the engine's arguments, then "--" and the editor's own.
        string[] line = System.Environment.GetCommandLineArgs();
        IEnumerable<string> engine = line.Length > 0
            && string.Equals(Path.GetFileName(line[0]), Path.GetFileName(OS.GetExecutablePath()), StringComparison.OrdinalIgnoreCase)
            ? line.Skip(1).TakeWhile(a => a is not ("--" or "++"))
            : OS.GetCmdlineArgs();
        var args = new List<string>(engine) { "--" };
        if (_ctx.Session.Folder is { } folder)
        {
            args.Add(folder);
        }

        if (BackgroundMode.Active)
        {
            args.Add(BackgroundMode.Flag);
        }

        return args;
    }

    /// <summary>Says how the last install went (written by the helper; also after installing on quit).</summary>
    private void Report(UpdateResult result)
    {
        SemVersion now = UpdateEnvironment.Version;
        if (result.Installed)
        {
            if (SemVersion.TryParse(result.To, out SemVersion? to) && to == now)
            {
                _ctx.Log($"Updated from {result.From} to {now}.");
                _ctx.SetStatus($"Updated to {now}");
                if (_staged is null)
                {
                    SetLink($"Updated to {now}: what's new", "Open the release notes", () => OpenPage($"{EditorRelease.ReleasesPage}/tag/v{now}"));
                }
            }

            return;
        }

        string reason = result.Reason ?? "The update log has the details.";
        _ctx.Log($"Update to {result.To} not installed: {reason}", Severity.Error);
        _ctx.SetStatus("Update not installed");
        Dialogs.Inform("Update", $"{EditorRelease.Product} {result.To} was not installed", $"{reason} You have {now}.",
            icon: "warning", action: "Open Log", actionChosen: () => OpenPage(_log.Path));
    }

    private static string Why(UpdateCheckResult r) =>
        r.RateLimitReset is { } reset
            ? $"GitHub's limit for checks was reached; try again after {reset.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}."
            : r.Problem ?? "GitHub could not be reached.";

    /// <summary>Download progress on the status line, a few times a second.</summary>
    private sealed class StatusProgress(EditorContext ctx, CancellationToken cancel) : IProgress<DownloadProgress>
    {
        private long _last;

        public void Report(DownloadProgress value)
        {
            long now = System.Environment.TickCount64;
            if (cancel.IsCancellationRequested || (now - Interlocked.Read(ref _last) < 250 && value.Received < value.Total))
            {
                return;
            }

            Interlocked.Exchange(ref _last, now);
            int percent = value.Total > 0 ? (int)(value.Received * 100 / value.Total) : 0;
            string text = $"Downloading update: {percent}% of {UpdateChecker.Size(value.Total)}";
            BackgroundJobs.Post(() => ctx.SetStatus(text));
        }
    }
}
