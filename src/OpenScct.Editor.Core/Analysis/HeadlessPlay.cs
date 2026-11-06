using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>What a headless play run is given: the compiled map, the game and the round to start.</summary>
public sealed record HeadlessPlayOptions
{
    /// <summary>OpenSCCT.exe (its OpenSCCT.console.exe twin is run when it is there, for the console output).</summary>
    public required string Exe { get; init; }

    /// <summary>The compiled SVMap folder (its parent is passed as <c>--svm-maps</c>).</summary>
    public required string MapFolder { get; init; }

    public required string MapId { get; init; }

    /// <summary>The local player's side; with a <see cref="Route"/>, the route bot's (you stand by on the other one).</summary>
    public string Side { get; init; } = "spy";

    public string Mode { get; init; } = "Story";

    public string Rules { get; init; } = "playlevel";

    /// <summary>Server bots; their status lines are where positions come from.</summary>
    public int Bots { get; init; } = 2;

    public string? Difficulty { get; init; }

    /// <summary>The player count the round is laid out for (the map's match-size rules); 0 lets the game count its own
    /// players (the bots and you).</summary>
    public int Players { get; init; }

    /// <summary>The launcher's content folder, when the exe is a launcher install.</summary>
    public string? Content { get; init; }

    /// <summary>Where the player spawns instead of a team start (the game's <c>--start-at</c>), or null. An offline
    /// round's only: not with a <see cref="Route"/> or a <see cref="Client"/>.</summary>
    public PlayStart? StartAt { get; init; }

    /// <summary>The longest the run lasts. It ends sooner once every bot has reported (after 10 s of play), the route
    /// is walked, or the client has seen the round.</summary>
    public double Seconds { get; init; } = 40;

    /// <summary>
    /// Stops a route bot walks in order on <see cref="Side"/>, reporting each one reached or where it got stuck (the
    /// game's <c>--svm-route</c>): floor points (<see cref="RouteStop"/>) or placed actors by id, an actor with a Use
    /// entry reached where the game's use test passes. Null for none.
    /// </summary>
    public IReadOnlyList<string>? Route { get; init; }

    /// <summary>The player start of <see cref="Side"/> the route bot starts at, or null for any of the side's.</summary>
    public string? RouteFrom { get; init; }

    /// <summary>
    /// Plays the round online instead: this game hosts it (loopback only, on <see cref="Side"/>, with the lobby's rules
    /// for <see cref="Mode"/>) and a second headless game joins it on the other side, and the report says what each one
    /// loaded and saw. No bots and no route.
    /// </summary>
    public bool Client { get; init; }
}

/// <summary>What one game of a host and client run showed: its role, whether it loaded the round, its errors and the
/// pawns its own world held last (the client's are those the host sent it).</summary>
public sealed record HeadlessNetPeer(string Role, bool Loaded, IReadOnlyList<string> Errors, IReadOnlyList<string> Lobby, string? Pawns, int PawnCount)
{
    public bool Ok => Loaded && Errors.Count == 0;
}

/// <summary>One bot's first status line: its name, side, what it was doing and where it stood.</summary>
public sealed record HeadlessBot(string Name, string Side, string Status, Vec3? At, string? NearestStart, double? StartDistance);

/// <summary>Where the local player's first pawn stood when the client first saw it, with the nearest start of its side.</summary>
public sealed record HeadlessSpawn(string Side, Vec3 At, int Yaw, string? NearestStart, double? StartDistance);

/// <summary>What a headless play run showed.</summary>
public sealed record HeadlessPlayReport
{
    public required bool Started { get; init; }

    /// <summary>True when the round started (no load error, and the game reached play).</summary>
    public required bool Loaded { get; init; }

    /// <summary>The game's own load error, when it could not start play on the map.</summary>
    public string? LoadError { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>The render warning count and the first one, when the game reported any.</summary>
    public string? RenderWarnings { get; init; }

    public required IReadOnlyList<HeadlessBot> Bots { get; init; }

    /// <summary>The local player's first spawn, or null when the game printed none (no pawn, or a game older than the
    /// test start that does not print it).</summary>
    public HeadlessSpawn? Player { get; init; }

    /// <summary>The start the run asked for (<see cref="HeadlessPlayOptions.StartAt"/>), or null.</summary>
    public PlayStart? StartAt { get; init; }

    /// <summary>The map's enabled player starts per side, as the server loads them.</summary>
    public required IReadOnlyList<(string Id, string Side, Vec3 At)> Starts { get; init; }

    /// <summary>The game's exit code, or null when ose ended the run at the time limit.</summary>
    public int? ExitCode { get; init; }

    public required double Seconds { get; init; }

    public required IReadOnlyList<string> Args { get; init; }

    /// <summary>The route's report lines in order (start, each point reached, where it got stuck, done), when one was walked.</summary>
    public IReadOnlyList<string> Route { get; init; } = [];

    /// <summary>The stops of the route asked for (0 for none) and those reached.</summary>
    public int RouteStops { get; init; }

    public int RouteReached { get; init; }

    /// <summary>Why the route was not finished: the game's line saying where the walker got stuck, or that the run
    /// ended first. Null when every stop was reached (or there was no route).</summary>
    public string? RouteStuck { get; init; }

    /// <summary>The host and the client of an online run, in that order.</summary>
    public IReadOnlyList<HeadlessNetPeer> Peers { get; init; } = [];

    /// <summary>Every line the game printed (for --verbose and the log).</summary>
    public required IReadOnlyList<string> Output { get; init; }

    public bool Ok => Started && Loaded && Errors.Count == 0 && RouteStuck is null && RouteReached == RouteStops && Peers.All(p => p.Ok);
}

/// <summary>
/// <c>ose play --headless</c>: starts OpenSCCT with no window (<c>--headless</c>) on a compiled map, with the server
/// running inline so it prints its bots' status every 10 s of play, then reads the console: whether the round
/// started, the errors and warnings it printed, where the local player spawned and where each bot stood, with the map's
/// starts (as the server loads them) and the start nearest each.
/// With a route the local player walks it and the report says how far it got; with a client the round is played
/// online by two headless games, a host and a client that joins it.
/// </summary>
public static partial class HeadlessPlay
{
    /// <summary>The most server bots the game starts (its LaunchOptions.MaxBots); it ignores a larger --bots.</summary>
    public const int MaxBots = 15;

    /// <summary>The bots of a test round when none are asked for: the map's Play setting, else 2 (one per side), since
    /// bots' status lines are where the report's positions come from.</summary>
    public static int DefaultBots(PlayOptions play) => play.Bots > 0 ? Math.Min(play.Bots, MaxBots) : 2;

    /// <summary>
    /// The player count a test round lays the map out for when none is asked for: the map's Play setting, else the
    /// count the Match panel and the <c>match</c> query preview (<see cref="PlayOptions.MatchPlayers"/>: the Play
    /// bots and you), so the extra bots <see cref="DefaultBots"/> adds for the report do not change the layout. When
    /// the caller picks the bots, 0: the game counts those bots and you.
    /// </summary>
    public static int DefaultPlayers(PlayOptions play, bool botsGiven) => play.Players > 0 || botsGiven ? play.Players : play.MatchPlayers;

    /// <summary>The game's arguments for a headless round of <paramref name="o"/>.</summary>
    public static List<string> Arguments(HeadlessPlayOptions o)
    {
        if (o.Bots is < 0 or > MaxBots)
        {
            throw new ArgumentException($"bots is 0 to {MaxBots}, not {o.Bots}");
        }

        var args = new List<string>
        {
            "--headless",
            "--",
            $"--svm-maps={Path.GetDirectoryName(Path.GetFullPath(o.MapFolder).TrimEnd('/', '\\'))}",
            $"--svm-play={o.MapId}",
            $"--side={o.Side}",
            $"--mode={o.Mode}",
            $"--rules={o.Rules}",
            $"--bots={o.Bots}",
            "--svm-host=inline",
            // Default settings, signed out, nothing of the player's written (route and client rounds are so anyway).
            "--svm-test-round",
        };
        if (o.Difficulty is { } difficulty)
        {
            args.Add($"--difficulty={difficulty}");
        }

        if (o.Players > 0)
        {
            args.Add($"--players={o.Players}");
        }

        if (o.Content is { } content)
        {
            args.Add($"--svm-content={content}");
        }

        if (o.StartAt is { } at)
        {
            if (o.Route is { Count: > 0 })
            {
                throw new ArgumentException("a route starts at its side's start (or its from), so it takes no start point");
            }

            args.Add(at.Argument);
        }

        if (o.Route is { Count: > 0 } route)
        {
            if (route.FirstOrDefault(s => s.Length == 0 || s.Contains(';', StringComparison.Ordinal)) is { } bad)
            {
                throw new ArgumentException($"route stop '{bad}' is empty or holds a ';'");
            }

            args.Add("--svm-route=" + string.Join(";", route));
            if (o.RouteFrom is { } from)
            {
                args.Add($"--svm-route-from={from}");
            }
        }

        return args;
    }

    /// <summary>A route stop for floor point <paramref name="p"/> (<c>x,y,z</c>).</summary>
    public static string RouteStop(Vec3 p) => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.#},{p.Y:0.#},{p.Z:0.#}");

    /// <summary>The time limit of a route run when none is given: the load and half a minute or so per stop.</summary>
    public static double RouteSeconds(int stops) => 30 + (40 * stops);

    /// <summary>The players of an online run: the host and the client.</summary>
    private const int NetPlayers = 2;

    /// <summary>The host's and the client's arguments for an online run of <paramref name="o"/> on UDP <paramref name="port"/>.</summary>
    public static (List<string> Host, List<string> Client) NetArguments(HeadlessPlayOptions o, int port)
    {
        if (o.Route is { Count: > 0 } || o.Bots > 0)
        {
            throw new ArgumentException("an online round with a client has no bots and walks no route");
        }

        if (o.StartAt is not null)
        {
            throw new ArgumentException("the test start is for offline rounds: an online round with a client takes no start point");
        }

        string maps = $"--svm-maps={Path.GetDirectoryName(Path.GetFullPath(o.MapFolder).TrimEnd('/', '\\'))}";
        string other = o.Side == "merc" ? "spy" : "merc";
        var host = new List<string>
        {
            "--headless", "--", maps, $"--svm-play={o.MapId}", $"--mode={o.Mode}", $"--side={o.Side}", $"--svm-lan-host={port}", $"--players={NetPlayers}",
        };
        var client = new List<string> { "--headless", "--", maps, $"--side={other}", $"--svm-join=127.0.0.1:{port}" };
        if (o.Content is { } content)
        {
            host.Add($"--svm-content={content}");
            client.Add($"--svm-content={content}");
        }

        return (host, client);
    }

    /// <summary>The console build next to a game exe (Godot's <c>X.console.exe</c>), else the exe itself.</summary>
    public static string ConsoleExe(string exe)
    {
        string console = Path.Combine(Path.GetDirectoryName(exe) ?? ".", Path.GetFileNameWithoutExtension(exe) + ".console.exe");
        return exe.EndsWith(".console.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(console) ? exe : console;
    }

    /// <summary>Runs the round and reads what the game printed. <paramref name="line"/> sees each line as it comes.</summary>
    public static HeadlessPlayReport Run(HeadlessPlayOptions o, Action<string>? line = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        List<(string, string, Vec3)> starts = Starts(o.MapFolder);
        if (o.Client)
        {
            return RunNet(o, starts, line, cancel);
        }

        List<string> args = Arguments(o);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        bool routed = o.Route is { Count: > 0 };

        // Done once the route ends, else once every bot has reported.
        bool Done(string text) => routed
            ? text.StartsWith(RouteDone, StringComparison.Ordinal)
            : o.Bots > 0 && BotLine().Match(text) is { Success: true } m && reported.Add(m.Groups["name"].Value) && reported.Count >= o.Bots;

        var clock = Stopwatch.StartNew();
        using var game = Game.Start(ConsoleExe(o.Exe), args, Done, line);
        if (game is null)
        {
            return new HeadlessPlayReport
            {
                Started = false, Loaded = false, Errors = [$"could not start {ConsoleExe(o.Exe)}"], Warnings = [], Bots = [], Starts = starts,
                StartAt = o.StartAt, Seconds = 0, Args = args, Output = [],
            };
        }

        TimeSpan limit = TimeSpan.FromSeconds(Math.Max(5, o.Seconds));
        while (clock.Elapsed < limit && !cancel.IsCancellationRequested && !game.Exited && !game.Finished)
        {
            Thread.Sleep(250);
        }

        int? exitCode = game.Stop();
        return Read(game.Lines(), starts, exitCode, clock.Elapsed.TotalSeconds, args, o.Route?.Count ?? 0) with { StartAt = o.StartAt };
    }

    /// <summary>
    /// The online run: the host game starts first; once it prints that it is hosting, the client game joins. The run
    /// ends when both have loaded the round and each has reported the pawns its world holds twice (once at load, once
    /// 10 s in), or at the time limit. The round counts as loaded when the host says it loaded it, and the client
    /// passes when its world holds the host's pawns where the host has them.
    /// </summary>
    private static HeadlessPlayReport RunNet(HeadlessPlayOptions o, List<(string, string, Vec3)> starts, Action<string>? line, CancellationToken cancel)
    {
        int port = FreeUdpPort();
        (List<string> hostArgs, List<string> clientArgs) = NetArguments(o, port);
        string exe = ConsoleExe(o.Exe);
        var clock = Stopwatch.StartNew();
        using var host = Game.Start(exe, hostArgs, _ => false, text => line?.Invoke("host: " + text));
        if (host is null)
        {
            return new HeadlessPlayReport
            {
                Started = false, Loaded = false, Errors = [$"could not start {exe}"], Warnings = [], Bots = [], Starts = starts,
                Seconds = 0, Args = hostArgs, Output = [],
            };
        }

        TimeSpan limit = TimeSpan.FromSeconds(Math.Max(5, o.Seconds));
        Game? client = null;
        try
        {
            while (clock.Elapsed < limit && !cancel.IsCancellationRequested && !host.Exited)
            {
                Thread.Sleep(250);
                if (client is null && host.Lines().Any(l => l.TrimStart().StartsWith(NetPrefix + " hosting", StringComparison.Ordinal)))
                {
                    client = Game.Start(exe, clientArgs, _ => false, text => line?.Invoke("client: " + text));
                    if (client is null)
                    {
                        break;
                    }
                }

                if (client is { } c && (c.Exited || (PawnReports(c.Lines()) >= 2 && PawnReports(host.Lines()) >= 2)))
                {
                    break;
                }
            }

            int? hostExit = host.Stop();
            int? clientExit = client?.Stop();
            List<string> hostLines = host.Lines(), clientLines = client?.Lines() ?? [];
            HeadlessPlayReport report = Read(hostLines, starts, hostExit, clock.Elapsed.TotalSeconds, hostArgs, 0);
            HeadlessNetPeer hostPeer = Peer("host", hostLines, hostExit);
            HeadlessNetPeer clientPeer = client is null
                ? new HeadlessNetPeer("client", false, [host.Exited ? "the host stopped before it was hosting" : "the host never said it was hosting, so the client did not start"], [], null, 0)
                : Peer("client", clientLines, clientExit);
            if (hostPeer.Loaded && clientPeer.Loaded)
            {
                clientPeer = clientPeer with { Errors = [.. clientPeer.Errors, .. Mismatches(hostPeer, clientPeer)] };
            }

            return report with
            {
                // An online round's load is the host's SVM_NET line: the offline load lines are not printed.
                Loaded = report.LoadError is null && hostPeer.Loaded,
                Peers = [hostPeer, clientPeer],
                Output = [.. hostLines.Select(l => "host: " + l), .. clientLines.Select(l => "client: " + l)],
            };
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>One game of an online run, read from its <c>SVM_NET</c> lines and errors.</summary>
    private static HeadlessNetPeer Peer(string role, IReadOnlyList<string> lines, int? exitCode)
    {
        var errors = new List<string>();
        var lobby = new List<string>();
        bool loaded = false;
        string? pawns = null;
        int count = 0;
        foreach (string raw in lines)
        {
            string text = raw.Trim();
            if (text.StartsWith(NetPrefix + " ", StringComparison.Ordinal))
            {
                string rest = text[(NetPrefix.Length + 1)..];
                loaded |= rest.StartsWith("loaded", StringComparison.Ordinal);
                if (rest.StartsWith("lobby:", StringComparison.Ordinal) || rest.StartsWith("could not", StringComparison.Ordinal) || rest.StartsWith("state Failed", StringComparison.Ordinal))
                {
                    (rest.StartsWith("lobby:", StringComparison.Ordinal) ? lobby : errors).Add(rest);
                }
                else if (PawnsLine().Match(rest) is { Success: true } m)
                {
                    count = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                    pawns = m.Groups["list"].Value;
                }
            }
            else if (text.StartsWith("ERROR:", StringComparison.Ordinal) || text.StartsWith("SCRIPT ERROR:", StringComparison.Ordinal) ||
                text.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Message(text));
            }
        }

        if (exitCode is { } code and not 0)
        {
            errors.Add($"the game exited with code {code}");
        }

        return new HeadlessNetPeer(role, loaded, errors, lobby, pawns, count);
    }

    /// <summary>The pawn reports an online game printed.</summary>
    private static int PawnReports(IEnumerable<string> lines) => lines.Count(l => l.TrimStart().StartsWith(NetPrefix + " pawns", StringComparison.Ordinal));

    /// <summary>
    /// Where the client's world differs from the host's at their last reports: a pawn count other than the players',
    /// and each host pawn the client lacks or holds more than <see cref="NetPlaceSlack"/> away (the players stand still
    /// in these rounds, so both worlds should agree).
    /// </summary>
    private static List<string> Mismatches(HeadlessNetPeer host, HeadlessNetPeer client)
    {
        var problems = new List<string>();
        if (host.PawnCount != NetPlayers)
        {
            problems.Add($"the host's world held {host.PawnCount} pawns, not the {NetPlayers} players");
        }

        if (client.PawnCount != host.PawnCount)
        {
            problems.Add($"the client's world held {client.PawnCount} pawns, the host's {host.PawnCount}");
        }

        List<(string Who, Vec3 At)> theirs = Pawns(client.Pawns);
        foreach ((string who, Vec3 at) in Pawns(host.Pawns))
        {
            if (!theirs.Any(p => p.Who == who && (p.At - at).Length <= NetPlaceSlack))
            {
                problems.Add($"the client does not hold {who} where the host has it, at {RouteStop(at)}");
            }
        }

        return problems;
    }

    /// <summary>How far apart the host's and the client's copies of a standing pawn may be.</summary>
    private const double NetPlaceSlack = 64;

    /// <summary>"Name (side) at (x,y,z); ..." as (name and side, place).</summary>
    private static List<(string Who, Vec3 At)> Pawns(string? list) =>
        list is null ? [] : [.. PawnEntry().Matches(list).Select(m => (m.Groups["who"].Value.Trim(), ParseVec(m.Groups["at"].Value) ?? Vec3.Zero))];

    /// <summary>A UDP port nothing is bound to now (the OS picks one).</summary>
    private static int FreeUdpPort()
    {
        using var socket = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private const string RoutePrefix = "SVM_ROUTE";
    private const string RouteDone = RoutePrefix + " done";
    private const string NetPrefix = "SVM_NET";

    /// <summary>A running game with its console lines collected; stopped (killed with its children) when disposed.</summary>
    private sealed class Game : IDisposable
    {
        private readonly Process _process;
        private readonly List<string> _lines = [];
        private readonly Func<string, bool> _finishes;
        private readonly Action<string>? _line;
        private volatile bool _finished;

        private Game(Process process, Func<string, bool> finishes, Action<string>? line)
        {
            _process = process;
            _finishes = finishes;
            _line = line;
        }

        /// <summary>A line <c>finishes</c> accepted has come: the caller may stop the game.</summary>
        public bool Finished => _finished;

        public bool Exited => _process.HasExited;

        public static Game? Start(string exe, IReadOnlyList<string> args, Func<string, bool> finishes, Action<string>? line)
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            // The native OpenAL backend still opens audio with Godot's --headless/Dummy driver.
            start.Environment["SVM_AUDIO_MUTE"] = "1";
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            if (Process.Start(start) is not { } process)
            {
                return null;
            }

            var game = new Game(process, finishes, line);
            process.OutputDataReceived += (_, e) => game.OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => game.OnLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return game;
        }

        public List<string> Lines()
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }

        /// <summary>The exit code when the game ended on its own; else it is killed and null.</summary>
        public int? Stop()
        {
            if (_process.HasExited)
            {
                _process.WaitForExit();
                return _process.ExitCode;
            }

            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            return null;
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }

            _process.Dispose();
        }

        private void OnLine(string? text)
        {
            if (text is null)
            {
                return;
            }

            lock (_lines)
            {
                _lines.Add(text);
            }

            if (_finishes(text.TrimStart()))
            {
                _finished = true;
            }

            _line?.Invoke(text);
        }
    }

    /// <summary>The report of the console lines of a run.</summary>
    public static HeadlessPlayReport Read(IReadOnlyList<string> lines, List<(string Id, string Side, Vec3 At)> starts, int? exitCode, double seconds,
        IReadOnlyList<string> args, int routeStops)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var bots = new Dictionary<string, HeadlessBot>(StringComparer.Ordinal);
        string? loadError = null;
        string? render = null;
        HeadlessSpawn? player = null;
        bool reachedPlay = false;
        var route = new List<string>();
        int routeReached = 0;
        string? stuck = null;
        for (int i = 0; i < lines.Count; i++)
        {
            // Only the start is trimmed: a bot without a pawn ends its line with "at ".
            string text = lines[i].TrimStart().TrimEnd('\r', '\n');
            if (text.StartsWith("SVM_PLAY ", StringComparison.Ordinal) || text.StartsWith("SVM_AUDIO ", StringComparison.Ordinal))
            {
                reachedPlay = true;
            }

            if (text.StartsWith(RoutePrefix + " ", StringComparison.Ordinal))
            {
                string rest = text[(RoutePrefix.Length + 1)..];
                route.Add(rest);
                routeReached += rest.StartsWith("reached", StringComparison.Ordinal) ? 1 : 0;
                stuck = rest.StartsWith("stuck", StringComparison.Ordinal) ? rest : stuck;
            }
            else if (BotLine().Match(text) is { Success: true } m)
            {
                string name = m.Groups["name"].Value;
                string status = m.Groups["status"].Value;
                Vec3? at = ParseVec(m.Groups["at"].Value);
                // A route walker's status is the route's ("MercRoute: route to stop 2 of 3").
                string side = status.StartsWith("merc", StringComparison.Ordinal) || name.StartsWith("Merc", StringComparison.Ordinal) ? "merc" : "spy";
                (string? near, double? distance) = Nearest(starts, side, at);
                bots.TryAdd(name, new HeadlessBot(name, side, status, at, near, distance));
            }
            else if (player is null && SpawnLine().Match(text) is { Success: true } sp && ParseVec(sp.Groups["at"].Value) is { } spawnAt)
            {
                string side = sp.Groups["side"].Value;
                (string? near, double? distance) = Nearest(starts, side, spawnAt);
                player = new HeadlessSpawn(side, spawnAt, int.Parse(sp.Groups["yaw"].Value, CultureInfo.InvariantCulture), near, distance);
            }
            else if (RenderLine().Match(text) is { Success: true } r)
            {
                render = r.Groups["rest"].Value;
            }
            else if (text.StartsWith("ERROR:", StringComparison.Ordinal) || text.StartsWith("SCRIPT ERROR:", StringComparison.Ordinal)
                || text.StartsWith("USER ERROR:", StringComparison.Ordinal) || text.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("CrashHandlerException", StringComparison.Ordinal))
            {
                string message = Message(text);
                if (message.StartsWith("Could not start play", StringComparison.Ordinal))
                {
                    loadError = message;
                }

                errors.Add(message);
            }
            else if (text.StartsWith("WARNING:", StringComparison.Ordinal) && !IsExitNoise(text))
            {
                warnings.Add(Message(text));
            }
        }

        if (routeStops > 0 && stuck is null && routeReached < routeStops)
        {
            stuck = route.Count == 0
                ? "the route did not start: the game printed no route report (a stop it could not read, or the round never started)"
                : $"not finished: the run ended after {routeReached} of {routeStops} stops (give it more seconds)";
        }

        return new HeadlessPlayReport
        {
            Started = true,
            Loaded = loadError is null && reachedPlay,
            LoadError = loadError,
            Errors = errors,
            Warnings = warnings,
            RenderWarnings = render,
            Bots = [.. bots.Values],
            Player = player,
            Starts = starts,
            ExitCode = exitCode,
            Seconds = seconds,
            Args = args,
            Output = lines,
            Route = route,
            RouteStops = routeStops,
            RouteReached = routeReached,
            RouteStuck = stuck,
        };
    }

    /// <summary>The report as compact JSON (the CLI and the API method print this).</summary>
    public static SvJsonObject ToJson(HeadlessPlayReport r, bool output = false)
    {
        SvJsonObject o = ApiJson.Obj(
            ("ok", ApiJson.Bool(r.Ok)),
            ("started", ApiJson.Bool(r.Started)),
            ("loaded", ApiJson.Bool(r.Loaded)),
            ("loadError", r.LoadError is { } e ? ApiJson.Str(e) : null),
            ("seconds", ApiJson.Num(Math.Round(r.Seconds, 1))),
            ("exit", r.ExitCode is { } code ? SvJsonNode.Integer(code) : ApiJson.Str("stopped by ose")),
            ("errors", ApiJson.Strings(r.Errors)),
            ("warnings", ApiJson.Strings(r.Warnings)),
            ("renderWarnings", r.RenderWarnings is { } rw ? ApiJson.Str(rw) : null),
            ("starts", ApiJson.Arr(r.Starts.Select(s => (SvJsonNode)ApiJson.Arr([ApiJson.Str(s.Id), ApiJson.Str(s.Side), ApiJson.Vec(Round(s.At))])))),
            ("botCols", ApiJson.Strings(["name", "side", "status", "at", "nearestStart", "distance"])),
            ("bots", ApiJson.Arr(r.Bots.Select(b => (SvJsonNode)ApiJson.Arr([
                ApiJson.Str(b.Name), ApiJson.Str(b.Side), ApiJson.Str(b.Status),
                b.At is { } at ? ApiJson.Vec(Round(at)) : SvJsonNode.Null,
                b.NearestStart is { } near ? ApiJson.Str(near) : SvJsonNode.Null,
                b.StartDistance is { } d ? ApiJson.Num(Math.Round(d)) : SvJsonNode.Null,
            ])))),
            ("noPawn", r.Bots.Any(b => b.At is null)
                ? ApiJson.Strings(r.Bots.Where(b => b.At is null).Select(b => $"{b.Name} had no pawn at its first status (it did not spawn, or was dead)"))
                : null),
            ("player", r.Player is { } sp
                ? ApiJson.Obj(
                    ("side", ApiJson.Str(sp.Side)),
                    ("at", ApiJson.Vec(Round(sp.At))),
                    ("yaw", SvJsonNode.Integer(sp.Yaw)),
                    ("nearestStart", sp.NearestStart is { } near ? ApiJson.Str(near) : null),
                    ("distance", sp.StartDistance is { } d ? ApiJson.Num(Math.Round(d)) : null))
                : ApiJson.Str("the game printed no spawn for the player (it had no pawn, or the game is older than the test start)")),
            ("startAt", r.StartAt is { } at
                ? ApiJson.Obj(
                    ("at", ApiJson.Vec(Round(at.Location))),
                    ("yaw", SvJsonNode.Integer(at.Rotation.Yaw)),
                    ("spawnedThere", r.Player is { } p ? ApiJson.Bool(SpawnedAt(p.At, at.Location)) : null))
                : null),
            ("route", r.Route.Count > 0 ? ApiJson.Strings(r.Route) : null),
            ("routeStops", r.RouteStops > 0 ? SvJsonNode.Integer(r.RouteStops) : null),
            ("routeReached", r.RouteStops > 0 ? SvJsonNode.Integer(r.RouteReached) : null),
            ("routeStuck", r.RouteStuck is { } stuck ? ApiJson.Str(stuck) : null),
            ("peers", r.Peers.Count > 0 ? ApiJson.Arr(r.Peers.Select(PeerJson)) : null),
            ("args", ApiJson.Strings(r.Args)));
        if (output)
        {
            o.Add("output", ApiJson.Strings(r.Output));
        }

        return o;
    }

    private static SvJsonNode PeerJson(HeadlessNetPeer p) => ApiJson.Obj(
        ("role", ApiJson.Str(p.Role)),
        ("loaded", ApiJson.Bool(p.Loaded)),
        ("errors", ApiJson.Strings(p.Errors)),
        ("lobby", p.Lobby.Count > 0 ? ApiJson.Str(p.Lobby[^1]) : null),
        ("pawnCount", SvJsonNode.Integer(p.PawnCount)),
        ("pawns", p.Pawns is { } pawns ? ApiJson.Str(pawns) : null));

    /// <summary>The map's enabled player starts (spy and merc), read with the server's loader.</summary>
    private static List<(string, string, Vec3)> Starts(string folder)
    {
        AnalysisMap map = AnalysisMap.Load(folder);
        var starts = new List<(string, string, Vec3)>();
        foreach (TeamSide side in new[] { TeamSide.Spy, TeamSide.Merc })
        {
            foreach ((string id, OpenScct.Core.Engine.PlayerStart s) in Reachability.StartsOf(map, side))
            {
                starts.Add((id, side == TeamSide.Spy ? "spy" : "merc", AnalysisMap.V(s.Location)));
            }
        }

        return starts;
    }

    private static (string?, double?) Nearest(List<(string Id, string Side, Vec3 At)> starts, string side, Vec3? at)
    {
        if (at is not { } p)
        {
            return (null, null);
        }

        (string Id, string Side, Vec3 At)? best = null;
        double bestDistance = double.MaxValue;
        foreach (var s in starts.Where(s => s.Side == side))
        {
            double d = (s.At - p).Length;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = s;
            }
        }

        return best is { } b ? (b.Id, bestDistance) : (null, null);
    }

    /// <summary>"ERROR: text" without the prefix.</summary>
    private static string Message(string text)
    {
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon < 20 ? text[(colon + 1)..].Trim() : text;
    }

    /// <summary>Godot's leak notices at exit: printed by every run that is stopped, not a problem of the map.</summary>
    private static bool IsExitNoise(string text) =>
        text.Contains("ObjectDB instances were leaked", StringComparison.Ordinal) || text.Contains("resources still in use at exit", StringComparison.Ordinal);

    private static Vec3? ParseVec(string text)
    {
        string[] parts = text.Trim('(', ')', ' ').Split(',');
        return parts.Length == 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)
            ? new Vec3(x, y, z) : null;
    }

    private static Vec3 Round(Vec3 v) => new(Math.Round(v.X), Math.Round(v.Y), Math.Round(v.Z));

    /// <summary>"SVM_PLAY bot MercBot1: merc guard post ... at (x,y,z)" (the position is empty while the bot has no pawn).</summary>
    [GeneratedRegex(@"^SVM_PLAY bot (?<name>[^:]+): (?<status>.*) at (?<at>\([^)]*\))?$")]
    private static partial Regex BotLine();

    /// <summary>
    /// True when a spawn is the asked-for start: within a pawn's width across, and from the spot down to the floor the
    /// game's 200 uu probe finds (plus the pawn's height above it).
    /// </summary>
    public static bool SpawnedAt(Vec3 spawn, Vec3 start) =>
        Math.Sqrt(Math.Pow(spawn.X - start.X, 2) + Math.Pow(spawn.Y - start.Y, 2)) <= 32 && spawn.Z <= start.Z + 80 && spawn.Z >= start.Z - 200;

    /// <summary>"SVM_PLAY player spy spawned at (x,y,z) yaw 16384" (OpenSCCT's test start report).</summary>
    [GeneratedRegex(@"^SVM_PLAY player (?<side>spy|merc) spawned at (?<at>\([^)]*\)) yaw (?<yaw>-?\d+)$")]
    private static partial Regex SpawnLine();

    [GeneratedRegex(@"^SVM_PLAY \S+ render (?<rest>warnings=.*)$")]
    private static partial Regex RenderLine();

    /// <summary>"pawns 2: Name (spy) at (x,y,z); ..." from a game of an online run.</summary>
    [GeneratedRegex(@"^pawns (?<n>\d+): ?(?<list>.*)$")]
    private static partial Regex PawnsLine();

    /// <summary>One pawn of a pawns line: "Name (spy) at (x,y,z)".</summary>
    [GeneratedRegex(@"(?<who>[^;]+? \((?:spy|merc)\)) at (?<at>\([^)]*\))")]
    private static partial Regex PawnEntry();
}
