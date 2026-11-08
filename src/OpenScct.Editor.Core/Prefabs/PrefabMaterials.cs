using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile.Output;

namespace OpenScct.Editor.Core.Prefabs;

/// <summary>Small repeatable finishes for the starter kit; no converted map package is required.</summary>
public static class PrefabMaterials
{
    private static readonly Lazy<Dictionary<string, LibraryMaterial>> Library = new(Build);
    public static LibraryMaterial? Find(string name) => name.StartsWith("Editor.Kit.", StringComparison.OrdinalIgnoreCase) ? Library.Value.GetValueOrDefault(name) : null;
    private static Dictionary<string, LibraryMaterial> Build()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OpenSCCT-Editor", "prefab-materials-v1"); Directory.CreateDirectory(Path.Combine(dir, "textures"));
        var materials = new Dictionary<string, LibraryMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, r, g, b) in new (string, int, int, int)[]
        { ("Wood", 133, 94, 59), ("Metal", 93, 115, 129), ("Concrete", 147, 151, 149), ("Dark", 44, 55, 67), ("Fabric", 86, 119, 111), ("Screen", 57, 142, 165), ("Paint", 186, 168, 109), ("White", 193, 200, 198) })
        {
            var pixels = new byte[32*32*4];
            for (int y=0; y<32; y++) for (int x=0; x<32; x++)
            {
                int i = (y*32+x)*4; int noise = ((x*17+y*31)%9)-4;
                if (name == "Wood") noise += (y%8 == 0 ? -17 : (x+y/3)%11 == 0 ? 10 : 0);
                if (name == "Metal" && (x%16 == 0 || y%16 == 0)) noise -= 10;
                if (name == "Fabric" && (x+y)%2 == 0) noise -= 8;
                pixels[i] = (byte)Math.Clamp(r+noise,0,255); pixels[i+1] = (byte)Math.Clamp(g+noise,0,255); pixels[i+2] = (byte)Math.Clamp(b+noise,0,255); pixels[i+3] = 255;
            }
            string file = "textures/kit_" + name + ".png"; File.WriteAllBytes(Path.Combine(dir,file), Png.EncodeRgba(32,32,pixels));
            string asset = "Editor.Kit." + name; materials[asset] = new(dir, "EditorKit", asset, new JsonObject { ["texture"] = file }, null, 32, 32);
        }
        return materials;
    }
}
