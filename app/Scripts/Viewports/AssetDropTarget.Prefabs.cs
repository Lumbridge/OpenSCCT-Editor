using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Viewports;

public sealed partial class AssetDropTarget
{
    private (EditorViewport View, Vec3 Point)? _prefabTarget;

    private bool ShowPrefab(EditorViewport view, Vector2 at, PrefabDragData prefab)
    {
        Vec3 point = PrefabPoint(view, at);
        if (_prefabTarget is { } previous && previous.View != view) previous.View.Redraw();
        _prefabTarget = (view, point);
        view.Redraw();
        _over = view;
        _pointer = EditorPointer.Global(view);
        _frame = Engine.GetProcessFrames();
        _refused = null;
        string status = $"Drop to place {prefab.Title} at {point}";
        if (_shown != status)
        {
            _shown = status;
            _ctx.SetStatus(status);
        }
        return true;
    }

    /// <summary>Put the prefab pivot on the visible surface under the pointer. Keep its authored orientation;
    /// in empty space use a point along the pointer ray, and in side views preserve the selection's depth.</summary>
    private Vec3 PrefabPoint(EditorViewport view, Vector2 at)
    {
        ViewCamera cam = view.Cam;
        SurfaceHit? hit = cam.Kind switch
        {
            ViewKind.Perspective => Nearest(Picking.Surface(cam, _ctx.Project, at), cam.Ray(at), 0),
            ViewKind.Top => FloorBelow(cam, at),
            _ => null,
        };
        if (hit is { } surface) return surface.Point;
        if (cam.IsOrtho)
        {
            int axis = ViewAxes.DepthAxis(cam.Kind);
            return SnapExcept(axis)(cam.PlanePoint(at, PlaneDepth(cam, axis)));
        }
        (Vec3 origin, Vec3 direction) = cam.Ray(at);
        return origin + direction * FallbackDistance;
    }
}
