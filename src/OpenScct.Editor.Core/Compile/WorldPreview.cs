using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// The viewport's world geometry: the CSG of the project's brushes as world polygons (no BSP, no lighting). Solid,
/// semisolid and non-solid surfaces come with their flags; portal sheets come too (Flags has PF_Portal), so the
/// viewport can show or hide them. Keeps a <see cref="CsgWorld"/> per project, so after an edit only the touched
/// brushes and their neighbours are solved again.
/// </summary>
[EditorService]
public sealed class WorldPreview : IWorldPreview
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MapProject, CsgWorld> _worlds = new();
    private readonly object _lock = new();

    public IReadOnlyList<PreviewPolygon> Build(MapProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        CsgResult csg;
        lock (_lock)
        {
            CsgWorld world = _worlds.GetValue(project, _ => new CsgWorld());
            BrushLayer layer = BakedContent.Render(project);
            csg = world.Build(layer.Brushes, layer.WorldSolid);
        }

        var result = new List<PreviewPolygon>();
        foreach (Poly p in csg.Polys)
        {
            result.Add(ToPreview(p));
        }

        return result;
    }

    public static PreviewPolygon ToPreview(Poly p)
    {
        Surface s = p.Surface;
        int flags = s.Flags | (s.Kind == SurfaceKind.Portal ? PolyFlags.Portal : 0);
        return new PreviewPolygon(p.Verts, p.Plane.N, s.Material, flags, s.BrushId, s.TextureBase, s.TextureU, s.TextureV, s.PanU, s.PanV,
            s.FaceIndex, s.LightMapScale);
    }
}
