using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Updates;
using OpenScct.Launcher.Update;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Cli.Commands;

/// <summary>
/// <c>ose update</c>: checks GitHub for a newer editor, and the steps the editor runs to install one (docs/RELEASING.md).
/// <c>check</c> works anywhere; <c>stage</c> downloads and unpacks into a release folder; <c>apply</c> and
/// <c>recover</c> are run by the editor from a copy of <c>ose</c> outside the folder they change.
/// </summary>
internal static partial class UpdateCommands
{
    [CliProvider]
    public static IEnumerable<CliCommand> Provide() =>
    [
        new("update",
            "update check [--install <folder>] [--prereleases] [--json] | update stage [--install <folder>] [--version <version>] [--json] [--cancel-file <path>] | " +
            "update status --install <folder> --json | update install-staged --install <folder> --version <version> --json | " +
            "update apply --plan <file> [--no-restart] | update recover (--install <folder> | --swap <record>) [--no-restart]",
            "Editor updates. check: is a newer release out (for the release folder ose runs from, or --install; from source it " +
            "only reports the latest release)? stage: download, verify and unpack it into that folder, ready to swap in when the " +
            "editor next restarts. apply and recover are used by the editor: apply swaps a staged build in after the editor quit, " +
            "recover puts the previous build back after an interrupted swap (run at sign-in by the entry the swap leaves).",
            RunAsync),
    ];

    private static Task<int> RunAsync(CliArgs a) => a.Positional(0) switch
    {
        "check" => CheckAsync(a),
        "stage" => ProtocolAsync(a, () => StageProtocolAsync(a)),
        "status" => ProtocolAsync(a, () => Task.FromResult(Status(a))),
        "install-staged" => ProtocolAsync(a, () => InstallStagedAsync(a)),
        "apply" => Task.FromResult(Apply(a)),
        "recover" => Task.FromResult(Recover(a)),
        _ => throw new ArgumentException("give check, stage, apply or recover"),
    };

    /// <summary>The release folder to work on: --install, else the one this ose is in (…\OpenSCCT Editor\ose\ose.exe).</summary>
    private static string? InstallFolder(CliArgs a)
    {
        if (a.Get("install") is { } given)
        {
            return Path.GetFullPath(given);
        }

        string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        return File.Exists(InstallInfo.PathIn(parent)) ? parent : null;
    }

    private static async Task<int> CheckAsync(CliArgs a)
    {
        string? install = InstallFolder(a);
        InstallInfo? info = install is null ? null : InstallInfo.TryRead(install);
        if (install is not null && info?.ParsedVersion is null)
        {
            throw new ArgumentException($"{install} has no readable {InstallInfo.FileName}; it is not a release folder");
        }

        SemVersion current = info?.ParsedVersion ?? EditorRelease.Version;
        using HttpClient http = UpdateHttp.ForChecks();
        UpdateCheckResult r = await UpdateChecker.CheckAsync(http, UpdatePaths.Default, current, a.Has("prereleases"), CancellationToken.None,
            install is not null && LauncherIntegration.IsManaged(install)).ConfigureAwait(false);
        bool fromSource = info is null;
        if (a.Has("json"))
        {
            Console.WriteLine(ApiJson.Write(ApiJson.Obj(
                ("current", ApiJson.Str(current.ToString())),
                ("install", install is null ? null : ApiJson.Str(install)),
                ("fromSource", ApiJson.Bool(fromSource)),
                ("result", ApiJson.Str(r.Kind.ToString())),
                ("latest", r.Latest is null ? null : ApiJson.Str(r.Latest.ToString())),
                ("available", ApiJson.Bool(r.Kind == UpdateCheckKind.Available && !fromSource)),
                ("asset", r.Offer is null ? null : ApiJson.Str(r.Offer.Archive.Name)),
                ("size", r.Offer is null ? null : SvJsonNode.Integer(r.Offer.Archive.Size)),
                ("page", r.Offer is null ? null : ApiJson.Str(r.Offer.Page)),
                ("fromCache", ApiJson.Bool(r.FromCache)),
                ("problem", r.Problem is null ? null : ApiJson.Str(r.Problem)))));
            return r.Kind == UpdateCheckKind.Failed ? 1 : 0;
        }

        Console.WriteLine(fromSource
            ? $"OpenSCCT Editor {EditorRelease.Display}, built from source: updates are off (git pull instead)."
            : $"OpenSCCT Editor {current} in {install}");
        if (r.Problem is not null)
        {
            Console.WriteLine((r.Kind == UpdateCheckKind.Failed ? "Could not check for updates: " : "Note: ") + r.Problem);
        }

        switch (r.Kind)
        {
            case UpdateCheckKind.Available when r.Offer is { } offer:
                Console.WriteLine($"Update available: {EditorRelease.Describe(offer.Version)}, {offer.Archive.Name} ({UpdateChecker.Size(offer.Archive.Size)})");
                Console.WriteLine($"  {offer.Page}");
                foreach (string line in offer.Notes)
                {
                    Console.WriteLine("  " + line);
                }

                break;
            case UpdateCheckKind.UpToDate:
                Console.WriteLine(r.Latest is null ? $"No release for {EditorRelease.Platform} yet." : $"Up to date (latest release {r.Latest}).");
                break;
        }

        return r.Kind == UpdateCheckKind.Failed ? 1 : 0;
    }

    private static int Apply(CliArgs a)
    {
        string planFile = a.Get("plan") ?? throw new ArgumentException("give --plan <file>");
        ApplyPlan plan = ApplyPlan.Read(planFile);
        // The plan sits in the updates folder that started this helper (its log and recovery records go there too).
        var paths = new UpdatePaths(Path.GetDirectoryName(Path.GetFullPath(planFile))!);
        var log = new UpdateLog(paths.Log) { Echo = Console.Out };
        return new UpdateSwap(log, paths).Apply(plan, restart: !a.Has("no-restart"));
    }

    private static int Recover(CliArgs a)
    {
        UpdatePaths paths = UpdatePaths.Default;
        string install;
        if (a.Get("swap") is { } record)
        {
            // Run at sign-in from the helper's copy: the record sits in that updates folder's "swaps".
            record = Path.GetFullPath(record);
            paths = new UpdatePaths(Path.GetDirectoryName(Path.GetDirectoryName(record)!)!);
            install = RecoveryEntry.InstallOf(record) ?? throw new ArgumentException($"{record} cannot be read");
        }
        else
        {
            install = a.Get("install") ?? throw new ArgumentException("give --install <release folder> or --swap <record>");
        }

        var log = new UpdateLog(paths.Log) { Echo = Console.Out };
        return new UpdateSwap(log, paths).Recover(install, restart: !a.Has("no-restart"));
    }

    /// <summary>Download progress on one console line, at most every half second.</summary>
    private sealed class ConsoleProgress : IProgress<DownloadProgress>
    {
        private long _last = -1;

        public void Report(DownloadProgress value)
        {
            long now = Environment.TickCount64;
            if (_last >= 0 && now - _last < 500 && value.Received < value.Total)
            {
                return;
            }

            _last = now;
            int percent = value.Total > 0 ? (int)(value.Received * 100 / value.Total) : 0;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {percent,3}%  {UpdateChecker.Size(value.Received)} of {UpdateChecker.Size(value.Total)}"));
        }
    }
}
