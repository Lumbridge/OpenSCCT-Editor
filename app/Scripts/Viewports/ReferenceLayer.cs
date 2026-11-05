using Godot;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The reference images of one 2D view (workspace references: a floor plan, a sketch, a screenshot of the original
/// map), drawn on that view's own layer just in front of its grid, so the map draws over them. An image is centred on
/// its origin and sized by its units per pixel. In the Top view with a storey shown, only the images whose origin
/// height lies in that storey show, so each floor can have its own plan.
/// </summary>
public sealed partial class ReferenceLayer : Node3D
{
    private static readonly Dictionary<string, (DateTime Written, ImageTexture? Texture)> s_textures = new(StringComparer.OrdinalIgnoreCase);
    private string _built = "";

    public ViewKind Kind { get; set; }

    /// <summary>Redraws the images when they, the storey or the view's back plane changed.</summary>
    public void Refresh(ViewCamera cam, IReadOnlyList<ReferenceImage> references, string? folder)
    {
        string view = Kind switch { ViewKind.Top => "top", ViewKind.Front => "front", ViewKind.Side => "side", _ => "" };
        Storey? storey = Kind == ViewKind.Top ? StoreyFilter.Current : null;
        List<ReferenceImage> shown = references
            .Where(r => string.Equals(r.View, view, StringComparison.OrdinalIgnoreCase) && (storey is null || storey.Contains(r.Origin.Z)))
            .ToList();
        string key = string.Join("|", shown.Select(r => $"{r.Path};{r.Origin};{r.UnitsPerPixel};{r.Opacity}")) + $"|{cam.BackDistance}|{folder}";
        if (key == _built)
        {
            return;
        }

        _built = key;
        foreach (Node child in GetChildren())
        {
            child.QueueFree();
        }

        (Vec3 right, Vec3 up) = ViewAxes.For(Kind) ?? (Vec3.UnitX, Vec3.UnitY);
        Vector3 back = Space.DirToGodot(right).Cross(Space.DirToGodot(up));
        for (int i = 0; i < shown.Count; i++)
        {
            ReferenceImage r = shown[i];
            if (Load(Resolve(r.Path, folder)) is not { } texture)
            {
                continue;
            }

            // Later images draw over earlier ones; all of them sit a little in front of the grid.
            Vector3 offset = -back * (cam.BackDistance - 0.02f * (i + 1));
            AddChild(Quad(r, texture, right, up, offset));
        }
    }

    /// <summary>An image path as stored: absolute, or relative to the project folder.</summary>
    public static string Resolve(string path, string? folder) =>
        Path.IsPathRooted(path) || folder is null ? path : Path.GetFullPath(Path.Combine(folder, path));

    private MeshInstance3D Quad(ReferenceImage r, ImageTexture texture, Vec3 right, Vec3 up, Vector3 offset)
    {
        double w = texture.GetWidth() * r.UnitsPerPixel / 2, h = texture.GetHeight() * r.UnitsPerPixel / 2;
        // The view plane through the origin: the depth component is the view's own (BackDistance) placement.
        int depthAxis = ViewAxes.DepthAxis(Kind);
        Vec3 centre = depthAxis < 0 ? r.Origin : r.Origin.With(depthAxis, 0);
        Vector3 Corner(double x, double y) => Space.ToGodot(centre + right * x + up * y) + offset;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        (Vector3 P, Vector2 Uv)[] corners =
        [
            (Corner(-w, h), new Vector2(0, 0)),
            (Corner(w, h), new Vector2(1, 0)),
            (Corner(w, -h), new Vector2(1, 1)),
            (Corner(-w, -h), new Vector2(0, 1)),
        ];
        foreach (int k in new[] { 0, 1, 2, 0, 2, 3 })
        {
            st.SetUV(corners[k].Uv);
            st.AddVertex(corners[k].P);
        }

        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = texture,
            AlbedoColor = new Color(1, 1, 1, (float)Math.Clamp(r.Opacity, 0, 1)),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        };
        return new MeshInstance3D
        {
            Mesh = st.Commit(),
            MaterialOverride = material,
            Layers = VisualLayers.Grid(Kind),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>The image as a texture (loaded again when the file changed), or null when it cannot be read. Shared
    /// by the views and the References panel, so each image is read once.</summary>
    public static ImageTexture? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        DateTime written = File.GetLastWriteTimeUtc(path);
        if (s_textures.TryGetValue(path, out var cached) && cached.Written == written)
        {
            return cached.Texture;
        }

        Image? image = Image.LoadFromFile(path);
        ImageTexture? texture = null;
        if (image is not null && !image.IsEmpty())
        {
            image.GenerateMipmaps();
            texture = ImageTexture.CreateFromImage(image);
        }

        s_textures[path] = (written, texture);
        return texture;
    }
}
