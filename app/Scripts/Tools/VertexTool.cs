using Godot;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;
using OpenScctEditor.Panels;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// Vertex and edge editing (V) on the selected brushes. Their vertices show as small squares. Click picks a vertex
/// (in edge mode the nearest edge, both ends), Shift adds, Ctrl toggles, dragging on empty space box-selects. Drag a
/// selected vertex, or an arrow of the handle at their centre, to move them on the grid; faces that bend are split
/// into triangles and the status bar warns when a brush stops being convex. Clicking a brush that is not selected
/// selects it. Keys: Delete removes vertices, arrows nudge by the grid in the view plane, PgUp/PgDn along the depth.
/// </summary>
public sealed class VertexTool : IViewportTool
{
    private const float PickPixels = 8;
    private const float AxisPixels = 70;
    private const float HitPixels = 7;
    private const float DragSlop = 3;
    private static int s_dragCounter;

    private readonly ToolManager _tools;
    private Vector2? _press;
    private Vector2 _current;
    private bool _boxing;
    private EditorViewport? _view;
    private Drag? _drag;

    public VertexTool(ToolManager tools)
    {
        _tools = tools;
        tools.DragInterrupted += DropDrag;
    }

    /// <summary>Clicks pick edges instead of vertices.</summary>
    public static bool EdgeMode
    {
        get => AppSettings.GetBool("tools", "vertex_edges", false);
        set
        {
            AppSettings.Set("tools", "vertex_edges", value);
            ToolManager.Instance?.NotifyChanged();
        }
    }

    public string Id => "vertex";

    public string Title => "Vertex";

    public bool IsDragging => _drag is not null;

    public string Shortcut => "V";

    private static GeometrySelection Geo => GeometrySelection.Instance;

    private MapProject Project => _tools.Context.Project;

    /// <summary>The brushes whose vertices are shown and pickable: the selected brushes.</summary>
    private List<Brush> Targets()
    {
        Dictionary<string, Brush> brushes = FaceEdit.BrushIndex(Project);
        return _tools.Context.Selection.Items.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair))
            .Select(r => brushes.GetValueOrDefault(r.Id)).OfType<Brush>()
            .Where(b => ObjectFilter.IsPickable(Project, b.Editor)).ToList();
    }

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press when _drag is null:
                if (Centre() is { } c && AxisAt(view, c, press.Position) is int axis)
                {
                    StartDrag(view, press.Position, c, axis);
                }
                else if (!press.ShiftPressed && !press.IsCommandOrControlPressed() && NearestVertex(view, press.Position) is { } v && Geo.HasVertex(v))
                {
                    StartDrag(view, press.Position, v.Position, -1);
                }
                else
                {
                    _press = press.Position;
                    _current = press.Position;
                    _boxing = false;
                    _view = view;
                }

                return true;
            case InputEventMouseMotion motion when _drag is { } drag:
                if (drag.View == view)
                {
                    UpdateDrag(drag, motion.Position);
                }

                return true;
            case InputEventMouseMotion motion when _press is { } start && _view == view:
                _current = motion.Position;
                _boxing |= start.DistanceTo(_current) > DragSlop;
                view.Redraw();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _drag is not null:
                DropDrag();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when _press is { } start && _view == view:
                _press = null;
                if (_boxing)
                {
                    _boxing = false;
                    BoxSelect(view, new Rect2(start, Vector2.Zero).Expand(release.Position), release);
                }
                else
                {
                    Click(view, release);
                }

                ViewportLayout.Instance?.RedrawAll();
                return true;
            case InputEventKey { Pressed: true } key when Geo.Vertices.Count > 0:
                return Key(view, key);
        }

        return false;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        ViewCamera cam = view.Cam;
        var dim = new Color(SceneStyle.Hover, 0.8f);
        foreach (Brush b in Targets())
        {
            if (EdgeMode)
            {
                foreach ((Vec3 a, Vec3 z) in BrushVertices.EdgesOf(b))
                {
                    if (cam.ToScreen(a) is { } sa && cam.ToScreen(z) is { } sz)
                    {
                        bool on = Geo.HasVertex(new VertexRef(b.Id, a)) && Geo.HasVertex(new VertexRef(b.Id, z));
                        canvas.DrawLine(sa, sz, on ? SceneStyle.Selection : new Color(SceneStyle.Hover, 0.35f), on ? 3 : 1, antialiased: true);
                    }
                }
            }

            foreach (Vec3 v in BrushVertices.Of(b))
            {
                bool on = Geo.HasVertex(new VertexRef(b.Id, v));
                FacePicking.DrawMarker(cam, canvas, v, on ? SceneStyle.Selection : dim, on ? 5 : 3.5f, filled: on);
            }
        }

        if (_boxing && _press is { } start && _view == view)
        {
            Rect2 r = new Rect2(start, Vector2.Zero).Expand(_current);
            canvas.DrawRect(r, new Color(EditorTheme.Accent, 0.12f));
            canvas.DrawRect(r, EditorTheme.Accent, filled: false, width: 1);
        }

        if (Centre() is { } c && cam.ToScreen(c) is { } sc0)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (AxisEnd(cam, c, axis) is { } end)
                {
                    bool active = _drag?.Axis == axis;
                    canvas.DrawLine(sc0, end, active ? SceneStyle.Selection : SceneStyle.AxisColor(axis), active ? 3 : 2, antialiased: true);
                    canvas.DrawCircle(end, 4, active ? SceneStyle.Selection : SceneStyle.AxisColor(axis));
                }
            }

            if (_drag is { } d && d.View == view && d.Readout.Length > 0)
            {
                ViewTags.Beside(canvas, d.Mouse, d.Readout);
            }
        }
    }

    public void BuildOptions(HBoxContainer bar)
    {
        PanelUi.Toggle(bar, "Edges", "Clicks pick edges (both ends) instead of vertices", EdgeMode, on => EdgeMode = on);
        foreach ((string text, string action) in new[]
                 {
                     ("All", "vertex.selectAll"), ("Weld", "vertex.weld"), ("Delete", "vertex.delete"), ("Snap", "vertex.snap"),
                     ("Bevel", "vertex.bevel"),
                 })
        {
            ToolOptions.Action(bar, _tools.Context, action, text);
        }

        ToolOptions.Hint(bar, "Click or box-select vertices", "Drag them or the handle to move", "Arrows nudge", "Del removes");
    }

    public bool Cancel()
    {
        if (_drag is { } drag)
        {
            _drag = null;
            if (drag.Executed)
            {
                _tools.Context.Session.Commands.Undo();
                Geo.SetVertices(drag.Selection);
            }

            _tools.Context.Session.Commands.EndMerge();
            ViewportLayout.Instance?.RedrawAll();
            return true;
        }

        if (_press is not null || Geo.Vertices.Count > 0)
        {
            _press = null;
            _boxing = false;
            Geo.SetVertices([]);
            ViewportLayout.Instance?.RedrawAll();
            return true;
        }

        return false;
    }

    // ---- picking ------------------------------------------------------------------------------------------------

    private VertexRef? NearestVertex(EditorViewport view, Vector2 mouse)
    {
        VertexRef? best = null;
        double bestDistance = PickPixels;
        foreach (Brush b in Targets())
        {
            foreach (Vec3 v in BrushVertices.Of(b))
            {
                if (view.Cam.ToScreen(v) is { } s && s.DistanceTo(mouse) < bestDistance)
                {
                    bestDistance = s.DistanceTo(mouse);
                    best = new VertexRef(b.Id, v);
                }
            }
        }

        return best;
    }

    private (VertexRef A, VertexRef B)? NearestEdge(EditorViewport view, Vector2 mouse)
    {
        (VertexRef, VertexRef)? best = null;
        double bestDistance = PickPixels;
        foreach (Brush b in Targets())
        {
            foreach ((Vec3 a, Vec3 c) in BrushVertices.EdgesOf(b))
            {
                if (view.Cam.ToScreen(a) is { } sa && view.Cam.ToScreen(c) is { } sc && Picking.SegmentDistance(sa, sc, mouse) is var d && d < bestDistance)
                {
                    bestDistance = d;
                    best = (new VertexRef(b.Id, a), new VertexRef(b.Id, c));
                }
            }
        }

        return best;
    }

    private void Click(EditorViewport view, InputEventMouseButton mods)
    {
        List<VertexRef> hit = EdgeMode
            ? NearestEdge(view, mods.Position) is { } edge ? [edge.A, edge.B] : []
            : NearestVertex(view, mods.Position) is { } v ? [v] : [];
        if (hit.Count == 0 && Picking.Pick(view.Cam, Project, mods.Position) is { Kind: ObjectKind.Brush } brush
            && !_tools.Context.Selection.Contains(brush))
        {
            // Not near a vertex of a selected brush: switch to the brush clicked.
            _tools.Context.Selection.Set([brush]);
            Geo.SetVertices([]);
            _tools.Context.SetStatus($"Editing {brush.Id}");
            return;
        }

        Apply(hit, mods);
    }

    private void BoxSelect(EditorViewport view, Rect2 rect, InputEventWithModifiers mods)
    {
        var hits = new List<VertexRef>();
        foreach (Brush b in Targets())
        {
            hits.AddRange(BrushVertices.Of(b).Where(v => view.Cam.ToScreen(v) is { } s && rect.HasPoint(s)).Select(v => new VertexRef(b.Id, v)));
        }

        Apply(hits, mods);
    }

    private void Apply(List<VertexRef> hits, InputEventWithModifiers mods)
    {
        if (mods.ShiftPressed)
        {
            Geo.SetVertices(Geo.Vertices.Concat(hits));
        }
        else if (mods.IsCommandOrControlPressed())
        {
            hits.ForEach(Geo.ToggleVertex);
        }
        else
        {
            Geo.SetVertices(hits);
        }

        _tools.Context.SetStatus(Geo.Vertices.Count == 0 ? "No vertices selected" : $"{Geo.Vertices.Count} vertices selected");
    }

    // ---- moving -------------------------------------------------------------------------------------------------

    /// <summary>The centre of the selected vertices (where the move handle sits), or null.</summary>
    private static Vec3? Centre()
    {
        if (Geo.Vertices.Count == 0)
        {
            return null;
        }

        Vec3 sum = Geo.Vertices.Aggregate(Vec3.Zero, (s, v) => s + v.Position);
        return sum / Geo.Vertices.Count;
    }

    private static Vector2? AxisEnd(ViewCamera cam, Vec3 centre, int axis)
    {
        if (cam.ToScreen(centre) is not { } c || cam.ToScreen(centre + Vec3.Zero.With(axis, AxisPixels * cam.UnitsPerPixelAt(centre))) is not { } end)
        {
            return null;
        }

        return end.DistanceTo(c) > 12 ? end : null;
    }

    private static int? AxisAt(EditorViewport view, Vec3 centre, Vector2 mouse)
    {
        if (view.Cam.ToScreen(centre) is not { } c)
        {
            return null;
        }

        for (int axis = 0; axis < 3; axis++)
        {
            if (AxisEnd(view.Cam, centre, axis) is { } end && Picking.SegmentDistance(c + (end - c).Normalized() * 8, end, mouse) < HitPixels)
            {
                return axis;
            }
        }

        return null;
    }

    private void StartDrag(EditorViewport view, Vector2 mouse, Vec3 anchor, int axis)
    {
        ViewCamera cam = view.Cam;
        (Vec3 o, Vec3 d) = cam.Ray(mouse);
        Vec3 normal = cam.IsOrtho ? Vec3.Zero.With(ViewAxes.DepthAxis(cam.Kind), 1) : cam.Axes.Forward;
        _drag = new Drag(view, mouse, anchor, axis, normal, $"vertex:{++s_dragCounter}", Geo.VertexBrushes().Select(b => b.Clone()).ToList(),
            Geo.Vertices.ToList())
        {
            T0 = axis >= 0 ? DragMath.LineParam(anchor, Vec3.Zero.With(axis, 1), o, d) : 0,
            PlaneStart = DragMath.PlaneHit(anchor, normal, o, d) ?? anchor,
        };
    }

    private void UpdateDrag(Drag drag, Vector2 mouse)
    {
        drag.Mouse = mouse;
        drag.View.Redraw();
        if (!drag.Moving && mouse.DistanceTo(drag.StartMouse) < DragSlop)
        {
            return;
        }

        drag.Moving = true;
        ViewCamera cam = drag.View.Cam;
        (Vec3 o, Vec3 d) = cam.Ray(mouse);
        Vec3 delta;
        if (drag.Axis >= 0)
        {
            double t = DragMath.LineParam(drag.Anchor, Vec3.Zero.With(drag.Axis, 1), o, d);
            double target = _tools.SnapValue(drag.Anchor[drag.Axis] + t - drag.T0);
            delta = Vec3.Zero.With(drag.Axis, target - drag.Anchor[drag.Axis]);
        }
        else
        {
            Vec3 hit = DragMath.PlaneHit(drag.Anchor, drag.Normal, o, d) ?? drag.PlaneStart;
            int[] axes = cam.IsOrtho ? [.. new[] { 0, 1, 2 }.Where(a => a != ViewAxes.DepthAxis(cam.Kind))] : [0, 1, 2];
            // The grabbed vertex lands on the grid; the others keep their offsets from it.
            delta = _tools.Snap(drag.Anchor + (hit - drag.PlaneStart), axes) - drag.Anchor;
            foreach (int axis in new[] { 0, 1, 2 }.Except(axes))
            {
                delta = delta.With(axis, 0);
            }
        }

        if (delta == drag.Applied)
        {
            return;
        }

        drag.Applied = delta;
        drag.Readout = ViewTags.Axes(delta);
        _tools.DragStep(() => drag.Executed |= VertexCommands.Run("Move vertices", (b, pts) => VertexEdit.Move(b, pts, delta), p => p + delta, drag.Key,
            drag.Brushes, drag.Selection));
        drag.View.Redraw();
    }

    /// <summary>Ends a drag where it is (mouse up, another edit came in, or another map opened).</summary>
    private void DropDrag()
    {
        if (_drag is null)
        {
            return;
        }

        _drag = null;
        _tools.Context.Session.Commands.EndMerge();
        ViewportLayout.Instance?.RedrawAll();
    }

    private bool Key(EditorViewport view, InputEventKey key)
    {
        if (key.IsCommandOrControlPressed() || key.AltPressed)
        {
            return false;
        }

        if (key.Keycode is Godot.Key.Delete or Godot.Key.Backspace)
        {
            VertexCommands.Delete();
            return true;
        }

        double g = _tools.GridSize;
        (Vec3 right, Vec3 up) = ViewAxes.For(view.Kind) ?? (Vec3.UnitX, Vec3.UnitY);
        Vec3 depth = view.Cam.IsOrtho ? Vec3.Zero.With(ViewAxes.DepthAxis(view.Kind), 1) : Vec3.UnitZ;
        Vec3 delta = key.Keycode switch
        {
            Godot.Key.Left => -right * g,
            Godot.Key.Right => right * g,
            Godot.Key.Up => up * g,
            Godot.Key.Down => -up * g,
            Godot.Key.Pageup => depth * g,
            Godot.Key.Pagedown => -depth * g,
            _ => Vec3.Zero,
        };
        if (delta == Vec3.Zero)
        {
            return false;
        }

        VertexCommands.Move(delta);
        return true;
    }

    private sealed class Drag(EditorViewport view, Vector2 startMouse, Vec3 anchor, int axis, Vec3 normal, string key, List<Brush> brushes,
        List<VertexRef> selection)
    {
        public EditorViewport View { get; } = view;

        public Vector2 StartMouse { get; } = startMouse;

        public Vec3 Anchor { get; } = anchor;

        /// <summary>The axis handle dragged, or -1 for a free drag in the view plane.</summary>
        public int Axis { get; } = axis;

        public Vec3 Normal { get; } = normal;

        public string Key { get; } = key;

        /// <summary>The brushes as they were when the drag started (every step starts from them).</summary>
        public List<Brush> Brushes { get; } = brushes;

        public List<VertexRef> Selection { get; } = selection;

        public double T0 { get; init; }

        public Vec3 PlaneStart { get; init; }

        public Vec3 Applied { get; set; }

        public bool Moving { get; set; }

        public bool Executed { get; set; }

        /// <summary>What the drag has done so far, for the readout beside the cursor.</summary>
        public TagPart[] Readout { get; set; } = [];

        /// <summary>The readout as plain text (the status bar).</summary>
        public string Label => ViewTags.Plain(Readout);

        /// <summary>Where the cursor is now (the readout follows it).</summary>
        public Vector2 Mouse { get; set; }
    }
}
