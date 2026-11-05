using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Layers, "like layers in Photoshop": visibility and lock per layer, object counts, a colour, rename (F2 or
/// double-click), new and delete (objects go to the default layer, the first one), move the selection to a layer,
/// and select a layer's objects. Every change is one undo step.
/// </summary>
[Panel("Layers", DockSide.Left, Order = 1)]
public partial class LayersPanel : EditorPanel
{
    private static readonly (string Name, string? Hex)[] s_colours =
    [
        ("No colour", null), ("Red", "#e05a5a"), ("Orange", "#e8a040"), ("Yellow", "#e0d050"), ("Green", "#5ac46a"),
        ("Cyan", "#4cc0d0"), ("Blue", "#5490f4"), ("Purple", "#a070e0"), ("Grey", "#9098a8"),
    ];

    private OutlinerTree _tree = null!;
    private PopupMenu _colourMenu = null!;
    private Button _delete = null!;
    private Button _moveHere = null!;
    private Button _select = null!;
    private DeferredAction _update = null!;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        // One row: add, the commands on the chosen layer, and delete in the trailing slot, as in the other lists.
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        AddChild(bar);
        PanelUi.IconButton(bar, "plus", "New layer", AddLayer);
        _moveHere = PanelUi.Button(bar, "Move here", "Move the selected objects to the chosen layer", MoveSelectionHere);
        _select = PanelUi.Button(bar, "Select", "Select every object on the chosen layer", SelectObjects);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _delete = PanelUi.IconButton(bar, "trash", "Delete the chosen layer; its objects move to the default layer", DeleteLayer, danger: true);

        _tree = new OutlinerTree
        {
            Name = "Tree",
            StateColumn = 1,
            Columns = 2,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AllowRmbSelect = true,
        };
        _tree.SetColumnExpand(1, false);
        _tree.SetColumnCustomMinimumWidth(1, 110);
        _tree.ButtonClicked += OnButton;
        _tree.ItemSelected += UpdateButtons;
        _tree.ItemActivated += BeginRename;
        _tree.ItemEdited += OnEdited;
        _tree.ItemMouseSelected += (_, button) =>
        {
            if (button == (long)MouseButton.Right)
            {
                PanelUi.PopupAtMouse(_tree, _colourMenu);
            }
        };
        _tree.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.F2 })
            {
                BeginRename();
                _tree.AcceptEvent();
            }
        };
        AddChild(_tree);

        _colourMenu = new PopupMenu();
        for (int i = 0; i < s_colours.Length; i++)
        {
            (string name, string? hex) = s_colours[i];
            if (hex is null)
            {
                _colourMenu.AddItem(name, i);
            }
            else
            {
                _colourMenu.AddIconItem(PanelIcons.Dot(Color.FromHtml(hex)), name, i);
            }
        }

        _colourMenu.IdPressed += id => SetColour(s_colours[(int)id].Hex);
        AddChild(_colourMenu);

        PanelUi.Caption(this, "Objects of a deleted layer go to Default, which cannot be renamed or deleted. Right-click a layer for its colour; double-click or F2 renames it.");

        Context.ProjectChanged += _ => _update.Queue();
        Context.SelectionChanged += UpdateButtons;
        Context.AddAction(new EditorAction("layers.new", "New Layer", "Edit", AddLayer) { Order = 400, Description = "Add a layer" });
        Context.AddAction(new EditorAction("layers.moveSelection", "Move to Layer", "Edit", MoveSelectionHere)
        {
            Order = 410, Description = "Move the selected objects to the layer chosen in the Layers panel",
            IsEnabled = () => Context.Selection.Count > 0,
        });
    }

    public override void Refresh() => _update.Queue();

    private void Rebuild()
    {
        MapProject p = Context.Project;
        string? selected = SelectedLayer();
        _tree.ForgetHot();
        var counts = ObjectInfo.All(p).GroupBy(r => ObjectInfo.LayerName(r.Meta), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        // A list without the default layer shows it first anyway; its buttons add it to the list.
        List<Layer> layers = p.Layers.Exists(l => ObjectInfo.IsDefaultLayer(l.Name)) ? p.Layers : [new Layer(ObjectInfo.DefaultLayer), .. p.Layers];
        for (int i = 0; i < layers.Count; i++)
        {
            Layer l = layers[i];
            TreeItem item = _tree.CreateItem(root);
            item.SetText(0, l.Name);
            item.SetMetadata(0, l.Name);
            // The layer's colour tints its icon; a layer without one has a dim icon like the outliner's.
            item.SetIcon(0, Icons.Get("type-layer", l.Color is { Length: > 0 } hex && Color.HtmlIsValid(hex) ? Color.FromHtml(hex) : EditorTheme.TextDim));
            if (ObjectInfo.IsDefaultLayer(l.Name))
            {
                item.SetTooltipText(0, "The default layer: new objects and the objects of deleted layers go here. It cannot be renamed or deleted.");
            }

            item.SetText(1, counts.GetValueOrDefault(l.Name).ToString(CultureInfo.InvariantCulture));
            item.SetTextAlignment(1, HorizontalAlignment.Right);
            item.SetCustomColor(1, EditorTheme.TextDim);
            RowStateButtons.Add(item, 1, "Show or hide the layer", "Lock or unlock the layer");
            RowStateButtons.Set(item, 1, hidden: !l.Visible, locked: l.Locked, hot: false);
            item.SetCustomColor(0, l.Visible ? EditorTheme.Text : EditorTheme.TextHidden);
            if (string.Equals(l.Name, selected, StringComparison.OrdinalIgnoreCase))
            {
                item.Select(0);
            }
        }

        // Objects that name a layer the list lacks: offer to add it.
        foreach ((string name, int count) in counts.Where(kv => !layers.Exists(l => string.Equals(l.Name, kv.Key, StringComparison.OrdinalIgnoreCase))))
        {
            TreeItem item = _tree.CreateItem(root);
            item.SetText(0, name + " (missing)");
            item.SetMetadata(0, name);
            item.SetText(1, count.ToString(CultureInfo.InvariantCulture));
            item.SetCustomColor(0, EditorTheme.Warning);
            item.SetTooltipText(0, "Objects name this layer but it is not in the list. Rename it to add it.");
        }

        UpdateButtons();
    }

    /// <summary>Enables the buttons that have something to act on.</summary>
    private void UpdateButtons()
    {
        string? layer = SelectedLayer();
        _delete.Disabled = layer is null || ObjectInfo.IsDefaultLayer(layer);
        _moveHere.Disabled = layer is null || Context.Selection.Count == 0;
        _select.Disabled = layer is null;
    }

    private string? SelectedLayer() => _tree.GetSelected()?.GetMetadata(0).AsString();

    private void AddLayer()
    {
        MapProject p = Context.Project;
        string name = PanelCommands.NewLayerName(p);
        Context.Execute(PanelCommands.Layers(p, $"New layer {name}", list =>
        {
            if (!list.Exists(l => ObjectInfo.IsDefaultLayer(l.Name)))
            {
                list.Insert(0, new Layer(ObjectInfo.DefaultLayer));
            }

            list.Add(new Layer(name));
        }));
    }

    private void DeleteLayer()
    {
        MapProject p = Context.Project;
        if (SelectedLayer() is not { } name)
        {
            return;
        }

        if (ObjectInfo.IsDefaultLayer(name))
        {
            Context.Log("The Default layer cannot be deleted.", Severity.Warning);
            return;
        }

        Context.Execute(PanelCommands.DeleteLayer(p, name));
    }

    private void MoveSelectionHere()
    {
        MapProject p = Context.Project;
        if (SelectedLayer() is not { } name || Context.Selection.Count == 0)
        {
            return;
        }

        string value = ObjectInfo.LayerValue(name);
        Context.Execute(OpenScct.Editor.Core.Commands.Edits.SetMeta(p, Context.Selection.Items.ToList(),
            $"Move {Context.Selection.Count} to layer {name}", m => m.Layer = value));
    }

    private void SelectObjects()
    {
        MapProject p = Context.Project;
        if (SelectedLayer() is { } name)
        {
            Context.Selection.Set(ObjectInfo.All(p)
                .Where(r => string.Equals(ObjectInfo.LayerName(r.Meta), name, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Ref));
        }
    }

    private void OnButton(TreeItem item, long column, long id, long mouseButton)
    {
        if (mouseButton == (long)MouseButton.Left)
        {
            Context.Execute(PanelCommands.ToggleLayer(Context.Project, item.GetMetadata(0).AsString(), id == RowStateButtons.Eye));
        }
    }

    private void SetColour(string? hex)
    {
        if (SelectedLayer() is not { } name)
        {
            return;
        }

        Context.Execute(PanelCommands.Layers(Context.Project, "Set layer colour", list =>
        {
            if (list.Find(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } layer)
            {
                layer = new Layer(name);
                list.Add(layer);
            }

            layer.Color = hex;
        }));
    }

    private void BeginRename()
    {
        if (_tree.GetSelected() is not { } item)
        {
            return;
        }

        item.SetText(0, item.GetMetadata(0).AsString());
        item.SetEditable(0, true);
        _tree.EditSelected(true);
    }

    private void OnEdited()
    {
        if (_tree.GetEdited() is not { } item)
        {
            return;
        }

        MapProject p = Context.Project;
        string oldName = item.GetMetadata(0).AsString();
        string newName = item.GetText(0).Trim();
        item.SetEditable(0, false);
        if (newName.Length == 0 || string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            _update.Queue();
            return;
        }

        if (ObjectInfo.IsDefaultLayer(oldName) || ObjectInfo.IsDefaultLayer(newName))
        {
            Context.Log("The Default layer cannot be renamed, and no other layer can take its name.", Severity.Warning);
            _update.Queue();
            return;
        }

        if (ObjectInfo.FindLayer(p, newName) is not null && !string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase))
        {
            Context.Log($"A layer named {newName} already exists.", Severity.Warning);
            _update.Queue();
            return;
        }

        if (ObjectInfo.FindLayer(p, oldName) is null)
        {
            // A missing layer: add it under the new name and move its objects there.
            Context.Execute(new OpenScct.Editor.Core.Commands.CompositeCommand($"Add layer {newName}",
            [
                PanelCommands.Layers(p, "Add layer", list => list.Add(new Layer(newName))),
                OpenScct.Editor.Core.Commands.Edits.SetMeta(p,
                    ObjectInfo.All(p).Where(r => string.Equals(r.Meta.Layer, oldName, StringComparison.OrdinalIgnoreCase)).Select(r => r.Ref).ToList(),
                    "Move to layer", m => m.Layer = newName),
            ]));
            return;
        }

        Context.Execute(PanelCommands.RenameLayer(p, oldName, newName));
    }
}
