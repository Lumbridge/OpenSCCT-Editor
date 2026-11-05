using System.Text.Json.Nodes;
using OpenScct.Core.Uc;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;

namespace OpenScct.Editor.Core.Compile;

public sealed partial class RenderBuild
{
    /// <summary>
    /// Each conveyor's built look (spec/svmap/1.2/conveyors.md): one vertex-lit library mesh per part, the static frame
    /// as a plain instance and the moving parts as gameplay instances named after the conveyor, and its item meshes in
    /// the library for the game to draw.
    /// </summary>
    private void AddConveyors()
    {
        int count = 0, triangles = 0;
        foreach (Actor a in _ctx.Gameplay.Where(ConveyorDesign.Is))
        {
            Conveyor c = ConveyorDesign.Read(a);
            ConveyorGeometry look = ConveyorGeometry.Build(c, ConveyorJoins.Find(a, _ctx.Gameplay));
            foreach ((ConveyorPart part, ConveyorMesh mesh) in look.Parts.OrderBy(p => p.Key))
            {
                AssetRef material = part == ConveyorPart.Frame ? c.FrameMaterial : c.BeltMaterial;
                MaterialUse use = Material(material.Path, 0);
                string asset = $"{_ctx.MapId}.Conveyor.{a.Id}_{part}";
                string node = $"{asset}#vc";
                _library.Add(_glb.AddNode(new JsonObject
                {
                    ["extras"] = new JsonObject { ["svmap"] = new JsonObject { ["asset"] = asset, ["role"] = "libraryMesh", ["variant"] = "vc" } },
                    ["mesh"] = ConveyorMeshData(asset, use, mesh),
                    ["name"] = node,
                }));
                JsonObject instance = Instance(a.Id + PartSuffix(part), "Conveyor", part != ConveyorPart.Frame, false, node, Vec3.Zero, Rot.Zero,
                    Vec3.One, Vec3.Zero, vertexLit: !use.Unlit);
                if (use.Unlit)
                {
                    instance["unlit"] = true;
                }
                else
                {
                    _vertexLit.Add(new JsonObject { ["base"] = asset, ["name"] = node });
                }

                _instances.Add(instance);
                triangles += mesh.TriangleCount;
            }

            foreach (AssetRef item in c.ItemMeshes)
            {
                if (item.Path is { } path && ImportMesh(path) is null)
                {
                    _ctx.Warn("render.mesh-missing", $"conveyor {a.Id}: no converted map ships item mesh {path}; its items are not drawn",
                        ObjectRef.Actor(a.Id));
                }
            }

            count++;
        }

        _ctx.Count("render.conveyors", count);
        _ctx.Count("render.conveyorTriangles", triangles);
    }

    private static string PartSuffix(ConveyorPart part) => part switch
    {
        ConveyorPart.Belt => ConveyorLayout.BeltPart,
        ConveyorPart.Steps => ConveyorLayout.StepsPart,
        ConveyorPart.Rollers => ConveyorLayout.RollersPart,
        _ => ".Frame",
    };

    /// <summary>A conveyor part as a glTF mesh, light baked into its vertex colours as for placed static meshes
    /// (<see cref="WorldLighting.VertexAt"/>; the game adds the zone ambient).</summary>
    private int ConveyorMeshData(string name, MaterialUse use, ConveyorMesh mesh)
    {
        int n = mesh.Positions.Count;
        var pos = new float[n * 3];
        var nrm = new float[n * 3];
        var uv = new float[n * 2];
        var col = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            Vec3 v = mesh.Positions[i], normal = mesh.Normals[i];
            GltfSpace.Position(v, pos, i * 3);
            GltfSpace.Normal(normal, nrm, i * 3);
            uv[i * 2] = (float)mesh.Uvs[i].U;
            uv[i * 2 + 1] = (float)mesh.Uvs[i].V;
            Vec3 light = use.Unlit ? new Vec3(0.5, 0.5, 0.5) : _lighting.VertexAt(v, normal);
            col[i * 4] = WorldLighting.StoreVertex(light.X);
            col[i * 4 + 1] = WorldLighting.StoreVertex(light.Y);
            col[i * 4 + 2] = WorldLighting.StoreVertex(light.Z);
            col[i * 4 + 3] = 255;
        }

        // glTF winds the other way round.
        var idx = new uint[mesh.Indices.Count];
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            idx[t] = (uint)mesh.Indices[t];
            idx[t + 1] = (uint)mesh.Indices[t + 2];
            idx[t + 2] = (uint)mesh.Indices[t + 1];
        }

        return _glb.AddMesh(name.Replace('.', '_'), pos, nrm, uv, col, idx, use.GltfIndex);
    }
}
