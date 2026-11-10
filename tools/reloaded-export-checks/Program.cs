using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Interop.T3d;

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
var animated = new Actor("NativeAnimated", "SBase.SAnimatedMesh") { Location = new(-128, 0, 96) };
animated.Props.Set("Mesh", PropValues.AssetRef(native.PathOf(skeletal.Index + 1)));
project.Actors.Add(animated);
animated.Props.Set("Group", SvJsonNode.String("Meshes"));
animated.Props.Set("Skins", new SvJsonArray { PropValues.AssetRef("Prison_TEX.sfx.starsky") });
var light = new Actor("GroupedLight", "Engine.Light") { Location = new(0, 0, 128) };
light.Props.Set("Group", SvJsonNode.String("Lighting"));
light.Props.Set("bHiddenEdGroup", SvJsonNode.Bool(true));
project.Actors.Add(light);
project.LevelInfo.Set("VisibleGroups", SvJsonNode.String("Existing,Lighting"));
var transformed = new Brush("TransformProbe") { Oper = CsgOper.Add, Shape = Box.Shape(new(128,96,64)),
    MainScale = new(2,.5,3), PostScale = new(1.25,2,.75), Rotation = new(4096,8192,2048), PrePivot = new(5,7,9), Location = new(120,240,360) };
transformed.Props.Set("Group", SvJsonNode.String("Architecture"));
transformed.Props.Set("bHiddenEdGroup", SvJsonNode.Bool(true));
transformed.Shape.Faces[0].Material = "Prison_TEX.sfx.starsky";
project.Brushes.Add(transformed);
string bundle = Path.Combine(root, "DropA");
var exporter = new OriginalExporter();
var options = new ExportOptions { ContentRoot = content, ProjectFolder = projectFolder, GameRoot = game };
ExportResult result = exporter.Export(project, bundle, options);
Check(result.Ok, "Reloaded export succeeds: " + string.Join("; ", result.Findings.Where(f => f.Severity == Severity.Error)));
foreach (string directory in new[] { "System/_PC_", "Packages/Animations", "Packages/Maps", "Packages/MapsEd", "Packages/Sounds", "Packages/StaticMeshes", "Packages/Textures" })
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
Check(native.Exports.Count == repacked.Exports.Count, "Native animation package keeps reference slots stable");
UPackage trimmed = UPackage.Load(result.Files.Single(f => Path.GetFileName(f).Contains("Tex_TXT_MAL_")));
Check(trimmed.ExportsOfClass("Texture").Count() < UPackage.Load(Path.Combine(game, "Packages", "Textures", "TXT_MAL.utx")).ExportsOfClass("Texture").Count(), "Unreferenced native textures are omitted");
Check(trimmed.Exports.Any(e => e.Name.StartsWith("UnusedNative") && e.Flags == 0), "Unused object slots become private non-asset placeholders");
UPackage shaderPackage = UPackage.Load(result.Files.Single(f => Path.GetFileName(f).Contains("Tex_Prison_TEX_")));
UExport originalShader = shaderPackage.Exports.Single(e => e.Name == "starsky");
UExport safeShader = shaderPackage.Exports.Single(e => e.Name.StartsWith("starsky_Bsp_"));
int Blending(UExport e) => PropSet.FromJson(new UPropertyReader(UeSchema.Load(content), (_, _) => null)
    .ReadObject(shaderPackage.Reader(e), "Engine.Shader", e.Name)).GetInt("OutputBlending");
Check(Blending(originalShader) == 3 && Blending(safeShader) == 0, "Only the BSP variant disables native-unsafe blending");
Check(result.Findings.Any(f => f.Code == "original.bspMaterialFallback"), "Native BSP material fallback is reported");
for (int i = 0; i < native.Exports.Count; i++)
{
    UExport a = native.Exports[i], b = repacked.Exports[i];
    if (b.Name.StartsWith("UnusedNative")) continue;
    Check(a.Offset == b.Offset && a.Size == b.Size && native.Data.AsSpan(a.Offset, a.Size).SequenceEqual(repacked.Data.AsSpan(b.Offset, b.Size)), "Opaque skeletal/animation data and lazy-array offsets are preserved");
}
foreach (string layout in new[] { "Maps", "MapsEd" })
{
    UPackage map = UPackage.Load(Path.Combine(bundle, "Packages", layout, "DropA.sdc"));
    Check(!map.ExportsOfClass("Texture").Any(), layout + " does not duplicate plain project textures inside MyLevel");
    Check(map.Imports.Any(i => i.Outer == 0 && i.Name == repacked.Name), layout + " references the renamed animation package");
    Check(!map.Imports.Any(i => i.Outer == 0 && i.Name == native.Name), layout + " needs no original animation package");
    Check(map.Imports.Any(i => i.Outer == 0 && i.Name == "DropA_Assets"), layout + " references the generated custom asset package");
    SdcMap read = SdcMapReader.Read(map, UeSchema.Load(content));
    var groups = ((SvJsonString)read.Actors[0].Props["VisibleGroups"]!).Value.Split(',');
    Check(new[] { "None", "Existing", "Lighting", "Meshes", "Architecture" }.All(groups.Contains) && groups.Distinct().Count() == groups.Length, layout + " enables and deduplicates all groups including ungrouped actors");
    if (layout == "MapsEd")
    {
        Check(!read.Actors.Single(a => a.Name == light.Id).Props.ContainsKey("bHiddenEdGroup"), layout + " clears cached group hiding on lights");
        SdcActor exportedBrush = read.Actors.Single(a => a.Name == transformed.Id);
        Brush b = BrushImport.ToBrush(exportedBrush.Name, exportedBrush.Class, PropSet.FromJson(exportedBrush.Props),
            new BrushShape { Faces = exportedBrush.BrushPolys!.Select(p => new BrushFace { Vertices = p.Vertices.Select(v => new Vec3(v.X,v.Y,v.Z)).ToList() }).ToList() }, [])!;
        Vec3[] expectedVertices = transformed.Shape.Faces.SelectMany(f => f.Vertices).Select(transformed.LocalToWorld).ToArray();
        Vec3[] actualVertices = b.Shape.Faces.SelectMany(f => f.Vertices).Select(b.LocalToWorld).ToArray();
        Check(b.MainScale == Vec3.One && b.PostScale == Vec3.One && b.Rotation == Rot.Zero && b.PrePivot == Vec3.Zero, "CSG transforms are baked for native rebuilds");
        Check(expectedVertices.All(v => actualVertices.Any(a => (a-v).Length < .001)), "Scaled, rotated and pivoted brush vertices keep their world positions");
        Check(!exportedBrush.Props.ContainsKey("bHiddenEdGroup"), "Grouped brushes are enabled");
    }
    UModelData world = UModelData.Read(map, map.Export(read.LevelRecord.WorldModel), read.Layout);
    Check(world.Surfs.Any(s => map.PathOf(s.Material).EndsWith(safeShader.Name)), layout + " BSP uses the opaque variant");
}
T3dDocument text = T3dExport.FromProject(project, UeSchema.Load(content));
Check(text.Actors[0].Props.Any(f => f.Name == "VisibleGroups" && f.Value.ToString()!.Contains("Lighting")), "T3D enables the exported groups");
Check(!text.Actors.SelectMany(a => a.Props).Any(f => f.Name == "bHiddenEdGroup"), "T3D omits cached group hiding");
UPackage settings = UPackage.Load(Path.Combine(bundle, "Packages", "Textures", "DropA-i.utc"));
Check(settings.Exports.Any(e => e.Name == "MapSettings" && settings.ClassName(e) == "SMapSettings"), "Interface package contains native map settings");
Check(settings.Exports.Any(e => e.Name == "Menu" && settings.ClassName(e) == "Texture") && settings.Exports.Any(e => e.Name == "LoadingATT0"), "Interface pictures use the native menu/loading names");
var settingsExport = settings.Exports.Single(e => e.Name == "MapSettings");
var properties = new UPropertyReader(UeSchema.Load(content), (_, _) => null).ReadObject(settings.Reader(settingsExport), "SBase.SMapSettings", "MapSettings");
var modeIds = ((SvJsonArray)properties["Modes"]!).Select(m => PropSet.FromJson(m).GetInt("GameModeId")).ToArray();
Check(modeIds.SequenceEqual(new[] { 4, 5, 6, 7 }), "Native mode settings preserve the game ids and ordering");
Check(!Directory.Exists(Path.Combine(bundle, ".source")), "Internal source files are excluded from the install overlay");
Check(!Directory.EnumerateDirectories(bundle, "*", SearchOption.AllDirectories).Any(d => !Directory.EnumerateFileSystemEntries(d).Any()), "Exports contain no empty directories");
string unrelated = Path.Combine(bundle, "keep-me.txt"); File.WriteAllText(unrelated, "Unrelated data");
Check(!exporter.Export(project, bundle, options).Ok && File.ReadAllText(unrelated) == "Unrelated data", "A previous export cannot overwrite unrelated files");
File.Delete(unrelated);
Check(exporter.Export(project, bundle, options).Ok, "A second complete export replaces the previous folder");
string[] packagePaths = result.Files.Where(f => Path.GetExtension(f) is ".utc" or ".usx" or ".ukx" or ".uax").Order().ToArray();
byte[][] previousBytes = packagePaths.Select(File.ReadAllBytes).ToArray();
string paintingPath = project.Brushes[0].Shape.Faces[0].Material!;
var painting = AssetResolver.For(content, projectFolder).FindMaterial(paintingPath)!;
string imageFile = Path.Combine(painting.RenderDir, painting.Texture!);
byte[] originalImage = File.ReadAllBytes(imageFile);
try
{
    RgbaImage changedImage = RgbaImage.Decode(originalImage); changedImage.Pixels[0] ^= 255;
    File.WriteAllBytes(imageFile, changedImage.EncodePng());
    ExportResult edited = exporter.Export(project, bundle, options);
    Check(edited.Ok && packagePaths.SequenceEqual(edited.Files.Where(f => Path.GetExtension(f) is ".utc" or ".usx" or ".ukx" or ".uax").Order()), "Editing used assets keeps package filenames stable");
    Check(packagePaths.Select((p,i) => !File.ReadAllBytes(p).SequenceEqual(previousBytes[i])).Any(changed => changed), "A repeated export overwrites the package with edited asset data");
}
finally { File.WriteAllBytes(imageFile, originalImage); }
Check(exporter.Export(project, bundle, options).Ok, "Restoring an asset also reuses its package filenames");
Check(light.Props.Contains("bHiddenEdGroup") && transformed.MainScale != Vec3.One && transformed.Rotation != Rot.Zero, "Export preserves the original project's group and transform state");
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
var blendSourceProject = MapProject.CreateDefault("SrcBlend", "Retained blended BSP");
Starter.AddStarterRoom(blendSourceProject);
blendSourceProject.Brushes[0].Shape.Faces[0].Material = "Prison_TEX.sfx.starsky";
string blendSourceFile = Path.Combine(root, "SourceBlend.sdc");
SdcWriter.WriteFile(blendSourceProject, blendSourceFile, new() { Layout = SdcLayout.Editor, GameRoot = game, Schema = UeSchema.Load(content) });
SdcMap blendSource = SdcMapReader.Read(UPackage.Load(blendSourceFile, blendSourceProject.Id), UeSchema.Load(content));
var blendImported = MapProject.CreateDefault(blendSourceProject.Id, blendSourceProject.Title);
foreach (var property in blendSource.Actors[0].Props) blendImported.LevelInfo.Set(property.Key, property.Value);
SdcOverlay.Apply(blendImported, blendSource, UeSchema.Load(content), []);
blendImported.Source = new() { Kind = "sdc", File = blendSourceFile,
    Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(blendSourceFile))) };
string blendFolder = Path.Combine(root, "RetainedBlend");
ExportResult blendResult = exporter.Export(blendImported, blendFolder, options with { ProjectFolder = null });
Check(blendResult.Ok, "Retained source BSP supports native-safe material variants: " + string.Join(';', blendResult.Findings.Where(f => f.Severity == Severity.Error)));
UPackage blendMap = UPackage.Load(Path.Combine(blendFolder, "Packages", "MapsEd", "SrcBlend.sdc"));
SdcMap blendRead = SdcMapReader.Read(blendMap, UeSchema.Load(content));
UModelData blendWorld = UModelData.Read(blendMap, blendMap.Export(blendRead.LevelRecord.WorldModel), blendRead.Layout);
Check(blendWorld.Surfs.Any(s => blendMap.PathOf(s.Material).Contains("starsky_Bsp_")), "Retained source world surfaces use their BSP variant");
Check(blendWorld.Sections.Any(s => blendMap.PathOf(s.Material).Contains("starsky_Bsp_")), "Retained source render sections use their BSP variant");
Check(File.ReadAllText(Path.Combine(blendFolder, "Packages", "MapsEd", "SrcBlend.sdc.export-report.md")).Contains("opaque material variant"), "Saved reports disclose BSP appearance changes");
byte[] old = File.ReadAllBytes(Path.Combine(bundle, "Packages", "Maps", "DropA.sdc"));
project.FindActor("CustomCube")!.Props.Set("StaticMesh", PropValues.AssetRef("Missing.Geometry"));
ExportResult failed = exporter.Export(project, bundle, options);
Check(!failed.Ok, "Missing mesh refuses the bundle");
Check(old.SequenceEqual(File.ReadAllBytes(Path.Combine(bundle, "Packages", "Maps", "DropA.sdc"))), "Failed export preserves the previous install folder");
Check(!Directory.GetDirectories(root, "*.stage-*").Any(), "Temporary source packages are cleaned");
Console.WriteLine($"PASS {checks} Reloaded folder checks; {bundle}");
