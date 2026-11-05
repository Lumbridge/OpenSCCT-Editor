using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Snap;

/// <summary>
/// Actor boxes for neighbour snap without the editor's loaded meshes (the <c>move</c> op's <c>stick</c>): a static mesh
/// actor by its mesh's bounds from the asset index, placed with its class defaults (scale, PrePivot) and rotation;
/// else by its collision cylinder (approximate for a mesh). Shaped actors have none (they use their shape).
/// </summary>
public sealed class SnapBoxes(ActorRules rules, AssetCatalog catalog)
{
    private const int DrawTypeStaticMesh = 8;

    public SnapBox? Of(Actor a)
    {
        if (a.Shape is not null)
        {
            return null;
        }

        bool mesh = (int)rules.Number(a, "DrawType") == DrawTypeStaticMesh && PropValues.AssetPath(rules.Value(a, "StaticMesh")) is not null;
        if (mesh && PropValues.AssetPath(rules.Value(a, "StaticMesh")) is { } path && catalog.Find(AssetKind.Mesh, path) is { } e && e.Size != Vec3.Zero)
        {
            return new SnapBox(OrientedBox.FromLocal(e.BoundsMin, e.BoundsMax, p => rules.LocalToWorld(a, p)), SnapSource.Mesh, false);
        }

        (double radius, double height) = MapObjects.CollisionSize(a);
        if (radius <= 0 || height <= 0)
        {
            return null;
        }

        double half = ActorPlacement.HalfHeight(a, height);
        var ext = new Vec3(radius, radius, half);
        return new SnapBox(OrientedBox.Aligned(a.Location - ext, a.Location + ext), mesh ? SnapSource.Mesh : SnapSource.Actor, mesh);
    }
}
