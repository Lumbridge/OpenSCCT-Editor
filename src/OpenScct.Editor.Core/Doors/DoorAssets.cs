using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Doors;

/// <summary>Generated assets are rebuilt from the saved design, including in a fresh editor process.</summary>
public static class DoorAssets
{
    private static readonly ConcurrentDictionary<string, LibraryMesh> Meshes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, LibraryMaterial> Materials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    public static LibraryMesh? FindMesh(string path) => Meshes.GetValueOrDefault(path);
    public static LibraryMaterial? FindMaterial(string path) => Materials.GetValueOrDefault(path);
    public static (byte R, byte G, byte B) Colour(string material) => material switch
    {
        "Editor.Door_plain" => (141, 158, 162), "Editor.Door_panelled" => (141, 96, 63),
        "Editor.Door_industrial" => (81, 115, 123), "Editor.Door_security" => (79, 88, 102),
        "Editor.Door_louvred" => (112, 133, 116), "Editor.Door_glazed" => (172, 184, 186),
        "Editor.DoorInset" => (112, 75, 49), "Editor.DoorMetal" => (159, 170, 177),
        "Editor.DoorGlass" => (92, 163, 188), _ => (69, 78, 88),
    };
    public static string Prepare(Actor a, AssetResolver? assets = null)
    {
        var d = a.Door?.Clone() ?? throw new ArgumentException("This actor is not an editable door");
        d.Validate(); d.Wall = null; d.CutDepth = 0;
        var resolver = assets ?? AssetResolver.For(EditorServices.ContentRoot, null);
        string TextureHash(string name) => resolver.FindMaterial(name) is { Texture: { } texture } lib && File.Exists(Path.Combine(lib.RenderDir, texture))
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(lib.RenderDir, texture)))) : "";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(d.ToJson() + TextureHash(d.Material) + TextureHash(d.FrameMaterial))))[..24].ToLowerInvariant();
        string asset = "EditorDoor.D" + hash;
        lock (Gate)
        {
            if (!Meshes.ContainsKey(asset))
            {
                string dir = Path.Combine(Path.GetTempPath(), "OpenSCCT-Editor", "doors-v1", hash);
                Directory.CreateDirectory(Path.Combine(dir, "textures"));
                var glb = Glb.Create("OpenSCCT Editor doors"); var mats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int Material(string name)
                {
                    if (mats.TryGetValue(name, out int known)) return known;
                    string image = "textures/finish" + mats.Count + ".png";
                    var colour = Colour(name); byte alpha = name == "Editor.DoorGlass" ? (byte)130 : (byte)255;
                    string path = Path.Combine(dir, image);
                    LibraryMaterial? custom = name.StartsWith("Editor.", StringComparison.Ordinal) ? null : resolver.FindMaterial(name);
                    if (custom is { Texture: { } texture } && File.Exists(Path.Combine(custom.RenderDir, texture))) File.Copy(Path.Combine(custom.RenderDir, texture), path, true);
                    else File.WriteAllBytes(path, Png.EncodeRgba(1, 1, [colour.R, colour.G, colour.B, alpha]));
                    int index = glb.AddMaterial(name, image, false, alpha < 255 ? "BLEND" : null); mats[name] = index;
                    Materials[name] = new(dir, "EditorDoors", name, new JsonObject { ["texture"] = image, ["translucent"] = alpha < 255 }, null, 1, 1);
                    return index;
                }
                // The frame material also belongs to generated world brushes.
                Material(d.FrameMaterial);
                var parts = new List<Glb.MeshPart>();
                foreach (var part in DoorGeometry.Leaf(d))
                {
                    var pos = new List<float>(); var normals = new List<float>(); var uv = new List<float>(); var indices = new List<uint>();
                    foreach (var face in part.Shape.Faces)
                    {
                        var normal = face.Normal();
                        for (int i = 1; i + 1 < face.Vertices.Count; i++) foreach (var v in new[] { face.Vertices[0], face.Vertices[i + 1], face.Vertices[i] })
                        {
                            indices.Add((uint)(pos.Count / 3)); pos.AddRange([(float)(v.X / 100), (float)(v.Z / 100), (float)(v.Y / 100)]);
                            normals.AddRange([(float)normal.X, (float)normal.Z, (float)normal.Y]); uv.AddRange([(float)(v.Y / d.Width + .5), (float)(.5 - v.Z / d.Height)]);
                        }
                    }
                    parts.Add(new(pos.ToArray(), normals.ToArray(), uv.ToArray(), null, null, indices.ToArray(), Material(part.Material)));
                }
                int mesh = glb.AddMesh(asset, parts); glb.AddSceneRoot(glb.AddNode(new JsonObject { ["name"] = asset, ["mesh"] = mesh }));
                string file = Path.Combine(dir, "door.glb"); File.WriteAllBytes(file, glb.ToBytes(null));
                Meshes[asset] = new(dir, "EditorDoors", file, asset, asset, mesh);
            }
        }
        a.Props.Set("StaticMesh", PropValues.AssetRef(asset)); return asset;
    }
}
