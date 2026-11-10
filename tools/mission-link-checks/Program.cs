using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Content.Loading;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Validation;
using OpenScct.Game.SBase;
using Actor = OpenScct.Editor.Core.Model.Actor;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
void Refuses(Action action, string message)
{
    bool refused = false;
    try { action(); } catch (ArgumentException) { refused = true; }
    Check(refused, message);
}
string root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "ose-mission-links-" + Guid.NewGuid().ToString("N")));
if (args.Length > 1 && args[1] == "--verify-workflow")
{
    MapProject edited = ProjectStore.Load(Path.Combine(root, "Ui.osmap"));
    Actor linkedMission = edited.FindActor("Mission0")!;
    Actor objective = edited.FindActor("Objective0")!;
    Check(MissionLinks.Contains(objective, "Triggers", "ExtraComputer0"), "Device-to-objective review, undo and redo persist");
    Check(MissionLinks.Contains(linkedMission, "Objectives", "ExtraObjective0"), "Objective-to-mission guide persists");
    Check(MissionLinks.Contains(linkedMission, "Objectives", "ExtraObjective1"), "Mission-to-objectives guide persists");
    Check(MissionLinks.Contains(objective, "Triggers", "ExtraComputer1") && MissionLinks.Contains(objective, "Triggers", "Computer1"),
        "Objective-to-devices guide keeps checks across searches and applies the batch");
    Check(MissionLinks.Contains(edited.FindActor("Objective1")!, "Triggers", "Computer1"), "Adding a shared device preserves its existing owner");
    Check(linkedMission.Props.GetString("Event") == "KeepThisEvent", "Guided links preserve event wiring");
    var devices = edited.Actors.Where(a => a.Id.StartsWith("SComputerObjectiveTrigger", StringComparison.Ordinal)).ToList();
    Check(devices.Count == 3 && devices.All(a => MissionLinks.Contains(objective, "Triggers", a.Id)),
        "Two New device placements and a palette drag stay linked to the chosen objective as selection changes");
    Check(edited.Actors.Count(a => a.Class == "SBase.SExtincteur") == 1, "Translated Classes row drags the original class into the map");
    Check(edited.Actors.Count(a => a.Class == "Engine.Light") == 2, "Generic class drag places one light; cancelling the next drag adds none");
    Check(ClassNames.Title("SBase.SInterrupteur") == "Switch" && ClassNames.Title("SBase.SProjoFixe") == "Searchlight", "French gameplay classes have English names");
    Console.WriteLine($"Passed {checks} saved workflow checks.");
    return;
}
if (args.Length > 1 && args[1] == "--verify-ui")
{
    MapProject edited = ProjectStore.Load(Path.Combine(root, "Ui.osmap"));
    Check(MissionLinks.Contains(edited.FindActor("Mission0")!, "Objectives", "ExtraObjective0")
        && MissionLinks.Contains(edited.FindActor("Mission0")!, "Objectives", "ExtraObjective1"), "UI picker links both checked objectives across search filters");
    Check(MissionLinks.Contains(edited.FindActor("ExtraObjective0")!, "Triggers", "ExtraComputer0")
        && MissionLinks.Contains(edited.FindActor("ExtraObjective0")!, "Triggers", "ExtraComputer1"), "UI batch selection links both devices to the pinned objective");
    Check(MissionLinks.Contains(edited.FindActor("Mission1")!, "Objectives", "Objective3"), "UI creates a new mission and linked objective");
    Check(MissionLinks.Contains(edited.FindActor("Mission0")!, "Objectives", "Objective2"), "UI redo restores a newly created linked objective");
    Console.WriteLine($"Passed {checks} saved UI interaction checks.");
    return;
}
Directory.CreateDirectory(root);
EditorServices.Discover(typeof(MapProject).Assembly);
string? content = ContentLocator.FindContentRoot();
EditorServices.ContentRoot = content;
EditorServices.Classes = ClassCatalog.FromContent(content);
var session = EditorSession.New("Linking", "Mission linking check");
MapProject p = session.Project;
Starter.AddStarterRoom(p);
Actor Add(string id, string type, string name)
{
    var actor = new Actor(id, "SBase." + type) { Location = new Vec3(0, 0, 100) };
    actor.Props.Set(type.EndsWith("Trigger", StringComparison.Ordinal) ? "TriggerName" : "ObjectiveName", SvJsonNode.String(name));
    p.Actors.Add(actor);
    return actor;
}
Actor mission = Add("Mission0", "SMission", "Infiltrate the archive");
mission.Props.Set("GameMode", SvJsonNode.Integer(5));
mission.Props.Set("MinimumObjectives", SvJsonNode.Integer(0));
Check(MissionTree.Build(p).Missions.Single().Chained, "Mission analysis uses the default-on chain flag when it is not stored");
Check(mission.Props["bChained"] is null, "Reading the default does not store an override");
session.Execute(ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(mission.Id)], "Unchain mission", actor: a =>
    a.Props.Set("bChained", SvJsonNode.Bool(false))));
Check(!MissionTree.Build(p).Missions.Single().Chained, "An explicit false overrides the default-on chain flag");
session.Commands.Undo();
Check(MissionTree.Build(p).Missions.Single().Chained && p.FindActor(mission.Id)!.Props["bChained"] is null,
    "Undo restores inherited chaining without storing a value");
mission = p.FindActor(mission.Id)!;
mission.Props.Set("Event", SvJsonNode.String("KeepThisEvent"));
Actor first = Add("Objective0", "SObjective", "Open the vault");
Actor second = Add("Objective1", "SObjective", "Download the files");
Actor computer = Add("Computer0", "SComputerObjectiveTrigger", "Vault terminal");
Actor other = Add("Computer1", "SComputerObjectiveTrigger", "Archive terminal");
computer.Location = new Vec3(-128, 0, 64);
other.Location = new Vec3(128, 0, 64);
Check(MissionLinks.IsObjective(mission) && MissionLinks.IsObjective(first), "Mission inheritance is recognized");
Check(MissionLinks.IsTrigger(computer) && !MissionLinks.IsObjective(computer), "Computer device classification");
session.Execute(MissionLinks.Add(p, mission.Id, [first.Id, second.Id, first.Id])!);
Check(MissionTree.Refs(p.FindActor(mission.Id)!.Props["Objectives"]).SequenceEqual([first.Id, second.Id]), "Batch adds preserve order and deduplicate");
session.Commands.Undo();
Check(MissionTree.Refs(p.FindActor(mission.Id)!.Props["Objectives"]).Count == 0, "One undo removes the entire batch");
session.Commands.Redo();
Check(MissionLinks.Add(p, mission.Id, [first.Id.ToUpperInvariant()]) is null, "Already linked references are a no-op, case insensitive");
session.Execute(MissionLinks.Add(p, first.Id, [computer.Id])!);
session.Execute(MissionLinks.Add(p, second.Id, [other.Id])!);
MissionTree tree = MissionTree.Build(p);
Check(tree.Missions.Single().Objectives.Count == 2 && tree.Unlisted.Count == 0, "Mission analysis sees both linked objectives");
Check(tree.Missions.Single().Objectives[0].Triggers.Single() == computer.Id, "Mission analysis follows the device link");
Check(p.FindActor(mission.Id)!.Props.GetString("Event") == "KeepThisEvent", "Linking preserves unrelated event wiring");
Check(MissionLinks.Label(first).Contains("Open the vault", StringComparison.Ordinal) && MissionLinks.Label(first).Contains(first.Id, StringComparison.Ordinal), "Human labels retain unique actor identity");
Refuses(() => MissionLinks.Add(p, first.Id, [second.Id]), "Objectives cannot contain other objectives");
Refuses(() => MissionLinks.Add(p, mission.Id, [mission.Id]), "Self-link is refused");
Refuses(() => MissionLinks.Add(p, mission.Id, ["MissingActor"]), "Stale picker actor is refused");
Actor invalid = Add("Light0", "SAlarm", "Invalid child");
Refuses(() => MissionLinks.Add(p, mission.Id, [invalid.Id]), "Unrelated actor cannot become an objective");
Check(MissionTree.Refs(p.FindActor(mission.Id)!.Props["Objectives"]).Count == 2, "Refused batches do not mutate the mission");
p.Actors.Remove(invalid);

session.Execute(MissionLinks.Move(p, mission.Id, second.Id, -1)!);
Check(MissionTree.Refs(p.FindActor(mission.Id)!.Props["Objectives"])[0] == second.Id, "Chained objectives can move earlier");
session.Commands.Undo();
Check(MissionTree.Refs(p.FindActor(mission.Id)!.Props["Objectives"])[0] == first.Id, "Undo restores chain order");
Check(MissionLinks.Move(p, mission.Id, first.Id, -1) is null, "Cannot move past the first position");
session.Execute(MissionLinks.Remove(p, mission.Id, "Objectives", first.Id)!);
Check(p.FindActor(first.Id) is not null && p.FindActor(computer.Id) is not null, "Unlink keeps objective and device actors");
Check(MissionLinks.Contains(p.FindActor(first.Id)!, "Triggers", computer.Id), "Unlink keeps the objective's device links");
session.Commands.Undo();
Check(MissionLinks.Contains(p.FindActor(mission.Id)!, "Objectives", first.Id), "Undo restores relationship");

var created = new Actor("Objective2", "SBase.SObjective");
session.Execute(MissionLinks.CreateObjective(p, mission.Id, created));
Check(p.FindActor(created.Id) is not null && MissionLinks.Contains(p.FindActor(mission.Id)!, "Objectives", created.Id), "New objective is created and linked together");
session.Commands.Undo();
Check(p.FindActor(created.Id) is null && !MissionLinks.Contains(p.FindActor(mission.Id)!, "Objectives", created.Id), "One undo removes new objective and link");

Actor sub = Add("SubMission0", "SMission", "A nested mission");
session.Execute(MissionLinks.Add(p, mission.Id, [sub.Id])!);
Refuses(() => MissionLinks.Add(p, sub.Id, [mission.Id]), "Nested mission cycle is refused");
Actor deep = Add("SubMission1", "SMission", "A deeper mission");
session.Execute(MissionLinks.Add(p, sub.Id, [deep.Id])!);
Refuses(() => MissionLinks.Add(p, deep.Id, [mission.Id]), "Indirect nested mission cycle is refused");
Check(!new MapChecker().Check(p, new CheckOptions()).Any(f => f.Code == "mission.objective-no-trigger"), "Nested missions are not reported as objectives missing devices");
session.Commands.Undo();
session.Commands.Undo();
p.Actors.RemoveAll(a => a.Id == sub.Id || a.Id == deep.Id);

// Imported maps can carry single references, missing actors and null array slots.
Actor imported = Add("Imported", "SObjective", "Imported objective");
imported.Props.Set("Triggers", PropValues.ActorRef("MissingDevice"));
session.Execute(MissionLinks.Add(p, imported.Id, [computer.Id])!);
Check(MissionTree.Refs(p.FindActor(imported.Id)!.Props["Triggers"]).SequenceEqual(["MissingDevice", computer.Id]), "Appending preserves an imported single reference");
session.Execute(MissionLinks.Remove(p, imported.Id, "Triggers", "MissingDevice")!);
Check(MissionTree.Refs(p.FindActor(imported.Id)!.Props["Triggers"]).Single() == computer.Id, "Missing references can be unlinked");
session.Commands.Undo();
Check(MissionTree.Refs(p.FindActor(imported.Id)!.Props["Triggers"])[0] == "MissingDevice", "Undo restores missing reference");
p.Actors.RemoveAll(a => a.Id == imported.Id);

Actor sparse = Add("SparseMission", "SMission", "Sparse mission");
sparse.Props.Set("Objectives", PropValues.Array([PropValues.ActorRef(first.Id), SvJsonNode.Null, PropValues.ActorRef(second.Id)]));
session.Execute(MissionLinks.Move(p, sparse.Id, first.Id, 1)!);
Check(MissionTree.Refs(p.FindActor(sparse.Id)!.Props["Objectives"]).SequenceEqual([second.Id, first.Id]), "Reorder skips imported empty slots");
Check(p.FindActor(sparse.Id)!.Props["Objectives"] is SvJsonArray values && values[1].Kind == SvJsonKind.Null, "Reorder preserves imported empty slots");
session.Commands.Undo();
p.Actors.RemoveAll(a => a.Id == sparse.Id);

string folder = Path.Combine(root, "Linking.osmap");
ProjectStore.Save(p, folder);
MapProject reopened = ProjectStore.Load(folder);
Check(MissionTree.Refs(reopened.FindActor(mission.Id)!.Props["Objectives"]).SequenceEqual([first.Id, second.Id]), "Links and order survive save/reopen");
Check(MissionLinks.Contains(reopened.FindActor(first.Id)!, "Triggers", computer.Id), "Device links survive save/reopen");
var built = new MapCompiler().Compile(reopened, Path.Combine(root, "compiled"), new CompileOptions { ContentRoot = content, ProjectFolder = folder, FastLighting = true });
foreach (var finding in built.Findings.Where(f => f.Severity == Severity.Error)) Console.WriteLine(finding);
Check(built.Ok, "Edited mission compiles and passes strict map validation");
var registry = ActorClassRegistry.Create(CorePackage.Info, EnginePackage.Info, SBasePackage.Info);
var world = new World(1);
var loaded = new MapLoader(registry).Load(world, SvMap.Load(built.OutputFolder));
var runtimeMission = (SMission)loaded.FindActor(mission.Id)!;
var runtimeComputer = (SComputerObjectiveTrigger)loaded.FindActor(computer.Id)!;
var runtimeOther = (SComputerObjectiveTrigger)loaded.FindActor(other.Id)!;
Check(runtimeMission.Objectives.Count == 2 && ReferenceEquals(runtimeMission.Objectives[0], loaded.FindActor(first.Id)), "Game loads the edited objective order");
runtimeMission.Desactivate();
Check(runtimeComputer.bHidden && runtimeOther.bHidden, "Game mission deactivation reaches both devices");
runtimeMission.Reset();
Check(!runtimeComputer.bHidden && runtimeComputer.bActive && runtimeComputer.ComputerInteraction is { bUsable: true }, "First chained objective reveals a usable computer in the game");
Check(!runtimeOther.bActive, "Second chained objective stays inactive until activated");
((SObjective)loaded.FindActor(first.Id)!).NotifyTaken(null, runtimeComputer);
Check(!runtimeComputer.bActive && runtimeOther.bActive, "Completing the first objective unlocks the second in tree order");
ZoneChecks.Run(Check, root, content);
Console.WriteLine($"Passed {checks} mission linking checks, including compiled game activation and sequential zones. Fixture: {folder}");

Add("ExtraObjective0", "SObjective", "Camera control");
Add("ExtraObjective1", "SObjective", "Research files");
Add("ExtraComputer0", "SComputerObjectiveTrigger", "Unlinked terminal A");
Add("ExtraComputer1", "SComputerObjectiveTrigger", "Unlinked terminal B");
ProjectStore.Save(p, Path.Combine(root, "Ui.osmap"));
