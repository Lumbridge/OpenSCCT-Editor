using System.Reflection;
using Godot;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;

namespace OpenScctEditor.Shell;

/// <summary>
/// The editor window: menu bar, toolbar, the viewport area in the middle, docks left, right and bottom, and a status
/// bar. Panels ([Panel] classes) are found by reflection and docked by their default side.
/// </summary>
public partial class MainWindow : Control
{
    /// <summary>The narrowest a side dock or the right column can be dragged.</summary>
    private const int MinDockWidth = 200;

    /// <summary>The lowest the bottom dock (or one of the right column's docks) can be dragged.</summary>
    private const int MinDockHeight = 120;

    private readonly HashSet<SplitContainer> _userSized = new();

    /// <summary>The workbench panels' tabs: in the workbench dock, or in the Inspector's dock on a short window.</summary>
    private readonly List<PanelHost> _workbenchPanels = new();
    private Label? _status;
    private bool _simpleBottomShown;
    private HSplitContainer _outer = null!;
    private HSplitContainer _inner = null!;
    private VSplitContainer _middle = null!;
    private VSplitContainer _right = null!;

    /// <summary>The window, once ready (dialogs and parts that need the tree use it).</summary>
    public static MainWindow? Instance { get; private set; }

    /// <summary>Raised when the user closes the window. The quit waits until a handler calls <see cref="Quit"/>
    /// (the document part asks about unsaved changes first); with no handler the editor quits at once.</summary>
    public event Action? CloseRequested;

    /// <summary>The status bar row; the main status text is first, parts add their own fields after it.</summary>
    public HBoxContainer StatusRow { get; private set; } = null!;

    /// <summary>The middle area; the viewport layout fills it.</summary>
    public Control ViewportArea { get; private set; } = null!;

    public TabContainer LeftDock { get; private set; } = null!;

    /// <summary>The Inspector column (Inspector, Map, Identity, Surface, Bookmarks).</summary>
    public TabContainer RightDock { get; private set; } = null!;

    /// <summary>The workbench column under the Inspector (Match, Mover, Camera Network...); hidden while empty.</summary>
    public TabContainer WorkbenchDock { get; private set; } = null!;

    public TabContainer BottomDock { get; private set; } = null!;

    /// <summary>Every dock, left to right and top to bottom.</summary>
    public IReadOnlyList<TabContainer> Docks => [LeftDock, RightDock, WorkbenchDock, BottomDock];

    /// <summary>The menu bar row (the shell's menus go here).</summary>
    public HBoxContainer MenuRow { get; private set; } = null!;

    /// <summary>The toolbar under the menus.</summary>
    public HBoxContainer ToolRow { get; private set; } = null!;

    /// <summary>The active tool's options and the typed transform values: beside the toolbar when the window is wide
    /// enough, else on a line of their own under it.</summary>
    public HBoxContainer OptionsRow { get; private set; } = null!;

    public override void _Ready()
    {
        // The updater starting a new build once to see that it runs: answer and quit before anything else.
        if (Updates.UpdatePart.AnswerProbe(GetTree()))
        {
            return;
        }

        Instance = this;
        WorkspaceMode.Initialize();
        Dialogs.Host = this;
        GetTree().AutoAcceptQuit = false;
        // A content builder run (build, export, import) is stopped, with its processes, when the editor quits.
        SvmbuildRunner.Stopping = EditorLifetime.Token;
        EditorServices.Discover(typeof(MainWindow).Assembly);
        EditorContext ctx = EditorContext.Instance;
        ctx.Init();
        Theme = EditorTheme.Create();

        // The window background shows in the gaps between docks; bars and docks sit on it.
        var backdrop = new Panel { Name = "Backdrop", ThemeTypeVariation = "Backdrop", MouseFilter = MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var root = new VBoxContainer { Name = "Root" };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        MenuRow = Row(root, "MenuRow", "TopBar");
        var toolBar = new PanelContainer { Name = "ToolRowBar", ThemeTypeVariation = "Bar" };
        root.AddChild(toolBar);
        var toolFlow = new HFlowContainer { Name = "ToolFlow" };
        toolFlow.AddThemeConstantOverride("h_separation", EditorTheme.SpaceXl);
        toolFlow.AddThemeConstantOverride("v_separation", EditorTheme.SpaceXxs);
        toolBar.AddChild(toolFlow);
        ToolRow = new HBoxContainer { Name = "ToolRow" };
        toolFlow.AddChild(ToolRow);
        OptionsRow = new HBoxContainer { Name = "OptionsRow", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        toolFlow.AddChild(OptionsRow);

        var docks = new MarginContainer { Name = "Docks", SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (string side in new[] { "left", "top", "right", "bottom" })
        {
            docks.AddThemeConstantOverride("margin_" + side, EditorTheme.SpaceXs);
        }

        root.AddChild(docks);
        _outer = new HSplitContainer { Name = "Outer" };
        docks.AddChild(_outer);
        LeftDock = Dock("LeftDock", new Vector2(MinDockWidth, 0));
        _outer.AddChild(LeftDock);

        _inner = new HSplitContainer { Name = "Inner", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _outer.AddChild(_inner);
        _middle = new VSplitContainer { Name = "Middle", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _inner.AddChild(_middle);
        ViewportArea = new Control { Name = "ViewportArea", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _middle.AddChild(ViewportArea);
        BottomDock = Dock("BottomDock", new Vector2(0, MinDockHeight));
        _middle.AddChild(BottomDock);

        // The right column: the Inspector's dock over the workbenches' dock, the Inspector with the larger share.
        var right = new VSplitContainer { Name = "RightColumn", CustomMinimumSize = new Vector2(MinDockWidth, 0) };
        _right = right;
        _inner.AddChild(right);
        RightDock = Dock("RightDock", new Vector2(0, MinDockHeight));
        RightDock.SizeFlagsVertical = SizeFlags.ExpandFill;
        RightDock.SizeFlagsStretchRatio = 1 - EditorTheme.DockWorkbenchShare;
        right.AddChild(RightDock);
        WorkbenchDock = Dock("WorkbenchDock", new Vector2(0, MinDockHeight));
        WorkbenchDock.SizeFlagsVertical = SizeFlags.ExpandFill;
        WorkbenchDock.SizeFlagsStretchRatio = EditorTheme.DockWorkbenchShare;
        right.AddChild(WorkbenchDock);

        // Docks start in proportion to the window and follow its size until the user drags a splitter: from then on
        // that splitter is theirs.
        foreach (SplitContainer split in new SplitContainer[] { _outer, _inner, _middle, _right })
        {
            split.DragStarted += () => _userSized.Add(split);
        }

        GetViewport().SizeChanged += () => EditorLifetime.Defer(SizeDocks);
        EditorLifetime.Defer(SizeDocks);

        StatusRow = Row(root, "StatusRow", "StatusBar");
        StatusRow.AddThemeConstantOverride("separation", EditorTheme.SpaceM);
        _status = new Label
        {
            Name = "Status",
            Text = "Ready",
            ThemeTypeVariation = "StatusText",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Pass,
        };
        StatusRow.AddChild(_status);
        // Fields other parts add (live editing's) get the status bar's look: small text after a hairline.
        StatusRow.ChildEnteredTree += node =>
        {
            if (node is Label field && field != _status && ((string)field.ThemeTypeVariation).Length == 0)
            {
                field.ThemeTypeVariation = "StatusField";
            }
        };
        ctx.StatusChanged += t =>
        {
            _status.Text = t;
            _status.TooltipText = t;
        };

        CreatePanels(ctx);
        foreach (IShellPart part in FindParts())
        {
            part.Attach(this, ctx);
            // A node part with nothing to do here (the shot runner without --shots) stays out of the tree: free it, or
            // it leaks at exit.
            if (part is Node idle && !idle.IsInsideTree())
            {
                idle.Free();
            }
        }
    }

    public override void _Notification(int what)
    {
        // A close while the editor is already quitting changes nothing.
        if (what != NotificationWMCloseRequest || EditorLifetime.IsQuitting)
        {
            return;
        }

        if (CloseRequested is null)
        {
            Quit();
        }
        else
        {
            CloseRequested.Invoke();
        }
    }

    /// <summary>Quits the editor: background work is stopped and its results dropped first (see
    /// <see cref="EditorLifetime"/>).</summary>
    public void Quit() => EditorLifetime.Quit(GetTree());

    /// <summary>The tree is going, by whatever route (also Godot's own <c>--quit-after</c>): no background job may call
    /// the engine from here on.</summary>
    public override void _ExitTree() => EditorLifetime.TreeExiting();

    /// <summary>A full-width bar (menus, toolbar, tool options, status): a styled panel holding a row. The row never
    /// widens the window: what does not fit is clipped at the right.</summary>
    private static HBoxContainer Row(Container parent, string name, string style)
    {
        var bar = new PanelContainer { Name = name + "Bar", ThemeTypeVariation = style };
        parent.AddChild(bar);
        var row = new HBoxContainer { Name = name };
        bar.AddChild(new PanelHost(row, keepHeight: true) { Name = name + "Clip" });
        return row;
    }

    /// <summary>A dock: tabs that clip instead of pushing the dock wider (icon-only when their titles do not fit, see
    /// <see cref="DockTabs"/>), and an overflow menu listing every panel in it.</summary>
    private static TabContainer Dock(string name, Vector2 minimum)
    {
        var dock = new TabContainer { Name = name, CustomMinimumSize = minimum, ClipTabs = true };
        var overflow = new PopupMenu { Name = "Panels" };
        PanelUi.CapPopup(overflow);
        overflow.AboutToPopup += () =>
        {
            overflow.Clear();
            // Every panel, also the ones the tab row has no room for (DockTabs.Fit hides those): this menu is how
            // they are reached.
            for (int i = 0; i < dock.GetTabCount(); i++)
            {
                if (!WorkspaceMode.Allows(DockTabs.Name(dock, i))) continue;
                overflow.AddRadioCheckItem(DockTabs.Name(dock, i), i);
                overflow.SetItemIcon(i, dock.GetTabIcon(i));
                overflow.SetItemChecked(i, i == dock.CurrentTab);
            }
        };
        overflow.IdPressed += id => DockTabs.Show(dock, (int)id);
        MenuToggles.KeepOpen(overflow, () =>
        {
            for (int i = 0; i < overflow.ItemCount; i++)
            {
                overflow.SetItemChecked(i, overflow.GetItemId(i) == dock.CurrentTab);
            }
        });
        // The popup has to be in the tree to open; as an internal child it is no tab and no panel.
        dock.AddChild(overflow, @internal: InternalMode.Front);
        dock.SetPopup(overflow);
        DockTabs.Track(dock);
        return dock;
    }

    /// <summary>Sizes the docks from the window (see <see cref="EditorTheme.DockLeft"/>), except splitters the user
    /// has dragged. On a short window the workbench panels join the Inspector's dock; the workbench dock shows only
    /// when a panel docks there.</summary>
    private void SizeDocks()
    {
        Vector2 canvas = GetViewportRect().Size;
        TabContainer workbenches = canvas.Y < EditorTheme.CompactCanvasHeight ? RightDock : WorkbenchDock;
        foreach (PanelHost host in _workbenchPanels.Where(h => h.GetParent() != workbenches))
        {
            int open = workbenches.CurrentTab;
            host.GetParent().RemoveChild(host);
            workbenches.AddChild(host);
            workbenches.CurrentTab = Math.Max(open, 0);
        }

        if (!_userSized.Contains(_outer))
        {
            _outer.SplitOffsets = [EditorTheme.DockLeft(canvas.X)];
        }

        if (!_userSized.Contains(_inner))
        {
            // The views expand and the right column does not, so the split sits at the end, less the column.
            _inner.SplitOffsets = [-EditorTheme.DockRight(canvas.X)];
        }

        if (!_userSized.Contains(_middle))
        {
            _middle.SplitOffsets = [-EditorTheme.DockBottom(canvas.Y)];
        }

        UpdateWorkspaceDocks();
    }

    public void UpdateWorkspaceDocks()
    {
        foreach (TabContainer dock in Docks)
            dock.Visible = Enumerable.Range(0, dock.GetTabCount()).Any(i => WorkspaceMode.Allows(DockTabs.Name(dock, i))) &&
                (dock != BottomDock || !WorkspaceMode.Simple || _simpleBottomShown);
    }

    public void RevealWorkspaceDock(TabContainer dock)
    {
        if (dock == BottomDock) _simpleBottomShown = true;
        dock.Show();
    }

    public void SaveWorkspaceLayout(string mode)
    {
        string section = "workspace." + mode;
        AppSettings.Set(section, "bottom_visible", BottomDock.Visible);
        AppSettings.Set(section, "view", Viewports.ViewportLayout.Instance?.Single?.ToString() ?? "");
        int i = 0;
        foreach (SplitContainer split in new SplitContainer[] { _outer, _inner, _middle, _right })
            AppSettings.Set(section, "split" + i++, split.SplitOffsets.FirstOrDefault());
        foreach (TabContainer dock in Docks)
            if (dock.CurrentTab >= 0) AppSettings.Set(section, dock.Name, DockTabs.Name(dock, dock.CurrentTab));
    }

    public void RestoreWorkspaceLayout(string mode)
    {
        string section = "workspace." + mode;
        _simpleBottomShown = AppSettings.GetBool(section, "bottom_visible", false);
        if (AppSettings.Has(section, "view"))
        {
            string view = AppSettings.GetString(section, "view", "");
            Viewports.ViewportLayout.Instance?.SetSingle(Enum.TryParse(view, out Viewports.ViewKind kind) ? kind : null);
        }
        else if (mode == "simple") Viewports.ViewportLayout.Instance?.SetSingle(Viewports.ViewKind.Perspective);
        int i = 0;
        foreach (SplitContainer split in new SplitContainer[] { _outer, _inner, _middle, _right })
        {
            string key = "split" + i++;
            _userSized.Remove(split);
            if (AppSettings.Has(section, key))
            {
                split.SplitOffsets = [AppSettings.GetInt(section, key, 0)];
                _userSized.Add(split);
            }
        }
        SizeDocks();
        foreach (TabContainer dock in Docks)
        {
            if (dock == BottomDock && WorkspaceMode.Simple && !_simpleBottomShown) continue;
            string selected = AppSettings.GetString(section, dock.Name, "");
            for (int n = 0; n < dock.GetTabCount(); n++)
                if (DockTabs.Name(dock, n) == selected && WorkspaceMode.Allows(selected)) DockTabs.Show(dock, n);
        }
    }

    private void CreatePanels(EditorContext ctx)
    {
        var panels = typeof(MainWindow).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(EditorPanel).IsAssignableFrom(t) && t.GetCustomAttribute<PanelAttribute>() is not null)
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<PanelAttribute>()!))
            .OrderBy(x => x.Attr.Order)
            .ThenBy(x => x.Attr.Title, StringComparer.Ordinal);
        foreach ((Type type, PanelAttribute attr) in panels)
        {
            var panel = (EditorPanel)Activator.CreateInstance(type)!;
            panel.Name = attr.Title;
            TabContainer dock = attr.Side switch
            {
                DockSide.Left => LeftDock,
                DockSide.Right => RightDock,
                DockSide.Workbench => WorkbenchDock,
                _ => BottomDock,
            };
            var host = new PanelHost(panel);
            dock.AddChild(host);
            if (attr.Side == DockSide.Workbench)
            {
                _workbenchPanels.Add(host);
            }

            panel.Bind(ctx);
        }
    }

    private static IEnumerable<IShellPart> FindParts() =>
        typeof(MainWindow).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IShellPart).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.GetCustomAttribute<ShellPartAttribute>()?.Order ?? 0)
            .ThenBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IShellPart)Activator.CreateInstance(t)!);
}

/// <summary>Orders a shell part (lower attaches first).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ShellPartAttribute : Attribute
{
    public int Order { get; init; }
}

/// <summary>A piece of the shell that attaches itself to the main window at startup (menus, toolbar, viewport
/// layout, shortcuts, autosave...). Found by reflection, so features add parts without editing the window.</summary>
public interface IShellPart
{
    void Attach(MainWindow window, EditorContext context);
}
