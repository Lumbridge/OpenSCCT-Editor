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
using OpenScct.Editor.Core.Windows;
using OpenScct.Editor.Core.Assets;

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
File.Delete(unsafeFile + ".export-report.md");
Directory.Delete(Path.GetDirectoryName(unsafeFile)!);
Directory.Delete(Path.Combine(temp, "Packages"));
Directory.Delete(temp);

// Exercise the replacement with the real original-game package and converted standard mesh.
string gameRoot = args.ElementAtOrDefault(2) ?? SdcImporter.GameRoot(null, null);
var gallery = ProjectSnapshot.Take(starter);
foreach (string shape in WindowDesign.Shapes)
foreach (string finish in WindowDesign.Finishes)
{
    var a = WindowCommands.Create(gallery, new WindowDesign { Shape = shape, Finish = finish, Framed = true, DepthOffset = 3 }, new(40, 50, 120), new(3400, 7200, 1100));
    a.DrawScale = 1.25;
    a.DrawScale3D = new(-1, 1.4, .75);
    a.PrePivot = new(5, 10, 15);
    a.Props.Set("Tag", SvJsonNode.String("WindowGroup"));
    a.Props.Set("Event", SvJsonNode.String("BrokenGlass"));
    gallery.Actors.Add(a);
}
int beforeBrushes = gallery.Brushes.Count;
var replacements = new List<Finding>();
var compatible = SdcCompatibility.Prepare(gallery, content, gameRoot, replacements);
Check(!replacements.Any(f => f.Severity == Severity.Error), "Window conversion must succeed: " + string.Join(';', replacements.Select(f => f.Message)));
Check(replacements.Count(SdcExportReport.IsReplacement) == 18, "Every shape and finish needs its own explicit replacement finding");
Check(gallery.Brushes.Count == beforeBrushes && gallery.Actors.Count(a => a.Window is not null) == 18, "Window replacement must leave the project and authored frames untouched");
Check(compatible.Actors.All(a => a.Window is null) && compatible.Brushes.Count > beforeBrushes, "Replacement keeps ordinary frame brushes and removes authored window metadata from the export copy");
var resolver = AssetResolver.For(content, null);
var standard = resolver.FindViewMesh(SdcCompatibility.WindowMesh)!;
var vertices = resolver.Glb(standard.GlbPath).ReadUeTriangles(standard.Mesh).Positions;
foreach (var source in gallery.Actors.Where(a => a.Window is not null))
{
    var converted = compatible.FindActor(source.Id)!;
    var (lo, hi) = WindowGeometry.Pane(source.Window!).Bounds();
    Vec3 expectedCentre = source.LocalToWorld((lo + hi) / 2);
    Vec3[] actual = vertices.Select(converted.LocalToWorld).ToArray();
    Vec3 centre = (actual.Aggregate(Vec3.Min) + actual.Aggregate(Vec3.Max)) / 2;
    Check((centre - expectedCentre).Length < .02, "Stock glass centre must preserve the authored pivot, depth offset and scale: " + source.Id);
    var local = actual.Select(source.WorldToLocal).ToArray();
    Vec3 min = local.Aggregate(Vec3.Min), max = local.Aggregate(Vec3.Max);
    Check(Math.Abs(min.Y - lo.Y) < .03 && Math.Abs(max.Y - hi.Y) < .03 && Math.Abs(min.Z - lo.Z) < .03 && Math.Abs(max.Z - hi.Z) < .03,
        "Stock glass must fit the pane bounds through rotation and nonuniform/mirrored scale: " + source.Id);
    Check(converted.Props.GetString("Tag") == "WindowGroup" && converted.Props.GetString("Event") == "BrokenGlass", "Glass gameplay links must be preserved");
}
Check(ReferenceEquals(compatible, SdcCompatibility.Prepare(compatible, content, gameRoot, replacements)), "Compatible window conversion must be idempotent");

foreach (SdcLayout layout in Enum.GetValues<SdcLayout>())
{
    var sample = ProjectSnapshot.Take(starter);
    sample.Actors.Add(WindowCommands.Create(sample, new WindowDesign { Shape = "arch", Finish = "frosted", Width = 100, Height = 120 }, new(0, 0, 130)));
    string file = Path.Combine(recoveryRoot, "Glass" + (layout == SdcLayout.Editor ? "E" : "C") + ".sdc");
    var export = exporter.Export(sample, file, new ExportOptions { ContentRoot = content, GameRoot = gameRoot,
        Choices = new Dictionary<string, string> { ["layout"] = layout.ToString().ToLowerInvariant() } });
    Check(export.Ok, "Window export must pass in " + layout + ": " + string.Join(';', export.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
    var read = SdcMapReader.Read(UPackage.Load(file), schema);
    var pane = read.Actors.Single(a => a.Class == "SBase.SVitre");
    SvJsonNode? effectiveMesh = pane.Props["StaticMesh"] ?? schema?.DefaultOf(pane.Class, "StaticMesh");
    Check(effectiveMesh is SvJsonObject obj && obj["$asset"] is SvJsonString asset && asset.Value.Equals(SdcCompatibility.WindowMesh, StringComparison.OrdinalIgnoreCase),
        "Written window must reference the real original-game mesh in " + layout + ": " + effectiveMesh);
    string report = File.ReadAllText(file + ".export-report.md");
    Check(report.Contains("1 automatic replacement") && report.Contains("Automatic compatible replacements") && report.Contains("arch/frosted"), "Saved report must explain the automatic window substitution");
    Check(report.Contains("**warning sdc.meshNotLit:") && report.Contains("**warning sdc.fallback.window:"), "Required post-export rebuilds and fit checks must be bold in the saved Markdown report");
}

var invalid = ProjectSnapshot.Take(gallery);
invalid.Actors.Add(new Actor("1InvalidName", "Engine.PlayerStart"));
invalid.Actors.Add(new Actor("2InvalidName", "Engine.PlayerStart"));
string protectedFile = Path.Combine(recoveryRoot, "NoBad.sdc");
File.WriteAllBytes(protectedFile, existing);
var failed = exporter.Export(invalid, protectedFile, new ExportOptions { ContentRoot = content, GameRoot = gameRoot });
Check(!failed.Ok && failed.Findings.Count(f => f.Code == "roundtrip.name") == 2, "Export must collect multiple object errors alongside replacement findings");
Check(failed.Findings.Where(f => f.Code == "roundtrip.name").All(f => f.Message.Contains("Workaround:") && f.Objects.Count > 0), "Every named object error must include a remedy and selectable object");
Check(File.ReadAllBytes(protectedFile).SequenceEqual(existing), "Preflight failures must preserve the previous SDC");
Check(File.ReadAllText(protectedFile + ".export-report.md").Contains("Errors requiring attention"), "Failed exports must also save a readable report");
var missing = new List<Finding>();
SdcCompatibility.Prepare(gallery, content, Path.Combine(recoveryRoot, "NoInstall"), missing);
Check(missing.Count(f => f.Code == "sdc.windowReplacement") == 18 && !missing.Any(SdcExportReport.IsReplacement), "A missing stock mesh must give an actionable error for every affected window, without claiming a replacement");
Check(missing.All(f => f.Objects.Count == 1 && f.Message.Contains("SGameplayObjects.usx")), "Missing-stock errors must identify the object and required package");
Check(SdcExportReport.RequiresAction(Finding.Warning("sdc.lightingNotRebuilt", "Rebuild in the stock editor")), "A required rebuild must be emphasized");
Check(!SdcExportReport.RequiresAction(Finding.Info("sdc.unchanged", "The source was preserved")), "Ordinary informational notes do not need emphasis");
Console.WriteLine($"Passed {checks} SDC export checks.");
