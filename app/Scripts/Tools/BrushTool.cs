using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// Shapes the builder brush (B). Click moves it there (sitting on the surface in the perspective view); dragging a
/// box in a 2D view sets its position and its size on the two screen axes (for builders with x/y/z sizes); with
/// Neighbour Snap on, the dragged corner's sides stick to nearby faces first (Alt held inverts it). The toolbar shows
/// the builder kind and its parameters; Add (Alt+A) and Subtract (Alt+S) put it in the map.
/// </summary>
public sealed class BrushTool : IViewportTool
{
    private const float DragThreshold = 4;

    /// <summary>The cube's size per axis (X, Y, Z).</summary>
    private static readonly string[] SizeKeys = ["breadth", "width", "height"];

    private readonly ToolManager _tools;
    private Vector2? _press;
    private Vector2 _current;
    private EditorViewport? _view;
    private NeighbourDrag? _snap;
    private (Vec3 P0, Vec3 P1)? _corners;

    public BrushTool(ToolManager tools)
    {
        _tools = tools;
        // Neighbour snap turned on or off mid-drag (Shift+G) shows at once.
        tools.Changed += () =>
        {
            if (_press is not null && _snap is not null)
            {
                Track();
            }
        };
    }

    public string Id => "brush";

    public string Title => "Builder Brush";

    public string Shortcut => "B";

    private BuilderBrush Builder => _tools.Builder;

    public void Activate() => _tools.NotifyChanged();

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _press = press.Position;
                _current = press.Position;
                _view = view;
                _snap = view.Cam.IsOrtho && HasSizes() ? new NeighbourDrag(_tools, view, SnapUse.Resize, [], () => null) : null;
                return true;
            case InputEventMouseMotion motion when _press is not null && _view == view:
                _current = motion.Position;
                if (_snap is not null)
                {
                    _snap.AltHeld = motion.AltPressed || Godot.Input.IsKeyPressed(Key.Alt);
                }

                Track();
                return true;
            case InputEventKey { Keycode: Key.Alt } alt when _press is not null && _snap is not null:
                _snap.AltHeld = alt.Pressed;
                Track();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when _press is { } start && _view == view:
                _press = null;
                if (view.Cam.IsOrtho && start.DistanceTo(release.Position) > DragThreshold && HasSizes())
                {
                    SizeFromBox(view.Cam, start, release.Position);
                }
                else
                {
                    MoveTo(view.Cam, release.Position);
                }

                _snap = null;
                _corners = null;
                ViewportLayout.Instance?.RedrawAll();
                return true;
        }

        return false;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        if (_press is not { } start || _view is not { } dragView || !dragView.Cam.IsOrtho || start.DistanceTo(_current) <= DragThreshold)
        {
            return;
        }

        // The box as it will be made (grid- and neighbour-snapped corners); the faces it sticks to show in every view.
        _snap?.Draw(view, canvas);
        if (view != dragView)
        {
            return;
        }

        Rect2 r = _corners is var (p0, p1) && view.Cam.ToScreen(p0) is { } s0 && view.Cam.ToScreen(p1) is { } s1
            ? new Rect2(s0, Vector2.Zero).Expand(s1)
            : new Rect2(start, Vector2.Zero).Expand(_current);
        canvas.DrawRect(r, new Color(SceneStyle.BuilderBrush, 0.1f));
        canvas.DrawRect(r, SceneStyle.BuilderBrush, filled: false, width: 1);
        if (_snap is { } snap && _corners is var (a, b) && (snap.Stuck || snap.AltHeld))
        {
            int depth = ViewAxes.DepthAxis(view.Cam.Kind);
            string size = string.Join(" × ", new[] { 0, 1, 2 }.Where(x => x != depth).Select(x => ViewTags.Number(Math.Abs(b[x] - a[x]))));
            ViewTags.BesideStack(canvas, _current, [ViewTags.Value("Box", size), .. snap.Readout()]);
        }
    }

    /// <summary>Works the box's corners out for where the mouse is now, and redraws the views.</summary>
    private void Track()
    {
        if (_press is { } start && _view is { } view && view.Cam.IsOrtho && HasSizes() && start.DistanceTo(_current) > DragThreshold)
        {
            _corners = Corners(view.Cam, start, _current);
            if (_snap is { } snap)
            {
                _tools.Context.SetStatus("Builder box" + snap.Status());
            }
        }
        else
        {
            _corners = null;
            _snap?.Clear();
        }

        ViewportLayout.Instance?.RedrawAll();
    }

    public bool Cancel()
    {
        if (_press is null)
        {
            return false;
        }

        _press = null;
        _snap = null;
        _corners = null;
        ViewportLayout.Instance?.RedrawAll();
        return true;
    }

    public void BuildOptions(HBoxContainer bar)
    {
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

        kinds.ItemSelected += i => Builder.SetKind(list[(int)i]);
        if (Builder.IsCustom)
        {
            int pieces = Builder.Pieces().Count;
            Label custom = ToolOptions.Name(bar, pieces == 1 ? "Custom shape" : $"Custom shape · {pieces} pieces");
            custom.TooltipText = "Pick a kind or change a size to go back to the parametric shape";
            custom.MouseFilter = Control.MouseFilterEnum.Pass;
        }

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
                check.Toggled += on => Builder.SetNumber(k, on ? 1 : 0);
                bar.AddChild(check);
                continue;
            }

            ToolOptions.Name(bar, PanelUi.Words(key));
            SpinBox spin = PanelUi.Spin(bar, -65536, 65536, 1, value, tip);
            spin.AllowGreater = true;
            spin.AllowLesser = true;
            spin.ValueChanged += v =>
            {
                Builder.SetNumber(k, v);
                if (Builder.Problem is { } problem)
                {
                    _tools.Context.SetStatus(problem);
                }
            };
        }

        bar.AddChild(new VSeparator());
        bar.AddChild(new ActionButton(_tools.Context, "brush.add", "Add", ToolbarIcons.For("brush.add")));
        bar.AddChild(new ActionButton(_tools.Context, "brush.subtract", "Subtract", ToolbarIcons.For("brush.subtract")));
    }

    /// <summary>True when the shape has a size per axis (the cube): a box dragged in a 2D view resizes it.</summary>
    private bool HasSizes() => SizeKeys.All(Builder.Spec.Numbers.ContainsKey);

    private void MoveTo(ViewCamera cam, Vector2 mouse)
    {
        (Vec3 min, Vec3 max) = Builder.DisplayShape().Bounds();
        Vec3 half = (max - min) / 2;
        int depth = ViewAxes.DepthAxis(cam.Kind);
        Builder.Location = Placement.Point(_tools, cam, mouse, n => Placement.HalfAlong(half, n), depth >= 0 ? Builder.Location[depth] : null);
        Builder.Moved();
        _tools.Context.SetStatus($"Builder brush at {Builder.Location}");
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

    private void SizeFromBox(ViewCamera cam, Vector2 a, Vector2 b)
    {
        int depth = ViewAxes.DepthAxis(cam.Kind);
        int[] axes = [.. new[] { 0, 1, 2 }.Where(x => x != depth)];
        // Release keeps the box the last step showed.
        (Vec3 p0, Vec3 p1) = _corners ?? Corners(cam, a, b);
        Vec3 center = Builder.Location;
        var sizes = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (int axis in axes)
        {
            sizes[SizeKeys[axis]] = Math.Max(Math.Abs(p1[axis] - p0[axis]), _tools.GridSnap ? _tools.GridSize : 1);
            center = center.With(axis, (p0[axis] + p1[axis]) / 2);
        }

        Builder.Resize(sizes, center);
        _tools.Context.SetStatus(string.Create(CultureInfo.InvariantCulture,
            $"Builder brush {Builder.Spec.Get("breadth", 0)} x {Builder.Spec.Get("width", 0)} x {Builder.Spec.Get("height", 0)} at {center}"));
    }
}
