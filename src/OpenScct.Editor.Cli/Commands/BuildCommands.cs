using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose build</c>, <c>ose validate</c> and <c>ose play</c>: compile a project to an SVMap folder, check a
/// folder with OpenSCCT's loader, and play a project in OpenSCCT.</summary>
public static class BuildCommands
{
    private static readonly JsonSerializerOptions s_compact = new() { WriteIndented = false };

    [CliProvider]
    public static IEnumerable<CliCommand> Commands() =>
    [
        new("build", "build <project.osmap> [--out <folder>] [--target openscct|original] [--fast] [--classes] [--no-validate] [--content <dir>] [--verbose]",
            "Compiles the project to an SVMap 1.2 folder (default <project>/../build/<MapId>) and prints a JSON result. --target original also " +
            "checks the stock engine's limits and writes the original-format export (<MapId>.t3d next to the map folder) when an exporter is installed.",
            BuildAsync),
        new("validate", "validate <svmap-folder>",
            "Loads a map folder with OpenSCCT's strict loader, checks its manifest and render files; prints a JSON result.", ValidateAsync),
        new("play", "play <project.osmap> [--side spy|merc] [--mode Story] [--bots N] [--players N] [--difficulty easy|normal|hard|expert] [--rules playlevel|map] [--at camera|<bookmark>|X,Y,Z[,Yaw[,Pitch]]] [--out <folder>] [--exe <OpenSCCT.exe>] [--dry-run] [--headless [--seconds N] [--output] [--route x,y,z;id;... [--from <start id>]] [--client]]",
            "Builds the project, then starts OpenSCCT on it (--svm-maps, --svm-play). --players lays the map out for that many players " +
            "(default: the map's Play setting; 0 counts the bots and you). --at spawns you there once instead of at a start: camera (the 3D view's " +
            "camera saved with the map), a bookmark's name or number, or a spot in Unreal units with yaw and pitch in rotation units; you " +
            "start on the floor under it, or under where it looks when it is outside the level (offline only: not with --route or --client). " +
            "--dry-run prints the command instead. --headless runs " +
            "the round with no window and reports whether it loaded, the errors and warnings the game printed, where you spawned, the map's starts and " +
            "where each bot stood (bots default to the map's Play setting, else 2, at most 15; the run ends once every bot has reported, or after --seconds, 40). " +
            "--route has a route bot of --side walk those stops in order (floor points, or objects by id, reached where the game's use " +
            "test lets it use them) with the side's own navigation and door handling, while you stand by on the other side; the report " +
            "says each stop reached or where it got stuck (no other bots unless --bots; --from starts it at that player start; --seconds " +
            "defaults to 30 plus 40 a stop). Lifts are not ridden. --client plays the round online instead (no bots, no route): this game " +
            "hosts it with the lobby's rules for --mode and a second headless game joins it on the other side; the report says what each " +
            "loaded and which pawns each one's world held, and the client fails when its world does not match the host's. Exit code 1 " +
            "when the round did not load, printed errors, the route was not finished or a game of the pair failed.", PlayAsync),
    ];

    private static Task<int> BuildAsync(CliArgs a)
    {
        (MapProject project, CompileResult result) = Compile(a);
        JsonObject json = ResultJson(result, project.Id);
        bool ok = result.Ok;
        if (result.Ok && TargetOf(a) == TargetGame.Original)
        {
            ok = ExportOriginal(project, result, a, json);
            json["ok"] = ok;
        }

        Console.WriteLine(json.ToJsonString(s_compact));
        return Task.FromResult(ok ? 0 : 1);
    }

    /// <summary>Writes the original-format export next to the map folder with the installed exporter for the original
    /// game; adds its files and findings to the result.</summary>
    private static bool ExportOriginal(MapProject project, CompileResult result, CliArgs a, JsonObject json)
    {
        // The T3D export, as the help says: the folder export (export --format original) would land on the build output,
        // and the .sdc export is its own step (export --format sdc).
        IMapExporter? exporter = EditorServices.Exporters.FirstOrDefault(e => e.Target == TargetGame.Original && e.Name == "t3d")
            ?? EditorServices.Exporters.FirstOrDefault(e => e.Target == TargetGame.Original && !e.WritesFolder);
        if (exporter is null)
        {
            json["export"] = new JsonObject { ["ok"] = false, ["error"] = "no exporter for the original game is installed" };
            return false;
        }

        string path = Path.Combine(Path.GetDirectoryName(result.OutputFolder) ?? ".", project.Id + exporter.Extension);
        ExportResult export = exporter.Export(project, path, new ExportOptions
        {
            ContentRoot = a.Get("content") ?? EditorServices.ContentRoot,
            ProjectFolder = a.Positional(0) is { } folder ? Path.GetFullPath(folder) : null,
        });
        json["export"] = new JsonObject
        {
            ["ok"] = export.Ok,
            ["exporter"] = exporter.Name,
            ["files"] = new JsonArray(export.Files.Select(f => (JsonNode)f).ToArray()),
            ["findings"] = Findings(export.Findings),
        };
        return export.Ok;
    }

    private static TargetGame TargetOf(CliArgs a) => (a.Get("target") ?? "openscct").ToLowerInvariant() switch
    {
        "openscct" => TargetGame.OpenScct,
        "original" => TargetGame.Original,
        var t => throw new ArgumentException($"unknown target '{t}' (openscct or original)"),
    };

    private static Task<int> ValidateAsync(CliArgs a)
    {
        string folder = Path.GetFullPath(a.Positional(0) ?? throw new ArgumentException("give the map folder"));
        (List<Finding> findings, SortedDictionary<string, long> stats) = MapFolder.Validate(folder);
        bool ok = findings.All(f => f.Severity != Severity.Error);
        var json = new JsonObject
        {
            ["ok"] = ok,
            ["folder"] = folder,
            ["findings"] = Findings(findings),
            ["stats"] = Stats(stats),
        };
        Console.WriteLine(json.ToJsonString(s_compact));
        return Task.FromResult(ok ? 0 : 1);
    }

    private static Task<int> PlayAsync(CliArgs a)
    {
        (MapProject project, CompileResult result) = Compile(a);
        if (!result.Ok)
        {
            Console.WriteLine(ResultJson(result, project.Id).ToJsonString(s_compact));
            return Task.FromResult(1);
        }

        string? exe = GameExe.Find(a.Get("exe"));
        if (exe is null)
        {
            Console.Error.WriteLine("ose play: OpenSCCT.exe not found; give --exe, set OSE_GAME_EXE (or OPENSCCT_EXE), or install OpenSCCT with its launcher");
            return Task.FromResult(1);
        }

        if (GameExe.TooOld(exe) is { } tooOld)
        {
            Console.Error.WriteLine("ose play: " + tooOld);
            return Task.FromResult(1);
        }

        PlayOptions play = project.Workspace.Play;
        PlayStart? startAt = a.Get("at") is { } at
            ? ActorPlacement.OnFloor(new WorldGeometry(project, Path.GetFullPath(a.Positional(0)!)), PlayStart.Parse(at, project.Workspace))
            : null;
        if (a.Has("headless"))
        {
            return Task.FromResult(PlayHeadless(a, project, result, exe, play, startAt));
        }

        var args = new List<string>
        {
            "--",
            $"--svm-maps={Path.GetDirectoryName(result.OutputFolder)}",
            $"--svm-play={project.Id}",
            $"--side={a.Get("side") ?? play.Side}",
            $"--mode={a.Get("mode") ?? play.Mode}",
            $"--rules={a.Get("rules") ?? play.Rules}",
            $"--bots={a.GetInt("bots", play.Bots)}",
            $"--difficulty={a.Get("difficulty") ?? play.Difficulty}",
        };
        // Without it the game counts the playtest's own players (its bots and you).
        if (a.GetInt("players", play.Players) is > 0 and var players)
        {
            args.Add($"--players={players}");
        }

        if (GameExe.LauncherContent(exe) is { } content)
        {
            args.Add($"--svm-content={content}");
        }

        if (startAt is { } spot)
        {
            args.Add(spot.Argument);
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        var json = ResultJson(result, project.Id);
        json["exe"] = exe;
        json["args"] = new JsonArray(args.Select(s => (JsonNode)s).ToArray());
        if (a.Has("dry-run"))
        {
            json["started"] = false;
            Console.WriteLine(json.ToJsonString(s_compact));
            return Task.FromResult(0);
        }

        using Process? p = Process.Start(start);
        json["started"] = p is not null;
        json["pid"] = p?.Id;
        Console.WriteLine(json.ToJsonString(s_compact));
        return Task.FromResult(p is null ? 1 : 0);
    }

    /// <summary>A round with no window: the report of what the game printed (see <see cref="HeadlessPlay"/>).</summary>
    private static int PlayHeadless(CliArgs a, MapProject project, CompileResult result, string exe, PlayOptions play, PlayStart? startAt)
    {
        List<string>? route = a.Get("route") is { } text ? RouteStops(project, text) : null;
        bool client = a.Has("client");
        if (client && (route is not null || a.Has("from") || a.GetInt("bots", 0) > 0))
        {
            throw new ArgumentException("--client plays an online round with no bots and no route: leave out --route, --from and --bots");
        }

        // The online lobby sets its own rules for the mode and seats the two players; nothing would take these.
        if (client && new[] { "rules", "difficulty", "players" }.FirstOrDefault(a.Has) is { } unused)
        {
            throw new ArgumentException($"--client plays the online lobby's rules for --mode with two players: leave out --{unused}");
        }

        // The test start is an offline round's, and a route bot starts at its side's start (the game would move the
        // player standing by on the other side instead).
        if (startAt is not null && (client || route is not null))
        {
            throw new ArgumentException($"--at starts an offline round where you play: leave it out with --{(client ? "client" : "route")}");
        }

        string? from = a.Get("from");
        if (from is not null && project.FindActor(from) is null)
        {
            throw new ArgumentException($"--from: the map has no {from}");
        }

        // A route run, or an online one, has no other bots unless asked for.
        bool plain = route is null && !client;
        var options = new HeadlessPlayOptions
        {
            Exe = exe,
            MapFolder = result.OutputFolder,
            MapId = project.Id,
            Side = a.Get("side") ?? play.Side,
            Mode = a.Get("mode") ?? play.Mode,
            Rules = a.Get("rules") ?? play.Rules,
            Bots = a.GetInt("bots", plain ? HeadlessPlay.DefaultBots(play) : 0),
            Difficulty = a.Get("difficulty") ?? play.Difficulty,
            Players = a.GetInt("players", HeadlessPlay.DefaultPlayers(play, botsGiven: a.Has("bots") || !plain)),
            Content = GameExe.LauncherContent(exe),
            Seconds = a.Get("seconds") is { } seconds ? double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)
                : route is not null ? HeadlessPlay.RouteSeconds(route.Count) : 40,
            StartAt = startAt,
            Route = route,
            RouteFrom = from,
            Client = client,
        };
        if (a.Has("dry-run"))
        {
            var json = ResultJson(result, project.Id);
            json["exe"] = HeadlessPlay.ConsoleExe(exe);
            if (client)
            {
                (List<string> host, List<string> joiner) = HeadlessPlay.NetArguments(options, 0);
                json["args"] = new JsonArray(host.Select(s => (JsonNode)s).ToArray());
                json["clientArgs"] = new JsonArray(joiner.Select(s => (JsonNode)s).ToArray());
                json["port"] = "a free UDP port picked at the start (0 here)";
            }
            else
            {
                json["args"] = new JsonArray(HeadlessPlay.Arguments(options).Select(s => (JsonNode)s).ToArray());
            }

            json["started"] = false;
            Console.WriteLine(json.ToJsonString(s_compact));
            return 0;
        }

        Console.Error.WriteLine($"ose play: running {project.Id} headless (up to {options.Seconds:0} s)");
        HeadlessPlayReport report = HeadlessPlay.Run(options, a.Has("verbose") ? line => Console.Error.WriteLine($"  {line}") : null);
        Console.WriteLine(CanonicalJson.ToCanonicalString(HeadlessPlay.ToJson(report, a.Has("output"))));
        return report.Ok ? 0 : 1;
    }

    /// <summary>Route stops as <c>x,y,z;Id;...</c>: a floor point, or an actor of the map by id (the game finds where to stand).</summary>
    private static List<string> RouteStops(MapProject project, string text) =>
        [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p =>
            ApiJson.TryParseTriple(p, out Vec3 v) ? HeadlessPlay.RouteStop(v)
            : project.FindActor(p) is not null ? p : throw new ArgumentException($"route stop '{p}' is neither x,y,z nor an actor of the map"))];

    internal static (MapProject Project, CompileResult Result) Compile(CliArgs a)
    {
        string path = a.Positional(0) ?? throw new ArgumentException("give the project (.osmap folder)");
        EditorServices.Discover();
        MapProject project = ProjectStore.Load(path);
        foreach (string note in project.LoadNotes)
        {
            Console.Error.WriteLine($"ose: {note}");
        }

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('/', '\\')) ?? ".";
        string output = a.Get("out") ?? Path.Combine(projectDir, "build", project.Id);
        TargetGame target = TargetOf(a);
        var options = new CompileOptions
        {
            Validate = !a.Has("no-validate"),
            FastLighting = a.Has("fast"),
            ContentRoot = a.Get("content") ?? EditorServices.ContentRoot,
            ProjectFolder = Path.GetFullPath(path),
            Target = target,
            WriteClassTable = a.Has("classes"),
            Progress = a.Has("verbose") ? line => Console.Error.WriteLine($"ose build: {line}") : null,
        };
        IMapCompiler compiler = EditorServices.Compiler ?? new MapCompiler();
        return (project, compiler.Compile(project, output, options));
    }

    internal static JsonObject ResultJson(CompileResult r, string map) => new()
    {
        ["ok"] = r.Ok,
        ["map"] = map,
        ["out"] = r.OutputFolder,
        ["elapsedMs"] = (long)r.Elapsed.TotalMilliseconds,
        ["errors"] = r.Findings.Count(f => f.Severity == Severity.Error),
        ["warnings"] = r.Findings.Count(f => f.Severity == Severity.Warning),
        ["findings"] = Findings(r.Findings),
        ["stats"] = Stats(r.Stats),
    };

    private static JsonArray Findings(IEnumerable<Finding> findings) => new(findings.Select(f => (JsonNode)new JsonObject
    {
        ["severity"] = f.Severity.ToString().ToLowerInvariant(),
        ["code"] = f.Code,
        ["message"] = f.Message,
        ["objects"] = new JsonArray(f.Objects.Select(o => (JsonNode)o.ToString()).ToArray()),
    }).ToArray());

    private static JsonObject Stats(IEnumerable<KeyValuePair<string, long>> stats)
    {
        var o = new JsonObject();
        foreach ((string k, long v) in stats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            o[k] = v;
        }

        return o;
    }
}
