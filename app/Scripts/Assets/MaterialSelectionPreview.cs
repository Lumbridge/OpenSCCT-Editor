using Godot;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Assets;

/// <summary>A snapshot of the current selection in the material preview; never changes or paints the map.</summary>
internal static class MaterialSelectionPreview
{
    public static ArrayMesh? Build(out int triangles)
    {
        var ctx = EditorContext.Instance;
        var points = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
        void Add(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc)
        { points.AddRange([a, c, b]); normals.AddRange([normal, normal, normal]); uvs.AddRange([ua, uc, ub]); }
        var faces = GeometrySelection.Instance.Faces;
        var brushIds = faces.Count > 0 ? faces.Select(f => f.Brush).Distinct() : ctx.Selection.Items.Where(r => r.Kind is ObjectKind.Brush or ObjectKind.Stair).Select(r => r.Id);
        foreach (string id in brushIds) if (ctx.Project.FindBrush(id) is { } brush)
        {
            foreach (Poly poly in CsgBrush.Prepare(brush, 0).Faces)
            {
                if (faces.Count > 0 && !faces.Any(f => f.Brush == id && f.Face == poly.Surface.FaceIndex)) continue;
                SurfaceLook look = SurfaceLook.Resolve(AssetService.Instance.Resolver, poly.Surface.Material, poly.Surface.Flags);
                Vector2 Uv(Vec3 p) => new((float)((Vec3.Dot(p - poly.Surface.TextureBase, poly.Surface.TextureU) + poly.Surface.PanU) / look.USize), (float)((Vec3.Dot(p - poly.Surface.TextureBase, poly.Surface.TextureV) + poly.Surface.PanV) / look.VSize));
                for (int i = 1; i + 1 < poly.Verts.Length; i++) Add(Space.ToGodot(poly.Verts[0]), Space.ToGodot(poly.Verts[i]), Space.ToGodot(poly.Verts[i + 1]), Space.DirToGodot(poly.Plane.N), Uv(poly.Verts[0]), Uv(poly.Verts[i]), Uv(poly.Verts[i + 1]));
            }
        }
        foreach (var reference in ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Actor))
            if (ctx.Project.FindActor(reference.Id) is { } actor && StaticMeshDrawing.MeshPath(actor) is { } path && AssetService.Instance.Resolver.FindViewMesh(path) is { } library)
                foreach (var section in MeshSections.Read(GlbCache.Load(library.GlbPath), library.RenderDir, library.Mesh))
                {
                    var positions = section.Positions; var indices = section.Indices;
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        Vector3 P(int index) { int o = index * 3; return Space.ToGodot(StaticMeshDrawing.MeshToWorld(actor, new Vec3(positions[o] * 100, positions[o + 2] * 100, positions[o + 1] * 100))); }
                        Vector3 a = P(indices[i]), b = P(indices[i + 1]), c = P(indices[i + 2]);
                        Vector2 U(int index) => new(section.Uvs[index * 2], section.Uvs[index * 2 + 1]);
                        Add(a, c, b, (b - a).Cross(c - a).Normalized(), U(indices[i]), U(indices[i + 2]), U(indices[i + 1]));
                    }
                }
        triangles = points.Count / 3;
        if (points.Count == 0) return null;
        Vector3 min = points.Aggregate(Vector3.Inf, (a, b) => a.Min(b)), max = points.Aggregate(-Vector3.Inf, (a, b) => a.Max(b));
        Vector3 center = (min + max) / 2; float scale = 1.8f / Math.Max(.001f, (max - min).Length());
        Vector3 average = normals.Aggregate(Vector3.Zero, (a,b) => a + b) / normals.Count;
        Basis facing = average.LengthSquared() > .25f ? new Basis(new Quaternion(average.Normalized(), Vector3.Back)) : Basis.Identity;
        for (int i = 0; i < points.Count; i++) { points[i] = facing * (points[i] - center) * scale; normals[i] = facing * normals[i]; }
        var result = new ArrayMesh(); using var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points.ToArray(); arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray(); arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        MeshUpload.AddSurface(result, Mesh.PrimitiveType.Triangles, arrays); return result;
    }
}
