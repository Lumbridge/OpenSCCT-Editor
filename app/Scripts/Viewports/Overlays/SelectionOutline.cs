using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>
/// The selection drawn over the views' 1 px lines: the edges of selected brushes and shaped actors (volumes, movers)
/// <see cref="SceneStyle.SelectionWidth"/> pixels wide in the selection colour, and the primary one's outline faintly
/// filled in the 2D views (the others get <see cref="SceneStyle.SelectionMultiFillAlpha"/>, so fills never stack into a
/// wash). Other selected objects keep their 3D lines in the selection colour. Not switchable: it is the selection.
/// </summary>
public static class SelectionOutline
{
    /// <summary>A bigger selection keeps the 3D lines only, so selecting a whole map stays quick to draw.</summary>
    private const int MaxObjects = 400;

    private static OverlayBatch? s_batch;

    /// <summary>Drops the cached shapes (the map or the selection changed).</summary>
    public static void Invalidate() => s_batch = null;

    public static void Draw(EditorViewport view, Control canvas) => (s_batch ??= Build()).Draw(view.Cam, canvas);

    private static OverlayBatch Build()
    {
        var batch = new OverlayBatch();
        EditorContext ctx = EditorContext.Instance;
        MapProject p = ctx.Project;
        if (ctx.Selection.Count > MaxObjects)
        {
            return batch;
        }

        Color line = SceneStyle.Selection;
        Color primaryFill = new(SceneStyle.Selection, SceneStyle.SelectionFillAlpha);
        Color otherFill = new(SceneStyle.Selection, SceneStyle.SelectionMultiFillAlpha);
        ObjectRef? primary = ctx.Selection.Primary;
        foreach (ObjectRef r in ctx.Selection.Items)
        {
            Color fill = r == primary ? primaryFill : otherFill;
            if (r.Kind == ObjectKind.Brush && p.FindBrush(r.Id) is { } b && ObjectFilter.IsVisible(p, b.Editor))
            {
                foreach (WorldFace f in BrushGeometry.Of(b).Faces)
                {
                    Face(batch, f.Vertices, f.Normal, line, fill);
                }
            }
            else if (r.Kind == ObjectKind.Actor && p.FindActor(r.Id) is { Shape.Faces.Count: > 0 } a && ObjectFilter.IsVisible(p, a.Editor))
            {
                foreach (BrushFace f in a.Shape!.Faces)
                {
                    Vec3[] world = [.. f.Vertices.Select(a.LocalToWorld)];
                    Vec3 normal = world.Length >= 3 ? Vec3.Cross(world[1] - world[0], world[2] - world[0]).Normalized() : Vec3.Zero;
                    Face(batch, world, normal, line, fill);
                }
            }
        }

        return batch;
    }

    private static void Face(OverlayBatch batch, IReadOnlyList<Vec3> vertices, Vec3 normal, Color line, Color fill)
    {
        batch.Polyline(vertices, line, SceneStyle.SelectionWidth, closed: true);
        if (fill.A > 0)
        {
            batch.Fill(vertices, normal, fill);
        }
    }
}
