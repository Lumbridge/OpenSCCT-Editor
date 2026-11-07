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
