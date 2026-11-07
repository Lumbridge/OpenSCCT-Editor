using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
const string smoke = "--svm-responsive-smoke";
string? previousContent = EditorServices.ContentRoot;
try
{
    EditorServices.ContentRoot = "C:/content with spaces";
    foreach (string side in new[] { "spy", "merc" })
    foreach (string rules in new[] { "playlevel", "map" })
    {
        var play = new PlayOptions { Side = side, Mode = "DiskHunt", Rules = rules, Bots = 3, Difficulty = "expert", Players = 6 };
        var start = new PlayStart(new Vec3(64, -32, 128), new Rot(-1024, 8192, 0));
        IReadOnlyList<string> ordinary = GameLauncher.PlayArguments("C:/maps with spaces", "SmokeTest", play, start);
        IReadOnlyList<string> volumetric = GameLauncher.PlayArguments("C:/maps with spaces", "SmokeTest", play, start, volumetricSmoke: true);
        Check(!ordinary.Contains(smoke), "Ordinary play keeps ordinary smoke");
        Check(volumetric.Count(a => a == smoke) == 1, "Volumetric play enables the match rule once");
        Check(volumetric.Where(a => a != smoke).SequenceEqual(ordinary), "Volumetric play preserves every existing launch option");
        Check(volumetric.Contains("--side=" + side) && volumetric.Contains("--rules=" + rules)
            && volumetric.Contains("--mode=DiskHunt") && volumetric.Contains("--bots=3")
            && volumetric.Contains("--difficulty=expert") && volumetric.Contains("--players=6")
            && volumetric.Contains("--svm-content=C:/content with spaces") && volumetric.Contains(start.Argument),
            "Side, rules, mode, bots, difficulty, player count, content and spawn are retained");
        Check(GameLauncher.PlayArguments("C:/maps with spaces", "SmokeTest", play, start).SequenceEqual(ordinary),
            "A volumetric launch does not change subsequent ordinary launches");
    }

    EditorServices.ContentRoot = null;
    var defaults = new PlayOptions();
    IReadOnlyList<string> minimal = GameLauncher.PlayArguments("maps", "SmokeTest", defaults, volumetricSmoke: true);
    Check(minimal.Contains(smoke) && !minimal.Any(a => a.StartsWith("--players=", StringComparison.Ordinal)
        || a.StartsWith("--start-at=", StringComparison.Ordinal) || a.StartsWith("--svm-content=", StringComparison.Ordinal)),
        "Volumetric smoke also works with default player count, content and spawn");
}
finally { EditorServices.ContentRoot = previousContent; }
Console.WriteLine($"PASS {checks} play launch checks");
