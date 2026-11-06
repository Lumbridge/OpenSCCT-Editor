using System.Text.Json;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Identity;

string temp = Path.Combine(Path.GetTempPath(), "ose-mesh-import-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    byte[] jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "quadrants.jpg"));
    CheckTexture("JpegObj", jpeg, "jpg", false, image =>
    {
        Check(image.Width == 32 && image.Height == 32, "external JPEG keeps its dimensions");
        int red = (8 * 32 + 8) * 4, green = (8 * 32 + 24) * 4, blue = (24 * 32 + 8) * 4;
        Check(image.Pixels[red] > 220 && image.Pixels[red + 1] < 20 &&
            image.Pixels[green + 1] > 220 && image.Pixels[blue + 2] > 220, "external JPEG retains distinct colour regions");
    });
    CheckTexture("JpegGltf", jpeg, "jpg", true, image =>
        Check(image.Width == 32 && image.Pixels.SequenceEqual(RgbaImage.Decode(jpeg).Pixels), "embedded glTF JPEG retains all decoded pixels"));

    var alpha = new RgbaImage(2, 1, [200, 20, 10, 0, 40, 80, 120, 127]);
    CheckTexture("PngAlpha", alpha.EncodePng(), "png", false, image =>
        Check(image.Pixels.SequenceEqual(alpha.Pixels), "PNG alpha and colour are unchanged"));

    File.WriteAllBytes(Path.Combine(temp, "broken.jpg"), [0xff, 0xd8, 0xff, 0xe0, 0, 1]);
    WriteObj("Broken", "broken.jpg");
    var broken = Import("Broken", "obj");
    Check(broken.Notes.Any(n => n.Contains("could not decode", StringComparison.OrdinalIgnoreCase)), "corrupt texture reports its decoding failure");
    Check(ReadTexture("Broken").Width == 8, "corrupt texture uses the fallback colour");
    WriteObj("Missing", "does-not-exist.jpg");
    Check(Import("Missing", "obj").Notes.Any(n => n.Contains("not found", StringComparison.OrdinalIgnoreCase)), "missing texture remains a clear import warning");
}
finally
{
    Directory.Delete(temp, recursive: true);
}

void CheckTexture(string name, byte[] data, string extension, bool embedded, Action<RgbaImage> verify)
{
    if (embedded)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
            foreach (ushort value in new ushort[] { 0, 1, 2 }) writer.Write(value);
        }
        var doc = new
        {
            asset = new { version = "2.0" }, scene = 0, scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0 } },
            buffers = new[] { new { byteLength = (int)buffer.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer.ToArray()) } },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 36 }, new { buffer = 0, byteOffset = 36, byteLength = 6 } },
            accessors = new[] { new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3" }, new { bufferView = 1, componentType = 5123, count = 3, type = "SCALAR" } },
            images = new[] { new { uri = "data:image/jpeg;base64," + Convert.ToBase64String(data) } },
            textures = new[] { new { source = 0 } },
            materials = new[] { new { name = "Paint", pbrMetallicRoughness = new { baseColorTexture = new { index = 0 } } } },
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 }, indices = 1, material = 0 } } } },
        };
        File.WriteAllText(Path.Combine(temp, name + ".gltf"), JsonSerializer.Serialize(doc));
    }
    else
    {
        File.WriteAllBytes(Path.Combine(temp, name + "." + extension), data);
        WriteObj(name, name + "." + extension);
    }
    var result = Import(name, embedded ? "gltf" : "obj");
    Check(result.Notes.Count == 0, name + " imports without fallback warnings: " + string.Join("; ", result.Notes));
    verify(ReadTexture(name));
}

MeshImportResult Import(string name, string extension) => MeshImporter.Import(Path.Combine(temp, "map.osmap"),
    Path.Combine(temp, name + "." + extension), new MeshImportOptions { Name = name, Collision = CollisionMode.Box });

RgbaImage ReadTexture(string name) => RgbaImage.Decode(File.ReadAllBytes(Path.Combine(temp, "map.osmap", "assets", "textures", "MyLevel_" + name + "_Paint.png")));

void WriteObj(string name, string texture)
{
    File.WriteAllText(Path.Combine(temp, name + ".obj"), $"mtllib {name}.mtl\nv 0 0 0\nv 100 0 0\nv 0 100 0\nusemtl Paint\nf 1 2 3\n");
    File.WriteAllText(Path.Combine(temp, name + ".mtl"), $"newmtl Paint\nKd 0.5 0.2 0.1\nmap_Kd {texture}\n");
}

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
