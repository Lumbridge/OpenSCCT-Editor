using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

string root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("local", "ps2-objective-checks", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);
string content = Path.Combine(root, "base-content");
Directory.CreateDirectory(Path.Combine(content, "shared"));
string stock = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSCCT", "packs", "shared", "classes.json");
File.Copy(stock, Path.Combine(content, "shared", "classes.json"), true);
var catalog = ClassCatalog.FromContent(content);
EditorServices.Classes = catalog;
Check(Placeables.Available(catalog).Select(p => p.Key).Distinct().Count() == Placeables.Available(catalog).Count, "palette keys are unique");
Check(Placeables.Find("SBase.SFlag", catalog)!.PresetId is null, "stock disk lookup stays stock");
var coercion = new PropCoercion(catalog, id => id);
Check(coercion.Coerce("SBase.SFlag", "Ps2Computer", new SvJsonObject { { "$actor", SvJsonNode.String("Console") } }, new PropSet(), "Ps2Computer", []).Value is SvJsonObject,
    "PS2 actor reference passes typed coercion");
bool rejected = false;
try { coercion.Lookup("SBase.SFlag", "Ps2Misspelled", "probe", []); } catch (ApiException) { rejected = true; }
Check(rejected, "unknown properties remain rejected");

var project = MapProject.CreateDefault("Ps2AuthorD", "PS2 authoring check");
Starter.AddStarterRoom(project);
project.LevelInfo.Set("AmbientBrightness", SvJsonNode.Integer(96));
project.LevelInfo.Set("AmbientSaturation", SvJsonNode.Integer(255));
var zoneLight = new Actor("AmbientZone", "Engine.ZoneInfo") { Location = new Vec3(0, 0, 128) };
zoneLight.Props.Set("AmbientBrightness", SvJsonNode.Integer(96));
zoneLight.Props.Set("AmbientSaturation", SvJsonNode.Integer(255));
project.Actors.Add(zoneLight);
PlacedPlaceable hack = PlaceableWiring.Place(project, Placeables.Find(Ps2ObjectivePresets.Hack, catalog)!, new Vec3(150, 100, 96), Rot.Zero, [], catalog);
hack.Command.Do(project);
Check(project.FindActor(hack.Id)!.Props.GetBool("bPS2Objective"), "ND133 typed profile placed");
var secondHack = PlaceableWiring.Place(project, Placeables.Find(Ps2ObjectivePresets.Hack, catalog)!, new Vec3(150, -200, 96), Rot.Zero, [], catalog);
secondHack.Command.Do(project);
project.Actors.Single(a => a.Class == "SBase.SMission" && a.Props.GetInt("GameMode") == 3).Props.Set("MinimumObjectives", SvJsonNode.Integer(2));
PlacedPlaceable vial = PlaceableWiring.Place(project, Placeables.Find(Ps2ObjectivePresets.Vial, catalog)!, new Vec3(-150, 100, 96), new Rot(0, 16384, 0), [], catalog);
vial.Command.Do(project);
Actor flag = project.FindActor(vial.Id)!;
Check(flag.Props["Ps2SourceStaticMesh"] is SvJsonObject && flag.Props["Ps2Computer"] is SvJsonObject, "vial asset and companion refs survive placement");
Check(project.Prefabs.Count == 1 && project.Actors.Count(a => a.Editor.PrefabInstance == project.Prefabs[0].Id) == 2, "vial and console share linked assembly");
int count = project.Actors.Count;
vial.Command.Undo(project);
Check(project.Prefabs.Count == 0 && project.FindActor(vial.Id) is null, "undo removes assembly and wiring");
vial.Command.Do(project);
Check(project.Actors.Count == count && project.Prefabs.Count == 1, "redo restores exact assembly");
var zone = PlaceableWiring.Place(project, Placeables.Find("SBase.SFlagDropZone", catalog)!, new Vec3(250, -250, 80), Rot.Zero, [vial.Id], catalog);
zone.Command.Do(project);
Check(project.FindActor(zone.Id)!.Props.GetBool("bPS2Objective") && project.FindActor(zone.Id)!.Props.GetFloat("CollisionRadius") == 100,
    "existing drop-zone palette wires source touch-delivery profile");

string folder = Path.Combine(root, "Project.osmap");
ProjectStore.Save(project, folder);
Check(File.Exists(Path.Combine(folder, "assets/ps2-objectives/chars/nd133_1mesh/nd133_mesh.glb")), "saving embeds console and animation resources");
MapProject reopened = ProjectStore.Load(folder);
Check(reopened.FindActor(vial.Id)!.Props["InitialStaticMesh"] is SvJsonObject, "explicit initial vial mesh survives save/reopen");
string? bundle = Environment.GetEnvironmentVariable("OSE_PS2_OBJECTIVES");
Environment.SetEnvironmentVariable("OSE_PS2_OBJECTIVES", "none");
try
{
    ProjectStore.Save(reopened, folder);
    Check(true, "existing project saves without the Editor bundle");
    var session = new EditorSession(reopened, folder);
    string moved = Path.Combine(root, "SavedAs.osmap");
    session.Save(moved);
    Check(File.Exists(Path.Combine(moved, "assets/ps2-objectives/meshes/vial.glb")), "Save As carries source assets with the bundle unavailable");
    AssetResolver resolver = AssetResolver.For(content, moved);
    var skeletal = resolver.FindSkeletalMesh(Ps2ObjectivePresets.ConsoleMesh);
    Check(skeletal is not null && ReferenceEquals(skeletal, resolver.FindSkeletalMesh(Ps2ObjectivePresets.ConsoleMesh)), "repeated skeletal lookups reuse the parsed model");
    string compiled = Path.Combine(root, "svmap", project.Id);
    var result = new MapCompiler().Compile(reopened, compiled, new CompileOptions { ContentRoot = content, ProjectFolder = folder, FastLighting = true });
    foreach (var finding in result.Findings) Console.WriteLine(finding);
    Check(result.Ok, "fresh project compiles and strict-validates without any installed PS2 map or bundle");
    Check(File.Exists(Path.Combine(compiled, "render/chars/nd133_1mesh/nd133_mesh.anim.json")), "compiled output contains console animation metadata");
    var gameplay = JsonNode.Parse(File.ReadAllBytes(Path.Combine(compiled, "sim/gameplay.json")))!;
    Check(gameplay["actors"]!.AsArray().Any(a => a!["props"]?["Ps2SourceStaticMesh"] is JsonObject), "runtime gameplay retains typed PS2 asset fields");
    Console.WriteLine("OUTPUT " + compiled);
}
finally { Environment.SetEnvironmentVariable("OSE_PS2_OBJECTIVES", bundle); }
string conflict = Path.Combine(root, "Conflicting.osmap");
ProjectAssets.Save(conflict, new JsonObject { ["format"] = ProjectAssets.Format,
    ["meshes"] = new JsonObject { [Ps2ObjectivePresets.VialMesh] = new JsonObject { ["file"] = "user.glb", ["mesh"] = 7 } } });
bool preserved = false;
try { ProjectStore.Save(project, conflict); } catch (InvalidDataException) { preserved = true; }
Check(preserved && !Directory.Exists(Path.Combine(conflict, "assets/ps2-objectives")), "unequal asset aliases are refused before any bundle files are copied");
Console.WriteLine("PS2_OBJECTIVE_CHECKS PASS");
