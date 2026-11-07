using System.Runtime.CompilerServices;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Assets;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Tools;

/// <summary>
/// The boxes neighbour snap uses for actors and the faces it uses for brushes. A static mesh actor sticks by its mesh's
/// box, carried into the world as it is drawn (class-default scale and PrePivot honoured, rotation kept, so a turned
/// crate's box stays tight); a mesh not loaded yet by the asset index's bounds (read from the same file, so exact, as the
/// <c>move</c> op's <see cref="SnapBoxes"/> has them); other actors by their collision cylinder as a box, else their class
/// box (approximate). A drag never starts a mesh load.
/// </summary>
public static class ActorSnapBox
{
    private static readonly ConditionalWeakTable<WorldBrush, IReadOnlyList<SnapFace>> s_brushFaces = new();

    /// <summary>The actor's snap box, or null for a shaped actor (volumes and mover brushes use their shape).</summary>
    public static SnapBox? Of(Actor a)
    {
        if (a.Shape is not null)
        {
            return null;
        }

        if (StaticMeshDrawing.MeshPath(a) is { } path)
        {
            if (StaticMeshes.Loaded(path) is { } mesh)
            {
                return new SnapBox(OrientedBox.FromLocal(mesh.Min, mesh.Max, p => StaticMeshDrawing.MeshToWorld(a, p)), SnapSource.Mesh, false);
            }

            if (AssetService.Instance.Catalog.Find(AssetKind.Mesh, path) is { } e && e.Size != Vec3.Zero)
            {
                // The index reads its bounds from the same mesh file the views load, so they are the mesh box itself.
                return new SnapBox(OrientedBox.FromLocal(e.BoundsMin, e.BoundsMax, p => StaticMeshDrawing.MeshToWorld(a, p)), SnapSource.Mesh, false);
            }

            return new SnapBox(Cylinder(a) ?? ClassBox(a), SnapSource.Mesh, true);
        }

        return Cylinder(a) is { } box ? new SnapBox(box, SnapSource.Actor, false) : new SnapBox(ClassBox(a), SnapSource.Actor, true);
    }

    /// <summary>A brush's snap faces, made once per brush state (the views' world faces, so nothing is recomputed).</summary>
    public static IReadOnlyList<SnapFace> Faces(Brush b)
    {
        WorldBrush w = BrushGeometry.Of(b);
        if (s_brushFaces.TryGetValue(w, out IReadOnlyList<SnapFace>? hit))
        {
            return hit;
        }

        ObjectRef owner = ObjectRef.ForBrush(b);
        var faces = new List<SnapFace>(w.Faces.Count);
        for (int i = 0; i < w.Faces.Count; i++)
        {
            if (SnapFace.Of(owner, i, SnapSource.Brush, w.Faces[i].Vertices) is { } f)
            {
                faces.Add(f);
            }
        }

        s_brushFaces.AddOrUpdate(w, faces);
        return faces;
    }

    /// <summary>The collision cylinder (radius, half height) as a world box, or null when the actor has none.</summary>
    private static OrientedBox? Cylinder(Actor a)
    {
        (double radius, double height) = MapObjects.CollisionSize(a);
        if (radius <= 0 || height <= 0)
        {
            return null;
        }

        double half = ActorPlacement.HalfHeight(a, height);
        var e = new Vec3(radius, radius, half);
        return OrientedBox.Aligned(a.Location - e, a.Location + e);
    }

    private static OrientedBox ClassBox(Actor a)
    {
        Vec3 h = ObjectGeometry.ActorHalfExtent(a, ActorLook.For(a));
        return OrientedBox.Aligned(a.Location - h, a.Location + h);
    }
}
