using System.Text;
using OpenScct.Editor.Core.Identity;

static string Key(string lighting, byte lightmap = 1, string classes = "{}") => MapShots.MapKey(
    new Dictionary<string, byte[]>
    {
        ["render/lighting.json"] = Encoding.UTF8.GetBytes(lighting),
        ["render/lightmaps/0.png"] = [lightmap],
        ["sim/classes.json"] = Encoding.UTF8.GetBytes(classes),
    });

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

const string cold = """{"bake":{"rays":6000000,"fast":false},"lights":[{"brightness":160}],"lightmaps":["lightmaps/0.png"]}""";
const string warm = """{"bake":{"rays":5000000,"fast":false},"lights":[{"brightness":160}],"lightmaps":["lightmaps/0.png"]}""";
const string legacy = """{ "lights": [{"brightness":160}], "lightmaps": ["lightmaps/0.png"] }""";
Check(Key(cold) == Key(warm), "a warm lightmap cache keeps a current picture current");
Check(Key(cold) == Key(legacy), "bake provenance and JSON whitespace do not change the picture");
Check(Key(cold) != Key(cold, 2), "changed lightmap pixels invalidate the picture");
Check(Key(cold) != Key(cold.Replace("160", "180")), "changed runtime lighting invalidates the picture");
Check(Key(cold) != Key(cold, classes: "{\"classMesh\":\"changed\"}"), "changed class visuals invalidate the picture");
