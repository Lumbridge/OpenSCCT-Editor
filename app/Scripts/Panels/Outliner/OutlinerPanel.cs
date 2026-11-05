using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// The outliner: layers, then folders, then objects (grouped by type by default, like RE+ Map Design's
/// layers panel; or by UE2 Group as RE+'s scene panel does, or flat). Search by id, class, tag, event or group; eye and
/// lock per row; multi-select kept in step with the viewports both ways; double-click frames; F2 renames the tag;
/// drag rows onto a layer, folder or group to move them.
/// </summary>
[Panel("Outliner", DockSide.Left, Order = 0)]
public partial class OutlinerPanel : EditorPanel
{
    private const int ButtonEye = 0;
    private const int ButtonLock = 1;
    private const string DragPrefix = "outliner:";
    private const string NoGroup = "(no group)";

    private readonly Dictionary<ObjectRef, TreeItem> _objectItems = new();
    private readonly Dictionary<TreeItem, OutlinerNode> _nodes = new();
    private readonly Dictionary<string, bool> _collapsed = new(StringComparer.Ordinal);
    private OutlinerTree _tree = null!;
    private LineEdit _search = null!;
    private Label _status = null!;
    private PopupMenu _menu = null!;
    private PopupMenu _layerMenu = null!;
    private DeferredAction _update = null!;
    private DeferredAction _pushSelection = null!;
    private string[] _words = [];
    private Grouping _grouping = Grouping.Type;
    private bool _building;
    private bool _renaming;
    private int _matches;
    private long _structure;
    private List<ObjectRef> _dragging = new();

    protected override void Build()
    {
        _update = new DeferredAction(Update);
        _pushSelection = new DeferredAction(PushSelection);

        HFlowContainer bar = PanelUi.Toolbar(this);
        _search = PanelUi.Search(bar, "Search…", t =>
        {
            _words = TextMatch.Words(t);
            _update.Queue();
        }, "Search ids, classes, tags and events; several words narrow it down");
        var grouping = new OptionButton { TooltipText = "How objects are grouped inside each folder", FocusMode = FocusModeEnum.None };
        grouping.AddItem("By type", (int)Grouping.Type);
        grouping.AddItem("By group", (int)Grouping.Group);
        grouping.AddItem("Flat", (int)Grouping.None);
        grouping.ItemSelected += index =>
        {
            _grouping = (Grouping)grouping.GetItemId((int)index);
            _structure = 0;
            _update.Queue();
        };
        bar.AddChild(grouping);

        _tree = new OutlinerTree
        {
            Name = "Tree",
            Columns = 2,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Multi,
            AllowRmbSelect = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        // Names get the width (a folder's object count follows its name); the second column holds only the eye and lock
        // buttons. Tags and events are in each row's tooltip (and the search finds them): a tag cut to two letters helps
        // nobody.
        _tree.SetColumnExpand(0, true);
        _tree.SetColumnExpand(1, false);
        _tree.SetColumnCustomMinimumWidth(1, 60);
        _tree.SetColumnClipContent(1, true);
        _tree.MultiSelected += (_, _, _) =>
        {
            if (!_building)
            {
                _pushSelection.Queue();
            }
        };
        _tree.ItemActivated += FocusSelected;
        _tree.EmptyClicked += (_, _) =>
        {
            _tree.DeselectAll();
            Context.Selection.Clear();
        };
        _tree.ButtonClicked += OnButton;
        _tree.ItemEdited += OnEdited;
        _tree.ItemCollapsed += item =>
        {
            if (!_building && _nodes.TryGetValue(item, out OutlinerNode? n))
            {
                _collapsed[n.Key] = item.Collapsed;
            }
        };
        _tree.ItemMouseSelected += (_, button) =>
        {
            if (button == (long)MouseButton.Right)
            {
                ShowMenu();
            }
        };
        _tree.GuiInput += OnTreeKey;
        _tree.DragStarting = StartDrag;
        _tree.CanDrop = (pos, data) => data.VariantType == Variant.Type.String && data.AsString().StartsWith(DragPrefix, StringComparison.Ordinal)
            && _tree.GetItemAtPosition(pos) is { } target && _nodes.ContainsKey(target);
        _tree.Dropped = OnDrop;
        _tree.StateColumn = 1;
        _tree.RowBands = true;
        AddChild(_tree);

        _status = PanelUi.Caption(this, "", wrap: false);
        BuildMenu();

        Context.ProjectChanged += _ => _update.Queue();
        // A rename cancelled with Escape sends no signal; relabel when the tree gets its focus back.
        _tree.FocusEntered += () =>
        {
            if (_renaming)
            {
                _renaming = false;
                _update.Queue();
            }
        };
        Context.SelectionChanged += PullSelection;
        // Menu orders: the bands in docs/UI-DESIGN.md (6.2).
        Context.AddAction(new EditorAction("outliner.search", "Search Outliner", "View", () => _search.GrabFocus())
        {
            Order = 900, Description = "Search the outliner by id, class, tag, event or group",
        });
        Context.AddAction(new EditorAction("outliner.hideSelected", "Hide Selected", "Edit", () => SetHidden(Context.Selection.Items, true))
        {
            Order = 300, Description = "Hide the selected objects in the views", IsEnabled = () => Context.Selection.Count > 0,
        });
        Context.AddAction(new EditorAction("outliner.isolateSelected", "Isolate Selected", "Edit", Isolate)
        {
            Order = 310, Description = "Show the selected objects and hide the rest", IsEnabled = () => Context.Selection.Count > 0,
        });
        Context.AddAction(new EditorAction("outliner.showAll", "Show All", "Edit", ShowAll)
        {
            Order = 320, Description = "Show every hidden object",
        });
        Context.AddAction(new EditorAction("outliner.lockSelected", "Lock Selected", "Edit", () => SetLocked(Context.Selection.Items, true))
        {
            Order = 330, Description = "Stop the selected objects being picked or moved in the views", IsEnabled = () => Context.Selection.Count > 0,
        });
        Context.AddAction(new EditorAction("outliner.unlockAll", "Unlock All", "Edit", UnlockAll)
        {
            Order = 340, Description = "Unlock every locked object",
        });
        Context.AddAction(new EditorAction("outliner.selectGroup", "Select Group", "Edit", SelectGroups)
        {
            Order = 240, Description = "Select every object in the selection's groups (UE2 Group)", IsEnabled = () => Context.Selection.Count > 0,
        });
    }

    public override void Refresh()
    {
        _structure = 0;
        _update.Queue();
    }

    // ---- building -------------------------------------------------------------------------------------------------

    private void Update()
    {
        MapProject p = Context.Project;
        List<ObjectRow> rows = ObjectInfo.All(p).Where(r => _words.Length == 0 || TextMatch.AllWords(_words, r.SearchText)).ToList();
        long structure = StructureOf(p, rows);
        if (structure == _structure && _tree.GetRoot() is not null)
        {
            UpdateInPlace(p);
        }
        else
        {
            _structure = structure;
            Rebuild(p, rows);
        }

        _matches = rows.Count;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        MapProject p = Context.Project;
        int total = p.Brushes.Count + p.Actors.Count + p.Ges.Count;
        _status.Text = _words.Length > 0
            ? $"{_matches} of {PanelUi.Count(total, "object")} match · {Context.Selection.Count} selected"
            : $"{PanelUi.Count(total, "object")} · {Context.Selection.Count} selected";
        _status.TooltipText = _status.Text;
    }

    /// <summary>A hash of everything that shapes the tree (which rows sit where); edits that keep it only relabel.</summary>
    private long StructureOf(MapProject p, List<ObjectRow> rows)
    {
        var h = new HashCode();
        h.Add(_grouping);
        h.Add(_words.Length);
        foreach (Layer l in p.Layers)
        {
            h.Add(l.Name);
        }

        foreach (ObjectRow r in rows)
        {
            h.Add(r.Ref);
            h.Add(r.Meta.Layer);
            h.Add(r.Meta.Folder);
            h.Add(r.Class);
            h.Add(r.Group);
        }

        return (uint)h.ToHashCode() | (1L << 40);
    }

    private void Rebuild(MapProject p, List<ObjectRow> rows)
    {
        _building = true;
        try
        {
            _tree.ForgetHot();
            _tree.Clear();
            _objectItems.Clear();
            _nodes.Clear();
            TreeItem root = _tree.CreateItem();
            var byLayer = rows.GroupBy(r => ObjectInfo.LayerName(r.Meta), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Project layers in order, then layers objects name that do not exist (shown so nothing is lost).
            List<string> layerNames = ObjectInfo.LayerNames(p);
            layerNames.AddRange(byLayer.Keys.Where(k => !layerNames.Contains(k, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase));
            foreach (string layer in layerNames)
            {
                List<ObjectRow> members = byLayer.GetValueOrDefault(layer) ?? [];
                if (_words.Length > 0 && members.Count == 0)
                {
                    continue;
                }

                var node = new OutlinerNode(NodeKind.Layer, "L|" + layer, layer, "", members.Select(m => m.Ref).ToList());
                TreeItem item = AddNode(root, node, layer);
                item.SetIcon(0, Icons.Get("type-layer", EditorTheme.TextDim));
                if (ObjectInfo.FindLayer(p, layer) is null && !ObjectInfo.IsDefaultLayer(layer))
                {
                    item.SetTooltipText(0, $"Layer '{layer}' is not in the layer list; objects still name it.");
                    item.SetCustomColor(0, EditorTheme.Warning);
                }

                AddFolderContents(item, layer, "", members);
            }

            UpdateInPlace(p);
        }
        finally
        {
            _building = false;
        }

        PullSelection();
    }

    private void AddFolderContents(TreeItem parent, string layer, string folder, List<ObjectRow> rows)
    {
        // Sub-folders first (sorted), then this folder's own objects.
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var direct = new List<ObjectRow>();
        var sub = new SortedDictionary<string, List<ObjectRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectRow r in rows)
        {
            string f = r.Meta.Folder.Trim('/');
            if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))
            {
                direct.Add(r);
                continue;
            }

            string rest = f[prefix.Length..];
            string child = prefix + rest.Split('/')[0];
            if (!sub.TryGetValue(child, out List<ObjectRow>? list))
            {
                sub[child] = list = new List<ObjectRow>();
            }

            list.Add(r);
        }

        foreach ((string path, List<ObjectRow> members) in sub)
        {
            string name = path[(path.LastIndexOf('/') + 1)..];
            var node = new OutlinerNode(NodeKind.Folder, $"F|{layer}|{path}", layer, path, members.Select(m => m.Ref).ToList());
            TreeItem item = AddNode(parent, node, name);
            item.SetCustomColor(0, EditorTheme.Text);
            item.SetIcon(0, Icons.Get("folder", EditorTheme.TextDim));
            AddFolderContents(item, layer, path, members);
        }

        // A folder holding one class only (an imported map's Static meshes) needs no class level: the names get its width.
        if (_grouping == Grouping.None || (_grouping == Grouping.Type && direct.Select(r => r.Class).Distinct(StringComparer.Ordinal).Count() == 1))
        {
            foreach (ObjectRow r in direct)
            {
                AddObject(parent, r);
            }

            return;
        }

        IEnumerable<IGrouping<string, ObjectRow>> groups = _grouping == Grouping.Type
            ? direct.GroupBy(r => r.Class, StringComparer.Ordinal)
                .OrderBy(g => g.Key == ObjectInfo.BrushClass ? 0 : g.Key.StartsWith("GE ", StringComparison.Ordinal) ? 2 : 1)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            : direct.GroupBy(r => r.PrimaryGroup.Length > 0 ? r.PrimaryGroup : NoGroup, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key == NoGroup ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, ObjectRow> group in groups)
        {
            var node = new OutlinerNode(NodeKind.Group, $"G{(int)_grouping}|{layer}|{folder}|{group.Key}", layer, folder, group.Select(m => m.Ref).ToList());
            TreeItem item = AddNode(parent, node, group.Key, defaultCollapsed: node.Members.Count > 25);
            item.SetCustomColor(0, EditorTheme.TextDim);
            item.SetIcon(0, _grouping == Grouping.Type ? PanelIcons.Type(group.Key) : Icons.Get("type-group", EditorTheme.TextDim));
            foreach (ObjectRow r in group)
            {
                AddObject(item, r);
            }
        }
    }

    private TreeItem AddNode(TreeItem parent, OutlinerNode node, string text, bool defaultCollapsed = false)
    {
        TreeItem item = _tree.CreateItem(parent);
        item.SetText(0, text);
        item.SetMetadata(0, node.Key);
        item.AddButton(1, PanelIcons.Eye(true), ButtonEye, false, "Show or hide");
        item.AddButton(1, PanelIcons.Lock(false), ButtonLock, false, "Lock or unlock");
        item.Collapsed = _words.Length == 0 && _collapsed.GetValueOrDefault(node.Key, defaultCollapsed);
        _nodes[item] = node;
        return item;
    }

    private void AddObject(TreeItem parent, ObjectRow row)
    {
        TreeItem item = _tree.CreateItem(parent);
        item.SetText(0, row.Ref.Id);
        item.SetIcon(0, PanelIcons.Type(row));
        item.AddButton(1, PanelIcons.Eye(true), ButtonEye, false, "Show or hide");
        item.AddButton(1, PanelIcons.Lock(false), ButtonLock, false, "Lock or unlock");
        _nodes[item] = new OutlinerNode(NodeKind.Object, "O|" + row.Ref, ObjectInfo.LayerName(row.Meta), row.Meta.Folder.Trim('/'), [row.Ref]);
        _objectItems[row.Ref] = item;
    }

    /// <summary>Relabels every row from the model: tooltips, counts, eye and lock states, colours.</summary>
    private void UpdateInPlace(MapProject p)
    {
        // One pass over the map; looking objects up one by one would be quadratic on big maps.
        Dictionary<ObjectRef, ObjectRow> rows = ObjectInfo.Lookup(p);

        foreach ((TreeItem item, OutlinerNode node) in _nodes)
        {
            if (node.Kind == NodeKind.Object)
            {
                ObjectRef r = node.Members[0];
                if (!rows.TryGetValue(r, out ObjectRow row))
                {
                    continue;
                }

                item.SetText(0, r.Id);
                item.SetTooltipText(0, Tooltip(row));
                item.SetEditable(0, false);
                SetButtons(item, row.Meta.Hidden, row.Meta.Locked);
                bool hidden = ObjectInfo.IsHidden(p, row.Meta);
                item.SetCustomColor(0, hidden ? EditorTheme.TextHidden : EditorTheme.Text);
                item.SetIconModulate(0, hidden ? EditorTheme.TextHidden : Colors.White);
                continue;
            }

            item.SetEditable(0, false);
            // Also puts back the name a rejected rename left in the row.
            item.SetText(0, $"{NodeName(node)} ({node.Members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

            if (node.Kind == NodeKind.Layer)
            {
                Layer? layer = ObjectInfo.FindLayer(p, node.Layer);
                SetButtons(item, layer is { Visible: false }, layer is { Locked: true });
                continue;
            }

            var metas = node.Members.Select(m => rows.TryGetValue(m, out ObjectRow row) ? row.Meta : null).OfType<EditorMeta>().ToList();
            SetButtons(item, metas.Count > 0 && metas.All(m => m.Hidden), metas.Count > 0 && metas.All(m => m.Locked));
        }
    }

    /// <summary>A layer, folder or class row's name, without its count.</summary>
    private static string NodeName(OutlinerNode node) => node.Kind switch
    {
        NodeKind.Layer => node.Layer,
        NodeKind.Folder => node.Folder[(node.Folder.LastIndexOf('/') + 1)..],
        _ => node.Key[(node.Key.LastIndexOf('|') + 1)..],
    };

    private void SetButtons(TreeItem item, bool hidden, bool locked)
    {
        RowStateButtons.Set(item, 1, hidden, locked, hot: item == _tree.Hot);
    }

    private static string Tooltip(ObjectRow row)
    {
        var lines = new List<string> { $"{row.Ref.Id}  ({row.Class})" };
        if (row.Tag.Length > 0)
        {
            lines.Add("Tag: " + row.Tag);
        }

        if (row.Event.Length > 0)
        {
            lines.Add("Event: " + row.Event);
        }

        if (row.Group.Length > 0)
        {
            lines.Add("Group: " + row.Group);
        }

        if (row.Meta.Note is { Length: > 0 } note)
        {
            lines.Add(note);
        }

        return string.Join('\n', lines);
    }

    // ---- selection ------------------------------------------------------------------------------------------------

    /// <summary>Tree selection to the editor: object rows, plus every object under a selected layer, folder or group.</summary>
    private void PushSelection()
    {
        var refs = new List<ObjectRef>();
        foreach (TreeItem item in _tree.SelectedItems())
        {
            if (_nodes.TryGetValue(item, out OutlinerNode? node))
            {
                refs.AddRange(node.Members);
            }
        }

        _building = true;
        try
        {
            Context.Selection.Set(refs);
        }
        finally
        {
            _building = false;
        }

        UpdateStatus();
    }

    /// <summary>Editor selection to the tree; reveals and scrolls to the primary object.</summary>
    private void PullSelection()
    {
        if (_building)
        {
            return;
        }

        _building = true;
        try
        {
            _tree.DeselectAll();
            foreach (ObjectRef r in Context.Selection.Items)
            {
                if (_objectItems.TryGetValue(r, out TreeItem? item))
                {
                    item.Select(0);
                }
            }

            if (Context.Selection.Primary is { } primary && _objectItems.TryGetValue(primary, out TreeItem? p))
            {
                p.UncollapseTree();
                _tree.ScrollToItem(p);
            }
        }
        finally
        {
            _building = false;
        }

        UpdateStatus();
    }

    private List<ObjectRef> SelectedRefs() => _tree.SelectedItems()
        .Where(_nodes.ContainsKey).SelectMany(i => _nodes[i].Members).Distinct().ToList();

    private void FocusSelected()
    {
        List<ObjectRef> refs = SelectedRefs();
        if (refs.Count > 0)
        {
            Context.Focus(refs);
        }
    }

    // ---- eye / lock -----------------------------------------------------------------------------------------------

    private void OnButton(TreeItem item, long column, long id, long mouseButton)
    {
        if (mouseButton != (long)MouseButton.Left || !_nodes.TryGetValue(item, out OutlinerNode? node))
        {
            return;
        }

        MapProject p = Context.Project;
        if (node.Kind == NodeKind.Layer)
        {
            Context.Execute(PanelCommands.ToggleLayer(p, node.Layer, id == ButtonEye));
            return;
        }

        // Clicking a selected object's eye or lock applies to the whole selection.
        IReadOnlyList<ObjectRef> targets = node.Kind == NodeKind.Object && Context.Selection.Contains(node.Members[0])
            ? Context.Selection.Items
            : node.Members;
        Dictionary<ObjectRef, ObjectRow> rows = ObjectInfo.Lookup(p);
        var metas = targets.Select(r => rows.TryGetValue(r, out ObjectRow row) ? row.Meta : null).OfType<EditorMeta>().ToList();
        if (id == ButtonEye)
        {
            SetHidden(targets, !metas.All(m => m.Hidden));
        }
        else
        {
            SetLocked(targets, !metas.All(m => m.Locked));
        }
    }

    private void SetHidden(IEnumerable<ObjectRef> refs, bool hidden)
    {
        var list = refs.ToList();
        if (list.Count > 0)
        {
            Context.Execute(Edits.SetMeta(Context.Project, list, hidden ? "Hide" : "Show", m => m.Hidden = hidden));
        }
    }

    private void SetLocked(IEnumerable<ObjectRef> refs, bool locked)
    {
        var list = refs.ToList();
        if (list.Count > 0)
        {
            Context.Execute(Edits.SetMeta(Context.Project, list, locked ? "Lock" : "Unlock", m => m.Locked = locked));
        }
    }

    private void ShowAll()
    {
        MapProject p = Context.Project;
        SetHidden(ObjectInfo.All(p).Where(r => r.Meta.Hidden).Select(r => r.Ref), false);
    }

    private void UnlockAll()
    {
        MapProject p = Context.Project;
        SetLocked(ObjectInfo.All(p).Where(r => r.Meta.Locked).Select(r => r.Ref), false);
    }

    /// <summary>Selects every object that shares a UE2 group with a selected object (RE+ groups move together).</summary>
    private void SelectGroups()
    {
        MapProject p = Context.Project;
        Dictionary<ObjectRef, ObjectRow> rows = ObjectInfo.Lookup(p);
        var groups = Context.Selection.Items.Where(rows.ContainsKey).SelectMany(r => rows[r].Group.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0)
        {
            Context.SetStatus("The selection is in no group.");
            return;
        }

        Context.Selection.Set(rows.Values.Where(r => r.Group.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(groups.Contains)).Select(r => r.Ref));
        Context.SetStatus($"Selected groups {string.Join(", ", groups.Order(StringComparer.OrdinalIgnoreCase))}.");
    }

    private void Isolate()
    {
        MapProject p = Context.Project;
        var keep = new HashSet<ObjectRef>(Context.Selection.Items);
        var parts = new List<IEditCommand>();
        var hide = ObjectInfo.All(p).Where(r => !keep.Contains(r.Ref) && !r.Meta.Hidden).Select(r => r.Ref).ToList();
        var show = ObjectInfo.All(p).Where(r => keep.Contains(r.Ref) && r.Meta.Hidden).Select(r => r.Ref).ToList();
        if (hide.Count > 0)
        {
            parts.Add(Edits.SetMeta(p, hide, "Hide others", m => m.Hidden = true));
        }

        if (show.Count > 0)
        {
            parts.Add(Edits.SetMeta(p, show, "Show selected", m => m.Hidden = false));
        }

        if (parts.Count > 0)
        {
            Context.Execute(new CompositeCommand("Isolate selection", parts));
        }
    }

    // ---- rename ---------------------------------------------------------------------------------------------------

    /// <summary>Starts an inline rename of the row: the tag of an object, the name of a folder or layer.</summary>
    private void BeginRename()
    {
        TreeItem? item = _tree.GetSelected() ?? _tree.SelectedItems().FirstOrDefault();
        if (item is null || !_nodes.TryGetValue(item, out OutlinerNode? node) || node.Kind == NodeKind.Group)
        {
            return;
        }

        // Renaming an object edits its tag, in the wide name column (the row shows the id again afterwards).
        if (node.Kind == NodeKind.Object && ObjectInfo.Find(Context.Project, node.Members[0]) is { } row)
        {
            if (row.Ref.Kind == ObjectKind.Ge)
            {
                return;
            }

            item.SetText(0, row.Tag);
        }
        else
        {
            item.SetText(0, NodeName(node));
        }

        _renaming = true;

        item.SetEditable(0, true);
        _tree.SetSelected(item, 0);
        _tree.EditSelected(true);
    }

    private void OnEdited()
    {
        TreeItem? item = _tree.GetEdited();
        if (item is null || !_nodes.TryGetValue(item, out OutlinerNode? node))
        {
            return;
        }

        _renaming = false;
        int column = _tree.GetEditedColumn();
        string text = item.GetText(column).Trim();
        item.SetEditable(column, false);
        MapProject p = Context.Project;
        switch (node.Kind)
        {
            case NodeKind.Object:
                item.SetText(0, node.Members[0].Id);
                RenameTag(p, node.Members[0], text);
                break;
            case NodeKind.Folder when text.Length > 0 && !text.Contains('/'):
                RenameFolder(p, node, text);
                break;
            case NodeKind.Layer when text.Length > 0 && !string.Equals(text, node.Layer, StringComparison.Ordinal):
                if (ObjectInfo.IsDefaultLayer(node.Layer) || ObjectInfo.IsDefaultLayer(text))
                {
                    Context.Log("The Default layer cannot be renamed, and no other layer can take its name.", OpenScct.Editor.Core.Services.Severity.Warning);
                    break;
                }

                if (ObjectInfo.FindLayer(p, text) is not null)
                {
                    Context.Log($"A layer named {text} already exists.", OpenScct.Editor.Core.Services.Severity.Warning);
                    break;
                }

                Context.Execute(PanelCommands.RenameLayer(p, node.Layer, text));
                break;
        }

        _update.Queue();
    }

    private void RenameTag(MapProject p, ObjectRef r, string tag)
    {
        // Enter on the unchanged tag is not an edit.
        if (ObjectInfo.Find(p, r) is not { } row || string.Equals(tag, row.Tag, StringComparison.Ordinal))
        {
            return;
        }

        // An empty tag, or the class name, is the UE2 default: store nothing.
        var cmd = ReplaceObjectsCommand.Edit(p, [r], $"Rename tag of {r.Id}",
            brush: b => b.Props["Tag"] = tag.Length == 0 ? null : OpenScct.Content.Json.SvJsonNode.String(tag),
            actor: a => a.Props["Tag"] = tag.Length == 0 || string.Equals(tag, a.ShortClass, StringComparison.Ordinal)
                ? null
                : OpenScct.Content.Json.SvJsonNode.String(tag));
        Context.Execute(cmd);
    }

    private void RenameFolder(MapProject p, OutlinerNode node, string newName)
    {
        string old = node.Folder;
        string parent = old.Contains('/') ? old[..old.LastIndexOf('/')] + "/" : "";
        string renamed = parent + newName;
        if (string.Equals(old, renamed, StringComparison.Ordinal))
        {
            return;
        }

        // Every object in the folder and its sub-folders on this layer, not only the rows the search shows.
        var members = ObjectInfo.All(p)
            .Where(r => string.Equals(ObjectInfo.LayerName(r.Meta), node.Layer, StringComparison.OrdinalIgnoreCase) && InFolder(r.Meta.Folder, old))
            .Select(r => r.Ref).ToList();
        Context.Execute(Edits.SetMeta(p, members, $"Rename folder {old} to {renamed}", m =>
        {
            string f = m.Folder.Trim('/');
            m.Folder = renamed + f[old.Length..];
        }));
    }

    /// <summary>True when <paramref name="folder"/> is <paramref name="path"/> or inside it (ignoring case).</summary>
    private static bool InFolder(string folder, string path)
    {
        string f = folder.Trim('/');
        return f.StartsWith(path, StringComparison.OrdinalIgnoreCase) && (f.Length == path.Length || f[path.Length] == '/');
    }

    // ---- folders, layers, drag and drop --------------------------------------------------------------------------

    private void NewFolderFromSelection()
    {
        MapProject p = Context.Project;
        List<ObjectRef> refs = Context.Selection.Items.ToList();
        if (refs.Count == 0)
        {
            return;
        }

        // The new folder goes inside the primary object's current folder.
        string parent = p.MetaOf(refs[^1])?.Folder.Trim('/') ?? "";
        var used = new HashSet<string>(ObjectInfo.All(p).Select(r => r.Meta.Folder.Trim('/')), StringComparer.OrdinalIgnoreCase);
        string path = "";
        for (int i = 1; path.Length == 0 || used.Contains(path); i++)
        {
            path = (parent.Length > 0 ? parent + "/" : "") + (i == 1 ? "New Folder" : $"New Folder {i}");
        }

        Context.Execute(Edits.SetMeta(p, refs, $"Move {refs.Count} to folder {path}", m => m.Folder = path));
        Context.Log($"Moved {refs.Count} objects to folder {path} (F2 renames it).");
    }

    private void MoveTo(IReadOnlyList<ObjectRef> refs, string layer, string folder)
    {
        MapProject p = Context.Project;
        if (refs.Count == 0)
        {
            return;
        }

        string value = ObjectInfo.LayerValue(layer);
        string where = folder.Length > 0 ? $"{layer}/{folder}" : layer;
        Context.Execute(Edits.SetMeta(p, refs, $"Move {refs.Count} to {where}", m =>
        {
            m.Layer = value;
            m.Folder = folder;
        }));
    }

    private (Variant Data, string Preview)? StartDrag(Vector2 at)
    {
        _dragging = SelectedRefs();
        if (_dragging.Count == 0)
        {
            return null;
        }

        string preview = _dragging.Count == 1 ? _dragging[0].Id : $"{_dragging.Count} objects";
        return (Variant.From(DragPrefix + _dragging.Count), preview);
    }

    private void OnDrop(Vector2 at, Variant data)
    {
        if (_tree.GetItemAtPosition(at) is not { } target || !_nodes.TryGetValue(target, out OutlinerNode? node))
        {
            return;
        }

        string folder = node.Kind == NodeKind.Layer ? "" : node.Folder;
        MoveTo(_dragging, node.Layer, folder);
    }

    // ---- context menu and keys ----------------------------------------------------------------------------------

    private enum MenuId
    {
        Focus,
        Hide,
        Show,
        Isolate,
        ShowAll,
        Lock,
        Unlock,
        SelectGroup,
        Rename,
        NewFolder,
        RemoveFromFolder,
        Delete,
        ExpandAll,
        CollapseAll,
    }

    private void BuildMenu()
    {
        _menu = new PopupMenu();
        _layerMenu = new PopupMenu { Name = "Layers" };
        _menu.AddChild(_layerMenu);
        void Item(string text, MenuId id, Key key = Key.None) => _menu.AddItem(text, (int)id, key);
        Item("Focus", MenuId.Focus, Key.F);
        _menu.AddSeparator();
        Item("Hide", MenuId.Hide, Key.H);
        Item("Show", MenuId.Show);
        Item("Isolate", MenuId.Isolate);
        Item("Show All", MenuId.ShowAll);
        Item("Lock", MenuId.Lock);
        Item("Unlock", MenuId.Unlock);
        Item("Select Group", MenuId.SelectGroup);
        _menu.AddSeparator();
        Item("Rename", MenuId.Rename, Key.F2);
        Item("New Folder from Selection", MenuId.NewFolder);
        Item("Remove from Folder", MenuId.RemoveFromFolder);
        _menu.AddSubmenuNodeItem("Move to Layer", _layerMenu);
        _menu.AddSeparator();
        Item("Expand All", MenuId.ExpandAll);
        Item("Collapse All", MenuId.CollapseAll);
        _menu.AddSeparator();
        Item("Delete", MenuId.Delete, Key.Delete);
        _menu.IdPressed += id => OnMenu((MenuId)(int)id);
        _layerMenu.IndexPressed += index =>
        {
            string layer = _layerMenu.GetItemText((int)index);
            MoveTo(Context.Selection.Items.ToList(), layer, "");
        };
        AddChild(_menu);
    }

    private void ShowMenu()
    {
        _layerMenu.Clear();
        MapProject p = Context.Project;
        foreach (string layer in ObjectInfo.LayerNames(p))
        {
            _layerMenu.AddItem(layer);
        }

        PanelUi.PopupAtMouse(_tree, _menu);
    }

    private void OnMenu(MenuId id)
    {
        List<ObjectRef> sel = Context.Selection.Items.ToList();
        switch (id)
        {
            case MenuId.Focus:
                FocusSelected();
                break;
            case MenuId.Hide:
                SetHidden(sel, true);
                break;
            case MenuId.Show:
                SetHidden(sel, false);
                break;
            case MenuId.Isolate:
                Isolate();
                break;
            case MenuId.ShowAll:
                ShowAll();
                break;
            case MenuId.Lock:
                SetLocked(sel, true);
                break;
            case MenuId.Unlock:
                SetLocked(sel, false);
                break;
            case MenuId.SelectGroup:
                SelectGroups();
                break;
            case MenuId.Rename:
                BeginRename();
                break;
            case MenuId.NewFolder:
                NewFolderFromSelection();
                break;
            case MenuId.RemoveFromFolder when sel.Count > 0:
                Context.Execute(Edits.SetMeta(Context.Project, sel, "Remove from folder", m => m.Folder = ""));
                break;
            case MenuId.Delete when sel.Count > 0:
                Context.Execute(PieceKeys.Delete(sel));
                break;
            case MenuId.ExpandAll:
            case MenuId.CollapseAll:
                SetAllCollapsed(id == MenuId.CollapseAll);
                break;
        }
    }

    private void SetAllCollapsed(bool collapsed)
    {
        foreach ((TreeItem item, OutlinerNode node) in _nodes)
        {
            if (node.Kind != NodeKind.Object)
            {
                item.Collapsed = collapsed;
                _collapsed[node.Key] = collapsed;
            }
        }
    }

    private void OnTreeKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key || key.CtrlPressed || key.AltPressed)
        {
            return;
        }

        switch (key.Keycode)
        {
            case Key.F2:
                BeginRename();
                break;
            case Key.F:
                FocusSelected();
                break;
            case Key.H:
                SetHidden(Context.Selection.Items.ToList(), !key.ShiftPressed);
                break;
            case Key.Delete when Context.Selection.Count > 0:
                Context.Execute(PieceKeys.Delete(Context.Selection.Items.ToList()));
                break;
            default:
                return;
        }

        _tree.AcceptEvent();
    }
}

/// <summary>How the outliner groups the objects of a folder.</summary>
internal enum Grouping
{
    Type,

    /// <summary>By the first UE2 Group the object is in.</summary>
    Group,
    None,
}

internal enum NodeKind
{
    Layer,
    Folder,
    Group,
    Object,
}

/// <summary>What an outliner row stands for: its key (stable across rebuilds, for fold state), layer, folder and the
/// objects it holds.</summary>
internal sealed record OutlinerNode(NodeKind Kind, string Key, string Layer, string Folder, IReadOnlyList<ObjectRef> Members);
