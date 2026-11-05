using Godot;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// Move (W), rotate (E) and scale (R) with a screen-space gizmo at the primary selection's pivot: an arrow, ring or
/// box handle per world axis, plus a centre handle (move in the view plane, uniform scale). In the move tool an
/// already selected object can also be dragged directly. Moves snap the pivot to the grid, rotations to the rotation
/// grid; with Neighbour Snap on (Shift+G; holding Alt inverts it for the drag) faces stick to nearby faces first
/// (<see cref="NeighbourDrag"/>). A whole drag is one undo step. Clicks that miss the gizmo select as the select tool does.
/// </summary>
public sealed class TransformTool : IViewportTool
{
    private const float AxisPixels = 90;
    private const float RingPixels = 80;
    private const float HitPixels = 7;
    private const float CenterPixels = 7;
    private const float DragSlop = 3;
    private static int s_dragCounter;

    /// <summary>The rotator component a turn about each world axis (X, Y, Z) changes.</summary>
    private static readonly string[] RotationNames = ["Roll", "Pitch", "Yaw"];

    private readonly ToolManager _tools;
    private readonly ClickSelect _select;
    private Drag? _drag;
    private Handle _hover = Handle.None;
    private EditorViewport? _hoverView;
    private bool _resolving;

    public TransformTool(ToolManager tools, TransformMode mode)
    {
        _tools = tools;
        Mode = mode;
        _select = new ClickSelect(tools);
        tools.DragInterrupted += DropDrag;
        // Neighbour snap turned on or off mid-drag (Shift+G) shows at once.
        tools.Changed += Resolve;
    }

    public TransformMode Mode { get; }

    public string Id => Mode.ToString().ToLowerInvariant();

    public string Title => Mode.ToString();

    public bool IsDragging => _drag is { Moving: true };

    public string Shortcut => Mode switch { TransformMode.Move => "W", TransformMode.Rotate => "E", _ => "R" };

    private MapProject Project => _tools.Context.Project;

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: false } press when _drag is null:
                if (Pivot() is { } pivot && HitTest(view, pivot, press.Position) is { } handle && handle != Handle.None)
                {
                    StartDrag(view, handle, press.Position, pivot);
                    return true;
                }

                if (Mode == TransformMode.Move && !press.ShiftPressed && !press.IsCommandOrControlPressed()
                    && Picking.Pick(view.Cam, Project, press.Position) is { } hit && _tools.Context.Selection.Contains(hit)
                    && Pivot() is { } p)
                {
                    StartDrag(view, Handle.Center, press.Position, p);
                    if (_drag is not null)
                    {
                        // A click that never moves selects just this object, as a click elsewhere would.
                        _drag.Clicked = hit;
                    }

                    return true;
                }

                return _select.Input(view, e);
            case InputEventMouseMotion motion when _drag is { } drag:
                if (drag.View == view)
                {
                    drag.Snap.AltHeld = motion.AltPressed || Godot.Input.IsKeyPressed(Key.Alt);
                    UpdateDrag(drag, motion.Position);
                }

                return true;
            case InputEventKey { Keycode: Key.Alt } alt when _drag is { } drag:
                // Holding Alt inverts neighbour snap for this drag: show it without waiting for the mouse to move.
                if (drag.Snap.AltHeld != alt.Pressed)
                {
                    drag.Snap.AltHeld = alt.Pressed;
                    UpdateDrag(drag, drag.Mouse, force: true);
                }

                return true;
            case InputEventMouseMotion motion:
                UpdateHover(view, motion.Position);
                return _select.Input(view, e);
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _drag is not null:
                EndDrag();
                return true;
        }

        return _select.Input(view, e);
    }

    public bool Cancel()
    {
        if (_drag is { } drag)
        {
            _drag = null;
            if (drag.Executed)
            {
                _tools.Context.Session.Commands.Undo();
            }

            _tools.Context.Session.Commands.EndMerge();
            _tools.Context.SetStatus($"{Title} cancelled");
            // The faces it stuck to were drawn in every view.
            ViewportLayout.Instance?.RedrawAll();
            return true;
        }

        return _select.Cancel();
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        _select.Draw(view, canvas);
        if (_drag is { Moving: true } dragging)
        {
            dragging.Snap.Draw(view, canvas);
        }

        if (Pivot() is not { } pivot || Handles(view, pivot) is not { } h)
        {
            return;
        }

        Handle active = _drag?.Handle ?? (_hoverView == view ? _hover : Handle.None);
        for (int axis = 0; axis < 3; axis++)
        {
            Color c = Highlight(SceneStyle.AxisColor(axis), active == AxisHandle(axis));
            if (Mode == TransformMode.Rotate)
            {
                if (h.Rings[axis] is { } ring)
                {
                    canvas.DrawPolyline(ring, c, active == AxisHandle(axis) ? 3 : 2, antialiased: true);
                }

                continue;
            }

            if (h.Ends[axis] is not { } end)
            {
                continue;
            }

            canvas.DrawLine(h.Center, end, c, active == AxisHandle(axis) ? 3 : 2, antialiased: true);
            Vector2 dir = (end - h.Center).Normalized();
            if (Mode == TransformMode.Move)
            {
                Vector2 side = new(-dir.Y, dir.X);
                canvas.DrawColoredPolygon([end + dir * 12, end + side * 5, end - side * 5], c);
            }
            else
            {
                canvas.DrawRect(new Rect2(end - new Vector2(5, 5), new Vector2(10, 10)), c);
            }
        }

        if (Mode != TransformMode.Rotate)
        {
            Color cc = Highlight(EditorTheme.Text, active == Handle.Center);
            canvas.DrawRect(new Rect2(h.Center - new Vector2(CenterPixels, CenterPixels), new Vector2(CenterPixels * 2, CenterPixels * 2)), cc, filled: false, width: 2);
        }
        else
        {
            canvas.DrawCircle(h.Center, 3, EditorTheme.Text);
        }

        if (_drag is { } d && d.View == view && d.Readout.Length > 0)
        {
            ViewTags.BesideStack(canvas, d.Mouse, [d.Readout, .. d.SnapReadout]);
        }
    }

    private static Color Highlight(Color c, bool on) => on ? SceneStyle.Selection : c;

    private Vec3? Pivot() =>
        _tools.Context.Selection.Primary is { } r ? ObjectGeometry.Pivot(Project, r) : null;

    private Gizmo? Handles(EditorViewport view, Vec3 pivot)
    {
        ViewCamera cam = view.Cam;
        if (cam.ToScreen(pivot) is not { } center)
        {
            return null;
        }

        double upp = cam.UnitsPerPixelAt(pivot);
        var ends = new Vector2?[3];
        var rings = new Vector2[]?[3];
        for (int axis = 0; axis < 3; axis++)
        {
            Vec3 e = Vec3.Zero.With(axis, 1);
            if (cam.ToScreen(pivot + e * (AxisPixels * upp)) is { } end && end.DistanceTo(center) > 14)
            {
                ends[axis] = end;
            }

            if (Mode == TransformMode.Rotate)
            {
                rings[axis] = Ring(cam, pivot, axis, RingPixels * upp, center);
            }
        }

        return new Gizmo(center, ends, rings);
    }

    /// <summary>The screen polyline of the ring about <paramref name="axis"/>, or null when it is seen edge-on.</summary>
    private static Vector2[]? Ring(ViewCamera cam, Vec3 pivot, int axis, double radius, Vector2 center)
    {
        Vec3 u = Vec3.Zero.With((axis + 1) % 3, 1), v = Vec3.Zero.With((axis + 2) % 3, 1);
        const int steps = 48;
        var pts = new Vector2[steps + 1];
        double area = 0;
        for (int i = 0; i <= steps; i++)
        {
            double a = i * Math.PI * 2 / steps;
            if (cam.ToScreen(pivot + (u * Math.Cos(a) + v * Math.Sin(a)) * radius) is not { } s)
            {
                return null;
            }

            pts[i] = s;
            if (i > 0)
            {
                area += (pts[i - 1] - center).Cross(s - center) / 2;
            }
        }

        return Math.Abs(area) < RingPixels * RingPixels * 0.25 ? null : pts;
    }

    private Handle? HitTest(EditorViewport view, Vec3 pivot, Vector2 mouse)
    {
        if (Handles(view, pivot) is not { } h)
        {
            return null;
        }

        if (Mode != TransformMode.Rotate && Math.Abs(mouse.X - h.Center.X) <= CenterPixels + 2 && Math.Abs(mouse.Y - h.Center.Y) <= CenterPixels + 2)
        {
            return Handle.Center;
        }

        Handle best = Handle.None;
        double bestDistance = HitPixels;
        for (int axis = 0; axis < 3; axis++)
        {
            double d = double.MaxValue;
            if (Mode == TransformMode.Rotate && h.Rings[axis] is { } ring)
            {
                for (int i = 0; i + 1 < ring.Length; i++)
                {
                    d = Math.Min(d, Picking.SegmentDistance(ring[i], ring[i + 1], mouse));
                }
            }
            else if (Mode != TransformMode.Rotate && h.Ends[axis] is { } end)
            {
                d = Picking.SegmentDistance(h.Center, end + (end - h.Center).Normalized() * 12, mouse);
            }

            if (d < bestDistance)
            {
                bestDistance = d;
                best = AxisHandle(axis);
            }
        }

        return best;
    }

    private void UpdateHover(EditorViewport view, Vector2 mouse)
    {
        Handle h = Pivot() is { } pivot ? HitTest(view, pivot, mouse) ?? Handle.None : Handle.None;
        if (h != _hover || _hoverView != view)
        {
            _hover = h;
            _hoverView = view;
            view.Redraw();
        }
    }

    private void StartDrag(EditorViewport view, Handle handle, Vector2 mouse, Vec3 pivot)
    {
        var snap = new TransformSnapshot(Project, _tools.Context.Selection.Items);
        if (snap.IsEmpty)
        {
            return;
        }

        ObjectRef? primary = _tools.Context.Selection.Primary;
        SnapUse use = Mode switch { TransformMode.Move => SnapUse.Move, TransformMode.Rotate => SnapUse.Rotate, _ => SnapUse.Resize };
        var neighbours = new NeighbourDrag(_tools, view, use, snap.Refs.Select(r => r.Id), () => NeighbourDrag.ShapeOf(snap, primary));
        var drag = new Drag(view, handle, mouse, pivot, snap, $"gizmo:{++s_dragCounter}", neighbours)
        {
            Mode = Mode,
            StartRotation = primary is { } pr ? snap.Start(pr)?.Rotation : null,
            ScaleBlocker = Mode == TransformMode.Scale ? NeighbourDrag.ScaleBlocker(snap) : null,
        };
        ViewCamera cam = view.Cam;
        (Vec3 o, Vec3 d) = cam.Ray(mouse);
        int axis = AxisOf(handle);
        switch (Mode)
        {
            case TransformMode.Move when axis >= 0:
                drag.T0 = DragMath.LineParam(pivot, Vec3.Zero.With(axis, 1), o, d);
                break;
            case TransformMode.Move:
                drag.PlaneNormal = cam.IsOrtho ? Vec3.Zero.With(ViewAxes.DepthAxis(cam.Kind), 1) : cam.Axes.Forward;
                drag.PlaneStart = DragMath.PlaneHit(pivot, drag.PlaneNormal, o, d) ?? pivot;
                break;
            case TransformMode.Rotate when axis >= 0:
                SetupRotate(drag, cam, pivot, axis, mouse);
                break;
            case TransformMode.Scale when axis >= 0 && Handles(view, pivot) is { Ends: var ends } && ends[axis] is { } end:
                Vector2 center = cam.ToScreen(pivot) ?? mouse;
                drag.ScreenDir = (end - center).Normalized();
                drag.ScreenLength = Math.Max(end.DistanceTo(center), 1);
                break;
        }

        _drag = drag;
    }

    private static void SetupRotate(Drag drag, ViewCamera cam, Vec3 pivot, int axis, Vector2 mouse)
    {
        double radius = RingPixels * cam.UnitsPerPixelAt(pivot);
        Vec3 u = Vec3.Zero.With((axis + 1) % 3, 1), v = Vec3.Zero.With((axis + 2) % 3, 1);
        Vec3 nearest = pivot + u * radius;
        double best = double.MaxValue;
        for (int i = 0; i < 64; i++)
        {
            double a = i * Math.PI * 2 / 64;
            Vec3 p = pivot + (u * Math.Cos(a) + v * Math.Sin(a)) * radius;
            if (cam.ToScreen(p) is { } s && s.DistanceTo(mouse) < best)
            {
                best = s.DistanceTo(mouse);
                nearest = p;
            }
        }

        // Dragging along the ring's screen tangent at the grabbed point turns it (pixels to rotator units).
        const int probe = 1024;
        Vec3 turned = pivot + RotationMath.About(axis, probe).Rotate(nearest - pivot);
        if (cam.ToScreen(nearest) is { } a0 && cam.ToScreen(turned) is { } a1 && a0.DistanceTo(a1) > 0.5f)
        {
            drag.ScreenDir = (a1 - a0).Normalized();
            drag.UnitsPerPixel = probe / a0.DistanceTo(a1);
        }
        else
        {
            drag.ScreenDir = Vector2.Right;
            drag.UnitsPerPixel = 64;
        }
    }

    /// <summary>Re-runs the drag's step where the mouse is (a snap setting changed mid-drag).</summary>
    private void Resolve()
    {
        if (_drag is { Moving: true } drag && !_resolving)
        {
            _resolving = true;
            try
            {
                UpdateDrag(drag, drag.Mouse, force: true);
            }
            finally
            {
                _resolving = false;
            }
        }
    }

    /// <summary>One step of the drag at <paramref name="mouse"/>; <paramref name="force"/> re-runs it where it is (Alt
    /// or a snap setting changed) and redraws every view.</summary>
    private void UpdateDrag(Drag drag, Vector2 mouse, bool force = false)
    {
        // A few pixels of slop, so a click on the gizmo or a selected object never nudges it.
        if (!drag.Moving && (force || mouse.DistanceTo(drag.StartMouse) < DragSlop))
        {
            return;
        }

        drag.Moving = true;
        drag.Mouse = mouse;
        ViewCamera cam = drag.View.Cam;
        (Vec3 o, Vec3 d) = cam.Ray(mouse);
        int axis = AxisOf(drag.Handle);
        OpenScct.Editor.Core.Commands.ReplaceObjectsCommand? cmd = null;
        switch (Mode)
        {
            case TransformMode.Move:
            {
                // The raw (unsnapped) offset first: a neighbour within the snap distance wins on its axis, and the grid
                // snaps the pivot on the others exactly as it does without neighbour snap.
                Vec3 raw;
                int[] free;
                if (axis >= 0)
                {
                    double t = DragMath.LineParam(drag.Pivot, Vec3.Zero.With(axis, 1), o, d);
                    raw = Vec3.Zero.With(axis, t - drag.T0);
                    free = [axis];
                }
                else
                {
                    Vec3 hit = DragMath.PlaneHit(drag.Pivot, drag.PlaneNormal, o, d) ?? drag.PlaneStart;
                    raw = hit - drag.PlaneStart;
                    free = cam.IsOrtho ? [.. new[] { 0, 1, 2 }.Where(a => a != ViewAxes.DepthAxis(cam.Kind))] : [0, 1, 2];
                }

                MoveSnap stick = drag.Snap.Move(raw, free);
                Vec3 offset = Vec3.Zero;
                foreach (int a in free)
                {
                    offset = offset.With(a, stick.Stuck(a) ? stick.Offset[a] : _tools.SnapValue(drag.Pivot[a] + raw[a]) - drag.Pivot[a]);
                }

                drag.Snap.Moved(offset);
                drag.Readout = ViewTags.Axes(offset);
                cmd = drag.Changes(offset) ? drag.Snapshot.Translate(Project, offset, drag.Key) : null;
                break;
            }

            case TransformMode.Rotate when axis >= 0:
            {
                // A turn that lines a face up with a nearby face wins over the rotation grid.
                // With the rotation grid on, turns landing on it are left to it and the reach (with its hold) stays under
                // half a grid step, so every grid step next to a lined-up angle can still be reached.
                double rawUnits = (mouse - drag.StartMouse).Dot(drag.ScreenDir) * drag.UnitsPerPixel;
                int threshold = (int)Math.Clamp(_tools.NeighbourSnapPx * drag.UnitsPerPixel, 182, 910);
                int grid = _tools.RotationSnap ? _tools.RotationGrid : 0;
                if (grid > 0)
                {
                    threshold = Math.Min(threshold, (int)(grid / (2 * NeighbourSnap.HoldReach)));
                }

                TurnSnap? turn = drag.Snap.Turn(axis, drag.Pivot, (int)Math.Round(rawUnits), threshold, drag.StartRotation, grid);
                int units = turn?.Units ?? _tools.SnapRotation(rawUnits);
                drag.Readout =
                [
                    new(RotationNames[axis] + " ", SceneStyle.AxisColor(axis)),
                    new(ViewTags.Number(Rot.ToDegrees(units)) + "\u00b0", EditorTheme.Text),
                    new("  " + ViewTags.Number(units), EditorTheme.TextDim),
                ];
                cmd = drag.Changes(units) ? drag.Snapshot.Rotate(Project, drag.Pivot, axis, units, drag.Key) : null;
                break;
            }

            case TransformMode.Scale:
            {
                Vector2 delta = mouse - drag.StartMouse;
                double factor = axis >= 0
                    ? 1 + delta.Dot(drag.ScreenDir) / drag.ScreenLength
                    : 1 + (delta.X - delta.Y) / 150.0;

                // A face reaching a neighbour's plane sets the factor, for a selection square to the world axes (where a
                // world-axis scale is a pure scale of its box); otherwise the factor rounds to eighths with grid snap.
                ValueSnap? stuck = null;
                if (drag.ScaleBlocker is null)
                {
                    stuck = drag.Snap.Scale(drag.Pivot, axis, Math.Max(factor, 0.125));
                }
                else
                {
                    // Said in the readout, so a scale that never sticks is not a mystery.
                    drag.Snap.Release();
                    drag.Snap.Note = drag.ScaleBlocker;
                }

                if (stuck is not null)
                {
                    factor = stuck.Value;
                }
                else if (_tools.GridSnap)
                {
                    factor = Math.Round(factor * 8) / 8;
                }

                factor = Math.Max(factor, 0.125);
                drag.Readout = ViewTags.Value("Scale", "\u00d7" + factor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                cmd = drag.Changes(factor) ? drag.Snapshot.Scale(Project, drag.Pivot, axis, factor, drag.Key) : null;
                break;
            }
        }

        drag.SnapReadout = drag.Snap.Readout();
        if (cmd is not null && !cmd.IsEmpty)
        {
            _tools.DragStep(() => _tools.Context.Execute(cmd));
            drag.Executed = true;
        }

        _tools.Context.SetStatus($"{Title} {drag.Label}{drag.Snap.Status()}");
        bool wasStuck = drag.WasStuck;
        drag.WasStuck = drag.Snap.Stuck;
        if (force || drag.Snap.Stuck || wasStuck)
        {
            // The stuck faces are drawn in every view (and wiped from them once it lets go).
            ViewportLayout.Instance?.RedrawAll();
        }
        else
        {
            drag.View.Redraw();
        }
    }

    private void EndDrag()
    {
        if (_drag is { Executed: false, Clicked: { } clicked })
        {
            _tools.Context.Selection.Set([clicked]);
        }

        _drag = null;
        _tools.Context.Session.Commands.EndMerge();
        ViewportLayout.Instance?.RedrawAll();
    }

    /// <summary>Ends a drag where it is, without undoing it (another edit came in, or another map opened).</summary>
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

    private static Handle AxisHandle(int axis) => axis switch { 0 => Handle.X, 1 => Handle.Y, _ => Handle.Z };

    private static int AxisOf(Handle h) => h switch { Handle.X => 0, Handle.Y => 1, Handle.Z => 2, _ => -1 };

    private enum Handle
    {
        None,
        X,
        Y,
        Z,
        Center,
    }

    private sealed record Gizmo(Vector2 Center, Vector2?[] Ends, Vector2[]?[] Rings);

    private sealed class Drag(EditorViewport view, Handle handle, Vector2 startMouse, Vec3 pivot, TransformSnapshot snapshot, string key,
        NeighbourDrag snap)
    {
        /// <summary>Neighbour snap for this drag.</summary>
        public NeighbourDrag Snap { get; } = snap;

        /// <summary>The primary object's rotation at the start (a turn can match a neighbour's rotation exactly).</summary>
        public Rot? StartRotation { get; init; }

        /// <summary>Why a scale of this selection cannot stick (a turned selection, an actor with no mesh), or null when it can.</summary>
        public string? ScaleBlocker { get; init; }

        /// <summary>True when the last step stuck to something (its highlights are in every view).</summary>
        public bool WasStuck { get; set; }

        /// <summary>The neighbour snap chips after the readout.</summary>
        public TagPart[][] SnapReadout { get; set; } = [];

        public EditorViewport View { get; } = view;

        public Handle Handle { get; } = handle;

        public Vector2 StartMouse { get; } = startMouse;

        public Vec3 Pivot { get; } = pivot;

        public TransformSnapshot Snapshot { get; } = snapshot;

        public string Key { get; } = key;

        public double T0 { get; set; }

        public Vec3 PlaneNormal { get; set; }

        public Vec3 PlaneStart { get; set; }

        public Vector2 ScreenDir { get; set; }

        public double ScreenLength { get; set; } = 1;

        public double UnitsPerPixel { get; set; } = 1;

        public bool Executed { get; set; }

        /// <summary>True once the mouse left the slop circle around the press.</summary>
        public bool Moving { get; set; }

        /// <summary>The value the last step applied (offset, rotator units or factor); null before the first.</summary>
        private object? _applied;

        /// <summary>True when <paramref name="value"/> differs from what is applied now, so a step is worth an edit
        /// (a click that does not move adds no undo step). Records it as applied.</summary>
        public bool Changes(object value)
        {
            object current = _applied ?? Mode switch { TransformMode.Move => Vec3.Zero, TransformMode.Rotate => 0, _ => 1.0 };
            if (Equals(current, value))
            {
                return false;
            }

            _applied = value;
            return true;
        }

        public TransformMode Mode { get; init; }

        /// <summary>The selected object the drag started on (moving it directly), or null for a handle drag.</summary>
        public ObjectRef? Clicked { get; set; }

        /// <summary>What the drag has done so far, for the readout beside the cursor.</summary>
        public TagPart[] Readout { get; set; } = [];

        /// <summary>The readout as plain text (the status bar).</summary>
        public string Label => ViewTags.Plain(Readout);

        /// <summary>Where the cursor is now (the readout follows it).</summary>
        public Vector2 Mouse { get; set; }
    }
}
