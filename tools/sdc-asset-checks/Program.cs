using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

string content = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "../SVM-Rewrite/.content");
string root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "local/sdc-asset-checks");
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
string projectFolder = Path.Combine(root, "Bundle.osmap");
Directory.CreateDirectory(projectFolder);
var image = new RgbaImage(16, 16);
for (int i = 0; i < image.Pixels.Length; i += 4) { image.Pixels[i] = 220; image.Pixels[i + 1] = 40; image.Pixels[i + 2] = 20; image.Pixels[i + 3] = 255; }
File.WriteAllBytes(Path.Combine(root, "Paint.png"), image.EncodePng());
File.WriteAllText(Path.Combine(root, "Cube.mtl"), "newmtl Paint\nmap_Kd Paint.png\n");
File.WriteAllText(Path.Combine(root, "Cube.obj"), """
mtllib Cube.mtl
v -32 -32 -32
v 32 -32 -32
v 32 32 -32
v -32 32 -32
v -32 -32 32
v 32 -32 32
v 32 32 32
v -32 32 32
usemtl Paint
f 1 4 3 2
f 5 6 7 8
f 1 2 6 5
f 2 3 7 6
f 3 4 8 7
f 4 1 5 8
""");
MeshImportResult imported = MeshImporter.Import(projectFolder, Path.Combine(root, "Cube.obj"), new() { Name = "BundleCube", Collision = CollisionMode.Box, Overwrite = true });
var project = MapProject.CreateDefault("Bndle", "SDC asset bundle");
Starter.AddStarterRoom(project);
var actor = new Actor("CustomCube", "Engine.StaticMeshActor") { Location = new(0, 0, 96) };
actor.Props.Set("StaticMesh", PropValues.AssetRef(imported.Path)); project.Actors.Add(actor);
// Reproduce a missing EscherGallery reference backed only by converted/project image data.
const string painting = "EscherGallery.Monet.ReflectionsOfClouds";
var manifest = ProjectAssets.For(projectFolder).Edit();
((JsonObject)manifest["materials"]!)[painting] = new JsonObject
{
    ["flags"] = new JsonObject { ["texture"] = "textures/Painting.png" }, ["uSize"] = 16, ["vSize"] = 16,
};
File.WriteAllBytes(Path.Combine(projectFolder, "assets", "textures", "Painting.png"), image.EncodePng());
Directory.CreateDirectory(Path.Combine(projectFolder, "assets", "sounds"));
using (var wave = new BinaryWriter(File.Create(Path.Combine(projectFolder, "assets", "sounds", "Tone.wav"))))
{
    wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wave.Write(36 + 160);
    wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16); wave.Write((short)1); wave.Write((short)1);
    wave.Write(8000); wave.Write(16000); wave.Write((short)2); wave.Write((short)16);
    wave.Write(System.Text.Encoding.ASCII.GetBytes("data")); wave.Write(160); wave.Write(new byte[160]);
}
manifest["sounds"] = new JsonObject { ["MyLevel.Sounds.BundleTone"] = new JsonObject { ["file"] = "sounds/Tone.wav" } };
actor.Props.Set("AmbientSound", PropValues.AssetRef("MyLevel.Sounds.BundleTone"));
actor.Props.Set("SoundVolume", SvJsonNode.Integer(0));
ProjectAssets.Save(projectFolder, manifest);
project.Brushes[0].Shape.Faces[0].Material = painting;
project.Brushes[0].Shape.Faces[1].Material = painting;
ProjectStore.Save(project, projectFolder);
byte[] sourceManifest = File.ReadAllBytes(Path.Combine(projectFolder, "assets", "assets.json"));
var exporter = new SdcExporter();
UeSchema? schema = UeSchema.Load(content);
string install = Path.Combine(root, "Install");
Directory.CreateDirectory(Path.Combine(install, "Packages"));
Directory.CreateDirectory(Path.Combine(install, "System", "_PC_"));
File.WriteAllBytes(Path.Combine(install, "System", "_PC_", "Core.u"), []);
var options = new ExportOptions { ContentRoot = content, ProjectFolder = projectFolder, GameRoot = "X:/deliberately-wrong-install" };
string? previousPackage = null;
foreach (string layout in new[] { "editor", "cooked" })
{
    string path = Path.Combine(install, "Packages", layout == "editor" ? "MapsEd" : "Maps", "Bndle.sdc");
    var result = exporter.Export(project, path, options with { Choices = new Dictionary<string, string> { [SdcExporter.LayoutChoice] = layout } });
    Check(result.Ok, layout + " exports: " + string.Join(';', result.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
    Check(!result.Findings.Any(f => f.Code is "sdc.unresolved" or "sdc.assetMissing" or "sdc.noInstall"), "Destination install is inferred and every asset resolves");
    string package = result.Files.Single(f => f.EndsWith(".usx", StringComparison.OrdinalIgnoreCase));
    Check(package.StartsWith(Path.Combine(install, "Packages", "StaticMeshes"), StringComparison.OrdinalIgnoreCase), "Package is installed alongside selected Maps/MapsEd");
    Check(previousPackage is null || previousPackage == package, "Editor/cooked exports share the same immutable asset package"); previousPackage = package;
    UPackage assets = UPackage.Load(package);
    Check(assets.ExportsOfClass("StaticMesh").Count() == 1 && assets.ExportsOfClass("Texture").Count() == 2, "Mesh, dependent material and one deduplicated painting are packaged");
    Check(assets.ExportsOfClass("Sound").Count() == 1, "The map-local sound is included in the same native asset package");
    var sound = assets.Reader(assets.ExportsOfClass("Sound").Single());
    UPropertyReader.SkipTagged(sound);
    Check(sound.Name() == "wav" && sound.FString() == "BundleTone", "Native sound header stores its format and source name");
    Check(sound.U32() == 0 && sound.F32() == 0, "New sounds are ordinary embedded audio rather than external stream stubs");
    int end = sound.I32(), bytes = sound.Compact();
    Check(bytes == 204 && end == sound.Pos + bytes, "Sound lazy array points past the complete WAV payload");
    var read = SdcMapReader.Read(UPackage.Load(path), schema);
    var cube = read.Actors.Single(a => a.Name == actor.Id);
    Check(PropValues.AssetPath(cube.Props["StaticMesh"]!)?.StartsWith(assets.Name + ".MyLevel.", StringComparison.Ordinal) == true, "Actor uses the bundled native mesh");
    Check(result.Findings.Any(f => f.Code == "sdc.assetPackage") && File.ReadAllText(path + ".export-report.md").Contains(package), "Export report lists the required asset file");
    var repeated = exporter.Export(project, path, options with { Choices = new Dictionary<string, string> { [SdcExporter.LayoutChoice] = layout } });
    Check(repeated.Ok && repeated.Files.Contains(package), "Repeated export reuses an identical asset package");
}
Check(project.Brushes[0].Shape.Faces[0].Material == painting && PropValues.AssetPath(actor.Props["StaticMesh"]!) == imported.Path, "Export does not rewrite the open project");
Check(File.ReadAllBytes(Path.Combine(projectFolder, "assets", "assets.json")).SequenceEqual(sourceManifest), "Source asset files remain unchanged");
// Imported SDCs retain their import slots and opaque objects. Redirect those slots too, so merely-unused
// old import records cannot continue to demand an absent external package at native load time.
string sourceInstall = Path.Combine(root, "SourceInstall");
Directory.CreateDirectory(Path.Combine(sourceInstall, "Packages", "StaticMeshes"));
File.Copy(previousPackage!, Path.Combine(sourceInstall, "Packages", "StaticMeshes", "SourceAssets.usx"), true);
var importedMap = MapProject.CreateDefault("SrcAA", "Imported dependency"); Starter.AddStarterRoom(importedMap);
const string oldReference = "SourceAssets.EscherGallery.Monet.ReflectionsOfClouds";
importedMap.Brushes[0].Shape.Faces[0].Material = oldReference;
string sourceMap = Path.Combine(sourceInstall, "Packages", "MapsEd", "SrcAA.sdc");
SdcWriter.WriteFile(importedMap, sourceMap, new() { Layout = SdcLayout.Editor, Schema = schema, GameRoot = sourceInstall, ContentRoot = content });
SdcMap nativeSource = SdcMapReader.Read(UPackage.Load(sourceMap), schema);
foreach (var property in nativeSource.Actors[0].Props) importedMap.LevelInfo.Set(property.Key, property.Value);
importedMap.Brushes.Clear();
SdcOverlay.Apply(importedMap, nativeSource, schema, []);
importedMap.Source = new() { Kind = "sdc", File = sourceMap, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourceMap))) };
var sourceAssets = ProjectAssets.For(projectFolder).Edit();
((JsonObject)sourceAssets["materials"]!)[oldReference] = ((JsonObject)sourceAssets["materials"]!)[painting]!.DeepClone();
ProjectAssets.Save(projectFolder, sourceAssets);
string importedDestination = Path.Combine(install, "Packages", "MapsEd", "Imprt.sdc");
var redirected = exporter.Export(importedMap, importedDestination, options with { Choices = new Dictionary<string, string> { [SdcExporter.LayoutChoice] = "editor" } });
Check(redirected.Ok, "Imported SDC packages its missing dependencies: " + string.Join(';', redirected.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
UPackage redirectedMap = UPackage.Load(importedDestination);
Check(!redirectedMap.Imports.Any(i => i.Outer == 0 && i.Name == "SourceAssets"), "The imported map no longer requires the old missing package, even through its retained import table");
SdcMap redirectedLevel = SdcMapReader.Read(redirectedMap, schema);
UModelData redirectedWorld = UModelData.Read(redirectedMap, redirectedMap.Export(redirectedLevel.LevelRecord.WorldModel), SdcLayout.Editor);
Check(redirectedWorld.Surfs.Where(s => s.Material != 0).All(s => redirectedMap.PathOf(s.Material).StartsWith("Imprt_Assets_", StringComparison.Ordinal)), "Retained baked-world material references follow the rewritten import slots");
Console.WriteLine("Imported native fixture: " + importedDestination);
string protectedMap = Path.Combine(install, "Packages", "MapsEd", "SafeA.sdc");
byte[] oldMap = [1, 2, 3, 4]; File.WriteAllBytes(protectedMap, oldMap);
project.Brushes[0].Shape.Faces[2].Material = "MissingPack.NotAvailable";
int filesBefore = Directory.GetFiles(Path.Combine(install, "Packages", "StaticMeshes")).Length;
var missing = exporter.Export(project, protectedMap, options with { Choices = new Dictionary<string, string> { [SdcExporter.LayoutChoice] = "editor" } });
Check(missing.Ok && missing.Findings.Any(f => f.Code == "sdc.textureMissing" && f.Severity == Severity.Warning), "A missing texture warns and exports a valid placeholder");
Check(!File.ReadAllBytes(protectedMap).SequenceEqual(oldMap), "The SDC is exported even when one texture is missing");
var missingAssets = UPackage.Load(missing.Files.Single(f => f.EndsWith(".usx", StringComparison.OrdinalIgnoreCase)));
Check(missingAssets.Exports.Any(e => missingAssets.PathOf(e.Index + 1).EndsWith(".MissingPack.NotAvailable", StringComparison.OrdinalIgnoreCase)), "The placeholder keeps the missing texture's reference path inside the map package");
actor.Props.Set("StaticMesh", PropValues.AssetRef("MissingPack.RequiredMesh"));
byte[] protectedBytes = File.ReadAllBytes(protectedMap);
filesBefore = Directory.GetFiles(Path.Combine(install, "Packages", "StaticMeshes")).Length;
var missingMesh = exporter.Export(project, protectedMap, options with { Choices = new Dictionary<string, string> { [SdcExporter.LayoutChoice] = "editor" } });
Check(!missingMesh.Ok && missingMesh.Findings.Any(f => f.Code == "sdc.assetMissing"), "Missing geometry still has an actionable error");
Check(File.ReadAllBytes(protectedMap).SequenceEqual(protectedBytes), "A missing mesh preserves the previous map");
Check(Directory.GetFiles(Path.Combine(install, "Packages", "StaticMeshes")).Length == filesBefore, "A refused export publishes no incomplete asset package");
Console.WriteLine($"PASS {checks} SDC asset checks. Native fixture: {Path.Combine(install, "Packages", "MapsEd", "Bndle.sdc")}");
