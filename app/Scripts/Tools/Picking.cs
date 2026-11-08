using Godot;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Windows;
using OpenScctEditor.Core;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Render;
using WorldGeometry = OpenScct.Editor.Core.Scripting.WorldGeometry;

namespace OpenScctEditor.Tools;

/// <summary>A surface the cursor ray hit: where, its outward normal, what it belongs to (null for an imported map's
/// baked world), and whether it is drawn from both sides (a two-sided, non-solid or portal sheet).</summary>
public readonly record struct SurfaceHit(ObjectRef? Object, double Distance, Vec3 Point, Vec3 Normal, bool TwoSided = false);

/// <summary>
/// Click picking and box selection. Orthographic views pick like the stock editor's wireframe views: the nearest
/// brush edge, actor outline or GE line within a few pixels. The perspective view casts a ray against brush faces,
/// static meshes' triangles and other actors' drawn boxes (GE lines by screen distance, at their depth where they pass
/// the cursor), and the icons it draws for actors without a mesh (<see cref="ActorIconBatch"/>): an icon is hit
/// anywhere on its square, and wins over whatever it is drawn over (lines, boxes, the surface it sits on or is buried
/// just behind); an actor drawn as an icon has no box to hit until it is selected. Volumes are see-through there: they
/// are hit by their faces from outside only, and actors and GEs seen through them win. An actor box is not hit from
/// inside either. Hidden and locked objects, and kinds View > Show hides, are never picked.
/// </summary>
public static class Picking
{
    /// <summary>Pick tolerance in pixels.</summary>
    public const float Tolerance = 6;

    public static ObjectRef? Pick(ViewCamera cam, MapProject p, Vector2 mouse) =>
        cam.IsOrtho ? PickOrtho(cam, p, mouse) : PickPerspective(cam, p, mouse);

    /// <summary>The nearest surface under the cursor (for placing things on surfaces), or null: brush faces and, unless
    /// <paramref name="pickableOnly"/>, an imported map's baked collision (a locked layer).
    /// Selection uses <paramref name="displayedBrushes"/> to respect openings in the displayed CSG.</summary>
    public static SurfaceHit? Surface(ViewCamera cam, MapProject p, Vector2 mouse, bool pickableOnly = false, bool displayedBrushes = false)
    {
        (Vec3 o, Vec3 d) = cam.Ray(mouse);
        SurfaceHit? best = null;
        if (displayedBrushes && FacePicking.Pick(cam, p, mouse, _ => IsShown(ObjectKind.Brush)) is { } brushHit
            && p.FindBrush(brushHit.Face.Brush) is { } source)
        {
            best = new SurfaceHit(ObjectRef.ForBrush(source), brushHit.Distance, brushHit.Point,
                source.Oper == CsgOper.Subtract ? -brushHit.Normal : brushHit.Normal);
        }
        foreach (Brush b in displayedBrushes ? Enumerable.Empty<Brush>() : p.Brushes)
        {
            if (!ObjectFilter.IsVisible(p, b.Editor) || (pickableOnly && !ObjectFilter.IsPickable(p, b.Editor)))
            {
                continue;
            }

            WorldBrush world = BrushGeometry.Of(b);
            if (world.IsEmpty || !RayBox(o, d, world.Min - Vec3.One, world.Max + Vec3.One, out double entry)
                || (best is { } sofar && entry > sofar.Distance))
            {
                continue;
            }

            bool inward = b.Oper == CsgOper.Subtract;
            bool sheet = b.IsNotSolid || b.IsPortal;
            foreach (WorldFace f in world.Faces)
            {
                if (RayPolygon(o, d, f.Vertices, f.Normal, out double t) && (best is null || t < best.Value.Distance))
                {
                    // A subtractive brush's faces bound empty space: the surface you stand on faces into the brush.
                    best = new SurfaceHit(ObjectRef.ForBrush(b), t, o + d * t, inward ? -f.Normal : f.Normal,
                        sheet || (f.Flags & PolyFlags.TwoSided) != 0);
                }
            }
        }

        foreach (Ge ge in p.Ges)
        {
            if (!IsShown(ObjectKind.Ge) || !ObjectFilter.IsVisible(p, ge.Editor)
                || (pickableOnly && !ObjectFilter.IsPickable(p, ge.Editor)))
            {
                continue;
            }

            foreach (WorldBrush part in GeLookGeometry.Of(ge))
            {
                if (part.IsEmpty || !RayBox(o, d, part.Min - Vec3.One, part.Max + Vec3.One, out double entry)
                    || (best is { } sofar && entry > sofar.Distance))
                {
                    continue;
                }

                foreach (WorldFace face in part.Faces.Where(f => (f.Flags & PolyFlags.Invisible) == 0))
                {
                    if (RayPolygon(o, d, face.Vertices, face.Normal, out double t) && (best is null || t < best.Value.Distance))
                    {
                        best = new SurfaceHit(ObjectRef.Ge(ge.Id), t, o + d * t, face.Normal,
                            (face.Flags & PolyFlags.TwoSided) != 0);
                    }
                }
            }
        }

        // A hit right at the camera means it sits in the baked world's solid outside the rooms: no surface there.
        if (!pickableOnly && p.Baked is { KeepCollision: true }
            && new WorldGeometry(p, EditorContext.Instance.Session.Folder).Raycast(o, d, best?.Distance ?? 65536) is { BrushId: null, Distance: >= 1 } baked)
        {
            best = new SurfaceHit(null, baked.Distance, baked.Point, baked.Normal);
        }

        return best;
    }

    /// <summary>Objects inside a screen rectangle: brushes and GEs whose bounds are fully inside, actors whose
    /// location is inside (the stock editor's box select).</summary>
    public static List<ObjectRef> InRect(ViewCamera cam, MapProject p, Rect2 rect)
    {
        var result = new List<ObjectRef>();
        bool Inside(Vec3 v) => cam.ToScreen(v) is { } s && rect.HasPoint(s);
        bool BoxInside((Vec3 Min, Vec3 Max) b) => ObjectGeometry.Corners(b.Min, b.Max).All(Inside);
        foreach (Actor a in p.Actors)
        {
            if (IsShown(ObjectKind.Actor) && ObjectFilter.IsPickable(p, a.Editor) && Inside(a.Location))
            {
                result.Add(ObjectRef.Actor(a.Id));
            }
        }

        foreach (Ge g in p.Ges)
        {
            if (IsShown(ObjectKind.Ge) && ObjectFilter.IsPickable(p, g.Editor) && g.Points.Count > 0 && BoxInside(ObjectGeometry.GeBounds(g)))
            {
                result.Add(ObjectRef.Ge(g.Id));
            }
        }

        foreach (Brush b in p.Brushes)
        {
            if (IsShown(ObjectKind.Brush) && ObjectFilter.IsPickable(p, b.Editor) && b.Shape.Faces.Count > 0 && BoxInside(ObjectGeometry.BrushBounds(b)))
            {
                result.Add(ObjectRef.ForBrush(b));
            }
        }

        return result;
    }

    private static ObjectRef? PickOrtho(ViewCamera cam, MapProject p, Vector2 mouse)
    {
        ObjectRef? best = null;
        double bestDistance = Tolerance;
        (Vec3 o, Vec3 dir) = cam.Ray(mouse);

        void Consider(ObjectRef r, double distance)
        {
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = r;
            }
        }

        // Actors first and slightly favoured: they are small and usually what a click near them means.
        foreach (Actor a in p.Actors)
        {
            if (!IsShown(ObjectKind.Actor) || !ObjectFilter.IsPickable(p, a.Editor))
            {
                continue;
            }

            if (a.Shape is { Faces.Count: > 0 } shape)
            {
                // Volumes can be huge: pick them by their edges, like brushes.
                foreach (BrushFace f in shape.Faces)
                {
                    Consider(ObjectRef.Actor(a.Id), PolylineDistance(cam, f.Vertices.Select(a.LocalToWorld).ToList(), mouse, closed: true));
                }

                continue;
            }

            (Vec3 min, Vec3 max) = ObjectGeometry.ActorBounds(a);
            if (ScreenRect(cam, min, max) is { } r)
            {
                double d = r.HasPoint(mouse) ? 0 : DistanceToRect(r, mouse);
                if (d == 0 && (a.Door is not null ? !RayDoor(o, dir, a, double.NegativeInfinity, out _) : a.Window is not null
                    ? !RayWindow(o, dir, a, double.NegativeInfinity, out _)
                    : StaticMeshDrawing.Loaded(a) is { } mesh && !RayMesh(o, dir, a, mesh, double.NegativeInfinity, out _)))
                {
                    // Inside a static mesh's box but off the mesh: only the box outline is near.
                    d = DistanceToEdge(r, mouse);
                }

                Consider(ObjectRef.Actor(a.Id), d - 2);
            }
        }

        foreach (Ge g in p.Ges)
        {
            if (IsShown(ObjectKind.Ge) && ObjectFilter.IsPickable(p, g.Editor))
            {
                Consider(ObjectRef.Ge(g.Id), PolylineDistance(cam, g.Points, mouse, closed: false) - 1);
                foreach (WorldFace face in GeLookGeometry.Of(g).SelectMany(b => b.Faces).Where(f => (f.Flags & PolyFlags.Invisible) == 0))
                {
                    Consider(ObjectRef.Ge(g.Id), PolylineDistance(cam, face.Vertices, mouse, closed: true) - 1);
                }
            }
        }

        foreach (Brush b in p.Brushes)
        {
            if (!IsShown(ObjectKind.Brush) || !ObjectFilter.IsPickable(p, b.Editor))
            {
                continue;
            }

            WorldBrush world = BrushGeometry.Of(b);
            if (world.IsEmpty || ScreenRect(cam, world.Min, world.Max) is not { } r || DistanceToRect(r, mouse) > bestDistance)
            {
                continue;
            }

            foreach (WorldFace f in world.Faces)
            {
                Consider(ObjectRef.ForBrush(b), PolylineDistance(cam, f.Vertices, mouse, closed: true));
            }
        }

        return best;
    }

    private static ObjectRef? PickPerspective(ViewCamera cam, MapProject p, Vector2 mouse)
    {
        (Vec3 o, Vec3 d) = cam.Ray(mouse);
        ObjectRef? best = null;
        double bestT = double.MaxValue;
        ObjectRef? volume = null;
        double volumeT = double.MaxValue;
        bool icons = ActorIconBatch.Shown;
        Selection selection = EditorContext.Instance.Selection;
        // Icons under the cursor, and the nearest surface or mesh that could hide one (lines and boxes do not).
        List<IconCandidate>? iconHits = null;
        double solidT = double.MaxValue;
        if (IsShown(ObjectKind.Actor))
        {
            foreach (Actor a in p.Actors)
            {
                if (!ObjectFilter.IsPickable(p, a.Editor))
                {
                    continue;
                }

                if (a.Shape is { Faces.Count: > 0 })
                {
                    if (RayVolume(o, d, a, out double tv) && tv < volumeT)
                    {
                        volumeT = tv;
                        volume = ObjectRef.Actor(a.Id);
                    }

                    continue;
                }

                var r = ObjectRef.Actor(a.Id);
                // Use the same shaped pane and frame as the renderer, even before its mesh has loaded. The
                // assembly's subtractive opening is empty space and must never become a selection target.
                if (a.Door is not null)
                {
                    if (RayDoor(o, d, a, 0, out double td))
                    { solidT = Math.Min(solidT, td); if (td < bestT) { bestT = td - 1; best = r; } }
                    continue;
                }
                if (a.Window is not null)
                {
                    if (RayWindow(o, d, a, 0, out double tw))
                    {
                        solidT = Math.Min(solidT, tw);
                        if (tw < bestT) { bestT = tw - 1; best = r; }
                    }
                    continue;
                }
                IconUse use = icons ? ActorIconBatch.Use(a) : IconUse.None;
                bool selected = use != IconUse.None && selection.Contains(r);
                if ((use == IconUse.Always || (use == IconUse.WhenSelected && selected)) &&
                    IconHit(cam, a, mouse, o, d, use, selected) is { } ih)
                {
                    (iconHits ??= []).Add(new IconCandidate(r, ih.Front, ih.Hidden, selected));
                }

                // An actor drawn as an icon has no box on show until it is selected: until then only its icon is hit.
                if (use == IconUse.Always && !selected)
                {
                    continue;
                }

                (Vec3 min, Vec3 max) = ObjectGeometry.ActorBounds(a);
                double pixel = cam.UnitsPerPixelAt(a.Location);
                if (StaticMeshDrawing.Loaded(a) is { } mesh && (max - min).Length > pixel * 12)
                {
                    // A static mesh by its triangles: a big one (a help path, a floor) takes only the clicks on itself.
                    if (RayBox(o, d, min, max, out _) && RayMesh(o, d, a, mesh, 0, out double tm))
                    {
                        solidT = Math.Min(solidT, tm);
                        if (tm < bestT)
                        {
                            bestT = tm - 1;
                            best = r;
                        }
                    }

                    continue;
                }

                // Others by the box the view draws, grown a little so small ones are easy to hit from afar.
                Vec3 grow = Vec3.One * (pixel * 3);
                if (RayBox(o, d, min - grow, max + grow, out double t) && t > 0 && t < bestT)
                {
                    bestT = t - 1;
                    best = r;
                }
            }
        }

        if (IsShown(ObjectKind.Ge))
        {
            foreach (Ge g in p.Ges)
            {
                if (ObjectFilter.IsPickable(p, g.Editor) && g.Points.Count > 0 &&
                    PolylineDistance(cam, g.Points, mouse, closed: false) < Tolerance)
                {
                    // Its depth where it passes the cursor, not at its first point: a long line reaches far either way.
                    double t = RayPolylineDepth(o, d, g.Points);
                    if (t < bestT)
                    {
                        bestT = t - 1;
                        best = ObjectRef.Ge(g.Id);
                    }
                }
            }
        }

        bool seeThrough = best is not null;
        if (Surface(cam, p, mouse, pickableOnly: true, displayedBrushes: true) is { } hit && (hit.Object is not { } owner || IsShown(owner.Kind)))
        {
            // A one-sided surface seen from behind (a room's ceiling from above) is not drawn and hides no icon.
            if (hit.TwoSided || Vec3.Dot(hit.Normal, d) < 0)
            {
                solidT = Math.Min(solidT, hit.Distance);
            }

            if (hit.Distance < bestT)
            {
                best = hit.Object;
                bestT = hit.Distance;
                seeThrough = false;
            }
        }

        // Icons are drawn over lines, boxes and whatever in front of them does not hide them (the surface they sit on,
        // or one they are buried just behind, where they show faint), so an icon on show wins: a selected one (drawn
        // over everything) first, else the nearest.
        if (iconHits is not null)
        {
            IconCandidate? icon = null;
            foreach (IconCandidate c in iconHits)
            {
                if ((c.Selected || solidT >= c.Hidden) && (icon is not { } sofar || (c.Selected && !sofar.Selected) ||
                    (c.Selected == sofar.Selected && c.Front < sofar.Front)))
                {
                    icon = c;
                }
            }

            if (icon is { } shown)
            {
                return shown.Ref;
            }
        }

        // A volume wins only over the brush surface behind it.
        if (volume is not null && !seeThrough && volumeT < bestT)
        {
            best = volume;
        }

        return best;
    }

    /// <summary>Whether the views show a kind of object (View > Show > Brushes, Actors, Geometric Events); a kind not
    /// on show is not picked. The viewports set it.</summary>
    public static Func<ObjectKind, bool> IsShown { get; set; } = _ => true;

    /// <summary>An icon under the cursor: its actor, the distance along the ray to the icon as drawn, the distance a
    /// surface or mesh must be nearer than to hide it, and whether its actor is selected (drawn over everything).</summary>
    private readonly record struct IconCandidate(ObjectRef Ref, double Front, double Hidden, bool Selected);

    /// <summary>When the cursor is on an actor's icon (the square the perspective view draws, a pick tolerance around
    /// it): the distance along the ray to the icon as drawn (pulled towards the camera) and the distance a surface must
    /// be nearer than to hide it (<see cref="ActorIconBatch.HiddenBy"/>); else null.</summary>
    private static (double Front, double Hidden)? IconHit(ViewCamera cam, Actor a, Vector2 mouse, Vec3 o, Vec3 d, IconUse use, bool selected)
    {
        if (cam.ToScreen(a.Location) is not { } centre)
        {
            return null;
        }

        double pixels = ActorIconBatch.ScreenPixels(cam, a.Location, use, selected);
        double reach = (pixels / 2) + (Tolerance / 2);
        if (Math.Abs(mouse.X - centre.X) > reach || Math.Abs(mouse.Y - centre.Y) > reach)
        {
            return null;
        }

        double along = Vec3.Dot(a.Location - o, d);
        double pull = ActorIconBatch.Pull(cam, a.Location, pixels);
        return along - pull > 0 ? (along - pull, along - ActorIconBatch.HiddenBy(pull)) : null;
    }

    /// <summary>The distance along a ray to where a polyline passes nearest to it (by angle, as the cursor sees it).</summary>
    private static double RayPolylineDepth(Vec3 o, Vec3 d, IReadOnlyList<Vec3> pts)
    {
        if (pts.Count == 1)
        {
            return Math.Max(Vec3.Dot(pts[0] - o, d), 0);
        }

        double bestGap = double.MaxValue, bestT = double.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            // Closest points of the ray (o + d*t, d of unit length) and the segment (a + e*s, s in 0..1).
            Vec3 a = pts[i], e = pts[i + 1] - a, w = o - a;
            double ee = Vec3.Dot(e, e), de = Vec3.Dot(d, e), dw = Vec3.Dot(d, w), ew = Vec3.Dot(e, w);
            double denom = ee - (de * de);
            double s = ee < 1e-9 ? 0 : denom < 1e-9 ? Math.Clamp(ew / ee, 0, 1) : Math.Clamp((ew - (de * dw)) / denom, 0, 1);
            Vec3 q = a + (e * s);
            double t = Math.Max(Vec3.Dot(q - o, d), 0);
            double gap = (o + (d * t) - q).Length / Math.Max(t, 1);
            if (gap < bestGap)
            {
                bestGap = gap;
                bestT = t;
            }
        }

        return bestT;
    }

    /// <summary>The nearest face of a shaped actor (volume) the ray hits. From inside its box nothing is hit: a
    /// room-sized volume would otherwise take every click made in the room.</summary>
    private static bool RayVolume(Vec3 o, Vec3 d, Actor a, out double t)
    {
        t = double.MaxValue;
        (Vec3 min, Vec3 max) = ObjectGeometry.ActorBounds(a);
        if (!RayBox(o, d, min, max, out double entry) || entry <= 0)
        {
            return false;
        }

        foreach (BrushFace f in a.Shape!.Faces)
        {
            Vec3[] v = f.Vertices.Select(a.LocalToWorld).ToArray();
            if (RayPolygon(o, d, v, Poly.Normal(v), out double ft))
            {
                t = Math.Min(t, ft);
            }
        }

        return t < double.MaxValue;
    }

    /// <summary>The actual pane and solid frame, excluding the assembly's wall cut and empty outline corners.</summary>
    public static bool RayWindow(Vec3 o, Vec3 d, Actor a, double minT, out double t)
    {
        t = double.MaxValue;
        if (a.Window is not { } design) return false;
        try
        {
            var pane = new Brush(a.Id) { Shape = WindowGeometry.Pane(design), Location = a.Location,
                Rotation = a.Rotation, MainScale = a.DrawScale3D * a.DrawScale, PrePivot = a.PrePivot };
            foreach (Brush part in WindowGeometry.Parts(a).Where(b => b.Oper == CsgOper.Add).Prepend(pane))
            foreach (WorldFace face in BrushGeometry.Of(part).Faces)
            {
                if (RayPolygon(o, d, face.Vertices, face.Normal, out double hit) && hit > minT && hit < t) t = hit;
            }
        }
        catch (ArgumentException) { } // Invalid designs keep their last valid drawing until the user fixes them.
        return t < double.MaxValue;
    }

    public static bool RayDoor(Vec3 o, Vec3 d, Actor a, double minT, out double t)
    {
        t = double.MaxValue;
        if (a.Door is not { } design) return false;
        foreach (var part in OpenScct.Editor.Core.Doors.DoorGeometry.Leaf(design)) foreach (var face in part.Shape.Faces)
        {
            var vertices = face.Vertices.Select(a.LocalToWorld).ToArray();
            if (RayPolygon(o, d, vertices, Poly.Normal(vertices), out double hit) && hit > minT && hit < t) t = hit;
        }
        foreach (var part in OpenScct.Editor.Core.Doors.DoorGeometry.Parts(a).Where(b => b.Oper == CsgOper.Add)) foreach (var face in part.WorldFaces())
            if (RayPolygon(o, d, face.Vertices, face.Normal(), out double hit) && hit > minT && hit < t) t = hit;
        return t < double.MaxValue;
    }

    /// <summary>The nearest triangle of a static mesh the ray hits (either side) beyond <paramref name="minT"/>. The ray
    /// goes into the mesh's own space; the actor transform is affine, so the distance along it carries over.</summary>
    private static bool RayMesh(Vec3 o, Vec3 d, Actor a, LoadedMesh mesh, double minT, out double t)
    {
        Vec3 lo = StaticMeshDrawing.WorldToMesh(a, o);
        Vec3 ld = StaticMeshDrawing.WorldToMesh(a, o + d) - lo;
        t = double.MaxValue;
        foreach ((float[] pos, int[] idx) in mesh.Triangles)
        {
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                if (RayTriangle(lo, ld, Corner(pos, idx[i]), Corner(pos, idx[i + 1]), Corner(pos, idx[i + 2]), out double ti)
                    && ti > minT && ti < t)
                {
                    t = ti;
                }
            }
        }

        return t < double.MaxValue;

        // Mesh positions are glTF (Godot) space in metres.
        static Vec3 Corner(float[] pos, int i) => Space.ToUe(new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]));
    }

    /// <summary>Ray (as a line) against a triangle, either side (Moller-Trumbore); outputs the distance along the ray.</summary>
    private static bool RayTriangle(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c, out double t)
    {
        t = 0;
        Vec3 e1 = b - a, e2 = c - a;
        Vec3 p = Vec3.Cross(d, e2);
        double det = Vec3.Dot(e1, p);
        if (Math.Abs(det) < 1e-12)
        {
            return false;
        }

        Vec3 s = o - a;
        double u = Vec3.Dot(s, p) / det;
        if (u < 0 || u > 1)
        {
            return false;
        }

        Vec3 q = Vec3.Cross(s, e1);
        double v = Vec3.Dot(d, q) / det;
        if (v < 0 || u + v > 1)
        {
            return false;
        }

        t = Vec3.Dot(e2, q) / det;
        return true;
    }

    /// <summary>Ray against a convex polygon with its (Newell) normal; outputs the distance along the ray.</summary>
    public static bool RayPolygon(Vec3 o, Vec3 d, IReadOnlyList<Vec3> poly, Vec3 normal, out double t)
    {
        t = 0;
        double denom = Vec3.Dot(d, normal);
        if (poly.Count < 3 || Math.Abs(denom) < 1e-9)
        {
            return false;
        }

        t = Vec3.Dot(poly[0] - o, normal) / denom;
        if (t <= 0)
        {
            return false;
        }

        Vec3 hit = o + d * t;
        int sign = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3 a = poly[i], b = poly[(i + 1) % poly.Count];
            double side = Vec3.Dot(Vec3.Cross(b - a, hit - a), normal);
            if (Math.Abs(side) < 1e-6)
            {
                continue;
            }

            int s = side > 0 ? 1 : -1;
            if (sign != 0 && s != sign)
            {
                return false;
            }

            sign = s;
        }

        return true;
    }

    /// <summary>Ray against an axis-aligned box (slab test); outputs the entry distance (0 when inside).</summary>
    public static bool RayBox(Vec3 o, Vec3 d, Vec3 min, Vec3 max, out double t)
    {
        double tMin = 0, tMax = double.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(d[axis]) < 1e-12)
            {
                if (o[axis] < min[axis] || o[axis] > max[axis])
                {
                    t = 0;
                    return false;
                }

                continue;
            }

            double t1 = (min[axis] - o[axis]) / d[axis], t2 = (max[axis] - o[axis]) / d[axis];
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tMin = Math.Max(tMin, t1);
            tMax = Math.Min(tMax, t2);
            if (tMin > tMax)
            {
                t = 0;
                return false;
            }
        }

        t = tMin;
        return true;
    }

    /// <summary>Screen distance from the cursor to a polyline (infinite when off screen).</summary>
    public static double PolylineDistance(ViewCamera cam, IReadOnlyList<Vec3> pts, Vector2 mouse, bool closed)
    {
        double best = double.MaxValue;
        int n = pts.Count;
        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            if (cam.ToScreen(pts[i]) is { } a && cam.ToScreen(pts[(i + 1) % n]) is { } b)
            {
                best = Math.Min(best, SegmentDistance(a, b, mouse));
            }
        }

        if (n == 1 && cam.ToScreen(pts[0]) is { } only)
        {
            best = only.DistanceTo(mouse);
        }

        return best;
    }

    public static double SegmentDistance(Vector2 a, Vector2 b, Vector2 p)
    {
        Vector2 ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-6f ? 0 : Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return (a + ab * t).DistanceTo(p);
    }

    /// <summary>The screen rectangle of a box's corners, or null when part of it is behind the camera.</summary>
    public static Rect2? ScreenRect(ViewCamera cam, Vec3 min, Vec3 max)
    {
        Rect2? r = null;
        foreach (Vec3 c in ObjectGeometry.Corners(min, max))
        {
            if (cam.ToScreen(c) is not { } s)
            {
                return null;
            }

            r = r is { } rr ? rr.Expand(s) : new Rect2(s, Vector2.Zero);
        }

        return r;
    }

    /// <summary>Distance from a point inside a rectangle to its nearest side.</summary>
    private static double DistanceToEdge(Rect2 r, Vector2 p) =>
        Math.Min(Math.Min(p.X - r.Position.X, r.End.X - p.X), Math.Min(p.Y - r.Position.Y, r.End.Y - p.Y));

    private static double DistanceToRect(Rect2 r, Vector2 p)
    {
        float dx = Math.Max(Math.Max(r.Position.X - p.X, 0), p.X - r.End.X);
        float dy = Math.Max(Math.Max(r.Position.Y - p.Y, 0), p.Y - r.End.Y);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
