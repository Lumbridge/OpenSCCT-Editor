using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Panels;

public partial class PrefabsPanel
{
    internal IEnumerable<DiscoveryEntry> DiscoveryEntries() => _all.Select(p => new DiscoveryEntry(
        "prefab:" + Name + ":" + p.Name, p.Title ?? p.Name, "Prefab", Name + " › " + p.Category,
        p.Summary ?? "Select this reusable piece, then drag it into the map.", p.Name + " " + p.Category,
        () =>
        {
            DockTabs.ShowPanel(Name);
            foreach (LineEdit search in DiscoveryNavigation.Descendants(this).OfType<LineEdit>().Where(e => e.PlaceholderText == "Search prefabs")) search.Text = "";
            Reveal(p.Name);
            int index = _shown.FindIndex(s => s.Name == p.Name);
            if (index >= 0) NavigationHighlight.Instance?.PointAt(_list, localRect: () => index < _shown.Count && _shown[index].Name == p.Name ? _list.GetItemRect(index) : new Rect2());
        }));
}

public partial class GeometryPanel
{
    internal IEnumerable<DiscoveryEntry> DiscoveryEntries() => GeometryPresets.All.Select(p => new DiscoveryEntry(
        "geometry:" + p.Category + ":" + p.Title, p.Title, "Preset", "Geometry › " + p.Category, p.Summary,
        p.Kind + " " + p.Category, () =>
        {
            DockTabs.ShowPanel("Geometry");
            _query = ""; _category.Select(0);
            foreach (LineEdit search in DiscoveryNavigation.Descendants(this).OfType<LineEdit>().Where(e => e.PlaceholderText == "Search geometry")) search.Text = "";
            Fill();
            int index = _shown.FindIndex(s => s == p);
            if (index < 0) return;
            _list.Select(index); _list.EnsureCurrentIsVisible(); Details();
            NavigationHighlight.Instance?.PointAt(_list, localRect: () => index < _shown.Count && _shown[index] == p ? _list.GetItemRect(index) : new Rect2());
        }));
}

public partial class LightsPanel
{
    internal IEnumerable<DiscoveryEntry> DiscoveryEntries()
    {
        foreach (var preset in LightPresets.All)
            yield return new("light:" + preset.Title, preset.Title, "Preset", "Lights › Raw Lights", preset.Summary, "light lighting", () =>
            {
                DockTabs.ShowPanel("Lights"); _tabs.CurrentTab = 1;
                foreach (LineEdit search in DiscoveryNavigation.Descendants(this).OfType<LineEdit>()) search.Text = "";
                Filter("");
                if (_tiles.GetChildren().OfType<Control>().FirstOrDefault(c => c.Name == preset.Title.Replace(" ", "")) is { } tile) DiscoveryNavigation.Reveal(tile);
            });
        foreach (var fixture in _fixtureTiles)
        {
            string title = fixture.Tile.TooltipText.Split('\n')[0];
            yield return new("fixture:" + title, title, "Preset", "Lights › Fixtures", fixture.Search, "light fixture lamp", () =>
            {
                DockTabs.ShowPanel("Lights"); _tabs.CurrentTab = 0;
                foreach (LineEdit search in DiscoveryNavigation.Descendants(this).OfType<LineEdit>()) search.Text = "";
                Filter("");
                if (IsInstanceValid(fixture.Tile)) DiscoveryNavigation.Reveal(fixture.Tile);
            });
        }
    }
}

public partial class EmitterLibraryPanel
{
    internal void WarmDiscovery() { if (!_loaded) Reload(); }
    internal IEnumerable<DiscoveryEntry> DiscoveryEntries() => _presets.Select(p => new DiscoveryEntry(
        "effect:" + p.Id, p.Name, "Effect", "Emitter Library › " + p.Group + " › " + p.Category,
        p.Description + (p.UnavailableReason is { } why ? " · " + why : ""), "emitter particles " + p.Id,
        () =>
        {
            DockTabs.ShowPanel("Emitter Library"); _search.Text = ""; Fill(p.Id);
            DiscoveryNavigation.Reveal(_tree);
        }));
}
