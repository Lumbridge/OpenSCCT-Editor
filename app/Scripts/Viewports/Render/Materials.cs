using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile.Output;
using OpenScctEditor.Assets;

namespace OpenScctEditor.Viewports.Render;

/// <summary>The unlit and lit Godot materials of one surface material, its texture size for UV maths and the image
/// file it draws (null for the grid).</summary>
public sealed record ViewMaterial(StandardMaterial3D Unlit, StandardMaterial3D Lit, double USize, double VSize, string? TextureFile)
{
    public StandardMaterial3D For(bool lit) => lit ? Lit : Unlit;
}

/// <summary>
/// The textured materials of the solid views. A brush face's material resolves as the compiler resolves it (the
/// project's own assets, then the converted maps); its texture loads in the background while the compiler's grid
/// stands in, and a material nothing ships keeps the grid. One pair per material and poly-flag mix, shared by every
/// surface that uses it.
/// </summary>
public static class ViewMaterials
{
    private static readonly Dictionary<string, ViewMaterial> s_surfaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ViewMaterial> s_meshes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<ViewMaterial, StandardMaterial3D> s_vertexLit = new();

    /// <summary>The materials of a brush surface, resolved by the compiler's rules (<see cref="SurfaceLook"/>).</summary>
    public static ViewMaterial Surface(string? material, int polyFlags)
    {
        string key = SurfaceLook.KeyOf(material, polyFlags);
        if (!s_surfaces.TryGetValue(key, out ViewMaterial? m))
        {
            SurfaceLook look = SurfaceLook.Resolve(AssetService.Instance.Resolver, material, polyFlags);
            // An unlit material ignores lights in the lit view too.
            m = Make(look.Name, look.TextureFile, look.AlphaMode, look.TwoSided, look.Unlit, look.USize, look.VSize);
            s_surfaces[key] = m;
        }

        return m;
    }

    /// <summary>The materials of a static mesh section (its glb material: image, alpha mode, sides).</summary>
    public static ViewMaterial Section(MeshSection s)
    {
        string key = $"{s.Texture ?? s.Material ?? ""}|{s.AlphaMode}|{s.DoubleSided}";
        if (!s_meshes.TryGetValue(key, out ViewMaterial? m))
        {
            bool windowUnlit = OpenScct.Editor.Core.Windows.WindowAssets.FindMaterial(s.Material ?? "")?.Flags["unlit"]?.GetValue<bool>() == true;
            m = Make(s.Material ?? "mesh", s.Texture, s.AlphaMode, s.DoubleSided, unlitOnly: windowUnlit, 1, 1);
            s_meshes[key] = m;
        }

        return m;
    }

    /// <summary>The unlit material of <paramref name="m"/> times its mesh's vertex colours, as the game draws a static
    /// mesh with baked vertex light. The albedo colour of 2 is the game's 2x stage (colours hold half the light),
    /// applied before the sRGB decode as the game applies it.</summary>
    public static StandardMaterial3D VertexLit(ViewMaterial m)
    {
        if (!s_vertexLit.TryGetValue(m, out StandardMaterial3D? mat))
        {
            mat = (StandardMaterial3D)m.Unlit.Duplicate();
            mat.AlbedoColor = new Color(2, 2, 2);
            mat.VertexColorUseAsAlbedo = true;
            mat.VertexColorIsSrgb = true;
            if (m.TextureFile is { } file)
            {
                TextureCache.Request(file, tex => mat.AlbedoTexture = tex);
            }

            s_vertexLit[m] = mat;
        }

        return mat;
    }

    /// <summary>Drops every material (after the project's assets changed), so the next draw resolves again.</summary>
    public static void Clear()
    {
        s_surfaces.Clear();
        s_meshes.Clear();
        s_vertexLit.Clear();
        StoreyClip.Clear();
    }

    private static ViewMaterial Make(string name, string? texture, string? alpha, bool twoSided, bool unlitOnly, double uSize, double vSize)
    {
        StandardMaterial3D unlit = Base(BaseMaterial3D.ShadingModeEnum.Unshaded, alpha, twoSided);
        StandardMaterial3D lit = unlitOnly ? unlit : Base(BaseMaterial3D.ShadingModeEnum.PerPixel, alpha, twoSided);
        Texture2D grid = TextureCache.Grid(name);
        unlit.AlbedoTexture = lit.AlbedoTexture = grid;
        if (texture is not null)
        {
            TextureCache.Request(texture, tex =>
            {
                unlit.AlbedoTexture = tex;
                lit.AlbedoTexture = tex;
            });
        }

        return new ViewMaterial(unlit, lit, uSize, vSize, texture);
    }

    private static StandardMaterial3D Base(BaseMaterial3D.ShadingModeEnum shading, string? alpha, bool twoSided) => new()
    {
        ShadingMode = shading,
        CullMode = twoSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        Transparency = alpha switch
        {
            "MASK" => BaseMaterial3D.TransparencyEnum.AlphaScissor,
            "BLEND" => BaseMaterial3D.TransparencyEnum.Alpha,
            _ => BaseMaterial3D.TransparencyEnum.Disabled,
        },
        AlphaScissorThreshold = 0.5f,
        Roughness = 1,
        MetallicSpecular = 0.1f,
    };
}
