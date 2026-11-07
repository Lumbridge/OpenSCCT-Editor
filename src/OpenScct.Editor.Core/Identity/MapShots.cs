using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScct.Content;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Identity;

/// <summary>One camera picture of the map: its slot, recipe and bookmark, the size it is rendered at and the cache key
/// of its render.</summary>
public sealed record CameraShot(string Slot, PictureRecipe Recipe, Bookmark Bookmark, int Width, int Height, string Key);

/// <summary>Whether a camera picture has a render, and whether that render shows the map as it is now.</summary>
public enum ShotState
{
    /// <summary>Never rendered.</summary>
    Missing,

    /// <summary>Rendered from an earlier version of the map: still used, but out of date.</summary>
    Stale,

    /// <summary>Rendered from this version of the map.</summary>
    Current,
}

/// <summary>What happened to one camera picture in <see cref="MapShots.RenderAsync"/>.</summary>
public sealed record MapShotResult(string Slot, string Key, bool Ok, bool Cached, string? File, string? Error);

/// <summary>
/// Camera pictures: a workspace bookmark rendered by OpenSCCT's own renderer (<c>--svm-mapshot</c>, docs/MAPS.md
/// "Map pictures" in OpenSCCT), so menu and loading pictures look like the game. Renders are cached in the project's
/// <c>.cache/pictures</c> under a key made of the recipe, the bookmark and the size, so a compile finds them without a
/// GPU. Beside each render, <c>&lt;key&gt;.map</c> holds the key of the map it was rendered from: a render of an earlier
/// version of the map is still used, and the build says it is out of date.
/// </summary>
public static class MapShots
{
    /// <summary>The size square camera pictures are rendered at.</summary>
    public const int Size = 1024;

    /// <summary>The size the 16:9 banner is rendered at.</summary>
    public const int BannerWidth = 1920, BannerHeight = 1080;

    /// <summary>How long one render run may take (the map loads, shaders compile, then a few frames per shot).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>The camera slots of a project whose bookmark exists, with their keys; slots naming a missing bookmark
    /// are returned in <paramref name="missing"/>.</summary>
    public static List<CameraShot> CameraSlots(MapProject project, out List<string> missing)
    {
        var shots = new List<CameraShot>();
        missing = new List<string>();
        foreach ((string slot, PictureRecipe recipe) in project.Identity.Pictures.Slots())
        {
            if (recipe.From != PictureSource.Camera)
            {
                continue;
            }

            if (project.Workspace.Bookmarks.FirstOrDefault(b => b.Name == recipe.Bookmark) is { } bookmark)
            {
                (int width, int height) = SizeOf(slot);
                shots.Add(new CameraShot(slot, recipe, bookmark, width, height, Key(recipe, bookmark, width, height)));
            }
            else
            {
                missing.Add(slot);
            }
        }

        return shots;
    }

    /// <summary>The size a slot's camera picture is rendered at: 16:9 for the banner, square for the rest.</summary>
    public static (int Width, int Height) SizeOf(string slot) => slot == "banner" ? (BannerWidth, BannerHeight) : (Size, Size);

    /// <summary>The key of a compiled map's look: the hash of its sim and render files (what a render shows). The class
    /// table <c>sim/classes.json</c> is part of it, as OpenSCCT draws a device's class mesh (cameras, laser triggers,
    /// laptops) from it, so a picture rendered from a build without it (every build before the table was written by
    /// default, or <c>ose build --no-classes</c>) is stale.</summary>
    public static string MapKey(IEnumerable<KeyValuePair<string, byte[]>> files) =>
        SvMapHash.GroupHash(files.Where(f => IsShown(f.Key)).Select(f => (f.Key, ShownDigest(f.Key, f.Value))));

    private static string ShownDigest(string path, byte[] data)
    {
        // A warm lightmap cache changes the bake's ray count without changing the rendered lightmaps.
        if (path == "render/lighting.json" && JsonNode.Parse(data) is JsonObject lighting && lighting.Remove("bake"))
        {
            return SvMapHash.FileDigest(path, JsonSerializer.SerializeToUtf8Bytes(lighting));
        }

        return SvMapHash.FileDigest(path, data);
    }

    private static bool IsShown(string file) =>
        file.StartsWith("sim/", StringComparison.Ordinal) || file.StartsWith("render/", StringComparison.Ordinal);

    /// <summary>The key of a compiled map folder, the same as <see cref="MapKey(IEnumerable{KeyValuePair{string, byte[]}})"/>
    /// gives for its files.</summary>
    public static string MapKey(string mapFolder) =>
        MapKey(SvMapHash.GroupFiles(mapFolder, "sim").Concat(SvMapHash.GroupFiles(mapFolder, "render"))
            .Select(rel => new KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(Path.Combine(mapFolder, rel)))));

    /// <summary>The cache key of a camera picture. Numbers are taken at the float precision the project stores, so a
    /// bookmark made in the app keys the same before and after the project is saved and opened again.</summary>
    public static string Key(PictureRecipe recipe, Bookmark bookmark, int width, int height)
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{recipe.Look}|{(float)recipe.Fov:R}|{(float)bookmark.Location.X:R},{(float)bookmark.Location.Y:R},{(float)bookmark.Location.Z:R}|"
            + $"{bookmark.Rotation.Pitch},{bookmark.Rotation.Yaw},{bookmark.Rotation.Roll}|{width}x{height}");
        return SvMapHash.Sha256Hex(Encoding.UTF8.GetBytes(text))[..32];
    }

    /// <summary>The cached render of a key in a project folder.</summary>
    public static string CachePath(string projectFolder, string key) => Path.Combine(CacheFolderOf(projectFolder), key + ".png");

    /// <summary>Whether the render of <paramref name="key"/> exists and whether it was made from the map whose key is
    /// <paramref name="mapKey"/> (null: only whether it exists, which is all Map Check can know).</summary>
    public static ShotState StateOf(string projectFolder, string key, string? mapKey)
    {
        if (!File.Exists(CachePath(projectFolder, key)))
        {
            return ShotState.Missing;
        }

        string stamp = MapStampPath(projectFolder, key);
        return mapKey is null || (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == mapKey) ? ShotState.Current : ShotState.Stale;
    }

    private static string MapStampPath(string projectFolder, string key) => Path.Combine(CacheFolderOf(projectFolder), key + ".map");

    private static string CacheFolderOf(string projectFolder) =>
        Path.Combine(projectFolder, ProjectPictures.CacheFolder.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Renders the camera pictures of <paramref name="shots"/> that have no render of this version of the compiled map
    /// (all of them with <paramref name="force"/>) by starting OpenSCCT on it in a background window, and stores them in
    /// the project's cache. The window starts minimised and never takes the focus (<see cref="BackgroundGame"/>; when
    /// that copy cannot be made the game is not started), and the game moves it off-screen once its code runs
    /// (<c>--svm-background</c>). A game that fails, takes longer than <see cref="Timeout"/> or cannot be copied gives
    /// each picture an error; a cancel stops the game and throws.
    /// </summary>
    public static async Task<List<MapShotResult>> RenderAsync(MapProject project, string projectFolder, string compiledFolder,
        IReadOnlyList<CameraShot> shots, string exe, bool force, Action<string>? progress, CancellationToken cancel = default)
    {
        var results = new List<MapShotResult>();
        var todo = new List<CameraShot>();
        string mapKey = MapKey(compiledFolder);
        foreach (CameraShot shot in shots)
        {
            string cached = CachePath(projectFolder, shot.Key);
            if (!force && StateOf(projectFolder, shot.Key, mapKey) == ShotState.Current)
            {
                results.Add(new MapShotResult(shot.Slot, shot.Key, true, true, cached, null));
            }
            else
            {
                todo.Add(shot);
            }
        }

        if (todo.Count == 0)
        {
            return results;
        }

        if (!GameExe.SupportsMapShot(exe))
        {
            // An older game ignores --svm-mapshot and would open its main menu instead.
            string error = $"{Path.GetFileName(exe)} cannot render map pictures (its version has no --svm-mapshot); update OpenSCCT";
            results.AddRange(todo.Select(shot => new MapShotResult(shot.Slot, shot.Key, false, false, null, error)));
            return results;
        }

        string work = Path.Combine(Path.GetTempPath(), "ose-mapshot-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        try
        {
            string request = Path.Combine(work, "request.json");
            await File.WriteAllTextAsync(request, Request(project, compiledFolder, work, todo).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancel);
            progress?.Invoke($"rendering {todo.Count} picture(s) with {Path.GetFileName(exe)}");
            int exit;
            string log;
            try
            {
                (exit, log) = await RunAsync(exe, compiledFolder, request, work, progress, cancel);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                results.AddRange(todo.Select(shot => new MapShotResult(shot.Slot, shot.Key, false, false, null, ex.Message)));
                return results;
            }

            Directory.CreateDirectory(CacheFolderOf(projectFolder));
            foreach (CameraShot shot in todo)
            {
                string made = Path.Combine(work, shot.Key + ".png");
                if (File.Exists(made))
                {
                    string cached = CachePath(projectFolder, shot.Key);
                    File.Copy(made, cached, overwrite: true);
                    await File.WriteAllTextAsync(MapStampPath(projectFolder, shot.Key), mapKey, cancel);
                    results.Add(new MapShotResult(shot.Slot, shot.Key, true, false, cached, null));
                }
                else
                {
                    results.Add(new MapShotResult(shot.Slot, shot.Key, false, false, null, $"no picture (exit code {exit}){LastLine(log)}"));
                }
            }
        }
        finally
        {
            DeleteWork(work, progress);
        }

        return results;
    }

    /// <summary>The <c>--svm-mapshot</c> request: the golden camera format without pawns, one shot per key.</summary>
    private static JsonObject Request(MapProject project, string compiledFolder, string outFolder, IEnumerable<CameraShot> shots)
    {
        var list = new JsonObject();
        foreach (CameraShot shot in shots)
        {
            list[shot.Key] = new JsonObject
            {
                ["width"] = shot.Width,
                ["height"] = shot.Height,
                ["look"] = shot.Recipe.Look,
                ["camera"] = new JsonObject
                {
                    ["location"] = new JsonArray(shot.Bookmark.Location.X, shot.Bookmark.Location.Y, shot.Bookmark.Location.Z),
                    ["rotation"] = new JsonArray(shot.Bookmark.Rotation.Pitch, shot.Bookmark.Rotation.Yaw, shot.Bookmark.Rotation.Roll),
                    ["fov"] = shot.Recipe.Fov,
                },
            };
        }

        return new JsonObject
        {
            ["format"] = "svm.golden-cameras/1",
            ["map"] = project.Id,
            ["folder"] = Path.GetFullPath(compiledFolder),
            ["out"] = outFolder,
            ["mode"] = project.MapSettings.ContainsKey("Story") ? "Story" : project.MapSettings.Keys.FirstOrDefault() ?? "Story",
            ["shots"] = list,
        };
    }

    private static async Task<(int Exit, string Log)> RunAsync(string exe, string compiledFolder, string request, string work,
        Action<string>? progress, CancellationToken cancel)
    {
        string game = BackgroundGame.Create(exe, Path.Combine(work, "game"), progress);
        var start = new ProcessStartInfo(game)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(game)!,
        };
        start.ArgumentList.Add("--position");
        start.ArgumentList.Add("-32000,-32000");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("--svm-background");
        start.ArgumentList.Add("--svm-maps=" + Path.GetDirectoryName(Path.GetFullPath(compiledFolder)));
        start.ArgumentList.Add(GameExe.MapShotOption + request);

        // What the game would find beside its own exe, which the copy lacks.
        if (GameExe.ContentOf(exe) is { } content)
        {
            start.ArgumentList.Add("--svm-content=" + content);
        }

        string builtIn = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "maps");
        if (Directory.Exists(builtIn))
        {
            start.ArgumentList.Add("--svm-maps=" + builtIn);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{game} did not start");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancel);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            // Its files in the work folder can only be deleted once it has gone.
            process.WaitForExit(TimeSpan.FromSeconds(30));
            cancel.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(game)} did not finish the pictures in {Timeout.TotalMinutes:0} minutes");
        }

        return (process.ExitCode, await output + await errors);
    }

    /// <summary>Removes the work folder; a game still shutting down may hold its files, which then stay in the temp
    /// folder rather than hide the render's result.</summary>
    private static void DeleteWork(string work, Action<string>? progress)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress?.Invoke($"could not remove {work}: {ex.Message}");
        }
    }

    /// <summary>The game's last SVM_MAPSHOT line or error, for the report.</summary>
    private static string LastLine(string log)
    {
        string? line = log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("SVM_MAPSHOT", StringComparison.Ordinal) || l.Contains("ERROR", StringComparison.Ordinal));
        return line is null ? "" : ": " + line;
    }
}
