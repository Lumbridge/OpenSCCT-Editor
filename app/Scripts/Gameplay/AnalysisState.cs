using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Game.AI;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The analyses of the compiled map in the editor: each runs in the background on a snapshot (compiled once and kept
/// in the session's <see cref="AnalysisCache"/> until the next edit) and keeps its last result for the overlays. A
/// result stays on screen after edits until it is run again or cleared; the overlays dim it once the map has changed
/// since it was made (<see cref="IsStale"/>).
/// </summary>
public sealed class AnalysisState
{
    private readonly EditorContext _ctx;

    /// <summary>The project version each result on show was made from, by result name.</summary>
    private readonly Dictionary<string, int> _versions = [];
    private int _runVersion;

    private AnalysisState(EditorContext ctx)
    {
        _ctx = ctx;
        ctx.SessionReplaced += Clear;
    }

    private static AnalysisState? s_instance;

    public static AnalysisState Instance => s_instance ??= new AnalysisState(EditorContext.Instance);

    /// <summary>The last stealth light map.</summary>
    public StealthResult? Stealth { get; private set; }

    /// <summary>The last reach maps, and per spy node whether only spies reach it.</summary>
    public (ReachMap Spy, ReachMap Merc, bool[] SpyOnly)? Reach { get; private set; }

    /// <summary>The bots' graph on show, with the GE ids of its links.</summary>
    public (NavGraph Graph, TeamSide Side, Dictionary<int, string> Ges)? Nav { get; private set; }

    /// <summary>The last route asked for.</summary>
    public (RouteResult Route, ReachProfile Profile)? Route { get; private set; }

    /// <summary>Stealth maps read for a standing spy instead of a crouched one.</summary>
    public bool Standing { get; set; }

    /// <summary>Stealth maps read night vision instead of normal vision.</summary>
    public bool Night { get; set; }

    /// <summary>True when the map changed after the result on show (<c>nameof</c> its property) was made.</summary>
    public bool IsStale(string result) =>
        _versions.TryGetValue(result, out int version) && version != AnalysisCache.Of(_ctx.Session).Version;

    /// <summary>Raised on the main thread when a result arrives or is cleared.</summary>
    public event Action? Changed;

    public void Clear()
    {
        Stealth = null;
        Reach = null;
        Nav = null;
        Route = null;
        _versions.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the compiled map in the background (compiling a snapshot first when the cached
    /// map is out of date), then <paramref name="done"/> on the main thread.
    /// </summary>
    public void Run<T>(string name, Func<AnalysisMap, T> work, Action<T> done)
    {
        EditorSession session = _ctx.Session;
        AnalysisCache cache = AnalysisCache.Of(session);
        int version = cache.Version;
        AnalysisMap? current = cache.Current;
        MapProject? snapshot = current is null ? ProjectSnapshot.Take(session.Project) : null;
        string? folder = session.Folder;
        BackgroundJobs.Run(name, () =>
        {
            AnalysisMap map = current ?? AnalysisMap.Build(snapshot!, folder, line => BackgroundJobs.Post(() => _ctx.SetStatus(line)));
            T result;
            lock (map.Gate)
            {
                result = work(map);
            }

            return (map, result);
        }, r =>
        {
            cache.Store(r.map, version);
            if (!ReferenceEquals(session, _ctx.Session))
            {
                return;
            }

            _runVersion = version;
            done(r.result);
            Changed?.Invoke();
        });
    }

    // Called from a Run's done, so the result is stamped with the version that run compiled.
    public void ShowStealth(StealthResult result)
    {
        Stealth = result;
        _versions[nameof(Stealth)] = _runVersion;
    }

    public void ShowReach(ReachMap spy, ReachMap merc)
    {
        Reach = (spy, merc, Reachability.SpyOnly(spy, merc));
        _versions[nameof(Reach)] = _runVersion;
    }

    public void ShowNav(AnalysisMap map, NavGraph graph, TeamSide side)
    {
        Nav = (graph, side, map.GeList().ToDictionary(g => g.Index, g => g.Id));
        _versions[nameof(Nav)] = _runVersion;
    }

    public void ShowRoute(RouteResult route, ReachProfile profile)
    {
        Route = (route, profile);
        _versions[nameof(Route)] = _runVersion;
    }

    /// <summary>Logs findings and hands them to the Map Check panel.</summary>
    public void Report(string what, IReadOnlyList<Finding> findings)
    {
        _ctx.SetFindings(findings);
        int errors = findings.Count(f => f.Severity == Severity.Error);
        int warnings = findings.Count(f => f.Severity == Severity.Warning);
        _ctx.Log($"{what}: {errors} errors, {warnings} warnings, {findings.Count - errors - warnings} notes",
            errors > 0 ? Severity.Error : warnings > 0 ? Severity.Warning : Severity.Info);
    }
}
