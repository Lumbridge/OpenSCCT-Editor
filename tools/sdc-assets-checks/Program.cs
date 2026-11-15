using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Audio;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Services;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
string root = Path.GetFullPath(args[0]);
string imported = Path.GetFullPath(args[1]);
string original = Path.GetFullPath(args[2]);
Directory.CreateDirectory(root);
EditorServices.Discover(typeof(MapProject).Assembly);
EditorServices.Classes = ClassCatalog.GeneratedOnly();

var assets = ProjectAssets.For(imported);
Check(assets.Materials.Count > 0 && assets.Meshes.Count > 0 && assets.Sounds.Count > 0, "Imported map has a portable asset library");
var resolver = AssetResolver.For(null, imported);
foreach ((string path, JsonNode? _) in assets.Materials)
{
    var material = resolver.FindMaterial(path);
    Check(material is not null, "Local material resolves without original packages: " + path);
    if (material!.Texture is { } file) Check(File.Exists(Path.Combine(material.RenderDir, file)), "Material texture exists: " + path);
}
var checkedGlbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach ((string path, JsonNode? _) in assets.Meshes)
{
    var mesh = resolver.FindMesh(path);
    Check(mesh is not null && File.Exists(mesh.GlbPath), "Local static mesh resolves: " + path);
    if (!checkedGlbs.Add(mesh!.GlbPath)) continue;
    var json = GlbCache.TryReadJson(mesh.GlbPath)!;
    foreach (JsonObject image in (json["images"] as JsonArray ?? []).OfType<JsonObject>())
        if (image["uri"]?.GetValue<string>() is { } uri)
            Check(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mesh.GlbPath)!, uri))), "GLB image is inside the bundle: " + uri);
}
var catalog = AssetCatalog.For(null, imported);
var sounds = new SoundLibrary(catalog, null, imported);
foreach ((string path, JsonNode? _) in assets.Sounds)
{
    Check(sounds.Resolve(path) is not null, "Imported sound definition resolves: " + path);
    Check(sounds.Leaves(path).All(leaf => leaf.File is { } file && File.Exists(file)), "Composite sound dependencies are available: " + path);
}
foreach ((string path, JsonNode? _) in assets.Manifest["skeletalMeshes"]?.AsObject() ?? new())
    Check(resolver.FindSkeletalMesh(path) is not null, "Skeletal mesh resolves without the original install: " + path);

MapProject project = ProjectStore.Load(imported);
var report = ImportReport.Build(project, null);
Check(report.Sections.Any(s => s.Key == ImportReport.Assets), "Asset summary survives reopening");
Check(report.Sections.Any(s => s.Key == ImportReport.MissingAssets), "Missing assets have a separate persistent section");
Check(report.Sections.Where(s => s.Key == ImportReport.MissingAssets).SelectMany(s => s.Lines).Any(l => l.Objects.Count > 0), "Missing references select their owning actors");

// First-save adoption and Save As must keep every file, including the machine-readable dependency report.
var unsaved = new MapProject { Id = "AdoptAssets", Source = new ProjectSource { Kind = "sdc", File = original } };
unsaved.Extra.TryAdd(HandoffFile.AssetsKey, SvJsonNode.String(assets.Root));
Check(SdcAssets.ProjectFolder(unsaved, null) == imported, "Unsaved import resolves its staged library");
var session = new EditorSession(unsaved);
string first = Path.Combine(root, "AdoptAssets.osmap");
session.Save(first);
Check(File.Exists(Path.Combine(first, "assets", ProjectAssets.ManifestFile)), "First save adopts the assets");
Check(unsaved.Extra[HandoffFile.AssetsKey] is null, "First save removes the cache dependency");
if (assets.Manifest["sourceMap"] is not null)
    Check(unsaved.Source.File!.StartsWith(first, StringComparison.OrdinalIgnoreCase) && File.Exists(unsaved.Source.File), "First save also retains the source SDC locally");
string copy = Path.Combine(root, "SaveAs.osmap");
session.Save(copy);
Check(ProjectAssets.For(copy).Sounds.Count == assets.Sounds.Count, "Save As carries the sound definitions");
if (assets.Manifest["sourceMap"] is not null)
    Check(ProjectStore.Load(copy).Source.File!.StartsWith(copy, StringComparison.OrdinalIgnoreCase) && File.Exists(ProjectStore.Load(copy).Source.File), "Save As relocates the source SDC");

// Reproduce AirDoor_Open: map entries occur before shared entries and name a WAV under the shared audio root.
string content = Path.Combine(root, "sound-index");
string shared = Path.Combine(content, "shared", "audio");
string mapAudio = Path.Combine(content, "svmap", "TestMap", "audio");
Directory.CreateDirectory(shared); Directory.CreateDirectory(mapAudio);
const string shortSound = "Amb_Aqua.Ambiances.AirDoor_Open";
string realAudio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSCCT", "packs", "shared", "audio");
string shortSource = Path.Combine(realAudio, "wav", "Amb_Aqua", "Ambiances", "AirDoor_Open.wav");
File.Copy(shortSource, Path.Combine(shared, "short.wav"), true);
var definition = new JsonObject { ["kind"] = "wave", ["file"] = "short.wav", ["root"] = "shared", ["volume"] = 0.63095737, ["duration"] = 0.974966 };
File.WriteAllText(Path.Combine(shared, "audio.json"), new JsonObject { ["sounds"] = new JsonObject { [shortSound] = definition.DeepClone() } }.ToJsonString());
File.WriteAllText(Path.Combine(mapAudio, "audio.json"), new JsonObject { ["sounds"] = new JsonObject { [shortSound] = definition.DeepClone() } }.ToJsonString());
File.WriteAllText(Path.Combine(content, "svmap", "TestMap", "manifest.json"), "{}");
var indexed = AssetCatalog.For(content, null);
var clip = new SoundLibrary(indexed, content).Resolve(shortSound)!;
Check(clip.File == Path.Combine(shared, "short.wav") && File.Exists(clip.File), "Short clip resolves to the shared WAV, not an absent map file");
Check(clip.Gain > 0.63 && clip.Gain < 0.64, "Short clip keeps its gain");

// A real package with one deliberately damaged map-local asset exercises unsupported reporting without mocks.
UPackage package = UPackage.Load(original);
UExport broken = package.Exports.First(e => package.ClassName(e) is "Sound" or "StaticMesh" && e.Size >= 4);
byte[] damaged = package.Data.ToArray();
damaged.AsSpan(broken.Offset, 4).Fill(0xff);
string damagedFolder = Path.Combine(root, "damaged");
Directory.CreateDirectory(damagedFolder);
File.WriteAllBytes(Path.Combine(damagedFolder, Path.GetFileName(original)), damaged);
Console.WriteLine($"Damaged-asset fixture: {Path.Combine(damagedFolder, Path.GetFileName(original))} ({package.PathOf(broken.Index + 1)})");
Console.WriteLine($"PASS: {checks} asset portability, report persistence, save and short-sound checks.");
