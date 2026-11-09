using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Compile.Output;

namespace OpenScct.Editor.Core.Assets;

/// <summary>One drawable section of a static mesh: triangles in glTF space (metres, Y up, counter-clockwise fronts)
/// with UVs, the material that draws them and the glb primitive it was read from (which keys its baked vertex light).</summary>
public sealed record MeshSection(string? Material, string? Texture, bool DoubleSided, string? AlphaMode, float[] Positions, float[] Normals,
    float[] Uvs, int[] Indices, int Primitive);

/// <summary>Reads a library or map-local static mesh for drawing (the viewport, thumbnails): its sections with the
/// absolute path of each section's base colour image.</summary>
public static class MeshSections
{
    public static List<MeshSection> Read(LibraryMesh mesh) => Read(mesh.GlbPath, mesh.RenderDir, mesh.Mesh);

    /// <summary>The sections of every glTF mesh a skeletal mesh's glb draws, in its reference pose (the stored
    /// vertices; joints and weights are not read). Primitives are numbered on across the meshes.</summary>
    public static List<MeshSection> Read(SkeletalMeshFile skeletal)
    {
        var sections = new List<MeshSection>();
        string dir = Path.GetDirectoryName(skeletal.GlbPath) ?? ".";
        foreach (int mesh in skeletal.Meshes)
        {
            int first = sections.Count;
            sections.AddRange(Read(skeletal.GlbPath, dir, mesh).Select(s => s with { Primitive = first + s.Primitive }));
        }

        return sections;
    }

    /// <summary>The sections of mesh <paramref name="mesh"/> of a glb whose image URIs are relative to
    /// <paramref name="renderDir"/> (a converted map's baked BSP, for one).</summary>
    public static List<MeshSection> Read(string glbPath, string renderDir, int mesh)
    {
        Glb glb = GlbCache.Load(glbPath);
        return Read(glb, renderDir, mesh);
    }

    public static List<MeshSection> Read(Glb glb, string renderDir, int mesh)
    {
        var sections = new List<MeshSection>();
        JsonArray primitives = glb.Array("meshes")[mesh]!["primitives"]!.AsArray();
        for (int pi = 0; pi < primitives.Count; pi++)
        {
            JsonNode? p = primitives[pi];
            if (p is null || (p["mode"]?.GetValue<int>() ?? 4) != 4 || p["attributes"]?["POSITION"] is not JsonValue pos)
            {
                continue;
            }

            float[] positions = glb.ReadFloats(pos.GetValue<int>(), 3);
            int count = positions.Length / 3;
            float[] normals = p["attributes"]!["NORMAL"] is JsonValue n ? glb.ReadFloats(n.GetValue<int>(), 3) : new float[positions.Length];
            float[] uvs = p["attributes"]!["TEXCOORD_0"] is JsonValue t && IsFloat(glb, t.GetValue<int>())
                ? glb.ReadFloats(t.GetValue<int>(), 2)
                : new float[count * 2];
            int[] indices = p["indices"] is JsonValue ia ? glb.ReadIndices(ia.GetValue<int>()) : Enumerable.Range(0, count).ToArray();
            JsonObject? material = p["material"] is JsonValue mv ? glb.Array("materials")[mv.GetValue<int>()] as JsonObject : null;
            sections.Add(new MeshSection(material?["name"]?.GetValue<string>(), Image(glb, material, renderDir),
                material?["doubleSided"]?.GetValue<bool>() == true, material?["alphaMode"]?.GetValue<string>(), positions, normals, uvs, indices, pi));
        }

        return sections;
    }

    private static bool IsFloat(Glb glb, int accessor) => glb.Array("accessors")[accessor]?["componentType"]?.GetValue<int>() == Glb.Float;

    /// <summary>The base colour image of a material, absolute (image URIs are relative to the render folder).</summary>
    private static string? Image(Glb glb, JsonObject? material, string renderDir)
    {
        if (material?["pbrMetallicRoughness"]?["baseColorTexture"]?["index"] is not JsonValue ti ||
            glb.Array("textures")[ti.GetValue<int>()]?["source"] is not JsonValue si ||
            glb.Array("images")[si.GetValue<int>()]?["uri"]?.GetValue<string>() is not { } uri)
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(renderDir, uri));
    }
}
