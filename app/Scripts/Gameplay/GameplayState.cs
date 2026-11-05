using System.Runtime.CompilerServices;
using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// What the gameplay tools, panels and overlays share: the traversal world (rebuilt only when the brushes or the baked
/// map change, in the background when a tool or overlay asks ahead), the event graph and mission tree (rebuilt after
/// any edit), the ledge detector's settings and its last proposals waiting to be applied.
/// </summary>
public sealed class GameplayState
{
    private readonly EditorContext _ctx;
    private TraversalWorld? _world;
    private int _worldKey;
    private Task<TraversalWorld>? _building;
    private int _buildingKey;
    private GameplayGraph? _graph;
    private MissionTree? _missions;

    public GameplayState(EditorContext ctx)
    {
        _ctx = ctx;
        ctx.ProjectChanged += _ => Invalidate(keepProposals: true);
        ctx.SessionReplaced += () => Invalidate(keepProposals: false);
    }

    private static GameplayState? s_instance;

    /// <summary>The editor's gameplay state (made on first use, for the shared editor context).</summary>
    public static GameplayState Instance => s_instance ??= new GameplayState(EditorContext.Instance);

    public EditorContext Context => _ctx;

    public LedgeOptions Options { get; set; } = new();

    /// <summary>Lips the last detection found that no LG covers yet.</summary>
    public IReadOnlyList<Lip> Proposals { get; private set; } = [];

    private Lip? _highlightedProposal;

    /// <summary>The proposal being inspected in the viewport, until it is added, discarded or replaced.</summary>
    public Lip? HighlightedProposal => _highlightedProposal is { } lip && Proposals.Contains(lip) ? lip : null;

    public void HighlightProposal(Lip lip) => _highlightedProposal = Proposals.Contains(lip) ? lip : null;

    /// <summary>The last detection's summary line.</summary>
    public string LastDetection { get; private set; } = "";

    /// <summary>Raised after the graph, the proposals or the world changed (panels and overlays refresh).</summary>
    public event Action? Changed;

    /// <summary>The current traversal world: built now when it is out of date (waiting for a background build of the
    /// same geometry when one is running).</summary>
    public TraversalWorld World
    {
        get
        {
            int key = WorldKey(_ctx.Project);
            if (_world is not null && key == _worldKey)
            {
                return _world;
            }

            // A build that failed, or was cancelled because the editor is quitting, is done again here.
            if (_building is { IsFaulted: false, IsCanceled: false } building && _buildingKey == key)
            {
                Adopt(building.Result, key);
                return _world!;
            }

            Adopt(TraversalWorld.Build(_ctx.Project, _ctx.Session.Folder), key);
            return _world!;
        }
    }

    /// <summary>The traversal world when it is built and current, else null (for overlays that must not stall a redraw).</summary>
    public TraversalWorld? WorldIfBuilt
    {
        get
        {
            int key = WorldKey(_ctx.Project);
            if (_world is null || key != _worldKey)
            {
                if (_building is not { IsCompletedSuccessfully: true } done || _buildingKey != key)
                {
                    return null;
                }

                Adopt(done.Result, key);
            }

            return _world;
        }
    }

    /// <summary>The current traversal world when built, else the last one built for this map while the new one builds
    /// in the background (started here), else null; never waits. For drawing that should not flicker on each brush
    /// edit: <see cref="Changed"/> follows when the current world is ready.</summary>
    public TraversalWorld? LatestWorld
    {
        get
        {
            if (WorldIfBuilt is { } current)
            {
                return current;
            }

            Prewarm();
            return _world;
        }
    }

    /// <summary>
    /// Starts building the traversal world on a worker thread when it is out of date, so the first hover of a tool
    /// does not stall the editor (about a second on a large baked map). <see cref="Changed"/> is raised on the main
    /// thread when it is ready.
    /// </summary>
    public void Prewarm()
    {
        MapProject p = _ctx.Project;
        int key = WorldKey(p);
        if ((_world is not null && key == _worldKey) || (_building is not null && _buildingKey == key))
        {
            return;
        }

        // Brushes are replaced, never changed in place, by edits: a copy of the list is a stable snapshot.
        var snapshot = new MapProject { Brushes = [.. p.Brushes], Baked = p.Baked };
        string? folder = _ctx.Session.Folder;
        _buildingKey = key;
        Task<TraversalWorld> task = BackgroundJobs.StartTask(_ => TraversalWorld.Build(snapshot, folder));
        _building = task;
        task.ContinueWith(_ => BackgroundJobs.Post(() =>
        {
            if (ReferenceEquals(_building, task) && WorldIfBuilt is not null)
            {
                Changed?.Invoke();
            }
        }), TaskScheduler.Default);
    }

    public GameplayGraph Graph => _graph ??= GameplayGraph.Build(_ctx.Project);

    public MissionTree Missions => _missions ??= MissionTree.Build(_ctx.Project, ClassDefaults.Catalog);

    /// <summary>Runs the ledge detector over the map (or <paramref name="region"/>) and keeps the uncovered lips.</summary>
    public LedgeResult Detect((Vec3 Min, Vec3 Max)? region = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        LedgeResult result = LedgeDetector.Detect(World, Options with { Region = region });
        List<Ge> ledges = _ctx.Project.Ges.Where(g => g.Kind == GeKinds.LedgeGrab && g.Points.Count >= 2).ToList();
        Proposals = result.Lips.Where(l => !GeEdits.Covered(l, ledges)).ToList();
        LastDetection = $"{result.Lips.Count} ledges found, {Proposals.Count} not covered by an LG ({result.Edges} edges, " +
            $"{result.Probes} probes, {watch.ElapsedMilliseconds} ms)";
        Changed?.Invoke();
        return result;
    }

    /// <summary>Drops proposals (all, or those listed).</summary>
    public void Discard(IEnumerable<Lip>? lips = null)
    {
        Proposals = lips is null ? [] : Proposals.Except(lips).ToList();
        Changed?.Invoke();
    }

    /// <summary>Adds LGs for the given proposals (all when null), one undo step; returns how many.</summary>
    public int Apply(IReadOnlyList<Lip>? lips = null)
    {
        List<Lip> chosen = (lips ?? Proposals).ToList();
        if (GeEdits.AddLedges(_ctx.Project, chosen, label: "Add detected ledges") is not { } cmd)
        {
            return 0;
        }

        _ctx.Execute(cmd);
        _ctx.Selection.Set(cmd.Added);
        Proposals = Proposals.Except(chosen).ToList();
        Changed?.Invoke();
        return chosen.Count;
    }

    private void Adopt(TraversalWorld world, int key)
    {
        _world = world;
        _worldKey = key;
        _building = null;
    }

    private void Invalidate(bool keepProposals)
    {
        _graph = null;
        _missions = null;
        if (!keepProposals)
        {
            _world = null;
            _building = null;
            Proposals = [];
            _highlightedProposal = null;
            LastDetection = "";
        }

        Changed?.Invoke();
    }

    /// <summary>Changes when a brush is replaced, moved or reshaped, or the baked map changes; GE and actor edits keep it.</summary>
    private static int WorldKey(MapProject p)
    {
        var h = new HashCode();
        h.Add(p.Brushes.Count);
        h.Add(p.Baked?.SourceMap);
        h.Add(p.Baked?.KeepCollision);
        foreach (Brush b in p.Brushes)
        {
            h.Add(RuntimeHelpers.GetHashCode(b));
            h.Add(b.Location);
            h.Add(b.Shape.Faces.Count);
        }

        return h.ToHashCode();
    }
}
