using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Compile.Output;

namespace OpenScct.Editor.Core.Assets;

public static class AuthoredMeshGeometry
{
    public static string SkinKey(Model.Actor? actor) => (actor?.Props["Skins"] is OpenScct.Content.Json.SvJsonArray skins ? string.Join('|', skins.Select(Model.PropValues.AssetPath)) : "") + (actor?.DisplacementCollision.Count > 0 ? "|collision:" + string.Join(',', actor.DisplacementCollision.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value)) : "");
    public static Glb WithSkins(Glb source, Model.Actor? actor, int mesh)
    {
        if (actor?.Props["Skins"] is not OpenScct.Content.Json.SvJsonArray skins || skins.All(s => Model.PropValues.AssetPath(s) is null)) return source;
        var result = Glb.Load(source.ToBytes(null));
        JsonArray primitives = result.Array("meshes")[mesh]!["primitives"]!.AsArray();
        for (int i = 0; i < primitives.Count && i < skins.Count; i++) if (Model.PropValues.AssetPath(skins[i]) is { } name)
        {
            JsonObject primitive = primitives[i]!.AsObject();
            JsonObject material = primitive["material"] is JsonValue index ? result.Array("materials")[index.GetValue<int>()]!.DeepClone().AsObject() : new JsonObject();
            material["name"] = name; result.Array("materials").Add(material); primitive["material"] = result.Count("materials") - 1;
        }
        return result;
    }
    private readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv, Vector2 Uv2, Vector4 Color)
    {
        public static Vector3 Unit(Vector3 n) => n.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(n);
        public static Vertex Mid(Vertex a, Vertex b) => new((a.Position + b.Position) / 2, Unit(a.Normal + b.Normal), (a.Uv + b.Uv) / 2, (a.Uv2 + b.Uv2) / 2, (a.Color + b.Color) / 2);
    }

    public static bool HasCollision(Glb source, int mesh, AssetResolver resolver, Model.Actor? actor = null) => source.ReadUePrimitives(mesh).Any(p =>
        AuthoredMaterial.From(resolver.FindMaterial(p.Material ?? "")?.Flags["authored"]) is { Displacement: not 0 } m && (actor?.DisplacementCollision.GetValueOrDefault(p.Index, m.DisplacementCollision) ?? m.DisplacementCollision));

    public static Glb Prepare(Glb source, AssetResolver resolver, bool collision, Model.Actor? actor = null)
    {
        bool any = source.Array("materials").Any(m => AuthoredMaterial.From(resolver.FindMaterial(m?["name"]?.GetValue<string>() ?? "")?.Flags["authored"]) is { Displacement: not 0 });
        if (!any) return source;
        Glb result = Glb.Load(source.ToBytes(null));
        for (int mesh = 0; mesh < source.Count("meshes"); mesh++)
        {
            foreach (MeshSection section in MeshSections.Read(source, "", mesh))
            {
                LibraryMaterial? library = resolver.FindMaterial(section.Material ?? "");
                if (AuthoredMaterial.From(library?.Flags["authored"]) is not { Displacement: not 0 } material || !material.Textures.TryGetValue("height", out MaterialTexture? binding) || library is null || (collision && !(actor?.DisplacementCollision.GetValueOrDefault(section.Primitive, material.DisplacementCollision) ?? material.DisplacementCollision))) continue;
                if (material.Problems().Count > 0) throw new InvalidDataException(string.Join("\n", material.Problems()));
                if ((long)section.Indices.Length / 3 * (1L << (material.Subdivision * 2)) > 262144) throw new InvalidDataException("Displaced mesh section exceeds 262144 triangles; reduce subdivision.");
                var image = new MaterialImage(File.ReadAllBytes(TextureImporter.SafeFile(library.RenderDir, binding.File)));
                JsonObject original = source.Array("meshes")[mesh]!["primitives"]![section.Primitive]!.AsObject();
                float[]? uv2 = original["attributes"]?["TEXCOORD_1"] is JsonValue uvIndex ? source.ReadFloats(uvIndex.GetValue<int>(), 2) : null;
                byte[]? colors = original["attributes"]?["COLOR_0"] is JsonValue colorIndex ? source.ReadColors(colorIndex.GetValue<int>()) : null;
                var positions = new List<float>(); var normals = new List<float>(); var uvs = new List<float>(); var uvs2 = new List<float>(); var outputColors = new List<float>();
                Vertex Read(int i) => new(new(section.Positions[i * 3], section.Positions[i * 3 + 1], section.Positions[i * 3 + 2]), Vertex.Unit(new(section.Normals[i * 3], section.Normals[i * 3 + 1], section.Normals[i * 3 + 2])), new(section.Uvs[i * 2], section.Uvs[i * 2 + 1]), uv2 is null ? Vector2.Zero : new(uv2[i * 2], uv2[i * 2 + 1]), colors is null ? Vector4.One : new Vector4(colors[i * 4], colors[i * 4 + 1], colors[i * 4 + 2], colors[i * 4 + 3]) / 255);
                var welded = new Dictionary<(Vector3 Position, Vector3 Normal, Vector2 Uv), int>(); var vertices = new List<Vertex>();
                int Weld(int index) { Vertex v = Read(index); var key = (v.Position, v.Normal, v.Uv); if (welded.TryGetValue(key, out int id)) return id; welded[key] = vertices.Count; vertices.Add(v); return vertices.Count - 1; }
                var edges = new Dictionary<(int A, int B), int>();
                for (int i = 0; i + 2 < section.Indices.Length; i += 3) for (int j = 0; j < 3; j++)
                { int a = Weld(section.Indices[i + j]), b = Weld(section.Indices[i + (j + 1) % 3]); var edge = a < b ? (a, b) : (b, a); edges[edge] = edges.GetValueOrDefault(edge) + 1; }
                var borders = edges.Where(e => e.Value == 1).Select(e => (A: vertices[e.Key.A].Position, B: vertices[e.Key.B].Position)).ToList();
                Vector3 Move(Vertex v)
                {
                    double x = v.Uv.X * material.TileU + material.OffsetU - .5, y = v.Uv.Y * material.TileV + material.OffsetV - .5;
                    double u = x * Math.Cos(material.Rotation) - y * Math.Sin(material.Rotation) + .5, w = x * Math.Sin(material.Rotation) + y * Math.Cos(material.Rotation) + .5;
                    float distance = float.MaxValue;
                    foreach (var edge in borders) { Vector3 delta = edge.B - edge.A; float t = Math.Clamp(Vector3.Dot(v.Position - edge.A, delta) / Math.Max(1e-12f, delta.LengthSquared()), 0, 1); distance = Math.Min(distance, Vector3.Distance(v.Position, edge.A + delta * t)); }
                    float fade = Math.Clamp(distance / Math.Max(.01f, Math.Abs(material.Displacement) / 100), 0, 1);
                    return v.Position + v.Normal * (float)((image.Sample(u, w, binding) - material.HeightMidpoint) * material.Displacement / 100 * fade);
                }
                void Triangle(Vertex a, Vertex b, Vertex c, int level)
                {
                    if (level > 0) { Vertex ab = Vertex.Mid(a, b), bc = Vertex.Mid(b, c), ca = Vertex.Mid(c, a); Triangle(a, ab, ca, level - 1); Triangle(ab, b, bc, level - 1); Triangle(ca, bc, c, level - 1); Triangle(ab, bc, ca, level - 1); return; }
                    Vector3 pa = Move(a), pb = Move(b), pc = Move(c), normal = Vertex.Unit(Vector3.Cross(pb - pa, pc - pa));
                    foreach (var pair in new[] { (pa, a), (pb, b), (pc, c) }) { positions.AddRange([pair.Item1.X, pair.Item1.Y, pair.Item1.Z]); normals.AddRange([normal.X, normal.Y, normal.Z]); uvs.AddRange([pair.Item2.Uv.X, pair.Item2.Uv.Y]); uvs2.AddRange([pair.Item2.Uv2.X, pair.Item2.Uv2.Y]); outputColors.AddRange([pair.Item2.Color.X, pair.Item2.Color.Y, pair.Item2.Color.Z, pair.Item2.Color.W]); }
                }
                for (int i = 0; i < section.Indices.Length; i += 3) Triangle(Read(section.Indices[i]), Read(section.Indices[i + 1]), Read(section.Indices[i + 2]), material.Subdivision);
                JsonObject primitive = result.Array("meshes")[mesh]!["primitives"]![section.Primitive]!.AsObject();
                var attributes = new JsonObject();
                float[] min = [float.MaxValue, float.MaxValue, float.MaxValue], max = [float.MinValue, float.MinValue, float.MinValue];
                for (int i = 0; i < positions.Count; i++) { min[i % 3] = Math.Min(min[i % 3], positions[i]); max[i % 3] = Math.Max(max[i % 3], positions[i]); }
                foreach (var part in new[] { ("POSITION", positions, 3), ("NORMAL", normals, 3), ("TEXCOORD_0", uvs, 2) })
                    attributes[part.Item1] = result.AddAccessor(MemoryMarshal.AsBytes(part.Item2.ToArray().AsSpan()), Glb.Float, positions.Count / 3, part.Item3 == 3 ? "VEC3" : "VEC2", Glb.ArrayBuffer, min: part.Item1 == "POSITION" ? min : null, max: part.Item1 == "POSITION" ? max : null);
                if (uv2 is not null) attributes["TEXCOORD_1"] = result.AddAccessor(MemoryMarshal.AsBytes(uvs2.ToArray().AsSpan()), Glb.Float, positions.Count / 3, "VEC2", Glb.ArrayBuffer);
                if (colors is not null) attributes["COLOR_0"] = result.AddAccessor(MemoryMarshal.AsBytes(outputColors.ToArray().AsSpan()), Glb.Float, positions.Count / 3, "VEC4", Glb.ArrayBuffer);
                uint[] indices = Enumerable.Range(0, positions.Count / 3).Select(i => (uint)i).ToArray();
                primitive["attributes"] = attributes;
                primitive["indices"] = result.AddAccessor(MemoryMarshal.AsBytes(indices.AsSpan()), Glb.UnsignedInt, indices.Length, "SCALAR", Glb.ElementArrayBuffer);
            }
        }
        return result;
    }
}
