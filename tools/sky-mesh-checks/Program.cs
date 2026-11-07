using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

string root = Path.Combine(Path.GetTempPath(), "ose-sky-mesh-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int failures = 0;
try
{
    string folder = Path.Combine(root, "fixture.osmap");
    string obj = Path.Combine(root, "triangle.obj");
    File.WriteAllText(obj, "v 0 0 0\nv 32 0 0\nv 0 32 0\nf 1 2 3\n");
    MeshImporter.Import(folder, obj, new MeshImportOptions { Name = "Triangle", ZUp = true });
    var project = MapProject.CreateDefault("SkyMeshCheck", "Sky mesh checks");
    var session = new EditorSession(project);
    var preview = new SkyPreview();
    Check(preview.Build(project, folder) is null, "maps without a sky retain their ordinary editor background");
    Apply("""
        {"ops":[
          {"op":"brush","id":"Room","brush":{"size":[512,512,512]},"csg":"subtract","at":[0,0,0]},
          {"op":"brush","id":"SkyRoom","brush":{"size":[512,512,512]},"csg":"subtract","at":[0,0,2048]},
          {"op":"create","id":"SkyCamera","class":"Engine.SkyZoneInfo","at":[0,0,2048]},
          {"op":"create","id":"SkyMesh","class":"Engine.StaticMeshActor","at":[0,0,2048],"props":{"StaticMesh":{"$asset":"MyLevel.Meshes.Triangle"},"DrawType":8,"bUnlit":true}},
          {"op":"create","id":"WorldMesh","class":"Engine.StaticMeshActor","at":[0,0,0],"props":{"StaticMesh":{"$asset":"MyLevel.Meshes.Triangle"},"DrawType":8,"bUnlit":false}}
        ]}
        """);
    SkyPreviewScene zonePreview = preview.Build(project, folder)!;
    IReadOnlyList<PreviewPolygon> polygons = new WorldPreview().Build(project);
    Check(polygons.Where(p => p.BrushId == "SkyRoom").All(zonePreview.ContainsPolygon) &&
        !polygons.Where(p => p.BrushId == "Room").Any(zonePreview.ContainsPolygon),
        "preview classifies sky boundaries from their empty side and leaves playable surfaces alone");
    string first = Compile("first");
    JsonObject sky = Instance(first, "SkyMesh"), world = Instance(first, "WorldMesh");
    Check(Flag(sky, "skyZone"), "new mesh in a sky zone exports skyZone=true");
    Check(Flag(sky, "unlit"), "unlit sky mesh stays unlit in the game");
    Check(!Flag(world, "skyZone") && !Flag(world, "unlit"), "ordinary lit mesh stays in the main world");

    // A converted map keeps its mesh library and follows each actor's edited transform and flags.
    project.Brushes.Clear();
    project.Baked = new BakedWorld { SourceMap = first };
    Apply("""
        {"ops":[
          {"op":"update","id":"SkyMesh","at":[0,0,0],"props":{"bUnlit":false}},
          {"op":"update","id":"WorldMesh","at":[0,0,2048],"props":{"bUnlit":true}}
        ]}
        """);
    string followed = Compile("followed");
    sky = Instance(followed, "SkyMesh");
    world = Instance(followed, "WorldMesh");
    Check(!Flag(sky, "skyZone") && !Flag(sky, "unlit"), "moving a kept sky mesh into the world clears both flags");
    Check(Flag(world, "skyZone") && Flag(world, "unlit"), "moving a kept mesh into the sky follows its current flags");
    project.Actors.RemoveAll(a => a.Id == "SkyCamera");
    Check(preview.Build(project, folder) is null, "removing the sky camera clears a cached preview");

    void Apply(string json)
    {
        ChangeResult result = ChangeApplier.Apply(session, ApiJson.Parse(json));
        if (!result.Ok) throw new Exception("Fixture change failed: " + result.Json);
    }

    string Compile(string name)
    {
        string output = Path.Combine(root, name, project.Id);
        CompileResult result = new MapCompiler().Compile(project, output, new CompileOptions
        {
            ProjectFolder = folder, FastLighting = true,
        });
        if (!result.Ok) throw new Exception(string.Join("; ", result.Findings.Select(f => f.Message)));
        SkyPreviewScene? sky = preview.Build(project, folder);
        Check(sky is not null && sky.Location == new Vec3(0, 0, 2048) && sky.Contains(new Vec3(0, 0, 2048)) &&
            !sky.Contains(Vec3.Zero), name + ": editor preview uses the build's sky camera and zones");
        Check(result.Stats.GetValueOrDefault("strategicmap.floors") == 1,
            name + ": the sky room above the playable room adds no strategic-map floor");
        return output;
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}
return failures == 0 ? 0 : 1;

void Check(bool ok, string description)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + description);
    if (!ok) failures++;
}

static bool Flag(JsonObject obj, string key) => obj[key]?.GetValue<bool>() ?? false;

static JsonObject Instance(string folder, string actor)
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(folder, "render", "world.glb"));
    int length = BitConverter.ToInt32(bytes, 12);
    JsonNode doc = JsonNode.Parse(bytes.AsSpan(20, length))!;
    return doc["extras"]!["svmap"]!["instances"]!.AsArray().Select(n => n!.AsObject())
        .Single(n => n["actor"]?.GetValue<string>() == actor);
}
