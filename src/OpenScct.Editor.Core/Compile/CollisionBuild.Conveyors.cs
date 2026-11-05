using OpenScct.Content.Collision;
using OpenScct.Core;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;

namespace OpenScct.Editor.Core.Compile;

public sealed partial class CollisionBuild
{
    /// <summary>
    /// A conveyor's built look as world collision (spec/svmap/1.2/conveyors.md): one per-polygon collider without an
    /// actor id, so pawns stand on the belt and the escalator's slope and are stopped by the walls (open where another
    /// conveyor joins an auto-walled one). The belt and the slope are conveyor surface (footsteps) unless their material
    /// names a surface of its own. A driver conveyor has none.
    /// </summary>
    private void AddConveyor(Actor a)
    {
        Conveyor c = ConveyorDesign.Read(a);
        ConveyorGeometry look = ConveyorGeometry.Build(c, ConveyorJoins.Find(a, _ctx.Gameplay));
        // Pawns walk on the escalator's slope, not on its drawn steps.
        var solid = look.Parts.Where(p => p.Key != ConveyorPart.Steps)
            .Select(p => p.Key == ConveyorPart.Frame
                ? (c.FrameMaterial.Path, ESurfaceType.SURFACE_Generic, p.Value)
                : (c.BeltMaterial.Path, ESurfaceType.SURFACE_Conveyor, p.Value))
            .ToList();
        if (look.Slope is { } slope)
        {
            solid.Add((c.BeltMaterial.Path, ESurfaceType.SURFACE_Conveyor, slope));
        }

        if (solid.Count == 0)
        {
            return;
        }

        uint vertStart = (uint)_verts.Count, triStart = (uint)_tris.Count;
        ushort collider = (ushort)_colliders.Count;
        Func<Vec3, uint> vertex = SharedVertices();
        var points = new List<Vec3>();
        foreach ((string? materialName, ESurfaceType surface, ConveyorMesh mesh) in solid)
        {
            ushort material = Material(materialName, 0, CollisionMaterialFlags.None, surface);
            for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
            {
                uint i0 = vertex(mesh.Positions[mesh.Indices[t]]);
                uint i1 = vertex(mesh.Positions[mesh.Indices[t + 1]]);
                uint i2 = vertex(mesh.Positions[mesh.Indices[t + 2]]);
                if (i0 != i1 && i1 != i2 && i0 != i2)
                {
                    _tris.Add(new CollisionTriangle(i0, i1, i2, collider, material));
                }
            }

            points.AddRange(mesh.Positions);
        }

        AddCollider(ColliderKind.MeshPoly, WorldFlags, null, vertStart, triStart, (uint)_bsp.Count, (uint)_hulls.Count, Vec3.Zero, Rot.Zero,
            Vec3.One, Vec3.Zero, Aabb.Of(points), a.Id, null, SetPerPoly);
    }
}
