using Godot;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The picked surfaces of the surface tool in the perspective view: a tinted fill and an outline on each picked brush
/// face (or on the baked-world surface picked), drawn in the scene a little in front of the surface, so walls hide
/// what is behind them and a floor under the camera is tinted where it is seen. Shown while the surface tool is active,
/// as the 2D views draw picked faces on their overlays then (<see cref="FacePicking.DrawFaces"/>).
/// </summary>
public sealed partial class PickedFacesNode : Node3D
{
    /// <summary>How far in front of a surface the tint sits (UE units): enough to win the depth test at a distance.</summary>
    private const double Lift = 1;

    private static StandardMaterial3D? s_fill;
    private static StandardMaterial3D? s_line;
    private bool _dirty = true;

    private static StandardMaterial3D Fill => s_fill ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        RenderPriority = 8,
    };

    private static StandardMaterial3D Line => s_line ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        RenderPriority = 9,
    };

    public override void _Ready()
    {
        GeometrySelection.Instance.Changed += MarkDirty;
        EditorContext.Instance.ProjectChanged += OnProjectChanged;
        StoreyFilter.Changed += MarkDirty;
        if (ToolManager.Instance is { } tools)
        {
            tools.Changed += ShowForTool;
        }

        ShowForTool();
    }

    public override void _ExitTree()
    {
        GeometrySelection.Instance.Changed -= MarkDirty;
        EditorContext.Instance.ProjectChanged -= OnProjectChanged;
        StoreyFilter.Changed -= MarkDirty;
        if (ToolManager.Instance is { } tools)
        {
            tools.Changed -= ShowForTool;
        }
    }

    private void ShowForTool() => Visible = ToolManager.Instance?.Active is FaceTool;

    public override void _Process(double delta)
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        Rebuild();
    }

    private void OnProjectChanged(OpenScct.Editor.Core.Commands.IEditCommand _) => MarkDirty();

    private void MarkDirty() => _dirty = true;

    private void Rebuild()
    {
        foreach (Node c in GetChildren())
        {
            c.QueueFree();
        }

        GeometrySelection geo = GeometrySelection.Instance;
        MapProject p = EditorContext.Instance.Project;
        var fill = new SurfaceBuilder();
        var lines = new LineBuilder();
        Color tint = new(SceneStyle.FaceSelection, SceneStyle.FaceFillAlpha);
        Color edge = new(SceneStyle.FaceSelection, SceneStyle.FaceLineAlpha);
        if (geo.BakedSurface is { } baked)
        {
            Vec3 lift = baked.Normal * Lift;
            Vector3 n = Space.DirToGodot(baked.Normal);
            for (int i = 0; i + 2 < baked.Triangles.Count; i += 3)
            {
                Vector3[] tri = [Space.ToGodot(baked.Triangles[i] + lift), Space.ToGodot(baked.Triangles[i + 1] + lift), Space.ToGodot(baked.Triangles[i + 2] + lift)];
                fill.Polygon(tri, n, tint);
            }
        }

        if (geo.Faces.Count > 0)
        {
            Dictionary<string, Brush> brushes = FaceEdit.BrushIndex(p);
            foreach (FaceRef f in geo.Faces)
            {
                if (!brushes.TryGetValue(f.Brush, out Brush? b) || !ObjectFilter.IsVisible(p, b.Editor)
                    || BrushGeometry.Of(b) is not { } w || f.Face >= w.Faces.Count)
                {
                    continue;
                }

                WorldFace face = w.Faces[f.Face];
                Vec3 seen = b.Oper == CsgOper.Subtract ? face.Normal * -1 : face.Normal;
                Vector3[] pts = face.Vertices.Select(v => Space.ToGodot(v + seen * Lift)).ToArray();
                fill.Polygon(pts, Space.DirToGodot(seen), tint);
                lines.Polyline(pts, edge, closed: true);
            }
        }

        WorldScene.AddMesh(this, fill.Build(), Fill, VisualLayers.PickedFaces);
        WorldScene.AddMesh(this, lines.Build(), Line, VisualLayers.PickedFaces);
    }
}
