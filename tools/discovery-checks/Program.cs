using OpenScctEditor.Shell;

int passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
foreach (var (text, query, kind) in new (string, string, string?)[]
{
    ("how do I setup a ladder?", "ladder", "Tutorial"),
    ("Tutorial: How to setup a laptop objective", "laptop objective", "Tutorial"),
    ("tutorial", "", "Tutorial"), ("tutorials", "", "Tutorial"),
    ("Resume Last Tutorial", "resume last tutorial", null),
    ("commands: Resume Last Tutorial", "resume last tutorial", "Command"),
    ("> delete", "delete", "Command"), ("assets: oak", "oak", "Asset"),
    ("controls: brightness", "brightness", "Control"), ("placeables: laptop", "laptop", "Placeable"),
    ("where is the laptop", "laptop", null),
    ("Loading screen", "loading screen", null),
    ("how do I set up a loading screen and thumbnail", "loading and thumbnail", "Tutorial"),
})
    Check(DiscoverySearch.Parse(text) == (query, kind), "Query parse: " + text);
Check(DiscoverySearch.Score("laptop", "Computer", DiscoverySearch.Aliases("Computer")) is not null, "Laptop synonym");
Check(DiscoverySearch.Score("resize", "Scale", DiscoverySearch.Aliases("Scale")) is not null, "Resize synonym");
Check(DiscoverySearch.Score("dropoff", "Disk drop zone", DiscoverySearch.Aliases("Disk drop zone")) is not null, "Drop-off synonym");
Check(DiscoverySearch.Score("nothinglikeanything", "Computer", "laptop") is null, "Reject unrelated results");
Check(DiscoverySearch.Score("save", "Save", "") > DiscoverySearch.Score("save", "Autosave", ""), "Exact titles beat substrings");
Check(TutorialCatalog.All.Select(t => t.Id).Distinct().Count() == TutorialCatalog.All.Count, "Unique tutorial IDs");
Check(TutorialCatalog.All.Count >= 30, "Broad tutorial coverage");
foreach (EditorTutorial tutorial in TutorialCatalog.All)
{
    Check(tutorial.Steps.Length >= 3, tutorial.Id + " has useful steps");
    Check(tutorial.Steps.All(s => s.Title.Length > 0 && s.Body.Length > 40), tutorial.Id + " readable instructions");
    foreach (TutorialStep step in tutorial.Steps)
    {
        string[] target = step.Target.Split(':', 2);
        Check(target.Length == 2 && target[0] is "panel" or "action" or "view" or "tool" or "placeable" or "assetkind", tutorial.Id + " valid navigation route");
    }
    string query = DiscoverySearch.Parse("how do I " + tutorial.Title).Query;
    Check(DiscoverySearch.Score(query, tutorial.Title, tutorial.Keywords) is not null, tutorial.Id + " can be found by its task title");
}
Console.WriteLine($"PASS {passed} search/lesson checks; {TutorialCatalog.All.Count} lessons, {TutorialCatalog.All.Sum(t => t.Steps.Length)} guided steps");
