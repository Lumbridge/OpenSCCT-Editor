using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Updates;
using OpenScct.Launcher.GitHub;
using OpenScct.Launcher.Update;
using OpenScct.Launcher.Versioning;

internal static class Program
{
    private static int _checks;

    private static async Task Main(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith("--update-probe=", StringComparison.Ordinal)) is { } probe)
        {
            using JsonDocument build = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "version.json")));
            File.WriteAllText(probe["--update-probe=".Length..], build.RootElement.GetProperty("version").GetString());
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "ose-launcher-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? oldRoot = Environment.GetEnvironmentVariable(GameExe.LauncherRootVariable);
        string? oldExe = Environment.GetEnvironmentVariable("OSE_GAME_EXE");
        string? oldContent = Environment.GetEnvironmentVariable("OSE_CONTENT");
        string? oldData = Environment.GetEnvironmentVariable("OSE_DATA");
        try
        {
            Environment.SetEnvironmentVariable(GameExe.LauncherRootVariable, root);
            Environment.SetEnvironmentVariable("OSE_DATA", Path.Combine(root, "user-data"));
            string obsolete = Path.Combine(root, "obsolete.exe");
            File.WriteAllText(obsolete, "old");
            Environment.SetEnvironmentVariable("OSE_GAME_EXE", obsolete);
            Environment.SetEnvironmentVariable("OSE_CONTENT", Path.Combine(root, "old-content"));
            string packs = Path.Combine(root, "packs");
            Directory.CreateDirectory(Path.Combine(packs, "shared"));
            File.WriteAllText(Path.Combine(packs, "shared", "classes.json"), "{}");
            string v1 = Game(root, "0.8.112"), v2 = Game(root, "0.8.113");
            File.WriteAllText(Path.Combine(root, "install.json"), "{\"Current\":\"0.8.112\"}");
            Check(GameExe.Find() == v1, "managed root beats an inherited stale executable");
            Check(ContentLocator.FindContentRoot() == packs, "managed content uses packs");
            Check(GameExe.LauncherContent(v1) == packs, "headless content uses packs");
            File.WriteAllText(Path.Combine(root, "install.json"), "{\"Current\":\"0.8.113\"}");
            Check(GameExe.Find() == v2, "game is resolved again after version switch");
            Check(SvmbuildRunner.InstalledBuild() == Path.GetDirectoryName(v2), "builder follows the same root/version");
            Directory.Delete(Path.GetDirectoryName(v1)!, true);
            Check(GameExe.Find() == v2, "pruning old game does not break editor");
            Environment.SetEnvironmentVariable(GameExe.LauncherRootVariable, null);
            Check(GameExe.Find() == obsolete, "standalone executable override is preserved");
            Environment.SetEnvironmentVariable(GameExe.LauncherRootVariable, root);

            string install = Path.Combine(root, "editor");
            Fixture(install, "0.1.0", protocol: 1);
            File.WriteAllText(Path.Combine(install, LauncherIntegration.ManagedMark), "1");
            var paths = new UpdatePaths(Path.Combine(root, "updates"));
            var log = new UpdateLog(paths.Log);
            Check(LauncherIntegration.Problem(InstallInfo.Read(install)) is null, "new package protocol accepted");
            Check(LauncherIntegration.Problem(new InstallInfo()) is not null, "legacy package not adopted as managed");
            SemVersion next = SemVersion.Parse("0.2.0");
            Check(UpdatePaths.OperationName(install, next) != UpdatePaths.OperationName(install, next), "helper handoffs are unique");
            UpdateResult.Success("0.1.0", "0.2.0").Write(install, log);
            Check(UpdateResult.Read(install)?.Installed == true && UpdateResult.Read(install)?.Installed == true,
                "launcher status does not consume editor update notification");

            string package = Path.Combine(root, "package", EditorRelease.FolderName);
            Fixture(package, next.ToString(), protocol: 1);
            string zip = Path.Combine(root, "release.zip");
            ZipFile.CreateFromDirectory(Path.GetDirectoryName(package)!, zip);
            var handler = new Feed(zip, next);
            using var http = new HttpClient(handler);
            UpdateOffer offer = handler.Offer;
            var stager = new UpdateStager(http, paths, install, log);
            using (UpdateLock held = UpdateLock.TryTake(paths)!)
            {
                await Throws<UpdateBusyException>(() => stager.StageAsync(offer, null, default), "stage respects global update lock");
                Check(new UpdateSwap(log, paths).Apply(new ApplyPlan { Install = install }, false) == 2, "apply respects global update lock");
                Check(new UpdateSwap(log, paths).Recover(install, false) == 2, "recover respects global update lock");
            }
            using (UpdateLock held = UpdateLock.TryTake(UpdatePaths.SwapLock(install))!)
            {
                await Throws<UpdateBusyException>(() => stager.StageAsync(offer, null, default), "stage respects swap lock");
                Check(new UpdateSwap(log, paths).Apply(new ApplyPlan { Install = install }, false) == 2, "apply respects swap lock");
            }
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => stager.StageAsync(offer, null, cancel.Token), "cancelled staging does not download");
            }
            StagedUpdate staged = await stager.StageAsync(offer, null, default);
            Check(staged.Version == next && UpdateStager.CheckFiles(staged.Folder, staged.Info) is null, "stage verifies manifest and hashes");
            Check(InstallInfo.Read(install).Version == "0.1.0", "staging leaves installed version unchanged");
            File.WriteAllText(Path.Combine(staged.Folder, EditorRelease.ExeName), "damaged");
            staged = await stager.StageAsync(offer, null, default);
            Check(UpdateStager.CheckFiles(staged.Folder, staged.Info) is null, "corrupt staged build is re-extracted");
            // A deliberately non-executable fixture proves a failed startup probe rolls back without touching user files.
            File.WriteAllText(Path.Combine(install, "my-map.osmap"), "user data");
            int applied = new UpdateSwap(log, paths) { ProbeWait = TimeSpan.FromSeconds(2), RenameRetry = TimeSpan.FromSeconds(1) }
                .Apply(new ApplyPlan { Install = install, Staged = staged.Folder, From = "0.1.0", To = next.ToString() }, false);
            Check(applied == 1 && InstallInfo.Read(install).Version == "0.1.0", "failed probe restores previous installation");
            Check(File.ReadAllText(Path.Combine(install, "my-map.osmap")) == "user data", "rollback preserves user projects");
            Check(File.Exists(Path.Combine(install, LauncherIntegration.ManagedMark)), "swap preserves launcher ownership marker");

            string cancelFile = Path.Combine(root, "cancel");
            File.WriteAllText(cancelFile, "1");
            (int code, string output) = await Cli("update", "stage", "--install", install, "--version", "0.2.0", "--json", "--cancel-file", cancelFile);
            Check(code == 1 && JsonDocument.Parse(output).RootElement.GetProperty("status").GetString() == "cancelled", "CLI cancellation is structured");
            (code, output) = await Cli("update", "stage", "--install", install, "--version", "0.1.0", "--json");
            Check(code == 0 && JsonDocument.Parse(output).RootElement.GetProperty("status").GetString() == "current", "exact current target needs no network");
            (code, output) = await Cli("update", "status", "--install", install, "--json");
            Check(code == 0 && JsonDocument.Parse(output).RootElement.GetProperty("launcherProtocol").GetInt32() == 1, "CLI status reports protocol");
            Check(UpdateResult.Read(install) is not null, "CLI status leaves failure notice for editor");

            // A runnable package exercises the real headless startup acknowledgement and later recovery.
            SemVersion runnable = SemVersion.Parse("0.3.0");
            string runtimeStage = UpdatePaths.Staged(install, runnable);
            RuntimeFixture(runtimeStage, runnable.ToString());
            File.WriteAllText(Path.Combine(runtimeStage, UpdatePaths.StagedMarkName), runnable.ToString());
            applied = new UpdateSwap(log, paths).Apply(new ApplyPlan { Install = install, Staged = runtimeStage,
                From = "0.1.0", To = runnable.ToString() }, false);
            Check(applied == 0 && InstallInfo.Read(install).Version == "0.3.0", "successful headless probe commits update");
            Check(UpdateResult.Read(install)?.Installed == true, "successful apply reports installed");
            Check(File.ReadAllText(Path.Combine(install, "my-map.osmap")) == "user data", "successful update preserves user projects");

            // Emulate interruption after moving one old entry, then run the same recovery the helper uses.
            string old = UpdatePaths.Old(install, runnable);
            Directory.CreateDirectory(old);
            string interruptedStage = UpdatePaths.Staged(install, SemVersion.Parse("0.4.0"));
            RuntimeFixture(interruptedStage, "0.4.0");
            string exe = Path.Combine(install, EditorRelease.ExeName);
            File.Move(exe, Path.Combine(old, EditorRelease.ExeName));
            new SwapJournal { From = "0.3.0", To = "0.4.0", Install = install,
                Staged = interruptedStage, Old = old,
                OldEntries = InstallInfo.Read(install).Entries, NewEntries = InstallInfo.Read(install).Entries }.Write();
            Check(new UpdateSwap(log, paths).Recover(install, false) == 0 && File.Exists(exe), "interrupted swap recovers moved executable");
            Check(!File.Exists(UpdatePaths.Journal(install)), "recovery clears completed journal");

            SemVersion cliTarget = SemVersion.Parse("0.4.0");
            string cliStage = UpdatePaths.Staged(install, cliTarget);
            File.WriteAllText(Path.Combine(cliStage, UpdatePaths.StagedMarkName), cliTarget.ToString());
            (code, output) = await Cli("update", "install-staged", "--install", install, "--version", cliTarget.ToString(), "--json");
            Check(code == 0 && JsonDocument.Parse(output).RootElement.GetProperty("status").GetString() == "installed",
                "CLI orchestrates external apply helper and reports structured success");
            Check(InstallInfo.Read(install).Version == cliTarget.ToString(), "CLI apply installs exact selected version");

            Console.WriteLine($"PASS: {_checks} launcher integration checks");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GameExe.LauncherRootVariable, oldRoot);
            Environment.SetEnvironmentVariable("OSE_GAME_EXE", oldExe);
            Environment.SetEnvironmentVariable("OSE_CONTENT", oldContent);
            Environment.SetEnvironmentVariable("OSE_DATA", oldData);
            Directory.Delete(root, true);
        }
    }

    private static string Game(string root, string version)
    {
        string dir = Path.Combine(root, "versions", version);
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "OpenSCCT.exe");
        File.WriteAllText(exe, "fixture");
        return exe;
    }

    private static void Fixture(string root, string version, int protocol)
    {
        Directory.CreateDirectory(Path.Combine(root, "ose"));
        File.WriteAllText(Path.Combine(root, EditorRelease.ExeName), "deliberately not an executable " + version);
        File.WriteAllText(Path.Combine(root, "ose", EditorRelease.CliExeName), "fixture cli");
        var info = new InstallInfo { Version = version, Platform = EditorRelease.Platform, LauncherProtocol = protocol,
            MinimumGameVersion = "0.8.112", Entries = [EditorRelease.ExeName, "ose", InstallInfo.FileName] };
        info.Files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(path => new InstallFile {
            Path = Path.GetRelativePath(root, path).Replace('\\', '/'), Size = new FileInfo(path).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToList();
        File.WriteAllText(InstallInfo.PathIn(root), JsonSerializer.Serialize(info));
    }

    private static void RuntimeFixture(string root, string version)
    {
        Directory.CreateDirectory(Path.Combine(root, "ose"));
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            File.Copy(file, Path.Combine(root, "ose", Path.GetFileName(file)));
        }
        File.Copy(Path.Combine(root, "LauncherIntegrationChecks.exe"), Path.Combine(root, EditorRelease.ExeName));
        var info = new InstallInfo { Version = version, Platform = EditorRelease.Platform, LauncherProtocol = 1,
            MinimumGameVersion = "0.8.112", Entries = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().Append(InstallInfo.FileName).ToList() };
        info.Files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(path => new InstallFile {
            Path = Path.GetRelativePath(root, path).Replace('\\', '/'), Size = new FileInfo(path).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToList();
        File.WriteAllText(InstallInfo.PathIn(root), JsonSerializer.Serialize(info));
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }

    private static async Task Throws<T>(Func<Task<StagedUpdate>> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("FAILED: " + name);
    }

    private static async Task<(int, string)> Cli(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ose.dll"));
        foreach (string a in args) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string stderr = await error;
        if (stderr.Length > 0) Console.WriteLine(stderr);
        return (process.ExitCode, (await output).Trim());
    }

    private sealed class Feed : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = [];
        public UpdateOffer Offer { get; }
        public Feed(string zip, SemVersion version)
        {
            byte[] bytes = File.ReadAllBytes(zip);
            ReleaseAsset Asset(string name, byte[] body)
            {
                string url = "https://fixture.invalid/" + name;
                _files[url] = body;
                return new ReleaseAsset { Id = _files.Count, Name = name, Size = body.Length, Url = url, BrowserDownloadUrl = url };
            }
            string name = EditorRelease.AssetName(version);
            ReleaseAsset archive = Asset(name, bytes);
            ReleaseAsset checksum = Asset(name + ".sha256", Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)) + "  " + name));
            ReleaseAsset metadata = Asset(LauncherIntegration.AssetName(version), Encoding.UTF8.GetBytes("{\"launcherProtocol\":1,\"minimumGameVersion\":\"0.8.112\"}"));
            Offer = new UpdateOffer(new VersionedRelease(version, new ReleaseInfo { TagName = "v" + version, Assets = [archive, checksum, metadata] }), archive, checksum);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_files[request.RequestUri!.AbsoluteUri]) });
        }
    }
}
