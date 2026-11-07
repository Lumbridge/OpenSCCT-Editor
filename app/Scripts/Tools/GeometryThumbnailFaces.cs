using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Tools;

/// <summary>The camera direction must be the null space of the thumbnail's projection, not an arbitrary isometric view.</summary>
public static class GeometryThumbnailFaces
{
    // Project(p) = (.6 * (x-y), .25 * (x+y) - .7 * z).
    public static readonly Vec3 Eye = new(1, 1, 5.0 / 7);

    public static IEnumerable<BrushFace> Visible(IEnumerable<BrushFace> faces) => faces
        .Where(f => Vec3.Dot(f.Normal(), Eye) > 1e-6 || (f.Flags & PolyFlags.TwoSided) != 0)
        .OrderBy(f => f.Vertices.Average(v => Vec3.Dot(v, Eye)));
}
