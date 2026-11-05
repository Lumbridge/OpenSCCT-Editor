using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>
/// The brush clip tool (UE2 brush clip): a plane from two points in a 2D view (the plane runs along the view's depth
/// axis) or three points anywhere. Clip keeps the part behind the plane; split keeps both parts as two brushes.
/// </summary>
public static class ClipEdit
{
    /// <summary>
    /// The clip plane through the points: three points give their plane (normal by the right-hand rule); two points
    /// give the plane through them that contains <paramref name="depthAxis"/> (0 X, 1 Y, 2 Z). Null when the points
    /// do not define a plane.
    /// </summary>
    public static Plane3? PlaneFrom(IReadOnlyList<Vec3> points, int depthAxis = 2)
    {
        return points.Count switch
        {
            >= 3 => Plane3.FromPoints(points[0], points[1], points[2]),
            2 => Plane3.FromPoints(points[0], points[1], points[0] + Vec3.Zero.With(depthAxis, 256)),
            _ => null,
        };
    }

    /// <summary>The axis plane through <paramref name="point"/> across <paramref name="axis"/> (normal +axis).</summary>
    public static Plane3 AxisPlane(int axis, Vec3 point) => Plane3.FromPointNormal(point, Vec3.Zero.With(axis, 1));

    /// <summary>
    /// Clips (or splits) the brushes by a world plane. Clip replaces each brush by its back part and deletes brushes
    /// wholly in front; split keeps the back part in place and inserts the front part right after it. Brushes wholly
    /// behind the plane (or wholly in front, for split) are left alone.
    /// </summary>
    public static BrushListEdit Run(MapProject p, IEnumerable<string> brushIds, Plane3 plane, bool split, string? capMaterial = null)
    {
        var edit = new BrushListEdit(split ? "Split brushes" : "Clip brushes");
        var ids = new FreshIds(p);
        foreach (string id in brushIds)
        {
            Brush b = p.FindBrush(id) ?? throw new ArgumentException($"no brush '{id}'");
            (Brush? front, Brush? back) = BrushOps.Split(b, plane, capMaterial);
            if (front is null)
            {
                continue; // wholly behind: nothing to cut
            }

            if (back is null)
            {
                if (!split)
                {
                    edit.Delete(b);
                }

                continue;
            }

            edit.Replace(b, back);
            if (split)
            {
                // The new piece is a plain brush: only the back piece stays the prefab part.
                front.Id = ids.Next(b.Id);
                front.Editor.UnlinkPrefab();
                edit.InsertAfter(b, front);
            }
        }

        return edit;
    }
}
