using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Panels;

/// <summary>
/// Copy and paste of properties between actors (tick what a paste takes: identity, placement and links to other
/// actors start unticked) and property sets: named presets per class from the map's propsets.json and the user
/// library, applied to the selection, saved from an actor, or imported from the original game's PropertiesSets.ini.
/// </summary>
[Panel("Properties", DockSide.Workbench, Order = 10)]
public partial class PropertiesPanel : EditorPanel
{
    private Label _copied = null!;
    private VBoxContainer _ticks = null!;
    private Label _setsTitle = null!;
    private Label _library = null!;
    private ItemList _sets = null!;
    private LineEdit _setName = null!;
    private CheckBox _inMap = null!;
    private DeferredAction _update = null!;
    private HBoxContainer _tickChoice = null!;
    private Control _setsEmpty = null!;
    private List<PropertySet> _shown = [];

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        VBoxContainer box = PanelUi.Scroll(this);
        PanelUi.Header(box, "Copy properties");
        HFlowContainer bar = PanelUi.Toolbar(box);
        WorkbenchUi.Action(bar, "Copy", "Copy the selected actor's stored values (Ctrl+Alt+C)", "edit.props.copy", Context).Icon = Icons.Get("copy", EditorTheme.Text);
        WorkbenchUi.Action(bar, "Paste", "Paste the ticked values onto the selected actors (Ctrl+Alt+V)", "edit.props.paste", Context).Icon = Icons.Get("paste", EditorTheme.Text);
        _tickChoice = PanelUi.Segmented(bar, [("All", "Tick every copied value"), ("Usual", "Tick all but identity, placement and links to other actors"),
            ("None", "Untick everything")], -1, i => TickAll(i switch
            {
                0 => _ => true,
                1 => k => PropertyCopy.DefaultKeys(PropertyClipboard.Copied!).Contains(k),
                _ => _ => false,
            }));
        _copied = PanelUi.Caption(box, "");
        _ticks = new VBoxContainer();
        _ticks.AddThemeConstantOverride("separation", 0);
        box.AddChild(_ticks);

        PanelUi.Header(box, "Property sets");
        _setsTitle = PanelUi.Caption(box, "");
        _sets = new ItemList { CustomMinimumSize = new Vector2(0, 160), SelectMode = ItemList.SelectModeEnum.Single };
        _sets.ItemActivated += _ => UseSet();
        box.AddChild(_sets);
        _setsEmpty = PanelUi.EmptyState(_sets, "property-sets", "No property sets", "Save an actor's values as a set, or import the original game's sets.");
        HFlowContainer setBar = PanelUi.Toolbar(box);
        WorkbenchUi.Guard(PanelUi.Button(setBar, "Use", "Give the selected actors the set's values", UseSet), () => ChosenSet is not null);
        WorkbenchUi.Guard(PanelUi.Button(setBar, "Delete", "Remove the set from where it is kept", DeleteSet), () => ChosenSet is not null);
        PanelUi.Button(setBar, "Import INI\u2026", "Read the original game's System/PropertiesSets.ini into your library", () => Context.Run("gameplay.propset.importIni"))
            .Icon = Icons.Get("import", EditorTheme.Text);
        var saveRow = new HBoxContainer();
        box.AddChild(saveRow);
        _setName = new LineEdit { PlaceholderText = "New set name", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(80, 0) };
        saveRow.AddChild(_setName);
        _inMap = new CheckBox { Text = "In this map", TooltipText = "Keep the set in the map's propsets.json (it travels with the map) instead of your library" };
        saveRow.AddChild(_inMap);
        WorkbenchUi.Guard(PanelUi.Button(saveRow, "Save", "Save the selected actor's values as a set (the ticked ones when it is the copied actor)", SaveSet),
            () => Primary is not null && _setName.Text.Trim().Length > 0);
        var library = new HBoxContainer();
        box.AddChild(library);
        _library = PanelUi.Caption(library, "", wrap: false);
        PanelUi.IconButton(library, "folder", "Show the library's folder", () => ExternalPrograms.OpenFolder(Path.GetDirectoryName(PropertySets.LibraryFile) ?? ""));

        Context.SelectionChanged += _update.Queue;
        Context.ProjectChanged += _ => _update.Queue();
        PropertyClipboard.Changed += _update.Queue;
    }

    public override void Refresh() => _update.Queue();

    private Actor? Primary => Context.Selection.Primary is { Kind: ObjectKind.Actor } r ? Context.Project.FindActor(r.Id) : null;

    private void Rebuild()
    {
        foreach (Node child in _ticks.GetChildren())
        {
            child.QueueFree();
        }

        if (PropertyClipboard.Copied is { } copy)
        {
            _copied.Text = $"From {copy.From} ({InteropShort(copy.Class)}): {PropertyClipboard.Keys.Count} of {PanelUi.Count(copy.Props.Count, "value")} paste.";
            foreach (KeyValuePair<string, SvJsonNode> kv in copy.Props)
            {
                string key = kv.Key;
                string value = CanonicalJson.ToCanonicalString(kv.Value);
                var tick = new CheckBox
                {
                    Text = $"{PanelUi.DisplayName(key)} = {value}",
                    TooltipText = $"{key} = {value}",
                    ButtonPressed = PropertyClipboard.Keys.Contains(key),
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    CustomMinimumSize = new Vector2(1, 0),
                };
                tick.Toggled += on => PropertyClipboard.Tick(key, on);
                _ticks.AddChild(tick);
            }
        }
        else
        {
            _copied.Text = "Nothing copied: select an actor and Copy.";
        }

        _tickChoice.Visible = PropertyClipboard.Copied is not null;

        _sets.Clear();
        Actor? a = Primary;
        try
        {
            _shown = a is null ? PropertySets.All(Context.Session.Folder) : PropertySets.For(a.Class, Context.Session.Folder);
            _setsTitle.Text = (a is null ? "Every set" : $"Sets for {a.ShortClass}") + $" ({_shown.Count})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OpenScct.Content.SvMapFormatException)
        {
            _shown = [];
            _setsTitle.Text = "Cannot read the property sets: " + ex.Message;
        }

        _library.Text = "Library: " + Path.GetFileName(PropertySets.LibraryFile);
        _library.TooltipText = PropertySets.LibraryFile;
        _setsEmpty.Visible = _shown.Count == 0;

        foreach (PropertySet s in _shown)
        {
            int i = _sets.AddItem($"{s.Key}{(s.Source == PropertySetSource.Map ? " (map)" : "")}");
            _sets.SetItemTooltip(i, string.Join("\n", s.Props.Select(kv => $"{kv.Key} = {CanonicalJson.ToCanonicalString(kv.Value)}")) + (s.Note is { } n ? $"\n({n})" : ""));
        }
    }

    private static string InteropShort(string cls) => cls[(cls.LastIndexOf('.') + 1)..];

    private void TickAll(Func<string, bool> on)
    {
        if (PropertyClipboard.Copied is not { } copy)
        {
            return;
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in copy.Props)
        {
            PropertyClipboard.Tick(kv.Key, on(kv.Key));
        }
    }

    private PropertySet? ChosenSet => _sets.GetSelectedItems() is { Length: > 0 } items && items[0] < _shown.Count ? _shown[items[0]] : null;

    private void UseSet()
    {
        if (ChosenSet is not { } set)
        {
            Context.SetStatus("Choose a set first");
            return;
        }

        var skipped = new List<string>();
        List<string> ids = WorkbenchPart.Selected(Context, _ => true).Select(x => x.Id).ToList();
        var cmd = PropertySets.Apply(Context.Project, set, ids, skipped);
        if (!cmd.IsEmpty)
        {
            Context.Execute(cmd);
        }

        Context.SetStatus($"Used {set.Key} on {ids.Count - skipped.Count} actors" + (skipped.Count > 0 ? $"; not a {InteropShort(set.Class)}: {string.Join(", ", skipped)}" : ""));
    }

    private void DeleteSet()
    {
        if (ChosenSet is not { } set)
        {
            return;
        }

        string? file = set.Source == PropertySetSource.Map ? PropertySets.MapFile(Context.Session.Folder) : PropertySets.LibraryFile;
        if (file is not null && PropertySets.Delete(file, set.Key))
        {
            Context.SetStatus($"Deleted {set.Key} from {file}");
        }

        _update.Queue();
    }

    private void SaveSet()
    {
        string name = _setName.Text.Trim();
        if (Primary is not { } a || name.Length == 0)
        {
            Context.SetStatus("Select an actor and name the set");
            return;
        }

        string? file = _inMap.ButtonPressed ? PropertySets.MapFile(Context.Session.Folder) : PropertySets.LibraryFile;
        if (file is null)
        {
            Context.SetStatus("Save the map first: a map's sets are kept in its folder");
            return;
        }

        IEnumerable<string>? keys = PropertyClipboard.Copied is { } copy && string.Equals(copy.From, a.Id, StringComparison.OrdinalIgnoreCase) ? PropertyClipboard.Keys : null;
        PropertySet set = PropertySets.FromActor(a, name, keys, _inMap.ButtonPressed ? PropertySetSource.Map : PropertySetSource.Library);
        try
        {
            PropertySets.Save(file, [set]);
            Context.SetStatus($"Saved {set.Key} ({set.Props.Count} values) to {file}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Context.Log($"Cannot save {set.Key}: {ex.Message}", Severity.Error);
        }

        _update.Queue();
    }
}
