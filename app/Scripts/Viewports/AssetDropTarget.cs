using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports.Render;
using WorldGeometry = OpenScct.Editor.Core.Scripting.WorldGeometry;

namespace OpenScctEditor.Viewports;

/// <summary>
/// Assets dragged into the views (<see cref="AssetDragData"/>). A mesh shows as a ghost where it would land while the
/// pointer moves over a view, and dropping it adds a StaticMeshActor there (one undo step, selected, logged). In the
/// perspective view it lands on the surface under the pointer (brushes, an imported map's collision, static meshes):
/// standing on floors and tilted with slopes, upright against walls facing out, hanging under ceilings, its footprint
/// centred on the pointer and its pivot snapped to the grid along the surface. The top view drops it on the first
/// floor below the pointer (on the storey shown), the front and side views on the view plane at the depth of the
/// selection (or of what the view is centred on). A material or texture (a texture as the material that shows it)
/// goes on the brush face it is dropped on in the perspective view, whatever tool is active: in the surface tool on
/// every picked face when the face is one of them, else on that face only, one undo step. While it is over a face the
/// face (and the picked faces it would also go on) is highlighted and the status bar names it; over empty space, an
/// actor, the imported baked world, a read-only face or the view's own controls the view refuses the drop and the
/// status bar says why, as it does for a texture no material shows. The 2D views refuse surfaces: they cannot show
/// which face of a brush (floor, wall or ceiling) a drop would go on. A tile of the placeables palette places its
/// gameplay class by that class's rule (<see cref="PlaceableLanding"/>: on the wall, ceiling or floor under the pointer
/// facing out, or standing on the floor), with a faint copy of its mesh and its cone, beam or facing drawn while it is
/// over a view, refused with the reason where it cannot go; the drop is one undo step that also makes the links the
/// selection asks for, selects the new actor and says what still needs wiring. Escape, or leaving the view, cancels.
/// </summary>
[ShellPart(Order = -18)]
public sealed partial class AssetDropTarget : Node, IShellPart
{
    /// <summary>How far in front of the perspective camera a mesh goes when the pointer is over nothing.</summary>
    private const double FallbackDistance = 512;

    /// <summary>The box a mesh is shown and placed with before anything says how big it is.</summary>
    private static readonly (Vec3 Min, Vec3 Max) s_unknownBox = (new Vec3(-32, -32, 0), new Vec3(32, 32, 64));

    private EditorContext _ctx = null!;
    private MeshDropGhost? _ghost;
    private EditorViewport? _over;
    private Vector2 _pointer;
    private ulong _frame;
    private string? _shown;

    /// <summary>The face a material or texture would go on (and the view it is over), for the overlay; null when the
    /// pointer is not over one that takes it.</summary>
    private SurfaceTarget? _face;

    private EditorViewport? _faceView;

    /// <summary>Why the view refused the surface last hovered, said again when the drag ends there.</summary>
    private string? _refused;

    /// <summary>The last surface answer and where it was asked (the views ask on every move).</summary>
    private (EditorViewport View, Vector2 At, AssetDrag Drag, SurfaceTarget? Target, string Status)? _lastSurface;

    /// <summary>The palette placeable shown where it would land (and the view it is over), for the overlay.</summary>
    private (EditorViewport View, Placeable Entry, PlaceableSpot Spot)? _placeable;

    /// <summary>The map's level geometry for the top view's downward rays, made once per drag.</summary>
    private WorldGeometry? _world;

    /// <summary>The lowest and highest the map's objects reach (a little beyond), made once per drag.</summary>
    private (double Bottom, double Top)? _heights;

    public static AssetDropTarget? Instance { get; private set; }

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        Name = "AssetDropTarget";
        window.AddChild(this);
        context.SessionReplaced += End;
    }

    /// <summary>A view's <c>_CanDropData</c>: true for a mesh (and its ghost follows the pointer), or for a material
    /// or texture over a brush face that takes it (the face is outlined).</summary>
    public bool CanDrop(EditorViewport view, Vector2 position, Variant data)
    {
        if (AssetDragData.ReadPlaceable(data) is { } placeable)
        {
            return ShowPlaceable(view, placeable, PlaceableLanding.Find(view, Pointer(view, position), placeable, wait: false));
        }

        switch (AssetDragData.Read(data))
        {
            case { IsMesh: true } mesh:
                ShowGhost(view, mesh, Landing(view, Pointer(view, position), mesh.Path));
                return true;
            case { IsSurface: true } surface:
                return ShowSurface(view, Surface(view, Pointer(view, position), surface));
            default:
                // Other drags are not assets.
                HideGhost();
                return false;
        }
    }

    /// <summary>A view's <c>_DropData</c>: places the mesh or applies the material.</summary>
    public void Drop(EditorViewport view, Vector2 position, Variant data)
    {
        AssetDrag? drag = AssetDragData.Read(data);
        // The drop says what it did in the status bar.
        _shown = null;
        try
        {
            if (AssetDragData.ReadPlaceable(data) is { } placeable)
            {
                PlacePlaceable(view, Pointer(view, position), placeable);
            }
            else if (drag is { IsMesh: true })
            {
                PlaceMesh(view, Pointer(view, position), drag);
            }
            else if (drag is { IsSurface: true })
            {
                ApplySurface(view, Pointer(view, position), drag);
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>The pointer in the view. Godot hands drop targets a position read from the real cursor; in background
    /// mode the pointer is the synthetic one (<see cref="EditorPointer"/>).</summary>
    private static Vector2 Pointer(EditorViewport view, Vector2 position) => EditorPointer.Detached ? EditorPointer.Local(view) : position;

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } && GetViewport().GuiIsDragging()
            && DragName(GetViewport().GuiGetDragData()) is { } name)
        {
            GetViewport().GuiCancelDrag();
            End();
            _ctx.SetStatus($"Cancelled dragging {name}.");
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_over is null)
        {
            return;
        }

        // The views ask again on every move over them: when the pointer moved and no view asked, it left, or is over
        // something on top of the view (its menu, the storey slider), which takes no drop.
        Vector2 pointer = EditorPointer.Global(_over);
        if (!GetViewport().GuiIsDragging() || !_over.GetGlobalRect().HasPoint(pointer))
        {
            HideGhost();
        }
        else if (pointer != _pointer && Engine.GetProcessFrames() > _frame + 1)
        {
            OverControls(pointer);
        }
    }

    /// <summary>The pointer is over a control on top of the view: nothing would land, and the status bar says so (also
    /// when the drag ends there) instead of going back to Ready.</summary>
    private void OverControls(Vector2 pointer)
    {
        _pointer = pointer;
        if (DragName(GetViewport().GuiGetDragData()) is not { } name)
        {
            HideGhost();
            return;
        }

        string why = $"over the view's controls; drop {name} in the view itself";
        if (_shown == NotHere + why)
        {
            return;
        }

        EditorViewport? view = _over;
        HideGhost();
        _over = view;
        _refused = "Not applied: " + why;
        _shown = NotHere + why;
        _ctx.SetStatus(_shown);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd)
        {
            // Let go where the view refused a material: say why nothing happened (the hover said it, End clears it).
            string? refused = GetViewport().GuiIsDragSuccessful() ? null : _refused;
            End();
            if (refused is not null)
            {
                _ctx.SetStatus(refused);
            }
        }
    }

    /// <summary>The name of what an asset or palette drag carries, or null for any other drag.</summary>
    private static string? DragName(Variant data) => AssetDragData.Read(data)?.Name ?? AssetDragData.ReadPlaceable(data)?.Title;

    // ---- a palette placeable ----------------------------------------------------------------------------------------

    /// <summary>Shows where a palette placeable would land (its ghost and its cone, beam or facing), or why it cannot;
    /// true when the view takes it there.</summary>
    private bool ShowPlaceable(EditorViewport view, Placeable entry, (PlaceableSpot? Spot, string Why) at)
    {
        if (_ghost is not null && IsInstanceValid(_ghost))
        {
            _ghost.Visible = false;
        }

        EditorViewport? old = _placeable?.View;
        if (at.Spot is { } spot)
        {
            if (_placeable is not { } shown || shown.Spot != spot || shown.Entry != entry)
            {
                PlaceableLanding.ShowGhost(entry, spot);
            }

            _placeable = (view, entry, spot);
        }
        else if (_placeable is not null)
        {
            _placeable = null;
            PlaceableLanding.ShowGhost(null, null);
        }

        if (old is not null && old != view && IsInstanceValid(old))
        {
            old.Redraw();
        }

        view.Redraw();
        _over = view;
        _pointer = EditorPointer.Global(view);
        _frame = Engine.GetProcessFrames();
        // While the collision world is still being built the drop is taken (it waits for it); only the preview waits.
        bool pending = at.Spot is null && at.Why == PlaceableLanding.Pending;
        string status = at.Spot is { } s ? $"Drop to place {entry.Title} on {s.On} at {s.Location}"
            : pending ? $"Drop to place {entry.Title}: {at.Why}\u2026" : NotHere + at.Why;
        _refused = at.Spot is null && !pending ? "Not placed: " + at.Why : null;
        if (status != _shown)
        {
            _shown = status;
            _ctx.SetStatus(status);
        }

        return at.Spot is not null || pending;
    }

    /// <summary>Places a palette placeable where it was dropped (waiting for the collision world if it is still being
    /// built), as one undo step with its wiring; where it cannot go, the status bar says why.</summary>
    private void PlacePlaceable(EditorViewport view, Vector2 at, Placeable entry)
    {
        (PlaceableSpot? spot, string why) = PlaceableLanding.Find(view, at, entry, wait: true);
        if (spot is not { } s)
        {
            _ctx.SetStatus("Not placed: " + why);
            return;
        }

        _refused = null;
        PlaceableLanding.Place(_ctx, entry, s, view.Kind);
    }

    // ---- where a mesh lands -----------------------------------------------------------------------------------------

    /// <summary>The location and rotation a mesh dropped at <paramref name="at"/> in <paramref name="view"/> gets, and
    /// the local box it is placed by.</summary>
    private Land Landing(EditorViewport view, Vector2 at, string path)
    {
        (Vec3 min, Vec3 max) = Box(path);
        MapProject p = _ctx.Project;
        ViewCamera cam = view.Cam;
        SurfaceHit? hit = cam.Kind switch
        {
            ViewKind.Perspective => Nearest(Picking.Surface(cam, p, at), cam.Ray(at), 0),
            ViewKind.Top => FloorBelow(cam, at),
            _ => null,
        };
        if (hit is { } h)
        {
            // Snapped along the surface only, so the mesh stays on it.
            (Vec3 location, Rot rotation) = MeshDrop.OnSurface(h.Point, h.Normal, min, max, SnapExcept(Placement.DominantAxis(h.Normal)));
            return new Land(location, rotation, min, max, h.Object is { } on ? on.Id : "the map's collision");
        }

        // Nothing under the pointer: the mesh stands on the point, unturned; on the view plane in a 2D view.
        Vec3 free;
        int fixedAxis = 2;
        if (cam.IsOrtho)
        {
            fixedAxis = ViewAxes.DepthAxis(cam.Kind);
            free = cam.PlanePoint(at, PlaneDepth(cam, fixedAxis));
        }
        else
        {
            (Vec3 o, Vec3 d) = cam.Ray(at);
            free = o + d * FallbackDistance;
        }

        (Vec3 loc, Rot rot) = MeshDrop.OnSurface(free, Vec3.UnitZ, min, max, SnapExcept(fixedAxis));
        return new Land(loc, rot, min, max, null);
    }

    /// <summary>Snaps a point to the grid (when grid snap is on) on every axis but <paramref name="axis"/>.</summary>
    private static Func<Vec3, Vec3> SnapExcept(int axis) =>
        v => ToolManager.Instance?.Snap(v, [.. new[] { 0, 1, 2 }.Where(a => a != axis)]) ?? v;

    /// <summary>A mesh's local box: the loaded mesh's, else the asset index's, else a small default.</summary>
    private static (Vec3 Min, Vec3 Max) Box(string path)
    {
        if (StaticMeshes.Loaded(path) is { } mesh)
        {
            return (mesh.Min, mesh.Max);
        }

        return AssetService.Instance.Catalog.Find(AssetKind.Mesh, path) is { } e && e.Size != Vec3.Zero ? (e.BoundsMin, e.BoundsMax) : s_unknownBox;
    }

    /// <summary>The depth of the plane a 2D view drops on: the floor of the storey shown in the top view, else the
    /// selection's depth (as the place tool uses), else the depth of what the view was last centred on.</summary>
    private double PlaneDepth(ViewCamera cam, int axis)
    {
        if (cam.Kind == ViewKind.Top && StoreyFilter.Current is { } storey)
        {
            return storey.Level;
        }

        return _ctx.Selection.Primary is { } r && ObjectGeometry.Pivot(_ctx.Project, r) is { } pivot ? pivot[axis] : cam.Center[axis];
    }

    /// <summary>The first floor below the pointer in the top view: a ray cast down from just above the map (or the
    /// storey shown), moved on until it starts in open space, as an imported map's collision is solid outside its
    /// rooms; static meshes in the open count too.</summary>
    private SurfaceHit? FloorBelow(ViewCamera cam, Vector2 at)
    {
        MapProject p = _ctx.Project;
        _world ??= new WorldGeometry(p, _ctx.Session.Folder);
        if (!_world.HasGeometry && p.Actors.Count == 0)
        {
            return null;
        }

        Vec3 onPlane = cam.PlanePoint(at, 0);
        _heights ??= ObjectGeometry.Bounds(p, p.AllRefs()) is { } bounds ? (bounds.Min.Z - 64, bounds.Max.Z + 64) : (-64, 64);
        (double bottom, double top) = _heights.Value;
        if (StoreyFilter.Current is { } storey)
        {
            top = Math.Min(top, storey.Top - 1);
            bottom = Math.Max(bottom, storey.Bottom);
        }

        double reach = top - bottom;
        if (reach <= 0)
        {
            return null;
        }

        var origin = onPlane.With(2, top);
        var down = new Vec3(0, 0, -1);
        const double Step = 64;
        double start = 0;
        OpenScct.Editor.Core.Scripting.RayHit? world = _world.Raycast(origin, down, reach);
        for (double t = Step; world is { Distance: < 1 } && t < reach; t += Step)
        {
            start = t;
            world = _world.Raycast(origin + down * t, down, reach - t);
        }

        SurfaceHit? floor = world is { Distance: >= 1 } w
            ? new SurfaceHit(w.BrushId is { } id ? ObjectRef.Brush(id) : null, start + w.Distance, w.Point, w.Normal)
            : null;

        // Meshes reaching up to the ceiling over the point hang from it (lamps) or go through it (pillars): the ray
        // would meet them from inside, and nothing is stood on them from above.
        double ceiling = origin.Z - start;
        if (start > 0)
        {
            ceiling += _world.Raycast(origin + down * start, Vec3.UnitZ, Step) is { } up ? up.Distance : Step;
        }

        const double Hanging = 4;
        return Nearest(floor, (origin, down), start, box => box.Max.Z >= ceiling - Hanging);
    }

    /// <summary><paramref name="hit"/>, or a static mesh the ray meets before it (beyond <paramref name="from"/>), but
    /// not one whose world box <paramref name="skip"/> says to leave out.</summary>
    private SurfaceHit? Nearest(SurfaceHit? hit, (Vec3 Origin, Vec3 Direction) ray, double from, Func<(Vec3 Min, Vec3 Max), bool>? skip = null)
    {
        MapProject p = _ctx.Project;
        (Vec3 o, Vec3 d) = ray;
        SurfaceHit? best = hit;
        foreach (Actor a in p.Actors)
        {
            // The box test first: it is cheap, and most actors are nowhere near the ray.
            (Vec3 min, Vec3 max) = ObjectGeometry.ActorBounds(a);
            if (!Picking.RayBox(o, d, min, max, out double entry) || (best is { } b && entry > b.Distance)
                || StaticMeshDrawing.Loaded(a) is not { } mesh || !ObjectFilter.IsVisible(p, a.Editor) || skip?.Invoke((min, max)) == true)
            {
                continue;
            }

            if (MeshHit(a, mesh, o, d, from, best?.Distance ?? double.MaxValue) is { } m)
            {
                best = m;
            }
        }

        return best;
    }

    /// <summary>The nearest triangle of a static mesh the ray meets between <paramref name="from"/> and
    /// <paramref name="to"/>, with its normal turned towards the ray (meshes are hit from either side).</summary>
    private static SurfaceHit? MeshHit(Actor a, LoadedMesh mesh, Vec3 o, Vec3 d, double from, double to)
    {
        // The ray goes into the mesh's own space; the actor transform is affine, so the distance along it carries over.
        Vec3 lo = a.WorldToLocal(o);
        Vec3 ld = a.WorldToLocal(o + d) - lo;
        double best = to;
        (Vec3 A, Vec3 B, Vec3 C)? tri = null;
        foreach ((float[] pos, int[] idx) in mesh.Triangles)
        {
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                Vec3 ta = Corner(pos, idx[i]), tb = Corner(pos, idx[i + 1]), tc = Corner(pos, idx[i + 2]);
                if (RayTriangle(lo, ld, ta, tb, tc, out double t) && t > from && t < best)
                {
                    best = t;
                    tri = (ta, tb, tc);
                }
            }
        }

        if (tri is not { } hit)
        {
            return null;
        }

        Vec3 wa = a.LocalToWorld(hit.A);
        Vec3 n = Vec3.Cross(a.LocalToWorld(hit.B) - wa, a.LocalToWorld(hit.C) - wa).Normalized();
        return new SurfaceHit(ObjectRef.Actor(a.Id), best, o + d * best, Vec3.Dot(n, d) > 0 ? -n : n);

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

    // ---- the drop ---------------------------------------------------------------------------------------------------

    private void PlaceMesh(EditorViewport view, Vector2 at, AssetDrag drag)
    {
        MapProject p = _ctx.Project;
        Land land = Landing(view, at, drag.Path);
        Actor actor = AssetEdits.MeshActor(p, drag.Path, land.Location, rotation: land.Rotation);
        AddObjectsCommand cmd = AssetEdits.PlaceMesh(actor);
        _ctx.Execute(cmd);
        _ctx.Selection.Set(cmd.Added);
        string text = $"Placed {actor.Id} ({drag.Path}) at {actor.Location}{On(land)} in the {view.Kind} view.";
        _ctx.Log(text);
        _ctx.SetStatus(text);
    }

    /// <summary>A material or texture on the face dropped on: on every picked face when it is one of them, else on
    /// that face only (one undo step, through the surface commands). Where the view refuses it, the status bar says why.</summary>
    private void ApplySurface(EditorViewport view, Vector2 at, AssetDrag drag)
    {
        _lastSurface = null;
        (SurfaceTarget? target, string status) = Surface(view, at, drag);
        if (target is not { } t)
        {
            _ctx.SetStatus(status);
            return;
        }

        _refused = null;
        SurfaceCommands.ApplyMaterial(t.Material, t.Picked ? null : [t.Hit.Face]);
    }

    // ---- where a material or texture goes ---------------------------------------------------------------------------

    /// <summary>The face under the pointer that takes <paramref name="drag"/> and the hover status, or null and why the
    /// view refuses it: a texture no material shows, a 2D view, empty space, an actor or the imported baked world in
    /// front, a read-only face.</summary>
    private (SurfaceTarget? Target, string Status) Surface(EditorViewport view, Vector2 at, AssetDrag drag)
    {
        if (_lastSurface is { } last && last.View == view && last.At == at && last.Drag == drag)
        {
            return (last.Target, last.Status);
        }

        (SurfaceTarget? Target, string Status) answer = Resolve(view, at, drag);
        _lastSurface = (view, at, drag, answer.Target, answer.Status);
        return answer;
    }

    private (SurfaceTarget? Target, string Status) Resolve(EditorViewport view, Vector2 at, AssetDrag drag)
    {
        string name = drag.Name;
        (string? material, string why) = AssetService.Instance.SurfaceMaterial(drag.Kind, drag.Path);
        if (material is null)
        {
            return Refuse(why);
        }

        if (view.Cam.IsOrtho)
        {
            return Refuse($"a 2D view cannot show which face it would go on; drop {name} on a face in the perspective view");
        }

        MapProject p = _ctx.Project;
        FaceHit? hit = FacePicking.Pick(view.Cam, p, at);
        (Vec3 Origin, Vec3 Direction) ray = view.Cam.Ray(at);
        BakedSurfacePick? baked = FaceTool.BakedNearer(view, at, hit);

        // Whatever is nearest takes the drop: a static mesh in front of the face (or of the baked world) refuses it as
        // an actor, the baked world in front of the face as read-only.
        SurfaceHit? front = baked is { } bk ? new SurfaceHit(null, (bk.Point - ray.Origin).Length, bk.Point, bk.Normal)
            : hit is { } fh ? new SurfaceHit(ObjectRef.Brush(fh.Face.Brush), fh.Distance, fh.Point, fh.Normal) : null;
        if (Nearest(front, ray, 0) is { Object: { Kind: ObjectKind.Actor } actor })
        {
            return Refuse($"{actor.Id} is an actor; drop {name} on a brush face");
        }

        if (baked is not null)
        {
            return Refuse($"that surface is read-only, part of the imported baked world: {SurfaceCommands.BuildRenderHint}");
        }

        if (hit is not { } h)
        {
            return Refuse($"no brush face there; drop {name} on one");
        }

        if (SurfaceCommands.IsLocked(h.Face))
        {
            return Refuse($"face {h.Face} is read-only, part of the imported baked world: {SurfaceCommands.BuildRenderHint}");
        }

        // Picked faces count only where they are shown, in the surface tool.
        GeometrySelection geo = GeometrySelection.Instance;
        bool picked = ToolManager.Instance?.Active is FaceTool && geo.HasFace(h.Face);
        var target = new SurfaceTarget(h, picked, material);
        if (!picked)
        {
            string now = FaceEdit.Find(p, h.Face)?.Material is { Length: > 0 } m ? $" (now {AssetPaths.Split(m).Name})" : "";
            return (target, $"Drop to apply {name} to face {h.Face}{now}");
        }

        int locked = geo.Faces.Count(SurfaceCommands.IsLocked);
        int editable = geo.Faces.Count - locked;
        string skipped = locked > 0 ? $" ({locked} read-only skipped)" : "";
        return (target, $"Drop to apply {name} to the {PanelUi.Count(editable, "picked face")}{skipped}");
    }

    /// <summary>A refusal: the hover status says "Not here: why", and letting go there "Not applied: why".</summary>
    private static (SurfaceTarget? Target, string Status) Refuse(string why) => (null, NotHere + why);

    private const string NotHere = "Not here: ";

    /// <summary>Shows where a material or texture would go (or why not); true when the view takes it there.</summary>
    private bool ShowSurface(EditorViewport view, (SurfaceTarget? Target, string Status) at)
    {
        if (_ghost is not null && IsInstanceValid(_ghost))
        {
            _ghost.Visible = false;
        }

        // Redrawn when the face (or what it applies to) changes, not on every move over the same face.
        if (at.Target?.Hit.Face != _face?.Hit.Face || at.Target?.Picked != _face?.Picked || _faceView != view)
        {
            EditorViewport? old = _faceView;
            _face = at.Target;
            _faceView = view;
            if (old is not null && old != view && IsInstanceValid(old))
            {
                old.Redraw();
            }

            view.Redraw();
        }

        _over = view;
        _pointer = EditorPointer.Global(view);
        _frame = Engine.GetProcessFrames();
        _refused = at.Target is null ? "Not applied: " + at.Status[NotHere.Length..] : null;
        if (at.Status != _shown)
        {
            _shown = at.Status;
            _ctx.SetStatus(at.Status);
        }

        return at.Target is not null;
    }

    /// <summary>Draws the face a material or texture would go on in <paramref name="view"/>'s overlay, filled and outlined
    /// in the drop colour (and the other picked faces, lighter, when it goes on them too), so the target stands out from
    /// the picked faces' own blue.</summary>
    public void Draw(EditorViewport view, Control canvas)
    {
        if (_placeable is { } placing && placing.View == view)
        {
            PlaceableLanding.Draw(view.Cam, canvas, placing.Entry, placing.Spot);
        }

        if (_face is not { } target || _faceView != view)
        {
            return;
        }

        MapProject p = _ctx.Project;
        ViewCamera cam = view.Cam;
        if (target.Picked)
        {
            // A whole map can be picked: past a few hundred faces only the one under the pointer is drawn.
            const int MaxOutlined = 400;
            IReadOnlyList<FaceRef> picked = GeometrySelection.Instance.Faces;
            if (picked.Count <= MaxOutlined)
            {
                foreach (FaceRef f in picked)
                {
                    if (f != target.Hit.Face && !SurfaceCommands.IsLocked(f) && FacePicking.World(p, f) is { } w)
                    {
                        FacePicking.DrawPolygon(cam, canvas, w.Vertices, new Color(SceneStyle.DropTarget, 0.2f), new Color(SceneStyle.DropTarget, 0.8f));
                    }
                }
            }
        }

        if (FacePicking.World(p, target.Hit.Face) is { } face)
        {
            FacePicking.DrawPolygon(cam, canvas, face.Vertices, new Color(SceneStyle.DropTarget, 0.38f), SceneStyle.DropTarget, 3);
        }
    }

    /// <summary>The face a material or texture would be dropped on, whether it goes on all the picked faces, and the
    /// material the faces get.</summary>
    private readonly record struct SurfaceTarget(FaceHit Hit, bool Picked, string Material);


    // ---- the ghost --------------------------------------------------------------------------------------------------

    private void ShowGhost(EditorViewport view, AssetDrag drag, Land land)
    {
        if (_ghost is null || !IsInstanceValid(_ghost))
        {
            if (ViewportLayout.Instance?.Scene is not { } scene)
            {
                return;
            }

            _ghost = new MeshDropGhost { Name = "MeshDropGhost" };
            scene.AddChild(_ghost);
        }

        _ghost.Place(drag.Path, land.Min, land.Max, land.Location, land.Rotation);
        _over = view;
        _pointer = EditorPointer.Global(view);
        _frame = Engine.GetProcessFrames();
        string status = $"Drop to place {drag.Name} at {land.Location}{On(land)}";
        if (status != _shown)
        {
            _shown = status;
            _ctx.SetStatus(status);
        }
    }

    private void HideGhost()
    {
        if (_ghost is not null && IsInstanceValid(_ghost))
        {
            _ghost.Visible = false;
        }

        if (_face is not null || _faceView is not null)
        {
            EditorViewport? was = _faceView;
            _face = null;
            _faceView = null;
            if (was is not null && IsInstanceValid(was))
            {
                was.Redraw();
            }
        }

        if (_placeable is { } placing)
        {
            _placeable = null;
            PlaceableLanding.ShowGhost(null, null);
            if (IsInstanceValid(placing.View))
            {
                placing.View.Redraw();
            }
        }

        _refused = null;
        _over = null;
        if (_shown is not null)
        {
            // The "Drop to place" (or "Drop to apply") hint no longer holds.
            _shown = null;
            _ctx.SetStatus("Ready");
        }
    }

    /// <summary>" on" what the mesh lands on, for the status bar and the log.</summary>
    private static string On(Land land) => land.On is { } on ? $" on {on}" : "";

    /// <summary>Where a dropped mesh goes, the local box it is placed by, and what it lands on (an object's id, the
    /// map's collision, or null in the air).</summary>
    private readonly record struct Land(Vec3 Location, Rot Rotation, Vec3 Min, Vec3 Max, string? On);

    /// <summary>The drag is over (dropped, cancelled or the map changed): no ghost, and the next drag looks at the map
    /// afresh.</summary>
    private void End()
    {
        HideGhost();
        _ghost?.Clear();
        _world = null;
        _heights = null;
        _lastSurface = null;
    }
}
