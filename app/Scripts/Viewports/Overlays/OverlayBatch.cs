using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>
/// World-space face fills, lines, dots and labels an overlay wants drawn. Collected once per change and drawn into every view
/// (projected through its camera, clipped at the perspective camera's near plane). Labels are outlined caption text,
/// shortened to <see cref="ViewTags.LabelMaxChars"/>; when the view is zoomed out (more than
/// <see cref="LabelMaxUnitsPerPixel"/> in a 2D view, further than <see cref="LabelMaxDistance"/> in the perspective view)
/// only pinned labels (those of selected objects) are drawn.
/// </summary>
public sealed class OverlayBatch
{
    private const double NearPlane = 8;

    /// <summary>2D views zoomed out beyond this many units per pixel show pinned labels only.</summary>
    public const double LabelMaxUnitsPerPixel = 2;

    /// <summary>The perspective view shows labels this far away (units) only when pinned.</summary>
    public const double LabelMaxDistance = 2000;

    private readonly List<Segment> _segments = new();
    private readonly List<Mark> _marks = new();
    private readonly List<Face> _fills = new();

    public int Count => _segments.Count + _marks.Count + _fills.Count;

    public void Line(Vec3 a, Vec3 b, Color c, float width = 1.5f, bool dashed = false, bool arrow = false) =>
        _segments.Add(new Segment(a, b, c, width, dashed, arrow));

    public void Arrow(Vec3 a, Vec3 b, Color c, float width = 1.5f, bool dashed = false) => Line(a, b, c, width, dashed, arrow: true);

    public void Polyline(IReadOnlyList<Vec3> points, Color c, float width = 1.5f, bool closed = false, bool dashed = false)
    {
        for (int i = 0; i + 1 < points.Count; i++)
        {
            Line(points[i], points[i + 1], c, width, dashed);
        }

        if (closed && points.Count > 2)
        {
            Line(points[^1], points[0], c, width, dashed);
        }
    }

    /// <summary>A circle of <paramref name="radius"/> round <paramref name="centre"/> in the plane of the unit axes u and v.</summary>
    public void Ring(Vec3 centre, Vec3 u, Vec3 v, double radius, Color c, float width = 1f, bool dashed = false, int segments = 48) =>
        Polyline(OverlayShapes.Arc(centre, u, v, radius, 0, Math.Tau, segments), c, width, closed: true, dashed);

    /// <summary>A sphere's three great circles (seen as a circle in every orthographic view).</summary>
    public void Sphere(Vec3 centre, double radius, Color c, float width = 1f)
    {
        Ring(centre, Vec3.UnitX, Vec3.UnitY, radius, c, width);
        Ring(centre, Vec3.UnitX, Vec3.UnitZ, radius, c, width);
        Ring(centre, Vec3.UnitY, Vec3.UnitZ, radius, c, width);
    }

    public void Dot(Vec3 at, Color c, float radius = 3) => _marks.Add(new Mark(at, null, c, radius, false));

    /// <summary>A label at a world point; <paramref name="pinned"/> keeps it when the view is zoomed out (selected objects,
    /// analysis readouts), and <paramref name="whole"/> keeps it whole instead of ending it in "…" (readouts whose end is
    /// the point: "zone leak: put a zone portal here (out of date: check again)").</summary>
    public void Label(Vec3 at, string text, Color c, bool pinned = false, bool whole = false) =>
        _marks.Add(new Mark(at, whole ? text : ViewTags.Shorten(text), c, 0, pinned));

    /// <summary>Fills a face in the 2D views when its outward <paramref name="normal"/> faces the camera, so the faces of a
    /// closed body fill its outline once.</summary>
    public void Fill(IReadOnlyList<Vec3> points, Vec3 normal, Color c) => _fills.Add(new Face(points, normal, c));

    /// <summary>Draws the batch into a view, its labels after its shapes.</summary>
    public void Draw(ViewCamera cam, Control canvas)
    {
        var labels = new LabelLayout();
        Draw(cam, canvas, labels);
        labels.Draw(canvas);
    }

    /// <summary>Draws the shapes into a view and queues the labels in <paramref name="labels"/>, which several batches
    /// share so their labels do not land on each other.</summary>
    public void Draw(ViewCamera cam, Control canvas, LabelLayout labels)
    {
        Vec3 forward = cam.LookDirection;
        if (cam.IsOrtho)
        {
            foreach (Face f in _fills.Where(f => Vec3.Dot(f.Normal, forward) < -0.01))
            {
                DrawFace(cam, canvas, f);
            }
        }

        foreach (Segment s in _segments)
        {
            Vec3 a = s.A, b = s.B;
            if (!cam.IsOrtho && !Clip(cam.Location, forward, ref a, ref b))
            {
                continue;
            }

            if (cam.ToScreen(a) is not { } sa || cam.ToScreen(b) is not { } sb)
            {
                continue;
            }

            if (s.Dashed)
            {
                canvas.DrawDashedLine(sa, sb, s.Color, s.Width, 6);
            }
            else
            {
                canvas.DrawLine(sa, sb, s.Color, s.Width, antialiased: true);
            }

            // An arrowhead far off screen is not seen, and at huge coordinates Godot cannot triangulate it.
            if (s.Arrow && (sb - sa).LengthSquared() > 64 && new Rect2(Vector2.Zero, canvas.Size).Grow(64).HasPoint(sb))
            {
                Vector2 dir = (sb - sa).Normalized() * 9;
                Vector2 side = new Vector2(-dir.Y, dir.X) * 0.5f;
                canvas.DrawPolygon([sb, sb - dir + side, sb - dir - side], [s.Color]);
            }
        }

        bool zoomedOut = cam.IsOrtho && cam.UnitsPerPixel > LabelMaxUnitsPerPixel;
        foreach (Mark m in _marks)
        {
            if ((!cam.IsOrtho && Vec3.Dot(m.At - cam.Location, forward) < NearPlane) || cam.ToScreen(m.At) is not { } at)
            {
                continue;
            }

            if (m.Text is null)
            {
                canvas.DrawCircle(at, m.Radius, m.Color);
            }
            else if (m.Pinned || !(zoomedOut || (!cam.IsOrtho && (m.At - cam.Location).Length > LabelMaxDistance)))
            {
                labels.Add((at + new Vector2(6, -4)).Round(), m.Text, m.Color, m.Pinned);
            }
        }
    }

    private static void DrawFace(ViewCamera cam, Control canvas, Face f)
    {
        var points = new Vector2[f.Points.Count];
        float area = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (cam.ToScreen(f.Points[i]) is not { } s)
            {
                return;
            }

            points[i] = s;
            area += i > 0 ? points[i - 1].Cross(s) : 0;
        }

        // A face seen edge-on has no area to fill (and Godot cannot triangulate it).
        if (points.Length >= 3 && Math.Abs(area + points[^1].Cross(points[0])) >= 2)
        {
            canvas.DrawColoredPolygon(points, f.Color);
        }
    }

    /// <summary>Cuts a segment at the near plane; false when it is wholly behind it.</summary>
    private static bool Clip(Vec3 eye, Vec3 forward, ref Vec3 a, ref Vec3 b)
    {
        double da = Vec3.Dot(a - eye, forward) - NearPlane, db = Vec3.Dot(b - eye, forward) - NearPlane;
        if (da < 0 && db < 0)
        {
            return false;
        }

        if (da < 0)
        {
            a = Vec3.Lerp(a, b, da / (da - db));
        }
        else if (db < 0)
        {
            b = Vec3.Lerp(a, b, da / (da - db));
        }

        return true;
    }

    private readonly record struct Segment(Vec3 A, Vec3 B, Color Color, float Width, bool Dashed, bool Arrow);

    private readonly record struct Mark(Vec3 At, string? Text, Color Color, float Radius, bool Pinned);

    private readonly record struct Face(IReadOnlyList<Vec3> Points, Vec3 Normal, Color Color);
}

/// <summary>Point lists for arcs and cones.</summary>
public static class OverlayShapes
{
    /// <summary>Points of an arc round <paramref name="centre"/> in the plane of u and v, from angle a0 to a1 (radians).</summary>
    public static List<Vec3> Arc(Vec3 centre, Vec3 u, Vec3 v, double radius, double a0, double a1, int segments)
    {
        var points = new List<Vec3>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            double a = a0 + (a1 - a0) * i / segments;
            points.Add(centre + (u * Math.Cos(a) + v * Math.Sin(a)) * radius);
        }

        return points;
    }

    /// <summary>Two unit vectors at right angles to <paramref name="n"/> and each other.</summary>
    public static (Vec3 U, Vec3 V) Basis(Vec3 n)
    {
        Vec3 u = Vec3.Cross(n, Math.Abs(n.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX).Normalized();
        return (u, Vec3.Cross(n, u).Normalized());
    }
}

/// <summary>
/// The labels of one view, drawn after every shape: a label that would land on one already drawn is left out (a pile of
/// overlapping names reads as noise), and pinned labels go first, so a selected object's label wins.
/// </summary>
public sealed class LabelLayout
{
    /// <summary>Clear space kept round the view's chrome.</summary>
    private const float ChromeMargin = EditorTheme.SpaceXs;

    private readonly List<(Vector2 Baseline, string Text, Color Color, bool Pinned)> _queued = new();
    private readonly List<Rect2> _reserved = new();

    /// <summary>Areas no label may land on (the view's label, storey slider, tripod and scale tag): chrome always
    /// wins over the scene's labels.</summary>
    public void Reserve(IEnumerable<Rect2> rects) => _reserved.AddRange(rects.Select(r => r.Grow(ChromeMargin)));

    public void Add(Vector2 baseline, string text, Color color, bool pinned) => _queued.Add((baseline, text, color, pinned));

    public void Draw(Control canvas)
    {
        Font font = canvas.GetThemeDefaultFont();
        var placed = new List<Rect2>(_reserved);
        var screen = new Rect2(Vector2.Zero, canvas.Size);
        foreach ((Vector2 baseline, string text, Color color, _) in _queued.OrderByDescending(l => l.Pinned))
        {
            Rect2 box = ViewTags.LabelBox(font, baseline, text);
            if (!screen.Intersects(box))
            {
                continue;
            }

            // A label running off the edge of the view moves back inside it rather than being cut.
            Vector2 shift = ViewTags.Inside(canvas, box.Position, box.Size) - box.Position;
            box.Position += shift;
            if (!placed.Any(l => l.Intersects(box)))
            {
                placed.Add(box);
                ViewTags.Label(canvas, font, baseline + shift, text, color);
            }
        }

        _queued.Clear();
    }
}
