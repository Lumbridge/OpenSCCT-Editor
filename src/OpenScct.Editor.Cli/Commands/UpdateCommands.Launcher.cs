using System.Diagnostics;
using System.Text.Json;
using OpenScct.Editor.Core.Updates;
using OpenScct.Launcher.GitHub;
using OpenScct.Launcher.Update;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Cli.Commands;

internal static partial class UpdateCommands
{
    private static async Task<int> ProtocolAsync(CliArgs args, Func<Task<int>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { return Result(args, "cancelled", args.Get("version"), "Download stopped; it can be resumed.", 1); }
        catch (UpdateBusyException ex) { return Result(args, "busy", args.Get("version"), ex.Message, 2); }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or HttpRequestException
            or InvalidDataException or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
        { return Result(args, "failed", args.Get("version"), ex.Message, 1); }
    }

    private static void Json(object value) => Console.WriteLine(JsonSerializer.Serialize(value));

    private static int Result(CliArgs args, string status, string? version, string? message = null, int code = 0, string? folder = null)
    {
        if (args.Has("json")) Json(new { type = "result", status, version, message, folder });
        else Console.WriteLine(message ?? $"{status}: {version}");
        return code;
    }

    private static int Status(CliArgs args)
    {
        string install = InstallFolder(args) ?? throw new ArgumentException("give --install <release folder>");
        InstallInfo info = InstallInfo.Read(install);
        using UpdateLock? global = UpdateLock.TryTake(UpdatePaths.Default);
        using UpdateLock? swap = global is null ? null : UpdateLock.TryTake(UpdatePaths.SwapLock(install));
        bool busy = global is null || swap is null;
        StagedUpdate? staged = busy ? null : UpdateStager.FindStaged(install, info.ParsedVersion!);
        Json(new { launcherProtocol = info.LauncherProtocol, current = info.Version, staged = staged?.Version.ToString(),
            minimumGameVersion = info.MinimumGameVersion, busy, result = UpdateResult.Read(install) });
        return 0;
    }

    private static async Task<int> StageProtocolAsync(CliArgs args)
    {
        string install = InstallFolder(args) ?? throw new ArgumentException("give --install <release folder>");
        InstallInfo currentInfo = InstallInfo.Read(install);
        SemVersion current = currentInfo.ParsedVersion!;
        SemVersion? exact = args.Get("version") is { } requested ? SemVersion.Parse(requested) : null;
        if (exact is not null && exact <= current) return Result(args, "current", current.ToString());
        using var cancel = new CancellationTokenSource();
        string? cancelFile = args.Get("cancel-file");
        void CheckCancel(object? _) { if (cancelFile is not null && File.Exists(cancelFile)) cancel.Cancel(); }
        await using var watch = new Timer(CheckCancel, null, 0, 100);
        CheckCancel(null);
        cancel.Token.ThrowIfCancellationRequested();
        var paths = UpdatePaths.Default;
        using HttpClient checks = UpdateHttp.ForChecks();
        bool managed = LauncherIntegration.IsManaged(install);
        UpdateOffer? offer;
        if (exact is not null)
        {
            var client = new GitHubReleaseClient(checks, EditorRelease.Source, paths.Cache);
            ReleaseInfo? release = await client.GetReleaseByTagAsync("v" + exact, cancel.Token).ConfigureAwait(false);
            offer = release is null ? null : UpdateChecker.Offer(release);
            if (offer is null || offer.Version != exact) throw new UpdateException($"Editor {exact} has no complete release for {EditorRelease.Platform}.");
        }
        else
        {
            UpdateCheckResult check = await UpdateChecker.CheckAsync(checks, paths, current, args.Has("prereleases"), cancel.Token, managed).ConfigureAwait(false);
            if (check.Kind == UpdateCheckKind.Failed) throw new UpdateException(check.Problem ?? "Could not check for updates.");
            offer = check.Offer;
        }
        if (offer is null) return Result(args, "current", current.ToString());
        var log = new UpdateLog(paths.Log) { Echo = args.Has("json") ? Console.Error : Console.Out };
        using HttpClient downloads = UpdateHttp.ForDownloads();
        var stager = new UpdateStager(downloads, paths, install, log);
        IProgress<DownloadProgress> progress = args.Has("json") ? new JsonProgress() : new ConsoleProgress();
        StagedUpdate ready = await stager.StageAsync(offer, progress, cancel.Token).ConfigureAwait(false);
        return Result(args, "staged", ready.Version.ToString(), folder: ready.Folder);
    }

    private static async Task<int> InstallStagedAsync(CliArgs args)
    {
        string install = InstallFolder(args) ?? throw new ArgumentException("give --install <release folder>");
        SemVersion target = SemVersion.Parse(args.Get("version") ?? throw new ArgumentException("give --version <version>"));
        // Waiting from inside the installed tree keeps its CLI files open on Windows. The launcher copies ose first.
        string own = Path.GetFullPath(AppContext.BaseDirectory);
        if (own.StartsWith(Path.TrimEndingDirectorySeparator(install) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("Run install-staged from a copy of the ose folder outside the editor installation.");
        var paths = UpdatePaths.Default;
        string planFile, helper;
        using (UpdateLock global = UpdateLock.TryTake(paths) ?? throw new UpdateBusyException("An editor update is already in progress."))
        using (UpdateLock swap = UpdateLock.TryTake(UpdatePaths.SwapLock(install)) ?? throw new UpdateBusyException("This editor is already being updated."))
        {
            InstallInfo current = InstallInfo.Read(install);
            if (current.ParsedVersion! >= target) return Result(args, "current", current.Version);
            if (InstallProcesses.Running(install).Count > 0) return Result(args, "busy", current.Version, "Save your work and close the editor and its command-line tools first.", 2);
            string staged = UpdatePaths.Staged(install, target);
            InstallInfo info = InstallInfo.Read(staged);
            if (!File.Exists(Path.Combine(staged, UpdatePaths.StagedMarkName))) throw new UpdateException("This update has not finished staging.");
            string? problem = UpdateStager.Problem(staged, info, target)
                ?? (LauncherIntegration.IsManaged(install) ? LauncherIntegration.Problem(info) : null) ?? UpdateStager.CheckFiles(staged, info);
            if (problem is not null) throw new UpdateException(problem);
            string operation = UpdatePaths.OperationName(install, target);
            helper = UpdateHelper.Prepare(paths, Path.Combine(staged, EditorRelease.CliFolder), operation);
            planFile = paths.OperationPlan(operation);
            new ApplyPlan { Install = install, Staged = staged, From = current.Version, To = target.ToString(),
                Download = paths.Download(EditorRelease.AssetName(target)) }.Write(planFile);
        }
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Root,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "update", "apply", "--plan", planFile, "--no-restart" }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new IOException("The editor update helper did not start.");
        Task stdout = ForwardLogAsync(process.StandardOutput);
        Task stderr = ForwardLogAsync(process.StandardError);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        UpdateResult? outcome = UpdateResult.Read(install);
        return Result(args, process.ExitCode == 0 ? "installed" : process.ExitCode == 2 ? "busy" : "failed",
            InstallInfo.TryRead(install)?.Version, process.ExitCode == 0 ? null : outcome?.Reason ?? "The editor could not be updated; try again when other editor operations finish.", process.ExitCode);
    }

    private static async Task ForwardLogAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) await Console.Error.WriteLineAsync(line).ConfigureAwait(false);
    }

    private sealed class JsonProgress : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => Json(new { type = "progress", phase = "downloading", received = value.Received,
            total = value.Total, bytesPerSecond = value.BytesPerSecond });
    }
}
