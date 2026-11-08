using Godot;
using OpenScctEditor.Panels;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using Clip = OpenScct.Animation.AnimationClip;

namespace OpenScctEditor.Animation;

/// <summary>Animation documents have their own workspace, history and transport, independent of map editing.</summary>
public partial class AnimationStudio : VBoxContainer
{
    public StudioState State { get; } = new();
    public bool HasUnsavedChanges => State.Session.Dirty;
    private AnimationStage _stage = null!;
    private AnimationTimeline _timeline = null!;
    private TabContainer _tabs = null!;
    private AnimationTimeline ActiveTimeline => _tabs.GetCurrentTabControl() as AnimationTimeline ?? _timeline;
    public bool HasSelectedKeys => ActiveTimeline.HasSelectedKeys;
    public void CopyKeys() => ActiveTimeline.Copy();
    public void PasteKeys() => Run(ActiveTimeline.Paste);
    public void DeleteKeys() => Run(ActiveTimeline.Delete);
    public void SelectKeys(bool all) => ActiveTimeline.SelectKeys(all);
    public void DuplicateSelectedClip() => DuplicateClip();
    private AnimationGraphView _graph = null!;
    private AnimationSequenceView _sequence = null!;
    private FirstPersonStudio _firstPerson = null!;
    private Tree _clips = null!, _rig = null!, _stock = null!;
    private LineEdit _search = null!;
    private Label _title = null!, _timeLabel = null!, _status = null!;
    private HSlider _seek = null!;
    private Button _play = null!;
    private readonly Dictionary<string, Button> _poseTools = [];
    private VBoxContainer _inspector = null!;
    private bool _refreshing;
    private double _autosave;
    private float _exportRate = 60;
    private IReadOnlyList<AnimationLibraryEntry> _stockEntries = [];
    public override void _Ready()
    {
        FocusMode = FocusModeEnum.All;
        HFlowContainer files = PanelUi.Toolbar(this);
        PanelUi.Button(files, "New", "New standalone animation project", () => GuardUnsaved(() => State.Open(new())));
        PanelUi.Button(files, "Open…", "Open an .osanim project", () => GuardUnsaved(() => Dialogs.OpenFolder("Open animation project", path => Run(() => State.Open(AnimationSession.Open(path))))));
        PanelUi.Button(files, "Save", "Save animation project (Ctrl+S)", () => Save());
        PanelUi.Button(files, "Save As…", "Save a portable copy", () => SaveAs());
        PanelUi.Button(files, "Recover…", "Open an animation recovery document", () => GuardUnsaved(() => Dialogs.OpenFile("Recover animation project", ["*.json;Recovery document"], path => Run(() => State.Open(AnimationSession.Recover(path))), ProjectSettings.GlobalizePath("user://animation-recovery"))));
        PanelUi.Button(files, "Import…", "Import GLB, glTF, FBX, BVH, PSK or PSA", Import);
        PanelUi.Button(files, "Export…", "Export GLB/glTF or a compiled animation pack", Export);
        PanelUi.Button(files, "Add to Map", "Copy this animation project into the open map", AttachMap);
        PanelUi.Button(files, "Place prop", "Place the selected rig and animation in the open map", PlaceMapProp);
        PanelUi.Button(files, "Scene items", "Select, duplicate, add or remove scene rigs", () => ShowInspector("Scene"));
        PanelUi.Primary(files, "Test in OpenSCCT", "Build a private content overlay and launch a local animation test", TestGame);
        PanelUi.Button(files, "Play replacements", "Play TestRange with this project's explicitly bound replacements", () => TestGame(true));
        PanelUi.Button(files, "Tutorials", "Guided animation lessons", () => CommandPalette.Instance?.OpenSearch("tutorials: animation"));
        _title = PanelUi.Caption(files, "", false);

        var horizontal = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(horizontal);
        var library = new TabContainer { CustomMinimumSize = new(210, 0), ClipTabs = true };
        horizontal.AddChild(library);
        var local = new VBoxContainer { Name = "Project" }; library.AddChild(local);
        HFlowContainer actions = PanelUi.Toolbar(local);
        PanelUi.Button(actions, "+ Clip", "Create an animation on the selected rig", NewClip);
        PanelUi.IconButton(actions, "copy", "Duplicate the selected clip", DuplicateClip).Name = "AnimationDuplicateClip";
        _clips = new Tree { Name = "AnimationClips", HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        local.AddChild(_clips);
        _clips.ItemSelected += () =>
        {
            if (_refreshing || _clips.GetSelected()?.GetMetadata(0).AsString() is not { Length: > 0 } id) return;
            // Selection refreshes this tree. Wait until Godot finishes dispatching its selection signal.
            AnimationSession session = State.Session;
            Callable.From(() =>
            {
                if (State.Session == session && State.Project.Clips.Any(c => c.Id == id)) State.SelectClip(id);
            }).CallDeferred();
        };
        var stock = new VBoxContainer { Name = "Stock" }; library.AddChild(stock);
        _search = PanelUi.Search(stock, "Search animation assets", _ => FillStock());
        PanelUi.Caption(stock, "Double-click a model to import an editable copy.");
        _stock = new Tree { Name = "StockAnimationLibrary", HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill }; stock.AddChild(_stock);
        _stock.ItemActivated += () => { if (_stock.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } path) ImportPath(path); };
        PanelUi.Button(stock, "Refresh library", "Read installed animation assets", ReloadLibrary);
        PanelUi.Button(stock, "Import Spy rig", "Add an editable stock Spy", () => ImportStock(true)).SetMeta("editor_action", "animation.spy");
        PanelUi.Button(stock, "Import Merc rig", "Add an editable stock Merc", () => ImportStock(false)).SetMeta("editor_action", "animation.merc");
        PanelUi.Button(stock, "Create example rig", "Start with an original three-bone sample, without installed game content", CreateExample).SetMeta("editor_action", "animation.example");
        library.Name = "AnimationLibraryTabs";

        var right = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; horizontal.AddChild(right);
        var middle = new VSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; right.AddChild(middle);
        middle.SplitOffsets = [AppSettings.GetInt("animation.layout", "middle", 0)];
        var view = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2.5f }; middle.AddChild(view);
        HFlowContainer viewTools = PanelUi.Toolbar(view);
        PanelUi.Toggle(viewTools, "Bones", "Show skeleton overlay", true, value => { State.ShowBones = value; State.Notify(); });
        PanelUi.Toggle(viewTools, "Ghosts", "Show poses before and after the current frame", false, value => { State.Ghosts = value; State.Notify(); });
        PanelUi.Toggle(viewTools, "Trails", "Show the selected bone's motion path", false, value => { State.Trails = value; State.Notify(); });
        PanelUi.Button(viewTools, "Frame", "Frame the rig (F)", () => _stage.Frame());
        PanelUi.Button(viewTools, "First person", "Preview at eye height", () => _stage.FirstPerson());
        PanelUi.Toggle(viewTools, "Sequence camera", "Preview explicitly bound camera tracks", false, value => { _stage.SequenceCamera = value; State.Notify(); });
        foreach (string tool in new[] { "move", "rotate", "scale" })
        {
            string captured = tool;
            Button button = PanelUi.Button(viewTools, PanelUi.DisplayName(tool), "Select a bone, then drag a coloured handle · Shift snaps", () => State.SelectTool(captured));
            button.Name = "AnimationTool_" + tool; button.ToggleMode = true; _poseTools[tool] = button;
        }
        PanelUi.Toggle(viewTools, "World", "Inspect and edit world-space bone transforms", false, value => { State.WorldSpace = value; State.Notify(); });
        _stage = new AnimationStage { Name = "AnimationStage", SizeFlagsVertical = SizeFlags.ExpandFill };
        _stage.Bind(State); view.AddChild(_stage);
        HFlowContainer transport = PanelUi.Toolbar(view);
        PanelUi.Button(transport, "|◀", "Go to loop start", () => State.Seek(State.RangeStart));
        PanelUi.Button(transport, "◀", "Previous frame", () => State.Seek(State.Time - 1 / State.Project.FramesPerSecond));
        _play = PanelUi.Button(transport, "Play", "Play / pause (Space)", () => State.Playing = !State.Playing);
        PanelUi.Button(transport, "▶", "Next frame", () => State.Seek(State.Time + 1 / State.Project.FramesPerSecond));
        PanelUi.Toggle(transport, "Loop", "Loop the selected range", true, value => State.Loop = value);
        PanelUi.Caption(transport, "Speed", false);
        PanelUi.Spin(transport, -4, 4, .1, 1, "Playback speed", 65).ValueChanged += v => State.Speed = (float)v;
        PanelUi.Toggle(transport, "Auto key", "Commit pose edits as keys", true, value => State.AutoKey = value);
        PanelUi.Button(transport, "Key pose", "Key the whole pose at the playhead", () => State.KeyPose());
        _timeLabel = PanelUi.Caption(transport, "0.000 s", false);
        _seek = new HSlider { Name = "AnimationSeek", MinValue = 0, MaxValue = 1, Step = .0001, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(90, 0) };
        transport.AddChild(_seek); _seek.ValueChanged += v => { if (!_refreshing) State.Seek((float)v); };
        _seek.DragStarted += () => State.Playing = false;

        var tabs = new TabContainer { CustomMinimumSize = new(0, 190), SizeFlagsVertical = SizeFlags.ExpandFill }; middle.AddChild(tabs);
        _tabs = tabs;
        _timeline = new AnimationTimeline(State) { Name = "Dope sheet", SizeFlagsVertical = SizeFlags.ExpandFill }; tabs.AddChild(_timeline);
        var curves = new AnimationTimeline(State, curves: true) { Name = "Curves", SizeFlagsVertical = SizeFlags.ExpandFill }; tabs.AddChild(curves);
        _graph = new AnimationGraphView(State) { Name = "Graph" }; tabs.AddChild(_graph);
        _sequence = new AnimationSequenceView(State) { Name = "Sequence" }; tabs.AddChild(_sequence);

        var details = new VBoxContainer { CustomMinimumSize = new(240, 0) }; right.AddChild(details);
        _rig = new Tree { Name = "RigHierarchy", HideRoot = true, CustomMinimumSize = new(0, 155), SizeFlagsVertical = SizeFlags.ExpandFill }; details.AddChild(_rig);
        _rig.ItemSelected += () =>
        {
            if (_refreshing || _rig.GetSelected() is not { } selected) return;
            // Rebuilding this tree in its own selection signal can invalidate the active item.
            int bone = selected.GetMetadata(0).AsInt32();
            Callable.From(() => State.SelectBone(bone)).CallDeferred();
        };
        _inspector = PanelUi.Scroll(details); PanelUi.ScrollOf(_inspector).SizeFlagsVertical = SizeFlags.ExpandFill;
        _status = PanelUi.Caption(this, "Import a rig from the library or start an example.");
        _firstPerson = new FirstPersonStudio(State, _stage, Refresh) { Name = "FirstPersonStudio" }; AddChild(_firstPerson); MoveChild(_firstPerson, 1);
        horizontal.SplitOffsets = [AppSettings.GetInt("animation.layout", "left", 0)];
        right.SplitOffsets = [AppSettings.GetInt("animation.layout", "right", 0)];
        horizontal.DragEnded += () => AppSettings.Set("animation.layout", "left", horizontal.SplitOffsets.FirstOrDefault());
        right.DragEnded += () => AppSettings.Set("animation.layout", "right", right.SplitOffsets.FirstOrDefault());
        middle.DragEnded += () => AppSettings.Set("animation.layout", "middle", middle.SplitOffsets.FirstOrDefault());
        State.Changed += Refresh; State.TimeChanged += RefreshTime;
        TreeExiting += () => { State.Changed -= Refresh; State.TimeChanged -= RefreshTime; };
        AnimationImport.ConvertFbx = path => AnimationFbxProcess.Convert(OS.GetExecutablePath(), OS.HasFeature("template") ? null : ProjectSettings.GlobalizePath("res://"), path);
        ReloadLibrary(); Refresh();
        string? startup = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--animation=", StringComparison.Ordinal));
        if (startup is not null) Run(() => State.Open(AnimationSession.Open(startup[12..])));
        if (OS.GetCmdlineUserArgs().Contains("--animation-demo")) CreateExample();
        if (OS.GetCmdlineUserArgs().Contains("--animation-ui-checks")) Callable.From(() => { _ = RunInteractionChecks(); }).CallDeferred();
    }
    public override void _Process(double delta)
    {
        State.Advance((float)delta); _play.Text = State.Playing ? "Pause" : "Play";
        _autosave += delta;
        if (_autosave >= 60) { _autosave = 0; if (HasUnsavedChanges) Run(() => State.Session.Autosave(ProjectSettings.GlobalizePath("user://animation-recovery"))); }
    }
    private void Refresh()
    {
        if (!IsNodeReady()) return;
        _refreshing = true;
        try
        {
            _title.Text = State.Project.Name + (HasUnsavedChanges ? " *" : "");
            foreach ((string tool, Button button) in _poseTools) button.SetPressedNoSignal(State.TransformTool == tool);
            _clips.Clear(); TreeItem root = _clips.CreateItem();
            foreach (Clip clip in State.Project.Clips.Where(c => _firstPerson?.EditingSkeleton is not { } skeleton || c.Skeleton == skeleton))
            {
                TreeItem item = _clips.CreateItem(root); item.SetText(0, clip.Name); item.SetMetadata(0, clip.Id);
                item.SetTooltipText(0, $"{clip.Duration:0.###} s · {clip.Tracks.Count} tracks · {clip.FramesPerSecond:0.##} fps");
                if (clip.Id == State.ClipId) item.Select(0);
            }
            _rig.Clear(); TreeItem rigRoot = _rig.CreateItem();
            if (State.Skeleton is { } skeleton)
            {
                var items = new List<TreeItem>();
                for (int i = 0; i < skeleton.Bones.Count; i++)
                {
                    TreeItem item = _rig.CreateItem(skeleton.Bones[i].Parent < 0 ? rigRoot : items[skeleton.Bones[i].Parent]);
                    item.SetText(0, skeleton.Bones[i].Name); item.SetMetadata(0, i); items.Add(item);
                    item.SetTextOverrunBehavior(0, TextServer.OverrunBehavior.TrimEllipsis); item.SetTooltipText(0, skeleton.Bones[i].Name);
                    if (i == State.Bone) item.Select(0);
                }
            }
            foreach (Node child in _inspector.GetChildren()) { _inspector.RemoveChild(child); child.QueueFree(); }
            BuildInspector();
        }
        finally { _refreshing = false; }
        RefreshTime();
    }
    private void RefreshTime()
    {
        if (!IsNodeReady()) return;
        _refreshing = true;
        // Keep range updates separate from the playhead while the user drags the slider.
        double duration = MathF.Max(.001f, State.Duration);
        if (!Mathf.IsEqualApprox(_seek.MaxValue, duration)) _seek.MaxValue = duration;
        _seek.SetValueNoSignal(State.Time);
        _timeLabel.Text = $"{State.Time:0.000} s · {State.Time * State.Project.FramesPerSecond:0.#} f";
        _refreshing = false;
    }
    private void ReloadLibrary() { Run(() => { _stockEntries = OpenScct.Editor.Core.Animation.AnimationLibrary.Index(EditorServices.ContentRoot).ToArray(); FillStock(); }); }
    public void ImportStock(bool spy)
    {
        string name = spy ? "ATT_01" : "DEF_01";
        string? path = _stockEntries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && e.Category.Equals("SPerso", StringComparison.OrdinalIgnoreCase))?.Path;
        if (path is null) { _status.Text = "Install OpenSCCT character content to import this rig."; return; }
        ImportPath(path);
        string idle = spy ? "WaitStAlFd0" : "WaitStSpFd2";
        if (State.Project.Clips.FirstOrDefault(c => c.Name.Equals(idle, StringComparison.OrdinalIgnoreCase) && c.Skeleton == State.Skeleton?.Id) is { } clip) State.SelectClip(clip.Id);
        _stage.Frame();
    }
    public void OpenFirstPerson(FirstPersonRole role) => _firstPerson.ShowRole(role);
    private void FillStock()
    {
        _stock.Clear(); TreeItem root = _stock.CreateItem(); var groups = new Dictionary<string, TreeItem>();
        foreach (AnimationLibraryEntry entry in _stockEntries.Where(e => (e.Category + "." + e.Name).Contains(_search.Text, StringComparison.OrdinalIgnoreCase)))
        {
            if (!groups.TryGetValue(entry.Category, out TreeItem? group)) { group = _stock.CreateItem(root); group.SetText(0, entry.Category); group.SetSelectable(0, false); groups.Add(entry.Category, group); }
            TreeItem item = _stock.CreateItem(group); item.SetText(0, entry.Name); item.SetMetadata(0, entry.Path); item.SetTooltipText(0, entry.Path);
        }
    }
    private void NewClip() { if (State.Skeleton is { } skeleton) Run(() => State.SelectClip(State.Session.NewClip(skeleton.Id))); }
    private void DuplicateClip() { if (State.CurrentClip is { } clip) Run(() => { Clip copy = AnimationEdits.Duplicate(clip); State.Session.Edit("Duplicate clip", p => p.Clips.Add(copy)); State.SelectClip(copy.Id); }); }
    public void ShowInspector(string page) { _page = page; Refresh(); }
    public void ShowLibrary(bool stock) { if (FindChild("AnimationLibraryTabs", true, false) is TabContainer library) library.CurrentTab = stock ? 1 : 0; }
    public void RevealTutorial(string route)
    {
        string[] parts = route.Split('|', 2);
        if (parts[0] is "Scene" or "Pose" or "Bindings" or "Clip") ShowInspector(parts[0]);
        if (parts[0] == "Stock") ShowLibrary(true);
        if (parts[0] == "Project") ShowLibrary(false);
        string target = parts.Length > 1 ? parts[1] : parts[0];
        if (target is "Load setup" or "New action clip" or "Eye view" or "Bind actions") _firstPerson.ShowControls();
        Control? control = DiscoveryNavigation.Descendants(this).OfType<Control>().FirstOrDefault(c => c.Name == target && c.IsVisibleInTree())
            ?? DiscoveryNavigation.FindControl(this, target);
        if (control is not null) DiscoveryNavigation.Reveal(control, lesson: true);
    }
    private void Import() => Dialogs.OpenFile("Import animation", ["*.glb,*.gltf,*.fbx,*.bvh,*.psk,*.pskx,*.psa;Animation assets"], ImportPath);
    public void ImportPath(string path) => Run(() =>
    {
        int clips = State.Project.Clips.Count, rigs = State.Project.Scene.Count;
        State.Session.Import(path, State.Skeleton?.Id);
        if (State.Project.Clips.Count > clips) State.SelectClip(State.Project.Clips[clips].Id);
        else if (State.Project.Scene.Count > rigs) { State.ClipId = null; State.RigId = State.Project.Scene[^1].Id; }
        State.RangeEnd = State.Duration; State.Notify(); _status.Text = "Imported " + Path.GetFileName(path);
    });
    public void GuardUnsaved(Action done)
    {
        if (!HasUnsavedChanges) { done(); return; }
        Dialogs.AskSave(State.Project.Name, () => Save(done), () => { DiscardMarker(); done(); });
    }
    private AnimationSession DiscardMarker()
    {
        // Replacing the document clears its dirty flag before a repeated close request.
        var session = new AnimationSession(AnimationJson.Clone(State.Project)); State.Open(session); return session;
    }
    private void Save(Action? done = null)
    {
        if (State.Session.Folder is null) { SaveAs(done); return; }
        Run(() => { State.Session.Save(); _status.Text = "Saved " + State.Session.Folder; done?.Invoke(); });
    }
    public void SaveDocument(bool saveAs = false) { if (saveAs) SaveAs(); else Save(); }
    public void OpenDocument() => GuardUnsaved(() => Dialogs.OpenFolder("Open animation project", path => Run(() => State.Open(AnimationSession.Open(path)))));
    public void NewDocument() => GuardUnsaved(() => State.Open(new()));
    private void SaveAs(Action? done = null) => Dialogs.SaveFile("Save animation project folder", ["*.osanim;Animation project"], State.Project.Name + ".osanim", path => Run(() =>
    { State.Session.Save(path); _status.Text = "Saved " + path; done?.Invoke(); }));
    private void Export()
    {
        SkeletonDefinition? skeleton = State.Skeleton;
        Dialogs.SaveFile("Export animation", ["*.osanimpack;OpenSCCT animation pack", "*.glb;Binary glTF", "*.gltf;glTF"], State.Project.Name + (skeleton is null ? ".osanimpack" : ".glb"), path => Run(() =>
        {
            if (Path.GetExtension(path).Equals(".osanimpack", StringComparison.OrdinalIgnoreCase)) AnimationPackBuild.Export(State.Project, State.Session.Folder, path, _exportRate);
            else AnimationExport.Write(State.Project, skeleton?.Id ?? throw new InvalidOperationException("glTF export needs a skeleton. Choose an animation pack for sequence-only projects."), path, State.Session.Folder, _exportRate);
            _status.Text = "Exported " + path;
        }));
    }
    public void CreateExample()
    {
        GuardUnsaved(() =>
        {
            var skeleton = new SkeletonDefinition { Name = "Example arm", Bones = [
                new() { Name = "Root", Rest = new(new(0, .5f, 0), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One) },
                new() { Name = "Upper", Parent = 0, Rest = new(new(0, .5f, 0), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One) },
                new() { Name = "Hand", Parent = 1, Rest = new(new(.5f, 0, 0), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One) }] };
            var clip = new Clip { Name = "Wave", Skeleton = skeleton.Id, Duration = 2, Loop = true };
            AnimationEdits.AddPoseKeys(clip, "Upper", skeleton.Bones[1].Rest, 0);
            AnimationEdits.AddPoseKeys(clip, "Upper", skeleton.Bones[1].Rest with { Rotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, .8f) }, 1);
            AnimationEdits.AddPoseKeys(clip, "Upper", skeleton.Bones[1].Rest, 2);
            State.Open(new(new AnimationProject { Name = "Animation example", Skeletons = [skeleton], Clips = [clip], Scene = [new() { Name = "Example", Skeleton = skeleton.Id, Clip = clip.Id }] }));
        });
    }
    public void HandleShortcut(InputEventKey key)
    {
        if (key.IsCommandOrControlPressed())
        {
            switch (key.Keycode)
            {
                case Key.S: if (key.ShiftPressed) SaveAs(); else Save(); break;
                case Key.O: OpenDocument(); break;
                case Key.N: NewDocument(); break;
                case Key.Z: if (key.ShiftPressed) State.Session.Redo(); else State.Session.Undo(); break;
                case Key.Y: State.Session.Redo(); break;
                case Key.C: CopyKeys(); break;
                case Key.X: CopyKeys(); DeleteKeys(); break;
                case Key.V: PasteKeys(); break;
                case Key.A: SelectKeys(true); break;
                case Key.D: DuplicateClip(); break;
            }
            return;
        }
        switch (key.Keycode)
        {
            case Key.Space: State.Playing = !State.Playing; break;
            case Key.Left: State.Seek(State.Time - 1 / State.Project.FramesPerSecond); break;
            case Key.Right: State.Seek(State.Time + 1 / State.Project.FramesPerSecond); break;
            case Key.Home: State.Seek(State.RangeStart); break;
            case Key.F: _stage.Frame(); break;
            case Key.I: State.KeyPose([State.Bone]); break;
            case Key.Delete: DeleteKeys(); break;
            case Key.Escape: SelectKeys(false); break;
            case Key.G: State.TransformTool = "move"; break;
            case Key.R: State.TransformTool = "rotate"; break;
            case Key.S: State.TransformTool = "scale"; break;
        }
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        { _status.Text = e.Message; EditorContext.Instance.Log("Animation Studio: " + e.Message, Severity.Error); }
    }
}
