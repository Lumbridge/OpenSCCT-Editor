using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Viewports;

/// <summary>The 2D layer over a view: the selection's outline and name, the overlays, the active tool's handles, the face a
/// dragged material would go on, an axis tripod, the grid scale (2D views) and a border on the active view.</summary>
public sealed partial class ViewportOverlay : Control
{
    /// <summary>Gap between the selection's screen bounds and its name tag.</summary>
    private const float TagGap = 6;

    /// <summary>The tripod's centre, from the view's lower left corner, and the length of its axes.</summary>
    private const float TripodInset = 28, TripodLength = 18;

    private readonly EditorViewport? _view;

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public ViewportOverlay()
    {
    }

    public ViewportOverlay(EditorViewport view) => _view = view;

    public override void _Draw()
    {
        if (_view is null)
        {
            return;
        }

        ViewCamera cam = _view.Cam;
        SelectionOutline.Draw(_view, this);
        ViewOverlays.Draw(_view, this, Chrome(cam));
        if (ToolManager.Instance is { } tools)
        {
            if (!tools.Active.IsDragging)
            {
                DrawSelectionTag(cam, tools);
            }

            tools.Active.Draw(_view, this);
            AssetDropTarget.Instance?.Draw(_view, this);
            if (cam.IsOrtho)
            {
                DrawScale(cam, tools);
            }
        }

        DrawTripod(cam);
        if (_view.IsActiveView)
        {
            DrawRect(new Rect2(Vector2.One, Size - Vector2.One * 2), new Color(EditorTheme.Accent, 0.9f), filled: false, width: 2);
        }
    }

    /// <summary>The primary selection's name (and how many more are selected) just above the top left of its bounds on
    /// screen (just inside them when there is no room above), kept inside the view and clear of the view's own label and
    /// storey slider.</summary>
    private void DrawSelectionTag(ViewCamera cam, ToolManager tools)
    {
        if (tools.Context.Selection.Primary is not { } primary || ScreenBounds(cam, tools.Context.Project, primary) is not { } bounds)
        {
            return;
        }

        int count = tools.Context.Selection.Count;
        TagPart[] parts = count > 1
            ? [new(primary.Id, SceneStyle.Selection), new($"  +{count - 1}", EditorTheme.TextDim)]
            : [new(primary.Id, SceneStyle.Selection)];
        Vector2 size = ViewTags.Size(GetThemeDefaultFont(), parts);
        float top = _view!.TagTop;
        Vector2 at = bounds.Position - new Vector2(0, size.Y + TagGap);
        if (at.Y < top)
        {
            // No room above the bounds: just inside their top edge instead.
            at.Y = bounds.Position.Y + TagGap;
        }

        at = ViewTags.Inside(this, at, size);
        at.Y = Math.Max(at.Y, top);
        Rect2 storeys = _view.StoreysRect;
        if (storeys.HasArea() && storeys.Intersects(new Rect2(at, size)))
        {
            // Left of the storey slider rather than under it.
            at.X = Math.Max(ViewTags.Inset, storeys.Position.X - EditorTheme.SpaceXs - size.X);
        }

        ViewTags.Draw(this, at, parts);
    }

    /// <summary>The screen rectangle of an object's bounds; its pivot point when part of the box is behind the camera.</summary>
    private static Rect2? ScreenBounds(ViewCamera cam, MapProject p, ObjectRef r)
    {
        if (ObjectGeometry.Bounds(p, r) is { } box)
        {
            Rect2? rect = null;
            foreach (Vec3 corner in ObjectGeometry.Corners(box.Min, box.Max))
            {
                if (cam.ToScreen(corner) is not { } s)
                {
                    rect = null;
                    break;
                }

                rect = rect is { } sofar ? sofar.Expand(s) : new Rect2(s, Vector2.Zero);
            }

            if (rect is not null)
            {
                return rect;
            }
        }

        return ObjectGeometry.Pivot(p, r) is { } pivot && cam.ToScreen(pivot) is { } at ? new Rect2(at, Vector2.Zero) : null;
    }

    /// <summary>The view's own chrome, which world labels keep clear of: its label and storey slider at the top, the
    /// tripod at the lower left and the scale tag at the lower right.</summary>
    private List<Rect2> Chrome(ViewCamera cam)
    {
        var rects = new List<Rect2> { _view!.LabelRect, new(0, Size.Y - 2 * TripodInset, 2 * TripodInset, 2 * TripodInset) };
        if (_view.StoreysRect is { } storeys && storeys.HasArea())
        {
            rects.Add(storeys);
        }

        if (cam.IsOrtho && ToolManager.Instance is { } tools)
        {
            rects.Add(ScaleRect(cam, tools, out _));
        }

        return rects;
    }

    /// <summary>The grid size and the zoom, at the lower right of a 2D view.</summary>
    private void DrawScale(ViewCamera cam, ToolManager tools)
    {
        Rect2 rect = ScaleRect(cam, tools, out TagPart[] parts);
        ViewTags.Draw(this, rect.Position, parts);
    }

    private Rect2 ScaleRect(ViewCamera cam, ToolManager tools, out TagPart[] parts)
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"Grid {tools.GridSize:0.##}{(tools.GridSnap ? "" : " (snap off)")} \u00b7 {cam.UnitsPerPixel:0.##} u/px");
        parts = [new(text, EditorTheme.TextDim)];
        Vector2 size = ViewTags.Size(GetThemeDefaultFont(), parts);
        return new Rect2(Size - size - new Vector2(ViewTags.Inset, ViewTags.Inset), size);
    }

    /// <summary>Small X/Y/Z axes in the lower left, turned like the view.</summary>
    private void DrawTripod(ViewCamera cam)
    {
        var origin = new Vector2(TripodInset, Size.Y - TripodInset);
        Basis b = cam.Camera.GlobalTransform.Basis;
        Font font = GetThemeDefaultFont();
        const int fontSize = EditorTheme.FontSizeMicro;
        string[] names = ["X", "Y", "Z"];
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 g = Space.DirToGodot(Vec3.Zero.With(axis, 1));
            // Camera space: x right, y up; screen y grows downwards.
            var dir = new Vector2(g.Dot(b.X), -g.Dot(b.Y));
            if (dir.LengthSquared() < 0.01f)
            {
                continue;
            }

            Vector2 end = origin + dir * TripodLength;
            Color c = SceneStyle.AxisColor(axis);
            DrawLine(origin, end, c, 2, antialiased: true);
            Vector2 letter = font.GetStringSize(names[axis], fontSize: fontSize);
            Vector2 centre = end + dir.Normalized() * 7;
            DrawString(font, centre + new Vector2(-letter.X / 2, font.GetAscent(fontSize) / 2 - 1), names[axis], fontSize: fontSize, modulate: c);
        }
    }
}
