using Godot;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>A face the cursor ray hit: which one, where, and its world normal (outward from the brush).</summary>
public readonly record struct FaceHit(FaceRef Face, double Distance, Vec3 Point, Vec3 Normal);

/// <summary>
/// Picking brush faces and drawing them over a view. Perspective views pick the displayed CSG polygons, so cuts
/// remain open and doorway reveals retain their own editable source faces. Orthographic views pick original brush
/// faces along their depth axis, so the top view picks floors and the tops of blocks.
/// </summary>
public static class FacePicking
{
    private static BakedSurfacePick? s_outlined;
    private static List<(Vec3 A, Vec3 B)> s_outline = new();

    /// <summary>Picked faces: a blue fill and outline, so they never read as selected objects.</summary>
    public static Color FaceFill => new(SceneStyle.FaceSelection, SceneStyle.FaceFillAlpha);

    public static Color FaceLine => new(SceneStyle.FaceSelection, SceneStyle.FaceLineAlpha);

    /// <summary>The nearest face under the cursor, or null.</summary>
    public static FaceHit? Pick(ViewCamera cam, MapProject p, Vector2 mouse, Func<Brush, bool>? only = null)
    {
        (Vec3 o, Vec3 d) = cam.Ray(mouse);
        IReadOnlyList<PreviewPolygon>? surfaces = cam.IsOrtho ? null : SurfacePolygons(p);
        FaceHit? best = surfaces is null ? null : PickSurfaces(p, surfaces, o, d, only);
        foreach (Brush b in p.Brushes)
        {
            // In 3D the CSG result is the target: an uncut room face still spans a doorway's empty opening.
            // Kept baked brushes have no preview polygons; their original faces remain read-only pick targets.
            if (surfaces is not null && !BakedContent.SurfacesLocked(p, b) && b.Stair is null)
            {
                continue;
            }

            if (!ObjectFilter.IsPickable(p, b.Editor) || (only is not null && !only(b)))
            {
                continue;
            }

            WorldBrush world = BrushGeometry.Of(b);
            if (world.IsEmpty || !Picking.RayBox(o, d, world.Min - Vec3.One, world.Max + Vec3.One, out double entry)
                || (best is { } sofar && entry > sofar.Distance))
            {
                continue;
            }

            // Only the side a surface is seen from: an added brush from outside, a carved one from inside the room,
            // so the top view picks a room's floor through its ceiling and a view inside a room picks its walls.
            bool inward = b.Oper == CsgOper.Subtract;
            for (int i = 0; i < world.Faces.Count; i++)
            {
                WorldFace f = world.Faces[i];
                if ((Vec3.Dot(d, f.Normal) > 0) != inward)
                {
                    continue;
                }

                if (Picking.RayPolygon(o, d, f.Vertices, f.Normal, out double t) && (best is null || t < best.Value.Distance))
                {
                    best = new FaceHit(new FaceRef(b.Id, i), t, o + d * t, f.Normal);
                }
            }
        }

        return best;
    }

    /// <summary>The displayed CSG polygons, shared by picking, hover outlines and selected-face fills.</summary>
    public static IReadOnlyList<PreviewPolygon>? SurfacePolygons(MapProject p) =>
        ViewportLayout.Instance?.Scene.SurfacePolygons(p);

    /// <summary>Picks the visible cut surface, retaining its source face and original outward normal for edits.</summary>
    public static FaceHit? PickSurfaces(MapProject p, IReadOnlyList<PreviewPolygon> surfaces, Vec3 o, Vec3 d,
        Func<Brush, bool>? only = null)
    {
        Dictionary<string, Brush> brushes = FaceEdit.BrushIndex(p);
        FaceHit? best = null;
        foreach (PreviewPolygon poly in surfaces)
        {
            if (!brushes.TryGetValue(poly.BrushId, out Brush? b) || poly.FaceIndex < 0 || poly.FaceIndex >= b.Shape.Faces.Count
                || !ObjectFilter.IsPickable(p, b.Editor) || (only is not null && !only(b))
                || (poly.Flags & (PolyFlags.Invisible | PolyFlags.Portal)) != 0
                || ((poly.Flags & PolyFlags.TwoSided) == 0 && Vec3.Dot(d, poly.Normal) >= 0))
            {
                continue;
            }

            if (Picking.RayPolygon(o, d, poly.Vertices, poly.Normal, out double t) && (best is null || t < best.Value.Distance))
            {
                Vec3 outward = b.Oper == CsgOper.Subtract ? -poly.Normal : poly.Normal;
                best = new FaceHit(new FaceRef(b.Id, poly.FaceIndex), t, o + d * t, outward);
            }
        }

        return best;
    }

    /// <summary>The world polygon of a face, or null when it is gone.</summary>
    public static WorldFace? World(MapProject p, FaceRef f) =>
        p.FindBrush(f.Brush) is { } b && BrushGeometry.Of(b) is { } w && f.Face < w.Faces.Count ? w.Faces[f.Face] : null;

    /// <summary>Fills (unless <paramref name="fill"/> is null) and outlines a world polygon on a view. In the perspective
    /// view the part behind the camera is cut off first, so a floor the camera stands over still shows.</summary>
    public static void DrawPolygon(ViewCamera cam, Control canvas, IReadOnlyList<Vec3> poly, Color? fill, Color outline, float width = 2)
    {
        if (!cam.IsOrtho)
        {
            poly = ClipNear(poly, cam.Location, cam.LookDirection);
            if (poly.Count < 2)
            {
                return;
            }
        }

        var pts = new Vector2[poly.Count];
        for (int i = 0; i < poly.Count; i++)
        {
            if (cam.ToScreen(poly[i]) is not { } s)
            {
                return;
            }

            pts[i] = s;
        }

        if (fill is { } f && pts.Length >= 3 && !IsDegenerate(pts))
        {
            canvas.DrawColoredPolygon(pts, f);
        }

        var closed = new Vector2[pts.Length + 1];
        pts.CopyTo(closed, 0);
        closed[^1] = pts[0];
        canvas.DrawPolyline(closed, outline, width, antialiased: true);
    }

    /// <summary>Draws the picked faces of a project on a 2D view, and the hovered one as an outline only (in every view;
    /// the perspective view tints picked faces in the scene, <see cref="PickedFacesNode"/>).</summary>
    public static void DrawFaces(ViewCamera cam, Control canvas, MapProject p, IReadOnlyCollection<FaceRef> faces, FaceRef? hover)
    {
        if (hover is { } h && !cam.IsOrtho && SurfacePolygons(p) is { } surfaces
            && p.FindBrush(h.Brush) is { Stair: null } brush && !BakedContent.SurfacesLocked(p, brush))
        {
            foreach (PreviewPolygon poly in surfaces.Where(poly => poly.BrushId == h.Brush && poly.FaceIndex == h.Face))
            {
                DrawPolygon(cam, canvas, poly.Vertices, null, SceneStyle.Hover);
            }
        }
        else if (hover is { } raw && World(p, raw) is { } hw)
        {
            DrawPolygon(cam, canvas, hw.Vertices, null, SceneStyle.Hover);
        }

        if (faces.Count == 0 || !cam.IsOrtho)
        {
            return;
        }

        // One lookup table per draw: a whole map's faces can be picked.
        Dictionary<string, Brush> brushes = FaceEdit.BrushIndex(p);
        Vec3 forward = cam.LookDirection;
        Color orthoFill = new(SceneStyle.FaceSelection, SceneStyle.FaceFillAlpha2D);
        foreach (FaceRef f in faces)
        {
            if (!brushes.TryGetValue(f.Brush, out Brush? b) || BrushGeometry.Of(b) is not { } w || f.Face >= w.Faces.Count)
            {
                continue;
            }

            // A 2D view fills only the faces it looks onto (from the side the face is seen, as picking does), lighter:
            // a picked box's top and bottom, or a whole room, would otherwise stack into an opaque slab. The rest are
            // outlined.
            WorldFace face = w.Faces[f.Face];
            Color? fill = FaceFill;
            if (cam.IsOrtho)
            {
                double facing = Vec3.Dot(forward, face.Normal) * (b.Oper == CsgOper.Subtract ? 1 : -1);
                fill = facing > SceneStyle.FaceFacing2D ? orthoFill : null;
            }

            DrawPolygon(cam, canvas, face.Vertices, fill, FaceLine);
        }
    }

    /// <summary>The picked baked-world surface on a 2D view: its triangles filled where the view looks onto it, and its
    /// outline (the edges of its run of triangles no other triangle of the run shares), so a wall seen edge-on shows
    /// as a line.</summary>
    public static void DrawBaked(ViewCamera cam, Control canvas, BakedSurfacePick baked)
    {
        IReadOnlyList<Vec3> tris = baked.Triangles;
        if (Math.Abs(Vec3.Dot(cam.LookDirection, baked.Normal)) > SceneStyle.FaceFacing2D)
        {
            Color fill = new(SceneStyle.FaceSelection, SceneStyle.FaceFillAlpha2D);
            var pts = new Vector2[3];
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                if (cam.ToScreen(tris[i]) is { } a && cam.ToScreen(tris[i + 1]) is { } b && cam.ToScreen(tris[i + 2]) is { } c)
                {
                    pts[0] = a;
                    pts[1] = b;
                    pts[2] = c;
                    if (Math.Abs((b - a).Cross(c - a)) > 0.01f)
                    {
                        canvas.DrawColoredPolygon(pts, fill);
                    }
                }
            }
        }

        if (!ReferenceEquals(s_outlined, baked))
        {
            s_outlined = baked;
            s_outline = Outline(tris);
        }

        foreach ((Vec3 a, Vec3 b) in s_outline)
        {
            if (cam.ToScreen(a) is { } sa && cam.ToScreen(b) is { } sb)
            {
                canvas.DrawLine(sa, sb, FaceLine, 2, antialiased: true);
            }
        }
    }

    /// <summary>The edges of a run of triangles that only one of them has.</summary>
    private static List<(Vec3 A, Vec3 B)> Outline(IReadOnlyList<Vec3> tris)
    {
        var count = new Dictionary<((long, long, long), (long, long, long)), (Vec3 A, Vec3 B, int N)>();
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                Vec3 a = tris[i + e], b = tris[i + (e + 1) % 3];
                (long, long, long) ka = Key(a), kb = Key(b);
                var key = ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
                count[key] = count.TryGetValue(key, out var had) ? (had.A, had.B, had.N + 1) : (a, b, 1);
            }
        }

        return count.Values.Where(v => v.N == 1).Select(v => (v.A, v.B)).ToList();

        static (long, long, long) Key(Vec3 v) => ((long)Math.Round(v.X * 4), (long)Math.Round(v.Y * 4), (long)Math.Round(v.Z * 4));
    }

    /// <summary>A small square marker at a world point.</summary>
    public static void DrawMarker(ViewCamera cam, Control canvas, Vec3 at, Color c, float size = 4, bool filled = true)
    {
        if (cam.ToScreen(at) is { } s)
        {
            canvas.DrawRect(new Rect2(s - new Vector2(size, size), new Vector2(size * 2, size * 2)), c, filled, filled ? -1 : 1.5f);
        }
    }

    /// <summary>A view tag (<see cref="ViewTags"/>) whose text starts at <paramref name="at"/> (its baseline's left end),
    /// kept inside the view.</summary>
    public static void DrawLabel(Control canvas, Vector2 at, string text, Color color) =>
        DrawLabel(canvas, at, [new TagPart(text, color)]);

    /// <inheritdoc cref="DrawLabel(Control, Vector2, string, Color)"/>
    public static void DrawLabel(Control canvas, Vector2 at, IReadOnlyList<TagPart> parts)
    {
        Font font = canvas.GetThemeDefaultFont();
        StyleBox box = ViewTags.Box;
        Vector2 topLeft = at - new Vector2(box.ContentMarginLeft, box.ContentMarginTop + font.GetAscent(EditorTheme.FontSizeCaption));
        ViewTags.Draw(canvas, ViewTags.Inside(canvas, topLeft, ViewTags.Size(font, parts)), parts);
    }

    /// <summary>The part of a polygon in front of the camera's near plane (a little ahead of the eye).</summary>
    private static IReadOnlyList<Vec3> ClipNear(IReadOnlyList<Vec3> poly, Vec3 eye, Vec3 forward)
    {
        // Beyond the camera's own near plane (0.05 m, 5 units), or a clipped corner reads as behind it and the face is
        // not drawn at all.
        const double near = 8;
        bool allIn = true;
        foreach (Vec3 v in poly)
        {
            allIn &= Vec3.Dot(v - eye, forward) >= near;
        }

        if (allIn)
        {
            return poly;
        }

        var kept = new List<Vec3>(poly.Count + 2);
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3 a = poly[i], b = poly[(i + 1) % poly.Count];
            double da = Vec3.Dot(a - eye, forward) - near, db = Vec3.Dot(b - eye, forward) - near;
            if (da >= 0)
            {
                kept.Add(a);
            }

            if ((da >= 0) != (db >= 0))
            {
                kept.Add(Vec3.Lerp(a, b, da / (da - db)));
            }
        }

        return kept;
    }

    private static bool IsDegenerate(Vector2[] pts)
    {
        float area = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            area += pts[i].Cross(pts[(i + 1) % pts.Length]);
        }

        return Math.Abs(area) < 1;
    }
}
