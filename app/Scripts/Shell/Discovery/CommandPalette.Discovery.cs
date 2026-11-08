using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Classes;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;

namespace OpenScctEditor.Shell;

public sealed partial class CommandPalette
{
    private const int MaxResults = 200;
    private readonly Dictionary<string, DiscoveryEntry> _results = new(StringComparer.Ordinal);
    private List<DiscoveryEntry> _index = [];
    private Label _description = null!;
    private Button _show = null!, _open = null!, _star = null!;
    private bool _favouritesOnly;
    internal IEnumerable<DiscoveryEntry> Results => _results.Values;
    internal string? SelectedResultKey => SelectedEntry?.Key;

    internal void RefreshDiscovery()
    {
        if (!Visible || _shortcutsOnly) return;
        Reindex(); Populate();
    }

    private void BuildDiscoveryUi(VBoxContainer box)
    {
        var filters = new HFlowContainer();
        box.AddChild(filters);
        foreach (string category in new[] { "All", "Tutorials", "Commands", "Placeables", "Assets", "Classes", "Panels", "Controls", "Favourites" })
        {
            string chosen = category;
            PanelUi.Button(filters, category, "Search " + category.ToLowerInvariant(), () =>
            {
                _favouritesOnly = chosen == "Favourites";
                _query.Text = chosen is "All" or "Favourites" ? "" : chosen.ToLowerInvariant() + ": ";
                Populate(); _query.GrabFocus(); _query.CaretColumn = _query.Text.Length;
            }, flat: true);
        }
    }

    private void BuildDiscoveryFooter(VBoxContainer box)
    {
        _description = PanelUi.Caption(box, "");
        _description.CustomMinimumSize = new Vector2(0, 46);
        _description.MaxLinesVisible = 3;
        var bar = new HBoxContainer(); box.AddChild(bar);
        _open = PanelUi.Button(bar, "Open", "Open the selected result (Enter)", () => RunSelected(false));
        _show = PanelUi.Button(bar, "Show me", "Locate this control without running its command (Alt+Enter)", LocateSelected);
        _star = PanelUi.Button(bar, "☆ Favourite", "Keep this result in Favourites (Ctrl+D)", ToggleFavourite);
        _list.ItemSelected += UpdateDetails;
    }

    private void Reindex()
    {
        _index = [];
        foreach (EditorAction a in _ctx.Actions)
        {
            if (a.Id.StartsWith("tutorial.", StringComparison.Ordinal)) continue;
            _index.Add(new("a:" + a.Id, a.Title, "Command", a.Menu.Replace("/", " › ", StringComparison.Ordinal),
                a.Description ?? "", $"{a.Id} {a.Menu} {a.Description} {ShortcutBindings.Text(a)} {DiscoverySearch.Aliases(a.Title + " " + a.Id)}",
                () => { Remember(a.Id); _ctx.Run(a.Id); }, () => DiscoveryNavigation.ShowAction(a), a.IsEnabled));
        }
        string[] completed = AppSettings.GetStrings("tutorials", "completed");
        foreach ((AssetKind assetKind, string title, string words) in new[]
        {
            (AssetKind.Mesh, "Meshes", "model prop staticmesh"), (AssetKind.Material, "Materials", "paint surface finish"),
            (AssetKind.Texture, "Textures", "image texture skin"), (AssetKind.Sound, "Sounds", "audio music preview"),
        })
            _index.Add(new("assets-tab:" + assetKind, title, "Panel", "Assets", "Open the " + title.ToLowerInvariant() + " browser and highlight its search box.",
                words, () => AssetBrowserPanel.Instance?.RevealKind(assetKind)));
        foreach ((string id, string title, string target, string words) in new[]
        {
            ("name", "Level name", "Name", "rename map title"), ("thumbnail", "Map thumbnail", "Menu", "menu image picture"),
            ("loading", "Loading screen", "Loading (spies)", "loading picture image spy merc"),
        })
            _index.Add(new("identity-field:" + id, title, "Control", "Identity", "Open Identity and highlight the " + target + " setting.",
                words, () => DiscoveryNavigation.ShowPanel("Identity", target)));
        foreach ((string kind, string title, string words) in new[]
        {
            (GeKinds.Ladder, "Ladder", "climb traversal"), (GeKinds.Pipe, "Climbable pipe / pole", "climb traversal"),
            (GeKinds.ZipLine, "Zip line", "zipline traversal"), (GeKinds.HandOverHand, "Overhead bar", "hand over hand traversal"),
            (GeKinds.Fence, "Fence", "climb traversal"), (GeKinds.LedgeGrab, "Ledge grab", "ledge mantle traversal"),
        })
            _index.Add(new("ge:" + kind, title, "Tool", "Tools › GE", "Choose this traversal type and highlight its options. Draw it in a view when ready.",
                words, () => DiscoveryNavigation.ShowGe(kind)));
        foreach (EditorTutorial lesson in TutorialCatalog.All)
            _index.Add(new("t:" + lesson.Id, "Tutorial: " + lesson.Title, "Tutorial", "Guided help",
                $"{lesson.Steps.Length} steps · Back, Next and Cancel at any time" + (completed.Contains(lesson.Id) ? " · Completed" : ""),
                lesson.Keywords + DiscoverySearch.Aliases(lesson.Title), () => TutorialRunner.Instance?.Start(lesson)));
        foreach (Placeable p in Placeables.Available(EditorServices.Classes))
            _index.Add(new("p:" + p.Class, p.Title, "Placeable", "Placeables › " + Placeables.GroupTitle(p.Group),
                p.Summary + " Locate the tile, then drag it into your map.", p.Class + " " + p.Summary + DiscoverySearch.Aliases(p.Title),
                () => PlaceablesPanel.Instance?.RevealClass(p.Class)));
        var known = Placeables.Available(EditorServices.Classes).Select(p => p.Class).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in (EditorServices.Classes?.All.Select(c => c.Name) ?? _ctx.Project.Actors.Select(a => a.Class)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (known.Contains(name)) continue;
            _index.Add(new("k:" + name, ClassNames.Title(name), "Class", "Classes", "Reveal " + name + " in the class browser; no actor is placed.",
                ClassNames.SearchText(name) + DiscoverySearch.Aliases(name), () => ClassBrowserPanel.Instance?.RevealClass(name)));
        }
        foreach (EditorPanel panel in DiscoveryNavigation.Descendants(MainWindow.Instance).OfType<EditorPanel>())
        {
            if (panel is PrefabsPanel prefabs) _index.AddRange(prefabs.DiscoveryEntries());
            if (panel is GeometryPanel geometry) _index.AddRange(geometry.DiscoveryEntries());
            if (panel is LightsPanel lights) _index.AddRange(lights.DiscoveryEntries());
            if (panel is EmitterLibraryPanel effects) { _index.AddRange(effects.DiscoveryEntries()); effects.WarmDiscovery(); }
            string name = panel.Name;
            _index.Add(new("d:" + name, name, "Panel", "View › Panels", "Open and highlight the " + name + " panel.",
                DiscoverySearch.Aliases(name), () => DiscoveryNavigation.ShowPanel(name)));
            foreach (Control control in DiscoveryNavigation.Descendants(panel).OfType<Control>())
            {
                if (DiscoveryNavigation.ActionId(control) is not null || control is not (BaseButton or LineEdit or SpinBox)) continue;
                string caption = DiscoveryNavigation.Caption(control);
                // Labelled property rows expose their labels, rather than an empty LineEdit or a raw number.
                if (control is LineEdit or SpinBox && control.GetParent()?.GetChildren().OfType<Label>().FirstOrDefault() is { } label)
                    caption = label.Text;
                if (string.IsNullOrWhiteSpace(caption)) caption = control.TooltipText.Split('\n')[0];
                if (string.IsNullOrWhiteSpace(caption) || caption.Length > 100) continue;
                string path = control.GetPath().ToString();
                _index.Add(new("u:" + name + ":" + caption + ":" + control.TooltipText, caption, "Control", name, control.TooltipText,
                    DiscoverySearch.Aliases(caption), () =>
                    {
                        if (MainWindow.Instance?.GetNodeOrNull<Control>(path) is { } current) DiscoveryNavigation.Reveal(current);
                        else DiscoveryNavigation.ShowPanel(name, caption);
                    }));
            }
        }
    }

    private void PopulateDiscovery(TreeItem root, string raw)
    {
        _results.Clear();
        (string query, string? kind) = DiscoverySearch.Parse(raw);
        HashSet<string> favourites = new(AppSettings.GetStrings("palette", "favourites"), StringComparer.Ordinal);
        string[] recent = AppSettings.GetStrings("palette", "discovery_recent");
        var candidates = new List<(DiscoveryEntry Entry, int Score)>();
        void Add(DiscoveryEntry entry)
        {
            if (_favouritesOnly && !favourites.Contains(entry.Key)) return;
            if (kind is not null && entry.Kind != kind && !(kind == "Class" && entry.Kind == "Placeable")
                && !(kind == "Asset" && entry.Kind is "Prefab" or "Preset" or "Effect")) return;
            if (DiscoverySearch.Score(query, entry.Title, entry.Keywords + " " + entry.Path, entry.Kind) is not { } score) return;
            int at = Array.IndexOf(recent, entry.Key);
            candidates.Add((entry, score + (favourites.Contains(entry.Key) ? 40 : 0) + (at >= 0 ? 24 - at : 0)
                + (query.Length > 0 && entry.Kind == "Placeable" ? 2200 : 0)));
        }
        foreach (DiscoveryEntry e in _index) Add(e);
        // The asset catalogue already indexes names and English glosses off-thread. Never fuzzy-scan all assets per keystroke.
        if (kind is null or "Asset" && (query.Length > 0 || kind == "Asset" || _favouritesOnly))
        {
            var catalog = AssetService.Instance.Catalog;
            var assets = _favouritesOnly ? catalog.All.Where(e => favourites.Contains("s:" + e.Kind + ":" + e.Path))
                : catalog.Search(query.Length == 0 ? null : query).Take(300);
            foreach (var e in assets)
                Add(new("s:" + e.Kind + ":" + e.Path, e.Name, "Asset", e.Kind + " › " + e.Path,
                    $"{e.Kind} from {e.Origin}" + (e.Gloss is { } gloss ? " · " + gloss : "") + ". Open locates the tile without placing or applying it.",
                    $"{e.Path} {e.Gloss} {e.Kind}", () => AssetBrowserPanel.Instance?.RevealAsset(e)));
        }
        if (kind is null or "Object" && query.Length > 0)
            foreach (ObjectRow row in ObjectInfo.All(_ctx.Project))
            {
                var captured = row;
                Add(new($"o:{(int)row.Ref.Kind}:{row.Ref.Id}", row.Ref.Id, "Object", row.Class,
                    "Select and frame this object in the current map. " + row.Tag, row.Tag,
                    () => { _ctx.Selection.Set([captured.Ref]); _ctx.Focus([captured.Ref]); }));
            }
        var ordered = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Entry.Title, StringComparer.OrdinalIgnoreCase).Take(MaxResults).ToList();
        foreach ((DiscoveryEntry entry, int _) in ordered)
        {
            _results[entry.Key] = entry;
            TreeItem item = _list.CreateItem(root);
            item.SetText(0, (favourites.Contains(entry.Key) ? "★ " : "") + entry.Title + "  ·  " + entry.Path);
            item.SetText(1, entry.Kind);
            item.SetMetadata(0, entry.Key);
            item.SetTooltipText(0, entry.Description);
            item.SetCustomColor(0, entry.Available ? EditorTheme.Text : EditorTheme.TextDisabled);
            item.SetCustomColor(1, EditorTheme.TextDim);
        }
        if (ordered.Count == 0)
        {
            TreeItem empty = _list.CreateItem(root);
            empty.SetText(0, _favouritesOnly ? "No favourites match. Star a result with Ctrl+D." : "No matches. Try a shorter name or choose a category above.");
            empty.SetSelectable(0, false); empty.SetSelectable(1, false);
        }
        _hint.Text = $"{ordered.Count}{(candidates.Count > MaxResults ? $" of {candidates.Count}" : "")} results"
            + (AssetService.Instance.Loading ? " · assets indexing…" : "") + " · Enter opens · Alt+Enter shows · Ctrl+D favourites · Esc closes";
        UpdateDetails();
    }

    private DiscoveryEntry? SelectedEntry => _list.GetSelected()?.GetMetadata(0).AsString() is { } key ? _results.GetValueOrDefault(key) : null;
    private void UpdateDetails()
    {
        if (_description is null) return;
        DiscoveryEntry? entry = SelectedEntry;
        bool discovery = !_shortcutsOnly && !_query.Text.TrimStart().StartsWith('@') && !_query.Text.TrimStart().StartsWith('{') && !_query.Text.TrimStart().StartsWith('[');
        _description.Visible = _open.GetParent<Control>().Visible = discovery;
        _description.Text = entry is null ? "Search a task, command, placeable, class, asset or setting. Type ‘tutorial’ or ‘how do I…’ for guided help."
            : entry.Description + (entry.Available ? "" : "\nUnavailable now. Use Show me to find the control and inspect its requirements; check the current selection or active tool.");
        _open.Text = entry?.Kind == "Command" ? "Run" : entry?.Kind == "Tutorial" ? "Start tutorial" : "Open";
        _open.Disabled = entry is null || !entry.Available;
        _show.Disabled = entry is null || entry.Locate is null && entry.Kind == "Tutorial";
        _star.Disabled = entry is null;
        bool starred = entry is not null && AppSettings.GetStrings("palette", "favourites").Contains(entry.Key);
        _star.Text = starred ? "★ Favourite" : "☆ Favourite";
    }

    private void LocateSelected()
    {
        if (SelectedEntry is not { } entry) return;
        if (entry.Locate is null && entry.Kind == "Tutorial") return;
        Close();
        (entry.Locate ?? entry.Open)();
    }

    private void ToggleFavourite()
    {
        if (SelectedEntry is not { } entry) return;
        HashSet<string> favourites = new(AppSettings.GetStrings("palette", "favourites"), StringComparer.Ordinal);
        if (!favourites.Remove(entry.Key)) favourites.Add(entry.Key);
        AppSettings.Set("palette", "favourites", favourites.ToArray());
        Populate();
        for (TreeItem? item = _list.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNext())
            if (item.GetMetadata(0).AsString() == entry.Key) { Select(item); break; }
        UpdateDetails();
    }

    private bool RunDiscovery(string key)
    {
        if (!_results.TryGetValue(key, out DiscoveryEntry? entry)) return false;
        if (!entry.Available) { _ctx.SetStatus("This command needs a different selection or editor state. Use Show me to locate it."); return true; }
        Close();
        AppSettings.Set("palette", "discovery_recent", AppSettings.GetStrings("palette", "discovery_recent").Where(k => k != key).Prepend(key).Take(MaxRecent).ToArray());
        entry.Open();
        return true;
    }
}
