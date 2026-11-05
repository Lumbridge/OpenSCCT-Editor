using Godot;
using OpenScct.Editor.Core.Assets;

namespace OpenScctEditor.Viewports.Render;

/// <summary>
/// The baked BSP of an imported map, textured: each section of its <c>world.glb</c> BSP meshes with its material's
/// base texture, as the static meshes are drawn, cut to the storey shown (<see cref="StoreyClip"/>). <see cref="Read"/>
/// runs on a worker thread; <see cref="Show"/> on the main thread. The baked lightmaps are not applied (the lit view uses the editor's preview lights).
/// </summary>
public static class BakedSurfaces
{
    /// <summary>The sections of the given BSP meshes, or null when the glb cannot be read that way (the caller then
    /// draws flat colours).</summary>
    public static List<MeshSection>? Read(string glbPath, IReadOnlyList<int> meshes)
    {
        string renderDir = Path.GetDirectoryName(Path.GetFullPath(glbPath))!;
        try
        {
            return meshes.SelectMany(m => MeshSections.Read(glbPath, renderDir, m)).ToList();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException
            or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Adds the unlit and lit instances under <paramref name="parent"/>; false when there is nothing to draw.</summary>
    public static bool Show(Node3D parent, List<MeshSection> sections)
    {
        if (StaticMeshes.Build(sections) is not { } mesh)
        {
            return false;
        }

        MeshInstance3D unlit = TexturedSurfaces.Instance(mesh.Mesh, mesh.Materials, lit: false, VisualLayers.Unlit);
        MeshInstance3D lit = TexturedSurfaces.Instance(mesh.Mesh, mesh.Materials, lit: true, VisualLayers.Lit);
        unlit.Name = "BspUnlit";
        lit.Name = "BspLit";
        parent.AddChild(unlit);
        parent.AddChild(lit);
        return true;
    }
}
