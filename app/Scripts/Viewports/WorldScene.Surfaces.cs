using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Viewports;

public sealed partial class WorldScene
{
    /// <summary>The cut polygons currently drawn, with their editable source faces. Null before the first preview.</summary>
    public IReadOnlyList<PreviewPolygon>? SurfacePolygons(MapProject project) =>
        ReferenceEquals(_project, project) ? _previewPolys : null;
}
