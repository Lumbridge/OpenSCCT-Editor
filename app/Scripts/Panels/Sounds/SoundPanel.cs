using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Audio;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Panels;

/// <summary>Sound library, preview transport and undoable editing of placed sources.</summary>
[Panel("Sounds", DockSide.Left, Order = 6)]
public partial class SoundPanel : EditorPanel
{
    public static SoundPanel? Instance { get; private set; }
    private LineEdit _search = null!;
    private SoundAssetTree _assets = null!;
    private Tree _sources = null!;
    private Label _info = null!;
    private Label _selection = null!;
    private Label _time = null!;
    private Label _state = null!;
    private SoundWaveform _waveform = null!;
    private HSlider _seek = null!;
    private SpinBox _volume = null!;
    private SpinBox _pitch = null!;
    private SpinBox _radius = null!;
    private Button _play = null!;
    private Button _apply = null!;
    private Button _place = null!;
    private Button _remove = null!;
    private Button _ambience = null!;
    private Button _solo = null!;
    private string? _sound;
    private string? _pendingImport;
    private bool _syncing;

    protected override void Build()
    {
        Instance = this;
        _search = PanelUi.Search(this, "Search sounds, packages or map sources", _ => FillAssets());
        _search.Name = "SoundSearch";
        HFlowContainer tools = PanelUi.Toolbar(this);
        PanelUi.Button(tools, "Import WAV / OGG…", "Copy a sound into this map's assets", Import);
        PanelUi.IconButton(tools, "refresh", "Reindex the installed sounds", () => AssetService.Instance.Reload());
        var split = new VSplitContainer { Name = "SoundSplit", SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        var tabs = new TabContainer { Name = "SoundLists", CustomMinimumSize = new Vector2(0, 92), SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.8f };
        split.AddChild(tabs);
        _assets = new SoundAssetTree { Name = "Library", HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        _assets.Settings = () => (Volume, Pitch, _radius.Value);
        _assets.ItemSelected += PickAsset;
        _assets.ItemActivated += Play;
        tabs.AddChild(_assets);
        _sources = new Tree { Name = "In This Map", HideRoot = true, Columns = 2, SizeFlagsVertical = SizeFlags.ExpandFill };
        _sources.SetColumnExpandRatio(0, 1);
        _sources.SetColumnExpandRatio(1, 2);
        _sources.ItemSelected += PickSource;
        _sources.ItemActivated += () => Context.Focus(Context.Selection.Items);
        tabs.AddChild(_sources);

        var detail = PanelUi.Scroll(split);
        PanelUi.ScrollOf(detail).CustomMinimumSize = new Vector2(0, 80);
        _info = PanelUi.Caption(detail, "Choose a sound, or select a sound source in the map.");
        _waveform = new SoundWaveform { Name = "SoundWaveform" };
        detail.AddChild(_waveform);
        _waveform.SeekRequested += fraction => Seek(fraction);
        _seek = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.001, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _seek.ValueChanged += value => { if (!_syncing) Seek(value); };
        detail.AddChild(_seek);
        HFlowContainer transport = PanelUi.Toolbar(detail);
        _play = PanelUi.Button(transport, "Play", "Play, pause or resume the selected sound", Play);
        PanelUi.Button(transport, "Stop", "Stop the current sound preview", () => EditorAudio.Instance?.Stop());
        PanelUi.Toggle(transport, "Loop Preview", "Repeat the preview; placed ambient sources loop in the map", false,
            on => { if (EditorAudio.Instance is { } audio) audio.Loop = on; });
        _time = new Label { Text = "0:00 / 0:00", ThemeTypeVariation = "Caption" };
        transport.AddChild(_time);

        _volume = PanelUi.Spin(PanelUi.Row(detail, "Volume (%)"), 0, 100, 0.1, 50, "Source volume; 100% is SoundVolume 255");
        _pitch = PanelUi.Spin(PanelUi.Row(detail, "Pitch (×)"), 1.0 / 64, 255.0 / 64, 0.01, 1, "Playback speed and pitch; 1× is the original sound");
        _radius = PanelUi.Spin(PanelUi.Row(detail, "Radius (m)"), 0, 255, 0.01, 64, "Maximum reach before zone attenuation; shown around selected sources in the viewport");
        _volume.ValueChanged += _ => UpdatePreviewSettings();
        _pitch.ValueChanged += _ => UpdatePreviewSettings();
        HFlowContainer ranges = PanelUi.Toolbar(detail);
        PanelUi.Caption(ranges, "Reach presets", wrap: false);
        foreach (var (name, radius) in new[] { ("Local", 8), ("Room", 24), ("Area", 64), ("Wide", 128) })
            PanelUi.Button(ranges, name, $"Set the sound radius to {radius} metres", () => _radius.Value = radius);
        _selection = PanelUi.Caption(this, "No actors selected.");
        HFlowContainer actions = PanelUi.Toolbar(this);
        _place = PanelUi.Primary(actions, "Place at Brush", "Add an ambient source at the builder brush as one undo step", Place);
        _apply = PanelUi.Button(actions, "Apply to Selected", "Set the sound and settings on the selected actors as one undo step", Apply);
        _remove = PanelUi.Button(actions, "Remove Sound", "Clear ambient sound from the selected actors without deleting them", Remove);
        HFlowContainer listen = PanelUi.Toolbar(this);
        _ambience = PanelUi.Toggle(listen, "Listen in Map", "Listen through the game sound engine from the perspective camera, including occlusion and room reverb", true,
            _ => Context.Run("audio.ambience"));
        _solo = PanelUi.Toggle(listen, "Solo Selected", "While listening in the map, play only the selected sources", false,
            _ => Context.Run("audio.solo"));
        PanelUi.Toggle(listen, "Sound Paths", "Show the selected source's reach and acoustic paths", true,
            on => { if (EditorAudio.Instance is { } audio) audio.ShowPaths = on; });
        PanelUi.Caption(this, "Sound paths: green = mixer arrival, blue = direct reach, amber = material-aware reflections (up to four bounces). Reflections are estimates, not captured Steam Audio rays.");
        _state = PanelUi.Caption(this, "");

        Context.AddAction(new EditorAction("sound.library", "Sound Library", "Assets", () => { DockTabs.ShowPanel("Sounds"); _search.GrabFocus(); })
        { Description = "Browse, preview, import and place sounds, or edit selected sound sources" });
        Context.AddAction(new EditorAction("sound.place", "Place Sound at Brush", "Assets", Place) { IsEnabled = () => _sound is not null });
        Context.AddAction(new EditorAction("sound.import", "Import Sound…", "Assets", Import));
        Context.SelectionChanged += LoadSelection;
        Context.ProjectChanged += _ => { FillSources(); LoadSelection(); };
        AssetService.Instance.CatalogChanged += CatalogChanged;
        EditorAudio.Changed += AudioChanged;
        TreeExiting += () => { AssetService.Instance.CatalogChanged -= CatalogChanged; EditorAudio.Changed -= AudioChanged; Instance = null; };
        SetProcess(true);
        Refresh();
    }

    private double Volume => Math.Round(_volume.Value * 255 / 100);
    private double Pitch => Math.Round(_pitch.Value * 64);
    private Actor[] SelectedActors() => Context.Selection.Items.Where(r => r.Kind == ObjectKind.Actor)
        .Select(r => Context.Project.FindActor(r.Id)).OfType<Actor>().ToArray();

    public override void Refresh() { if (_assets is null) return; EditorAudio.Instance?.Stop(); FillAssets(); FillSources(); LoadSelection(); }
    private void CatalogChanged()
    {
        FillAssets();
        if (_sound is { } current) _waveform.SetStream(EditorAudio.Instance?.PreviewStream(current));
        if (_pendingImport is { } path && AssetService.Instance.Catalog.Find(AssetKind.Sound, path) is { } entry)
        { _pendingImport = null; Choose(entry.Path); }
    }

    private void FillAssets()
    {
        _assets.Clear();
        TreeItem root = _assets.CreateItem();
        string[] words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entries = AssetService.Instance.Catalog.All.Where(e => e.Kind == AssetKind.Sound &&
            words.All(w => $"{e.Path} {e.Origin} {e.Gloss}".Contains(w, StringComparison.OrdinalIgnoreCase)));
        int count = 0;
        foreach (var group in entries.GroupBy(e => e.Package, StringComparer.OrdinalIgnoreCase))
        {
            TreeItem heading = _assets.CreateItem(root);
            heading.SetText(0, group.Key);
            heading.SetSelectable(0, false);
            heading.Collapsed = words.Length == 0;
            foreach (var entry in group)
            {
                TreeItem row = _assets.CreateItem(heading);
                row.SetText(0, entry.Group.Length > 0 ? entry.Group + "." + entry.Name : entry.Name);
                row.SetMetadata(0, entry.Path);
                row.SetTooltipText(0, $"{entry.Path}\n{entry.Origin} · {entry.Duration:0.0} s\nDrag into a viewport to place.");
                if (entry.Path.Equals(_sound, StringComparison.OrdinalIgnoreCase)) { heading.Collapsed = false; row.Select(0); }
                count++;
            }
        }
        _assets.TooltipText = AssetService.Instance.Loading ? "Indexing sounds…" : $"{count} sounds. Double-click to preview; drag into the map to place.";
    }

    private void FillSources()
    {
        _sources.Clear();
        TreeItem root = _sources.CreateItem();
        foreach (var actor in Context.Project.Actors.Where(a => SoundAuthoring.Sound(a) is not null))
        {
            TreeItem row = _sources.CreateItem(root);
            row.SetText(0, actor.Id);
            row.SetText(1, SoundAuthoring.Sound(actor)!);
            row.SetMetadata(0, actor.Id);
            row.SetTooltipText(0, $"{SoundAuthoring.Number(actor, "SoundRadius", 64):0.#} m · double-click to frame");
        }
    }

    private void PickAsset() { if (_assets.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } path) Choose(path); }
    private void PickSource()
    {
        if (_sources.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id)
            Context.Selection.Set([ObjectRef.Actor(id)]);
    }
    private void Choose(string path)
    {
        if (!string.Equals(_sound, path, StringComparison.OrdinalIgnoreCase))
        {
            EditorAudio.Instance?.Stop();
            _waveform.SetStream(EditorAudio.Instance?.PreviewStream(path));
        }
        _sound = path;
        var entry = AssetService.Instance.Catalog.Find(AssetKind.Sound, path);
        _info.Text = entry is null ? path + "\nSound content is not installed." : $"{entry.Name}\n{entry.Path} · {entry.Origin} · {entry.Duration:0.0} s" +
            (entry.File is null ? "\nComposite sound: preview plays its first available clip." : "");
        UpdateButtons();
    }

    public void ShowSound(string path) { DockTabs.ShowPanel("Sounds"); Choose(path); }

    private void LoadSelection()
    {
        Actor[] actors = SelectedActors();
        _selection.Text = actors.Length == 0 ? "No actors selected. Place a new source or select actors to attach sound." :
            actors.Length == 1 ? "Editing: " + actors[0].Id : $"{actors.Length} actors selected. Apply writes these settings to all.";
        Actor? source = actors.FirstOrDefault(a => SoundAuthoring.Sound(a) is not null);
        if (source is not null)
        {
            Choose(SoundAuthoring.Sound(source)!);
            _volume.SetValueNoSignal(SoundAuthoring.Number(source, "SoundVolume", 128) * 100 / 255);
            _pitch.SetValueNoSignal(SoundAuthoring.Number(source, "SoundPitch", 64) / 64);
            _radius.SetValueNoSignal(SoundAuthoring.Number(source, "SoundRadius", 64));
            if (actors.Skip(1).Any(a => SoundAuthoring.Sound(a) != SoundAuthoring.Sound(source) ||
                new[] { "SoundVolume", "SoundPitch", "SoundRadius" }.Any(p => SoundAuthoring.Number(a, p, p == "SoundVolume" ? 128 : 64) != SoundAuthoring.Number(source, p, p == "SoundVolume" ? 128 : 64))))
                _selection.Text += " Mixed values; controls show the first sound source.";
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _play.Disabled = _place.Disabled = _sound is null;
        _apply.Disabled = _sound is null || SelectedActors().Length == 0;
        _remove.Disabled = !SelectedActors().Any(a => SoundAuthoring.Sound(a) is not null);
    }
    private void UpdatePreviewSettings() => EditorAudio.Instance?.SetPreviewSettings(Volume, Pitch);
    private void Play()
    {
        if (_sound is null || EditorAudio.Instance is not { } audio) return;
        if ((audio.Playing || audio.Paused) && string.Equals(audio.CurrentPath, _sound, StringComparison.OrdinalIgnoreCase)) audio.Pause();
        else if (audio.Play(_sound, Volume, Pitch)) _waveform.SetStream(audio.Stream);
        AudioChanged();
    }
    private void Seek(double fraction)
    {
        if (_sound is null || EditorAudio.Instance is not { } audio || _waveform.Length <= 0) return;
        double position = Math.Clamp(fraction, 0, 1) * _waveform.Length;
        if (!string.Equals(audio.CurrentPath, _sound, StringComparison.OrdinalIgnoreCase))
            audio.Play(_sound, Volume, Pitch, position, paused: true);
        else audio.Seek(position);
    }
    private void AudioChanged()
    {
        if (_play is null) return;
        var audio = EditorAudio.Instance;
        bool current = audio is not null && string.Equals(audio.CurrentPath, _sound, StringComparison.OrdinalIgnoreCase);
        _play.Text = current && audio!.Paused ? "Resume" : current && audio!.Playing ? "Pause" : "Play";
        _ambience.SetPressedNoSignal(audio?.Ambience == true);
        _solo.SetPressedNoSignal(audio?.SoloSelection == true);
        _state.Text = EditorAudio.Muted ? "Audio muted — use the speaker button in the main toolbar to unmute." :
            audio?.Ambience == true ? "Listening from the perspective camera. Move through the map to audition reach." : "";
    }
    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || EditorAudio.Instance is not { } audio) return;
        bool current = string.Equals(audio.CurrentPath, _sound, StringComparison.OrdinalIgnoreCase);
        double length = _waveform.Length;
        double position = current ? audio.Position : 0;
        double fraction = length > 0 ? position / length : 0;
        _syncing = true;
        _seek.SetValueNoSignal(fraction);
        _syncing = false;
        _waveform.Fraction = fraction;
        _waveform.QueueRedraw();
        static string Time(double seconds) => seconds < 10 ? $"0:{seconds:00.0}" : $"{(int)seconds / 60}:{(int)seconds % 60:00}";
        _time.Text = $"{Time(position)} / {Time(length)}";
    }

    private void Place()
    {
        if (_sound is null) return;
        Vec3 at = ToolManager.Instance?.Builder.Location ?? Context.PlacementPoint?.Invoke() ?? Context.Project.Workspace.CameraLocation;
        Actor actor = SoundAuthoring.Create(Context.Project, _sound, at, Volume, Pitch, _radius.Value);
        Context.Execute(new AddObjectsCommand("Place sound source", actors: [actor]));
        Context.Selection.Set([ObjectRef.Actor(actor.Id)]);
        Context.SetStatus($"Placed {actor.Id}; select it to see its sound radius.");
    }
    private void Apply()
    {
        if (_sound is null) return;
        var command = SoundAuthoring.Edit(SelectedActors(), _sound, Volume, Pitch, _radius.Value);
        if (!command.IsEmpty) Context.Execute(command);
    }
    private void Remove()
    {
        var command = new ReplaceObjectsCommand("Remove ambient sound");
        foreach (Actor actor in SelectedActors().Where(a => SoundAuthoring.Sound(a) is not null))
        {
            Actor copy = actor.Clone();
            copy.Props.Set("AmbientSound", SvJsonNode.Null);
            command.Actor(actor, copy);
        }
        if (!command.IsEmpty) Context.Execute(command);
    }
    private void Import()
    {
        if (Context.Session.Folder is not { } folder) { Context.SetStatus("Save the map before importing sounds."); return; }
        Dialogs.OpenFile("Import Sound", ["*.wav ; WAV audio", "*.ogg ; Ogg Vorbis audio"], file =>
        {
            try
            {
                if (EditorAudio.LoadFile(file) is not { } stream) { Context.SetStatus("Cannot decode this sound."); return; }
                stream.Dispose();
                _pendingImport = SoundImporter.Import(folder, file);
                AssetService.Instance.ProjectAssetsChanged();
                Context.SetStatus($"Imported {_pendingImport}. Place it or apply it to selected actors.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            { Context.SetStatus(ex.Message); }
        });
    }
}

public partial class SoundAssetTree : Tree
{
    public Func<(double Volume, double Pitch, double Radius)>? Settings { get; set; }
    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (GetItemAtPosition(atPosition)?.GetMetadata(0).AsString() is not { Length: > 0 } path) return default;
        var settings = Settings?.Invoke() ?? (128, 64, 64);
        return AssetDragData.StartSound(this, new AssetDrag(AssetKind.Sound, path, AssetPaths.Split(path).Name), settings.Item1, settings.Item2, settings.Item3);
    }
}
