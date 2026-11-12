using System.Security.Cryptography;
using OpenScct.Editor.Core.Interop.Sdc.Lights;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// Imports an original map package (.sdc), an editor copy (Packages\MapsEd, with its brushes) or a cooked copy
/// (Packages\Maps). The file is read natively first (<see cref="SdcMapReader"/>): every actor with every stored
/// property, every brush with its faces, the shapes of volumes and movers. OpenSCCT's content builder then converts it
/// to an SVMap folder (the same conversion the game runs) for the baked world (BSP render, collision, lighting), the
/// map settings and sounds, which the SVMap importer reads; the native data is laid over that project
/// (<see cref="SdcOverlay"/>). Needs the builder (an OpenSCCT checkout with Python 3.10+, or an installed OpenSCCT
/// build, see <see cref="SvmbuildRunner"/>) and the original game install (the converter reads the stock script,
/// texture and mesh packages).
/// <para>
/// Conversions are kept in <c>%LOCALAPPDATA%\OpenSCCT-Editor\imports\&lt;Map&gt;-&lt;sha&gt;\</c>: the project's baked
/// world points at that SVMap folder, and importing the same file again reuses the builder's cache there.
/// </para>
/// </summary>
[EditorService]
public sealed class SdcImporter : IMapImporter
{
    /// <summary>The install the builder uses when none is given (its own default).</summary>
    public static string DefaultGameRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "Enhanced SCCT Versus v4.0 Public Beta 3");

    private static readonly HashSet<string> s_packageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".usx", ".utx", ".utc", ".ukx", ".uax", ".uas", ".u",
    };

    /// <summary>The map's text files (names and briefings, per language), as the game's System/_PC_ holds them.</summary>
    private static readonly HashSet<string> s_textExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".int", ".eng", ".fra", ".deu", ".ita", ".esp",
    };

    public string Name => "sdc";

    public bool CanImport(string path) => File.Exists(path) && Path.GetExtension(path).Equals(".sdc", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the import report says first of a map RE+ recovered from a compiled map.</summary>
    public const string ReRecoveredNote = "this map was recovered by RE+ from a compiled map (its map recovery rebuilds an editor copy from the "
        + "cooked .sdc), so it can carry defects of its own, such as bad materials, odd brushes or recovered volumes; they are its own, not the "
        + "original editor's, and any warnings below are what was wrong in the file";

    /// <summary>
    /// True for a map RE+'s map recovery rebuilt from a compiled map: named <c>*_Recovered*</c>, or in its recovery
    /// folder (<c>MapsEd\Recovery\...\CompiledInput.sdc</c>).
    /// </summary>
    public static bool IsReRecovered(string path) =>
        Path.GetFileNameWithoutExtension(path).Contains("_Recovered", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals("CompiledInput.sdc", StringComparison.OrdinalIgnoreCase)
        || path.Replace('/', '\\').Contains(@"\MapsEd\Recovery\", StringComparison.OrdinalIgnoreCase);

    public ImportResult Import(string path, ImportOptions options)
    {
        string sdc = Path.GetFullPath(path);
        string map = Path.GetFileNameWithoutExtension(sdc);
        if (!IsPackageName(map))
        {
            throw new ArgumentException($"'{map}' is not a usable package name (letters, digits, _ and - only)");
        }

        options.Progress?.Invoke($"reading {Path.GetFileName(sdc)}");
        UeSchema? schema = UeSchema.Load(options.ContentRoot ?? EditorServices.ContentRoot);
        // The map is converted and compiled under its map id, so the paths of its own objects are rooted there.
        UPackage pkg = Native(sdc, () => UPackage.Load(sdc, MapId(map)));
        // Most maps decode only with the class data; without the editor's, the builder's classes step makes it in the
        // conversion's content folder, so the actors are read after the conversion.
        SdcMap? native = schema is null ? null : Native(sdc, () => SdcMapReader.Read(pkg, schema));
        SdcLayout layout = native?.Layout ?? Native(sdc, () => SdcLayoutDetector.Detect(pkg));

        SvmbuildRunner runner = SvmbuildRunner.Find()
            ?? throw new InvalidOperationException(
                "importing an .sdc needs OpenSCCT's content builder: install OpenSCCT, or set OSE_OPENSCCT to an OpenSCCT checkout or game build");
        string game = GameRoot(sdc, options.GameRoot);
        string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sdc)));
        string work = Path.Combine(SvmbuildRunner.DataRoot, "imports", $"{map}-{sha[..12]}");
        try
        {
            ImportResult converted = ConvertMap(sdc, map, sha, game, work, runner, layout, options);
            schema ??= UeSchema.Load(Path.Combine(work, "content"));
            native ??= Native(sdc, () => SdcMapReader.Read(pkg, schema));
            var findings = new List<Finding>(converted.Findings);
            if (MapId(map) != map)
            {
                findings.Add(Finding.Info("import.sdcMapId",
                    $"the package name {map} is not a map id (letters, digits and _), so the map is imported as {MapId(map)}"));
            }

            SdcOverlay.Apply(converted.Project, native, schema, findings);
            MapClasses.Keep(converted.Project, MapClasses.Read(pkg));
            if (IsReRecovered(sdc))
            {
                findings.Insert(0, Finding.Warning(ImportReport.ReRecoveredCode, ReRecoveredNote));
            }

            if (native.Layout == SdcLayout.Cooked)
            {
                // The cook threw the brushes away: recover them from the compiled BSP. A failure leaves the baked world
                // only, with a warning.
                BrushRecoveryImport.Run(converted.Project, pkg, native.Layout, options.BrushRecovery, options.Progress, findings);

                // The cook stripped the static lights that only lit the bake: bring back what can be found. The
                // recovery reads other installs' files and bakes; if it fails, the map still imports with its cooked
                // lightmaps (Tier 3).
                try
                {
                    LightRecoveryReport? lights = LostLightRecovery.Run(converted.Project, sdc, Path.Combine(work, "content", "svmap", MapId(map)), schema, new LightRecoveryOptions
                    {
                        Mode = options.LightRecovery,
                        GameRoot = game,
                        TwinFolders = options.TwinFolders,
                        Fit = new LightFitSettings { TimeLimit = options.LightFitTime },
                        ContentRoot = options.ContentRoot,
                        Progress = options.Progress,
                    }, findings);

                    // The light recovery's search found the editor copy of this very bake: its brushes are the author's.
                    BrushRecoveryImport.NoteEditorCopy(converted.Project, lights?.Twin, lights?.TwinExact == true, findings);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                {
                    LostLightRecovery.Discard(converted.Project);
                    findings.Add(Finding.Warning("import.sdcLightsFailed",
                        $"the light recovery failed ({ex.GetType().Name}: {ex.Message}), so the lights the cook stripped are missing; the cooked "
                        + "lightmaps are kept, and a relight would come out darker. Run ose sdc-lights on the project to try again"));
                }
            }

            return new ImportResult(converted.Project, findings);
        }
        finally
        {
            // The stand-in install (hard links, see InstallWithCopy) is only needed while the builder runs.
            DeleteStandIn(work);
        }
    }

    /// <summary>Runs a step of the native read; a file it cannot read is reported as such.</summary>
    private static T Native<T>(string sdc, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{sdc} is not an original map package the editor can read: {ex.Message}", ex);
        }
    }

    private static ImportResult ConvertMap(string sdc, string map, string sha, string game, string work, SvmbuildRunner runner, SdcLayout layout,
        ImportOptions options)
    {
        var findings = new List<Finding>();
        string content = Path.Combine(work, "content");
        string install = game;
        string? addons = null;
        string id = MapId(map);
        // A map converted under another id always goes through the staged addons, which hold it under that id.
        string? stock = id == map ? StockCopy(game, map) : null;
        if (stock is not null && !SameFile(stock, sdc, sha))
        {
            // The builder always takes the install's file of a name, so a changed copy of a stock map (an editor copy in
            // Packages\MapsEd, or one from another install) runs against a stand-in install that holds it instead.
            options.Progress?.Invoke($"the install has its own {Path.GetFileName(stock)}; converting {sdc} in its place");
            install = InstallWithCopy(game, stock, sdc, work);
        }
        else if (stock is null)
        {
            addons = Addons(sdc, map, id, game, work);
        }

        string? contentRoot = options.ContentRoot ?? EditorServices.ContentRoot;
        bool haveClasses = Seed(contentRoot, content, Path.Combine("shared", "classes.json"));
        bool haveAudio = Seed(contentRoot, content, Path.Combine("shared", "audio", "audio.json"));

        var log = new List<string>();
        void OnLine(string line)
        {
            log.Add(line);
            options.Progress?.Invoke(line);
        }

        // The class table normally comes from the editor's content folder; without one the classes step makes it.
        List<string> steps = haveClasses ? ["maps"] : ["classes", "maps"];
        options.Progress?.Invoke($"converting {map}.sdc with the content builder ({string.Join(", ", steps)})");
        int code = runner.Run(Arguments(steps, id, install, content, addons), work, OnLine);
        string folder = Path.Combine(content, "svmap", id);
        if ((code != 0 || !SvMapImport.IsSvMapFolder(folder)) && layout == SdcLayout.Editor && stock is not null && install != game)
        {
            // The builder could not convert the editor copy. The install's game copy has the same baked world when it
            // comes from the same save; brushes and actors still come from this file.
            string failure = Failure(code, log);
            options.Progress?.Invoke($"the content builder could not convert the editor copy {sdc} ({failure}); taking the baked world from the install's {stock}");
            DeleteStandIn(work);
            install = game;
            log.Clear();
            code = runner.Run(Arguments(steps, map, install, content, addons), work, OnLine);
            string remedy = runner.ReadsEditorCopies switch
            {
                false => "This builder predates editor-copy support: update OpenSCCT and import again.",
                true => "This builder reads editor copies, so something in the file stopped it: if the file opens in the stock editor, "
                    + "report this error with the file.",
                // Its sources could not be read (an installed build laid out differently), so it may be either.
                null => "Whether this builder reads editor copies could not be told from its files: update OpenSCCT and import again, and "
                    + "if the error stays, report it with the file.",
            };
            findings.Add(Finding.Warning("import.sdcBakedFromGameCopy",
                $"the content builder of {runner.Description} could not convert this editor copy ({failure}), so the baked world (render, "
                + $"collision, lighting) and the map settings come from the install's game copy {stock}; brushes, actors and their "
                + "properties come from this file. If the two copies are from different saves, the baked world shows the game copy's "
                + $"level. {remedy}"));
        }

        if (code != 0 || !SvMapImport.IsSvMapFolder(folder))
        {
            throw new InvalidOperationException(
                $"the content builder could not convert {map}.sdc (exit code {code}):{Environment.NewLine}"
                + string.Join(Environment.NewLine, log.TakeLast(20)));
        }

        // The map's sounds (audio/) are a separate step; it needs the shared sound library from the content folder.
        if (haveAudio)
        {
            log.Clear();
            if (runner.Run(Arguments(["audio-map"], id, install, content, addons), work, OnLine) != 0)
            {
                findings.Add(Finding.Warning("import.sdcAudio",
                    $"the map's sounds were not converted, so ambient sounds are missing: {log.LastOrDefault(l => l.Trim().Length > 0)}"));
            }
        }
        else
        {
            findings.Add(Finding.Warning("import.sdcAudio",
                "no shared sound library (shared/audio/audio.json in the content folder), so the map's ambient sounds are not imported"));
        }

        // The brushes come from the .sdc itself (its own, or recovered from its BSP with its surfs), not the conversion.
        ImportResult result = SvMapImport.Import(folder, options with { BrushRecovery = BrushRecoveryMode.Off });
        result.Project.Source = new ProjectSource { Kind = "sdc", File = sdc, Sha256 = sha };
        findings.Add(Finding.Info("import.sdc",
            $"converted with the content builder of {runner.Description} (game {game}{(addons is null ? "" : $", addons {addons}")}"
            + $"{(install == game ? "" : $", this copy in place of the install's {map}.sdc")}); "
            + $"the SVMap is kept in {folder}"));
        findings.AddRange(result.Findings);
        return new ImportResult(result.Project, findings);
    }

    /// <summary>What a failed builder run says: its exit code and the last error it printed, or that it wrote no map.</summary>
    private static string Failure(int code, IReadOnlyList<string> log)
    {
        if (code == 0)
        {
            return "it finished but wrote no SVMap folder";
        }

        // A Python error ends with its exception line; otherwise the builder's last words.
        string? error = log.LastOrDefault(l => l.Contains("Error", StringComparison.Ordinal) || l.Contains("error:", StringComparison.OrdinalIgnoreCase))
            ?? log.LastOrDefault(l => l.Trim().Length > 0);
        return error is null ? $"exit code {code}" : $"exit code {code}: {error.Trim()}";
    }

    private static List<string> Arguments(IEnumerable<string> steps, string map, string game, string content, string? addons)
    {
        var args = new List<string>(steps) { "--maps", map, "--game", game, "--out", content, "--no-deps" };
        if (addons is not null)
        {
            args.AddRange(["--addons", addons]);
        }

        return args;
    }

    /// <summary>The original install: given, else the install the .sdc sits in (&lt;root&gt;\Packages\Maps, or an
    /// editor copy in &lt;root&gt;\Packages\MapsEd), else SVM_GAME_ROOT, else the builder's default.</summary>
    public static string GameRoot(string? sdc, string? given)
    {
        string? env = Environment.GetEnvironmentVariable("SVM_GAME_ROOT");
        DirectoryInfo? maps = string.IsNullOrEmpty(sdc) ? null : new FileInfo(sdc).Directory;
        string? around = maps is { Parent: { Name: var pk, Parent: { } root } }
            && pk.Equals("Packages", StringComparison.OrdinalIgnoreCase)
            && IsInstall(root.FullName) ? root.FullName : null;
        foreach (string? candidate in new[] { given, around, string.IsNullOrEmpty(env) ? null : env, DefaultGameRoot })
        {
            if (candidate is not null && IsInstall(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            if (candidate == given && given is not null)
            {
                throw new DirectoryNotFoundException($"'{given}' is not an SCCT install (no Packages folder and System\\_PC_\\Core.u)");
            }
        }

        throw new DirectoryNotFoundException(
            "importing an .sdc needs the original game install (it reads the stock packages): give it with --game or SVM_GAME_ROOT");
    }

    /// <summary>A package name the engine can load and refer to: '.' and ' ' would split an object path.</summary>
    private static bool IsPackageName(string map) =>
        map.Length is > 0 and <= 63 && map.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>The SVMap id of a map package: SVMap ids are identifiers, so a '-' (new-island) becomes '_'.</summary>
    private static string MapId(string map) => map.Replace('-', '_');

    /// <summary>An install has the engine's script packages; a downloaded map's folder also holds Packages and System
    /// (the map's text) but not those.</summary>
    private static bool IsInstall(string root) =>
        Directory.Exists(Path.Combine(root, "Packages")) && File.Exists(Path.Combine(root, "System", "_PC_", "Core.u"));

    /// <summary>The install's own map file of this name (Packages\Maps), or null.</summary>
    private static string? StockCopy(string game, string map)
    {
        string mapsDir = Path.Combine(game, "Packages", "Maps");
        return Directory.Exists(mapsDir)
            ? Directory.EnumerateFiles(mapsDir, "*.sdc").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(map, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static bool SameFile(string stock, string sdc, string sha) =>
        string.Equals(Path.GetFullPath(stock), sdc, StringComparison.OrdinalIgnoreCase)
        || System.Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(stock))) == sha;

    /// <summary>
    /// A stand-in for <paramref name="game"/> in <c>work\game</c> whose Packages\Maps holds <paramref name="sdc"/> in
    /// place of the install's own copy (<paramref name="stock"/>). Every other file is a hard link to the install's,
    /// so it costs no disk space and the install is never changed (the builder only reads it). Editor copies
    /// (Packages\MapsEd) are left out: the builder never reads them.
    /// </summary>
    private static string InstallWithCopy(string game, string stock, string sdc, string work)
    {
        DeleteStandIn(work);
        string root = Path.Combine(work, "game");
        string fullStock = Path.GetFullPath(stock);
        string mapsEd = Path.Combine(game, "Packages", "MapsEd") + Path.DirectorySeparatorChar;
        // Never walk into links inside the install (they could lead anywhere); hidden and system files are copied too.
        var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (string file in Directory.EnumerateFiles(game, "*", walk))
        {
            string full = Path.GetFullPath(file);
            if (full.Equals(fullStock, StringComparison.OrdinalIgnoreCase) || full.StartsWith(mapsEd, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string to = Path.Combine(root, Path.GetRelativePath(game, full));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (!HardLink.Create(to, full))
            {
                DeleteStandIn(work);
                throw new InvalidOperationException(
                    $"the install has its own {Path.GetFileName(stock)}, and the editor could not stand your copy in for it "
                    + $"(hard links from {work} to {game} failed: {HardLink.LastError}); keep the editor's data folder "
                    + $"({SvmbuildRunner.DataVariable}) on the same drive as the install, or rename the map (Save As in the original editor)");
            }
        }

        // The chosen copy is copied, not linked, under the install's file name.
        CopyIfChanged(sdc, Path.Combine(root, "Packages", "Maps", Path.GetFileName(fullStock)));
        return root;
    }

    /// <summary>Removes the stand-in install. It holds only folders this importer made and hard links, so deleting it
    /// removes link names and never the install's files.</summary>
    private static void DeleteStandIn(string work)
    {
        string root = Path.Combine(work, "game");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The downloaded-maps folder for the builder when the install has no map of this name: the folder laid out like
    /// an install that holds it, else a staged copy with the map (under its SVMap id) and the packages beside it.
    /// </summary>
    private static string? Addons(string sdc, string map, string id, string game, string work)
    {
        DirectoryInfo dir = new FileInfo(sdc).Directory!;
        DirectoryInfo? root = dir.Parent is { Name: var pk, Parent: { } r } && pk.Equals("Packages", StringComparison.OrdinalIgnoreCase)
            && !Overlaps(r.FullName, game) && !Overlaps(r.FullName, work) ? r : null;
        if (root is not null && dir.Name.Equals("Maps", StringComparison.OrdinalIgnoreCase) && id == map)
        {
            return root.FullName;
        }

        // Stage the map and the files next to it (a downloaded map's textures, meshes and sounds, and its text) as an
        // addons folder laid out like an install.
        string addons = Path.Combine(work, "addons");
        string packages = Path.Combine(addons, "Packages");
        Directory.CreateDirectory(Path.Combine(packages, "Maps"));
        CopyIfChanged(sdc, Path.Combine(packages, "Maps", id + ".sdc"));
        foreach (string f in Directory.EnumerateFiles(dir.FullName))
        {
            StageAddonFile(f, map, id, addons, Path.Combine(packages, "Extra"));
        }

        if (id != map)
        {
            // The builder finds the map's names (<Map>-i.utc), sound bank (<Map>.uas) and texts by the map's name: the
            // install's copies are staged under the id too (a download's, staged below, are renamed the same way).
            foreach (string sub in new[] { "Textures", "Sounds" })
            {
                string from = Path.Combine(game, "Packages", sub);
                if (Directory.Exists(from))
                {
                    foreach (string f in Directory.EnumerateFiles(from, map + "*"))
                    {
                        StageAddonFile(f, map, id, addons, Path.Combine(packages, sub), ownFilesOnly: true);
                    }
                }
            }

            string gameTexts = Path.Combine(game, "System", "_PC_");
            if (Directory.Exists(gameTexts))
            {
                foreach (string f in Directory.EnumerateFiles(gameTexts, map + "*"))
                {
                    StageAddonFile(f, map, id, addons, packageDir: null);
                }
            }
        }

        if (root is not null)
        {
            // An editor copy (Packages\MapsEd) of a download laid out like an install: the rest of the download goes
            // with it (its packages, the map's names in Packages\Textures\<Map>-i.utc, its System texts). Its cooked
            // twin in Packages\Maps stays out: the builder must read this copy.
            foreach (string sub in Directory.EnumerateDirectories(Path.Combine(root.FullName, "Packages")))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals("Maps", StringComparison.OrdinalIgnoreCase) || sub.Equals(dir.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string f in Directory.EnumerateFiles(sub))
                {
                    StageAddonFile(f, map, id, addons, Path.Combine(packages, name));
                }
            }

            string texts = Path.Combine(root.FullName, "System", "_PC_");
            if (Directory.Exists(texts))
            {
                foreach (string f in Directory.EnumerateFiles(texts))
                {
                    StageAddonFile(f, map, id, addons, packageDir: null);
                }
            }
        }

        return addons;
    }

    /// <summary>Copies a package file into <paramref name="packageDir"/>, or the map's own text file (names and
    /// briefings) into the addons' System\_PC_; other files are left out. The map's own files (&lt;Map&gt; and
    /// &lt;Map&gt;-i) are named after <paramref name="id"/>, as the builder looks them up by it.</summary>
    private static void StageAddonFile(string file, string map, string id, string addons, string? packageDir, bool ownFilesOnly = false)
    {
        string ext = Path.GetExtension(file);
        string name = Path.GetFileNameWithoutExtension(file);
        string? own = name.Equals(map, StringComparison.OrdinalIgnoreCase) ? id
            : name.Equals(map + "-i", StringComparison.OrdinalIgnoreCase) ? id + "-i"
            : null;
        if (packageDir is not null && s_packageExtensions.Contains(ext) && (own is not null || !ownFilesOnly))
        {
            CopyIfChanged(file, Path.Combine(packageDir, (own ?? name) + ext));
        }
        else if (s_textExtensions.Contains(ext) && own is not null)
        {
            CopyIfChanged(file, Path.Combine(addons, "System", "_PC_", own + ext));
        }
    }

    private static bool Overlaps(string a, string b)
    {
        string fa = Path.GetFullPath(a).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string fb = Path.GetFullPath(b).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return fa.StartsWith(fb, StringComparison.OrdinalIgnoreCase) || fb.StartsWith(fa, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies <paramref name="rel"/> from the editor's content folder into the builder's output folder.</summary>
    private static bool Seed(string? contentRoot, string content, string rel)
    {
        string? from = contentRoot is null ? null : Path.Combine(contentRoot, rel);
        if (from is null || !File.Exists(from))
        {
            return false;
        }

        CopyIfChanged(from, Path.Combine(content, rel));
        return true;
    }

    private static void CopyIfChanged(string from, string to)
    {
        if (File.Exists(to) && new FileInfo(to).Length == new FileInfo(from).Length
            && File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(from)))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }
}
