using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

string content = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "../SVM-Rewrite/.content");
string game = Path.GetFullPath(args.ElementAtOrDefault(1) ?? SdcImporter.GameRoot(Environment.CurrentDirectory, null));
string root = Path.GetFullPath(args.ElementAtOrDefault(2) ?? "local/reloaded-export-checks");
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
string projectFolder = Path.GetFullPath("local/sdc-asset-checks/Bundle.osmap");
MapProject project = ProjectStore.Load(projectFolder);
project.Id = "DropA";
project.Identity.Pictures.Menu = PictureRecipe.OfFile(Path.GetFullPath("local/sdc-asset-checks/Paint.png"));
project.Identity.Pictures.LoadingSpy.Add(project.Identity.Pictures.Menu.Clone());
project.Brushes[0].Shape.Faces[2].Material = "NotInstalled.MissingTexture";
string animationFile = Path.Combine(game, "Packages", "Animations", "MAL_anm.ukx");
UPackage native = UPackage.Load(animationFile);
UExport skeletal = native.Exports.First(e => native.ClassName(e) == "SkeletalMesh");
var animated = new Actor("NativeAnimated", "Engine.StaticMeshActor") { Location = new(-128, 0, 96) };
animated.Props.Set("Mesh", PropValues.AssetRef(native.PathOf(skeletal.Index + 1)));
project.Actors.Add(animated);
string bundle = Path.Combine(root, "DropA");
var exporter = new OriginalExporter();
var options = new ExportOptions { ContentRoot = content, ProjectFolder = projectFolder, GameRoot = game };
ExportResult result = exporter.Export(project, bundle, options);
Check(result.Ok, "Reloaded export succeeds: " + string.Join("; ", result.Findings.Where(f => f.Severity == Severity.Error)));
foreach (string directory in new[] { "System/_PC_", "Packages/Animations", "Packages/Assets", "Packages/Maps", "Packages/MapsEd", "Packages/Sounds", "Packages/StaticMeshes", "Packages/Textures" })
    Check(Directory.Exists(Path.Combine(bundle, directory)), "Install layout includes " + directory);
Check(result.Findings.Any(f => f.Code == "sdc.textureMissing" && f.Severity == Severity.Warning), "Missing texture is a warning");
Check(!result.Files.Any(f => f.EndsWith(".t3d") || f.EndsWith("ImportAssets.txt")), "Native output needs no manual import commands");
foreach (string file in result.Files.Where(f => Path.GetExtension(f) is ".usx" or ".ukx" or ".utc" or ".uax" or ".uas"))
{
    byte[] bytes = File.ReadAllBytes(file);
    bool compressed = Path.GetExtension(file) is ".utc";
    Check((BitConverter.ToUInt32(bytes) != SdcContainer.PackageTag) == compressed, "Native file extensions select the required compressed or raw reader");
    var (unpacked, chunks) = SdcContainer.Unwrap(bytes);
    Check((chunks > 0) == compressed && BitConverter.ToUInt32(unpacked) == SdcContainer.PackageTag, "Every native file round-trips its extension-specific container");
}
foreach (string extension in new[] { ".sdc", ".utc", ".usx", ".ukx", ".uax", ".utx" })
{
    bool compressed = extension is ".sdc" or ".utc";
    byte[] encoded = UPackageBuilder.FileBytes("Container" + extension.ToUpperInvariant(), native.Data);
    var (unpacked, chunks) = SdcContainer.Unwrap(encoded);
    Check(unpacked.SequenceEqual(native.Data) && (chunks > 0) == compressed, "Native containers preserve bytes for " + extension);
}
Check(result.Files.Any(f => f.Contains("Packages" + Path.DirectorySeparatorChar + "Textures") && Path.GetFileName(f).StartsWith("DropA_Assets_Textures")), "Custom textures have a map-specific UTC package");
Check(result.Files.Any(f => f.EndsWith(".uax")), "Custom sounds have a native sound package");
string repackedFile = result.Files.Single(f => f.EndsWith(".ukx") && Path.GetFileName(f).Contains("MAL_anm"));
UPackage repacked = UPackage.Load(repackedFile);
Check(repacked.Name.StartsWith("DropA_Anim_"), "Animation package name is map-specific");
Check(native.Exports.Count == repacked.Exports.Count, "Native animation package retains every export");
for (int i = 0; i < native.Exports.Count; i++)
{
    UExport a = native.Exports[i], b = repacked.Exports[i];
    Check(a.Offset == b.Offset && a.Size == b.Size && native.Data.AsSpan(a.Offset, a.Size).SequenceEqual(repacked.Data.AsSpan(b.Offset, b.Size)), "Opaque skeletal/animation data and lazy-array offsets are preserved");
}
foreach (string layout in new[] { "Maps", "MapsEd" })
{
    UPackage map = UPackage.Load(Path.Combine(bundle, "Packages", layout, "DropA.sdc"));
    Check(map.Imports.Any(i => i.Outer == 0 && i.Name == repacked.Name), layout + " references the renamed animation package");
    Check(!map.Imports.Any(i => i.Outer == 0 && i.Name == native.Name), layout + " needs no original animation package");
    Check(map.Imports.Any(i => i.Outer == 0 && i.Name.StartsWith("DropA_Assets_")), layout + " references the generated custom asset package");
}
UPackage settings = UPackage.Load(Path.Combine(bundle, "Packages", "Textures", "DropA-i.utc"));
Check(settings.Exports.Any(e => e.Name == "MapSettings" && settings.ClassName(e) == "SMapSettings"), "Interface package contains native map settings");
Check(settings.Exports.Any(e => e.Name == "Menu" && settings.ClassName(e) == "Texture") && settings.Exports.Any(e => e.Name == "LoadingATT0"), "Interface pictures use the native menu/loading names");
var settingsExport = settings.Exports.Single(e => e.Name == "MapSettings");
var properties = new UPropertyReader(UeSchema.Load(content), (_, _) => null).ReadObject(settings.Reader(settingsExport), "SBase.SMapSettings", "MapSettings");
var modeIds = ((SvJsonArray)properties["Modes"]!).Select(m => PropSet.FromJson(m).GetInt("GameModeId")).ToArray();
Check(modeIds.SequenceEqual(new[] { 4, 5, 6, 7 }), "Native mode settings preserve the game ids and ordering");
Check(!Directory.Exists(Path.Combine(bundle, ".source")), "Internal source files are excluded from the install overlay");
string unrelated = Path.Combine(bundle, "keep-me.txt"); File.WriteAllText(unrelated, "Unrelated data");
Check(!exporter.Export(project, bundle, options).Ok && File.ReadAllText(unrelated) == "Unrelated data", "A previous export cannot overwrite unrelated files");
File.Delete(unrelated);
Check(exporter.Export(project, bundle, options).Ok, "A second complete export replaces the previous folder");
string sourceMapFile = Path.Combine(bundle, "Packages", "MapsEd", "DropA.sdc");
SdcMap sourceMap = SdcMapReader.Read(UPackage.Load(sourceMapFile), UeSchema.Load(content));
var importedProject = MapProject.CreateDefault("DropA", "Re-exported native map");
foreach (var property in sourceMap.Actors[0].Props) importedProject.LevelInfo.Set(property.Key, property.Value);
SdcOverlay.Apply(importedProject, sourceMap, UeSchema.Load(content), []);
importedProject.Source = new() { Kind = "sdc", File = sourceMapFile,
    Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourceMapFile))) };
string importedBundle = Path.Combine(root, "Imported");
ExportResult reexported = exporter.Export(importedProject, importedBundle, options with { ProjectFolder = null });
Check(reexported.Ok, "Imported SDC finds dependencies beside its source map: " + string.Join("; ", reexported.Findings.Where(f => f.Severity == Severity.Error)));
Check(!Directory.Exists(Path.Combine(importedBundle, ".source")), "Temporary patched source imports are not shipped");
byte[] old = File.ReadAllBytes(Path.Combine(bundle, "Packages", "Maps", "DropA.sdc"));
project.FindActor("CustomCube")!.Props.Set("StaticMesh", PropValues.AssetRef("Missing.Geometry"));
ExportResult failed = exporter.Export(project, bundle, options);
Check(!failed.Ok, "Missing mesh refuses the bundle");
Check(old.SequenceEqual(File.ReadAllBytes(Path.Combine(bundle, "Packages", "Maps", "DropA.sdc"))), "Failed export preserves the previous install folder");
Check(!Directory.GetDirectories(root, "*.stage-*").Any(), "Temporary source packages are cleaned");
Console.WriteLine($"PASS {checks} Reloaded folder checks; {bundle}");
