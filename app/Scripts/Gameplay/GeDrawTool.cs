using Godot;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The GE tool (T). Ledge grabs: hover a top edge to see the lip the detector finds there (with why not, when there is
/// none) and click to add an LG flush on it; Shift+drag draws one freehand. Other kinds accept a drag or two clicks,
/// with matching parametric geometry. The same generator previews and builds the visible parts. Selected endpoints
/// can be dragged; pending drawings and edits do not change the map until committed, and Escape cancels either.
/// </summary>
public sealed class GeDrawTool : IViewportTool
{
    private const double LedgePickPixels = 24;

    private readonly ToolManager _tools;
    private readonly GameplayState _state;
    private EditorViewport? _view;
    private Vector2 _mouse;
    private GameplayHit? _start;
    private GameplayHit? _end;
    private Lip? _lip;
    private string _hint = "";
    private TraversalWorld? _edgeWorld;
    private LedgeOptions? _edgeOptions;
    private IReadOnlyList<(Vec3 A, Vec3 B, Vec3 Inner)> _ledgeEdges = [];
    private int _hoverCamera;
    private Vector2 _hoverAt = new(float.PositiveInfinity, 0);
    private Vector2 _pressedAt;
    private bool _pressed;
    private bool _moved;
    private Ge? _editing;
    private Ge? _draft;
    private int _pointIndex;
    private NeighbourDrag? _handleSnap;
    private ScreenEdgeHit? _edgeTarget;
    private int? _previewShapeKey;
    private readonly List<(Vec3 A, Vec3 B)> _previewEdges = [];

    public GeDrawTool(ToolManager tools, GameplayState state)
    {
        _tools = tools;
        _state = state;
        tools.DragInterrupted += Reset;
        tools.Changed += ResolveHandle;
    }

    public string Id => "ge";

    public string Title => "Draw GE";

    public string Shortcut => "T";

    public string Description => "Draw ladders, pipes, poles, zip lines, overhead bars and fences with matching geometry; drag endpoints to reshape";

    // POLE is a legacy marker with no runtime grab action. The creator offers PIPE for climbable poles.
    private static readonly string[] DrawingKinds = GeKinds.All.Where(k => k != GeKinds.Pole).ToArray();
    private string Kind => _tools.GeKind == GeKinds.Pole ? GeKinds.Pipe : _tools.GeKind;

    private bool Geometry
    {
        get => AppSettings.GetBool("tools", "ge_geometry", true);
        set => AppSettings.Set("tools", "ge_geometry", value);
    }

    private double Setting(string name, double fallback) => AppSettings.GetDouble("tools", $"ge_{Kind}_{name}", fallback);

    private GeAppearance Appearance()
    {
        GeAppearance a = GeGeometry.Defaults(Kind);
        a.Width = Setting("width", a.Width);
        a.Radius = Setting("radius", a.Radius);
        a.Spacing = Setting("spacing", a.Spacing);
        return a;
    }

    /// <summary>Builds the collision world in the background, so the first hover does not wait for it.</summary>
    public void Activate() => _state.Prewarm();

    public void Deactivate() => Reset();

    public bool IsDragging => _editing is not null && _moved;

    /// <summary>Selection-tool editing only: consume selected endpoint drags, never start drawing a new GE.</summary>
    public bool InputSelected(EditorViewport view, InputEvent e)
    {
        if (_editing is not null) return InputCore(view, e, selectedOnly: true);
        return e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, ShiftPressed: false, CtrlPressed: false, MetaPressed: false } press
            && HandleAt(view, press.Position, selectedOnly: true) is not null && InputCore(view, e, selectedOnly: true);
    }

    public bool Input(EditorViewport view, InputEvent e) => InputCore(view, e);

    private bool InputCore(EditorViewport view, InputEvent e, bool selectedOnly = false)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _view = view;
                _mouse = press.Position;
                _pressedAt = press.Position;
                _pressed = true;
                _moved = false;
                if (_start is not null)
                {
                    _end = Hit(view, press.Position);
                    Finish();
                    return true;
                }

                if (!press.ShiftPressed && HandleAt(view, press.Position, selectedOnly) is { } handle)
                {
                    _editing = handle.Ge.Clone();
                    _draft = handle.Ge.Clone();
                    _pointIndex = handle.Index;
                    bool facing = GeKinds.HasThirdPoint(_editing.Kind) && _pointIndex == 2;
                    Vec3 anchor = _editing.Points[_pointIndex];
                    _handleSnap = new NeighbourDrag(_tools, view, facing ? SnapUse.Rotate : _pointIndex == 0 ? SnapUse.Move : SnapUse.Resize,
                        [_editing.Id], () => facing ? SnapFaces.OfGe(_editing) : MovingShape.Box(anchor, anchor, ObjectRef.Ge(_editing.Id)))
                    { AltHeld = press.AltPressed };
                    _tools.Context.Selection.Set([ObjectRef.Ge(handle.Ge.Id)]);
                    return true;
                }

                if (Kind == GeKinds.LedgeGrab && !press.ShiftPressed)
                {
                    AddHoveredLedge(view);
                    return true;
                }

                _start = Hit(view, press.Position);
                _end = _start;
                view.Redraw();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _pressed = false;
                if (_editing is { } before && _draft is { } after)
                {
                    if (_moved)
                    {
                        var command = new ReplaceObjectsCommand($"Reshape {before.Id}");
                        command.Ge(before, after);
                        Reset();
                        _tools.Context.Execute(command);
                        _tools.Context.SetStatus($"Reshaped {after.Id}");
                    }
                    else
                    {
                        Reset();
                    }
                }
                else if (_start is not null && _moved)
                {
                    _end = Hit(view, ((InputEventMouseButton)e).Position);
                    Finish();
                }

                ViewportLayout.Instance?.RedrawAll();
                return true;
            case InputEventMouseMotion motion:
                _view = view;
                _mouse = motion.Position;
                _moved |= _pressed && _mouse.DistanceTo(_pressedAt) >= 4;
                if (_editing is not null && _moved)
                {
                    _handleSnap!.AltHeld = motion.AltPressed || Godot.Input.IsKeyPressed(Key.Alt);
                    MoveHandle(view, motion.Position);
                }
                else if (_start is not null)
                {
                    _end = Hit(view, motion.Position);
                }
                else if (Kind == GeKinds.LedgeGrab)
                {
                    Hover(view);
                }

                ViewportLayout.Instance?.RedrawAll();
                return _start is not null || _editing is not null;
            case InputEventKey { Keycode: Key.Alt } alt when _handleSnap is not null:
                _handleSnap.AltHeld = alt.Pressed;
                ResolveHandle();
                return true;
            case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } when _start is not null:
                Finish();
                return true;
            case InputEventKey { Pressed: true, Keycode: Key.Backspace } when _start is not null:
                Reset();
                ViewportLayout.Instance?.RedrawAll();
                return true;
        }

        return false;
    }

    public void DrawSelected(EditorViewport view, Control canvas) => DrawCore(view, canvas, selectedOnly: true);

    public void Draw(EditorViewport view, Control canvas) => DrawCore(view, canvas);

    private void DrawCore(EditorViewport view, Control canvas, bool selectedOnly = false)
    {
        if (!selectedOnly && Kind == GeKinds.LedgeGrab && _editing is null && _start is null && view == _view) Hover(view);
        var batch = new OverlayBatch();
        Color c = WorldScene.GeColor(Kind);
        List<string> lines = [];
        foreach (Ge ge in Shown(selectedOnly))
        {
            foreach (Vec3 point in ge.Points)
            {
                batch.Dot(point, SceneStyle.Hover, 5);
            }
        }

        if (_draft is { } draft)
        {
            Preview(batch, draft);
            lines.Add($"Reshape {draft.Id}");
        }
        if (_edgeTarget is { } target)
        {
            batch.Dot(target.Point, SceneStyle.SnapTarget, 7);
            batch.Label(target.Point, target.Vertex ? "Corner" : "Edge", SceneStyle.SnapTarget, pinned: true);
        }

        if (_start is { } s && _end is { } e && Points(s, e) is { } pts)
        {
            batch.Polyline(pts.Take(2).ToList(), c, 3);
            if (pts.Count > 2)
            {
                batch.Arrow(pts[0], pts[2] + (pts[2] - pts[0]) * 2, c, 2);
            }

            lines.Add($"{Kind} {(pts[1] - pts[0]).Length:0} long");
            lines.AddRange(GeShapes.Problems(Kind, pts).Select(p => p.Message));
            if (Geometry && GeGeometry.Supports(Kind))
            {
                Preview(batch, new Ge("Preview", Kind) { Points = pts, Appearance = Appearance() });
            }
        }
        else if (!selectedOnly && _lip is { } lip)
        {
            batch.Line(lip.A, lip.B, SceneStyle.Hover, 4);
            batch.Arrow(lip.Mid, lip.Mid + lip.Out * 24, SceneStyle.Hover, 2);
            lines.Add(_hint);
        }
        else if (!selectedOnly && _hint.Length > 0)
        {
            lines.Add(_hint);
        }

        batch.Draw(view.Cam, canvas);
        _handleSnap?.Draw(view, canvas);
        // The first line says what is drawn (in the GE's colour), the rest are the shape checks' warnings.
        if (view == _view)
        {
            ViewTags.BesideLines(canvas, _mouse, lines.Select((line, i) => new TagPart(line, i == 0 ? c : EditorTheme.Warning)));
            if (_handleSnap is { } snap)
                ViewTags.BesideStack(canvas, _mouse + new Vector2(0, 24), snap.Readout());
        }
    }

    public bool Cancel()
    {
        if (_start is null && _lip is null && _editing is null)
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
        OptionButton kinds = ToolOptions.Options(bar, "Kind of GE to draw", 180);
        for (int i = 0; i < DrawingKinds.Length; i++)
        {
            kinds.AddItem(KindTitle(DrawingKinds[i]), i);
            if (DrawingKinds[i] == Kind)
            {
                kinds.Select(i);
            }
        }

        kinds.ItemSelected += i =>
        {
            _tools.GeKind = DrawingKinds[(int)i];
            Reset();
            _tools.RefreshOptions();
        };
        if (GeGeometry.Supports(Kind))
        {
            PanelUi.Toggle(bar, "Geometry", "Create matching visible geometry that follows the GE", Geometry, on =>
            {
                Geometry = on;
                _tools.RefreshOptions();
                ViewportLayout.Instance?.RedrawAll();
            });
            if (Kind is GeKinds.Ladder or GeKinds.Pipe or GeKinds.Pole)
            {
                Number(bar, "Height", "Height when drawing on a floor; side-view drawing uses the endpoints", "height", 256, 4, 8192);
            }

            if (GeKinds.HasThirdPoint(Kind))
            {
                Number(bar, "Facing", "Climber side in degrees; drawing on a wall uses its outward normal", "facing", 0, -180, 180);
            }

            if (Kind is GeKinds.HandOverHand or GeKinds.ZipLine or GeKinds.Fence)
            {
                Number(bar, "Lift", "Height above a clicked floor (side views place endpoints directly)", "lift", DefaultLift, 0, 8192);
            }

            if (Kind == GeKinds.ZipLine)
            {
                Number(bar, "Drop", "Extra drop at the second endpoint when drawing on floors", "drop", 64, 0, 8192);
            }

            if (Geometry)
            {
                GeAppearance defaults = GeGeometry.Defaults(Kind);
                if (Kind is GeKinds.Ladder or GeKinds.Fence)
                {
                    Number(bar, Kind == GeKinds.Fence ? "Height" : "Width", "Visible geometry dimensions", "width", defaults.Width, 4, 512);
                    Number(bar, "Spacing", "Distance between rungs or fence uprights", "spacing", defaults.Spacing, 4, 256);
                }

                Number(bar, "Radius", "Radius of the rails, pole, bar or cable", "radius", defaults.Radius, 0.5, 32);
            }
        }

        ToolOptions.Hint(bar, Kind == GeKinds.LedgeGrab ? "Click edge; Shift+drag: freehand" : "Two clicks or drag",
            Kind == GeKinds.Pole ? "For climbing use pipe / pole" : "Drag dots to edit", "Esc: cancel");
    }

    private static string KindTitle(string kind) => kind switch
    {
        GeKinds.LedgeGrab => "Ledge grab",
        GeKinds.ZipLine => "Zip line",
        GeKinds.Pipe => "Climbable pipe / pole",
        GeKinds.Ladder => "Ladder",
        GeKinds.HandOverHand => "Hand over hand",
        GeKinds.Pole => "Pole marker",
        GeKinds.Fence => "Fence",
        _ => kind,
    };

    private double DefaultLift => Kind == GeKinds.HandOverHand ? 277 : Kind == GeKinds.ZipLine ? 304 : 256;

    private void Number(HBoxContainer bar, string label, string hint, string key, double fallback, double min, double max)
    {
        ToolOptions.Name(bar, label);
        SpinBox box = PanelUi.Spin(bar, min, max, key == "radius" ? 0.5 : 1, Math.Clamp(Setting(key, fallback), min, max), hint, EditorTheme.SpinMinWidth);
        box.FocusMode = Control.FocusModeEnum.Click;
        string settingKey = $"ge_{Kind}_{key}";
        box.ValueChanged += v =>
        {
            AppSettings.Set("tools", settingKey, v);
            ViewportLayout.Instance?.RedrawAll();
        };
    }

    private GameplayHit Hit(EditorViewport view, Vector2 mouse)
    {
        int depth = ViewAxes.DepthAxis(view.Cam.Kind);
        double? z = depth >= 0 && _start is { } s ? s.Point[depth] : null;
        // Orthographic drawing keeps the chosen depth rather than jumping to an unrelated wall behind it.
        if (!view.Cam.IsOrtho)
        {
            return GameplayPicking.Point(_tools, _state.World, view.Cam, mouse, z);
        }

        Vec3 point = Placement.Point(_tools, view.Cam, mouse, depth: z);
        if (view.Cam.Kind != ViewKind.Top)
        {
            return new GameplayHit(point, null);
        }

        // A previously selected overhead GE is not a new floor: find the floor beneath its drawing plane before
        // adding Lift, otherwise repeated top-view drawings would stack their heights on the previous selection.
        if (_state.World.DropBelow(point + Vec3.UnitZ, 65536) is { } drop)
        {
            point = point with { Z = point.Z + 1 - drop };
        }

        return new GameplayHit(point, Vec3.UnitZ);
    }

    /// <summary>Finds a visible top edge within a fixed pixel radius, even when the cursor ray misses its surface.</summary>
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

        var options = _state.Options with { Region = null };
        bool changed = !ReferenceEquals(w, _edgeWorld) || options != _edgeOptions;
        if (changed)
        {
            _edgeWorld = w; _edgeOptions = options;
            _ledgeEdges = LedgeDetector.CandidateEdges(w, options);
        }
        var camera = view.Cam;
        int cameraKey = HashCode.Combine(camera.Location, camera.Yaw, camera.Pitch, camera.Center, camera.UnitsPerPixel, camera.ScreenSize, camera.PixelScale);
        if (!wait && !changed && cameraKey == _hoverCamera && _hoverAt.DistanceTo(_mouse) < 2) return;
        _hoverCamera = cameraKey; _hoverAt = _mouse;
        var near = _ledgeEdges.Where(e => GeEdgePicking.Near(camera, _mouse, e.A, e.B, LedgePickPixels, vertices: false) is { } p
            && GeEdgePicking.Visible(w, camera, p.Point)).ToList();
        LedgeResult result = LedgeDetector.DetectEdges(w, options, near);
        _lip = result.Lips.Select(l => (Lip: l, Hit: GeEdgePicking.Near(camera, _mouse, l.A, l.B, LedgePickPixels, vertices: false)))
            .Where(p => p.Hit is not null && GeEdgePicking.Visible(w, camera, p.Hit.Value.Point))
            .OrderBy(p => p.Hit!.Value.Pixels).ThenBy(p => (p.Hit!.Value.Point - camera.Location).Length).Select(p => p.Lip).FirstOrDefault();
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
        _hoverAt = new(float.PositiveInfinity, 0);
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

        _hoverAt = new(float.PositiveInfinity, 0);
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
        Ge ge;
        try
        {
            ge = GeDrawing.Create(Kind, pts[0], pts[1], pts.Count > 2 ? pts[2] - pts[0] : null,
                Geometry && GeGeometry.Supports(Kind) ? Appearance() : null);
            ge.Id = id;
        }
        catch (ArgumentException error)
        {
            _tools.Context.SetStatus(error.Message);
            return;
        }
        List<GeProblem> problems = GeShapes.Problems(Kind, ge.Points);
        Reset();
        Commit(new AddObjectsCommand($"Add {id}", ges: [ge]),
            problems.Count == 0 ? $"Added {id} ({ge.Kind})" : $"Added {id}: {string.Join("; ", problems.Select(p => p.Message))}",
            problems.Any(p => p.Severity != Severity.Info));
    }

    /// <summary>The GE's points for a drag from <paramref name="s"/> to <paramref name="e"/>.</summary>
    private List<Vec3>? Points(GameplayHit s, GameplayHit e)
    {
        Vec3 a = Lift(s), b = Lift(e);
        switch (Kind)
        {
            case GeKinds.Ladder or GeKinds.Pipe:
                return ClimbFromDrag(s, e);
            case GeKinds.Pole:
                double top = Math.Abs(b.Z - a.Z) >= 4 ? b.Z : a.Z + Setting("height", 256);
                return [a with { Z = Math.Min(a.Z, top) }, a with { Z = Math.Max(a.Z, top) }];
            case GeKinds.ZipLine:
                if (e.Normal is { Z: > 0.7 })
                {
                    b -= Vec3.UnitZ * Setting("drop", 64);
                }

                return [a, b];
            case GeKinds.HandOverHand:
                return [a, b with { Z = a.Z }];
            default:
                return [a, b];
        }
    }

    private Vec3 Lift(GameplayHit hit) => Kind is GeKinds.HandOverHand or GeKinds.ZipLine or GeKinds.Fence && hit.Normal is { Z: > 0.7 }
        ? hit.Point + Vec3.UnitZ * Setting("lift", DefaultLift)
        : hit.Point;

    /// <summary>A ladder or pipe from a drag: the foot on the floor below where the drag began (on a wall: out from it),
    /// the top at the height where it ended, the climber on the wall's side.</summary>
    private List<Vec3> ClimbFromDrag(GameplayHit s, GameplayHit e)
    {
        double facing = Setting("facing", 0) * Math.PI / 180;
        Vec3 n = s.Normal is { } wall && Math.Abs(wall.Z) < 0.7
            ? new Vec3(wall.X, wall.Y, 0).Normalized()
            : new Vec3(Math.Cos(facing), Math.Sin(facing), 0);
        Vec3 foot = s.Point;
        double top = e.Point.Z;
        if (Math.Abs(top - foot.Z) < 4)
        {
            top = foot.Z + Setting("height", 256);
        }

        if (top < foot.Z)
        {
            (foot, top) = (foot with { Z = top }, foot.Z);
        }

        if (s.Normal is { } sn && Math.Abs(sn.Z) < 0.7 && _state.World.DropBelow(foot + n * 16 + Vec3.UnitZ, 4096) is { } drop)
        {
            foot = foot with { Z = foot.Z + 1 - drop };
        }

        // Keep the visible rails clear of the supporting wall.
        if (s.Normal is { } normal && Math.Abs(normal.Z) < 0.7)
        {
            double clearance = Geometry ? Appearance().Radius + 2 : 2;
            if (Geometry && Kind == GeKinds.Pipe)
            {
                clearance += Appearance().Radius + 35 * (Math.Abs(n.X) + Math.Abs(n.Y)) - 35;
            }
            foot += n * clearance;
        }

        return GeShapes.ClimbPoints(foot, top, n);
    }

    private IEnumerable<Ge> Shown(bool selectedOnly = false)
    {
        MapProject project = _tools.Context.Project;
        var pickable = ObjectFilter.Pickable(project).Where(r => r.Kind == ObjectKind.Ge).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<Ge> selected = _tools.Context.Selection.Items.Where(r => r.Kind == ObjectKind.Ge && pickable.Contains(r.Id))
            .Select(r => project.FindGe(r.Id)).OfType<Ge>().ToList();
        return selectedOnly || selected.Count > 0 ? selected : project.Ges.Where(g => pickable.Contains(g.Id));
    }

    private (Ge Ge, int Index)? HandleAt(EditorViewport view, Vector2 mouse, bool selectedOnly = false)
    {
        (Ge Ge, int Index)? best = null;
        float distance = 8;
        foreach (Ge ge in Shown(selectedOnly))
        {
            if (ge.Points.Count != (GeKinds.HasThirdPoint(ge.Kind) ? 3 : 2))
            {
                // Invalid imported point lists remain repairable in the Inspector; endpoint constraints require a complete shape.
                continue;
            }

            for (int i = 0; i < ge.Points.Count; i++)
            {
                if (view.Cam.ToScreen(ge.Points[i]) is { } point && point.DistanceTo(mouse) <= distance)
                {
                    distance = point.DistanceTo(mouse);
                    best = (ge, i);
                }
            }
        }

        return best;
    }

    private void MoveHandle(EditorViewport view, Vector2 mouse)
    {
        Ge source = _editing!;
        Ge draft = source.Clone();
        Vec3 anchor = source.Points[_pointIndex];
        bool facing = GeKinds.HasThirdPoint(source.Kind) && _pointIndex == 2;
        bool verticalTop = _pointIndex == 1 && (GeKinds.HasThirdPoint(source.Kind) || source.Kind == GeKinds.Pole);
        Vec3 at;
        if (view.Cam.IsOrtho)
        {
            at = view.Cam.PlanePoint(mouse, anchor[ViewAxes.DepthAxis(view.Cam.Kind)]);
        }
        else
        {
            // Move in the camera-facing plane through the endpoint, preserving its height above a distant floor.
            (Vec3 origin, Vec3 direction) = view.Cam.Ray(mouse);
            Vec3 normal = view.Cam.LookDirection;
            double denominator = Vec3.Dot(direction, normal);
            if (Math.Abs(denominator) < 1e-8)
            {
                return;
            }

            at = origin + direction * (Vec3.Dot(anchor - origin, normal) / denominator);
            if (verticalTop && 1 - direction.Z * direction.Z > 1e-5)
            {
                Vec3 offset = origin - anchor;
                double height = (offset.Z - direction.Z * Vec3.Dot(direction, offset)) / (1 - direction.Z * direction.Z);
                at = anchor + Vec3.UnitZ * height;
            }
            else if (facing && Math.Abs(direction.Z) > .05)
                at = origin + direction * ((anchor.Z - origin.Z) / direction.Z);
        }

        _edgeTarget = null;
        if (facing)
        {
            Vec3 direction = at - source.Points[0];
            if (direction.X * direction.X + direction.Y * direction.Y < 1e-8) return;
            Vec3 start = source.Points[2] - source.Points[0];
            double angle = Math.Atan2(direction.Y, direction.X) * Rot.UnitsPerTurn / Math.Tau;
            double original = Math.Atan2(start.Y, start.X) * Rot.UnitsPerTurn / Math.Tau;
            double raw = angle - original;
            raw -= Math.Round(raw / Rot.UnitsPerTurn) * Rot.UnitsPerTurn;
            double radius = view.Cam.ToScreen(source.Points[0]) is { } center && view.Cam.ToScreen(anchor) is { } tip
                ? Math.Max(center.DistanceTo(tip), 1) : 1;
            int threshold = (int)Math.Clamp(_tools.NeighbourSnapPx * Rot.UnitsPerTurn / (Math.Tau * radius), 182, 910);
            if (_tools.RotationSnap) threshold = Math.Min(threshold, (int)(_tools.RotationGrid / (2 * NeighbourSnap.HoldReach)));
            TurnSnap? turn = _handleSnap!.Turn(2, source.Points[0], (int)Math.Round(raw), threshold, null, 0);
            int units = turn?.Units ?? _tools.SnapRotation(angle) - (int)Math.Round(original);
            at = source.Points[0] + RotationMath.About(2, units).Rotate(start);
        }
        else
        {
            int depth = view.Cam.IsOrtho ? ViewAxes.DepthAxis(view.Cam.Kind) : -1;
            int[] free = Enumerable.Range(0, 3).Where(a => a != depth && (!verticalTop || a == 2)).ToArray();
            if (_handleSnap!.Effective && _state.WorldIfBuilt is { } world
                && GeEdgePicking.Pick(world, view.Cam, mouse, _tools.NeighbourSnapPx, source.Id,
                    source.Kind == GeKinds.LedgeGrab, verticalTop ? anchor : null) is { } target)
            {
                at = target.Point;
                if (depth >= 0) at = at.With(depth, anchor[depth]);
                _edgeTarget = target with { Point = at };
                _handleSnap.Release(); _handleSnap.Note = target.Vertex ? "corner" : "edge";
            }
            else
            {
                Vec3 raw = at - anchor;
                foreach (int axis in Enumerable.Range(0, 3).Except(free)) raw = raw.With(axis, 0);
                MoveSnap stick = _handleSnap.Move(raw, free);
                at = anchor;
                foreach (int axis in free)
                    at = at.With(axis, stick.Stuck(axis) ? anchor[axis] + stick.Offset[axis] : _tools.SnapValue(anchor[axis] + raw[axis]));
                _handleSnap.Moved(at - anchor);
            }
        }

        if (GeKinds.HasThirdPoint(source.Kind) && source.Points.Count == 3)
        {
            if (_pointIndex == 2)
            {
                draft.Points = GeShapes.ClimbPoints(source.Points[0], source.Points[1].Z, at - source.Points[0]);
            }
            else
            {
                Vec3 bottom = _pointIndex == 0 ? at : source.Points[0];
                double top = _pointIndex == 1 ? Math.Max(at.Z, bottom.Z + 4) : Math.Max(source.Points[1].Z, bottom.Z + 4);
                draft.Points = GeShapes.ClimbPoints(bottom, top, source.Points[2] - source.Points[0]);
            }
        }
        else if (source.Kind == GeKinds.Pole && source.Points.Count >= 2)
        {
            if (_pointIndex == 0)
            {
                draft.Points[0] = at with { Z = Math.Min(at.Z, source.Points[1].Z - 4) };
                draft.Points[1] = new Vec3(at.X, at.Y, source.Points[1].Z);
            }
            else
            {
                draft.Points[1] = source.Points[0] with { Z = Math.Max(at.Z, source.Points[0].Z + 4) };
            }
        }
        else
        {
            if (source.Kind is GeKinds.HandOverHand or GeKinds.LedgeGrab)
            {
                draft.Points[1 - _pointIndex] = draft.Points[1 - _pointIndex] with { Z = at.Z };
            }

            draft.Points[_pointIndex] = at;
            if (draft.Points.Count < 2 || (draft.Points[1] - draft.Points[0]).Length < 4)
            {
                return;
            }
        }

        draft.Points = draft.Points.Select(GeEdits.Round).ToList();
        try
        {
            GeGeometry.Validate(draft);
        }
        catch (ArgumentException error)
        {
            _handleSnap?.Release();
            _tools.Context.SetStatus(error.Message);
            return;
        }

        _draft = draft;
        if (!facing && (draft.Points[_pointIndex] - at).Length > .01) _handleSnap?.Release();
        _tools.Context.SetStatus($"Reshape {draft.Id}{_handleSnap?.Status()}");
    }

    private void ResolveHandle()
    {
        if (_editing is null || !_moved || _view is null) return;
        MoveHandle(_view, _mouse);
        ViewportLayout.Instance?.RedrawAll();
    }

    private void Preview(OverlayBatch batch, Ge ge)
    {
        if (ge.Points.Count < 2 || (ge.Points[1] - ge.Points[0]).Length < 4)
        {
            return;
        }

        int key = GeLookGeometry.Key(ge);
        if (_previewShapeKey != key)
        {
            _previewShapeKey = key;
            _previewEdges.Clear();
            IReadOnlyList<Brush> brushes;
            try
            {
                brushes = GeGeometry.Build(ge);
            }
            catch (ArgumentException)
            {
                // The toolbar may hold a temporarily invalid width/radius combination; Finish explains it.
                brushes = [];
            }

            // Four viewports share one shape. Each edge is drawn once, rather than once for each adjacent face.
            var seen = new HashSet<(Vec3 A, Vec3 B)>();
            foreach (BrushFace face in brushes.SelectMany(b => b.WorldFaces()))
            {
                for (int i = 0; i < face.Vertices.Count; i++)
                {
                    Vec3 a = face.Vertices[i], b = face.Vertices[(i + 1) % face.Vertices.Count];
                    if (!seen.Contains((b, a)) && seen.Add((a, b)))
                    {
                        _previewEdges.Add((a, b));
                    }
                }
            }
        }

        foreach ((Vec3 a, Vec3 b) in _previewEdges)
        {
            batch.Line(a, b, SceneStyle.Hover, 1);
        }

        batch.Polyline(ge.Points.Take(2).ToList(), SceneStyle.Hover, 3);
        foreach (Vec3 point in ge.Points)
        {
            batch.Dot(point, SceneStyle.Hover, 5);
        }
    }

    private void Reset()
    {
        _start = null;
        _end = null;
        _lip = null;
        _hint = "";
        _hoverAt = new(float.PositiveInfinity, 0);
        _edgeTarget = null;
        _editing = null;
        _draft = null;
        _handleSnap = null;
        _pressed = false;
        _moved = false;
        _previewShapeKey = null;
        _previewEdges.Clear();
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
