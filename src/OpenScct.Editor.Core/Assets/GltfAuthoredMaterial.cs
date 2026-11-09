using System.Text.Json.Nodes;
using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Identity;

namespace OpenScct.Editor.Core.Assets;

/// <summary>Standard glTF appearance alongside the complete versioned authored definition in extras.</summary>
public static class GltfAuthoredMaterial
{
    public static void Apply(Glb glb, int index, AuthoredMaterial m, Func<string, byte[]> read, Action<string, byte[]> write)
    {
        JsonObject target = glb.Array("materials")[index]!.AsObject();
        JsonObject pbr = (target["pbrMetallicRoughness"] ??= new JsonObject()).AsObject();
        pbr["baseColorFactor"] = new JsonArray(m.BaseColor.Select(v => (JsonNode?)JsonValue.Create(v)).Append(JsonValue.Create(m.Opacity)).ToArray());
        pbr["metallicFactor"] = m.Metallic; pbr["roughnessFactor"] = m.Roughness;
        target["doubleSided"] = m.TwoSided; target["alphaMode"] = m.Surface == "masked" ? "MASK" : m.Surface == "translucent" ? "BLEND" : "OPAQUE"; target["alphaCutoff"] = m.Cutoff;
        JsonObject? Texture(string slot) => m.Textures.TryGetValue(slot, out var binding) ? new JsonObject { ["index"] = glb.AddTexture(binding.File) } : null;
        if (Texture("baseColor") is { } color) pbr["baseColorTexture"] = color;
        if (Texture("normal") is { } normal)
        {
            if (m.NormalDirectX)
            {
                var image = RgbaImage.Decode(read(m.Textures["normal"].File));
                for (int at = 1; at < image.Pixels.Length; at += 4) image.Pixels[at] = (byte)(255 - image.Pixels[at]);
                string uri = Derived(image.EncodePng()); normal["index"] = glb.AddTexture(uri);
            }
            normal["scale"] = m.NormalStrength; target["normalTexture"] = normal;
        }
        if (m.Textures.ContainsKey("roughness") || m.Textures.ContainsKey("metallic") || m.Textures.ContainsKey("ao"))
        {
            var maps = new Dictionary<string, (MaterialImage Image, MaterialTexture Binding)>();
            foreach (string slot in new[] { "roughness", "metallic", "ao" }) if (m.Textures.TryGetValue(slot, out var binding)) maps[slot] = (new MaterialImage(read(binding.File)), binding);
            int width = 1, height = 1;
            foreach (string slot in maps.Keys) if (RgbaImage.SizeOf(read(m.Textures[slot].File)) is { } size) { width = Math.Max(width, size.Width); height = Math.Max(height, size.Height); }
            var orm = new RgbaImage(width, height);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                foreach ((string slot, int channel) in new[] { ("ao", 0), ("roughness", 1), ("metallic", 2) })
                    orm.Pixels[(y * width + x) * 4 + channel] = maps.TryGetValue(slot, out var map) ? (byte)Math.Clamp(Math.Round(map.Image.Sample((x + .5) / width, (y + .5) / height, map.Binding) * 255), 0, 255) : (byte)255;
                orm.Pixels[(y * width + x) * 4 + 3] = 255;
            }
            int texture = glb.AddTexture(Derived(orm.EncodePng()));
            pbr["metallicRoughnessTexture"] = new JsonObject { ["index"] = texture };
            if (maps.ContainsKey("ao")) target["occlusionTexture"] = new JsonObject { ["index"] = texture, ["strength"] = m.AoStrength };
        }
        target["emissiveTexture"] = Texture("emission");
        target["emissiveFactor"] = new JsonArray(m.EmissionColor.Select(c => (JsonNode?)JsonValue.Create(Math.Min(1, c * m.EmissionEnergy))).ToArray());
        var extensions = new JsonObject();
        if (m.Unlit) extensions["KHR_materials_unlit"] = new JsonObject();
        if (m.Clearcoat > 0) extensions["KHR_materials_clearcoat"] = new JsonObject { ["clearcoatFactor"] = m.Clearcoat, ["clearcoatRoughnessFactor"] = 1 - m.ClearcoatGloss };
        extensions["KHR_materials_specular"] = new JsonObject { ["specularFactor"] = Math.Min(1, m.Specular * 2), ["specularColorFactor"] = new JsonArray(m.SpecularColor.Select(c => (JsonNode?)JsonValue.Create(Math.Min(1, c))).ToArray()) };
        if (m.EmissionEnergy > 1) { target["emissiveFactor"] = new JsonArray(m.EmissionColor.Select(c => (JsonNode?)JsonValue.Create(Math.Min(1, c))).ToArray()); extensions["KHR_materials_emissive_strength"] = new JsonObject { ["emissiveStrength"] = m.EmissionEnergy }; }
        if (m.Anisotropy > 0) extensions["KHR_materials_anisotropy"] = new JsonObject { ["anisotropyStrength"] = m.Anisotropy };
        target["extensions"] = extensions;
        var used = (glb.Json["extensionsUsed"] ??= new JsonArray()).AsArray();
        foreach (string key in extensions.Select(p => p.Key)) if (!used.Any(n => n?.GetValue<string>() == key)) used.Add(key);
        var extras = (target["extras"] ??= new JsonObject()).AsObject(); extras["authored"] = m.ToJson();

        string Derived(byte[] bytes) { string file = "textures/authored_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16].ToLowerInvariant() + ".png"; write(file, bytes); return file; }
    }
}
