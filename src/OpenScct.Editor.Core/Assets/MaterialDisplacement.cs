using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>Deterministic CPU tessellation for rendering and collision. Patch borders stay pinned so independently
/// tessellated material boundaries remain watertight. The editable source is never changed.</summary>
public sealed class MaterialDisplacement(AssetResolver resolver)
{
    private readonly Dictionary<string, (AuthoredMaterial? Material, LibraryMaterial? Library, MaterialImage? Height)> _maps = new(StringComparer.OrdinalIgnoreCase);
    private (AuthoredMaterial? Material, LibraryMaterial? Library, MaterialImage? Height) Map(string? path)
    {
        if (path is null) return default;
        if (_maps.TryGetValue(path, out var cached)) return cached;
        LibraryMaterial? library = resolver.FindMaterial(path);
        AuthoredMaterial? definition = AuthoredMaterial.From(library?.Flags["authored"]);
        MaterialImage? height = definition?.Textures.TryGetValue("height", out MaterialTexture? binding) == true && library is not null
            ? new(File.ReadAllBytes(TextureImporter.SafeFile(library.RenderDir, binding.File))) : null;
        return _maps[path] = (definition, library, height);
    }

    public IEnumerable<Poly> Expand(Poly polygon)
    {
        var map = Map(polygon.Surface.Material);
        if (map.Material is not { Displacement: not 0 } material || map.Height is null || map.Library is null || polygon.Surface.Displaced)
        { yield return polygon; yield break; }
        var errors = material.Problems(); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        MaterialTexture binding = material.Textures["height"];
        int level = material.Subdivision;
        if ((long)Math.Max(1, polygon.Verts.Length - 2) * (1L << (2 * level)) > 262144) throw new InvalidDataException("Displacement exceeds 262144 triangles on one patch. Reduce subdivision.");
        Vec3 Point(Vec3 point)
        {
            double u = (Vec3.Dot(point - polygon.Surface.TextureBase, polygon.Surface.TextureU) + polygon.Surface.PanU) / Math.Max(1, map.Library.USize);
            double v = (Vec3.Dot(point - polygon.Surface.TextureBase, polygon.Surface.TextureV) + polygon.Surface.PanV) / Math.Max(1, map.Library.VSize);
            double x = u * material.TileU + material.OffsetU - .5, y = v * material.TileV + material.OffsetV - .5;
            u = x * Math.Cos(material.Rotation) - y * Math.Sin(material.Rotation) + .5;
            v = x * Math.Sin(material.Rotation) + y * Math.Cos(material.Rotation) + .5;
            double border = double.MaxValue;
            for (int i = 0; i < polygon.Verts.Length; i++)
            {
                Vec3 a = polygon.Verts[i], edge = polygon.Verts[(i + 1) % polygon.Verts.Length] - a;
                double t = Math.Clamp(Vec3.Dot(point - a, edge) / Math.Max(1e-12, edge.LengthSquared), 0, 1);
                border = Math.Min(border, (point - a - edge * t).Length);
            }
            double fade = Math.Clamp(border / Math.Max(1, Math.Abs(material.Displacement)), 0, 1);
            double displacement = (map.Height.Sample(u, v, binding) - material.HeightMidpoint) * material.Displacement * fade;
            return point + polygon.Plane.N * displacement;
        }
        IEnumerable<Poly> Triangle(Vec3 a, Vec3 b, Vec3 c, int remaining)
        {
            if (remaining > 0)
            {
                Vec3 ab = (a + b) / 2, bc = (b + c) / 2, ca = (c + a) / 2;
                foreach (Poly p in Triangle(a, ab, ca, remaining - 1).Concat(Triangle(ab, b, bc, remaining - 1)).Concat(Triangle(ca, bc, c, remaining - 1)).Concat(Triangle(ab, bc, ca, remaining - 1))) yield return p;
                yield break;
            }
            Vec3 pa = Point(a), pb = Point(b), pc = Point(c), normal = Vec3.Cross(pb - pa, pc - pa).Normalized();
            yield return new Poly([pa, pb, pc], new Plane3(normal, Vec3.Dot(normal, pa)), polygon.Surface);
        }
        for (int i = 1; i + 1 < polygon.Verts.Length; i++) foreach (Poly p in Triangle(polygon.Verts[0], polygon.Verts[i], polygon.Verts[i + 1], level)) yield return p;
    }

    public MapProject CollisionProject(MapProject source)
    {
        MapProject? result = null;
        for (int b = 0; b < source.Brushes.Count; b++)
        {
            Brush brush = source.Brushes[b];
            if (!brush.Shape.Faces.Any(f => Map(f.Material).Material is { Displacement: not 0 } m && (f.DisplacementCollision ?? m.DisplacementCollision))) continue;
            result ??= Session.ProjectSnapshot.Take(source);
            var faces = new List<BrushFace>();
            var transform = Geometry.ShapeTransform.For(brush);
            foreach (BrushFace face in brush.Shape.Faces)
            {
                if (Map(face.Material).Material is not { Displacement: not 0 } m || !(face.DisplacementCollision ?? m.DisplacementCollision) || face.Vertices.Count < 3) { faces.Add(face.Clone()); continue; }
                Vec3[] vertices = transform.FaceToWorld(face).ToArray();
                Vec3 normal = Vec3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]).Normalized();
                if (brush.Oper == CsgOper.Subtract) normal = -normal;
                var surface = new Surface { BrushId = brush.Id, FaceIndex = faces.Count, Material = face.Material, TextureBase = transform.ToWorld(face.TextureBase), TextureU = transform.TextureAxisToWorld(face.TextureU), TextureV = transform.TextureAxisToWorld(face.TextureV), PanU = face.PanU, PanV = face.PanV };
                foreach (Poly triangle in Expand(new Poly(vertices, new Plane3(normal, Vec3.Dot(normal, vertices[0])), surface)))
                { BrushFace generated = face.Clone(); generated.Vertices = triangle.Verts.Select(transform.ToLocal).ToList(); if (transform.Mirrors) generated.Vertices.Reverse(); generated.GeneratedDisplacement = true; faces.Add(generated); }
            }
            result.Brushes[b].Shape.Faces = faces;
        }
        return result ?? source;
    }

    public IEnumerable<Services.PreviewPolygon> Preview(IEnumerable<Services.PreviewPolygon> polygons)
    {
        foreach (var p in polygons)
        {
            var surface = new Surface { BrushId = p.BrushId, FaceIndex = p.FaceIndex, Material = p.Material, Flags = p.Flags,
                TextureBase = p.TextureBase, TextureU = p.TextureU, TextureV = p.TextureV, PanU = p.PanU, PanV = p.PanV, LightMapScale = p.LightMapScale };
            if (p.Vertices.Count < 3) { yield return p; continue; }
            foreach (Poly triangle in Expand(new Poly(p.Vertices.ToArray(), new Plane3(p.Normal, Vec3.Dot(p.Normal, p.Vertices[0])), surface)))
                yield return WorldPreview.ToPreview(triangle);
        }
    }
}
