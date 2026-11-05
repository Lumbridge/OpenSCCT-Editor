using Godot;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The grid of one view, on that view's own layer. Orthographic views get a grid in the view plane that covers the
/// screen, placed at the back of what the view shows (<see cref="ViewCamera.BackDistance"/>) so everything draws over
/// it; the spacing is the snap grid, doubled until lines are at least 8 pixels apart, with a major line every 8 cells
/// (Preferences change it) and the world axes tinted. Minor lines fade as they close up towards 8 pixels, so a
/// zoomed-out view is not a mesh of lines. The perspective view gets a floor grid at Z = 0 around the camera.
/// </summary>
public sealed partial class ViewGrid : MeshInstance3D
{
    private (Vec3 Center, double Zoom, double Grid, Vector2 Size, Vec3 Cam, float Back) _built;

    /// <summary>Minor lines this many pixels apart (or closer) are drawn at <see cref="MinorFadeFloor"/> of their colour's
    /// strength; from <see cref="MinorFadeEnd"/> pixels apart they are drawn in full.</summary>
    private const double MinorFadeStart = 8, MinorFadeEnd = 24;

    private const float MinorFadeFloor = 0.35f;

    /// <summary>The world axes on the grid: their axis colour at half strength.</summary>
    private const float AxisAlpha = 0.5f;

    /// <summary>A major line every this many cells (Preferences).</summary>
    public static int MajorEvery { get; set; } = 8;

    public ViewKind Kind { get; set; }

    public void Refresh(ViewCamera cam, double gridSize, bool visible)
    {
        Visible = visible;
        Layers = VisualLayers.Grid(Kind);
        CastShadow = ShadowCastingSetting.Off;
        if (!visible)
        {
            return;
        }

        var key = (cam.Center, cam.UnitsPerPixel, gridSize, cam.ScreenSize, cam.IsOrtho ? Vec3.Zero : Snap(cam.Location, 256), cam.BackDistance);
        if (key == _built && Mesh is not null)
        {
            return;
        }

        _built = key;
        Mesh = cam.IsOrtho ? BuildOrtho(cam, gridSize) : BuildFloor(cam, gridSize);
    }

    /// <summary>Builds the grid again at the next refresh (its colours or spacing changed).</summary>
    public void Invalidate() => _built = default;

    private static Vec3 Snap(Vec3 v, double step) => new(Math.Round(v.X / step) * step, Math.Round(v.Y / step) * step, 0);

    private ArrayMesh? BuildOrtho(ViewCamera cam, double gridSize)
    {
        (Vec3 right, Vec3 up) = ViewAxes.For(Kind)!.Value;
        double spacing = Math.Max(gridSize, 1);
        while (spacing / cam.UnitsPerPixel < 8)
        {
            spacing *= 2;
        }

        double halfW = cam.ScreenSize.X * cam.UnitsPerPixel / 2 + spacing;
        double halfH = cam.ScreenSize.Y * cam.UnitsPerPixel / 2 + spacing;
        double cx = Vec3.Dot(cam.Center, right), cy = Vec3.Dot(cam.Center, up);
        Color minor = MinorAt(spacing / cam.UnitsPerPixel);
        // Behind everything the view shows, so the map always draws over the grid.
        Vector3 back = Space.DirToGodot(right).Cross(Space.DirToGodot(up));
        Vector3 behind = -back * cam.BackDistance;
        var lines = new LineBuilder();

        Vector3 At(double a, double b) => Space.ToGodot(right * a + up * b) + behind;
        void Lines(double c, double half, double otherC, double otherHalf, bool vertical)
        {
            long first = (long)Math.Floor((c - half) / spacing), last = (long)Math.Ceiling((c + half) / spacing);
            for (long i = first; i <= last; i++)
            {
                double v = i * spacing;
                Color color = i == 0 ? (vertical ? AxisTint(up) : AxisTint(right)) : i % MajorEvery == 0 ? SceneStyle.GridMajor : minor;
                if (vertical)
                {
                    lines.Line(At(v, otherC - otherHalf), At(v, otherC + otherHalf), color);
                }
                else
                {
                    lines.Line(At(otherC - otherHalf, v), At(otherC + otherHalf, v), color);
                }
            }
        }

        Lines(cx, halfW, cy, halfH, vertical: true);
        Lines(cy, halfH, cx, halfW, vertical: false);
        return Finish(lines);
    }

    private static ArrayMesh? BuildFloor(ViewCamera cam, double gridSize)
    {
        double height = Math.Abs(cam.Location.Z);
        double spacing = Math.Max(gridSize, 16);
        while (spacing < height / 12)
        {
            spacing *= 2;
        }

        const int cells = 48;
        double major = spacing * MajorEvery;
        double cx = Math.Round(cam.Location.X / major) * major;
        double cy = Math.Round(cam.Location.Y / major) * major;
        double half = cells * spacing;
        var lines = new LineBuilder();
        for (int i = -cells; i <= cells; i++)
        {
            Color c = i % MajorEvery == 0 ? SceneStyle.GridMajor : SceneStyle.GridMinor;
            double x = cx + i * spacing, y = cy + i * spacing;
            lines.Line(Space.ToGodot(new Vec3(x, cy - half, 0)), Space.ToGodot(new Vec3(x, cy + half, 0)), x == 0 ? new Color(SceneStyle.AxisY, AxisAlpha) : c);
            lines.Line(Space.ToGodot(new Vec3(cx - half, y, 0)), Space.ToGodot(new Vec3(cx + half, y, 0)), y == 0 ? new Color(SceneStyle.AxisX, AxisAlpha) : c);
        }

        return Finish(lines);
    }

    private static Color AxisTint(Vec3 axis) =>
        new(SceneStyle.AxisColor(Math.Abs(axis.X) > 0.5 ? 0 : Math.Abs(axis.Y) > 0.5 ? 1 : 2), AxisAlpha);

    /// <summary>The minor line colour for lines <paramref name="pixels"/> apart.</summary>
    private static Color MinorAt(double pixels)
    {
        float t = (float)Math.Clamp((pixels - MinorFadeStart) / (MinorFadeEnd - MinorFadeStart), 0, 1);
        Color c = SceneStyle.GridMinor;
        return new Color(c, c.A * Mathf.Lerp(MinorFadeFloor, 1, t));
    }

    private static ArrayMesh? Finish(LineBuilder lines)
    {
        ArrayMesh? mesh = lines.Build();
        mesh?.SurfaceSetMaterial(0, SceneStyle.Grid);
        return mesh;
    }
}
