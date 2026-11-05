using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Gameplay;

/// <summary>A point the cursor found on the level geometry, with the surface normal there (null off the geometry).</summary>
public readonly record struct GameplayHit(Vec3 Point, Vec3? Normal);

/// <summary>Cursor rays against the traversal world (brushes and a baked map's collision), for the gameplay tools.</summary>
public static class GameplayPicking
{
    private const double Reach = 65536;

    /// <summary>The level surface under the cursor, or null when the ray hits nothing.</summary>
    public static TraceHit? Surface(TraversalWorld w, ViewCamera cam, Vector2 mouse)
    {
        (Vec3 o, Vec3 d) = cam.Ray(mouse);
        // Orthographic rays start on the camera plane, which may sit inside the level: trace from far behind it.
        Vec3 from = cam.IsOrtho ? o - d * Reach : o;
        return w.Trace(from, o + d * Reach);
    }

    /// <summary>Where a click puts a GE point: on the surface under the cursor (snapped along it), else the tool's
    /// usual placement point.</summary>
    public static GameplayHit Point(ToolManager tools, TraversalWorld w, ViewCamera cam, Vector2 mouse, double? depth = null)
    {
        if (Surface(w, cam, mouse) is { } hit)
        {
            int normalAxis = Placement.DominantAxis(hit.Normal);
            return new GameplayHit(tools.Snap(hit.Point, [.. new[] { 0, 1, 2 }.Where(a => a != normalAxis)]), hit.Normal);
        }

        return new GameplayHit(Placement.Point(tools, cam, mouse, depth: depth), null);
    }
}
