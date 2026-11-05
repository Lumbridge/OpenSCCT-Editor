using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The mesh being dragged into a view, drawn where it would land: its box outline over everything, and once the mesh
/// has loaded a see-through copy of it in the textured render modes. It is built once per mesh and only moved while
/// the pointer moves; it lives in the shared world scene and never touches the map.
/// </summary>
public sealed partial class MeshDropGhost : Node3D
{
    public static readonly Color GhostColor = new(0.3f, 0.95f, 1f);

    /// <summary>How see-through the mesh copy is (0 solid, 1 invisible).</summary>
    private const float MeshTransparency = 0.3f;

    private static StandardMaterial3D? s_lines;
    private string? _path;
    private (Vec3 Min, Vec3 Max) _box;
    private MeshInstance3D? _outline;
    private MeshInstance3D? _mesh;

    /// <summary>The box outline, drawn over everything so the ghost is never hidden behind a wall.</summary>
    private static StandardMaterial3D LineMaterial => s_lines ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        NoDepthTest = true,
        RenderPriority = 12,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(1, 1, 1, 0.9f),
    };

    /// <summary>Shows mesh <paramref name="path"/> with local box <paramref name="min"/>..<paramref name="max"/> at a
    /// location and rotation (UE).</summary>
    public void Place(string path, Vec3 min, Vec3 max, Vec3 location, Rot rotation)
    {
        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            Load(path);
        }

        if (_box != (min, max))
        {
            _box = (min, max);
            _outline?.QueueFree();
            var lines = new LineBuilder();
            lines.Box(ObjectGeometry.Corners(min, max).Select(Space.ToGodot).ToList(), GhostColor);
            _outline = WorldScene.AddMesh(this, lines.Build(), LineMaterial, VisualLayers.Common);
        }

        Transform = new Transform3D(Space.RotationToGodot(rotation), Space.ToGodot(location));
        Visible = true;
    }

    /// <summary>Hides the ghost and forgets its mesh.</summary>
    public void Clear()
    {
        Visible = false;
        _path = null;
        _box = default;
        _outline?.QueueFree();
        _mesh?.QueueFree();
        _outline = _mesh = null;
    }

    private void Load(string path)
    {
        Clear();
        _path = path;
        StaticMeshes.Request(path, mesh =>
        {
            // The drag may have ended, or moved on to another mesh, while this one loaded.
            if (!IsInstanceValid(this) || !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Only where placed meshes draw textured: the wireframe views show the box, as they show placed meshes, and a
            // big mesh does not cover the map there.
            _mesh = TexturedSurfaces.Instance(mesh.Mesh, mesh.Materials, lit: false, VisualLayers.Unlit | VisualLayers.Lit);
            _mesh.Transparency = MeshTransparency;
            AddChild(_mesh);
        });
    }
}
