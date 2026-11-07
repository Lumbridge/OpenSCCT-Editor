using Godot;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Viewports.Render;

/// <summary>
/// The CSG preview as textured surfaces: one mesh surface per material, with UVs worked out as the compiler works
/// them out (u = (dot(v - base, U) + panU) / USize), so the solid views show what a build will look like.
/// </summary>
public sealed class TexturedSurfaces
{
    private readonly Dictionary<ViewMaterial, Section> _sections = new();

    /// <summary>Adds visible world surfaces. Zone portals are editing helpers, not opaque world faces.</summary>
    public void Add(IEnumerable<PreviewPolygon> polys)
    {
        foreach (PreviewPolygon p in polys)
        {
            if (p.Vertices.Count < 3 || (p.Flags & (PolyFlags.Invisible | PolyFlags.Portal)) != 0)
            {
                continue;
            }

            ViewMaterial m = ViewMaterials.Surface(p.Material, p.Flags);
            if (!_sections.TryGetValue(m, out Section? s))
            {
                s = new Section();
                _sections[m] = s;
            }

            s.Polygon(p, m);
        }
    }

    /// <summary>The mesh (one surface per material) and the materials in surface order, or null when empty.</summary>
    public (ArrayMesh Mesh, List<ViewMaterial> Materials)? Build()
    {
        var mesh = new ArrayMesh();
        var materials = new List<ViewMaterial>();
        foreach ((ViewMaterial m, Section s) in _sections)
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
            MeshUpload.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays);
            materials.Add(m);
        }

        return materials.Count == 0 ? null : (mesh, materials);
    }

    /// <summary>One instance of the mesh on <paramref name="layer"/>, each surface with the <see cref="StoreyClip"/>
    /// copy of its unlit or lit material (the world, cut to the storey shown).</summary>
    public static MeshInstance3D Instance(ArrayMesh mesh, IReadOnlyList<ViewMaterial> materials, bool lit, uint layer)
    {
        var mi = new MeshInstance3D { Mesh = mesh, Layers = layer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        for (int i = 0; i < materials.Count; i++)
        {
            mi.SetSurfaceOverrideMaterial(i, StoreyClip.Of(materials[i].For(lit), materials[i].TextureFile));
        }

        return mi;
    }

    private sealed class Section
    {
        public List<Vector3> Points { get; } = new();

        public List<Vector3> Normals { get; } = new();

        public List<Vector2> Uvs { get; } = new();

        public void Polygon(PreviewPolygon p, ViewMaterial m)
        {
            Vector3 n = Space.DirToGodot(p.Normal);
            for (int i = 1; i + 1 < p.Vertices.Count; i++)
            {
                Vec3 a = p.Vertices[0], b = p.Vertices[i], d = p.Vertices[i + 1];
                Vector3 ga = Space.ToGodot(a), gb = Space.ToGodot(b), gd = Space.ToGodot(d);
                // Godot fronts are clockwise seen from the front (as in SurfaceBuilder).
                if ((gb - ga).Cross(gd - ga).Dot(n) > 0)
                {
                    (b, d) = (d, b);
                    (gb, gd) = (gd, gb);
                }

                Add(ga, n, Uv(p, a, m));
                Add(gb, n, Uv(p, b, m));
                Add(gd, n, Uv(p, d, m));
            }
        }

        private void Add(Vector3 point, Vector3 normal, Vector2 uv)
        {
            Points.Add(point);
            Normals.Add(normal);
            Uvs.Add(uv);
        }

        private static Vector2 Uv(PreviewPolygon p, Vec3 v, ViewMaterial m) => new(
            (float)((Vec3.Dot(v - p.TextureBase, p.TextureU) + p.PanU) / m.USize),
            (float)((Vec3.Dot(v - p.TextureBase, p.TextureV) + p.PanV) / m.VSize));
    }
}
