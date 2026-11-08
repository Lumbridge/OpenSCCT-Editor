using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Panels;

/// <summary>
/// The asset browser: the materials, static meshes, textures and sounds the map can use (its own assets, shared
/// content and every converted map's), by kind, with search over names and their English glosses, a package/group
/// tree, a source filter, thumbnails and favourites. Double-click or drag a mesh into a view to place it. With brush
/// faces picked in the surface tool, a click on a material or texture puts it on them; drag one onto a face in the
/// perspective view to put it on that face. A texture goes on as the material that shows it. Actions live in the
/// Assets menu.
/// </summary>
[Panel("Assets", DockSide.Bottom, Order = 3)]
public partial class AssetBrowserPanel : EditorPanel
{
    public static AssetBrowserPanel? Instance { get; private set; }
    private const int MaxShown = 600;
    private const int FavouritesTab = 4;

    /// <summary>Seconds of no typing before the search runs.</summary>
    private const double SearchDelay = 0.15;

    private static readonly AssetKind[] s_tabKinds = [AssetKind.Material, AssetKind.Mesh, AssetKind.Texture, AssetKind.Sound];

    private readonly List<AssetEntry> _shown = new();
    private TabBar _tabs = null!;
    private LineEdit _search = null!;
    private OptionButton _origin = null!;
    private Button _inMap = null!;
    private Tree _tree = null!;
    private Button _treeToggle = null!;

    /// <summary>True once the user showed or hid the package tree: from then on its width no longer decides.</summary>
    private bool _treeChosen;
    private ItemList _list = null!;
    private Label _details = null!;
    private Label _count = null!;
    private Button _place = null!;
    private Button _apply = null!;
    private Button _play = null!;
    private Button _stop = null!;
    private Button _favourite = null!;
    private DeferredAction _rebuild = null!;
    private DeferredAction _rebuildTree = null!;
    private DeferredAction _refreshDetails = null!;
    private Godot.Timer _searchDelay = null!;
    private EmptyStateView _empty = null!;
    private string? _treeFilter;
    private (AssetKind Kind, string Path)? _selectedAsset;

    /// <summary>The tile a left press went down on (-1: none, or a double-click's second press), its entry (the list can
    /// be rebuilt before the release), and whether that press became a drag: a click applies on release, so a drag
    /// never applies to the picked faces as well.</summary>
    private int _pressed = -1;

    private AssetEntry? _pressedEntry;

    private bool _dragged;

    /// <summary>The tile whose last click applied it to the picked faces (-1: none): a double-click on it does nothing
    /// more.</summary>
    private int _appliedByClick = -1;

    /// <summary>The material last picked in the asset browser (the stock editor's current texture), or null: Alt+click in
    /// the surface tool and the Surface panel apply it. A texture picked counts as the material that shows it (a texture
    /// no material shows leaves it as it was).</summary>
    public static string? CurrentMaterial { get; private set; }

    /// <summary>The last texture explicitly picked in Assets, for texture fields that store its asset path.</summary>
    public static string? CurrentTexture { get; private set; }

    /// <summary>Raised when <see cref="CurrentMaterial"/> changes.</summary>
    public static event Action? CurrentMaterialChanged;

    private (AssetKind Kind, string Path)? Selected
    {
        get => _selectedAsset;
        set
        {
            _selectedAsset = value;
            if (value is { Kind: AssetKind.Texture } texture) CurrentTexture = texture.Path;
            if (value is { Kind: AssetKind.Material or AssetKind.Texture } m
                && AssetService.Instance.SurfaceMaterial(m.Kind, m.Path).Material is { } material
                && !string.Equals(material, CurrentMaterial, StringComparison.Ordinal))
            {
                CurrentMaterial = material;
                CurrentMaterialChanged?.Invoke();
            }
        }
    }

    protected override void Build()
    {
        Instance = this;
        _rebuild = new DeferredAction(RebuildList);
        _rebuildTree = new DeferredAction(RebuildTree);
        _refreshDetails = new DeferredAction(ShowDetails);
        HFlowContainer bar = PanelUi.Toolbar(this);
        // The package tree folds away, by itself in a narrow dock until the user chooses: the grid needs the room more.
        _treeToggle = WorkbenchUi.IconToggle(bar, "outliner", "Show the package tree", true, () =>
        {
            _treeChosen = true;
            ShowTree(_treeToggle.ButtonPressed);
        });
        _tabs = new TabBar { ClipTabs = false, ThemeTypeVariation = "SubTabs", FocusMode = FocusModeEnum.None };
        foreach (string t in new[] { "Materials", "Meshes", "Textures", "Sounds", "Favourites" })
        {
            _tabs.AddTab(t);
        }

        _tabs.TabChanged += _ => KindChanged();
        bar.AddChild(_tabs);
        _search = PanelUi.Search(bar, "Search assets", _ => SearchChanged(),
            "Search names, packages, groups, kinds and English glosses: every word must match.\n" +
            "Down: to the results · Enter: select the best match · Escape: clear");
        _search.GuiInput += SearchInput;
        _search.TextSubmitted += _ => PickBestMatch();
        // Focus in the box always means typing goes there. Godot's LineEdit is not editing when focus came by an arrow
        // key (Up from the results) or stayed after Enter, and ignores every letter until clicked.
        _search.KeepEditingOnTextSubmit = true;
        _search.FocusEntered += () => _search.Edit();
        // As you type, after a short pause: a fast typist does not rebuild the grid for every letter.
        _searchDelay = new Godot.Timer { OneShot = true, WaitTime = SearchDelay };
        _searchDelay.Timeout += () => _rebuild.Queue();
        AddChild(_searchDelay);
        _origin = PanelUi.Options(bar, "Where the assets come from: this map (MyLevel), shared content or a converted map");
        _origin.CustomMinimumSize = new Vector2(EditorTheme.NumberFieldWidth + EditorTheme.SpaceXxl, 0);
        _origin.ItemSelected += _ => KindChanged();
        _inMap = PanelUi.Toggle(bar, "In this map", "Only the assets this map uses", false, _ => _rebuild.Queue());
        PanelUi.Button(bar, "Get Packs…", "Download asset libraries from OpenSCCT-Content", () => ContentPacksPart.Open());
        _count = PanelUi.Caption(bar, "", wrap: false);
        _count.SizeFlagsVertical = SizeFlags.ShrinkCenter;

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        _tree = new Tree { HideRoot = false, CustomMinimumSize = new Vector2(PackageTreeWidth, 0), SelectMode = Tree.SelectModeEnum.Single };
        _tree.ItemSelected += () =>
        {
            _treeFilter = _tree.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } f ? f : null;
            _rebuild.Queue();
        };
        split.AddChild(_tree);
        var right = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(right);
        _list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SelectMode = ItemList.SelectModeEnum.Single,
            MaxColumns = 0,
            SameColumnWidth = true,
            AllowRmbSelect = true,
        };
        _list.ItemSelected += i => Select((int)i);
        _list.ItemActivated += i =>
        {
            Select((int)i);
            Activate();
        };
        _list.GuiInput += ListInput;
        _list.SetDragForwarding(Callable.From<Vector2, Variant>(DragData), Callable.From((Vector2 _, Variant _) => false),
            Callable.From((Vector2 _, Variant _) => { }));
        right.AddChild(_list);
        _empty = (EmptyStateView)PanelUi.EmptyState(_list, "search", "", "");
        _empty.Visible = false;
        BuildDetails(right);

        AssetService.Instance.CatalogChanged += () =>
        {
            PreselectFromArgs();
            FillOrigins();
            KindChanged();
        };
        AssetService.Instance.FavouritesChanged += () => _rebuild.Queue();
        Context.ProjectChanged += _ =>
        {
            if (_inMap.ButtonPressed)
            {
                _rebuild.Queue();
            }
            else
            {
                // The details count this map's uses of the selected asset.
                _refreshDetails.Queue();
            }
        };
        Resized += () =>
        {
            if (!_treeChosen)
            {
                ShowTree(Size.X >= WideWidth);
            }
        };
        RegisterActions();
    }

    /// <summary>The package tree's least width.</summary>
    private const int PackageTreeWidth = 160;

    /// <summary>The panel width from which the package tree shows unless the user hid it.</summary>
    private const int WideWidth = 900;

    private void ShowTree(bool on)
    {
        _tree.Visible = on;
        _treeToggle.SetPressedNoSignal(on);
        // A hidden tree filters nothing: the grid shows what its tabs and search say.
        if (!on && _treeFilter is not null)
        {
            _tree.DeselectAll();
            _treeFilter = null;
            _rebuild.Queue();
        }

        _treeToggle.TooltipText = on ? "Hide the package tree" : "Show the package tree";
    }

    public override void Refresh()
    {
        PreselectFromArgs();
        FillOrigins();
        KindChanged();
    }

    /// <summary>Reveal one exact asset, ignoring stale browser filters, without placing or applying it.</summary>
    public void RevealAsset(AssetEntry entry, bool lesson = false)
    {
        int revision = DiscoveryNavigation.Revision;
        DockTabs.ShowPanel("Assets");
        _tabs.CurrentTab = Array.IndexOf(s_tabKinds, entry.Kind);
        _origin.Select(0);
        _inMap.SetPressedNoSignal(false);
        _treeFilter = null;
        _search.Text = entry.Path;
        KindChanged();
        EditorLifetime.Defer(() =>
        {
            if (revision != DiscoveryNavigation.Revision) return;
            int index = _shown.FindIndex(e => e.Kind == entry.Kind && e.Path == entry.Path);
            if (index < 0) return;
            _list.Select(index);
            _list.EnsureCurrentIsVisible();
            // Assign directly: browsing a search result must not change the current painting material.
            _selectedAsset = (entry.Kind, entry.Path);
            ShowDetails();
            NavigationHighlight.Instance?.PointAt(_list, !lesson,
                () => index < _shown.Count && _shown[index].Path == entry.Path ? _list.GetItemRect(index) : new Rect2());
        });
    }

    public void RevealKind(AssetKind kind, bool lesson = false)
    {
        DockTabs.ShowPanel("Assets");
        _tabs.CurrentTab = Array.IndexOf(s_tabKinds, kind);
        _origin.Select(0);
        _inMap.SetPressedNoSignal(false);
        _treeFilter = null;
        _search.Text = "";
        KindChanged();
        DiscoveryNavigation.Reveal(_search, lesson);
    }

    private AssetKind? TabKind => _tabs.CurrentTab < s_tabKinds.Length ? s_tabKinds[_tabs.CurrentTab] : null;

    private string? OriginFilter => _origin.Selected > 0 ? _origin.GetItemText(_origin.Selected) : null;

    /// <summary>The selected asset's catalogue entry (null while the index loads or for an unknown path).</summary>
    private AssetEntry? SelectedEntry => Selected is { } s ? AssetService.Instance.Catalog.Find(s.Kind, s.Path) : null;

    private void BuildDetails(Control parent)
    {
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(230, 0) };
        parent.AddChild(box);
        // What the selected asset can do (the commands of the other kinds hide), then the favourite and the "more" menu.
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        box.AddChild(buttons);
        _place = WorkbenchUi.Action(buttons, "Place", "Place the selected mesh at the view's placement point", "assets.place", Context);
        _apply = WorkbenchUi.Action(buttons, "Apply", "Apply the selected material or texture to the picked faces, else to every face of the selected brushes",
            "assets.applyMaterial", Context);
        _play = WorkbenchUi.Guard(PanelUi.IconButton(buttons, "play", "Play the selected sound", () => Context.Run("assets.playSound")),
            () => Context.FindAction("assets.playSound")?.IsEnabled() ?? false);
        _stop = PanelUi.IconButton(buttons, "stop", "Stop the sound", StopSound);
        WorkbenchUi.Action(buttons, "Usages", "Select the objects of this map that use the selected asset", "assets.findUsages", Context);
        WorkbenchUi.Spacer(buttons);
        HBoxContainer slot = WorkbenchUi.Actions(buttons);
        _favourite = WorkbenchUi.Guard(WorkbenchUi.IconToggle(slot, "star-outline", "Add the selected asset to the favourites", false, () => Context.Run("assets.favourite")),
            () => SelectedEntry is not null);
        AddMoreMenu(slot);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _details = PanelUi.Caption(scroll, "");
        _details.SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    private void FillOrigins()
    {
        string? keep = OriginFilter;
        _origin.Clear();
        _origin.AddItem("All sources");
        foreach (AssetSource s in AssetService.Instance.Catalog.Sources)
        {
            _origin.AddItem(s.Name);
            if (string.Equals(s.Name, keep, StringComparison.OrdinalIgnoreCase))
            {
                _origin.Select(_origin.ItemCount - 1);
            }
        }
    }

    private void KindChanged()
    {
        _treeFilter = null;
        bool sounds = TabKind == AssetKind.Sound;
        _list.IconMode = sounds ? ItemList.IconModeEnum.Left : ItemList.IconModeEnum.Top;
        _list.FixedIconSize = sounds ? Vector2I.Zero : new Vector2I(AssetThumbnails.Size, AssetThumbnails.Size);
        _list.FixedColumnWidth = sounds ? 0 : AssetThumbnails.Size + 40;
        _list.MaxTextLines = sounds ? 1 : 2;
        _list.MaxColumns = sounds ? 1 : 0;
        bool any = _tabs.CurrentTab == FavouritesTab;
        _place.Visible = any || TabKind == AssetKind.Mesh;
        _apply.Visible = any || TabKind is AssetKind.Material or AssetKind.Texture;
        _play.Visible = _stop.Visible = any || sounds;
        _rebuildTree.Queue();
        _rebuild.Queue();
    }

    /// <summary>The entries of the current kind (or favourites) and source, before search and tree filters.</summary>
    private IEnumerable<AssetEntry> Scope()
    {
        AssetCatalog catalog = AssetService.Instance.Catalog;
        if (_tabs.CurrentTab == FavouritesTab)
        {
            return catalog.All.Where(e => AssetService.Instance.IsFavourite(e) &&
                (OriginFilter is null || e.Origin.Equals(OriginFilter, StringComparison.OrdinalIgnoreCase)));
        }

        return catalog.Search(null, TabKind, OriginFilter);
    }

    private void RebuildTree()
    {
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        root.SetText(0, AssetService.Instance.Loading ? "Indexing\u2026" : "All packages");
        root.SetMetadata(0, "");
        foreach (IGrouping<string, AssetEntry> package in Scope().GroupBy(e => e.Package, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            TreeItem p = _tree.CreateItem(root);
            p.SetText(0, $"{package.Key} ({package.Count()})");
            p.SetMetadata(0, package.Key);
            p.Collapsed = true;
            foreach (IGrouping<string, AssetEntry> group in package.Where(e => e.Group.Length > 0)
                         .GroupBy(e => e.Group, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                TreeItem g = _tree.CreateItem(p);
                g.SetText(0, $"{group.Key} ({group.Count()})");
                g.SetMetadata(0, $"{package.Key}.{group.Key}");
            }
        }
    }

    private void RebuildList()
    {
        // A rebuild for another reason (a tab, the source) takes the typed text as it is now.
        _searchDelay.Stop();
        AssetCatalog catalog = AssetService.Instance.Catalog;
        string query = _search.Text.Trim();
        IEnumerable<AssetEntry> list = query.Length == 0
            ? Scope()
            : catalog.Search(query, TabKind, OriginFilter).Where(e => _tabs.CurrentTab != FavouritesTab || AssetService.Instance.IsFavourite(e));
        if (_treeFilter is { } prefix)
        {
            list = list.Where(e => e.Path.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
        }

        List<AssetEntry> all = InMap(list).ToList();
        _shown.Clear();
        _shown.AddRange(all.Take(MaxShown));
        _list.Clear();
        for (int i = 0; i < _shown.Count; i++)
        {
            AddItem(i, _shown[i]);
        }

        string noun = query.Length > 0 ? "match" : "asset";
        _count.Text = AssetService.Instance.Loading ? "Indexing\u2026"
            : all.Count > MaxShown ? $"{PanelUi.Int(MaxShown)} of {PanelUi.Count(all.Count, noun)}" : PanelUi.Count(all.Count, noun);
        _count.TooltipText = all.Count > MaxShown ? $"Showing the first {PanelUi.Int(MaxShown)}: refine the search to see the rest" : "";
        // Room for a typical count whatever it says, so the search box and "In this map" do not shift as you type;
        // never cut: a count too wide for the row moves to the next one.
        if (_countWidth == 0)
        {
            _countWidth = TextWidth(_count, $"{PanelUi.Int(MaxShown)} of 00,000 matches");
        }

        _count.CustomMinimumSize = new Vector2(Math.Max(_countWidth, TextWidth(_count, _count.Text)), 0);
        ShowEmpty(query, all.Count);
        ShowDetails();
    }

    /// <summary>The count's least width (see <see cref="RebuildList"/>).</summary>
    private float _countWidth;

    private static float TextWidth(Label label, string text) =>
        label.GetThemeFont("font").GetStringSize(text, fontSize: label.GetThemeFontSize("font_size")).X;

    /// <summary>Only the assets this map uses, while "In this map" is on.</summary>
    private IEnumerable<AssetEntry> InMap(IEnumerable<AssetEntry> list)
    {
        if (!_inMap.ButtonPressed)
        {
            return list;
        }

        Dictionary<string, int> used = AssetEdits.InUse(Context.Project);
        return list.Where(e => used.ContainsKey(e.Path) || used.ContainsKey(AssetPaths.Short(e.Path)));
    }

    /// <summary>Over an empty grid: why it is empty (the index still loading, a search with no match and where it
    /// would match, no favourites yet), so an empty grid never looks like a search that did nothing.</summary>
    private void ShowEmpty(string query, int count)
    {
        _empty.Visible = count == 0;
        if (count > 0)
        {
            return;
        }

        string what = _tabs.GetTabTitle(_tabs.CurrentTab).ToLowerInvariant();
        if (AssetService.Instance.Loading)
        {
            _empty.SetText("Indexing assets\u2026", query.Length > 0
                ? $"The search for \u201c{query}\u201d runs as soon as the index is ready."
                : "The materials, meshes, textures and sounds show as soon as the index is ready.");
            return;
        }

        var narrowed = new List<string>();
        if (_treeFilter is { } package)
        {
            narrowed.Add($"in package {package}");
        }

        if (OriginFilter is { } origin)
        {
            narrowed.Add($"from {origin}");
        }

        if (_inMap.ButtonPressed)
        {
            narrowed.Add("used in this map");
        }

        string only = narrowed.Count > 0 ? $"Showing only assets {string.Join(", ", narrowed)}. " : "";
        if (query.Length == 0)
        {
            if (_tabs.CurrentTab == FavouritesTab)
            {
                _empty.SetText($"No {what}", $"{only}Star an asset (the \u2606 button) to keep it here.");
            }
            else if (narrowed.Count > 0)
            {
                _empty.SetText($"No {what} match these filters", $"{only}Turn a filter off to see more.");
            }
            else
            {
                _empty.SetText($"No {what}", $"Nothing of this kind in the index ({AssetService.Instance.Catalog.Description}).");
            }

            return;
        }

        // Where the same search would find something: the other tabs (same source and map filters, all packages).
        string elsewhere = "";
        if (TabKind is { } kind)
        {
            IEnumerable<string> others = InMap(AssetService.Instance.Catalog.Search(query, null, OriginFilter))
                .Where(e => e.Kind != kind).GroupBy(e => e.Kind).OrderBy(g => g.Key)
                .Select(g => PanelUi.Count(g.Count(), AssetKinds.Name(g.Key)));
            elsewhere = string.Join(", ", others);
        }

        _empty.SetText($"No {what} match \u201c{query}\u201d",
            only + (elsewhere.Length > 0 ? $"Other tabs have matches: {elsewhere}. " : "") +
            "Every word must appear in a name, package, group, kind or English gloss.");
    }

    /// <summary>Typing: the search runs after a short pause; clearing the box shows everything at once.</summary>
    private void SearchChanged()
    {
        if (_search.Text.Trim().Length == 0)
        {
            _searchDelay.Stop();
            _rebuild.Queue();
        }
        else
        {
            _searchDelay.Start();
        }
    }

    /// <summary>Keys in the search box: Down goes to the results, Escape clears the box (then leaves it).</summary>
    private void SearchInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key || key.IsCommandOrControlPressed() || key.AltPressed || key.ShiftPressed)
        {
            return;
        }

        switch (key.Keycode)
        {
            case Key.Down:
                FlushSearch();
                FocusResults(best: false);
                _search.AcceptEvent();
                break;
            case Key.Escape:
                if (_search.Text.Length > 0)
                {
                    _search.Text = "";
                    SearchChanged();
                }
                else
                {
                    _search.ReleaseFocus();
                }

                _search.AcceptEvent();
                break;
        }
    }

    /// <summary>Escape in the results goes back to the search box, its text selected; typing a letter there goes on
    /// with the search (the list would otherwise jump to an item starting with it). Left clicks: <see cref="TileClick"/>.</summary>
    private void ListInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            TileClick(mb);
            return;
        }

        if (e is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        if (key.Keycode == Key.Escape)
        {
            EditSearch(selectAll: true);
            _list.AcceptEvent();
        }
        else if (key.Unicode > ' ' && !key.IsCommandOrControlPressed() && !key.AltPressed && !key.MetaPressed)
        {
            EditSearch(selectAll: false);
            _search.CaretColumn = _search.Text.Length;
            _search.InsertTextAtCaret(char.ConvertFromUtf32((int)key.Unicode));
            _list.AcceptEvent();
        }
    }

    /// <summary>
    /// A left click on a tile while brush faces are picked in the surface tool: a material or texture goes on every
    /// picked face, one undo step, when the button comes up on the tile it went down on without a drag (a drag applies
    /// where it is dropped instead). The press still selects the tile as usual. A double-click on a tile its first click
    /// applied ends there: its second press does not reach the list, so nothing is applied twice or activated (and a
    /// further click finds the faces already showing it, which adds no step). With no faces picked clicks and
    /// double-clicks work as before.
    /// </summary>
    private void TileClick(InputEventMouseButton mb)
    {
        int index = _list.GetItemAtPosition(mb.Position, exact: true);
        if (mb.Pressed)
        {
            _dragged = false;
            if (mb.DoubleClick)
            {
                _pressed = -1;
                if (index >= 0 && index == _appliedByClick)
                {
                    _list.AcceptEvent();
                }

                return;
            }

            _pressed = index;
            _pressedEntry = index >= 0 && index < _shown.Count ? _shown[index] : null;
            _appliedByClick = -1;
            return;
        }

        bool click = index >= 0 && index == _pressed && !_dragged && index < _shown.Count && ReferenceEquals(_shown[index], _pressedEntry);
        _pressed = -1;
        _pressedEntry = null;
        if (click && ApplyToPickedFaces(index))
        {
            _appliedByClick = index;
        }
    }

    /// <summary>Puts the tile's material or texture on the faces picked in the surface tool; false when it is not one or
    /// nothing is picked. Read-only faces of an imported baked world are skipped, and the status bar says so (also when
    /// the pick is a surface of the baked world itself, which takes nothing), as it does for a texture no material
    /// shows.</summary>
    private bool ApplyToPickedFaces(int index)
    {
        GeometrySelection geo = GeometrySelection.Instance;
        if (index >= _shown.Count || !AssetDrag.Of(_shown[index]).IsSurface || !InSurfaceTool || (geo.Faces.Count == 0 && geo.BakedSurface is null))
        {
            return false;
        }

        AssetEntry e = _shown[index];
        (string? material, string why) = AssetService.Instance.SurfaceMaterial(e.Kind, e.Path);
        if (material is null)
        {
            Context.SetStatus($"Apply {e.Name}: {why}.");
            return true;
        }

        SurfaceCommands.ApplyMaterial(material);
        return true;
    }

    /// <summary>Puts the cursor in the search box, ready for typing.</summary>
    private void EditSearch(bool selectAll)
    {
        // GrabFocus does nothing on a box that already has the focus, and focus alone does not take keys: Edit does.
        _search.GrabFocus();
        _search.Edit();
        if (selectAll)
        {
            _search.SelectAll();
        }
    }

    /// <summary>Runs a search still waiting for the pause now (Enter or Down straight after typing).</summary>
    private void FlushSearch()
    {
        if (!_searchDelay.IsStopped())
        {
            RebuildList();
        }
    }

    /// <summary>Enter in the search box: selects the best match and moves to the results, where Enter places a mesh,
    /// applies a material or plays a sound. With no match the cursor stays in the box.</summary>
    private void PickBestMatch()
    {
        FlushSearch();
        FocusResults(best: true);
    }

    /// <summary>Moves to the results: the best match (<paramref name="best"/>, for Enter), else the selection where it
    /// still shows (Down), else the first.</summary>
    private void FocusResults(bool best)
    {
        if (_shown.Count == 0)
        {
            return;
        }

        int[] picked = _list.GetSelectedItems();
        int index = !best && picked.Length > 0 ? picked[0] : 0;
        _list.Select(index);
        Select(index);
        _list.GrabFocus();
        _list.EnsureCurrentIsVisible();
    }

    private void AddItem(int index, AssetEntry e)
    {
        bool favourite = AssetService.Instance.IsFavourite(e);
        if (e.Kind == AssetKind.Sound)
        {
            _list.AddItem($"{e.Name}  \u00b7  {e.Package}.{e.Group}  \u00b7  {e.Duration:0.0} s", Icons.Get("type-sound", EditorTheme.TextDim));
        }
        else
        {
            // Broken at word ends: the list itself breaks inside words.
            _list.AddItem(WorkbenchUi.TileCaption(e.Name, _list.GetThemeFont("font"), _list.GetThemeFontSize("font_size"), _list.FixedColumnWidth - 2 * EditorTheme.SpaceS));
        }

        // A favourite's name is in the accent colour (and its tooltip says so).
        if (favourite)
        {
            _list.SetItemCustomFgColor(index, EditorTheme.Accent);
        }

        _list.SetItemTooltip(index, (favourite ? "\u2605 Favourite\n" : "") + Tooltip(e));
        if (Selected is { } s && s.Kind == e.Kind && s.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase))
        {
            _list.Select(index);
        }

        // Still this tile: a thumbnail can arrive after a new search rebuilt the grid, or the panel is gone (the editor
        // quitting while loads run).
        bool Shown() => IsInstanceValid(_list) && index < _shown.Count && ReferenceEquals(_shown[index], e) && index < _list.ItemCount;
        AssetThumbnails.Instance?.Request(e, texture =>
        {
            if (Shown())
            {
                _list.SetItemIcon(index, texture);
            }
        }, Shown);
    }

    private static string Tooltip(AssetEntry e) =>
        $"{e.Path}{(e.Gloss is { } g ? $"\n“{g}”" : "")}\nfrom {e.Origin}";

    private void Select(int index)
    {
        if (index >= 0 && index < _shown.Count)
        {
            Selected = (_shown[index].Kind, _shown[index].Path);
            ShowDetails();
        }
    }

    /// <summary>Double-click or Enter in the results: place a mesh, apply a material or texture, play a sound.</summary>
    private void Activate()
    {
        switch (Selected?.Kind)
        {
            case AssetKind.Mesh:
                Context.Run("assets.place");
                break;
            case AssetKind.Material or AssetKind.Texture:
                Context.Run("assets.applyMaterial");
                break;
            case AssetKind.Sound:
                Context.Run("assets.playSound");
                break;
        }
    }

    private void ShowDetails()
    {
        bool favourite = SelectedEntry is { } chosen && AssetService.Instance.IsFavourite(chosen);
        _favourite.SetPressedNoSignal(favourite);
        _favourite.Icon = Icons.Get(favourite ? "star" : "star-outline", Colors.White);
        _favourite.TooltipText = favourite ? "Remove the selected asset from the favourites" : "Add the selected asset to the favourites";
        if (Selected is not { } s)
        {
            _details.Text = AssetService.Instance.Catalog.Description;
            return;
        }

        if (SelectedEntry is not { } e)
        {
            _details.Text = $"{s.Kind} {s.Path}\n(not in the index yet)";
            return;
        }

        // The path breaks only after its dots and underscores, never inside a word.
        var lines = new List<string>();
        // The selection outlives a search or a tab that no longer shows it (Place and Apply still use it): say so.
        if (!_shown.Any(x => x.Kind == e.Kind && x.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add(_shown.Count == 0 ? "Still selected (nothing matches here):" : "Still selected (not in these results):");
        }

        lines.Add(PanelUi.WrapIdentifier(e.Path));
        if (e.Gloss is { } gloss)
        {
            lines.Add($"“{gloss}”");
        }

        lines.Add($"{e.Kind} from {e.Origin}");
        switch (e.Kind)
        {
            case AssetKind.Material or AssetKind.Texture:
                lines.Add($"{e.Width} \u00d7 {e.Height}");
                break;
            case AssetKind.Mesh:
                lines.Add($"{PanelUi.Count(e.Triangles, "triangle")} \u00b7 {PanelUi.Num(e.Size.X)} \u00d7 {PanelUi.Num(e.Size.Y)} \u00d7 {PanelUi.Num(e.Size.Z)} u");
                if (e.Materials.Count > 0)
                {
                    lines.Add("Materials: " + string.Join(", ", e.Materials.Take(6)) + (e.Materials.Count > 6 ? " \u2026" : ""));
                }

                break;
            case AssetKind.Sound:
                lines.Add($"{e.Duration:0.00} s{(e.SoundKind is { } k ? $", {k}" : "")}");
                break;
        }

        int here = AssetEdits.Usages(Context.Project, e.Path).Count;
        lines.Add(here == 0 ? "Not used in this map" : $"Used by {PanelUi.Count(here, "object")} in this map");
        if (e.UsedIn.Count > 0)
        {
            lines.Add("Used in: " + string.Join(", ", e.UsedIn.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} ({kv.Value})")));
        }

        _details.Text = string.Join("\n", lines);
    }
}
