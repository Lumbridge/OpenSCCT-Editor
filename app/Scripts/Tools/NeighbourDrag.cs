using System.Diagnostics;
using Godot;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Render;
using WorldGeometry = OpenScct.Editor.Core.Scripting.WorldGeometry;

namespace OpenScctEditor.Tools;

/// <summary>
/// Neighbour snap for one drag (move, scale, turn, face extrude, builder box): the selection's shape at the start,
/// the neighbours' faces (built the first time the drag asks, so a drag with the feature off builds nothing), the
/// faces it stuck to last step (they hold a little longer) and what to show: the neighbour face highlighted, the
/// moving face outlined, a guide along the shared plane, chips in the readout. The snap distance is measured in the
/// drag's view at the moving shape, rebuilt every step, so zooming mid-drag changes it. Everything runs on the main
/// thread inside the drag step; only reading an imported map's baked collision runs on <see cref="BackgroundJobs"/>,
/// and a drag never waits for it.
/// </summary>
public sealed class NeighbourDrag
{
    /// <summary>A step whose solve takes longer than this drops the baked world for the rest of the drag.</summary>
    private const double SlowStepMs = 2;

    private const string BakedJob = "Collision for neighbour snap";

    private readonly ToolManager _tools;
    private readonly EditorViewport _view;
    private readonly SnapUse _use;
    private readonly HashSet<string> _exclude;
    private readonly Func<MovingShape?> _makeShape;
    private readonly List<(SnapMatch Match, SnapFace? Moving)> _shown = new();
    private MovingShape? _shape;
    private bool _shapeMade;
    private SnapNeighbours? _neighbours;
    private bool _bakedOff;
    private IReadOnlyList<SnapMatch> _stuck = [];
    private TurnSnap? _turn;
    private (Vec3 Pivot, double Radius) _turnAt;

    /// <param name="exclude">Ids left out of the neighbours (the selection, the brushes being extruded).</param>
    /// <param name="shape">Makes the selection's shape at the start (asked once, when the snap is first in effect).</param>
    public NeighbourDrag(ToolManager tools, EditorViewport view, SnapUse use, IEnumerable<string> exclude, Func<MovingShape?> shape)
    {
        _tools = tools;
        _view = view;
        _use = use;
        _exclude = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        _makeShape = shape;
    }

    /// <summary>True while Alt is held (it inverts the toggle for this drag).</summary>
    public bool AltHeld { get; set; }

    /// <summary>True when this drag sticks to neighbours now.</summary>
    public bool Effective => _tools.NeighbourSnapFor(_use, AltHeld);

    /// <summary>The selection's shape at the start of the drag, or null when it has none (GEs only).</summary>
    public MovingShape? Shape
    {
        get
        {
            if (!_shapeMade)
            {
                _shapeMade = true;
                _shape = _makeShape();
            }

            return _shape;
        }
    }

    /// <summary>True when the last step stuck to something.</summary>
    public bool Stuck => _shown.Count > 0 || _turn is not null;

    /// <summary>Starts reading a map's baked collision on a worker when it keeps one that is not read yet.</summary>
    public static void PrepareBaked(EditorContext ctx)
    {
        MapProject p = ctx.Project;
        string? folder = ctx.Session.Folder;
        if (!WorldGeometry.BakedCollisionPending(p, folder) || BackgroundJobs.IsRunning(BakedJob))
        {
            return;
        }

        BackgroundJobs.Run(BakedJob, () => WorldGeometry.LoadBakedCollision(p, folder) is not null, _ => { });
    }

    // ---- solves --------------------------------------------------------------------------------------------------

    /// <summary>Sticks a move of <paramref name="raw"/> (unsnapped) on the free axes; <see cref="MoveSnap.None"/>
    /// when the snap is not in effect. Call <see cref="Moved"/> with the offset applied.</summary>
    public MoveSnap Move(Vec3 raw, IReadOnlyList<int> free) => Effective && Shape is { } shape ? Move(shape, raw, free) : Nothing();

    /// <summary>Sticks a move of a shape given now (the builder box, whose shape changes as it is dragged).</summary>
    public MoveSnap Move(MovingShape shape, Vec3 raw, IReadOnlyList<int> free)
    {
        Clear();
        if (!Effective || Neighbours() is not { } n)
        {
            return MoveSnap.None;
        }

        var watch = Stopwatch.StartNew();
        MoveSnap snap = NeighbourSnap.Move(shape, raw, free, n, ViewAt(shape.Center + raw), _stuck, baked: !_bakedOff);
        Slow(watch, n);
        _stuck = snap.Matches;
        foreach (SnapMatch m in snap.Matches)
        {
            _shown.Add((m, m.Moving));
        }

        return snap;
    }

    private MoveSnap Nothing()
    {
        Clear();
        return MoveSnap.None;
    }

    /// <summary>Moves the outlined moving faces to where the step put them (after grid snap on the other axes).</summary>
    public void Moved(Vec3 offset)
    {
        for (int i = 0; i < _shown.Count; i++)
        {
            _shown[i] = (_shown[i].Match, _shown[i].Match.Moving?.Offset(offset));
        }
    }

    /// <summary>Sticks a scale about <paramref name="pivot"/> (axis -1: uniform), or null.</summary>
    public ValueSnap? Scale(Vec3 pivot, int axis, double rawFactor)
    {
        Clear();
        if (!Effective || Shape is not { } shape || Neighbours() is not { } n)
        {
            return null;
        }

        var watch = Stopwatch.StartNew();
        ValueSnap? snap = NeighbourSnap.Scale(shape, pivot, axis, rawFactor, n, ViewAt(shape.Center), _stuck.FirstOrDefault(), baked: !_bakedOff);
        Slow(watch, n);
        _stuck = snap is null ? [] : [snap.Match];
        if (snap is not null)
        {
            int[] axes = axis >= 0 ? [axis] : [0, 1, 2];
            Vec3 Scaled(Vec3 p)
            {
                foreach (int k in axes)
                {
                    p = p.With(k, pivot[k] + (p[k] - pivot[k]) * snap.Value);
                }

                return p;
            }

            SnapFace? moving = snap.Match.Moving is { } m
                ? SnapFace.Of(m.Owner, m.Face, m.Source, m.Polygon.Select(Scaled).ToArray(), m.Approximate, m.Normal)
                : null;
            _shown.Add((snap.Match, moving));
        }

        return snap;
    }

    /// <summary>Dimensions with fixed profiles (nosing, frames and landings) do not scale as a plain box.</summary>
    public ValueSnap? ParametricScale(int axis, double rawFactor, Func<double, MovingShape> shapeAt)
    {
        Clear();
        if (!Effective || Neighbours() is not { } neighbours) return null;
        try
        {
            var cache = new Dictionary<double, MovingShape>();
            MovingShape At(double f) { if (!cache.TryGetValue(f, out var s)) cache[f] = s = shapeAt(f); return s; }
            MovingShape raw = At(rawFactor), next = At(rawFactor + .001);
            ValueSnap? best = null; double score = double.MaxValue;
            foreach (SnapFace face in raw.Faces.Where(f => axis < 0 || f.Axis == axis))
            {
                int k = face.Axis; bool positive = face.Normal[k] > 0;
                double Coord(MovingShape s) => positive ? s.Max[k] : s.Min[k];
                double derivative = (Coord(next) - Coord(raw)) / .001;
                if (Math.Abs(derivative) < 1e-6) continue;
                var view = ViewAt(face.Center);
                var snap = NeighbourSnap.Extrude(face, 0, neighbours, view, _stuck.FirstOrDefault(), _exclude, baked: !_bakedOff);
                if (snap is null || snap.Match.Target.Axis != k) continue;
                double factor = rawFactor + snap.Value * face.Normal[k] / derivative;
                if (factor < .125) continue;
                double distance = Math.Abs(snap.Value) * view.PixelsPerUnitAlong(face.Normal);
                if (distance >= score) continue;
                MovingShape final = At(factor);
                double error = snap.Match.Target.Coord - Coord(final);
                if (Math.Abs(error) > .01)
                {
                    factor += error / derivative;
                    if (factor < .125) continue;
                    final = At(factor);
                    if (Math.Abs(snap.Match.Target.Coord - Coord(final)) > .01) continue;
                }
                SnapFace moved = final.Faces.First(f => f.Axis == k && f.Normal[k] * face.Normal[k] > 0);
                best = new ValueSnap(factor, snap.Match with { Moving = moved }); score = distance;
            }
            _stuck = best is null ? [] : [best.Match];
            if (best is not null) _shown.Add((best.Match, best.Match.Moving));
            return best;
        }
        catch (ArgumentException) { Release(); return null; }
    }

    /// <summary>Sticks a face extrude of the grabbed face, or null; distances at or below <paramref name="minDistance"/>
    /// (through the brush) are not offered.</summary>
    public ValueSnap? Extrude(SnapFace grabbed, double rawDistance, double minDistance = double.NegativeInfinity)
    {
        Clear();
        if (!Effective || Neighbours() is not { } n)
        {
            return null;
        }

        var watch = Stopwatch.StartNew();
        ValueSnap? snap = NeighbourSnap.Extrude(grabbed, rawDistance, n, ViewAt(grabbed.Center + grabbed.Normal * rawDistance),
            _stuck.FirstOrDefault(), _exclude, baked: !_bakedOff, minDistance);
        Slow(watch, n);
        _stuck = snap is null ? [] : [snap.Match];
        if (snap is not null)
        {
            _shown.Add((snap.Match, grabbed.Offset(grabbed.Normal * snap.Value)));
        }

        return snap;
    }

    /// <summary>Sticks a turn about a world axis through <paramref name="pivot"/>; <paramref name="ownRotation"/> is the
    /// primary object's rotation at the start (for matching a neighbour's rotation exactly). Turns landing on the
    /// rotation grid (<paramref name="grid"/> units, 0 for none) are left to it.</summary>
    public TurnSnap? Turn(int axis, Vec3 pivot, int rawUnits, int threshold, Rot? ownRotation, int grid = 0)
    {
        Clear();
        if (!Effective || Shape is not { } shape || Neighbours() is not { } n)
        {
            return null;
        }

        // +1 when a positive turn carries the next axis towards the one after it.
        int u = (axis + 1) % 3, v = (axis + 2) % 3;
        int sense = RotationMath.About(axis, Rot.UnitsPerTurn / 4).Rotate(Vec3.Zero.With(u, 1))[v] >= 0 ? 1 : -1;
        double radius = Math.Max((shape.Max - pivot).Length, (shape.Min - pivot).Length);
        MapProject p = _tools.Context.Project;
        TurnSnap? snap = NeighbourSnap.Turn(shape.Directions, ownRotation, axis, sense, pivot, radius, rawUnits, threshold, n,
            _stuck.FirstOrDefault(), r => p.FindActor(r.Id)?.Rotation, grid);
        _stuck = snap is null ? [] : [snap.Match];
        _turn = snap;
        _turnAt = (pivot, Math.Max(radius, 64));
        return snap;
    }

    /// <summary>Forgets what the last step showed (the stuck faces stay for the hold).</summary>
    public void Clear()
    {
        _shown.Clear();
        _turn = null;
        Note = null;
    }

    /// <summary>Why this step could not stick although the snap is on ("turned selection, scale does not stick"),
    /// shown dim in the readout; the tool sets it after the step.</summary>
    public string? Note { get; set; }

    /// <summary>Forgets the faces held from the last step too (the snap was turned off or skipped).</summary>
    public void Release()
    {
        Clear();
        _stuck = [];
    }

    // ---- feedback ------------------------------------------------------------------------------------------------

    /// <summary>The readout lines under the drag's own line: the Alt override first (only when it changes what this
    /// drag does), then one line per stuck axis ("X Kerb1 touching", with "off grid" and "(approximate)" when they
    /// apply), the turn's match, or the note saying why nothing can stick.</summary>
    public TagPart[][] Readout()
    {
        var lines = new List<TagPart[]>();
        if (AltHeld && _tools.NeighbourSnapFor(_use, true) != _tools.NeighbourSnapFor(_use, false))
        {
            lines.Add([new TagPart(_tools.NeighbourSnap ? "Neighbours off (Alt)" : "Neighbours on (Alt)", EditorTheme.TextDim)]);
        }

        foreach ((SnapMatch m, _) in _shown)
        {
            var parts = new List<TagPart>();
            if (m.Axis >= 0)
            {
                parts.Add(new TagPart("XYZ"[m.Axis] + " ", SceneStyle.AxisColor(m.Axis)));
            }

            parts.Add(new TagPart(ViewTags.Shorten(m.Target.Name), EditorTheme.Text));
            parts.Add(new TagPart(m.Relation == SnapRelation.Touching ? " touching" : " (in line)", EditorTheme.TextDim));
            if (OffGrid(m))
            {
                parts.Add(new TagPart(" off grid", EditorTheme.Warning));
            }

            if (m.Target.Approximate || m.Moving?.Approximate == true)
            {
                parts.Add(new TagPart(" (approximate)", EditorTheme.Warning));
            }

            lines.Add([.. parts]);
        }

        if (_turn is { } t)
        {
            lines.Add(
            [
                new TagPart("lines up with ", EditorTheme.TextDim),
                new TagPart(ViewTags.Shorten(t.Match.Target.Name), EditorTheme.Text),
                new TagPart(t.Exact ? " (its rotation)" : $" ({NeighbourSnap.Surface(t.Match.Target.Normal)})", EditorTheme.TextDim),
            ]);
        }

        if (Note is { } note && Effective)
        {
            lines.Add([new TagPart("Neighbours: " + note, EditorTheme.TextDim)]);
        }

        return [.. lines];
    }

    /// <summary>What the step stuck to, for the status bar ("stuck X to Kerb1 +X face (touching)").</summary>
    public string Status()
    {
        IEnumerable<string> parts = _shown.Select(s => NeighbourSnap.Describe(s.Match) + (OffGrid(s.Match) ? ", off grid" : ""));
        if (_turn is { } t)
        {
            parts = parts.Append(NeighbourSnap.DescribeTurn(t));
        }

        string text = string.Join(" · ", parts);
        return text.Length > 0 ? " · " + text : "";
    }

    /// <summary>Draws the stuck faces in a view: a touched face filled and outlined with the moving face outlined over
    /// it; a face lined up with outlined, with a dashed guide across the gap; a turn's face outlined with a guide
    /// through the pivot along the lined-up direction.</summary>
    public void Draw(EditorViewport view, Control canvas)
    {
        ViewCamera cam = view.Cam;
        int? depth = cam.IsOrtho ? ViewAxes.DepthAxis(cam.Kind) : null;
        float width = cam.IsOrtho ? 3 : 2;
        var fill = new Color(SceneStyle.SnapTarget, SceneStyle.SnapFillAlpha);
        var line = new Color(SceneStyle.SnapTarget, SceneStyle.SnapLineAlpha);
        foreach ((SnapMatch m, SnapFace? moving) in _shown)
        {
            bool touching = m.Relation == SnapRelation.Touching;
            // A 2D view looking straight at the face gets its outline only: a room's whole wall filled would wash the view.
            bool faceOn = depth is int fd && Math.Abs(m.Target.Normal[fd]) >= 0.7;
            FacePicking.DrawPolygon(cam, canvas, m.Target.Polygon, touching && !faceOn ? fill : null, line, width);
            if (moving is null)
            {
                continue;
            }

            if (touching)
            {
                FacePicking.DrawPolygon(cam, canvas, moving.Polygon, null, line, cam.IsOrtho ? 2 : 1.5f);
            }
            else if (depth is not int d || Math.Abs(m.Target.Normal[d]) < 0.7)
            {
                // Not in a 2D view that sees the shared plane face on, where the guide would be a meaningless diagonal.
                Guide(cam, canvas, m.Target.Center, moving.Center);
            }
        }

        if (_turn is { } t)
        {
            FacePicking.DrawPolygon(cam, canvas, t.Match.Target.Polygon, null, line, width);
            Guide(cam, canvas, _turnAt.Pivot - t.Direction * _turnAt.Radius, _turnAt.Pivot + t.Direction * _turnAt.Radius);
        }
    }

    /// <summary>A dashed guide between two world points, 16 px longer at each end.</summary>
    private static void Guide(ViewCamera cam, Control canvas, Vec3 a, Vec3 b)
    {
        if (cam.ToScreen(a) is not { } sa || cam.ToScreen(b) is not { } sb)
        {
            return;
        }

        Vector2 d = sb - sa;
        if (d.LengthSquared() < 1)
        {
            return;
        }

        Vector2 dir = d.Normalized() * 16;
        canvas.DrawDashedLine(sa - dir, sb + dir, SceneStyle.SnapGuide, 1.5f, 6);
    }

    // ---- internals -----------------------------------------------------------------------------------------------

    /// <summary>True when the plane stuck to is off the grid (with grid snap on): the result is kept exact, and said.</summary>
    private bool OffGrid(SnapMatch m)
    {
        if (!_tools.GridSnap)
        {
            return false;
        }

        if (!m.Target.IsAxis)
        {
            return true;
        }

        double g = _tools.GridSize;
        return Math.Abs(m.Target.Coord / g - Math.Round(m.Target.Coord / g)) * g > 1e-6;
    }

    /// <summary>The neighbours, built once per drag: visible brushes (not the excluded ones, nor non-solid, portal
    /// and anti-portal helpers), actors by the target settings, and the baked world when it is read and wanted.</summary>
    private SnapNeighbours? Neighbours()
    {
        if (_neighbours is not null)
        {
            return _neighbours;
        }

        MapProject p = _tools.Context.Project;
        bool meshes = _tools.NeighbourSnapMeshes, actors = _tools.NeighbourSnapActors;
        ISnapSweeper? baked = null;
        // The baked world sticks only while it is shown (View > Show > Baked World).
        if (_tools.NeighbourSnapBaked && ViewportLayout.Instance?.Scene.ShowBaked != false)
        {
            if (WorldGeometry.CachedBakedCollision(p, _tools.Context.Session.Folder) is { } world)
            {
                baked = new BakedSweeper(world);
            }
            else
            {
                PrepareBaked(_tools.Context);
            }
        }

        IEnumerable<SnapFace> brushFaces = p.Brushes
            .Where(b => !_exclude.Contains(b.Id) && !b.IsNotSolid && !b.IsPortal && (b.PolyFlags & PolyFlags.AntiPortal) == 0
                        && ObjectFilter.IsVisible(p, b.Editor))
            .SelectMany(ActorSnapBox.Faces);
        if (_tools.NeighbourSnapVolumes)
        {
            // Volumes and other brush-shaped actors, by their own faces.
            brushFaces = brushFaces.Concat(p.Actors
                .Where(a => a.Shape is not null && !_exclude.Contains(a.Id) && ObjectFilter.IsVisible(p, a.Editor))
                .SelectMany(SnapFaces.OfShape));
        }

        IEnumerable<(ObjectRef, Vec3)> actorSpots = meshes || actors
            ? p.Actors.Where(a => a.Shape is null && !_exclude.Contains(a.Id) && ObjectFilter.IsVisible(p, a.Editor))
                .Select(a => (ObjectRef.Actor(a.Id), a.Location))
            : [];
        _neighbours = SnapNeighbours.Build(brushFaces, actorSpots, r =>
        {
            if (p.FindActor(r.Id) is not { } a || ActorSnapBox.Of(a) is not { } box
                || (box.Source == SnapSource.Mesh ? !meshes : !actors))
            {
                return [];
            }

            return SnapFaces.OfActor(a, box);
        }, baked);
        return _neighbours;
    }

    private void Slow(Stopwatch watch, SnapNeighbours n)
    {
        if (!_bakedOff && n.Baked is not null && watch.Elapsed.TotalMilliseconds > SlowStepMs)
        {
            _bakedOff = true;
        }
    }

    /// <summary>How the drag's view measures distance at a point.</summary>
    private SnapView ViewAt(Vec3 c)
    {
        ViewCamera cam = _view.Cam;
        double upp = cam.UnitsPerPixelAt(c);
        double step = 16 * upp;
        double Along(Vec3 dir)
        {
            if (cam.ToScreen(c) is { } a && cam.ToScreen(c + dir * step) is { } b)
            {
                return a.DistanceTo(b) / step;
            }

            return 1 / Math.Max(upp, 1e-9);
        }

        var ppu = new Vec3(Along(Vec3.UnitX), Along(Vec3.UnitY), Along(Vec3.UnitZ));
        return new SnapView(_tools.NeighbourSnapPx, ppu, upp, cam.IsOrtho ? ViewAxes.DepthAxis(cam.Kind) : null, Along)
        {
            InLine = _tools.NeighbourSnapInLine,
        };
    }

    // ---- the moving shape ----------------------------------------------------------------------------------------

    /// <summary>
    /// The shape a selection sticks by: one brush (or shaped actor) by its own faces, one actor by its box, several
    /// objects by their combined box (plus the primary's faces when it is one small brush). GEs add nothing; a
    /// selection of GEs alone has no shape.
    /// </summary>
    public static MovingShape? ShapeOf(TransformSnapshot snap, ObjectRef? primary)
    {
        List<ObjectRef> refs = snap.Refs.Where(r => r.Kind != ObjectKind.Ge).ToList();
        if (refs.Count == 0)
        {
            return null;
        }

        if (refs.Count == 1)
        {
            return Single(snap, refs[0]);
        }

        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (ObjectRef r in refs)
        {
            if (BoundsAtStart(snap, r) is { } b)
            {
                min = Vec3.Min(min, b.Min);
                max = Vec3.Max(max, b.Max);
            }
        }

        if (min.X > max.X)
        {
            return null;
        }

        MovingShape box = MovingShape.Box(min, max);
        ObjectRef main = primary is { Kind: not ObjectKind.Ge } pr && refs.Contains(pr) ? pr : refs[0];
        MovingShape? own = refs.Count <= 400 ? Single(snap, main) : null;
        IReadOnlyList<SnapFace> faces = box.Faces;
        if (own is not null && main.Kind is (ObjectKind.Brush or ObjectKind.Stair) && own.Faces.Count <= SnapFaces.MaxMovingFaces)
        {
            faces = [.. box.Faces, .. own.Faces];
        }

        return new MovingShape(min, max, faces, own?.Directions ?? box.Directions);
    }

    private static MovingShape? Single(TransformSnapshot snap, ObjectRef r)
    {
        switch (r.Kind)
        {
            case ObjectKind.Brush or ObjectKind.Stair when snap.StartBrush(r.Id) is { } b:
                return SnapFaces.Shape(ActorSnapBox.Faces(b), r);
            case ObjectKind.Actor when snap.StartActor(r.Id) is { Shape: not null } a:
                return SnapFaces.Shape(SnapFaces.OfShape(a), r);
            case ObjectKind.Actor when snap.StartActor(r.Id) is { } a:
            {
                SnapBox box = ActorSnapBox.Of(a) ?? new SnapBox(OrientedBox.Aligned(a.Location, a.Location), SnapSource.Actor, true);
                (Vec3 min, Vec3 max) = box.Box.Bounds();
                (Vec3 f, Vec3 right, Vec3 up) = a.Rotation.Axes();
                var dirs = new List<Vec3>();
                foreach (Vec3 d in new[] { f, right, up, box.Box.X, box.Box.Y, box.Box.Z })
                {
                    if (!dirs.Any(x => Math.Abs(Vec3.Dot(x, d)) >= SnapFace.ParallelCos))
                    {
                        dirs.Add(d);
                    }
                }

                return new MovingShape(min, max, box.Box.Faces(r, box.Source, box.Approximate), dirs);
            }

            default:
                return null;
        }
    }

    private static (Vec3 Min, Vec3 Max)? BoundsAtStart(TransformSnapshot snap, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Brush or ObjectKind.Stair when snap.StartBrush(r.Id) is { } b => (BrushGeometry.Of(b).Min, BrushGeometry.Of(b).Max),
        ObjectKind.Actor when snap.StartActor(r.Id) is { Shape: not null } a => ObjectGeometry.ActorBounds(a),
        ObjectKind.Actor when snap.StartActor(r.Id) is { } a => ActorSnapBox.Of(a) is { } box ? box.Box.Bounds() : ObjectGeometry.ActorBounds(a),
        _ => null,
    };

    /// <summary>
    /// Why a scale of this selection cannot stick, or null when it can: every object must be turned by whole quarter
    /// turns only (so a scale along a world axis is a pure scale of its box and a stuck face lands where it is drawn),
    /// and every actor must be drawn as a mesh or have a shape (a point actor's collision box does not scale).
    /// </summary>
    public static string? ScaleBlocker(TransformSnapshot snap)
    {
        static bool Square(Rot r) => r.Pitch % 16384 == 0 && r.Yaw % 16384 == 0 && r.Roll % 16384 == 0;
        foreach (ObjectRef r in snap.Refs)
        {
            if (r.Kind is (ObjectKind.Brush or ObjectKind.Stair) && snap.StartBrush(r.Id) is { } b && !Square(b.Rotation))
            {
                return "turned selection, scale does not stick";
            }

            if (r.Kind == ObjectKind.Actor && snap.StartActor(r.Id) is { } a)
            {
                if (!Square(a.Rotation))
                {
                    return "turned selection, scale does not stick";
                }

                if (a.Shape is null && StaticMeshDrawing.MeshPath(a) is null)
                {
                    return "actor without a mesh, scale does not stick";
                }
            }
        }

        return null;
    }
}
