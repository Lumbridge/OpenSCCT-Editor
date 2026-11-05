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
/// and description; right-click a tile to keep it in the favourites, which come first.
/// </summary>
[Panel("Placeables", DockSide.Bottom, Order = 3)]
public partial class PlaceablesPanel : EditorPanel
{
    private const int FavouritesTab = 5;

    private static readonly string[] s_tabs = ["All", "Security", "Objectives", "Stations", "Interactive", "Favourites"];

    private readonly List<PlaceableTile> _tiles = new();
    private TabBar _tabs = null!;
    private LineEdit _search = null!;
    private Label _count = null!;
    private VBoxContainer _groups = null!;
    private EmptyStateView _empty = null!;
    private DeferredAction _rebuild = null!;
    private DeferredAction _retip = null!;

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
        // One line, so the tiles keep the dock's height; the whole of it is the tooltip.
        const string Hint = "Drag a tile into a view to place it, or click it to place with the place tool (P). " +
            "Select a mission, objective, disk, alarm or network camera first to wire the new actor to it.";
        Label hint = PanelUi.Caption(this, Hint, wrap: false);
        hint.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        hint.TooltipText = Hint;
        hint.MouseFilter = MouseFilterEnum.Pass;

        AssetService.Instance.CatalogChanged += () => _rebuild.Queue();
        Context.ProjectChanged += _ => _retip.Queue();
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
        if (starred.Count > 0 && (tab == 0 || tab == FavouritesTab))
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

                List<Placeable> items = all.Where(p => p.Group == group).ToList();
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

        int shown = sections.Where(s => s.Title != "Favourites" || tab == FavouritesTab).Sum(s => s.Items.Count);
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
        MapProject p = Context.Project;
        foreach (PlaceableTile tile in _tiles)
        {
            tile.Retip(p.Actors.Count(a => string.Equals(a.Class, tile.Entry.Class, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>A tile's click: the place tool, armed with the tile's class.</summary>
    internal void Arm(Placeable p)
    {
        if (ToolManager.Instance is not { } tools)
        {
            return;
        }

        tools.PlaceClass = p.Class;
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
            Texture = ActorIcons.For(entry.Class, EditorTheme.Text, EditorTheme.IconSizeTile),
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
        if (favourite)
        {
            _caption.AddThemeColorOverride("font_color", EditorTheme.Accent);
        }

        box.AddChild(_caption);
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
                Texture = ActorIcons.For(Entry.Class, EditorTheme.Text),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            });
            frame.AddChild(badge);
        }, () => IsInstanceValid(this) && IsInsideTree());
    }
}
