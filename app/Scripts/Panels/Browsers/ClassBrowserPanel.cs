using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>
/// The placeable classes as a tree under their parents (abstract and non-placeable parents shown dim), with fuzzy
/// search that lists matches flat. Double-click or "Place" puts the selected class at the view centre, snapped to the
/// grid. With no class catalog it lists the classes the open map already uses.
/// </summary>
[Panel("Classes", DockSide.Left, Order = 2)]
public partial class ClassBrowserPanel : EditorPanel
{
    private const double FallbackDistance = 512;

    private Tree _tree = null!;
    private LineEdit _search = null!;
    private Label _info = null!;
    private Label _source = null!;
    private Button _place = null!;
    private string? _selected;
    private DeferredAction _rebuild = null!;

    protected override void Build()
    {
        _rebuild = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        _search = PanelUi.Search(bar, "Search classes…", _ => _rebuild.Queue(), "Search the class names; several words narrow it down");
        _place = PanelUi.Button(bar, "Place", "Place the selected class at the view centre", Place);
        _place.Disabled = true;
        _source = PanelUi.Caption(this, "", wrap: false);
        _tree = new Tree { HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Single };
        _tree.ItemSelected += () =>
        {
            _selected = _tree.GetSelected()?.GetMetadata(0).AsString();
            _place.Disabled = _selected is null;
            ShowInfo();
        };
        _tree.ItemActivated += Place;
        AddChild(_tree);
        _info = PanelUi.Caption(this, "");

        // Without a catalog the list is the classes the map uses, so it follows edits.
        Context.ProjectChanged += _ =>
        {
            if (EditorServices.Classes is null)
            {
                _rebuild.Queue();
            }
        };
        Context.AddAction(new EditorAction("classes.place", "Place Selected Class", "Edit", Place)
        {
            Order = 900, Description = "Place the class chosen in the Classes panel at the view centre", IsEnabled = () => _selected is not null,
        });
        Context.AddAction(new EditorAction("classes.search", "Search Classes", "View", () => _search.GrabFocus())
        {
            Order = 910, Description = "Search the placeable classes",
        });
    }

    public override void Refresh() => _rebuild.Queue();

    private void Rebuild()
    {
        IClassCatalog? catalog = EditorServices.Classes;
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        string query = _search.Text.Trim();
        if (catalog is null)
        {
            BuildFallback(root, query);
            return;
        }

        var placeable = catalog.All.Where(c => c.Placeable && !c.Abstract).ToList();
        // Where the catalog came from is a long path: it goes in the tooltip, so it never sets the dock's width.
        _source.Text = $"{placeable.Count} placeable classes";
        _source.TooltipText = catalog.SourceDescription;
        if (query.Length > 0)
        {
            foreach ((ClassInfo c, int _) in placeable
                         .Select(c => (Class: c, Score: TextMatch.FuzzyWords(query, c.Name)))
                         .Where(x => x.Score is not null)
                         .Select(x => (x.Class, x.Score!.Value))
                         .OrderByDescending(x => x.Value).ThenBy(x => x.Class.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(300))
            {
                AddClass(root, c, placeable: true);
            }

            SelectFirst(root);
            return;
        }

        // The hierarchy: every placeable class plus the parents that connect it to its root.
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClassInfo c in placeable)
        {
            foreach (ClassInfo link in catalog.Chain(c.Name))
            {
                if (!shown.Add(link.Name))
                {
                    break;
                }
            }
        }

        var children = catalog.All.Where(c => shown.Contains(c.Name))
            .GroupBy(c => c.Super ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.ShortName, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (ClassInfo top in catalog.All.Where(c => shown.Contains(c.Name) && (c.Super is null || !shown.Contains(c.Super))))
        {
            AddTree(root, top, children, depth: 0);
        }
    }

    private void AddTree(TreeItem parent, ClassInfo c, Dictionary<string, List<ClassInfo>> children, int depth)
    {
        TreeItem item = AddClass(parent, c, c.Placeable && !c.Abstract);
        item.Collapsed = depth >= 2;
        foreach (ClassInfo child in children.GetValueOrDefault(c.Name) ?? [])
        {
            AddTree(item, child, children, depth + 1);
        }
    }

    private TreeItem AddClass(TreeItem parent, ClassInfo c, bool placeable)
    {
        TreeItem item = _tree.CreateItem(parent);
        item.SetText(0, c.ShortName);
        item.SetIcon(0, PanelIcons.Type(c.Name));
        if (!placeable)
        {
            item.SetIconModulate(0, EditorTheme.TextHidden);
        }

        item.SetMetadata(0, c.Name);
        item.SetTooltipText(0, $"{c.Name}\nParent: {c.Super ?? "none"}{(c.Abstract ? "\nAbstract" : "")}{(placeable ? "" : "\nNot placeable")}");
        item.SetCustomColor(0, placeable ? EditorTheme.Text : EditorTheme.TextDim);
        if (!placeable)
        {
            item.SetSelectable(0, false);
        }

        if (string.Equals(c.Name, _selected, StringComparison.OrdinalIgnoreCase))
        {
            item.Select(0);
        }

        return item;
    }

    private void BuildFallback(TreeItem root, string query)
    {
        var used = Context.Project.Actors.Select(a => a.Class).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => query.Length == 0 || TextMatch.FuzzyWords(query, c) is not null)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        _source.Text = "No class catalog: classes this map uses";
        _source.TooltipText = "";
        foreach (string cls in used)
        {
            TreeItem item = _tree.CreateItem(root);
            item.SetText(0, cls[(cls.LastIndexOf('.') + 1)..]);
            item.SetIcon(0, PanelIcons.Type(cls));
            item.SetMetadata(0, cls);
            item.SetTooltipText(0, cls);
        }

        SelectFirst(root);
    }

    private void SelectFirst(TreeItem root)
    {
        if (_search.Text.Trim().Length > 0 && root.GetFirstChild() is { } first && _tree.GetSelected() is null)
        {
            first.Select(0);
        }
    }

    private void ShowInfo()
    {
        if (_selected is null)
        {
            _info.Text = "";
            return;
        }

        IClassCatalog? catalog = EditorServices.Classes;
        if (catalog?.Find(_selected) is not { } c)
        {
            _info.Text = _selected;
            return;
        }

        string chain = string.Join(" → ", catalog.Chain(c.Name).Select(x => x.ShortName));
        int editable = catalog.AllProperties(c.Name).Count(p => p.Editable);
        _info.Text = $"{c.Name}\n{chain}\n{editable} editable properties";
    }

    /// <summary>Adds an actor of the selected class standing on the surface at the view centre (snapped to the grid
    /// across it, like the asset browser's meshes) and selects it. Brush-based classes (volumes) get a 256 cube shape
    /// to start from.</summary>
    private void Place()
    {
        if (_selected is not { } cls)
        {
            Context.SetStatus("Select a class to place first.");
            return;
        }

        MapProject p = Context.Project;
        IClassCatalog? catalog = EditorServices.Classes;
        ClassInfo? info = catalog?.Find(cls);
        if (info is { Placeable: false } or { Abstract: true })
        {
            Context.Log($"{cls} cannot be placed (abstract or not placeable).", Severity.Warning);
            return;
        }

        string full = info?.Name ?? cls;
        string shortName = full[(full.LastIndexOf('.') + 1)..];
        var actor = new Actor(p.NewId(shortName), full);
        if (catalog is not null && info is not null && catalog.IsA(full, "Engine.Brush"))
        {
            actor.Shape = Box.Shape(new Vec3(256, 256, 256));
        }

        // Snap across the floor only and lift the actor by its extent below its origin, so it stands on the surface.
        Vec3 point = PlacementPoint(p);
        double below = -ObjectGeometry.ActorBounds(actor).Min.Z;
        Vec3 at = point.Snap(p.Workspace.GridSize).With(2, point.Z + below);
        actor.Location = at;
        MoverBase.Place(actor);
        NewLights.Place(actor);

        Context.Execute(new AddObjectsCommand($"Place {shortName}", actors: [actor]));
        Context.Selection.Set([ObjectRef.Actor(actor.Id)]);
        Context.Log($"Placed {actor.Id} at {at}.");
    }

    private Vec3 PlacementPoint(MapProject p)
    {
        if (Context.PlacementPoint is { } point)
        {
            return point();
        }

        Workspace w = p.Workspace;
        return w.CameraLocation + w.CameraRotation.Axes().Forward * FallbackDistance;
    }
}
