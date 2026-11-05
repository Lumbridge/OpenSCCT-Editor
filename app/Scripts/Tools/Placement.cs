using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>Where a click puts something. The perspective view uses the surface under the cursor (lifted off it
/// along its normal); orthographic views use the view plane at the depth of the current selection (or 0), with the
/// two in-plane coordinates snapped to the grid.</summary>
public static class Placement
{
    /// <summary>The point for a click; <paramref name="lift"/> gives how far to stand off a surface with that
    /// normal (half the placed thing's size along it), or null to touch it.</summary>
    public static Vec3 Point(ToolManager tools, ViewCamera cam, Vector2 mouse, Func<Vec3, double>? lift = null, double? depth = null)
    {
        MapProject p = tools.Context.Project;
        if (!cam.IsOrtho)
        {
            if (Picking.Surface(cam, p, mouse) is { } hit)
            {
                // Snap along the surface only, so the thing stays on it.
                Vec3 at = hit.Point + hit.Normal * (lift?.Invoke(hit.Normal) ?? 0);
                int normalAxis = DominantAxis(hit.Normal);
                return tools.Snap(at, [.. new[] { 0, 1, 2 }.Where(a => a != normalAxis)]);
            }

            (Vec3 o, Vec3 d) = cam.Ray(mouse);
            return tools.Snap(o + d * 512);
        }

        int axis = ViewAxes.DepthAxis(cam.Kind);
        double z = depth ?? (tools.Context.Selection.Primary is { } r && ObjectGeometry.Pivot(p, r) is { } pivot ? pivot[axis] : 0);
        Vec3 point = cam.PlanePoint(mouse, z);
        return tools.Snap(point, [.. new[] { 0, 1, 2 }.Where(a => a != axis)]);
    }

    /// <summary>Half the extent of a box along a normal's main axis (to sit the box on a surface).</summary>
    public static double HalfAlong(Vec3 halfExtent, Vec3 normal) => halfExtent[DominantAxis(normal)];

    public static int DominantAxis(Vec3 n)
    {
        double x = Math.Abs(n.X), y = Math.Abs(n.Y), z = Math.Abs(n.Z);
        return z >= x && z >= y ? 2 : x >= y ? 0 : 1;
    }
}
