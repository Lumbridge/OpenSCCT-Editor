using Godot;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>Direct manipulation of the builder preview. Placement and box drawing are explicit, cancellable gestures.</summary>
public sealed class BrushTool : IViewportTool
{
    private const float DragThreshold = 4;
    private static readonly string[] SizeKeys = ["breadth", "width", "height"];
    private readonly ToolManager _tools;
    private readonly Dictionary<string, SpinBox> _numberFields = new();
    private readonly List<ActionButton> _commitButtons = new();
    private bool _syncFields;
    private bool _updating;
    private BrushMode _mode;
    private BuilderBrush.State? _start;
    private HandleDrag? _drag;
    private Vector2? _press;
    private Vector2 _current;
    private EditorViewport? _view;
    private NeighbourDrag? _snap;
    private (Vec3 P0, Vec3 P1)? _corners;
    private string? _hover;
    private string? _error;
    private bool _placeValid;
    private bool _dragMoved;

    private enum BrushMode { Handles, Place, Box }
    private sealed record Handle(string Id, Vec3 Point, Vec3 Direction, int Axis, BuilderSizeHandle? Size = null);
    private sealed record ScreenHandle(Handle Handle, Vector2 Point, Vector2 Origin);
    private sealed record HandleDrag(Handle Handle, Vector2 Mouse, Vec3 PlaneNormal, Vec3 PlaneStart, double LineStart, Vec3 Min, Vec3 Max);

    public BrushTool(ToolManager tools)
    {
        _tools = tools;
        tools.DragInterrupted += () => Cancel();
        tools.Changed += () =>
        {
            if (_updating) return;
            SyncFields();
            if (_drag is not null && _view is { } view) UpdateHandle(view, _current);
        };
    }

    public string Id => "brush";
    public string Title => "Builder Brush";
    public string Shortcut => "B";
    public string Description => "Drag arrows to move, squares or circles to size; Place Here previews on a surface";
    public bool IsDragging => Builder.GestureActive;
    private BuilderBrush Builder => _tools.Builder;
    public void Activate() => _tools.NotifyChanged();
    public void Deactivate() { Cancel(); _hover = null; }

    private void SyncFields()
    {
        foreach (ActionButton button in _commitButtons) if (GodotObject.IsInstanceValid(button)) button.Refresh();
        _syncFields = true;
        foreach (var (key, spin) in _numberFields)
            if (GodotObject.IsInstanceValid(spin)) spin.SetValueNoSignal(Builder.Spec.Get(key, 0));
        _syncFields = false;
    }

    private string ParameterLabel(string key)
    {
        BuilderSizeHandle? handle = BuilderHandles.For(Builder.Spec).FirstOrDefault(h => h.Key == key && h.Factor == 1);
        return handle?.Label ?? PanelUi.Words(key);
    }

    private void Begin()
    {
        _start = Builder.Capture();
        _dragMoved = false;
        Builder.GestureActive = true;
        _tools.NotifyChanged();
    }

    public void LoadPreset(BuilderSpec spec, Vec3? at = null)
    {
        Cancel();
        _tools.Activate("brush");
        Builder.Restore(new(spec, at ?? Builder.Location, Rot.Zero, null));
        if (at is null) SetMode(BrushMode.Place);
        else _tools.Context.SetStatus("Drag handles to adjust, then Add or Subtract to commit");
    }

    private void SetMode(BrushMode mode)
    {
        bool toggleOff = _mode == mode;
        Cancel();
        if (!toggleOff)
        {
            _mode = mode;
            Begin();
        }
        _tools.RefreshOptions();
        _tools.Context.SetStatus(_mode == BrushMode.Place ? "Place Here: point at a surface, click to confirm; Escape cancels"
            : _mode == BrushMode.Box ? "Draw Box: drag in a 2D view; Escape cancels" : "Drag a builder handle");
    }

    private void Finish()
    {
        _start = null;
        _drag = null;
        _press = null;
        _snap = null;
        _corners = null;
        _mode = BrushMode.Handles;
        _error = null;
        Builder.GestureActive = false;
        Builder.RefreshFields();
        _tools.NotifyChanged();
        ViewportLayout.Instance?.RedrawAll();
    }

    public bool Cancel()
    {
        if (_start is null) return false;
        BuilderBrush.State state = _start;
        // Clear the gesture before notifying listeners.
        _start = null;
        _drag = null;
        _press = null;
        _snap = null;
        _corners = null;
        _mode = BrushMode.Handles;
        _error = null;
        _placeValid = false;
        Builder.GestureActive = false;
        Builder.Restore(state);
        _tools.NotifyChanged();
        ViewportLayout.Instance?.RedrawAll();
        return true;
    }

    public void PointerLeft(EditorViewport view)
    {
        if (_view != view) return;
        _hover = null;
        if (_mode == BrushMode.Place && _start is { } start)
        {
            _placeValid = false;
            Builder.Restore(start, false);
        }
        ViewportLayout.Instance?.RedrawAll();
    }

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion:
                if ((_drag is not null || _press is not null) && _view != view) return true;
                _current = motion.Position;
                if (_snap is not null) _snap.AltHeld = motion.AltPressed;
                if (_drag is not null && _view == view) UpdateHandle(view, motion.Position);
                else if (_mode == BrushMode.Place) PreviewPlacement(view, motion.Position);
                else if (_mode == BrushMode.Box && _press is not null && _view == view) TrackBox();
                else
                {
                    _view = view;
                    _hover = Hit(view, motion.Position)?.Handle.Id;
                    ViewportLayout.Instance?.RedrawAll();
                }
                return _start is not null;
            case InputEventKey { Keycode: Key.Alt } alt when _snap is not null:
                _snap.AltHeld = alt.Pressed;
                if (_drag is not null && _view is { } dragView) UpdateHandle(dragView, _current);
                else TrackBox();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _current = press.Position;
                if (_mode == BrushMode.Place)
                {
                    PreviewPlacement(view, press.Position);
                    if (_placeValid) Finish();
                    return true;
                }
                if (_mode == BrushMode.Box)
                {
                    if (!view.Cam.IsOrtho) { _tools.Context.SetStatus("Draw Box needs a 2D view"); return true; }
                    _press = press.Position;
                    _view = view;
                    _snap = new NeighbourDrag(_tools, view, SnapUse.Resize, [], () => null);
                    _snap.AltHeld = press.AltPressed;
                    return true;
                }
                if (Hit(view, press.Position) is { } hit)
                {
                    Begin();
                    _view = view;
                    _hover = hit.Handle.Id;
                    Vec3 normal = view.Cam.LookDirection;
                    var ray = view.Cam.Ray(press.Position);
                    Vec3 plane = DragMath.PlaneHit(Builder.Location, normal, ray.Origin, ray.Direction) ?? Builder.Location;
                    var (min, max) = Builder.DisplayShape().Bounds();
                    double lineStart = hit.Handle.Axis < 0 ? 0 : DragMath.LineParam(hit.Handle.Point, hit.Handle.Direction, ray.Origin, ray.Direction);
                    _drag = new(hit.Handle, press.Position, normal, plane, lineStart, min, max);
                    MovingShape? shape = SnapFaces.Shape(Builder.WorldPieces().SelectMany(SnapFaces.OfBrush).ToList(), null);
                    _snap = new NeighbourDrag(_tools, view, hit.Handle.Size is null ? SnapUse.Move : SnapUse.Resize, [], () => shape);
                    _snap.AltHeld = press.AltPressed;
                    return true;
                }
                return true; // Background clicks deliberately leave the preview in place.
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release:
                if (_drag is not null)
                {
                    if (_view == view) UpdateHandle(view, release.Position);
                    Finish();
                    return true;
                }
                if (_mode == BrushMode.Box && _press is not null)
                {
                    if (_view == view)
                    {
                        _current = release.Position;
                        TrackBox();
                    }
                    if (_corners is not null) Finish();
                    else { _press = null; _snap = null; }
                    return true;
                }
                break;
        }
        return false;
    }

    private IReadOnlyList<ScreenHandle> Handles(EditorViewport view)
    {
        var result = new List<ScreenHandle>();
        ViewCamera cam = view.Cam;
        Vec3 pivot = Builder.Location;
        if (cam.ToScreen(pivot) is not { } center) return result;
        result.Add(new(new("move.center", pivot, Vec3.Zero, -1), center, center));
        double pixel = cam.UnitsPerPixelAt(pivot);
        for (int axis = 0; axis < 3; axis++)
        {
            Vec3 dir = Vec3.Zero.With(axis, 1);
            Vec3 point = pivot + dir * pixel * 82;
            if (cam.ToScreen(point) is { } screen && screen.DistanceTo(center) > 18)
                result.Add(new(new("move." + axis, point, dir, axis), screen, center));
        }
        if (Builder.IsCustom) return result;
        BrushShape display = Builder.DisplayShape();
        if (display.Faces.Count == 0) return result;
        var (min, max) = display.Bounds();
        Vec3 localCenter = (min + max) / 2;
        int index = 0;
        foreach (BuilderSizeHandle size in BuilderHandles.For(Builder.Spec))
        {
            string id = "size." + index++;
            Vec3 point = pivot + Builder.Rotation.Rotate(size.Point(Builder.Spec, localCenter));
            Vec3 dir = Builder.Rotation.Rotate(Vec3.Zero.With(size.Axis, size.Sign));
            if (cam.ToScreen(point) is not { } origin || cam.ToScreen(point + dir * pixel * 32) is not { } end) continue;
            Vector2 projected = end - origin;
            if (projected.Length() < 5) continue; // An end-on axis has no stable screen-space drag.
            Vector2 screen = origin + projected.Normalized() * 16;
            // Separate coincident parameters (e.g. a small radius and the movement arrow) with leader lines.
            Vector2 tangent = new(-projected.Normalized().Y, projected.Normalized().X);
            int attempts = 0;
            while (result.Any(h => h.Point.DistanceTo(screen) < 18) && attempts++ < 12) screen += tangent * 20;
            result.Add(new(new(id, point, dir, size.Axis, size), screen, origin));
        }
        return result;
    }

    private ScreenHandle? Hit(EditorViewport view, Vector2 mouse) => Handles(view)
        .Where(h => h.Point.DistanceTo(mouse) <= 9 || h.Handle.Id.StartsWith("move.") && h.Handle.Axis >= 0
            && Picking.SegmentDistance(h.Origin.Lerp(h.Point, .25f), h.Point, mouse) <= 6)
        .OrderBy(h => h.Point.DistanceTo(mouse)).FirstOrDefault();

    private void UpdateHandle(EditorViewport view, Vector2 mouse)
    {
        if (_updating || _drag is not { } drag || _start is not { } start || _snap is not { } snap) return;
        _current = mouse;
        if (!_dragMoved && drag.Mouse.DistanceTo(mouse) <= DragThreshold) return;
        _dragMoved = true;
        _updating = true;
        try
        {
            var pointerRay = view.Cam.Ray(mouse);
            double raw = drag.Handle.Axis >= 0
                ? DragMath.LineParam(drag.Handle.Point, drag.Handle.Direction, pointerRay.Origin, pointerRay.Direction) - drag.LineStart
                : 0;
            if (mouse.DistanceTo(drag.Mouse) < .01f)
            {
                Builder.Restore(start, false);
                _error = null;
                snap.Release();
            }
            else if (drag.Handle.Size is { } size)
            {
                double distance = _tools.SnapValue(size.Value(start.Spec) + raw) - size.Value(start.Spec);
                if (_tools.GridSnap)
                {
                    int axis = Enumerable.Range(0, 3).MaxBy(a => Math.Abs(drag.Handle.Direction[a]));
                    if (Math.Abs(drag.Handle.Direction[axis]) > .99999)
                        distance = (_tools.SnapValue(drag.Handle.Point[axis] + raw * drag.Handle.Direction[axis]) - drag.Handle.Point[axis]) / drag.Handle.Direction[axis];
                }
                SnapFace? face = SizingFace(start, size, drag.Min, drag.Max);
                if (face is not null && snap.Extrude(face, raw, -size.Value(start.Spec)) is { } stuck) distance = stuck.Value;
                var candidate = size.Candidate(start, distance);
                if (!Builder.TryApply(candidate.Spec, candidate.Location, start.Rotation, out _error)
                    || Math.Abs(size.Value(candidate.Spec) - size.Value(start.Spec) - distance) > 1e-5) snap.Release();
            }
            else
            {
                Vec3 delta;
                int[] free;
                if (drag.Handle.Axis >= 0)
                {
                    free = [drag.Handle.Axis];
                    delta = drag.Handle.Direction * raw;
                }
                else
                {
                    var ray = view.Cam.Ray(mouse);
                    delta = (DragMath.PlaneHit(start.Location, drag.PlaneNormal, ray.Origin, ray.Direction) ?? drag.PlaneStart) - drag.PlaneStart;
                    free = view.Cam.IsOrtho ? [.. new[] { 0, 1, 2 }.Where(a => a != ViewAxes.DepthAxis(view.Cam.Kind))] : [0, 1, 2];
                }
                MoveSnap stuck = snap.Move(delta, free);
                Vec3 location = _tools.Snap(start.Location + delta, free);
                foreach (int axis in free) if (stuck.Stuck(axis)) location = location.With(axis, start.Location[axis] + delta[axis] + stuck.Offset[axis]);
                Builder.Location = location;
                Builder.Moved();
                snap.Moved(location - start.Location);
            }
            SyncFields();
            ViewportLayout.Instance?.RedrawAll();
        }
        finally { _updating = false; }
    }

    private static SnapFace? SizingFace(BuilderBrush.State start, BuilderSizeHandle size, Vec3 min, Vec3 max)
    {
        // A semantic dimension plane, including room interiors rather than the outside of shell walls.
        Vec3 center = size.Point(start.Spec, (min + max) / 2);
        int a = (size.Axis + 1) % 3, b = (size.Axis + 2) % 3;
        Vec3 u = Vec3.Zero.With(a, Math.Max((max[a] - min[a]) / 2, 1)), v = Vec3.Zero.With(b, Math.Max((max[b] - min[b]) / 2, 1));
        Vec3 World(Vec3 p) => start.Location + start.Rotation.Rotate(p);
        return SnapFace.Of(null, 0, SnapSource.Brush,
            new[] { World(center-u-v), World(center+u-v), World(center+u+v), World(center-u+v) },
            normal: start.Rotation.Rotate(Vec3.Zero.With(size.Axis, size.Sign)));
    }

    private void PreviewPlacement(EditorViewport view, Vector2 mouse)
    {
        if (_start is not { } start) return;
        _view = view;
        _placeValid = false;
        _error = null;
        if (view.Cam.IsOrtho)
        {
            int depth = ViewAxes.DepthAxis(view.Cam.Kind);
            Builder.Location = _tools.Snap(view.Cam.PlanePoint(mouse, start.Location[depth]), [.. new[] { 0, 1, 2 }.Where(a => a != depth)]);
            _placeValid = true;
        }
        else if (Picking.Surface(view.Cam, _tools.Context.Project, mouse) is { } hit)
        {
            // Use the full rotated compound shape; its origin need not be at the bounds center.
            Vec3[] points = Builder.DisplayShape().Faces.SelectMany(f => f.Vertices).Select(start.Rotation.Rotate).ToArray();
            if (points.Length == 0) { _error = Builder.Problem ?? "Choose valid dimensions first"; return; }
            double support = points.Min(p => Vec3.Dot(p, hit.Normal));
            int normalAxis = Placement.DominantAxis(hit.Normal);
            Vec3 location = _tools.Snap(hit.Point - hit.Normal * support, [.. new[] { 0, 1, 2 }.Where(a => a != normalAxis)]);
            // Restore exact contact even on sloped surfaces after tangential grid rounding.
            Builder.Location = location + hit.Normal * (Vec3.Dot(hit.Point - location, hit.Normal) - support);
            _placeValid = true;
        }
        else
        {
            Builder.Location = start.Location;
            _error = "Point at a surface to place the brush";
        }
        Builder.Moved();
        ViewportLayout.Instance?.RedrawAll();
    }

    private void TrackBox()
    {
        if (_press is not { } start || _view is not { } view || _start is not { } snapshot || !view.Cam.IsOrtho || start.DistanceTo(_current) <= DragThreshold) return;
        _corners = Corners(view.Cam, start, _current);
        var (p0, p1) = _corners.Value;
        int depth = ViewAxes.DepthAxis(view.Cam.Kind);
        BuilderSpec spec = snapshot.Spec.Clone();
        Vec3 center = snapshot.Location;
        foreach (int axis in new[] { 0, 1, 2 }.Where(a => a != depth))
        {
            spec.Numbers[SizeKeys[axis]] = Math.Max(Math.Abs(p1[axis] - p0[axis]), _tools.GridSnap ? _tools.GridSize : 1);
            center = center.With(axis, (p0[axis] + p1[axis]) / 2);
        }
        Builder.TryApply(spec, center, Rot.Zero, out _error);
        SyncFields();
        ViewportLayout.Instance?.RedrawAll();
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        _snap?.Draw(view, canvas);
        if (_mode != BrushMode.Handles)
        {
            if (Builder.GestureActive && view.Cam.ToScreen(Builder.Location) is { } dimensionsAt)
            {
                var bounds = Builder.DisplayShape().Bounds();
                string dimensions = string.Join(" x ", new[] { 0, 1, 2 }.Select(a => ViewTags.Number(bounds.Max[a] - bounds.Min[a])));
                ViewTags.BesideStack(canvas, dimensionsAt, [ViewTags.Value("Size (X/Y/Z)", dimensions)]);
            }
            if (_view == view) ViewTags.BesideStack(canvas, _current, [ViewTags.Value(_mode == BrushMode.Place ? "Place Here" : "Draw Box",
                _error ?? (_mode == BrushMode.Place ? "Click to confirm · Escape cancels" : "Release to finish · Escape cancels"))]);
            return;
        }
        if (_drag is { } activeDrag && _view != view && view.Cam.ToScreen(Builder.Location) is { } readoutAt)
            ViewTags.BesideStack(canvas, readoutAt, [ViewTags.Value(activeDrag.Handle.Size?.Label ?? "Move",
                _error ?? (activeDrag.Handle.Size is { } dimension ? ViewTags.Number(dimension.ReadoutValue(Builder.Spec)) : Builder.Location.ToString()))]);
        foreach (ScreenHandle visual in Handles(view))
        {
            Handle h = visual.Handle;
            bool active = _drag?.Handle.Id == h.Id || _view == view && _hover == h.Id;
            Color color = active ? new Color(1, 1, .4f) : h.Axis < 0 ? SceneStyle.BuilderBrush : SceneStyle.AxisColor(h.Axis);
            float width = active ? 3 : 1.5f;
            canvas.DrawLine(visual.Origin, visual.Point, color, width);
            if (h.Size is { Radial: true }) canvas.DrawArc(visual.Point, 6, 0, Mathf.Tau, 24, color, width, true);
            else if (h.Size is not null || h.Axis < 0)
            {
                var rect = new Rect2(visual.Point - Vector2.One * 5, Vector2.One * 10);
                if (active) canvas.DrawRect(rect, color);
                else canvas.DrawRect(rect, color, false, width);
            }
            else
            {
                Vector2 dir = (visual.Point - visual.Origin).Normalized();
                Vector2 side = new(-dir.Y, dir.X);
                canvas.DrawColoredPolygon([visual.Point + dir * 9, visual.Point - dir * 4 + side * 5, visual.Point - dir * 4 - side * 5], color);
            }
            if (active && (_drag is not null || _view == view))
            {
                string label = h.Size?.Label ?? (h.Axis < 0 ? "Move in view plane" : "Move " + "XYZ"[h.Axis]);
                string value = _error ?? (h.Size is { } size ? ViewTags.Number(size.ReadoutValue(Builder.Spec)) : Builder.Location.ToString());
                ViewTags.BesideStack(canvas, _view == view ? _current : visual.Point, [ViewTags.Value(label, value), .. (_snap?.Readout() ?? [])]);
            }
        }
    }

    public void BuildOptions(HBoxContainer bar)
    {
        _numberFields.Clear();
        _commitButtons.Clear();
        OptionButton kinds = ToolOptions.Options(bar, "Builder shape", 132);
        IReadOnlyList<string> list = BuilderBrush.Kinds();
        for (int i = 0; i < list.Count; i++)
        {
            kinds.AddItem(BuilderBrush.KindTitle(list[i]), i);
            if (string.Equals(list[i], Builder.Spec.Kind, StringComparison.OrdinalIgnoreCase))
            {
                kinds.Select(i);
            }
        }

        kinds.ItemSelected += i => { Cancel(); Builder.SetKind(list[(int)i]); };
        if (Builder.IsCustom)
        {
            int pieces = Builder.Pieces().Count;
            Label custom = ToolOptions.Name(bar, pieces == 1 ? "Custom shape" : $"Custom shape · {pieces} pieces");
            custom.TooltipText = "Choose a builder shape to enable parametric sizing handles";
            custom.MouseFilter = Control.MouseFilterEnum.Pass;
        }

        ToolOptions.Command(bar, "Geometry", "Browse common floors, walls and builder shapes", () => DockTabs.ShowPanel("Geometry"));
        Button place = ToolOptions.Command(bar, "Place Here", "Preview on a surface, click to confirm; Escape cancels", () => SetMode(BrushMode.Place));
        place.ToggleMode = true;
        place.ButtonPressed = _mode == BrushMode.Place;
        Button draw = ToolOptions.Command(bar, "Draw Box", "Drag a new box in a 2D view; Escape cancels", () => SetMode(BrushMode.Box));
        draw.ToggleMode = true;
        draw.ButtonPressed = _mode == BrushMode.Box;
        draw.Disabled = Builder.IsCustom || Builder.Spec.Kind != "cube";
        var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(160, 44) };
        bar.AddChild(scroll);
        var fields = new HBoxContainer();
        scroll.AddChild(fields);

        // The builder's own order (sizes along X, Y, Z first, then the rest), its on/off settings as check boxes.
        IReadOnlyList<BuilderParam> declared = BuilderCatalog.Find(Builder.Spec.Kind)?.Params ?? [];
        List<string> order = declared.Select(p => p.Name).ToList();
        int Rank(string key) => order.IndexOf(key) is var i and >= 0 ? i : int.MaxValue;
        foreach ((string key, double value) in Builder.Spec.Numbers.OrderBy(kv => Rank(kv.Key)).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            BuilderParam? param = declared.FirstOrDefault(p => p.Name == key);
            string tip = param is null ? key : $"{param.Summary} ({key})";
            string k = key;
            if (param?.Type == ParamType.Flag)
            {
                var check = new CheckBox { Text = PanelUi.Words(key), ButtonPressed = value != 0, TooltipText = tip, FocusMode = Control.FocusModeEnum.None };
                check.Toggled += on => { Cancel(); Builder.SetNumber(k, on ? 1 : 0); };
                fields.AddChild(check);
                continue;
            }

            ToolOptions.Name(fields, ParameterLabel(key));
            SpinBox spin = PanelUi.Spin(fields, param?.Min ?? -65536, param?.Max ?? 65536, 1, value, tip);
            spin.AllowGreater = false;
            spin.AllowLesser = false;
            _numberFields[key] = spin;
            spin.ValueChanged += v =>
            {
                if (_syncFields) return;
                Cancel();
                Builder.SetNumber(k, v);
                if (Builder.Problem is { } problem)
                {
                    _tools.Context.SetStatus(problem);
                }
            };
        }

        foreach (BuilderParam param in declared.Where(p => p.IsText))
        {
            string key = param.Name;
            ToolOptions.Name(fields, PanelUi.Words(key));
            if (param.Type == ParamType.Choice)
            {
                OptionButton choice = ToolOptions.Options(fields, param.Summary, 100);
                foreach (string item in param.Choices) choice.AddItem(item);
                choice.Select(Math.Max(0, param.Choices.ToList().IndexOf(Builder.Spec.Strings.GetValueOrDefault(key, param.DefaultText ?? ""))));
                choice.ItemSelected += i => { Cancel(); Builder.SetText(key, param.Choices[(int)i]); };
            }
            else
            {
                var text = new LineEdit { Text = Builder.Spec.Strings.GetValueOrDefault(key, param.DefaultText ?? ""),
                    CustomMinimumSize = new Vector2(150, 0), TooltipText = param.Summary };
                fields.AddChild(text);
                text.TextSubmitted += value => { Cancel(); Builder.SetText(key, value); };
            }
        }

        bar.AddChild(new VSeparator());
        foreach (string action in new[] { "brush.add", "brush.subtract" })
        {
            var button = new ActionButton(_tools.Context, action, action == "brush.add" ? "Add" : "Subtract", ToolbarIcons.For(action), showLabel: true);
            _commitButtons.Add(button);
            bar.AddChild(button);
        }
        ToolOptions.Hint(bar, Builder.IsCustom ? "Choose a shape for sizing handles" : "Arrows move; squares and circles resize", "Escape cancels");
    }

    /// <summary>
    /// The box's two corners on the view plane: the fixed one (where the drag started) on the grid, the dragged one
    /// stuck to neighbour faces on each screen axis where one is within the snap distance, else on the grid.
    /// </summary>
    private (Vec3 P0, Vec3 P1) Corners(ViewCamera cam, Vector2 a, Vector2 b)
    {
        int depth = ViewAxes.DepthAxis(cam.Kind);
        double z = Builder.Location[depth];
        int[] axes = [.. new[] { 0, 1, 2 }.Where(x => x != depth)];
        Vec3 p0 = _tools.Snap(cam.PlanePoint(a, z), axes);
        Vec3 raw = cam.PlanePoint(b, z);
        Vec3 p1 = _tools.Snap(raw, axes);
        if (_snap is not { } snap)
        {
            return (p0, p1);
        }

        // The dragged corner's sides of the box (its depth as the builder's now) stick like a move of those faces.
        (Vec3 bMin, Vec3 bMax) = Builder.DisplayShape().Bounds();
        double half = (bMax[depth] - bMin[depth]) / 2;
        Vec3 min = Vec3.Min(p0, raw).With(depth, z - half), max = Vec3.Max(p0, raw).With(depth, z + half);
        IReadOnlyList<SnapFace> sides = OrientedBox.Aligned(min, max).Faces(null, SnapSource.Brush, false)
            .Where(f => f.IsAxis && f.Axis != depth && Math.Abs(f.Coord - raw[f.Axis]) < 1e-3).ToList();
        MoveSnap stick = snap.Move(new MovingShape(min, max, sides, []), Vec3.Zero, axes);
        foreach (int axis in axes)
        {
            if (stick.Stuck(axis))
            {
                p1 = p1.With(axis, raw[axis] + stick.Offset[axis]);
            }
        }

        snap.Moved(p1 - raw);
        return (p0, p1);
    }

}
