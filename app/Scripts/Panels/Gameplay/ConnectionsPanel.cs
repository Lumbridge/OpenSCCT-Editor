using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// The event graph: every wired actor with the events it fires (Event to Tag), the ones that reach it and its plain
/// references, plus events that name no tag (dangling) and actors that fire each other in a loop (rings). Filter by
/// text, show only the selection's links or only the dangling events; click selects, double-click frames. A tag can
/// be renamed together with every event that names it. The Graph switch draws the selection's neighbourhood instead.
/// </summary>
[Panel("Connections", DockSide.Bottom, Order = 3)]
public partial class ConnectionsPanel : EditorPanel
{
    private const int MaxRows = 3000;

    private Tree _tree = null!;
    private Control _empty = null!;
    private Label _summary = null!;
    private LineEdit _tag = null!;
    private DeferredAction _update = null!;
    private string _filter = "";
    private bool _followSelection;
    private bool _danglingOnly;
    private bool _showGraph;
    private EventGraphView _graph = null!;
    private ScrollContainer _graphScroll = null!;
    private HBoxContainer _depth = null!;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        PanelUi.Search(bar, "Filter links", t =>
        {
            _filter = t.Trim();
            _update.Queue();
        }, "Filter by id, class, tag or event");
        PanelUi.Toggle(bar, "Selection", "Only the links of the selected actors", false, on =>
        {
            _followSelection = on;
            _update.Queue();
        });
        PanelUi.Toggle(bar, "Dangling", "Only events that name no tag", false, on =>
        {
            _danglingOnly = on;
            _update.Queue();
        });
        PanelUi.Toggle(bar, "Graph", "Draw the selected actors' events as a graph (senders left, receivers right)", false, on =>
        {
            _showGraph = on;
            _tree.Visible = !on;
            _graphScroll.Visible = on;
            _depth.Visible = on;
            _update.Queue();
        });
        _depth = new HBoxContainer { Visible = false, TooltipText = "How many links away the graph reaches" };
        bar.AddChild(_depth);
        _depth.AddChild(new Label { Text = "Depth", ThemeTypeVariation = "Dim" });
        SpinBox depth = PanelUi.Spin(_depth, 1, 4, 1, 1, "How many links away the graph reaches", EditorTheme.SpinCompactWidth);
        depth.ValueChanged += v =>
        {
            _graph.Depth = (int)v;
            _update.Queue();
        };
        // The filters above are chips; the commands after the hairline are buttons.
        bar.AddChild(new VSeparator());
        WorkbenchUi.Action(bar, "Link", "The first selected actor fires (or locks) the others", "gameplay.wire.link", Context).Icon =
            Icons.Get("link", EditorTheme.Text);
        PanelUi.Button(bar, "Unlink", "Remove the chosen link row (else clear the selected actors' events)", Unlink).Icon = Icons.Get("unlink", EditorTheme.Text);

        var rename = new HBoxContainer();
        AddChild(rename);
        _summary = PanelUi.Caption(rename, "", wrap: false);
        _tag = new LineEdit { PlaceholderText = "New tag", TooltipText = "The new name of the selected row's tag (no spaces)", CustomMinimumSize = new Vector2(140, 0) };
        rename.AddChild(_tag);
        PanelUi.Button(rename, "Rename tag", "Rename the selected row's tag on every actor and event that uses it", RenameTag);

        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        PanelUi.Columns(_tree, ("Actor / Link", 160, true), ("Via", 90, true), ("Event / Tag", 110, true));
        _tree.SetColumnExpandRatio(0, 2);
        _tree.ItemSelected += () => Pick(focus: false);
        _tree.ItemActivated += () => Pick(focus: true);
        AddChild(_tree);
        _empty = PanelUi.EmptyState(_tree, "link", "No links", "No actor fires, locks or names another here. Select actors and press Link to wire them.");
        _graph = new EventGraphView();
        _graphScroll = new ScrollContainer { Visible = false, SizeFlagsVertical = SizeFlags.ExpandFill };
        _graphScroll.AddChild(_graph);
        AddChild(_graphScroll);

        Context.ProjectChanged += _ => _update.Queue();
        Context.SelectionChanged += () =>
        {
            if (_followSelection || _showGraph)
            {
                _update.Queue();
            }
        };
    }

    public override void Refresh() => _update.Queue();

    private void Rebuild()
    {
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        MapProject p = Context.Project;
        GameplayGraph g = GameplayState.Instance.Graph;
        List<List<string>> rings = g.Rings();
        _summary.Text = _summary.TooltipText = $"{PanelUi.Count(g.Links.Count, "link")} · {PanelUi.Count(g.Dangling.Count, "dangling event")} · {PanelUi.Count(rings.Count, "ring")}";
        if (_showGraph)
        {
            _graph.Filter = _filter;
            _graph.Rebuild(g);
            return;
        }

        HashSet<string>? only = _followSelection
            ? Context.Selection.Items.Where(r => r.Kind == ObjectKind.Actor).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var inRing = rings.SelectMany(r => r).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> nodes = g.Nodes.Order(StringComparer.OrdinalIgnoreCase).ToList();
        int rows = 0;
        foreach (string id in nodes)
        {
            if (rows >= MaxRows)
            {
                break;
            }

            if ((only is not null && !only.Contains(id)) || p.FindActor(id) is not { } a)
            {
                continue;
            }

            List<DanglingEvent> dangling = g.Dangling.Where(d => Same(d.From, id)).ToList();
            if ((_danglingOnly && dangling.Count == 0) || !Matches(a, g, dangling))
            {
                continue;
            }

            // The actor's type icon says its class (the tooltip names it), as in the outliner.
            TreeItem node = Row(root, a.Id, "", a.Tag, a.Id);
            node.SetIcon(0, PanelIcons.Type(a.Class));
            node.SetTooltipText(0, $"{a.Id} ({a.ShortClass})");
            if (inRing.Contains(id))
            {
                node.SetCustomColor(0, EditorTheme.Warning);
                node.SetTooltipText(0, $"{a.Id} ({a.ShortClass}): part of an event ring");
            }

            rows++;
            foreach (DanglingEvent d in dangling)
            {
                TreeItem missing = Row(node, "No actor has this tag", d.Via, d.Event, a.Id);
                missing.SetCustomColor(0, EditorTheme.Error);
                missing.SetIcon(0, PanelIcons.Severity(Severity.Error));
                rows++;
            }

            if (_danglingOnly)
            {
                continue;
            }

            foreach (GameplayLink l in g.Out(id))
            {
                TreeItem item = LinkRow(node, l.To, l, l.To);
                item.SetIcon(0, Icons.Get("arrow-right", l.Kind == LinkKind.Ref ? EditorTheme.TextDim : EditorTheme.Text));
                item.SetTooltipText(0, l.Kind == LinkKind.Accept ? $"Accepts events from {l.To}" : $"Fires {l.To}");
                if (l.Kind == LinkKind.Ref)
                {
                    item.SetCustomColor(0, EditorTheme.TextDim);
                    item.SetTooltipText(0, $"Names {l.To}");
                }

                rows++;
            }

            foreach (GameplayLink l in g.In(id))
            {
                TreeItem from = LinkRow(node, l.From, l, l.From);
                from.SetCustomColor(0, EditorTheme.TextDim);
                from.SetIcon(0, Icons.Get("arrow-left", EditorTheme.TextDim));
                from.SetTooltipText(0, $"Linked from {l.From}");
                rows++;
            }

            node.Collapsed = only is null && nodes.Count > 40;
        }

        if (rows >= MaxRows)
        {
            Row(root, $"(first {MaxRows} rows: filter to see more)", "", "", "");
        }

        _empty.Visible = rows == 0;
        _tree.ColumnTitlesVisible = rows > 0;
    }

    private bool Matches(Actor a, GameplayGraph g, List<DanglingEvent> dangling)
    {
        if (_filter.Length == 0)
        {
            return true;
        }

        bool Has(string? s) => s is not null && s.Contains(_filter, StringComparison.OrdinalIgnoreCase);
        return Has(a.Id) || Has(a.Class) || Has(a.Tag) || dangling.Any(d => Has(d.Event))
            || g.Out(a.Id).Any(l => Has(l.To) || Has(l.Event)) || g.In(a.Id).Any(l => Has(l.From) || Has(l.Event));
    }

    private TreeItem Row(TreeItem parent, string text, string via, string tag, string actorId)
    {
        TreeItem item = _tree.CreateItem(parent);
        item.SetText(0, text);
        item.SetText(1, via);
        item.SetText(2, tag);
        // The cells clip in a narrow dock: their tooltips have the whole text.
        item.SetTooltipText(0, text);
        item.SetTooltipText(1, via);
        item.SetTooltipText(2, tag);
        item.SetMetadata(0, actorId);
        return item;
    }

    /// <summary>A link row: selects <paramref name="actorId"/>, and remembers the link's two ends for Unlink.</summary>
    private TreeItem LinkRow(TreeItem parent, string text, GameplayLink l, string actorId)
    {
        TreeItem item = Row(parent, text, l.Via, l.Event ?? "", actorId);
        item.SetMetadata(1, l.From + "|" + l.To);
        return item;
    }

    private void Unlink()
    {
        if (_tree.GetSelected()?.GetMetadata(1).AsString() is not { Length: > 0 } pair)
        {
            Context.Run("gameplay.wire.unlink");
            return;
        }

        string[] ends = pair.Split('|');
        if (Wiring.Unlink(Context.Project, ends[0], ends[1]) is { } cmd)
        {
            Context.Execute(cmd);
            Context.SetStatus($"Removed the links from {ends[0]} to {ends[1]}");
        }
        else
        {
            Context.SetStatus($"{ends[0]} has no link of its own to {ends[1]} to remove");
        }
    }

    private void Pick(bool focus)
    {
        if (_tree.GetSelected()?.GetMetadata(0).AsString() is not { Length: > 0 } id || Context.Project.FindActor(id) is null)
        {
            return;
        }

        ObjectRef[] refs = [ObjectRef.Actor(id)];
        if (!_followSelection)
        {
            Context.Selection.Set(refs);
        }

        if (focus)
        {
            Context.Focus(refs);
        }
    }

    private void RenameTag()
    {
        string newTag = _tag.Text.Trim();
        if (_tree.GetSelected()?.GetMetadata(0).AsString() is not { Length: > 0 } id || Context.Project.FindActor(id) is not { } a
            || newTag.Length == 0 || newTag.Contains(' '))
        {
            Context.SetStatus("Select a row and type a new tag (no spaces)");
            return;
        }

        string oldTag = a.Tag;
        ReplaceObjectsCommand cmd = Wiring.RenameTag(Context.Project, oldTag, newTag);
        Context.Execute(cmd);
        Context.SetStatus(cmd.Name);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
