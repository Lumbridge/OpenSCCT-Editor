using Godot;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports.Render;

/// <summary>
/// The Lightmap Density view of a finished Lit Preview bake: every lit surface shows its lightmap texels as a
/// checkerboard, tinted by texel size against the default face's (<see cref="LightPreviewPass.DefaultTexel"/>): green
/// at the default, shading to red at a quarter of its size (four times the density and sixteen times the texels) and
/// to blue at four times its size. An imported map's own lightmaps show the same way, in the layout the original
/// gave them. Like the Lit Preview, it is cut to the storey shown (<see cref="StoreyClip"/>).
/// </summary>
public static class LightmapDensity
{
    private static readonly Color Dense = new(0.9f, 0.2f, 0.15f), Default = new(0.25f, 0.8f, 0.3f), Coarse = new(0.2f, 0.4f, 0.95f);

    /// <summary>The pass's density mesh on <paramref name="layer"/>, or null when it has no lit surfaces.</summary>
    public static MeshInstance3D? Instance(LightPreviewPass pass, uint layer)
    {
        // Sections by atlas: the bake's own (false) and an imported map's (true).
        var sections = new Dictionary<(bool Source, int Atlas), Section>();
        Section SectionOf(bool source, int atlas, int width, int height)
        {
            if (!sections.TryGetValue((source, atlas), out Section? s))
            {
                sections[(source, atlas)] = s = new Section(width, height);
            }

            return s;
        }

        foreach (LitPreviewPolygon p in pass.Lit)
        {
            SectionOf(false, p.Atlas, pass.Atlases[p.Atlas].Width, pass.Atlases[p.Atlas].Height)
                .Polygon(p.Vertices, p.Source.Normal, p.LightmapUvs, Tint(p.TexelSize / pass.DefaultTexel));
        }

        foreach (SourceLightmapTriangle t in pass.SourceLightmaps)
        {
            SectionOf(true, t.Atlas, t.AtlasWidth, t.AtlasHeight).Polygon(t.Vertices, t.Normal, t.Uvs, Tint(t.TexelSize / pass.DefaultTexel));
        }

        var mesh = new ArrayMesh();
        var materials = new List<ShaderMaterial>();
        foreach (Section s in sections.Values)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = s.Points.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = s.Normals.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = s.Uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = s.Colors.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            materials.Add(StoreyClip.Fresh(Material(s.Width, s.Height)));
        }

        if (materials.Count == 0)
        {
            return null;
        }

        var mi = new MeshInstance3D { Mesh = mesh, Layers = layer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        for (int i = 0; i < materials.Count; i++)
        {
            mi.SetSurfaceOverrideMaterial(i, materials[i]);
        }

        return mi;
    }

    /// <summary>The tint of a texel <paramref name="ratio"/> times the default size, on a log scale over a factor of four
    /// each way.</summary>
    private static Color Tint(double ratio)
    {
        float t = (float)Math.Clamp(Math.Log(ratio, 4), -1, 1);
        return t < 0 ? Default.Lerp(Dense, -t) : Default.Lerp(Coarse, t);
    }

    /// <summary>Unshaded, the vertex tint times a one-texel checkerboard of the atlas's size.</summary>
    private static StandardMaterial3D Material(int width, int height)
    {
        var bytes = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bytes[(y * width) + x] = ((x + y) & 1) == 0 ? (byte)255 : (byte)170;
            }
        }

        return new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.L8, bytes)),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };
    }

    private sealed class Section(int width, int height)
    {
        public int Width => width;

        public int Height => height;

        public List<Vector3> Points { get; } = new();

        public List<Vector3> Normals { get; } = new();

        public List<Vector2> Uvs { get; } = new();

        public List<Color> Colors { get; } = new();

        public void Polygon(IReadOnlyList<Vec3> verts, Vec3 normal, IReadOnlyList<(float U, float V)> uvs, Color tint)
        {
            Vector3 n = Space.DirToGodot(normal);
            for (int i = 1; i + 1 < verts.Count; i++)
            {
                foreach (int k in (ReadOnlySpan<int>)[0, i, i + 1])
                {
                    Points.Add(Space.ToGodot(verts[k]));
                    Normals.Add(n);
                    Uvs.Add(new Vector2(uvs[k].U, uvs[k].V));
                    Colors.Add(tint);
                }
            }
        }
    }
}
