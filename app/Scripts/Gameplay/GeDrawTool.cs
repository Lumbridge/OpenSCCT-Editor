using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The GE tool (T). Ledge grabs: hover a top edge to see the lip the detector finds there (with why not, when there is
/// none) and click to add an LG flush on it; Shift+drag draws one freehand. Every other kind is one drag: zip line
/// from the high end to the low one, ladder and pipe from the wall at their foot up to their top (the climber side
/// is the wall's normal), pole bottom to top, hand-over-hand and fence end to end. The line shows the shape checks
/// as you drag. Escape cancels a drag.
/// </summary>
public sealed class GeDrawTool : IViewportTool
{
    private const double LedgeSearch = 96;

    private readonly ToolManager _tools;
    private readonly GameplayState _state;
    private EditorViewport? _view;
    private Vector2 _mouse;
    private GameplayHit? _start;
    private GameplayHit? _end;
    private Lip? _lip;
    private string _hint = "";
    private Vec3? _lastQuery;

    public GeDrawTool(ToolManager tools, GameplayState state)
    {
        _tools = tools;
        _state = state;
    }

    public string Id => "ge";

    public string Title => "Draw GE";

    public string Shortcut => "T";

    private string Kind => _tools.GeKind;

    /// <summary>Builds the collision world in the background, so the first hover does not wait for it.</summary>
    public void Activate() => _state.Prewarm();

    public void Deactivate() => Reset();

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _view = view;
                _mouse = press.Position;
                if (Kind == GeKinds.LedgeGrab && !press.ShiftPressed)
                {
                    AddHoveredLedge(view);
                    return true;
                }

                _start = Hit(view, press.Position);
                _end = _start;
                view.Redraw();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _start is not null:
                Finish();
                ViewportLayout.Instance?.RedrawAll();
                return true;
            case InputEventMouseMotion motion:
                _view = view;
                _mouse = motion.Position;
                if (_start is not null)
                {
                    _end = Hit(view, motion.Position);
                }
                else if (Kind == GeKinds.LedgeGrab)
                {
                    Hover(view);
                }

                view.Redraw();
                return _start is not null;
        }

        return false;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        if (view != _view)
        {
            return;
        }

        var batch = new OverlayBatch();
        Color c = WorldScene.GeColor(Kind);
        List<string> lines = [];
        if (_start is { } s && _end is { } e && Points(s, e) is { } pts)
        {
            batch.Polyline(pts.Take(2).ToList(), c, 3);
            if (pts.Count > 2)
            {
                batch.Arrow(pts[0], pts[2] + (pts[2] - pts[0]) * 2, c, 2);
            }

            lines.Add($"{Kind} {(pts[1] - pts[0]).Length:0} long");
            lines.AddRange(GeShapes.Problems(Kind, pts).Select(p => p.Message));
        }
        else if (_lip is { } lip)
        {
            batch.Line(lip.A, lip.B, SceneStyle.Hover, 4);
            batch.Arrow(lip.Mid, lip.Mid + lip.Out * 24, SceneStyle.Hover, 2);
            lines.Add(_hint);
        }
        else if (_hint.Length > 0)
        {
            lines.Add(_hint);
        }

        batch.Draw(view.Cam, canvas);
        // The first line says what is drawn (in the GE's colour), the rest are the shape checks' warnings.
        ViewTags.BesideLines(canvas, _mouse, lines.Select((line, i) => new TagPart(line, i == 0 ? c : EditorTheme.Warning)));
    }

    public bool Cancel()
    {
        if (_start is null && _lip is null)
        {
            return false;
        }

        Reset();
        ViewportLayout.Instance?.RedrawAll();
        return true;
    }

    public void BuildOptions(HBoxContainer bar)
    {
        ToolOptions.Name(bar, "GE");
        OptionButton kinds = ToolOptions.Options(bar, "Kind of GE to draw", 150);
        for (int i = 0; i < GeKinds.All.Count; i++)
        {
            kinds.AddItem($"{GeKinds.All[i]}  {GeShapes.Stem(GeKinds.All[i])}", i);
            if (GeKinds.All[i] == Kind)
            {
                kinds.Select(i);
            }
        }

        kinds.ItemSelected += i =>
        {
            _tools.GeKind = GeKinds.All[(int)i];
            Reset();
        };
        ToolOptions.Hint(bar, "Ledge grab: hover an edge and click (Shift+drag draws freehand)", "Other kinds: drag", "Esc cancels");
    }

    private GameplayHit Hit(EditorViewport view, Vector2 mouse)
    {
        int depth = ViewAxes.DepthAxis(view.Cam.Kind);
        double? z = depth >= 0 && _start is { } s ? s.Point[depth] : null;
        return GameplayPicking.Point(_tools, _state.World, view.Cam, mouse, z);
    }

    /// <summary>Finds the lip nearest the surface under the cursor (detector run in a small box round it).</summary>
    private void Hover(EditorViewport view, bool wait = false)
    {
        if ((wait ? _state.World : _state.WorldIfBuilt) is not { } w)
        {
            // Hovering never waits for the collision world; the click does.
            _state.Prewarm();
            _lip = null;
            _hint = "Reading the level collision…";
            return;
        }

        if (GameplayPicking.Surface(w, view.Cam, _mouse) is not { } hit)
        {
            _lip = null;
            _hint = "";
            return;
        }

        if (_lastQuery is { } last && (last - hit.Point).Length < 8)
        {
            return;
        }

        _lastQuery = hit.Point;
        var box = new Vec3(LedgeSearch, LedgeSearch, LedgeSearch);
        LedgeResult result = LedgeDetector.Detect(w, _state.Options with { Region = (hit.Point - box, hit.Point + box) });
        _lip = result.Lips
            .Where(l => Math.Abs(l.A.Z - hit.Point.Z) <= LedgeSearch)
            .OrderBy(l => DistanceXY(l, hit.Point))
            .FirstOrDefault(l => DistanceXY(l, hit.Point) <= LedgeSearch);
        if (_lip is { } lip)
        {
            Ge? covering = GeEdits.Covering(lip, _tools.Context.Project.Ges);
            _hint = covering is not null
                ? $"Lip {lip.Length:0} long, drop {lip.Drop:0} · already covered by {covering.Id}"
                : $"Lip {lip.Length:0} long, drop {lip.Drop:0} · click to add a ledge grab";
        }
        else
        {
            KeyValuePair<LipProblem, int> worst = result.Refused.OrderByDescending(kv => kv.Value).FirstOrDefault();
            _hint = worst.Value > 0 ? $"No grabbable lip here: {LedgeProbe.Describe(worst.Key, 0)}" : "No ledge edge here";
        }
    }

    private void AddHoveredLedge(EditorViewport view)
    {
        _lastQuery = null;
        Hover(view, wait: true);
        if (_lip is not { } lip)
        {
            _tools.Context.SetStatus(_hint.Length > 0 ? _hint : "No ledge under the cursor");
            return;
        }

        if (GeEdits.Covering(lip, _tools.Context.Project.Ges) is { } covering)
        {
            _tools.Context.SetStatus($"That lip already has {covering.Id}");
            return;
        }

        if (GeEdits.AddLedges(_tools.Context.Project, [lip], label: "Add ledge grab") is { } cmd)
        {
            Commit(cmd, $"Added an LG {lip.Length:0} long over a {lip.Drop:0} drop");
        }

        _lastQuery = null;
    }

    private void Finish()
    {
        if (_start is not { } s || _end is not { } e || Points(s, e) is not { } pts || (pts[1] - pts[0]).Length < 4)
        {
            Reset();
            _tools.Context.SetStatus("Drag further to draw a GE");
            return;
        }

        string id = GeShapes.IdSource(_tools.Context.Project)(GeShapes.Stem(Kind));
        var ge = new Ge(id, Kind) { Points = pts.Select(GeEdits.Round).ToList() };
        List<GeProblem> problems = GeShapes.Problems(Kind, ge.Points);
        Reset();
        Commit(new AddObjectsCommand($"Add {id}", ges: [ge]),
            problems.Count == 0 ? $"Added {id} ({ge.Kind})" : $"Added {id}: {string.Join("; ", problems.Select(p => p.Message))}",
            problems.Any(p => p.Severity != Severity.Info));
    }

    /// <summary>The GE's points for a drag from <paramref name="s"/> to <paramref name="e"/>.</summary>
    private List<Vec3>? Points(GameplayHit s, GameplayHit e)
    {
        Vec3 a = s.Point, b = e.Point;
        switch (Kind)
        {
            case GeKinds.Ladder or GeKinds.Pipe:
                return ClimbFromDrag(s, e);
            case GeKinds.Pole:
                double flat = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                double rise = Math.Abs(b.Z - a.Z) > flat ? b.Z - a.Z : flat;
                return [a, a with { Z = a.Z + rise }];
            case GeKinds.ZipLine:
                // Drawn from the high end: keep the drag's order (the engine slides from high to low either way).
                return [a, b];
            default:
                return [a, b];
        }
    }

    /// <summary>A ladder or pipe from a drag: the foot on the floor below where the drag began (on a wall: out from it),
    /// the top at the height where it ended, the climber on the wall's side.</summary>
    private List<Vec3> ClimbFromDrag(GameplayHit s, GameplayHit e)
    {
        Vec3 n = s.Normal is { } wall && Math.Abs(wall.Z) < 0.7
            ? new Vec3(wall.X, wall.Y, 0).Normalized()
            : new Vec3(s.Point.X - e.Point.X, s.Point.Y - e.Point.Y, 0).Normalized();
        Vec3 foot = s.Point + n * 2;
        if (s.Normal is { } sn && Math.Abs(sn.Z) < 0.7 && _state.World.DropBelow(foot + n * 16 + Vec3.UnitZ, 4096) is { } drop)
        {
            foot = foot with { Z = foot.Z + 1 - drop };
        }

        return GeShapes.ClimbPoints(foot, Math.Max(e.Point.Z, s.Point.Z), n == Vec3.Zero ? Vec3.UnitX : n);
    }

    private static double DistanceXY(Lip l, Vec3 p)
    {
        Vec3 d = new Vec3(l.B.X - l.A.X, l.B.Y - l.A.Y, 0);
        double len2 = d.LengthSquared;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(Vec3.Dot(new Vec3(p.X - l.A.X, p.Y - l.A.Y, 0), d) / len2, 0, 1);
        Vec3 q = l.A + d * t;
        return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
    }

    private void Reset()
    {
        _start = null;
        _end = null;
        _lip = null;
        _hint = "";
        _lastQuery = null;
    }

    /// <summary>Runs a command, selects what it added and says so (warnings also go to the log).</summary>
    private void Commit(AddObjectsCommand cmd, string status, bool warn = false)
    {
        _tools.Context.Execute(cmd);
        _tools.Context.Selection.Set(cmd.Added);
        _tools.Context.SetStatus(status);
        if (warn)
        {
            _tools.Context.Log(status, Severity.Warning);
        }
    }
}
