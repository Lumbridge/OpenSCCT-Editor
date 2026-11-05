using System.Globalization;
using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell.Shots;

/// <summary>
/// The built-in screenshot tours, made at run time from what is registered (menus from the actions, every docked
/// panel, every tool, layout and render mode, one object of each class in the open map), so a new panel, tool or menu
/// is in the next tour without editing it. <c>tour:quick</c> is the startup screen, the docks and two menus;
/// <c>tour:full</c> is everything, then the shell again at other window sizes and UI scales. A tour name may pin the
/// window: <c>tour:full@1280x720</c> or <c>tour:full@1920x1080@1.5</c> (with a UI scale) runs the whole tour at that
/// size and scale, without the closing round of sizes.
/// </summary>
public static class ShotTour
{
    public static SvJsonArray Build(string spec, EditorContext ctx, MainWindow window)
    {
        var t = new Tour();
        string[] parts = spec.Split('@');
        string name = parts[0];
        bool pinned = parts.Length > 1;
        if (pinned)
        {
            Pin(t, parts);
        }

        t.Shot("startup", "Startup, four views, nothing selected", views: true);
        if (name == "quick")
        {
            Panels(t, window);
            t.Menu("File");
            t.Menu("View");
            return t.Steps;
        }

        if (name != "full")
        {
            throw new ApiException($"no tour '{name}' (tours: quick, full)");
        }

        Menus(t, ctx);
        Toolbar(t, window);
        Windows(t);
        Views(t, ctx);
        Tools(t, ctx);
        ToolStates(t, ctx, window);
        t.Step("select", ("what", ApiJson.Str("none")));
        Panels(t, window);
        Workbench(t, ctx, window);
        States(t, ctx, window);
        AssetKinds(t, window);
        AssetDrop(t, window);
        Inspector(t, ctx, window);
        Dialogs(t, ctx);
        if (!pinned)
        {
            Sizes(t);
        }

        return t.Steps;
    }

    /// <summary>The window size and UI scale of a pinned tour: <c>@1280x720</c>, then an optional <c>@1.5</c>.</summary>
    private static void Pin(Tour t, string[] parts)
    {
        string[] wh = parts[1].Split('x');
        if (wh.Length != 2 || !int.TryParse(wh[0], out int w) || !int.TryParse(wh[1], out int h))
        {
            throw new ApiException($"a tour size is WIDTHxHEIGHT, not '{parts[1]}'");
        }

        double scale = 1;
        if (parts.Length > 2 && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
        {
            throw new ApiException($"a tour UI scale is a number, not '{parts[2]}'");
        }

        t.Step("size", ("w", SvJsonNode.Integer(w)), ("h", SvJsonNode.Integer(h)));
        t.Step("scale", ("factor", ApiJson.Num(scale)));
        t.Step("wait", ("frames", SvJsonNode.Integer(10)));
    }

    private static void Menus(Tour t, EditorContext ctx)
    {
        var tops = ctx.Actions.Where(a => a.Menu.Length > 0).Select(a => a.Menu.Split('/')[0]).Distinct().ToList();
        foreach (string top in tops.Order(StringComparer.Ordinal))
        {
            t.Menu(top);
        }

        foreach (string sub in ctx.Actions.Select(a => a.Menu.Split('/')).Where(p => p.Length >= 2).Select(p => p[0] + "/" + p[1])
                     .Distinct().Order(StringComparer.Ordinal))
        {
            t.Menu(sub);
        }
    }

    private static void Toolbar(Tour t, MainWindow window)
    {
        int n = 0;
        foreach (ActionMenuButton menu in Descendants(window.ToolRow).OfType<ActionMenuButton>())
        {
            t.Step("click", ("at", ApiJson.Str(PathOf(menu))), ("frames", SvJsonNode.Integer(3)));
            t.Shot($"toolbar-menu-{++n}", "Toolbar drop-down " + menu.Text);
            t.Step("escape");
        }

        t.Step("hover", ("at", ApiJson.Str("action:tools.move")));
        t.Shot("tooltip-toolbar", "Tooltip on a tool button");
        t.Step("hover", ("at", ApiJson.Str("action:build.build")));
        t.Shot("tooltip-build", "Tooltip on Build");
        t.Step("escape");
        t.Step("run", ("action", ApiJson.Str("view.commandPalette")));
        t.Shot("palette-empty", "Command palette");
        t.Step("type", ("text", ApiJson.Str("brush")));
        t.Shot("palette-brush", "Command palette, searching 'brush'");
        t.Step("escape");
        t.Step("run", ("action", ApiJson.Str("help.shortcuts")));
        t.Shot("shortcuts", "Keyboard shortcuts (F1)");
        t.Step("escape");
    }

    private static void Views(Tour t, EditorContext ctx)
    {
        foreach (string layout in new[] { "perspective", "top", "front", "side", "quad" })
        {
            t.Step("run", ("action", ApiJson.Str("view.layout." + layout)));
            t.Shot("layout-" + layout, "Layout: " + layout, views: true);
        }

        foreach (string mode in new[] { "wireframe", "brushColors", "unlit", "lit", "lightmapDensity" })
        {
            t.Step("render", ("mode", ApiJson.Str(mode)));
            t.Shot("render-" + mode, "Every view in " + mode, views: true);
        }

        t.Step("render", ("mode", ApiJson.Str("unlit")), ("view", ApiJson.Str("Perspective")));
        t.Step("render", ("mode", ApiJson.Str("wireframe")), ("view", ApiJson.Str("Top")));
        t.Step("render", ("mode", ApiJson.Str("wireframe")), ("view", ApiJson.Str("Front")));
        t.Step("render", ("mode", ApiJson.Str("wireframe")), ("view", ApiJson.Str("Side")));
        t.Step("rightClick", ("at", ApiJson.Str("view:Top")));
        t.Shot("context-empty", "View context menu, nothing selected");
        t.Step("escape");
        if (ctx.Project.Brushes.Count > 0)
        {
            t.Step("select", ("what", ApiJson.Str("brush")), ("frame", ApiJson.Bool(true)));
            t.Step("rightClick", ("at", ApiJson.Str("view:Top")));
            t.Shot("context-brush", "View context menu with a brush selected");
            t.Step("escape");
        }

        // Fly mode polls the keyboard (Input.IsPhysicalKeyPressed): the manifest's camera must move between these two.
        t.Shot("fly-before", "Perspective view before flying");
        t.Step("press", ("at", ApiJson.Str("view:Perspective")), ("button", ApiJson.Str("right")));
        t.Step("hold", ("key", ApiJson.Str("W")), ("down", ApiJson.Bool(true)), ("frames", SvJsonNode.Integer(20)));
        t.Step("hold", ("key", ApiJson.Str("W")), ("down", ApiJson.Bool(false)));
        t.Step("release", ("button", ApiJson.Str("right")));
        t.Shot("fly-after", "After flying forward (right mouse held, W)", views: true);

        t.Step("click", ("at", ApiJson.Str("view:Perspective")), ("offset", ApiJson.Arr([ApiJson.Num(0.02), ApiJson.Num(0.02)])));
        t.Step("click", ("at", ApiJson.Str("view:Perspective")), ("offset", ApiJson.Arr([ApiJson.Num(0.05), ApiJson.Num(0.03)])));
        t.Shot("view-menu", "A view's own menu");
        t.Step("escape");
    }

    private static void Tools(Tour t, EditorContext ctx)
    {
        if (ToolManager.Instance is not { } tools)
        {
            return;
        }

        if (ctx.Project.Brushes.Count > 0)
        {
            t.Step("select", ("what", ApiJson.Str("brush")), ("frame", ApiJson.Bool(true)));
        }

        foreach (IViewportTool tool in tools.Tools)
        {
            t.Step("tool", ("id", ApiJson.Str(tool.Id)));
            t.Shot("tool-" + tool.Id, $"{tool.Title} tool, a brush selected", views: true);
        }

        t.Step("tool", ("id", ApiJson.Str("move")));
        t.Step("drag", ("at", ApiJson.Str("view:Top")), ("to", ApiJson.Str("view:Top")),
            ("toOffset", ApiJson.Arr([ApiJson.Num(0.62), ApiJson.Num(0.45)])), ("hold", ApiJson.Bool(true)));
        t.Shot("drag-in-progress", "Move tool while dragging in the top view", views: true);
        t.Step("escape");
        t.Step("tool", ("id", ApiJson.Str("select")));
    }

    /// <summary>The windows the editor opens over the main one: Preferences (each page) and About.</summary>
    private static void Windows(Tour t)
    {
        t.Step("run", ("action", ApiJson.Str("edit.preferences")));
        t.Shot("preferences-general", "Preferences, General");
        foreach (string page in new[] { "Views", "Shortcuts" })
        {
            // Preferences lists its pages down the left as buttons.
            t.Step("click", ("at", ApiJson.Str("text:" + page)), ("frames", SvJsonNode.Integer(3)));
            t.Shot("preferences-" + Slug(page), "Preferences, " + page);
        }

        t.Step("escape");
        t.Step("run", ("action", ApiJson.Str("help.about")));
        t.Shot("about", "Help > About");
        t.Step("escape");
    }

    /// <summary>Tool states beyond a bare activation: picked faces and the Surface panel, the face tool's menus, a vertex
    /// box selection, clip points, a measurement and the place tool's class list.</summary>
    private static void ToolStates(Tour t, EditorContext ctx, MainWindow window)
    {
        if (ctx.Project.Brushes.Count > 0)
        {
            t.Step("select", ("what", ApiJson.Str("brush")), ("frame", ApiJson.Bool(true)));
            t.Step("tool", ("id", ApiJson.Str("face")));
            t.Step("click", ("at", ApiJson.Str("view:Perspective")), ("frames", SvJsonNode.Integer(3)));
            t.Shot("face-click-centre", "Surface tool, a click in the middle of the perspective view (a wall's + opens Add on Wall)",
                views: true);
            t.Step("escape");
            t.Step("click", ("at", ApiJson.Str("text:Whole brush")), ("frames", SvJsonNode.Integer(3)));
            t.Shot("face-picked", "Surface tool, every face of the brush picked (Whole brush)", views: true);
            t.Step("tab", ("panel", ApiJson.Str("Surface")));
            t.Shot("face-surface-panel", "Surface panel with faces picked", crop: "dock:Surface");
            t.Step("rightClick", ("at", ApiJson.Str("view:Perspective")), ("offset", Offset(0.3, 0.75)), ("frames", SvJsonNode.Integer(3)));
            t.Shot("face-context", "Surface tool, right click in the perspective view");
            t.Step("escape");

            t.Step("select", ("what", ApiJson.Str("brush")), ("frame", ApiJson.Bool(true)));
            t.Step("tool", ("id", ApiJson.Str("vertex")));
            t.Step("drag", ("at", ApiJson.Str("view:Top")), ("offset", Offset(0.05, 0.2)), ("to", ApiJson.Str("view:Top")),
                ("toOffset", Offset(0.88, 0.95)));
            t.Shot("vertex-box", "Vertex tool after a box selection in the top view", views: true);

            t.Step("tool", ("id", ApiJson.Str("clip")));
            t.Step("click", ("at", ApiJson.Str("view:Top")), ("offset", Offset(0.35, 0.55)));
            t.Step("click", ("at", ApiJson.Str("view:Top")), ("offset", Offset(0.65, 0.45)));
            t.Shot("clip-points", "Clip tool with two clip points", views: true);
            t.Step("escape");
        }

        t.Step("tool", ("id", ApiJson.Str("measure")));
        t.Step("drag", ("at", ApiJson.Str("view:Top")), ("offset", Offset(0.3, 0.5)), ("to", ApiJson.Str("view:Top")),
            ("toOffset", Offset(0.7, 0.4)), ("hold", ApiJson.Bool(true)));
        t.Shot("measure-drag", "Measure tool while dragging", views: true);
        t.Step("release");
        t.Step("escape");

        t.Step("tool", ("id", ApiJson.Str("place")));
        t.Step("click", ("at", ApiJson.Str("text:Classes")), ("frames", SvJsonNode.Integer(3)));
        t.Shot("place-classes", "Place tool's class list");
        t.Step("escape");
        t.Step("tool", ("id", ApiJson.Str("select")));
    }

    /// <summary>The gameplay workbench panels with an object they work on selected (their empty states are in
    /// <see cref="Panels"/>, which runs with nothing selected).</summary>
    private static void Workbench(Tour t, EditorContext ctx, MainWindow window)
    {
        (string Class, string[] Panels)[] wants =
        [
            ("Mover", ["Mover", "Connections", "Match"]),
            ("SMagicEvent", ["Event Sequencer"]),
            ("SCamera", ["Camera Network", "Properties", "References"]),
            ("SZoneTips", ["Tips"]),
            ("SObjective", ["Missions"]),
            ("SAlarm", ["Connections"]),
            ("PlayerStart", ["Match", "Traversal"]),
        ];
        foreach ((string suffix, string[] panels) in wants)
        {
            if (ctx.Project.Actors.FirstOrDefault(a => a.Class == suffix || a.Class.EndsWith("." + suffix, StringComparison.Ordinal)) is not { } actor)
            {
                continue;
            }

            t.Step("select", ("class", ApiJson.Str(actor.Class)), ("frame", ApiJson.Bool(true)));
            foreach (string panel in panels)
            {
                if (DockOf(window, panel) is not null)
                {
                    t.Step("tab", ("panel", ApiJson.Str(panel)));
                    t.Shot($"workbench-{Slug(panel)}-{Slug(suffix)}", $"{panel} panel, a {suffix} selected", crop: "dock:" + panel);
                }
            }
        }

        if (ctx.Project.Ges.Count > 0 && DockOf(window, "Traversal") is not null)
        {
            t.Step("select", ("what", ApiJson.Str("ge")), ("frame", ApiJson.Bool(true)));
            t.Step("tab", ("panel", ApiJson.Str("Traversal")));
            t.Shot("workbench-traversal-ge", "Traversal panel, a GE selected", crop: "dock:Traversal");
        }

        t.Step("select", ("what", ApiJson.Str("none")));
    }

    /// <summary>Hover and selected states: a dock tab and an outliner row under the pointer, a selected row, the
    /// outliner's context menu, and a Map Check row under the pointer and selected.</summary>
    private static void States(Tour t, EditorContext ctx, MainWindow window)
    {
        t.Step("hover", ("at", ApiJson.Str("tab:Layers")));
        t.Shot("hover-dock-tab", "Pointer over a dock tab");
        if (window.LeftDock.GetNodeOrNull<Control>("Outliner") is { } outliner && Descendants(outliner).OfType<Tree>().FirstOrDefault() is { } tree)
        {
            string dock = PathOf(window.LeftDock);
            t.Step("tab", ("panel", ApiJson.Str("Outliner")));
            t.Step("hover", ("at", ApiJson.Str(PathOf(tree))), ("offset", Offset(0.4, 0.1)));
            t.Shot("hover-outliner-row", "Pointer over an outliner row", crop: dock);
            if (ctx.Project.Actors.FirstOrDefault() is { } actor)
            {
                t.Step("select", ("class", ApiJson.Str(actor.Class)), ("frame", ApiJson.Bool(true)));
                t.Shot("selected-outliner-row", "A selected object in the outliner", crop: dock);
                t.Step("rightClick", ("at", ApiJson.Str(PathOf(tree))), ("offset", Offset(0.4, 0.1)), ("frames", SvJsonNode.Integer(3)));
                t.Shot("outliner-context", "Outliner context menu");
                t.Step("escape");
            }

            t.Step("select", ("what", ApiJson.Str("none")));
        }

        t.Step("run", ("action", ApiJson.Str("build.check")));
        t.Step("wait", ("idle", ApiJson.Bool(true)));
        t.Step("tab", ("panel", ApiJson.Str("Map Check")));
        if (window.BottomDock.GetNodeOrNull<Control>("Map Check") is { } check && Descendants(check).OfType<Tree>().FirstOrDefault() is { } rows)
        {
            t.Step("hover", ("at", ApiJson.Str(PathOf(rows))), ("offset", Offset(0.3, 0.2)));
            t.Shot("hover-mapcheck-row", "Pointer over a Map Check row", crop: PathOf(window.BottomDock));
            t.Step("click", ("at", ApiJson.Str(PathOf(rows))), ("offset", Offset(0.3, 0.2)), ("frames", SvJsonNode.Integer(3)));
            t.Shot("selected-mapcheck-row", "A selected Map Check row", views: true);
        }

        t.Step("select", ("what", ApiJson.Str("none")));
    }

    /// <summary>Each kind the asset browser lists, then one asset selected with another under the pointer.</summary>
    private static void AssetKinds(Tour t, MainWindow window)
    {
        if (Descendants(window).OfType<AssetBrowserPanel>().FirstOrDefault() is not { } panel)
        {
            return;
        }

        string dock = PathOf(window.BottomDock);
        t.Step("tab", ("panel", ApiJson.Str("Assets")));
        foreach (string kind in new[] { "Materials", "Meshes", "Textures", "Sounds", "Favourites" })
        {
            t.Step("click", ("at", ApiJson.Str("tab:" + kind)));
            t.Step("wait", ("idle", ApiJson.Bool(true)));
            t.Shot("assets-" + Slug(kind), "Asset browser, " + kind, crop: dock);
        }

        if (Descendants(panel).OfType<ItemList>().FirstOrDefault() is { } list)
        {
            t.Step("click", ("at", ApiJson.Str("tab:Materials")));
            t.Step("wait", ("idle", ApiJson.Bool(true)));
            t.Step("click", ("at", ApiJson.Str(PathOf(list))), ("offset", Offset(0.1, 0.08)), ("frames", SvJsonNode.Integer(3)));
            t.Step("hover", ("at", ApiJson.Str(PathOf(list))), ("offset", Offset(0.25, 0.08)));
            t.Shot("assets-selected", "Asset browser, a material selected and another under the pointer", crop: dock);
        }
    }

    private static TabContainer? DockOf(MainWindow window, string panel) =>
        window.Docks.FirstOrDefault(d => d.GetNodeOrNull(panel) is not null);

    private static SvJsonArray Offset(double x, double y) => ApiJson.Arr([ApiJson.Num(x), ApiJson.Num(y)]);

    private static string Slug(string s) => s.ToLowerInvariant().Replace(' ', '-').Replace('.', '-');

    private static void Panels(Tour t, MainWindow window)
    {
        foreach (TabContainer dock in window.Docks)
        {
            for (int i = 0; i < dock.GetTabCount(); i++)
            {
                string title = DockTabs.Name(dock, i);
                t.Step("tab", ("panel", ApiJson.Str(title)));
                t.Shot("panel-" + title.ToLowerInvariant().Replace(' ', '-'), title + " panel", crop: "dock:" + title);
            }
        }
    }

    /// <summary>Drags the first mesh of the asset browser into the top view, where it is placed: the drop reads the
    /// synthetic pointer, so the manifest's object count must go up by one.</summary>
    private static void AssetDrop(Tour t, MainWindow window)
    {
        if (Descendants(window).OfType<AssetBrowserPanel>().FirstOrDefault() is not { } panel ||
            Descendants(panel).OfType<ItemList>().FirstOrDefault() is not { } list)
        {
            return;
        }

        t.Step("tab", ("panel", ApiJson.Str("Assets")));
        t.Step("click", ("at", ApiJson.Str("tab:Meshes")));
        t.Step("wait", ("idle", ApiJson.Bool(true)));
        t.Shot("drop-before", "Asset browser, meshes, before a drag into the top view");
        t.Step("drag", ("at", ApiJson.Str(PathOf(list))), ("offset", ApiJson.Arr([ApiJson.Num(0.1), ApiJson.Num(0.08)])),
            ("to", ApiJson.Str("view:Top")), ("steps", SvJsonNode.Integer(12)));
        t.Step("wait", ("frames", SvJsonNode.Integer(4)));
        t.Shot("drop-after", "After dragging the first mesh into the top view", views: true);
    }

    private static IEnumerable<Node> Descendants(Node n)
    {
        foreach (Node child in n.GetChildren())
        {
            yield return child;
            foreach (Node d in Descendants(child))
            {
                yield return d;
            }
        }
    }

    private static void Inspector(Tour t, EditorContext ctx, MainWindow window)
    {
        MapProject p = ctx.Project;
        string dock = PathOf(window.RightDock);
        t.Step("tab", ("panel", ApiJson.Str("Inspector")));
        t.Step("select", ("what", ApiJson.Str("none")));
        t.Shot("inspector-empty", "Inspector, nothing selected", crop: dock);
        if (p.Brushes.Count > 0)
        {
            t.Step("select", ("what", ApiJson.Str("brush")));
            t.Shot("inspector-brush", "Inspector, a brush", crop: dock);
        }

        if (p.Ges.Count > 0)
        {
            t.Step("select", ("what", ApiJson.Str("ge")));
            t.Shot("inspector-ge", "Inspector, a GE", crop: dock);
        }

        foreach (string cls in p.Actors.Select(a => a.Class).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Take(16))
        {
            t.Step("select", ("class", ApiJson.Str(cls)), ("frame", ApiJson.Bool(true)));
            t.Shot("inspector-" + cls, "Inspector, " + cls, crop: dock);
        }

        t.Step("select", ("what", ApiJson.Str("all")));
        t.Shot("inspector-many", "Inspector, everything selected", crop: dock);
        t.Step("select", ("what", ApiJson.Str("none")));
    }

    private static void Dialogs(Tour t, EditorContext ctx)
    {
        t.Step("run", ("action", ApiJson.Str("build.check")));
        t.Step("wait", ("idle", ApiJson.Bool(true)));
        t.Step("tab", ("panel", ApiJson.Str("Map Check")));
        t.Shot("mapcheck-after-run", "Map Check after a run");
        if (ctx.Project.Brushes.Count > 0)
        {
            t.Step("select", ("what", ApiJson.Str("brush")));
        }
        else if (ctx.Project.Actors.FirstOrDefault() is { } actor)
        {
            t.Step("select", ("class", ApiJson.Str(actor.Class)));
        }
        else
        {
            return;
        }

        t.Step("run", ("action", ApiJson.Str("edit.duplicate")));
        t.Step("run", ("action", ApiJson.Str("file.new")));
        t.Shot("unsaved-changes", "The unsaved-changes question");
        t.Step("escape");
        t.Step("run", ("action", ApiJson.Str("edit.undo")));
        t.Step("select", ("what", ApiJson.Str("none")));
    }

    private static void Sizes(Tour t)
    {
        foreach ((int w, int h, double scale) in new[] { (1280, 720, 1.0), (1920, 1080, 1.0), (2560, 1440, 1.0), (2400, 1350, 1.5), (3200, 1800, 2.0) })
        {
            t.Step("size", ("w", SvJsonNode.Integer(w)), ("h", SvJsonNode.Integer(h)));
            t.Step("scale", ("factor", ApiJson.Num(scale)));
            t.Shot($"size-{w}x{h}-scale{scale:0.#}", $"{w}x{h} at UI scale {scale:0.#}");
        }

        t.Step("scale", ("factor", ApiJson.Num(1)));
        t.Step("size", ("w", SvJsonNode.Integer(1600)), ("h", SvJsonNode.Integer(900)));
    }

    private static string PathOf(Control c) => c.GetTree().Root.GetPathTo(c).ToString();

    /// <summary>Collects steps in the shot script format.</summary>
    private sealed class Tour
    {
        public SvJsonArray Steps { get; } = new();

        public void Step(string op, params (string Key, SvJsonNode? Value)[] args) =>
            Steps.Add(ApiJson.Obj([("op", ApiJson.Str(op)), .. args]));

        public void Shot(string name, string caption, bool views = false, string? crop = null) =>
            Step("shot", ("name", ApiJson.Str(name)), ("caption", ApiJson.Str(caption)), ("views", views ? ApiJson.Bool(true) : null),
                ("crop", crop is null ? null : ApiJson.Str(crop)));

        public void Menu(string path)
        {
            Step("menu", ("path", ApiJson.Str(path)));
            Shot("menu-" + path.ToLowerInvariant().Replace('/', '-').Replace(' ', '-'), "Menu " + path.Replace("/", " > "));
            Step("escape");
        }
    }
}
