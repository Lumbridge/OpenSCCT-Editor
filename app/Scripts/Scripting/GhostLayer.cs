using Godot;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Scripting;

/// <summary>
/// Draws a proposal in every view as a translucent ghost over the map: what it creates in green, what it edits (as
/// it would be afterwards) in cyan, and what it deletes outlined in red. Lines show through walls so a proposal is
/// never hidden. It lives in the shared world scene and never touches the map.
/// </summary>
public sealed partial class GhostLayer : Node3D
{
    public static readonly Color CreatedColor = new(0.35f, 1f, 0.55f);
    public static readonly Color UpdatedColor = new(0.35f, 0.9f, 1f);
    public static readonly Color DeletedColor = new(1f, 0.35f, 0.35f);

    private const uint Layers = VisualLayers.Common | VisualLayers.Selected;

    /// <summary>Height of the marker line above a ghost actor (UE units).</summary>
    private const double StalkHeight = 96;

    private static StandardMaterial3D? s_lines;
    private static StandardMaterial3D? s_fill;

    /// <summary>Bounds of what is drawn (to frame the proposal), or null when empty.</summary>
    public (Vec3 Min, Vec3 Max)? Bounds { get; private set; }

    /// <summary>Lines drawn over everything, a little see-through.</summary>
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

    /// <summary>Faint faces in their vertex colour, seen from both sides.</summary>
    private static StandardMaterial3D FillMaterial => s_fill ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        AlbedoColor = new Color(1, 1, 1, 0.18f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        RenderPriority = 11,
    };

    public void Clear()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        Bounds = null;
    }

    /// <summary>Draws <paramref name="preview"/>; deleted objects are looked up in <paramref name="map"/> (the map now).</summary>
    public void Show(ChangePreview preview, MapProject map)
    {
        Clear();
        var lines = new LineBuilder();
        var fill = new SurfaceBuilder();
        var created = preview.Created.ToHashSet();
        foreach (Brush b in preview.Brushes)
        {
            Color c = created.Contains(ObjectRef.ForBrush(b)) ? CreatedColor : UpdatedColor;
            AddBrush(lines, fill, b, c);
        }

        foreach (Actor a in preview.Actors)
        {
            AddActor(lines, a, created.Contains(ObjectRef.Actor(a.Id)) ? CreatedColor : UpdatedColor);
        }

        foreach (Ge g in preview.Ges)
        {
            AddGe(lines, g, created.Contains(ObjectRef.Ge(g.Id)) ? CreatedColor : UpdatedColor);
        }

        foreach (ObjectRef r in preview.Deleted)
        {
            switch (r.Kind)
            {
                case ObjectKind.Brush or ObjectKind.Stair when map.FindBrush(r.Id) is { } b:
                    AddBrush(lines, null, b, DeletedColor);
                    break;
                case ObjectKind.Actor when map.FindActor(r.Id) is { } a:
                    AddActor(lines, a, DeletedColor);
                    break;
                case ObjectKind.Ge when map.FindGe(r.Id) is { } g:
                    AddGe(lines, g, DeletedColor);
                    break;
            }
        }

        AddMesh(fill.Build(), FillMaterial);
        AddMesh(lines.Build(), LineMaterial);
    }

    /// <summary>Adds a brush's edges and, when <paramref name="fill"/> is given, its faint faces.</summary>
    private void AddBrush(LineBuilder lines, SurfaceBuilder? fill, Brush b, Color color)
    {
        WorldBrush world = BrushGeometry.Of(b);
        foreach (WorldFace f in world.Faces)
        {
            List<Vector3> pts = f.Vertices.Select(Space.ToGodot).ToList();
            lines.Polyline(pts, color, closed: true);
            if (fill is not null && pts.Count >= 3)
            {
                fill.Polygon(pts, Space.DirToGodot(f.Normal), color);
            }
        }

        if (!world.IsEmpty)
        {
            Grow(world.Min, world.Max);
        }
    }

    private void AddActor(LineBuilder lines, Actor a, Color color)
    {
        (Vec3 min, Vec3 max) = ObjectGeometry.ActorBounds(a);
        lines.Box(ObjectGeometry.Corners(min, max).Select(Space.ToGodot).ToList(), color);
        // A stalk up from the top of the box makes small actors easy to spot from far away.
        Vec3 top = new((min.X + max.X) / 2, (min.Y + max.Y) / 2, max.Z);
        lines.Line(Space.ToGodot(top), Space.ToGodot(top + new Vec3(0, 0, StalkHeight)), color);
        Grow(min, max);
    }

    private void AddGe(LineBuilder lines, Ge g, Color color)
    {
        if (g.Points.Count == 0)
        {
            return;
        }

        lines.Polyline(g.Points.Select(Space.ToGodot).ToList(), color, closed: false);
        (Vec3 min, Vec3 max) = ObjectGeometry.GeBounds(g);
        Grow(min, max);
    }

    private void Grow(Vec3 min, Vec3 max) =>
        Bounds = Bounds is { } b ? (Vec3.Min(b.Min, min), Vec3.Max(b.Max, max)) : (min, max);

    private void AddMesh(ArrayMesh? mesh, Material material)
    {
        if (mesh is null)
        {
            return;
        }

        var mi = new MeshInstance3D { Mesh = mesh, Layers = Layers, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        mi.SetSurfaceOverrideMaterial(0, material);
        AddChild(mi);
    }
}
