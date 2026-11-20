using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Shell;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
CheckUnrealLaunch();
string? previousContent = EditorServices.ContentRoot;
try
{
    EditorServices.ContentRoot = "C:/content with spaces";
    foreach (string side in new[] { "spy", "merc" })
    foreach (string rules in new[] { "playlevel", "map" })
    {
        var play = new PlayOptions { Side = side, Mode = "DiskHunt", Rules = rules, Bots = 3, Difficulty = "expert", Players = 6 };
        var start = new PlayStart(new Vec3(64, -32, 128), new Rot(-1024, 8192, 0));
        IReadOnlyList<string> original = GameLauncher.PlayArguments("C:/maps with spaces", "SmokeTest", play, start);
        Check(!original.Contains("--svm-responsive-smoke"), "Original smoke is the default");
        foreach (string mode in PlayOptions.SmokeModes)
        {
            play.Smoke = mode;
            IReadOnlyList<string> launchArgs = GameLauncher.PlayArguments("C:/maps with spaces", "SmokeTest", play, start);
            Check(launchArgs.Where(a => a != "--svm-responsive-smoke" && !a.StartsWith("--svm-smoke-mode=", StringComparison.Ordinal)).SequenceEqual(original),
                "Smoke modes preserve side, rules, map, bots, content and spawn options");
            Check(launchArgs.Contains("--svm-responsive-smoke") == (mode != "original"), "Only volumetric modes enable the shared smoke rule");
            Check(mode == "original" ? !launchArgs.Any(a => a.StartsWith("--svm-smoke-mode=", StringComparison.Ordinal))
                : launchArgs.Contains("--svm-smoke-mode=" + (mode == "volumetric-full" ? "full" : "fast")), "Fast and Full select distinct game rendering modes");
            var workspace = new Workspace { Play = play.Clone() };
            Check(ProjectStore.WorkspaceFromJson(ProjectStore.WorkspaceToJson(workspace)).Play.Smoke == mode,
                "The selected smoke mode survives workspace save and reopen");
        }
        play.Smoke = "original";
        Check(GameLauncher.PlayArguments("C:/maps with spaces", "SmokeTest", play, start).SequenceEqual(original), "Switching back to Original removes both volumetric flags");
    }
    Check(ProjectStore.WorkspaceFromJson(new SvJsonObject()).Play.Smoke == "original", "Older workspaces keep original smoke");
    Check(ProjectStore.WorkspaceFromJson(ApiJson.Obj(("play", ApiJson.Obj(("smoke", ApiJson.Str("unknown")))))).Play.Smoke == "original",
        "Invalid stored smoke modes recover to Original");
    var session = EditorSession.New("SmokeTest", "Smoke modes");
    session.Execute(WorkspaceEdits.Play("Full smoke", p => p.Smoke = "volumetric-full"));
    Check(session.Project.Workspace.Play.Smoke == "volumetric-full", "Changing smoke mode is an ordinary workspace edit");
    session.Commands.Undo();
    Check(session.Project.Workspace.Play.Smoke == "original", "Undo restores the previous smoke mode");
    session.Commands.Redo();
    Check(session.Project.Workspace.Play.Smoke == "volumetric-full", "Redo restores Full smoke");
    var changes = (SvJsonObject)ApiJson.Parse("""{"ops":[{"op":"workspace","play":{"smoke":"volumetric-fast"}}]}""");
    var result = ChangeApplier.Apply(session, changes, writeFiles: false);
    Check(result.Ok && session.Project.Workspace.Play.Smoke == "volumetric-fast", "Scripting can select Fast smoke");
}
finally { EditorServices.ContentRoot = previousContent; }
Console.WriteLine($"PASS {checks} play mode checks");

void CheckUnrealLaunch()
{
    string root = Directory.CreateTempSubdirectory("ose unreal play ").FullName;
    string? previousLauncher = Environment.GetEnvironmentVariable(GameExe.LauncherRootVariable);
    string? previousUnreal = Environment.GetEnvironmentVariable("OSE_UNREAL_EXE");
    try
    {
        Environment.SetEnvironmentVariable(GameExe.LauncherRootVariable, root);
        Environment.SetEnvironmentVariable("OSE_UNREAL_EXE", null);
        string install = Path.Combine(root, "unreal");
        string active = Path.Combine(install, "versions", "0.1.10");
        Directory.CreateDirectory(active);
        string exe = Path.Combine(active, "OpenSCCTUnreal.exe");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(Path.Combine(install, "install.json"), """{"Current":"0.1.10","Pending":"0.1.11"}""");
        string manifest = Path.Combine(active, "version.json");
        File.WriteAllText(manifest, """{"launcherProtocol":1}""");
        bool rejected = false;
        try { GameExe.FindUnreal(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "An installed client that would silently ignore the editor map is refused");
        File.WriteAllText(manifest, """{"editorMapProtocol":1,"version":"0.1.9"}""");
        rejected = false;
        try { GameExe.FindUnreal(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A release that downloads stock packs is refused");
        File.WriteAllText(manifest, """{"editorMapProtocol":1,"version":"0.1.10"}""");
        Check(GameExe.FindUnreal() == exe, "The active Unreal version is selected");

        string build = Path.Combine(root, "fresh build", "AquaE");
        Directory.CreateDirectory(Path.Combine(build, "render"));
        File.WriteAllText(Path.Combine(build, "manifest.json"), "{}");
        rejected = false;
        try { GameLauncher.UnrealArguments(build, new(), null); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "An incomplete editor build cannot start the game");
        File.WriteAllBytes(Path.Combine(build, "render", "world.glb"), [1]);
        var options = new PlayOptions { Side = "merc", Mode = "DiskHunt", Difficulty = "hard", Bots = 3, Rules = "map", Smoke = "volumetric-full" };
        var args = GameLauncher.UnrealArguments(build, options, root);
        Check(args[0] == "--scct-editor-map=" + build && args.Contains("--svm-content=" + root), "Spaces survive in the exact output and content arguments");
        Check(args.Contains("-ScctEditorSide=merc") && args.Contains("-ScctEditorMode=DiskHunt") && args.Contains("-ScctEditorBots=3")
            && args.Contains("-ScctEditorDifficulty=hard") && args.Contains("-ScctEditorMatchPlayers=4") && args.Contains("-ScctEditorPlayLevel=0")
            && args.Contains("-ScctEditorResponsiveSmoke=1"), "Editor play options reach the Unreal client");
        string project = Path.Combine(root, "OpenSCCTUnreal.uproject");
        File.WriteAllText(project, "{}");
        Check(GameLauncher.UnrealArguments(build, options, root, project).Take(2).SequenceEqual([project, "-game"]), "Source builds launch as the game");
    }
    finally
    {
        Environment.SetEnvironmentVariable(GameExe.LauncherRootVariable, previousLauncher);
        Environment.SetEnvironmentVariable("OSE_UNREAL_EXE", previousUnreal);
        Directory.Delete(root, true);
    }
}
