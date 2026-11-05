using System.Runtime.CompilerServices;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>
/// A brush's distinct world vertices and edges for the vertex tool, worked out once per brush state (keyed on the
/// cached <see cref="WorldBrush"/>, which is replaced when the brush changes), so drawing and picking every frame stay
/// cheap with many brushes selected.
/// </summary>
public static class BrushVertices
{
    private static readonly ConditionalWeakTable<WorldBrush, Lists> s_cache = new();

    public static IReadOnlyList<Vec3> Of(Brush b) => Get(b).Vertices;

    public static IReadOnlyList<(Vec3 A, Vec3 B)> EdgesOf(Brush b) => Get(b).Edges;

    private static Lists Get(Brush b) =>
        s_cache.GetValue(BrushGeometry.Of(b), w =>
        {
            List<IReadOnlyList<Vec3>> faces = w.Faces.Select(f => (IReadOnlyList<Vec3>)f.Vertices).ToList();
            return new Lists(VertexEdit.DistinctVertices(faces), VertexEdit.DistinctEdges(faces));
        });

    private sealed record Lists(List<Vec3> Vertices, List<(Vec3 A, Vec3 B)> Edges);
}
