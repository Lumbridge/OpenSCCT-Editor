using Godot;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The camera of one view, in UE terms. Perspective: a location with yaw and pitch (rotator units) and a horizontal
/// field of view (the stock editor's 90 degrees unless Preferences change it). Orthographic: a centre in the view plane and a zoom in units per
/// pixel; the camera sits far back on the view axis so the whole map is in front of it.
/// </summary>
public sealed class ViewCamera
{
    /// <summary>How far behind the view plane an orthographic camera sits, in metres (maps are under 700 m).</summary>
    public const float OrthoDistance = 1000f;

    /// <summary>Horizontal field of view of every perspective view, in degrees (Preferences).</summary>
    public static double FieldOfView { get; set; } = 90;

    public ViewCamera(ViewKind kind, Camera3D camera)
    {
        Kind = kind;
        Camera = camera;
        if (IsOrtho)
        {
            Camera.Projection = Camera3D.ProjectionType.Orthogonal;
            Camera.Near = 0.05f;
            Camera.Far = OrthoDistance * 2;
        }
        else
        {
            Camera.Projection = Camera3D.ProjectionType.Perspective;
            Camera.KeepAspect = Camera3D.KeepAspectEnum.Width;
            Camera.Fov = (float)FieldOfView;
            Camera.Near = 0.05f;
            Camera.Far = 2000;
        }
    }

    public ViewKind Kind { get; }

    public Camera3D Camera { get; }

    public bool IsOrtho => Kind != ViewKind.Perspective;

    /// <summary>Perspective camera location (UE units).</summary>
    public Vec3 Location { get; set; } = new(-512, -512, 512);

    /// <summary>Perspective yaw and pitch in rotator units (65536 per turn).</summary>
    public double Yaw { get; set; } = 8192;

    public double Pitch { get; set; } = -4096;

    /// <summary>Orthographic centre (UE units; the depth component is ignored).</summary>
    public Vec3 Center { get; set; }

    /// <summary>Orthographic zoom: units per screen pixel.</summary>
    public double UnitsPerPixel { get; set; } = 4;

    /// <summary>The height range a Top view shows (the storey filter), or null for everything. Other views ignore it.</summary>
    public (double Bottom, double Top)? Clip { get; set; }

    /// <summary>How far behind the view plane the grid and reference images draw, in metres: at the back of everything,
    /// or just above the bottom of the storey a Top view shows.</summary>
    public float BackDistance => Kind == ViewKind.Top && Clip is { } c ? (float)(-(c.Bottom + 1) / Space.UnitsPerMetre) : OrthoDistance - 1;

    /// <summary>Size of the view in pixels (set by the view on resize).</summary>
    public Vector2 ScreenSize { get; set; } = new(800, 600);

    /// <summary>The box an orthographic view was last fitted to, and the view it gave; while the view is unchanged a
    /// resize fits the box again (a map opened before the views were laid out would otherwise show as a speck).</summary>
    private (Vec3 Min, Vec3 Max, Vec3 Center, double UnitsPerPixel)? _fitted;

    /// <summary>Rendered pixels per UI pixel: the view renders at the screen's resolution under a scaled UI, while
    /// screen points here are UI pixels.</summary>
    public float PixelScale { get; set; } = 1;

    private static double HalfFovTan => Math.Tan(Math.Clamp(FieldOfView, 30, 150) * Math.PI / 360);

    public Rot Rotation => new((int)Math.Round(Pitch), (int)Math.Round(Yaw), 0);

    public (Vec3 Forward, Vec3 Right, Vec3 Up) Axes => Rotation.Axes();

    /// <summary>The UE direction the view looks along: the perspective camera's forward, or straight into the screen
    /// of an orthographic view (whose pitch and yaw do not apply), as <see cref="Apply"/> sets it up.</summary>
    public Vec3 LookDirection
    {
        get
        {
            if (ViewAxes.For(Kind) is not { } screen)
            {
                return Axes.Forward;
            }

            Vector3 x = Space.DirToGodot(screen.Right), y = Space.DirToGodot(screen.Up);
            return Space.DirToUe(-x.Cross(y));
        }
    }

    /// <summary>Pushes the state into the Godot camera.</summary>
    public void Apply()
    {
        if (IsOrtho)
        {
            (Vec3 right, Vec3 up) = ViewAxes.For(Kind)!.Value;
            Vector3 x = Space.DirToGodot(right), y = Space.DirToGodot(up), z = x.Cross(y);
            Vec3 center = Flatten(Center);
            Camera.GlobalTransform = new Transform3D(new Basis(x, y, z), Space.ToGodot(center) + z * OrthoDistance);
            Camera.Size = (float)(Math.Max(ScreenSize.Y, 1) * UnitsPerPixel / Space.UnitsPerMetre);
            // The Top camera looks down from OrthoDistance metres up, so a height range is a near and far distance.
            (double bottom, double top) = Kind == ViewKind.Top && Clip is { } c ? c : (double.NegativeInfinity, double.PositiveInfinity);
            Camera.Near = double.IsFinite(top) ? Math.Max(0.05f, OrthoDistance - (float)(top / Space.UnitsPerMetre)) : 0.05f;
            Camera.Far = double.IsFinite(bottom) ? OrthoDistance - (float)(bottom / Space.UnitsPerMetre) + 0.05f : OrthoDistance * 2;
        }
        else
        {
            (Vec3 f, Vec3 r, Vec3 u) = Axes;
            Camera.Fov = (float)FieldOfView;
            Camera.GlobalTransform = new Transform3D(new Basis(Space.DirToGodot(r), Space.DirToGodot(u), -Space.DirToGodot(f)), Space.ToGodot(Location));
        }
    }

    /// <summary>The ray under a screen point, in UE units (direction normalised).</summary>
    public (Vec3 Origin, Vec3 Direction) Ray(Vector2 screen) =>
        (Space.ToUe(Camera.ProjectRayOrigin(screen * PixelScale)), Space.DirToUe(Camera.ProjectRayNormal(screen * PixelScale)).Normalized());

    /// <summary>The screen point of a UE position, or null when it is behind the camera.</summary>
    public Vector2? ToScreen(Vec3 p)
    {
        Vector3 g = Space.ToGodot(p);
        return Camera.IsPositionBehind(g) ? null : Camera.UnprojectPosition(g) / PixelScale;
    }

    /// <summary>World units per screen pixel at a point (for handle sizes and pick tolerances).</summary>
    public double UnitsPerPixelAt(Vec3 p)
    {
        if (IsOrtho)
        {
            return UnitsPerPixel;
        }

        double depth = Math.Max(Vec3.Dot(p - Location, Axes.Forward), 1);
        return 2 * depth * HalfFovTan / Math.Max(ScreenSize.X, 1);
    }

    /// <summary>The point under the cursor on the view plane (orthographic views) at the given depth value.</summary>
    public Vec3 PlanePoint(Vector2 screen, double depth)
    {
        (Vec3 o, Vec3 d) = Ray(screen);
        int axis = ViewAxes.DepthAxis(Kind);
        if (axis < 0 || Math.Abs(d[axis]) < 1e-9)
        {
            return o + d * 512;
        }

        double t = (depth - o[axis]) / d[axis];
        return (o + d * t).With(axis, depth);
    }

    /// <summary>Moves the view so the box fills most of it.</summary>
    public void Frame(Vec3 min, Vec3 max)
    {
        Vec3 c = (min + max) / 2;
        Vec3 size = max - min;
        if (IsOrtho)
        {
            (Vec3 right, Vec3 up) = ViewAxes.For(Kind)!.Value;
            double w = Math.Abs(Vec3.Dot(size, right)), h = Math.Abs(Vec3.Dot(size, up));
            Center = c;
            UnitsPerPixel = Math.Clamp(Math.Max(w / Math.Max(ScreenSize.X, 1), h / Math.Max(ScreenSize.Y, 1)) / 0.75, 0.05, 512);
            _fitted = (min, max, Center, UnitsPerPixel);
        }
        else
        {
            double radius = Math.Max(size.Length / 2, 64);
            Location = c - Axes.Forward * (radius / HalfFovTan * 1.25);
        }

        Apply();
    }

    /// <summary>The view has a new size: a view the user has not moved since <see cref="Frame"/> is fitted again.</summary>
    public void Resize(Vector2 size)
    {
        ScreenSize = size;
        if (IsOrtho && _fitted is { } f && f.Center == Center && f.UnitsPerPixel == UnitsPerPixel)
        {
            Frame(f.Min, f.Max);
        }
    }

    /// <summary>Centres the view on a point, keeping zoom and direction.</summary>
    public void LookAt(Vec3 p)
    {
        if (IsOrtho)
        {
            Center = p;
        }
        else
        {
            double distance = Math.Max((p - Location).Length, 256);
            Location = p - Axes.Forward * distance;
        }

        Apply();
    }

    /// <summary>Zeroes the depth component of an orthographic centre.</summary>
    private Vec3 Flatten(Vec3 v)
    {
        int axis = ViewAxes.DepthAxis(Kind);
        return axis < 0 ? v : v.With(axis, 0);
    }
}
