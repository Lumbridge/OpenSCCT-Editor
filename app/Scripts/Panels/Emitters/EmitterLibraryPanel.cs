using Godot;
using OpenScct.Editor.Core.Emitters;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Panels;

/// <summary>Browse, preview, edit and place particle effects. Edits stay in a draft until placed or saved.</summary>
[Panel(Title, DockSide.Left, Order = 5)]
public partial class EmitterLibraryPanel : EditorPanel
{
    private const string Title = "Emitter Library";
    private IReadOnlyList<EmitterPreset> _presets = [];
    private EmitterPreset? _selected;
    private EmitterPreset? _draft;
    private LineEdit _search = null!;
    private Tree _tree = null!;
    private VBoxContainer _editForm = null!;
    private EmitterPreview _preview = null!;
    private Label _status = null!;
    private Label _description = null!;
    private Button _place = null!;
    private Button _edit = null!;
    private Button _replay = null!;
    private Button _getSourcePack = null!;
    private CheckBox _loop = null!;
    private HFlowContainer _wait = null!;
    private SpinBox _min = null!;
    private SpinBox _max = null!;
    private DeferredAction _updatePreview = null!;
    private bool _loaded;
    private int _revision;

    protected override void Build()
    {
        _updatePreview = new DeferredAction(Replay);
        _search = PanelUi.Search(this, "Search effects", _ => Fill());
        _search.Name = "EffectSearch";
        HFlowContainer tools = PanelUi.Toolbar(this);
        PanelUi.Button(tools, "Import Pack…", "Install a read-only RE+ effect pack", ImportPack);
        PanelUi.IconButton(tools, "refresh", "Read stock, saved and installed effects again", () => Reload());

        _status = PanelUi.Caption(this, "");
        var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        _tree = new Tree { Name = "Effects", HideRoot = true, CustomMinimumSize = new Vector2(0, 92), SizeFlagsVertical = SizeFlags.ExpandFill };
        _tree.ItemSelected += Pick;
        _tree.ItemActivated += Place;
        split.AddChild(_tree);

        var detailPane = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        split.AddChild(detailPane);
        split.SplitOffsets = [-96];
        _preview = new EmitterPreview { Name = "EffectPreview" };
        detailPane.AddChild(_preview);
        HFlowContainer actions = PanelUi.Toolbar(detailPane);
        _place = PanelUi.Primary(actions, "Place at Brush", "Place the effect at the builder brush as one undo step", Place);
        _replay = PanelUi.Button(actions, "Replay", "Restart the effect in the preview", Replay);
        _edit = PanelUi.Button(actions, "Edit…", "Edit the effect before placing or saving a copy", ToggleEditor);
        _getSourcePack = PanelUi.Button(actions, "Get Source Pack", "Find the map that contains this effect", GetSourcePack);
        _getSourcePack.Name = "GetEmitterSourcePack";

        VBoxContainer details = PanelUi.Scroll(detailPane);
        PanelUi.ScrollOf(details).CustomMinimumSize = new Vector2(0, 65);
        _loop = new CheckBox { Text = "Loop", TooltipText = "Each particle system waits after its particles finish, then starts again" };
        details.AddChild(_loop);
        _wait = PanelUi.Toolbar(details);
        PanelUi.Caption(_wait, "Wait (s)", wrap: false);
        _min = PanelUi.Spin(_wait, 0, 3600, 0.1, 5, "Shortest wait after a particle system ends");
        _max = PanelUi.Spin(_wait, 0, 3600, 0.1, 10, "Longest wait after a particle system ends");
        _loop.Toggled += on => { _wait.Visible = on; _updatePreview.Queue(); };
        _min.ValueChanged += v => { if (v > _max.Value) _max.SetValueNoSignal(v); _updatePreview.Queue(); };
        _max.ValueChanged += v => { if (v < _min.Value) _min.SetValueNoSignal(v); _updatePreview.Queue(); };
        _wait.Hide();

        _editForm = new VBoxContainer();
        details.AddChild(_editForm);
        _editForm.Hide();
        _description = PanelUi.Caption(details, "Choose an effect to preview it.");
        VisibilityChanged += () =>
        {
            LoadWhenVisible();
            if (IsVisibleInTree() && _draft is not null) _updatePreview.Queue();
        };
        AssetService.Instance.PacksChanged += PacksChanged;
        TreeExiting += () => AssetService.Instance.PacksChanged -= PacksChanged;
        Context.AddAction(new EditorAction("emitter.library", Title, "Tools", () =>
        {
            if (DockTabs.ShowPanel(Title)) _search.GrabFocus();
        }) { Description = "Browse and preview particle effects" });
        Context.AddAction(new EditorAction("emitter.saveSelection", "Save Selected Emitters…", "Tools", SaveSelected)
        {
            IsEnabled = () => SelectedEmitters().Any(),
            Description = "Capture the selected particle effects in the emitter library",
        });
        ShowEffect();
    }

    public override void Refresh()
    {
        LoadWhenVisible();
        if (_loaded && IsVisibleInTree()) _updatePreview.Queue();
    }

    private void LoadWhenVisible()
    {
        if (!_loaded && IsVisibleInTree()) Reload();
    }

    private void PacksChanged()
    {
        _revision++;
        _loaded = false;
        LoadWhenVisible();
    }

    private void Reload(string? select = null)
    {
        _loaded = true;
        _status.Text = "Reading effects…";
        string? content = EditorServices.ContentRoot;
        int revision = ++_revision;
        BackgroundJobs.Start(_ =>
        {
            try
            {
                EmitterLibraryResult library = EmitterLibrary.Load(content);
                BackgroundJobs.Post(() =>
                {
                    if (revision != _revision) return;
                    _presets = library.Presets;
                    foreach (string warning in library.Warnings) Context.Log($"{Title}: {warning}", Severity.Warning);
                    if (select is not null) _search.Text = "";
                    bool replace = select is not null || _selected is { } previous &&
                        _presets.FirstOrDefault(p => p.Id == previous.Id) is { } current &&
                        (ReferenceEquals(_draft, previous) || current.UnavailableReason != previous.UnavailableReason);
                    Fill(select ?? _selected?.Id, replace);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                BackgroundJobs.Post(() => { if (revision == _revision) ReportError(ex); });
            }
        });
    }

    private void Fill(string? select = null, bool replace = false)
    {
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        TreeItem? selectedItem = null;
        int count = 0;
        string filter = _search.Text.Trim();
        string[] words = filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var group in _presets.Where(p => Matches(p, words)).GroupBy(p => p.Group, StringComparer.OrdinalIgnoreCase))
        {
            TreeItem heading = Heading(root, group.Key);
            foreach (var category in group.GroupBy(p => p.Category, StringComparer.OrdinalIgnoreCase))
            {
                TreeItem branch = Heading(heading, category.Key);
                foreach (EmitterPreset preset in category.OrderBy(p => SearchRank(p, filter, words)))
                {
                    TreeItem row = _tree.CreateItem(branch);
                    row.SetText(0, preset.Name);
                    row.SetIcon(0, Icons.Get("type-emitter", EditorTheme.TextDim));
                    row.SetTooltipText(0, preset.UnavailableReason is { } problem ? $"{preset.Description}\n{problem}" : preset.Description);
                    if (preset.UnavailableReason is not null) row.SetCustomColor(0, EditorTheme.TextDim);
                    row.SetMetadata(0, preset.Id);
                    if (preset.Id == (select ?? _selected?.Id)) selectedItem = row;
                    count++;
                }
            }
        }

        _status.Text = count > 0 ? $"{count} {(count == 1 ? "effect" : "effects")}" : filter.Length > 0
            ? "No effect matches the search." : "No effects found. Install the map content or import an effect pack.";
        if (selectedItem is not null)
        {
            if (replace) _selected = null;
            selectedItem.Select(0);
            _tree.ScrollToItem(selectedItem);
            Pick();
        }
        else if (_selected is not null)
        {
            _selected = _draft = null;
            ShowEffect();
        }
    }

    private static bool Matches(EmitterPreset preset, string[] words)
    {
        string search = $"{preset.Name} {preset.Category} {preset.Group} {preset.Description}";
        return words.All(word => search.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static int SearchRank(EmitterPreset preset, string filter, string[] words)
    {
        if (filter.Length == 0 || preset.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)) return 0;
        string identity = $"{preset.Name} {preset.Category} {preset.Group}";
        return words.All(word => identity.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 1 : 2;
    }

    private TreeItem Heading(TreeItem parent, string text)
    {
        TreeItem row = _tree.CreateItem(parent);
        row.SetText(0, text);
        row.SetSelectable(0, false);
        return row;
    }

    private void Pick()
    {
        string? id = _tree.GetSelected()?.GetMetadata(0).AsString();
        EmitterPreset? preset = _presets.FirstOrDefault(p => p.Id == id);
        if (preset?.Id == _selected?.Id) return;
        _selected = _draft = preset;
        ShowEffect();
    }

    private void ShowEffect()
    {
        _description.Text = _draft is { } effect ? $"{effect.Name} · {effect.Mode}\n{effect.Description}" : "Choose an effect to preview it.";
        if (_draft?.UnavailableReason is { } unavailable) _description.Text += $"\n{unavailable}";
        _place.Disabled = _replay.Disabled = _edit.Disabled = _loop.Disabled = _draft is null || _draft.UnavailableReason is not null;
        _getSourcePack.Visible = MissingSourceMap is not null;
        _edit.Text = "Edit…";
        _editForm.Hide();
        _description.Show();
        _updatePreview.Queue();
    }

    private string? MissingSourceMap => _draft is { UnavailableReason: not null } ? _draft.SourceMap : null;

    private void GetSourcePack()
    {
        if (MissingSourceMap is { } map)
        {
            ContentPacksPart.Open(map);
        }
    }

    private void Replay()
    {
        if (!IsVisibleInTree()) return;
        try
        {
            EmitterPreset? draft = _draft?.UnavailableReason is null ? _draft : null;
            _preview.Play(draft is not null && _loop.ButtonPressed ? EmitterPlacement.Loop(draft, _min.Value, _max.Value) : draft);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or IOException)
        {
            ReportError(ex);
        }
    }

    private void Place()
    {
        if (_draft is not { UnavailableReason: null } preset || _tree.GetSelected() is null) return;
        Vec3 at = ToolManager.Instance?.Builder.Location ?? Context.PlacementPoint?.Invoke() ?? Context.Project.Workspace.CameraLocation;
        try
        {
            var placement = EmitterPlacement.Place(Context.Project, preset, at, _loop.ButtonPressed ? _min.Value : null,
                _loop.ButtonPressed ? _max.Value : null);
            Context.Execute(placement.Command);
            Context.Selection.Set(placement.Actors.Select(a => ObjectRef.Actor(a.Id)));
            Context.SetStatus($"Placed {preset.Name} at the builder brush");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            ReportError(ex);
        }
    }

    private IEnumerable<Actor> SelectedEmitters() => Context.Selection.Items.Where(r => r.Kind == ObjectKind.Actor)
        .Select(r => Context.Project.FindActor(r.Id)).OfType<Actor>().Where(a => EmitterPlacement.CanCapture(a, Context.Project));

    private void SaveSelected()
    {
        Actor[] actors = SelectedEmitters().ToArray();
        if (actors.Length == 0) return;
        try
        {
            SaveDialog(EmitterPlacement.Capture(actors[0].Id, "Other", actors, Context.Project));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            ReportError(ex);
        }
    }

    private void SaveDialog(EmitterPreset draft)
    {
        var dialog = new ConfirmationDialog { Title = "Save as New Effect", OkButtonText = "Save", Exclusive = true };
        var form = new VBoxContainer { CustomMinimumSize = new Vector2(350, 0) };
        dialog.AddChild(form);
        LineEdit name = TextField(form, "Name", draft.Name);
        LineEdit category = TextField(form, "Category", draft.Category);
        LineEdit description = TextField(form, "Description", draft.Description);
        name.TextChanged += t => dialog.GetOkButton().Disabled = string.IsNullOrWhiteSpace(t);
        dialog.Confirmed += () =>
        {
            Save(draft with { Id = "", Group = EmitterLibrary.UserGroup, Name = name.Text.Trim(),
                Category = category.Text.Trim(), Description = description.Text.Trim() });
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        GetWindow().AddChild(dialog);
        dialog.PopupCentered();
        name.GrabFocus();
        name.SelectAll();
    }

    private static LineEdit TextField(Control parent, string label, string value)
    {
        var edit = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        PanelUi.Row(parent, label, labelWidth: 78).AddChild(edit);
        return edit;
    }

    private void Save(EmitterPreset draft)
    {
        try
        {
            EmitterPreset saved = EmitterLibrary.Save(draft);
            DockTabs.ShowPanel(Title);
            Reload(saved.Id);
            Context.SetStatus($"Saved {saved.Name} to the emitter library");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            ReportError(ex);
        }
    }

    private void ImportPack() => Dialogs.OpenFile("Import Emitter Pack", ["*.json ; Emitter packs"], path =>
    {
        string? content = EditorServices.ContentRoot;
        BackgroundJobs.Run("Import emitter pack", () => EmitterLibrary.ImportPack(path, content), name =>
        {
            Reload();
            Context.SetStatus($"Imported {name}");
        });
    });

    private void ReportError(Exception ex)
    {
        _status.Text = ex.Message;
        Context.Log($"{Title}: {ex.Message}", Severity.Warning);
        Context.SetStatus(ex.Message);
    }
}
