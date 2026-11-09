using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

internal sealed partial class SdcPackagePlan
{
    // The export snapshot already carries resized and authored UV transforms.
    private (double U, double V) ProjectTextureScale(string? path) => (1, 1);
    private void PlanProjectTextures()
    {
        if (_o.ProjectFolder is not { } folder) return;
        var assets = ProjectAssets.For(folder);
        foreach (var face in _p.Brushes.SelectMany(b => b.Shape.Faces))
            if (face.DisplacementCollision == true && AuthoredMaterial.From(assets.FindMaterial(face.Material ?? "")?.Flags["authored"]) is { Displacement: not 0 })
                throw new SdcWriteRefusedException("A surface uses displaced collision. Disable that collision or substitute original-compatible geometry before exporting.");
        var references = RoundTripLint.AssetRefs(_p, folder).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, _) in assets.Materials)
        {
            if (!references.Contains(path) || assets.FindMaterial(path) is not { } material) continue;
            RgbaImage image = LegacyMaterialBake.Base(material);
            AuthoredMaterial? authored = AuthoredMaterial.From(material.Flags["authored"]);
            if (authored is not null) foreach (string difference in LegacyMaterialBake.Differences(authored)) _findings.Add(Finding.Warning("sdc.materialApproximation", path + ": " + difference + "."));
            bool masked = material.Flags["masked"]?.GetValue<bool>() == true, translucent = material.Flags["translucent"]?.GetValue<bool>() == true;
            AlphaKind alpha = translucent ? AlphaKind.Blend : masked ? AlphaKind.Mask : AlphaKind.None;
            bool needsShader = authored is { EmissionEnergy: > 0 } || authored?.Textures.ContainsKey("environment") == true;
            Item target = TextureItem(path, needsShader ? "Shader" : "Texture");
            if (!needsShader) WriteTexture(target, image, alpha, authored?.TwoSided == true);
            else
            {
                Item diffuse = TextureItem(path + "_Diffuse", "Texture"); WriteTexture(diffuse, image, alpha, authored!.TwoSided);
                Item? emission = null, emissionMask = null, environment = null, specularMask = null;
                if (authored.EmissionEnergy > 0)
                {
                    RgbaImage light = authored.Textures.TryGetValue("emission", out MaterialTexture? binding) ? RgbaImage.Decode(File.ReadAllBytes(TextureImporter.SafeFile(material.RenderDir, binding.File))) : new RgbaImage(1, 1, [255, 255, 255, 255]);
                    if (binding is not null) light = LegacyMaterialBake.FirstFrame(light, authored);
                    var mask = new RgbaImage(light.Width, light.Height);
                    for (int i = 0; i < light.Pixels.Length; i += 4)
                    {
                        for (int c = 0; c < 3; c++) light.Pixels[i + c] = (byte)Math.Clamp(light.Pixels[i + c] * authored.EmissionColor[c] * authored.EmissionEnergy, 0, 255);
                        byte strength = Math.Max(light.Pixels[i], Math.Max(light.Pixels[i + 1], light.Pixels[i + 2]));
                        mask.Pixels[i] = mask.Pixels[i + 1] = mask.Pixels[i + 2] = mask.Pixels[i + 3] = strength;
                    }
                    emission = TextureItem(path + "_Emission", "Texture"); WriteTexture(emission, light, AlphaKind.None, authored.TwoSided);
                    emissionMask = TextureItem(path + "_EmissionMask", "Texture"); WriteTexture(emissionMask, mask, AlphaKind.Blend, false);
                }
                if (authored.Textures.TryGetValue("environment", out MaterialTexture? env))
                {
                    Item envTexture = TextureItem(path + "_EnvironmentImage", "Texture");
                    WriteTexture(envTexture, RgbaImage.Decode(File.ReadAllBytes(TextureImporter.SafeFile(material.RenderDir, env.File))), AlphaKind.None, false);
                    environment = TextureItem(path + "_Environment", "TexEnvMap");
                    environment.Write = w => { TextureObject(w, "Material", envTexture.Ref); w.Name("None"); };
                    byte strength = (byte)Math.Clamp(Math.Round(255 * authored.Specular * (1 - authored.Roughness)), 0, 255);
                    specularMask = TextureItem(path + "_SpecularMask", "Texture"); WriteTexture(specularMask, new RgbaImage(1, 1, [strength, strength, strength, strength]), AlphaKind.Blend, false);
                }
                target.Write = w =>
                {
                    TextureFloat(w, "SpecificScaleCoeff", 1);
                    TextureObject(w, "Diffuse", diffuse.Ref);
                    if (translucent) TextureObject(w, "Opacity", diffuse.Ref);
                    if (emission is not null) TextureObject(w, "SelfIllumination", emission.Ref);
                    if (emissionMask is not null) TextureObject(w, "SelfIlluminationMask", emissionMask.Ref);
                    if (environment is not null) TextureObject(w, "Specular", environment.Ref);
                    if (specularMask is not null) TextureObject(w, "SpecularityMask", specularMask.Ref);
                    TextureBool(w, "TwoSided", authored.TwoSided); TextureByte(w, "OutputBlending", translucent ? (byte)3 : masked ? (byte)1 : (byte)0); w.Name("None");
                };
            }
            if (image.Width != DdsFile.PowerOfTwo(image.Width) || image.Height != DdsFile.PowerOfTwo(image.Height)) _findings.Add(Finding.Warning("sdc.textureResized", $"{path}: {image.Width}×{image.Height} resized to {DdsFile.PowerOfTwo(image.Width)}×{DdsFile.PowerOfTwo(image.Height)} for original-game storage."));
        }
    }
    private Item TextureItem(string path, string cls)
    {
        string[] names = path.Split('.').Skip(1).ToArray(); Item? outer = null;
        for (int i = 0; i < names.Length; i++)
        {
            string relative = string.Join('.', names.Take(i + 1)), itemClass = i == names.Length - 1 ? cls : "Package";
            if (!_local.TryGetValue(relative, out Item? item)) { item = new Item { Name = names[i], Origin = New, Flags = 0x000F0004, Movable = true }; _items.Add(item); _local[relative] = item; }
            else if (item.Src >= 0 && _src is not null)
            {
                string existingClass = _src.ClassName(_src.Exports[item.Src]);
                if (i == names.Length - 1 ? existingClass is not ("Texture" or "Shader" or "FinalBlend") : existingClass != "Package")
                    throw new SdcWriteRefusedException(path + ": incompatible existing export class; rename this material or its group.");
            }
            Item? capturedOuter = outer;
            item.ClassRef = () => _b.ImportClass(itemClass == "Package" ? "Core" : "Engine", itemClass); item.Outer = () => capturedOuter?.Ref ?? 0;
            item.Copied = null; item.Write = w => w.Name("None"); outer = item;
        }
        return outer!;
    }
    private static void WriteTexture(Item target, RgbaImage image, AlphaKind alpha, bool twoSided)
    {
        image = image.Resize(Math.Max(4, image.Width), Math.Max(4, image.Height));
        DdsTexture dds = DdsFile.Encode(image, alpha);
        target.Write = w =>
        {
            TextureByte(w, "Format", dds.Format == "DXT1" ? (byte)3 : (byte)8);
            TextureFloat(w, "SpecificScaleCoeff", 1);
            TextureInt(w, "USize", dds.Width); TextureInt(w, "VSize", dds.Height); TextureInt(w, "UClamp", dds.Width); TextureInt(w, "VClamp", dds.Height);
            TextureByte(w, "UBits", (byte)System.Numerics.BitOperations.Log2((uint)dds.Width)); TextureByte(w, "VBits", (byte)System.Numerics.BitOperations.Log2((uint)dds.Height));
            TextureBool(w, "bMasked", alpha == AlphaKind.Mask); TextureBool(w, "bAlphaTexture", alpha == AlphaKind.Blend); TextureBool(w, "bTwoSided", twoSided);
            w.Name("None"); w.Compact(dds.Mips);
            int offset = 128, width = dds.Width, height = dds.Height;
            for (int mip = 0; mip < dds.Mips; mip++)
            {
                int count = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * (dds.Format == "DXT1" ? 8 : 16);
                w.I32(0); w.Compact(count); w.Bytes(dds.Bytes.AsSpan(offset, count)); w.I32(width); w.I32(height);
                w.U8((byte)System.Numerics.BitOperations.Log2((uint)width)); w.U8((byte)System.Numerics.BitOperations.Log2((uint)height));
                offset += count; width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
        };
    }
    private static void TextureByte(UWriter w, string name, byte value) { w.Name(name); w.U8(1); w.U8(value); }
    private static void TextureFloat(UWriter w, string name, float value) { w.Name(name); w.U8(0x24); w.F32(value); }
    private static void TextureInt(UWriter w, string name, int value) { w.Name(name); w.U8(0x22); w.I32(value); }
    private static void TextureBool(UWriter w, string name, bool value) { w.Name(name); w.U8((byte)(3 | (value ? 0x80 : 0))); }
    private static void TextureObject(UWriter w, string name, int value) { w.Name(name); w.U8(0x55); w.U8((byte)UWriter.CompactSize(value)); w.ObjRef(value); }
}
