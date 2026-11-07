using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

string root = Path.Combine(Path.GetTempPath(), "ose-beginner-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("OSE_PREFABS", Path.Combine(root, "prefabs"));
EditorServices.Discover(typeof(MapProject).Assembly);
EditorServices.Classes = ClassCatalog.GeneratedOnly();
int checks = 0;
void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); checks++; }
ChangeResult Apply(EditorSession session, params SvJsonObject[] ops)
{
    var result = ChangeApplier.Apply(session, ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)), ("ops", ApiJson.Arr(ops))), writeFiles: false);
    Check(result.Ok, ApiJson.Write(result.Json));
    return result;
}
SvJsonObject Place(string name, double x) => ApiJson.Obj(("op", ApiJson.Str("prefab.place")), ("name", ApiJson.Str(name)), ("at", ApiJson.Vec(new Vec3(x, 0, 0))));

var project = MapProject.CreateDefault("First", "My first map");
Starter.AddStarterRoom(project);
var session = new EditorSession(project, null);
string original = FirstMapGuide.Revision(project);
Check(!FirstMapGuide.Steps(project, false, original)[0], "Unsaved map cannot complete save step");
Check(FirstMapGuide.Steps(project, true, original)[3], "Starter room already has both teams");
Check(!FirstMapGuide.HasConnectedRooms(project), "One room is not a connected layout");
project.Workspace.FirstMap.Explored = true;
project.Workspace.FirstMap.Hidden = true;
project.Workspace.CameraLocation = new Vec3(123, 456, 789);
Check(FirstMapGuide.Revision(project) == original, "Camera and guidance must not invalidate gameplay checks");
project.Workspace.FirstMap.CheckedRevision = original;
project.Workspace.FirstMap.PlayedRevision = original;
var copy = project.Workspace.Clone();
copy.FirstMap.Hidden = false;
Check(project.Workspace.FirstMap.Hidden, "Workspace clone must not share guide state");
ProjectStore.Save(project, Path.Combine(root, "First.osmap"));
var reopened = ProjectStore.Load(Path.Combine(root, "First.osmap"));
Check(reopened.Workspace.FirstMap.Hidden && reopened.Workspace.FirstMap.Explored && reopened.Workspace.FirstMap.CheckedRevision == original && reopened.Workspace.FirstMap.PlayedRevision == original, "Guide state survives save/reopen");
Check(!ApiJson.Write(ProjectStore.MapToJson(project)).Contains("firstMap", StringComparison.Ordinal), "Guide state stays out of game map metadata");
Check(!ProjectStore.WorkspaceFromJson(new SvJsonObject()).FirstMap.Hidden, "Old workspaces get safe defaults");

Apply(session, Place("objective_computer", 128));
string withObjective = FirstMapGuide.Revision(project);
Check(FirstMapGuide.Steps(project, true, withObjective)[4], "Connected objective completes its step");
Check(!FirstMapGuide.Steps(project, true, withObjective)[5] && !FirstMapGuide.Steps(project, true, withObjective)[6], "An edit invalidates prior check and play");
session.Commands.Undo();
Check(!FirstMapGuide.Steps(project, true, FirstMapGuide.Revision(project))[4], "Undo removes objective completion");
session.Commands.Redo();
Check(FirstMapGuide.Steps(project, true, FirstMapGuide.Revision(project))[4], "Redo restores objective completion");

Apply(session, ApiJson.Obj(("op", ApiJson.Str("create")), ("brush", ApiJson.Obj(("kind", ApiJson.Str("room")), ("height", ApiJson.Num(512)))), ("at", ApiJson.Vec(new Vec3(1088, 0, 0)))));
Check(!FirstMapGuide.HasConnectedRooms(project), "Separated rooms must not complete connection step");
Apply(session, ApiJson.Obj(("op", ApiJson.Str("create")), ("brush", ApiJson.Obj(("kind", ApiJson.Str("doorway")), ("depth", ApiJson.Num(192)), ("width", ApiJson.Num(192)))), ("at", ApiJson.Vec(new Vec3(544, 0, 0)))));
Check(FirstMapGuide.HasConnectedRooms(project), "Doorway connects the starter and second room");
session.Commands.Undo();
Check(!FirstMapGuide.HasConnectedRooms(project), "Undoing doorway invalidates connection");

foreach (string name in FeaturedGameplay.Conditions.Keys)
{
    var s = EditorSession.New("Parts", "Parts");
    Apply(s, Place(name, 0), Place(name, 2048));
    Check(s.Project.Prefabs.Count == 2, name + ": two independent instances");
    var groups = s.Project.Actors.GroupBy(a => a.Editor.PrefabInstance).ToList();
    foreach (var group in groups)
    {
        var own = group.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Actor actor in group)
        {
            void Refs(SvJsonNode node)
            {
                if (PropValues.ActorId(node) is { } id) Check(own.Contains(id), name + ": actor reference crossed instances");
                else if (node is SvJsonObject o) foreach (var kv in o) Refs(kv.Value);
                else if (node is SvJsonArray a) foreach (var item in a) Refs(item);
            }
            foreach (var kv in actor.Props) Refs(kv.Value);
            if (actor.Props.GetString("Event") is { Length: > 0 } evt && evt != "None")
                Check(group.Any(a => a.Props.GetString("Tag") == evt), name + ": event must target own instance");
        }
    }
}
Check(PropertyHelp.Find(EditorServices.Classes, "Engine.PlayerStart", "TeamNumber")?.Contains("spies", StringComparison.Ordinal) == true, "Class-specific help");
Check(PropertyHelp.Find(EditorServices.Classes, "SBase.SCamera", "Event")?.Contains("matching Tag", StringComparison.Ordinal) == true, "Inherited property help");
Check(PropertyHelp.Find(EditorServices.Classes, "SBase.SCamera", "UnknownProperty") is null, "Unknown help keeps existing fallback");
var guided = EditorSession.New("Guide", "Guide");
guided.Execute(new WorkspaceCommand("Grid", w => w.GridSize = 64));
guided.Project.Workspace.FirstMap.Hidden = true;
guided.Commands.Undo();
Check(guided.Project.Workspace.FirstMap.Hidden, "Undoing workspace settings must not reopen dismissed guide");
guided.Execute(new ProjectSettingsCommand("Name", p => p.Title = "Named"));
guided.Project.Workspace.FirstMap.Explored = true;
guided.Commands.Undo();
Check(guided.Project.Workspace.FirstMap.Explored, "Undoing project settings must not forget navigation lesson");
Check(FeaturedGameplay.Problem("ladder", ApiJson.Obj(("mesh", ApiJson.Str("Missing.Package.Ladder"))), null, AssetResolver.Empty, EditorServices.Classes) is not null,
    "Missing prefab assets are explained before placement");
Check(FeaturedGameplay.Problem("ladder", new SvJsonObject(), null, AssetResolver.Empty, EditorServices.Classes) is null,
    "An optional ladder model is not a mandatory dependency");
Console.WriteLine($"Passed {checks} beginner workflow checks. Fixture: {root}");
if (args.Contains("--compile", StringComparer.Ordinal))
{
    string? content = ContentLocator.FindContentRoot();
    EditorServices.ContentRoot = content;
    EditorServices.Classes = ClassCatalog.FromContent(content);
    session.Commands.Redo(); // Restore the doorway for a playable two-room map.
    ProjectStore.Save(project, Path.Combine(root, "Tutorial.osmap"));
    var compiled = new MapCompiler().Compile(project, Path.Combine(root, "built", "First"), new CompileOptions
    { ContentRoot = content, ProjectFolder = Path.Combine(root, "Tutorial.osmap"), FastLighting = true });
    foreach (var finding in compiled.Findings) Console.WriteLine(finding);
    Check(compiled.Ok, "Tutorial map compiles and loads with OpenSCCT's strict loader");
    Console.WriteLine($"Tutorial compiled successfully: {compiled.OutputFolder}");
    foreach (string name in FeaturedGameplay.Conditions.Keys)
    {
        var pieces = EditorSession.New("Parts", "Prefab compile check");
        Starter.AddStarterRoom(pieces.Project);
        Apply(pieces, Place(name, 0), Place(name, 2048));
        string output = Path.Combine(root, "prefab-builds", name);
        var built = new MapCompiler().Compile(pieces.Project, output, new CompileOptions { ContentRoot = content, FastLighting = true });
        foreach (var error in built.Findings.Where(f => f.Severity == Severity.Error)) Console.WriteLine(error);
        Check(built.Ok, name + ": compiled pair must pass the strict game loader");
        Console.WriteLine($"Compiled and validated {name}");
    }
}
