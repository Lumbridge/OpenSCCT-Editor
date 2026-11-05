using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Game.AI;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The Gameplay/Analysis menu: the stealth light map (lights at start, blacked out, all lit, or after the selected
/// switch is used; crouched or standing), reachability, the bots' nav graph, a route between two selected objects,
/// the deep Map Check and a headless test round in OpenSCCT. Each runs in the background on the compiled map; the
/// same analyses are the <c>stealth</c>, <c>reach</c>, <c>route</c> and <c>nav</c> queries, <c>check {deep}</c> and
/// <c>playtest</c> of the scripting API.
/// </summary>
[ShellPart(Order = -27)]
public sealed class AnalysisPart : IShellPart
{
    private const string Menu = "Gameplay/Analysis";

    private EditorContext _ctx = null!;
    private AnalysisState _state = null!;

    private const string TestRound = "Test round";

    /// <summary>Ends the running (or last) headless test round.</summary>
    private CancellationTokenSource? _round;

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        _state = AnalysisState.Instance;
        _state.Changed += ViewOverlays.Invalidate;

        Add("analysis.stealth.start", "Stealth Map (Lights at Start)", () => Stealth(LightState.Start), 10,
            "How lit a spy is on each floor spies reach, with the lights as a round starts, and what mercs' trackers see (the Merc Sight overlay)");
        Add("analysis.stealth.blackout", "Stealth Map (Blackout)", () => Stealth(new LightState { Name = "blackout" }), 20,
            "The stealth map with every switchable light off");
        Add("analysis.stealth.lit", "Stealth Map (All Lit)", () => Stealth(new LightState { Name = "lit" }), 30,
            "The stealth map with every switchable light on");
        Add("analysis.stealth.switch", "Stealth Map (Selected Switch Used)", StealthSwitch, 40,
            "The stealth map after the selected switch, trigger or relay's first use reaches the lights", () => SelectedActors().Count == 1);
        _ctx.AddAction(new EditorAction("analysis.stealth.standing", "Stealth Map for a Standing Spy", Menu, () =>
        {
            _state.Standing = !_state.Standing;
            _ctx.SetStatus(_state.Standing ? "Stealth maps now read a standing spy" : "Stealth maps now read a crouched spy");
        })
        {
            Order = 50,
            IsChecked = () => _state.Standing,
            Description = "Read the light bands for a standing spy instead of a crouched one (the next stealth map you run)",
        });
        _ctx.AddAction(new EditorAction("analysis.stealth.night", "Stealth Map in Night Vision", Menu, () =>
        {
            _state.Night = !_state.Night;
            _ctx.SetStatus(_state.Night ? "Stealth maps now read what a merc sees in night vision" : "Stealth maps now read normal vision");
        })
        {
            Order = 55,
            IsChecked = () => _state.Night,
            Description = "Read the bands and backgrounds as night vision shows them: every lit surface and the spy lifted by its ambient, then the screen's gain (the next stealth map you run)",
        });
        Add("analysis.reach", "Reachability", Reach, 100,
            "Where spies and mercs get from their starts (doors and lifts as they can work them): both, spies only, mercs only");
        Add("analysis.nav.spy", "Bots' Nav Graph (Spy)", () => Nav(TeamSide.Spy), 110, "The graph spy bots plan on, with its traversal links");
        Add("analysis.nav.merc", "Bots' Nav Graph (Merc)", () => Nav(TeamSide.Merc), 120, "The graph merc bots plan on");
        Add("analysis.route.spy", "Route Between Selection (Spy)", () => Route(TeamSide.Spy), 130,
            "Can a spy get from the first selected object to the second, and how", () => SelectedCentres().Count == 2);
        Add("analysis.route.merc", "Route Between Selection (Merc)", () => Route(TeamSide.Merc), 140,
            "Can a merc get from the first selected object to the second, and how", () => SelectedCentres().Count == 2);
        Add("analysis.clear", "Clear Analysis", () =>
        {
            _state.Clear();
            _ctx.SetStatus("Analysis overlays cleared");
        }, 150);
        Add("analysis.check", "Deep Map Check", DeepCheck, 200,
            "Map Check plus the rules that compile the map: zone leaks, actors in the void, floating meshes, mesh collision caps, reachability",
            () => EditorServices.Checker is not null);
        Add("analysis.playtest", "Headless Test Round", () => Playtest(), 210,
            "Build the map and play a round of it in OpenSCCT with no window; the log gets whether it loaded, errors and where bots stood",
            CanPlaytest);
        Add("analysis.playtest.client", "Headless Online Round (Host and Client)", () => Playtest(client: true), 212,
            "Play the round online with no window: one game hosts it and a second joins on the other side; the log gets what each loaded and which pawns each world held",
            CanPlaytest);
        Add("analysis.playtest.route", "Walk Selection in a Headless Round", WalkSelection, 214,
            "Play a round with no window in which a bot walks to the selected objects in selection order (from the selected player start, when one is first) " +
            "with its side's own navigation and doors; the log gets each one reached or where it got stuck",
            () => CanPlaytest() && SelectedActors().Count > 0);
        Add("analysis.playtest.stop", "Stop Headless Test Round", () => _round?.Cancel(), 220,
            "End the running test round now and report what the game printed so far", () => BackgroundJobs.IsRunning(TestRound));
    }

    private void Add(string id, string title, Action run, int order, string? description = null, Func<bool>? enabled = null) =>
        _ctx.AddAction(new EditorAction(id, title, Menu, run)
        {
            Order = order,
            Description = description,
            IsEnabled = enabled ?? (() => true),
        });

    private void Stealth(LightState lights)
    {
        var options = new StealthOptions { Lights = lights, Crouched = !_state.Standing, Night = _state.Night };
        _state.Run("Stealth map", map => StealthMap.Build(map, options), r =>
        {
            _state.ShowStealth(r);
            int hidden = r.Cells.Count(c => c.Band == StealthBand.Hidden);
            int tracked = r.Cells.Count(c => c.Band == StealthBand.Hidden && c.Tracked);
            string lightsOff = r.LightsOff.Count > 0 ? $", {r.LightsOff.Count} lights off" : "";
            _ctx.Log($"Stealth map ({r.State}{lightsOff}, {(r.Crouched ? "crouched" : "standing")}{(r.Night ? ", night vision" : "")}): {r.Cells.Count} floor cells, " +
                $"{Percent(hidden, r.Cells.Count)} hidden ({Percent(tracked, r.Cells.Count)} hidden but in a merc tracker's sight), " +
                $"{Percent(r.Cells.Count(c => c.Band == StealthBand.Exposed), r.Cells.Count)} exposed, " +
                $"{Percent(r.Cells.Count(c => c.Silhouette), r.Cells.Count)} dark against a bright background" +
                (!r.HasLightData ? "; the map has no light data, so every cell reads as unknown"
                    : r.PawnLights + r.SpotLights == 0 ? "; no light lights pawns in game (only lights with bApplyToInGameLighting or bDynamicLight do)" : "") +
                $"; {r.LightRoles.Count(l => l.Kind == LightRoleKind.WorldOnly)} lights light only the world and " +
                $"{r.LightRoles.Count(l => l.Kind == LightRoleKind.PawnsOnly)} only pawns (the Light Roles overlay)");
            _ctx.SetStatus("Stealth map ready: blue hidden, amber partly lit, red exposed, white rings silhouetted; turn on the Merc Sight overlay for trackers and torches");
        });
    }

    private void StealthSwitch()
    {
        string id = SelectedActors()[0];
        Stealth(new LightState { Name = "switch:" + id });
    }

    private void Reach()
    {
        _state.Run("Reachability", map =>
        {
            ReachMap spy = Reachability.FromStarts(map, new ReachProfile(TeamSide.Spy), map.ActivationMask);
            ReachMap merc = Reachability.FromStarts(map, new ReachProfile(TeamSide.Merc), map.ActivationMask);
            return (Spy: spy, Merc: merc, Findings: Reachability.Findings(map, spy, merc), Areas: Reachability.SpyOnlyAreas(spy, merc));
        }, r =>
        {
            _state.ShowReach(r.Spy, r.Merc);
            _ctx.Log($"Reachability: spies reach {r.Spy.ReachedCount} of {r.Spy.Graph.NodeCount} floor points, mercs " +
                $"{r.Merc.ReachedCount} of {r.Merc.Graph.NodeCount}; {r.Areas.Count} areas only spies reach. Doors and lifts as each side can work them; " +
                "walls that events break and GE switches are read as the round starts.");
            foreach (Finding f in r.Findings)
            {
                _ctx.Log($"Reachability: {f}", f.Severity);
            }

            _ctx.SetStatus("Reachability ready (Gameplay/Analysis)");
        });
    }

    private void Nav(TeamSide side)
    {
        _state.Run("Nav graph", map =>
        {
            ReachProfile profile = new(side, DropRule.Bot);
            return (Map: map, Graph: map.Graph(profile, map.ActivationMask));
        }, r =>
        {
            _state.ShowNav(r.Map, r.Graph, side);
            _ctx.Log($"Bots' nav graph ({(side == TeamSide.Spy ? "spy" : "merc")}): {r.Graph.NodeCount} nodes, {r.Graph.EdgeCount} edges, {r.Graph.Links.Count} traversal links");
        });
    }

    private void Route(TeamSide side)
    {
        List<Vec3> points = SelectedCentres();
        var profile = new ReachProfile(side);
        _state.Run("Route", map => Reachability.Route(map, profile, map.ActivationMask, points[0], points[1]), r =>
        {
            _state.ShowRoute(r, profile);
            string who = side == TeamSide.Spy ? "A spy" : "A merc";
            string hurt = r.Damage > 0 ? $", drops cost {Math.Round(r.Damage)}% health" : "";
            _ctx.Log(r.Reachable ? $"{who} gets there: {Math.Round(r.Length)} units, {Moves(r)}{hurt}" : $"{who} cannot get there: {r.Problem}",
                r.Reachable ? Severity.Info : Severity.Warning);
        });
    }

    /// <summary>The moves of a route other than walking and crouching (drops, climbs, zip lines...).</summary>
    private static string Moves(RouteResult r) => string.Join(", ", r.Legs
        .Where(l => !l.IsWalk)
        .Select(l => l.Label)
        .DefaultIfEmpty("walking only"));

    private void DeepCheck()
    {
        if (EditorServices.Checker is not { } checker)
        {
            return;
        }

        MapProject snapshot = ProjectSnapshot.Take(_ctx.Project);
        _state.Run("Deep check", map => checker.Check(snapshot, new CheckOptions { Compiled = map }), findings =>
        {
            _state.Report("Deep Map Check", findings);
            _ctx.SetStatus($"Deep Map Check: {findings.Count} findings ({findings.Count(f => f.Fix is not null)} with a fix)");
        });
    }

    private bool CanPlaytest() => EditorServices.Compiler is not null && !BackgroundJobs.IsRunning(TestRound);

    /// <summary>
    /// A route bot walks the selected actors in selection order. A selected player start first is where it starts, and
    /// its team is the walker's side; otherwise the map's Play side, from any of its starts.
    /// </summary>
    private void WalkSelection()
    {
        List<string> ids = SelectedActors();
        string side = _ctx.Project.Workspace.Play.Side;
        string? from = null;
        if (_ctx.Project.FindActor(ids[0]) is { } first && MapObjects.ClassMatches(first.Class, "PlayerStart", subclasses: true))
        {
            from = ids[0];
            side = first.Props.GetInt("TeamNumber") == 1 ? "merc" : "spy";
            ids.RemoveAt(0);
        }

        if (ids.Count == 0)
        {
            _ctx.Log("Walk Selection: select the objects to walk to (after the player start to start from)", Severity.Warning);
            return;
        }

        Playtest(new RouteRequest(ids, side, from));
    }

    /// <summary>A walk for <see cref="Playtest"/>: the stops, the walker's side and its start.</summary>
    private sealed record RouteRequest(IReadOnlyList<string> Stops, string Side, string? From);

    private void Playtest(RouteRequest? route = null, bool client = false)
    {
        if (EditorServices.Compiler is not { } compiler || GameLauncher.FindGame() is not { } exe)
        {
            _ctx.Log("Headless test round: OpenSCCT.exe was not found (set OSE_GAME_EXE or install OpenSCCT with its launcher)", Severity.Warning);
            return;
        }

        if (GameExe.TooOld(exe) is { } tooOld)
        {
            _ctx.Log("Headless test round: " + tooOld, Severity.Warning);
            return;
        }

        if (EditorLifetime.IsQuitting)
        {
            return;
        }

        // One round at a time: the running round keeps its token source, so Stop and the editor quitting still end it.
        if (BackgroundJobs.IsRunning(TestRound))
        {
            _ctx.Log($"{TestRound} is already running.", Severity.Warning);
            return;
        }

        MapProject snapshot = ProjectSnapshot.Take(_ctx.Project);
        EditorSession session = _ctx.Session;
        string output = Path.Combine(BuildActions.BuildsFolder, snapshot.Id);
        PlayOptions play = snapshot.Workspace.Play;
        _round?.Dispose();
        // Stopped by a newer round or by the editor quitting.
        CancellationToken cancel = (_round = CancellationTokenSource.CreateLinkedTokenSource(EditorLifetime.Token)).Token;
        BackgroundJobs.Run(TestRound, () =>
        {
            CompileResult built = compiler.Compile(snapshot, output, new CompileOptions
            {
                FastLighting = true, ContentRoot = EditorServices.ContentRoot, ProjectFolder = session.Folder,
            });
            if (!built.Ok)
            {
                return (Built: built, Report: (HeadlessPlayReport?)null);
            }

            // A walk or an online round has no other bots: nothing gets in the walker's way, and both games stay in step.
            bool plain = route is null && !client;
            return (Built: built, Report: HeadlessPlay.Run(new HeadlessPlayOptions
            {
                Exe = exe, MapFolder = built.OutputFolder, MapId = snapshot.Id, Side = route?.Side ?? play.Side, Mode = play.Mode, Rules = play.Rules,
                Bots = plain ? HeadlessPlay.DefaultBots(play) : 0, Difficulty = play.Difficulty,
                Players = HeadlessPlay.DefaultPlayers(play, botsGiven: !plain),
                Content = GameExe.LauncherContent(exe), Route = route?.Stops, RouteFrom = route?.From, Client = client,
                Seconds = route is null ? 40 : HeadlessPlay.RouteSeconds(route.Stops.Count),
            }, cancel: cancel));
        }, r =>
        {
            if (r.Report is not { } report)
            {
                Finding first = r.Built.Findings.First(f => f.Severity == Severity.Error);
                _ctx.Log($"Headless test round: the map does not build: {first}", Severity.Error);
                return;
            }

            _ctx.Log($"Headless test round: {(report.Loaded ? "the round started" : "the round did not start")}{(report.LoadError is { } e ? ": " + e : "")}; " +
                $"{report.Errors.Count} errors, {report.Warnings.Count} warnings in {report.Seconds:0} s",
                report.Ok ? Severity.Info : Severity.Error);
            foreach (string error in report.Errors.Take(20))
            {
                _ctx.Log($"Headless test round: {error}", Severity.Error);
            }

            foreach (string step in report.Route)
            {
                _ctx.Log($"Headless test round: route {step}", step.StartsWith("stuck", StringComparison.Ordinal) ? Severity.Error : Severity.Info);
            }

            if (report.RouteStuck is { } stuck && !report.Route.Any(step => step.StartsWith("stuck", StringComparison.Ordinal)))
            {
                _ctx.Log($"Headless test round: route {stuck}", Severity.Error);
            }

            foreach (HeadlessNetPeer peer in report.Peers)
            {
                _ctx.Log($"Headless test round: the {peer.Role} {(peer.Loaded ? "loaded the round" : "did not load the round")}" +
                    $"{(peer.Lobby.Count > 0 ? "; " + peer.Lobby[^1] : "")}; {peer.PawnCount} pawns: {peer.Pawns ?? "none reported"}",
                    peer.Loaded && peer.Errors.Count == 0 ? Severity.Info : Severity.Error);
                foreach (string error in peer.Errors.Take(10))
                {
                    _ctx.Log($"Headless test round: {peer.Role}: {error}", Severity.Error);
                }
            }

            foreach (HeadlessBot bot in report.Bots)
            {
                _ctx.Log(bot.At is { } at
                    ? $"Headless test round: {bot.Name} at {at} ({Math.Round(bot.StartDistance ?? 0)} from {bot.NearestStart}): {bot.Status}"
                    : $"Headless test round: {bot.Name} had no pawn at its first status: {bot.Status}", bot.At is null ? Severity.Warning : Severity.Info);
            }
        });
    }

    private List<string> SelectedActors() => _ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Actor).Select(r => r.Id).ToList();

    private List<Vec3> SelectedCentres() => _ctx.Selection.Items.Select(r =>
    {
        (Vec3 min, Vec3 max) = MapObjects.Bounds(_ctx.Project, r);
        return (min + max) / 2;
    }).ToList();

    private static string Percent(int part, int whole) => whole == 0 ? "0%" : $"{100.0 * part / whole:0}%";
}
