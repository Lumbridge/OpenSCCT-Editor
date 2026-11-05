using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Panels;

/// <summary>
/// The placeables palette: the gameplay classes a map places (<see cref="Placeables"/>) as tiles in four groups
/// (security, objectives, pickups and stations, interactive), each with its icon and, where the class has a mesh, the
/// mesh's picture, its English name, and a tooltip with the class, what it does, how it is placed and how many stock
/// maps and this map use it. Drag a tile into a view to place it there by its class's rule (the views' drop pipeline,
/// <see cref="Viewports.AssetDropTarget"/>); click one to arm the place tool with it. Search filters by name, class
/// and description; right-click a tile to keep it in the favourites (starred, first in their group, and on a tab of
/// their own). The footer pins the selection as what the next placeables are wired to, as the selection moves to each
/// new actor (<see cref="Gameplay.PlaceableLanding.Pinned"/>).
/// </summary>
[Panel("Placeables", DockSide.Bottom, Order = 3)]
public partial class PlaceablesPanel : EditorPanel
{
    private const int FavouritesTab = 5;

    private static readonly string[] s_tabs =
        ["All", .. Enum.GetValues<PlaceableGroup>().Select(Placeables.GroupTitle), "Favourites"];

    private readonly List<PlaceableTile> _tiles = new();
    private TabBar _tabs = null!;
    private LineEdit _search = null!;
    private Label _count = null!;
    private VBoxContainer _groups = null!;
    private EmptyStateView _empty = null!;
    private DeferredAction _rebuild = null!;
    private DeferredAction _retip = null!;
    private Button _pin = null!;
    private Label _pinned = null!;
    private Button _unpin = null!;

    public static PlaceablesPanel? Instance { get; private set; }

    /// <summary>The favourite classes, kept in the settings.</summary>
    private static HashSet<string> Favourites => new(AppSettings.GetStrings("placeables", "favourites"), StringComparer.OrdinalIgnoreCase);

    protected override void Build()
    {
        Instance = this;
        _rebuild = new DeferredAction(Rebuild);
        _retip = new DeferredAction(Retip);
        HFlowContainer bar = PanelUi.Toolbar(this);
        _tabs = new TabBar { ClipTabs = false, ThemeTypeVariation = "SubTabs", FocusMode = FocusModeEnum.None };
        foreach (string t in s_tabs)
        {
            _tabs.AddTab(t);
        }

        _tabs.TabChanged += _ => _rebuild.Queue();
        bar.AddChild(_tabs);
        _search = PanelUi.Search(bar, "Search placeables", _ => _rebuild.Queue(),
            "Search names, classes and what they do: every word must match. Escape clears.");
        _search.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } && _search.Text.Length > 0)
            {
                _search.Clear();
                _rebuild.Queue();
                _search.AcceptEvent();
            }
        };
        _count = PanelUi.Caption(bar, "", wrap: false);
        _count.SizeFlagsVertical = SizeFlags.ShrinkCenter;

        var holder = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(holder);
        var area = new VBoxContainer();
        area.SetAnchorsPreset(LayoutPreset.FullRect);
        holder.AddChild(area);
        _groups = PanelUi.Scroll(area);
        _groups.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        _empty = (EmptyStateView)PanelUi.EmptyState(holder, "search", "", "");
        _empty.Visible = false;
        // One line, so the tiles keep the dock's height: the wiring pin, then the help (the whole of it is the tooltip).
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        AddChild(footer);
        _pin = new Button
        {
            Text = "Keep Wiring To Selection",
            Icon = Icons.Get("pin", EditorTheme.Text),
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = "Wire the next placeables to the selected actors (an alarm, a mission, an objective, a disk, a network camera)\n" +
                "while you place several: each new actor is selected after placing, so without this the next one is wired to it",
        };
        _pin.Pressed += PinSelection;
        footer.AddChild(_pin);
        _pinned = PanelUi.Caption(footer, "", wrap: false);
        _pinned.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _pinned.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _pinned.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        _pinned.AddThemeColorOverride("font_color", EditorTheme.Accent);
        _pinned.MouseFilter = MouseFilterEnum.Pass;
        _unpin = new Button
        {
            Icon = Icons.Get("close", EditorTheme.TextDim),
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = "Stop: wire new placeables to the selection again",
        };
        _unpin.Pressed += () => Gameplay.PlaceableLanding.Pin([]);
        footer.AddChild(_unpin);
        const string Hint = "Drag a tile into a view to place it, or click it to place with the place tool (P). " +
            "Select what it is wired to first: its mission or objective, the alarm it sets off, its disk, the network camera before it.";
        Label hint = PanelUi.Caption(footer, Hint, wrap: false);
        hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        hint.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        hint.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        hint.TooltipText = Hint;
        hint.MouseFilter = MouseFilterEnum.Pass;
        ShowPinned();

        AssetService.Instance.CatalogChanged += () => _rebuild.Queue();
        Context.ProjectChanged += _ => _retip.Queue();
        Context.SelectionChanged += ShowPinned;
        Gameplay.PlaceableLanding.PinnedChanged += ShowPinned;
        TreeExiting += () =>
        {
            Context.SelectionChanged -= ShowPinned;
            Gameplay.PlaceableLanding.PinnedChanged -= ShowPinned;
        };
    }

    /// <summary>Pins the selected actors as what the next placeables are wired to.</summary>
    private void PinSelection()
    {
        List<string> ids = Context.Selection.Items.Where(r => r.Kind == ObjectKind.Actor).Select(r => r.Id).ToList();
        Gameplay.PlaceableLanding.Pin(ids);
        Context.SetStatus(ids.Count == 0 ? "Select the actors to wire new placeables to first"
            : $"New placeables are wired to {string.Join(", ", ids.Take(3))}{(ids.Count > 3 ? $" and {ids.Count - 3} more" : "")} until you stop it");
    }

    /// <summary>The footer: what new placeables are wired to (the pinned actors), else the pin button.</summary>
    private void ShowPinned()
    {
        if (!IsInstanceValid(_pinned))
        {
            return;
        }

        IReadOnlyList<string> pinned = Gameplay.PlaceableLanding.PinnedIn(Context.Project);
        bool on = pinned.Count > 0;
        _pin.Visible = !on;
        _pin.Disabled = !Context.Selection.Items.Any(r => r.Kind == ObjectKind.Actor);
        _pinned.Visible = on;
        _unpin.Visible = on;
        if (on)
        {
            _pinned.Text = $"Wiring to: {string.Join(", ", pinned.Take(3))}{(pinned.Count > 3 ? $" +{pinned.Count - 3}" : "")}";
            _pinned.TooltipText = $"New placeables are wired to {string.Join(", ", pinned)}, whatever is selected";
        }
    }

    public override void Refresh() => _rebuild.Queue();

    /// <summary>Puts the typing focus in the search box (the palette command).</summary>
    public void FocusSearch()
    {
        _search.GrabFocus();
        _search.SelectAll();
    }

    private void Rebuild()
    {
        foreach (Node child in _groups.GetChildren())
        {
            _groups.RemoveChild(child);
            child.QueueFree();
        }

        _tiles.Clear();
        string query = _search.Text.Trim();
        HashSet<string> favourites = Favourites;
        List<Placeable> all = Placeables.Available(EditorServices.Classes).Where(p => query.Length == 0 || p.Matches(query)).ToList();
        int tab = _tabs.CurrentTab;
        var sections = new List<(string Title, List<Placeable> Items)>();
        List<Placeable> starred = all.Where(p => favourites.Contains(p.Class)).ToList();
        if (starred.Count > 0 && tab == FavouritesTab)
        {
            sections.Add(("Favourites", starred));
        }

        if (tab != FavouritesTab)
        {
            foreach (PlaceableGroup group in Enum.GetValues<PlaceableGroup>())
            {
                if (tab != 0 && (int)group != tab - 1)
                {
                    continue;
                }

                // Favourites first in their group (starred), the rest in the palette's order.
                List<Placeable> items = all.Where(p => p.Group == group).OrderBy(p => favourites.Contains(p.Class) ? 0 : 1).ToList();
                if (items.Count > 0)
                {
                    sections.Add((Placeables.GroupTitle(group), items));
                }
            }
        }

        foreach ((string title, List<Placeable> items) in sections)
        {
            // A group's own tab needs no heading: the tab names it.
            if (sections.Count > 1)
            {
                PanelUi.Header(_groups, title);
            }

            var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", EditorTheme.SpaceXs);
            flow.AddThemeConstantOverride("v_separation", EditorTheme.SpaceXs);
            _groups.AddChild(flow);
            foreach (Placeable p in items)
            {
                var tile = new PlaceableTile(p, favourites.Contains(p.Class), this);
                flow.AddChild(tile);
                _tiles.Add(tile);
            }
        }

        int shown = sections.Sum(s => s.Items.Count);
        _count.Text = PanelUi.Count(shown, query.Length > 0 ? "match" : "class", query.Length > 0 ? "matches" : "classes");
        _empty.Visible = sections.Count == 0;
        if (sections.Count == 0)
        {
            _empty.SetText(tab == FavouritesTab && query.Length == 0 ? "No favourites" : "No match",
                tab == FavouritesTab && query.Length == 0
                    ? "Right-click a tile and choose Add to Favourites to keep it here."
                    : $"No placeable class matches “{query}”{(tab == 0 ? "" : $" in {s_tabs[tab]}")}.");
        }

        Retip();
    }

    /// <summary>The tooltips count this map's uses: brought up to date after edits.</summary>
    private void Retip()
    {
        Dictionary<string, int> counts = Context.Project.Actors.GroupBy(a => a.Class, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (PlaceableTile tile in _tiles)
        {
            tile.Retip(counts.GetValueOrDefault(tile.Entry.Class));
        }

        ShowPinned();
    }

    /// <summary>A tile's click: the place tool, armed with the tile's class.</summary>
    internal void Arm(Placeable p)
    {
        if (ToolManager.Instance is not { } tools)
        {
            return;
        }

        tools.ArmPalette(p.Class);
        tools.Activate("place");
        tools.RefreshOptions();
        Context.SetStatus($"Place tool: click in a view to place {p.InSentence} {p.MountWords}");
    }

    internal void ToggleFavourite(Placeable p)
    {
        HashSet<string> favourites = Favourites;
        if (!favourites.Remove(p.Class))
        {
            favourites.Add(p.Class);
        }

        AppSettings.Set("placeables", "favourites", favourites.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        _rebuild.Queue();
    }

    /// <summary>Selects every actor of the class in the map.</summary>
    internal void SelectAll(Placeable p)
    {
        List<ObjectRef> found = Context.Project.Actors.Where(a => string.Equals(a.Class, p.Class, StringComparison.OrdinalIgnoreCase))
            .Select(a => ObjectRef.Actor(a.Id)).ToList();
        Context.Selection.Set(found);
        Context.SetStatus($"Selected {PanelUi.Count(found.Count, p.Title.ToLowerInvariant())}");
    }
}

/// <summary>
/// A tile of the placeables palette: the class's mesh picture (when it has a mesh) with its icon in the corner, else its
/// icon large, over its English name. Dragging it carries the placeable to the views; a click arms the place tool; a
/// right-click opens its menu. Lit on hover like a flat button.
/// </summary>
public sealed partial class PlaceableTile : PanelContainer
{
    private const int Picture = AssetThumbnails.Size;
    private const int Badge = EditorTheme.IconSize + EditorTheme.SpaceXs;

    /// <summary>A tile's width: the picture with room either side for a two-word name.</summary>
    private const int Width = Picture + EditorTheme.SpaceXxl + EditorTheme.SpaceL;

    /// <summary>A tooltip line's most characters: the description wraps there.</summary>
    private const int TooltipLine = 80;

    private readonly PlaceablesPanel? _panel;
    private readonly TextureRect _picture = null!;
    private readonly Label _caption = null!;
    private readonly bool _favourite;
    private readonly StyleBoxFlat _rest = EditorTheme.Box(Colors.Transparent, padX: EditorTheme.SpaceXs, padY: EditorTheme.SpaceXs);
    private readonly StyleBoxFlat _hover = EditorTheme.Box(EditorTheme.RowHover, padX: EditorTheme.SpaceXs, padY: EditorTheme.SpaceXs);

    /// <summary>A favourite's star, kept on top of the mesh picture's badge.</summary>
    private readonly TextureRect? _star;

    /// <summary>A left press on the tile that has not become a drag: its release is a click.</summary>
    private bool _pressed;

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public PlaceableTile()
    {
        Entry = Placeables.Stock[0];
    }

    public PlaceableTile(Placeable entry, bool favourite, PlaceablesPanel panel)
    {
        Entry = entry;
        _favourite = favourite;
        _panel = panel;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        CustomMinimumSize = new Vector2(Width, 0);
        AddThemeStyleboxOverride("panel", _rest);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        AddChild(box);
        var frame = new Control { CustomMinimumSize = new Vector2(Picture, Picture), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        box.AddChild(frame);
        _picture = new TextureRect
        {
            Texture = ActorIcons.For(Placeables.Kind(entry, EditorServices.Classes), EditorTheme.Text, EditorTheme.IconSizeTile),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _picture.SetAnchorsPreset(LayoutPreset.FullRect);
        frame.AddChild(_picture);

        // Two lines for every name, so the tiles of a row line up whether their names wrap or not.
        _caption = new Label
        {
            Text = entry.Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MaxLinesVisible = 2,
            LinesSkipped = 0,
            CustomMinimumSize = new Vector2(Width - 2 * EditorTheme.SpaceXs, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(_caption);
        if (favourite)
        {
            // A star in the picture's top-right corner (the kind badge takes the top-left).
            _star = new TextureRect
            {
                Texture = Icons.Get("star", EditorTheme.Accent),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                MouseFilter = MouseFilterEnum.Ignore,
                Position = new Vector2(Picture - Badge, 0),
                Size = new Vector2(Badge, Badge),
            };
            frame.AddChild(_star);
        }

        RequestPicture(frame);
    }

    public Placeable Entry { get; }

    /// <summary>The tooltip: name, class and group, what it does, how it is placed, where it is used.</summary>
    public void Retip(int inMap)
    {
        string uses = Entry.StockMaps > 0 ? $"On {Entry.StockMaps} of the {Placeables.StockMapCount} stock maps" : "A class of this map's catalog";
        TooltipText = $"{(_favourite ? "★ " : "")}{Entry.Title}\n{Entry.Class} · {Placeables.GroupTitle(Entry.Group)}\n\n" +
            $"{Wrap(Entry.Summary)}\n\n{Wrap($"Placed {Entry.MountWords}.")}\n{uses}; {PanelUi.Count(inMap, "in this map", "in this map")}.\n\n" +
            "Drag into a view to place it there; click to place it\nwith the place tool; right-click for more.";
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        _pressed = false;
        // The collision world the drop lands on starts building now, while the pointer travels to a view.
        Gameplay.GameplayState.Instance.Prewarm();
        return AssetDragData.StartPlaceable(this, Entry, _picture.Texture);
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                _pressed = true;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release:
                bool click = _pressed && new Rect2(Vector2.Zero, Size).HasPoint(release.Position);
                _pressed = false;
                if (click)
                {
                    AcceptEvent();
                    _panel?.Arm(Entry);
                }

                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } when _panel is { } panel:
                AcceptEvent();
                OpenMenu(panel);
                break;
        }
    }

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationMouseEnter:
                AddThemeStyleboxOverride("panel", _hover);
                break;
            case NotificationMouseExit:
                AddThemeStyleboxOverride("panel", _rest);
                break;
            case NotificationDragBegin:
                _pressed = false;
                break;
            case NotificationThemeChanged when _caption is not null:
                _caption.CustomMinimumSize = new Vector2(_caption.CustomMinimumSize.X, 2 * _caption.GetLineHeight());
                break;
        }
    }

    private void OpenMenu(PlaceablesPanel panel)
    {
        var menu = new PopupMenu();
        menu.AddItem("Place with the Place Tool", 0);
        menu.AddItem(_favourite ? "Remove from Favourites" : "Add to Favourites", 1);
        menu.AddItem("Select All in This Map", 2);
        menu.IdPressed += id =>
        {
            switch (id)
            {
                case 0:
                    panel.Arm(Entry);
                    break;
                case 1:
                    panel.ToggleFavourite(Entry);
                    break;
                default:
                    panel.SelectAll(Entry);
                    break;
            }
        };
        menu.PopupHide += () => menu.QueueFree();
        AddChild(menu);
        PanelUi.PopupAtMouse(this, menu);
    }

    /// <summary>Breaks a sentence into tooltip lines at word ends.</summary>
    private static string Wrap(string text)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > TooltipLine)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : line + " " + word;
            }
        }

        lines.Add(line);
        return string.Join("\n", lines);
    }

    /// <summary>The class's mesh picture, when it has a mesh the asset index knows: it takes the tile's middle, the
    /// class icon moves to its corner.</summary>
    private void RequestPicture(Control frame)
    {
        if (StaticMeshDrawing.MeshPath(new Actor("tile", Entry.Class)) is not { } path
            || AssetService.Instance.Catalog.Find(AssetKind.Mesh, path) is not { } mesh)
        {
            return;
        }

        AssetThumbnails.Instance?.Request(mesh, texture =>
        {
            if (!IsInstanceValid(this) || !IsInstanceValid(_picture))
            {
                return;
            }

            _picture.Texture = texture;
            _picture.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            var badge = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Position = Vector2.Zero, Size = new Vector2(Badge, Badge) };
            badge.AddThemeStyleboxOverride("panel", EditorTheme.Box(new Color(EditorTheme.Background, 0.8f), radius: EditorTheme.RadiusS, padX: 0, padY: 0));
            badge.AddChild(new TextureRect
            {
                Texture = ActorIcons.For(Placeables.Kind(Entry, EditorServices.Classes), EditorTheme.Text),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            });
            frame.AddChild(badge);
            if (_star is not null && IsInstanceValid(_star))
            {
                frame.MoveChild(_star, -1);
            }
        }, () => IsInstanceValid(this) && IsInsideTree());
    }
}
