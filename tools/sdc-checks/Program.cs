using OpenScct.Content.Json;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Session;
using System.Security.Cryptography;

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
string recoveryRoot = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "local/sdc-recovery-checks");
Directory.CreateDirectory(recoveryRoot);
string cookedFile = Path.Combine(recoveryRoot, starter.Id + ".sdc");
SdcWriter.WriteFile(starter, cookedFile, new SdcWriteOptions { Layout = SdcLayout.Cooked, Schema = schema, ContentRoot = content, UseSource = false });
byte[] originalCooked = File.ReadAllBytes(cookedFile);
var cooked = UPackage.Load(cookedFile, starter.Id);
var recovered = ProjectSnapshot.Take(starter);
recovered.Brushes.Clear();
recovered.Source = new() { Kind = "sdc", File = cookedFile, Sha256 = Convert.ToHexStringLower(SHA256.HashData(originalCooked)) };
recovered.Baked = new BakedWorld();
var recoveryFindings = new List<Finding>();
BrushRecoveryImport.Run(recovered, cooked, SdcLayout.Cooked, BrushRecoveryMode.Merge, null, recoveryFindings);
Check(recovered.Brushes.Any(BakedContent.IsRecovered), "Cooked fixture must actually recover editable brushes");
var block = new Brush("AddedAfterRecovery") { Oper = CsgOper.Add, Location = new(0,0,128), Shape = Box.Shape(new(64,64,64)) };
recovered.Brushes.Add(block);
string editorFile = Path.Combine(recoveryRoot, "RecoveredEditor.sdc");
var rebuilt = SdcWriter.WriteFile(recovered, editorFile, new SdcWriteOptions { Layout = SdcLayout.Editor, Schema = schema, ContentRoot = content });
Check(rebuilt.WorldRebuilt && rebuilt.Stats.SourceUsed, "Recovered editor export must rebuild its world while retaining source resources");
Check(recovered.Baked is { KeepRender:true, KeepCollision:true } && recovered.Brushes.Contains(block), "Export must not change the open project's settings or brushes");
Check(File.ReadAllBytes(cookedFile).SequenceEqual(originalCooked), "Recovery export must not change the cooked source");
var editorPackage = UPackage.Load(editorFile,"RecoveredEditor");
var editorMap = SdcMapReader.Read(editorPackage,schema);
Check(editorMap.Layout == SdcLayout.Editor && editorMap.Actors.Any(a=>a.Name==block.Id), "Recovered MapsEd copy must read back with the added brush");
var model = UModelData.Read(editorPackage,editorPackage.Export(editorMap.LevelRecord.WorldModel),SdcLayout.Editor);
Check(model.LightMaps.Count>0 && model.Nodes.Where(n=>n.LightMap>=0).All(n=>n.LightMap<model.LightMaps.Count), "Editor model must contain real lightmap records for its node references");
Check(BrushRecoveryImport.ReadBsp(cooked,SdcLayout.Cooked).IsOutside(block.Location) &&
    !BrushRecoveryImport.ReadBsp(editorPackage,SdcLayout.Editor).IsOutside(block.Location), "Fresh editor BSP must include edits made after recovery");
Check(editorMap.Actors[1].Class=="Engine.Brush" && editorMap.Actors[1].BrushPolys is {Count:>0}, "Recovered editor copy must have its builder brush in slot 1");
var rebuiltLint = SdcRoundTripLint.Check(recovered,rebuilt,"RecoveredEditor",SdcLayout.Editor,schema);
Check(!rebuiltLint.Any(f=>f.Severity==Severity.Error), "Recovered editor package must pass round-trip lint: "+string.Join(';',rebuiltLint.Where(f=>f.Severity==Severity.Error).Select(f=>f.Message)));
var geometryOnly = SdcWriter.Write(recovered,"RecoveredGeometry",new SdcWriteOptions { Layout=SdcLayout.Editor, Schema=schema, ContentRoot=content, UseSource=false });
Check(geometryOnly.WorldRebuilt && !geometryOnly.Stats.SourceUsed &&
    !BrushRecoveryImport.ReadBsp(UPackage.Parse("RecoveredGeometry",geometryOnly.Package),SdcLayout.Editor).IsOutside(block.Location),
    "Recovered geometry must rebuild even when exporting without the original package");
var unrecovered = ProjectSnapshot.Take(recovered); unrecovered.Brushes.Clear();
try { SdcWriter.Write(unrecovered,"Unrecovered",new SdcWriteOptions { Layout=SdcLayout.Editor, Schema=schema }); Check(false,"A cooked map without recovery must still be refused"); }
catch(SdcWriteRefusedException) { checks++; }
Console.WriteLine("Recovered editor fixture: "+editorFile);
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
