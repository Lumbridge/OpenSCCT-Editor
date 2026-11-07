using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Viewports;

public sealed partial class AssetDropTarget
{
    private (EditorViewport View, Vec3 Point)? _prefabTarget;
    private OpenScct.Editor.Core.Model.Brush? _stairGhost;
    private Vec3? _stairGhostAt;
    private string? _stairPlacementProblem;

    private bool ShowPrefab(EditorViewport view, Vector2 at, PrefabDragData prefab)
    {
        Vec3 point = PrefabPoint(view, at, prefab.SurfaceOffset);
        if (prefab.GeometryPreview is { } geometry && ToolManager.Instance is { } tools)
        {
            var builder = new BuilderBrush();
            builder.Restore(new(geometry, Vec3.Zero, Rot.Zero, null), false);
            if (!view.Cam.IsOrtho)
            {
                if (Picking.Surface(view.Cam, _ctx.Project, at) is not { } hit)
                {
                    _ctx.SetStatus("Point at a surface for the geometry preview");
                    _prefabTarget = null;
                    _stairGhost = null;
                    view.Redraw();
                    return false;
                }
                double support = builder.DisplayShape().Faces.SelectMany(f => f.Vertices).Min(p => Vec3.Dot(p, hit.Normal));
                int normalAxis = Placement.DominantAxis(hit.Normal);
                point = tools.Snap(hit.Point - hit.Normal * support, [.. new[] { 0, 1, 2 }.Where(a => a != normalAxis)]);
                point += hit.Normal * (Vec3.Dot(hit.Point - point, hit.Normal) - support);
            }
            else
            {
                int depth = ViewAxes.DepthAxis(view.Cam.Kind);
                point = tools.Snap(view.Cam.PlanePoint(at, tools.Builder.Location[depth]), [.. new[] { 0, 1, 2 }.Where(a => a != depth)]);
            }
            _stairGhost = new Brush("GeometryPreview") { Shape = builder.DisplayShape(), Location = point };
            _stairGhostAt = point;
        }
        if (prefab.StairPreview is not null && Tools.ToolManager.Instance is { } manager) point = new Vec3(manager.SnapValue(point.X), manager.SnapValue(point.Y), point.Z);
        if (prefab.StairPreview is { } design && (_stairGhostAt != point || _stairGhost?.Rotation.Yaw != prefab.StairYaw || _stairGhost.Stair?.Left != design.Left))
        {
            _stairGhostAt = point;
            try { _stairGhost = OpenScct.Editor.Core.Stairs.StairGeometry.Create("StairPreview", design, point, new OpenScct.Editor.Core.Model.Rot(0, prefab.StairYaw, 0)); }
            catch (ArgumentException ex) { _ctx.SetStatus(ex.Message); return false; }
            _stairPlacementProblem = OpenScct.Editor.Core.Stairs.StairPlacement.Problem(_ctx.Project, _stairGhost, _ctx.Session.Folder);
        }
        if (_prefabTarget is { } previous && previous.View != view) previous.View.Redraw();
        _prefabTarget = (view, point);
        view.Redraw();
        _over = view;
        _pointer = EditorPointer.Global(view);
        _frame = Engine.GetProcessFrames();
        _refused = null;
        string status = _stairPlacementProblem is { } problem ? "Not here: " + problem : prefab.GeometryPreview is null ? $"Drop to place {prefab.Title} at {point}" : $"Drop {prefab.Title} preview; adjust handles, then Add/Subtract";
        if (_shown != status)
        {
            _shown = status;
            _ctx.SetStatus(status);
        }
        return _stairPlacementProblem is null;
    }

    /// <summary>Put the prefab pivot on the visible surface under the pointer. Keep its authored orientation;
    /// in empty space use a point along the pointer ray, and in side views preserve the selection's depth.</summary>
    private Vec3 PrefabPoint(EditorViewport view, Vector2 at, double surfaceOffset = 0)
    {
        ViewCamera cam = view.Cam;
        SurfaceHit? hit = cam.Kind switch
        {
            ViewKind.Perspective => Nearest(Picking.Surface(cam, _ctx.Project, at), cam.Ray(at), 0),
            ViewKind.Top => FloorBelow(cam, at),
            _ => null,
        };
        if (hit is { } surface) return surface.Point + surface.Normal * surfaceOffset;
        if (cam.IsOrtho)
        {
            int axis = ViewAxes.DepthAxis(cam.Kind);
            return SnapExcept(axis)(cam.PlanePoint(at, PlaneDepth(cam, axis)));
        }
        (Vec3 origin, Vec3 direction) = cam.Ray(at);
        return origin + direction * FallbackDistance;
    }
}
