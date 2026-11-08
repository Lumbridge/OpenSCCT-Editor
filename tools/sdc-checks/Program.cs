using OpenScct.Content.Json;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

EditorServices.Discover(typeof(MapProject).Assembly);
string? content = args.FirstOrDefault() ?? ContentLocator.FindContentRoot();
UeSchema? schema = UeSchema.Load(content);
int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
var starter = MapProject.CreateDefault("SdcTD", "SDC export regression");
Starter.AddStarterRoom(starter);
foreach (SdcLayout layout in Enum.GetValues<SdcLayout>())
{
    // Test both the normal class-data path and the writer's schema-free reference tags.
    foreach (UeSchema? typing in new[] { schema, null })
    {
        var written = SdcWriter.Write(starter, starter.Id, new SdcWriteOptions
        {
            Layout = layout, Schema = typing, ContentRoot = content, UseSource = false,
        });
        var pkg = UPackage.Parse(starter.Id, written.Package);
        var map = SdcMapReader.Read(pkg, typing);
        Check(map.Layout == layout, $"{layout}: the serialized model layout must match its target");
        string levelInfo = map.Actors.Single(a => a.Position == 0).Name;
        foreach (var actor in map.Actors)
        {
            Check(actor.Props["Level"] is SvJsonObject owner && owner["$actor"] is SvJsonString name && name.Value == levelInfo,
                $"{layout}: {actor.Name} must reference the map's LevelInfo, including LevelInfo itself");
        }
        Check(!SdcRoundTripLint.Check(starter, written, starter.Id, layout, typing).Any(f => f.Code == "sdc.actorLevel"),
            "The read-back check must accept initialized actors");
    }
}

var exporter = new SdcExporter();
Check(exporter.Choices.Single().Default == "cooked", "SDC exports must default to the playable layout");
string temp = Path.Combine(Path.GetTempPath(), "ose-sdc-" + Guid.NewGuid().ToString("N"));
string unsafeFile = Path.Combine(temp, "Packages", "Maps", "SdcTD.sdc");
Directory.CreateDirectory(Path.GetDirectoryName(unsafeFile)!);
byte[] existing = [1, 2, 3, 4];
File.WriteAllBytes(unsafeFile, existing);
var refused = exporter.Export(starter, unsafeFile, new ExportOptions
{
    ContentRoot = content, Choices = new Dictionary<string, string> { ["layout"] = "editor" },
});
Check(!refused.Ok && refused.Findings.Any(f => f.Code == "sdc.editorInMaps"), "Editor layout in the playable Maps folder must be refused");
Check(File.ReadAllBytes(unsafeFile).SequenceEqual(existing), "A refused export must leave the existing playable map untouched");
File.Delete(unsafeFile);
Directory.Delete(Path.GetDirectoryName(unsafeFile)!);
Directory.Delete(Path.Combine(temp, "Packages"));
Directory.Delete(temp);
Console.WriteLine($"Passed {checks} SDC export checks.");
