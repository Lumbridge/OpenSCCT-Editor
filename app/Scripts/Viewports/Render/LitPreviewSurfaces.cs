using Godot;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Assets;

namespace OpenScctEditor.Viewports.Render;

/// <summary>
/// One pass of the Lit Preview's bake as a mesh: lit surfaces drawn as the game draws lightmapped BSP (texture times
/// twice the lightmap, the lightmap on UV2, with the run-time world lights added over it by
/// <see cref="DynamicWorldLights"/>), unlit surfaces at their texture's colour. One surface per material and atlas,
/// cut to the storey shown (<see cref="StoreyClip"/>).
/// </summary>
public static class LitPreviewSurfaces
{
    /// <summary>The game's lightmap scale (stored bytes are half the light), as an albedo colour: applied before the
    /// sRGB decode, as the game applies it.</summary>
    private static readonly Color LightmapScale = new(2, 2, 2);

    /// <summary>The pass's mesh on <paramref name="layer"/>, or null when it has no surfaces.</summary>
    public static MeshInstance3D? Instance(LightPreviewPass pass, uint layer)
    {
        ImageTexture[] atlases = pass.Atlases
            .Select(a => ImageTexture.CreateFromImage(Image.CreateFromData(a.Width, a.Height, false, Image.Format.Rgba8, a.Rgba)))
            .ToArray();
        var directions = pass.DirectionPngs.Select(png => { using var image = new Image(); image.LoadPngFromBuffer(png); return ImageTexture.CreateFromImage(image); }).ToArray();
        var sections = new Dictionary<(ViewMaterial Material, int Atlas), Section>();
        foreach (LitPreviewPolygon p in pass.Lit)
        {
            Get(sections, ViewMaterials.Surface(p.Source.Material, p.Source.Flags), p.Atlas).Polygon(p.Source, p.Vertices, p.LightmapUvs);
        }

        foreach (PreviewPolygon p in pass.Unlit)
        {
            Get(sections, ViewMaterials.Surface(p.Material, p.Flags), -1).Polygon(p, p.Vertices, null);
        }

        var mesh = new ArrayMesh();
        var materials = new List<ShaderMaterial>();
        foreach (((ViewMaterial m, int atlas), Section s) in sections)
        {
            if (s.Points.Count == 0)
            {
                continue;
            }

            using var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = s.Points.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = s.Normals.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = s.Uvs.ToArray();
            if (atlas >= 0)
            {
                arrays[(int)Mesh.ArrayType.TexUV2] = s.Uv2s.ToArray();
            }

            MeshUpload.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays);
            ShaderMaterial material = atlas >= 0 ? Lightmapped(m, atlases[atlas]) : StoreyClip.Of(m.Unlit, m.TextureFile);
            if (atlas >= 0 && atlas < directions.Length && m.Unlit.HasMeta("authored_json")) { material.SetShaderParameter("am_bakedDirection", directions[atlas]); material.SetShaderParameter("am_hasBakedDirection", true); }
            materials.Add(material);
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

    private static Section Get(Dictionary<(ViewMaterial, int), Section> sections, ViewMaterial m, int atlas)
    {
        if (!sections.TryGetValue((m, atlas), out Section? s))
        {
            sections[(m, atlas)] = s = new Section(m);
        }

        return s;
    }

    /// <summary>The cut lightmapped material (<see cref="StoreyClip.Lightmapped"/>) with the run-time world lights'
    /// additive pass over it.</summary>
    private static ShaderMaterial Lightmapped(ViewMaterial m, Texture2D atlas)
    {
        ShaderMaterial mat = StoreyClip.Lightmapped(m.Unlit, m.TextureFile, atlas, LightmapScale);
        ShaderMaterial dynamic = DynamicWorldLights.Pass(atlas, m.Unlit);
        mat.NextPass = dynamic;
        if (m.TextureFile is { } file)
        {
            TextureCache.Request(file, tex => DynamicWorldLights.SetAlbedo(dynamic, tex));
        }

        return mat;
    }

    private sealed class Section(ViewMaterial m)
    {
        public List<Vector3> Points { get; } = new();

        public List<Vector3> Normals { get; } = new();

        public List<Vector2> Uvs { get; } = new();

        public List<Vector2> Uv2s { get; } = new();

        public void Polygon(PreviewPolygon source, IReadOnlyList<Vec3> verts, IReadOnlyList<(float U, float V)>? lightmapUvs)
        {
            Vector3 n = Space.DirToGodot(source.Normal);
            for (int i = 1; i + 1 < verts.Count; i++)
            {
                int ia = 0, ib = i, id = i + 1;
                Vector3 ga = Space.ToGodot(verts[ia]), gb = Space.ToGodot(verts[ib]), gd = Space.ToGodot(verts[id]);
                // Godot fronts are clockwise seen from the front (as in SurfaceBuilder).
                if ((gb - ga).Cross(gd - ga).Dot(n) > 0)
                {
                    (ib, id) = (id, ib);
                }

                foreach (int k in (ReadOnlySpan<int>)[ia, ib, id])
                {
                    Vec3 v = verts[k];
                    Points.Add(Space.ToGodot(v));
                    Normals.Add(n);
                    Uvs.Add(new Vector2(
                        (float)((Vec3.Dot(v - source.TextureBase, source.TextureU) + source.PanU) / m.USize),
                        (float)((Vec3.Dot(v - source.TextureBase, source.TextureV) + source.PanV) / m.VSize)));
                    if (lightmapUvs is not null)
                    {
                        Uv2s.Add(new Vector2(lightmapUvs[k].U, lightmapUvs[k].V));
                    }
                }
            }
        }
    }
}
