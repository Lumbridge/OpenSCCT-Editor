using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using Poly = OpenScct.Editor.Core.Geometry.Poly;
using ShapeTransform = OpenScct.Editor.Core.Geometry.ShapeTransform;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// A handle on every side of the one selected brush (Select, Move and Scale): drag it along the side's normal to push
/// that side out or pull it in, with the grid and neighbour snapping of the other dimension handles. A side cut into
/// several faces (a recovered brush's material cuts) moves as one. The brush keeps its place and its other sides, so a
/// room, an opening, a window or a doorway is resized one wall at a time.
/// </summary>
public sealed class BrushSideHandles
{
    private const float HandlePixels = 6, HitPixels = 10, StalkPixels = 18;
    private static readonly Color s_colour = Colors.DeepSkyBlue;

    private readonly ToolManager tools;
    private Drag? _drag;
    private bool _edited;
    private int _hover = -1;

    public BrushSideHandles(ToolManager manager)
    {
        tools = manager;
        tools.DragInterrupted += () => { _drag = null; _edited = false; };
    }

    /// <summary>A side's handle: one of its faces, the middle of the side and its outward direction (world).</summary>
    private sealed record Handle(int Face, Vec3 Centre, Vec3 Normal);

    private sealed record Drag(Brush Start, Handle Handle, DimensionDrag Sizing, double Minimum, string Merge, bool Baked);

    public bool IsDragging => _drag is not null;

    private Brush? Selected => tools.Context.Selection.Items is { Count: 1 } refs
        && tools.Context.Project.FindBrush(refs[0].Id) is { Stair: null } b && !b.Editor.Locked ? b : null;

    private static IEnumerable<Handle> Of(Brush b)
    {
        ShapeTransform t = ShapeTransform.For(b);
        foreach (List<int> side in Extrude.Sides(b))
        {
            Vec3 sum = Vec3.Zero, normal = Vec3.Zero;
            double area = 0;
            foreach (int i in side)
            {
                List<Vec3> world = t.FaceToWorld(b.Shape.Faces[i]);
                double a = Poly.Area(world);
                sum += Poly.Average(world) * a;
                area += a;
                if (normal == Vec3.Zero)
                {
                    normal = Poly.Normal(world);
                }
            }

            if (area > 1e-6 && normal != Vec3.Zero)
            {
                yield return new Handle(side[0], sum / area, normal);
            }
        }
    }

    /// <summary>The handles a view can use: where the side's middle and the handle (a short stalk out along the normal)
    /// fall on screen. A side seen end-on has none: there is no direction on screen to drag it in.</summary>
    private static IEnumerable<(Handle Handle, Vector2 Origin, Vector2 Screen)> ScreenHandles(Brush b, EditorViewport view)
    {
        foreach (Handle h in Of(b))
        {
            if (view.Cam.ToScreen(h.Centre) is not { } origin
                || view.Cam.ToScreen(h.Centre + (h.Normal * (view.Cam.UnitsPerPixelAt(h.Centre) * 40))) is not { } along
                || along.DistanceTo(origin) < 5)
            {
                continue;
            }

            yield return (h, origin, origin + ((along - origin).Normalized() * StalkPixels));
        }
    }

    public bool Input(EditorViewport view, InputEvent e)
    {
        if (_drag is { } drag)
        {
            if (e is InputEventMouseMotion motion)
            {
                Step(view, drag, motion);
            }
            else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            {
                _drag = null;
                tools.FinishDrag();
                ViewportLayout.Instance?.RedrawAll();
            }

            return true;
        }

        if (Selected is not { } owner)
        {
            return false;
        }

        if (e is InputEventMouseMotion move)
        {
            int hover = Hit(owner, view, move.Position)?.Face ?? -1;
            if (hover != _hover)
            {
                _hover = hover;
                view.Redraw();
            }

            return false;
        }

        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: false } press
            || Hit(owner, view, press.Position) is not { } handle)
        {
            return false;
        }

        // The side stops a unit short of where it would leave the brush with no thickness.
        double minimum = Math.Min(0, 1 - Extrude.Room(owner, handle.Face));
        double extent = Math.Sqrt(Math.Max(256, Extrude.Sides(owner).Find(s => s.Contains(handle.Face))!
            .Sum(i => Poly.Area(ShapeTransform.For(owner).FaceToWorld(owner.Shape.Faces[i]))))) / 2;
        var sizing = new DimensionDrag(tools, view, press.Position, handle.Centre, handle.Normal, 0, 1, ObjectRef.Brush(owner.Id), extent);
        _drag = new Drag(owner.Clone(), handle, sizing, minimum, Guid.NewGuid().ToString("N"),
            BakedContent.IsBaked(owner) && BakedContent.BakedWorldKept(tools.Context.Project));
        _edited = false;
        return true;
    }

    private void Step(EditorViewport view, Drag drag, InputEventMouseMotion motion)
    {
        double distance = drag.Sizing.Value(motion.Position, motion.AltPressed || Godot.Input.IsKeyPressed(Key.Alt), drag.Minimum);
        if (Math.Abs(distance) < 1e-7 && !_edited)
        {
            return;
        }

        try
        {
            Brush current = tools.Context.Project.FindBrush(drag.Start.Id)!;
            VertexEditResult result = Extrude.Side(drag.Start, drag.Handle.Face, distance);
            tools.DragStep(() => tools.Context.Execute(new ReplaceObjectsCommand("Move brush side", drag.Merge).Brush(current.Clone(), result.Brush)));
            _edited = true;
            string note = result.Warning is { } warning ? $" ({warning})"
                : drag.Baked ? " (the baked world shows this brush as imported until it is built from the brushes: Map Settings, Baked world)"
                : "";
            tools.Context.SetStatus($"Side {(distance >= 0 ? "out" : "in")} {Math.Abs(distance):0.##}{drag.Sizing.Snap.Status()}{note}");
        }
        catch (ArgumentException ex)
        {
            drag.Sizing.Snap.Release();
            tools.Context.SetStatus(ex.Message);
        }

        ViewportLayout.Instance?.RedrawAll();
    }

    private static Handle? Hit(Brush owner, EditorViewport view, Vector2 mouse)
    {
        Handle? best = null;
        float nearest = HitPixels;
        foreach ((Handle h, Vector2 _, Vector2 screen) in ScreenHandles(owner, view))
        {
            float d = screen.DistanceTo(mouse);
            if (d <= nearest)
            {
                nearest = d;
                best = h;
            }
        }

        return best;
    }

    public bool Cancel()
    {
        if (_drag is null)
        {
            return false;
        }

        if (_edited)
        {
            tools.Context.Session.Commands.Undo();
        }

        tools.Context.Session.Commands.EndMerge();
        _drag = null;
        tools.Context.SetStatus("Move brush side cancelled");
        ViewportLayout.Instance?.RedrawAll();
        return true;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        if (Selected is not { } b)
        {
            return;
        }

        // While a side is dragged only its own handle shows, where the side now is.
        int dragged = _drag?.Handle.Face ?? -1;
        foreach ((Handle h, Vector2 origin, Vector2 screen) in ScreenHandles(b, view))
        {
            bool active = _drag is not null ? Extrude.Sides(b).Find(s => s.Contains(dragged))?.Contains(h.Face) == true : h.Face == _hover;
            if (_drag is not null && !active)
            {
                continue;
            }

            Color colour = active ? SceneStyle.Selection : s_colour;
            canvas.DrawLine(origin, screen, colour, active ? 2 : 1);
            canvas.DrawCircle(screen, active ? HandlePixels + 1 : HandlePixels, colour);
            canvas.DrawCircle(screen, active ? HandlePixels + 1 : HandlePixels, Colors.Black, false, 1);
        }

        _drag?.Sizing.Snap.Draw(view, canvas);
    }
}
