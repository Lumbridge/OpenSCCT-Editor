using System.Text;
using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Audio;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
EditorServices.Discover(typeof(MapProject).Assembly);
EditorServices.Classes = ClassCatalog.GeneratedOnly();
string root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "ose-audio-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
string folder = Path.Combine(root, "SoundDemo.osmap");
var map = new MapProject { Id = "SoundDemo", Title = "Sound editing and particle preview" };
ProjectStore.Save(map, folder);
string wav = Path.Combine(root, "TestTone.wav");
using (var writer = new BinaryWriter(File.Create(wav)))
{
    const int rate = 22050, frames = rate * 2;
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
    for (int i = 0; i < frames; i++) writer.Write((short)(Math.Sin(i * Math.Tau * 330 / rate) * 10000));
}
string sound = SoundImporter.Import(folder, wav);
string second = SoundImporter.Import(folder, wav);
Check(sound != second, "Repeated imports keep unique asset paths");
Check(File.ReadAllBytes(ProjectAssets.For(folder).FilePath(ProjectAssets.For(folder).Sounds[sound]!["file"]!.GetValue<string>())).SequenceEqual(File.ReadAllBytes(wav)), "Imported WAV data is unchanged");
string bad = Path.Combine(root, "Bad.wav"); File.WriteAllText(bad, "not audio");
try { SoundImporter.Import(folder, bad); throw new Exception("Malformed audio imported"); } catch (InvalidDataException) { checks++; }
Actor actor = SoundAuthoring.Create(map, sound, new(50, 70, 100), 200, 96, 8);
Check(SoundAuthoring.Loops(actor) && SoundAuthoring.RepeatInterval(actor) == 0, "Existing sound defaults remain continuous looping");
var timed = SoundAuthoring.EditLoop([actor], true, 3.5);
var add = new AddObjectsCommand("Place sound", actors: [actor]); add.Do(map);
timed.Do(map);
Check(SoundAuthoring.RepeatInterval(map.FindActor(actor.Id)!) == 3.5, "Loop interval edits apply to placed sources");
timed.Undo(map);
var once = SoundAuthoring.Create(map, second, Vec3.Zero, loop: false, repeatInterval: 4.5);
Check(!SoundAuthoring.Loops(once) && SoundAuthoring.RepeatInterval(once) == 4.5, "One-shot mode retains its configured interval");
SoundAuthoring.Set(once, sound, 20, 64, 10);
Check(!SoundAuthoring.Loops(once) && SoundAuthoring.RepeatInterval(once) == 4.5, "Other sound edits preserve loop settings");
try { SoundAuthoring.EditLoop([actor], true, double.NaN); throw new Exception("Invalid interval accepted"); } catch (ArgumentException) { checks++; }
Check(SoundAuthoring.Create(map, sound, Vec3.Zero).Id != actor.Id, "Repeated placements have unique actor ids");
var edit = SoundAuthoring.Edit([actor], second, 25, 32, 12); edit.Do(map);
Check(SoundAuthoring.Sound(map.FindActor(actor.Id)!) == second, "Apply changes the ambient sound reference");
edit.Undo(map);
Check(SoundAuthoring.Sound(map.FindActor(actor.Id)!) == sound && map.FindActor(actor.Id)!.Props.GetInt("SoundVolume") == 200, "Undo restores the sound and original settings together");
add.Undo(map); Check(map.Actors.Count == 0, "Undo placement removes the sound source"); add.Do(map);
SoundAuthoring.Set(map.Actors[0], sound, 200, 96, 8, false, 3.5);
ProjectStore.Save(map, folder);
map = ProjectStore.Load(folder);
Check(!SoundAuthoring.Loops(map.Actors[0]) && SoundAuthoring.RepeatInterval(map.Actors[0]) == 3.5, "Loop and interval survive save/reopen");
Check(SoundAuthoring.Sound(map.Actors[0]) == sound && SoundAuthoring.Number(map.Actors[0], "SoundPitch", 64) == 96, "Sound settings survive save/reopen");
var catalog = AssetCatalog.For(null, folder);
Check(new SoundLibrary(catalog, null).Leaves(sound).Single().File is { } resolved && File.Exists(resolved), "Imported sound resolves for preview");
Check(new SoundLibrary(catalog, null).Resolve(sound)!.Gain == 1, "Imported sound's constructed numeric gain resolves without a JSON cast failure");

string output = Path.Combine(root, "built", map.Id);
var compiled = new MapCompiler().Compile(map, output, new CompileOptions { ProjectFolder = folder, FastLighting = true });
Check(compiled.Ok, "Compiled imported sound: " + string.Join("; ", compiled.Findings.Where(f => f.Severity == Severity.Error)));
JsonObject audio = JsonNode.Parse(File.ReadAllBytes(Path.Combine(output, "audio/audio.json")))!.AsObject();
JsonObject record = audio["actors"]!.AsArray()[0]!.AsObject();
Check(record["loop"]!.GetValue<bool>() == false && record["repeatInterval"]!.GetValue<double>() == 3.5, "Export includes loop mode and repeat interval for the game engine");
Check(record["soundVolume"]!.GetValue<int>() == 200 && record["soundPitch"]!.GetValue<int>() == 96 && record["soundRadius"]!.GetValue<double>() == 8, "Exported settings match the authored source");
JsonObject definition = audio["sounds"]![sound]!.AsObject();
Check(definition["root"]!.GetValue<string>() == "map" && definition["flags"] is not null && definition["kind"]!.GetValue<string>() == "wave", "Export uses a complete runtime sound definition");
Check(File.ReadAllBytes(Path.Combine(output, "audio", definition["file"]!.GetValue<string>())).SequenceEqual(File.ReadAllBytes(wav)), "The export contains the playable sound payload");

// Regression: retained maps used to preserve the converter's old audio settings, ignoring edits and new sound attachments.
map.Baked = new BakedWorld { SourceMap = output, KeepCollision = true, KeepRender = true };
Actor before = map.Actors[0];
var changed = SoundAuthoring.Edit([before], second, 77, 48, 24); changed.Do(map);
var attachment = new Actor("NewAttachment", "Engine.StaticMeshActor");
map.Actors.Add(attachment);
SoundAuthoring.Set(attachment, sound, 128, 64, 10);
string rebuilt = Path.Combine(root, "rebuilt", map.Id);
var retained = new MapCompiler().Compile(map, rebuilt, new CompileOptions { ProjectFolder = folder, FastLighting = true });
Check(retained.Ok, "Retained map rebuild succeeds: " + string.Join("; ", retained.Findings.Where(f => f.Severity == Severity.Error)));
var records = JsonNode.Parse(File.ReadAllBytes(Path.Combine(rebuilt, "audio/audio.json")))!["actors"]!.AsArray().OfType<JsonObject>().ToArray();
var editedRecord = records.Single(r => r["id"]!.GetValue<string>() == before.Id);
Check(editedRecord["ambientSound"]!.GetValue<string>() == second && editedRecord["soundVolume"]!.GetValue<int>() == 77 && editedRecord["soundPitch"]!.GetValue<int>() == 48 && editedRecord["soundRadius"]!.GetValue<double>() == 24, "Rebuilding a retained map applies all sound edits");
Check(records.Any(r => r["id"]!.GetValue<string>() == attachment.Id), "New sound attachments are exported");
map.FindActor(before.Id)!.Props.Set("AmbientSound", SvJsonNode.Null);
var cleared = new MapCompiler().Compile(map, rebuilt, new CompileOptions { ProjectFolder = folder, FastLighting = true });
Check(cleared.Ok && !JsonNode.Parse(File.ReadAllBytes(Path.Combine(rebuilt, "audio/audio.json")))!["actors"]!.AsArray().OfType<JsonObject>().Any(r => r["id"]!.GetValue<string>() == before.Id), "Cleared ambient sound stays removed after rebuilding");

// Composite definitions must be searchable, previewable and export their leaf files exactly once.
string content = Path.Combine(root, "content");
string sharedAudio = Path.Combine(content, "shared", "audio");
Directory.CreateDirectory(Path.Combine(sharedAudio, "wav", "Test"));
File.Copy(wav, Path.Combine(sharedAudio, "wav", "Test", "Leaf.wav"), true);
JsonObject Def(string kind) => new() { ["kind"] = kind, ["flags"] = 0, ["volume"] = 0.75, ["radius"] = 0, ["loop"] = false, ["positional"] = true };
var leaf = Def("wave"); leaf["file"] = "wav/Test/Leaf.wav";
var random = Def("random"); random["entries"] = new JsonArray(new JsonObject { ["sound"] = "Test.Leaf", ["value"] = 1 });
File.WriteAllText(Path.Combine(sharedAudio, "audio.json"), new JsonObject { ["format"] = "svm.audio/1", ["sounds"] = new JsonObject { ["Test.Leaf"] = leaf, ["Test.Random"] = random } }.ToJsonString());
var sharedCatalog = AssetCatalog.For(content, null);
Check(sharedCatalog.Find(AssetKind.Sound, "Test.Random")?.SoundKind == "random", "Composite sounds appear in the sound catalogue");
Check(new SoundLibrary(sharedCatalog, content).Leaves("Test.Random").Single().Path == "Test.Leaf", "A composite resolves its playable preview clip");
Check(new SoundLibrary(sharedCatalog, content).Leaves("Test.Random").Single().Gain == 0.75, "Preview preserves the source clip gain");
var compositeMap = new MapProject { Id = "CompositeSound" };
compositeMap.Actors.Add(SoundAuthoring.Create(compositeMap, "Test.Random", Vec3.Zero));
string compositeOut = Path.Combine(root, "CompositeSound");
var compositeBuild = new MapCompiler().Compile(compositeMap, compositeOut, new CompileOptions { ContentRoot = content, FastLighting = true });
Check(compositeBuild.Ok, "A map using composite sound builds");
var bundled = JsonNode.Parse(File.ReadAllBytes(Path.Combine(compositeOut, "audio/audio.json")))!["sounds"]!.AsObject();
Check(bundled.ContainsKey("Test.Random") && bundled.ContainsKey("Test.Leaf") && bundled["Test.Leaf"]!["volume"]!.GetValue<double>() == 0.75,
    "Composite export includes its definition, leaf and original clip gain");

// A small, self-contained visual fixture with deliberately obvious particles and a two-second sound.
map.Baked = null; map.Actors.Remove(attachment); SoundAuthoring.Set(map.Actors[0], sound, 128, 64, 8, true, 0);
map.Actors[0].Location = new(0, 100, 100);
var emitter = new Actor("PreviewEmitter", "Engine.Emitter") { Location = new(0, 0, 100) };
EmitterBuild.WithSystems(emitter, (SvJsonArray)SvJson.Parse("""
[{"class":"Engine.SpriteEmitter","object":"PreviewParticles","props":{"MaxParticles":80,"RespawnDeadParticles":true,"ParticlesPerSecond":40.0,"InitialParticlesPerSecond":40.0,"LifetimeRange":{"Min":1.5,"Max":2.0},"StartSizeRange":{"X":{"Min":8.0,"Max":16.0},"Y":{"Min":8.0,"Max":16.0},"Z":{"Min":8.0,"Max":16.0}},"StartVelocityRange":{"X":{"Min":-40.0,"Max":40.0},"Y":{"Min":-40.0,"Max":40.0},"Z":{"Min":40.0,"Max":100.0}},"DrawStyle":3,"UseColorScale":false}}]
"""));
map.Actors.Add(emitter);
map.Workspace.CameraLocation = new(500, 500, 350);
map.Workspace.CameraRotation = new(-5000, -24576, 0);
ProjectStore.Save(map, folder);
Console.WriteLine($"PASS: {checks} sound authoring, import, undo, persistence and export checks. Fixture: {folder}");
