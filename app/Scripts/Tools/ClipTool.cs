using Godot;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// Brush clip (C). Click two points in a 2D view (the plane runs along that view's depth) or three points anywhere;
/// the part of the selected brushes that will be cut away is tinted red. Enter clips (keeps the untinted part),
/// Shift+Enter splits into two brushes, F flips the side, Escape drops the points. The Brush/Clip menu has the same
/// commands plus clip planes through the selection's centre.
/// </summary>
public sealed class ClipTool : IViewportTool
{
    private static readonly Color CutLine = new(1f, 0.35f, 0.35f);

    /// <summary>The dashes of the discarded part's outline, in pixels.</summary>
    private const float DashLength = 6;
    private readonly ToolManager _tools;
    private readonly List<Vec3> _points = new();
    private int _depthAxis = 2;
    private bool _flipped;
    private Plane3? _fixed;

    public ClipTool(ToolManager tools) => _tools = tools;

    public string Id => "clip";

    public string Title => "Clip";

    public string Shortcut => "C";

    private EditorContext Ctx => _tools.Context;

    /// <summary>The clip plane the points (or a menu command) give, already flipped when asked; null when unset.</summary>
    public Plane3? Plane
    {
        get
        {
            Plane3? p = _fixed ?? ClipEdit.PlaneFrom(_points, _depthAxis);
            return p is { } plane && _flipped ? plane.Flipped : p;
        }
    }

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                AddPoint(view, press.Position);
                return true;
            case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } enter when Plane is not null:
                Apply(split: enter.ShiftPressed);
                return true;
            case InputEventKey { Pressed: true, Keycode: Key.F, ShiftPressed: false, AltPressed: false, CtrlPressed: false } when Plane is not null:
                Flip();
                return true;
        }

        return false;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        ViewCamera cam = view.Cam;
        DrawDiscarded(cam, canvas);

        // The clip points, their numbers and the line through them go over the preview at full strength.
        for (int i = 0; i < _points.Count; i++)
        {
            FacePicking.DrawMarker(cam, canvas, _points[i], CutLine, 5);
            if (cam.ToScreen(_points[i]) is { } s)
            {
                FacePicking.DrawLabel(canvas, s + new Vector2(8, -8), (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), CutLine);
            }

            if (i > 0 && cam.ToScreen(_points[i - 1]) is { } a && cam.ToScreen(_points[i]) is { } b)
            {
                canvas.DrawLine(a, b, CutLine, 1.5f, antialiased: true);
            }
        }

    }

    /// <summary>The part a clip throws away: a light wash on the faces the view looks onto (so washes never stack) and
    /// a dashed outline on every face; the kept part keeps its selection look.</summary>
    private void DrawDiscarded(ViewCamera cam, Control canvas)
    {
        if (Plane is not { } plane)
        {
            return;
        }

        Vec3 forward = cam.LookDirection;
        foreach (Brush b in Targets())
        {
            (Brush? front, Brush? _) = BrushOps.Split(b, plane);
            if (front is null)
            {
                continue;
            }

            foreach (BrushFace f in front.WorldFaces())
            {
                if (Screen(cam, f.Vertices) is not { } pts)
                {
                    continue;
                }

                if (pts.Length >= 3 && -Vec3.Dot(forward, f.Normal()) > SceneStyle.FaceFacing2D)
                {
                    canvas.DrawColoredPolygon(pts, SceneStyle.ClipDiscardFill);
                }

                for (int i = 0; i < pts.Length; i++)
                {
                    canvas.DrawDashedLine(pts[i], pts[(i + 1) % pts.Length], SceneStyle.ClipDiscardLine, 1.5f, DashLength, antialiased: true);
                }
            }
        }
    }

    /// <summary>A face's corners on screen, or null when one is behind the camera.</summary>
    private static Vector2[]? Screen(ViewCamera cam, IReadOnlyList<Vec3> poly)
    {
        var pts = new Vector2[poly.Count];
        for (int i = 0; i < poly.Count; i++)
        {
            if (cam.ToScreen(poly[i]) is not { } p)
            {
                return null;
            }

            pts[i] = p;
        }

        return pts;
    }

    public void BuildOptions(HBoxContainer bar)
    {
        foreach ((string text, string action) in new[] { ("Clip", "clip.apply"), ("Split", "clip.split"), ("Flip", "clip.flip"), ("Clear", "clip.clear") })
        {
            ToolOptions.Action(bar, Ctx, action, text);
        }

        ToolOptions.Hint(bar, "Click 2 points in a 2D view or 3 anywhere", "Enter clips", "Shift+Enter splits", "F flips", "Esc clears");
    }

    public bool Cancel()
    {
        if (_points.Count == 0 && _fixed is null)
        {
            return false;
        }

        Clear();
        return true;
    }

    public void Clear()
    {
        _points.Clear();
        _fixed = null;
        _flipped = false;
        ViewportLayout.Instance?.RedrawAll();
    }

    public void Flip()
    {
        _flipped = !_flipped;
        ViewportLayout.Instance?.RedrawAll();
        Ctx.SetStatus("Clip side flipped");
    }

    /// <summary>Sets the plane through the selection's centre across <paramref name="axis"/> (keeps the low side).</summary>
    public void CentrePlane(int axis)
    {
        if (ObjectGeometry.Bounds(Ctx.Project, Ctx.Selection.Items) is not { } b)
        {
            Ctx.SetStatus("Clip: select brushes first");
            return;
        }

        _points.Clear();
        _flipped = false;
        _fixed = ClipEdit.AxisPlane(axis, (b.Min + b.Max) / 2);
        ViewportLayout.Instance?.RedrawAll();
        Ctx.SetStatus($"Clip plane across {"XYZ"[axis]} through the selection's centre; Enter clips, F flips");
    }

    /// <summary>Clips (or splits) the selected brushes by the pending plane, as one undo step.</summary>
    public void Apply(bool split)
    {
        if (Plane is not { } plane)
        {
            Ctx.SetStatus("Clip: place two or three points first");
            return;
        }

        List<string> ids = Targets().Select(b => b.Id).ToList();
        if (ids.Count == 0)
        {
            Ctx.SetStatus("Clip: select the brushes to cut");
            return;
        }

        BrushListEdit edit = ClipEdit.Run(Ctx.Project, ids, plane, split);
        if (edit.IsEmpty)
        {
            Ctx.SetStatus("Clip: the plane misses the selected brushes");
            return;
        }

        Ctx.Execute(edit.Build());
        Ctx.Selection.Set(edit.Changed.Concat(edit.Added).Select(ObjectRef.Brush));
        Ctx.SetStatus($"{(split ? "Split" : "Clipped")} {edit.Changed.Count} brush(es)" +
            (edit.Added.Count > 0 ? $", new {string.Join(", ", edit.Added)}" : "") + (edit.Removed.Count > 0 ? $", removed {string.Join(", ", edit.Removed)}" : ""));
        Clear();
    }

    private List<Brush> Targets() =>
        Ctx.Selection.Items.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)).Select(r => Ctx.Project.FindBrush(r.Id)).OfType<Brush>().ToList();

    private void AddPoint(EditorViewport view, Vector2 mouse)
    {
        if (_points.Count >= 3 || _fixed is not null)
        {
            _points.Clear();
            _fixed = null;
        }

        Vec3 at;
        if (view.Cam.IsOrtho)
        {
            int axis = ViewAxes.DepthAxis(view.Kind);
            if (_points.Count == 0)
            {
                _depthAxis = axis;
            }

            double depth = ObjectGeometry.Bounds(Ctx.Project, Ctx.Selection.Items) is { } b ? (b.Min[axis] + b.Max[axis]) / 2 : 0;
            at = _tools.Snap(view.Cam.PlanePoint(mouse, depth), [.. new[] { 0, 1, 2 }.Where(a => a != axis)]);
        }
        else if (Picking.Surface(view.Cam, Ctx.Project, mouse) is { } hit)
        {
            at = _tools.Snap(hit.Point);
        }
        else
        {
            return;
        }

        _points.Add(at);
        ViewportLayout.Instance?.RedrawAll();
        Ctx.SetStatus(Plane is null ? $"Clip point {_points.Count} at {at}" : "Clip plane set: Enter clips, Shift+Enter splits, F flips");
    }
}

/// <summary>The Brush/Clip menu: clip and split by the clip tool's plane, flip, clear, and centre planes.</summary>
[ShellPart(Order = -21)]
public sealed class ClipActions : IShellPart
{
    public void Attach(MainWindow window, EditorContext context)
    {
        ClipTool clip = ToolManager.Instance!.Tools.OfType<ClipTool>().First();
        bool HasPlane() => clip.Plane is not null;
        void Add(string id, string title, int order, Action run, Func<bool>? enabled = null, string? description = null) =>
            context.AddAction(new EditorAction(id, title, "Brush/Clip", run) { Order = order, IsEnabled = enabled ?? (() => true), Description = description });

        Add("clip.apply", "Clip", 100, () => clip.Apply(split: false), HasPlane, "Keep the part of the selected brushes behind the plane");
        Add("clip.split", "Split", 110, () => clip.Apply(split: true), HasPlane, "Cut the selected brushes in two");
        Add("clip.flip", "Flip Side", 120, clip.Flip, HasPlane);
        Add("clip.clear", "Clear Points", 130, clip.Clear, HasPlane);
        for (int axis = 0; axis < 3; axis++)
        {
            int a = axis;
            string n = "XYZ"[axis].ToString();
            Add("clip.centre" + n, $"Plane Across {n} Through Centre", 200 + axis, () => clip.CentrePlane(a),
                () => context.Selection.Items.Any(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)));
        }
    }
}
