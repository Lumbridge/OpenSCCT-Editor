using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Compile.Output;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Windows;

/// <summary>Disposable, content-addressed assets. Projects store designs, never paths into this cache.</summary>
public static class WindowAssets
{
    private static readonly ConcurrentDictionary<string, LibraryMesh> Meshes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, LibraryMaterial> Materials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    public static LibraryMesh? FindMesh(string path) => Meshes.GetValueOrDefault(path);
    public static LibraryMaterial? FindMaterial(string path) => Materials.GetValueOrDefault(path);
    public static string Prepare(Actor actor, AssetResolver? assets = null)
    {
        if (actor.Window is not { } source) throw new ArgumentException("This actor is not an editable window");
        var d = source.Clone(); d.Binding = null; d.Wall = null; d.CutDepth = 0; d.FrameCollisionSources = []; d.Validate();
        var resolver = assets ?? AssetResolver.For(EditorServices.ContentRoot, null);
        string MaterialKey(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "Editor.WindowFrame") return "";
            return resolver.FindMaterial(name) is { Texture: { } relative } lib && File.Exists(Path.Combine(lib.RenderDir, relative))
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(lib.RenderDir, relative)))) : "";
        }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(d.ToJson() + MaterialKey(d.GlassMaterial) + MaterialKey(d.FrameMaterial))))[..24].ToLowerInvariant();
        string asset = "EditorWindow.W" + hash, material = asset + "_Glass";
        lock (Gate)
        {
            if (!Meshes.ContainsKey(asset))
            {
                string dir = Path.Combine(Path.GetTempPath(), "OpenSCCT-Editor", "windows-v3", hash);
                Directory.CreateDirectory(Path.Combine(dir, "textures"));
                var glb = Glb.Create("OpenSCCT Editor windows");
                string glassName = string.IsNullOrWhiteSpace(d.GlassMaterial) ? material : d.GlassMaterial;
                string image = "textures/glass_" + hash + ".png";
                var rgba = new byte[32 * 32 * 4];
                double opacity = d.Finish == "frosted" ? .68 : d.Finish == "tinted" ? .38 : .16;
                for (int i = 0; i < 32 * 32; i++)
                {
                    uint noise = unchecked((uint)(i + 1) * 747796405u + 2891336453u); noise = unchecked((noise ^ (noise >> 16)) * 2246822519u);
                    double grain = d.Finish == "frosted" ? .93 + (noise & 255) / 255.0 * .14 : 1;
                    for (int channel = 0; channel < 3; channel++) rgba[i * 4 + channel] = (byte)Math.Clamp(d.Tint[channel] * grain * 255, 0, 255);
                    rgba[i * 4 + 3] = (byte)(opacity * 255);
                }
                File.WriteAllBytes(Path.Combine(dir, image), Png.EncodeRgba(32, 32, rgba));
                File.WriteAllBytes(Path.Combine(dir, "textures/window-frame.png"), Png.EncodeRgba(1, 1, [105, 114, 125, 255]));
                string Texture(string name, string fallback)
                {
                    if (resolver.FindMaterial(name) is { Texture: { } relative } lib && File.Exists(Path.Combine(lib.RenderDir, relative)))
                    { string target = "textures/custom_" + Path.GetFileName(fallback); File.Copy(Path.Combine(lib.RenderDir, relative), Path.Combine(dir, target), true); return target; }
                    return fallback;
                }
                int gm = glb.AddMaterial(glassName, Texture(glassName, image), true, "BLEND");
                if (string.IsNullOrWhiteSpace(d.GlassMaterial))
                {
                    glb.Json["extensionsUsed"] = new JsonArray("KHR_materials_unlit");
                    glb.Array("materials")[gm]!["extensions"] = new JsonObject { ["KHR_materials_unlit"] = new JsonObject() };
                }
                int fm = glb.AddMaterial(d.FrameMaterial, Texture(d.FrameMaterial, "textures/window-frame.png"), false, null);
                Glb.MeshPart Part(BrushShape shape, int mat)
                {
                    var pos = new List<float>(); var norm = new List<float>(); var uv = new List<float>(); var indices = new List<uint>();
                    foreach (var face in shape.Faces)
                    {
                        Vec3 normal = face.Normal();
                        for (int i = 1; i + 1 < face.Vertices.Count; i++)
                        {
                            foreach (Vec3 v in new[] { face.Vertices[0], face.Vertices[i + 1], face.Vertices[i] })
                            {
                                indices.Add((uint)(pos.Count / 3));
                                pos.AddRange([(float)(v.X / 100), (float)(v.Z / 100), (float)(v.Y / 100)]);
                                norm.AddRange([(float)normal.X, (float)normal.Z, (float)normal.Y]);
                                uv.AddRange([(float)(v.Y / d.Width + .5), (float)(.5 - v.Z / d.Height)]);
                            }
                        }
                    }
                    return new(pos.ToArray(), norm.ToArray(), uv.ToArray(), null, null, indices.ToArray(), mat);
                }
                var pane = Part(WindowGeometry.Pane(d), gm);
                int paneIndex = glb.AddMesh(asset, [pane]);
                var display = new List<Glb.MeshPart> { pane };
                var previewActor = new Actor("Preview", "SBase.SVitre") { Window = d };
                foreach (var frame in WindowGeometry.Parts(previewActor)) display.Add(Part(frame.Shape, fm));
                int displayIndex = glb.AddMesh(asset + "_Preview", display);
                int frameIndex = display.Count > 1 ? glb.AddMesh(asset + "_Frame", display.Skip(1).ToList()) : -1;
                glb.AddSceneRoot(glb.AddNode(new JsonObject { ["name"] = asset, ["mesh"] = paneIndex }));
                string path = Path.Combine(dir, "window.glb");
                File.WriteAllBytes(path, glb.ToBytes(null));
                Materials[material] = new(dir, "EditorWindows", material, new JsonObject { ["texture"] = image, ["translucent"] = true, ["two_sided"] = true, ["unlit"] = true }, null, 32, 32);
                Materials.TryAdd("Editor.WindowFrame", new(dir, "EditorWindows", "Editor.WindowFrame", new JsonObject { ["texture"] = "textures/window-frame.png" }, null, 1, 1));
                Meshes[asset] = new(dir, "EditorWindows", path, asset, asset, paneIndex);
                Meshes[asset + "_Preview"] = new(dir, "EditorWindows", path, asset + "_Preview", asset + "_Preview", displayIndex);
                if (frameIndex >= 0) Meshes[asset + "_Frame"] = new(dir, "EditorWindows", path, asset + "_Frame", asset + "_Frame", frameIndex);
            }
        }
        actor.Props.Set("StaticMesh", PropValues.AssetRef(asset));
        return asset;
    }
    public static string Preview(Actor actor, AssetResolver? assets = null) => Prepare(actor, assets) + "_Preview";
}
