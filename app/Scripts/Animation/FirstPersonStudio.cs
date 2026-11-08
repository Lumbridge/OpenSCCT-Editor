using Godot;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Animation;

/// <summary>Eye-view authoring, separate from third-person clips, with paired Spy arms and weapon tracks.</summary>
public partial class FirstPersonStudio : HFlowContainer
{
    private readonly StudioState _state;
    private readonly AnimationStage _stage;
    private readonly Action _refreshLibrary;
    private readonly Action _showProject;
    private Button _newAction = null!, _bindActions = null!;
    private OptionButton _role = null!, _weapon = null!, _action = null!, _part = null!;
    private OptionButton _mode = null!;
    private LineEdit _slot = null!;
    private Label _status = null!;
    private SpinBox _fov = null!;
    private string? _profileId;
    private bool _updating;
    private string? _projectId;
    public bool Active { get; private set; }
    public FirstPersonProfile? Profile => Active ? _state.Project.FirstPersonProfiles.FirstOrDefault(p => p.Id == _profileId) : null;
    public string? EditingSkeleton => Profile is not { } profile ? null : _state.Project.Scene.FirstOrDefault(r => r.Id == (_part.Selected == 1 ? profile.ArmsRig ?? profile.MainRig : profile.MainRig))?.Skeleton;
    public FirstPersonStudio(StudioState state, AnimationStage stage, Action refreshLibrary, Action showProject) { _state = state; _stage = stage; _refreshLibrary = refreshLibrary; _showProject = showProject; }
    public override void _Ready()
    {
        _projectId = _state.Project.Id;
        _mode = new OptionButton { Name = "AnimationStudioSection" }; _mode.AddItem("Animation"); _mode.AddItem("First Person"); AddChild(_mode);
        _mode.ItemSelected += index => { Active = index == 1; Activate(); };
        _role = Choice("Role", ["Spy", "Merc"]); _role.Name = "FirstPersonRole";
        _role.ItemSelected += _ => { Weapons(); SelectExisting(); };
        _weapon = Choice("Weapon", ["P190 / Shocker"]); _weapon.Name = "FirstPersonWeapon";
        _weapon.ItemSelected += _ => SelectExisting();
        PanelUi.Button(this, "Load setup", "Load the role's real first-person rigs", LoadSetup).Name = "LoadFirstPersonSetup";
        _action = Choice("Action", ["Idle", "Move", "Draw", "Holster", "Fire", "Reload", "Aim", "Gadget"]);
        _action.Name = "FirstPersonAction";
        _action.ItemSelected += _ => SelectAction();
        _part = Choice("Edit", ["Weapon", "Arms"]); _part.ItemSelected += _ => SelectAction();
        _newAction = PanelUi.Button(this, "New action clip", "Create an editable copy of the selected motion and reveal it in Project", NewAction);
        _newAction.Name = "NewFirstPersonActionClip";
        PanelUi.Button(this, "Eye view", "Use the game's X-forward eye-view framing", () => { _stage.FirstPersonEye = true; _stage.FirstPerson(); });
        PanelUi.Button(this, "Orbit", "Inspect the first-person rig from outside", () => { _stage.FirstPersonEye = false; _stage.Frame(); });
        var framing = new HBoxContainer(); AddChild(framing); framing.AddChild(new Label { Text = "FOV" });
        _fov = PanelUi.Spin(framing, 40, 120, 1, 70, "Horizontal field of view at the game's reference aspect ratio", 65);
        _fov.ValueChanged += value => { if (!_updating && Profile is { } profile) Edit("First-person field of view", p => p.FirstPersonProfiles.Single(f => f.Id == profile.Id).FieldOfView = (float)value); };
        _slot = new LineEdit { PlaceholderText = "Runtime animation slot", CustomMinimumSize = new(150, 0) }; AddChild(_slot);
        _slot.TextSubmitted += value => { if (Profile is { } profile) Edit("First-person action slot", p => p.FirstPersonProfiles.Single(f => f.Id == profile.Id).Actions[_action.Selected].Slot = value); };
        _bindActions = PanelUi.Button(this, "Bind actions", "Enable these first-person actions as explicit visual replacements", BindActions);
        _status = PanelUi.Caption(this, "Load a setup to edit Spy or Merc eye-view animations.");
        _status.CustomMinimumSize = new(180, 0);
        _state.Changed += Refresh; TreeExiting += () => _state.Changed -= Refresh;
        Refresh();
    }
    public void ShowRole(FirstPersonRole role)
    {
        Active = true; _mode.Selected = 1; _role.Selected = (int)role; Weapons(); LoadSetup();
    }
    public void ShowControls()
    {
        if (Active) return;
        Active = true; _mode.Selected = 1; SelectExisting();
    }
    private void Weapons()
    {
        _weapon.Clear(); foreach (string name in _role.Selected == 0 ? new[] { "P190 / Shocker" } : new[] { "Rifle", "Uzi", "Shotgun", "Desert Eagle" }) _weapon.AddItem(name);
    }
    private string MainAsset => _role.Selected == 0 ? "SWeaponFirst.p190_fp" : _weapon.Selected switch { 1 => "SWeaponFirst.1DEF_Uzi", 2 => "SWeaponFirst.1DEF_shotgun", 3 => "OpenScctSidearms.desert_eagle_first", _ => "SWeaponFirst.1DEF_01" };
    private void SelectExisting()
    {
        _profileId = _state.Project.FirstPersonProfiles.FirstOrDefault(p => p.Role == (FirstPersonRole)_role.Selected && p.MainAsset == MainAsset)?.Id;
        Activate();
    }
    private void Activate()
    {
        _stage.FirstPersonProfileId = Profile?.Id; _stage.FirstPersonEye = Active;
        if (Active) _stage.FirstPerson(); else _stage.Frame();
        _refreshLibrary(); _state.Notify();
    }
    private void LoadSetup()
    {
        try
        {
            Active = true;
            FirstPersonProfile? existing = _state.Project.FirstPersonProfiles.FirstOrDefault(p => p.Role == (FirstPersonRole)_role.Selected && p.MainAsset == MainAsset);
            if (existing is not null) { _profileId = existing.Id; Activate(); SelectAction(); return; }
            _stage.FirstPersonProfileId = null; _state.Notify();
            RigInstance Import(string asset)
            {
                string[] parts = asset.Split('.');
                SkeletonDefinition? skeleton = _state.Project.Skeletons.FirstOrDefault(s => s.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
                if (skeleton is null)
                {
                    AnimationLibraryEntry entry = OpenScct.Editor.Core.Animation.AnimationLibrary.Index(EditorServices.ContentRoot).FirstOrDefault(e => e.Category.Equals(parts[0], StringComparison.OrdinalIgnoreCase) && e.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("Install the OpenSCCT character content containing " + asset + ".");
                    _state.Session.Import(entry.Path); skeleton = _state.Project.Skeletons[^1];
                }
                return _state.Project.Scene.First(s => s.Skeleton == skeleton.Id);
            }
            RigInstance? arms = _role.Selected == 0 ? Import("SPerso.ATT_01_fp") : null;
            RigInstance main = Import(MainAsset);
            BoneTransform mount = BoneTransform.Identity; OpenScct.Animation.AnimationClip? grip = null;
            if (arms is not null)
            {
                var result = _stage.CreateSpyGrip(arms.Id);
                mount = result.Mount;
                grip = new() { Name = "Spy FP hand grip", Skeleton = arms.Skeleton, Duration = 1, Loop = true };
                SkeletonDefinition skeleton = _state.Project.Skeletons.Single(s => s.Id == arms.Skeleton);
                for (int i = 0; i < result.Pose.Length; i++) AnimationEdits.AddPoseKeys(grip, skeleton.Bones[i].Name, result.Pose[i], 0);
            }
            var profile = new FirstPersonProfile { Role = (FirstPersonRole)_role.Selected, Weapon = _weapon.Text, MainRig = main.Id, ArmsRig = arms?.Id, MainAsset = MainAsset, ArmsAsset = arms is null ? null : "SPerso.ATT_01_fp", WeaponMount = mount };
            string[] labels = ["Idle", "Move", "Draw", "Holster", "Fire", "Reload", "Aim", "Gadget"];
            string[] slots = _role.Selected == 0 ? ["SnipeP190", "MoveP190", "DrawP190", "HolsterP190", "FireP190", "ReloadP190", "AimP190", "GadgetP190"]
                : ["FirstWaitStFd2", "FirstWalkStFd2", "FirstDrawStEd2", "FirstHolsterStFd2", "FirstShooStFd2", "FirstReloStFd2", "FirstSnipStBg2", "FirstMineStBg2"];
            OpenScct.Animation.AnimationClip[] candidates = _state.Project.Clips.Where(c => c.Skeleton == main.Skeleton).ToArray();
            for (int i = 0; i < labels.Length; i++)
            {
                string pattern = labels[i] switch { "Idle" => "Wait", "Move" => "Walk", "Draw" or "Holster" => "Draw", "Fire" => "Shoo", "Reload" => "Relo", "Aim" => "Snip", _ => "Mine" };
                var clip = candidates.FirstOrDefault(c => c.Name.Equals(slots[i], StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault(c => c.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault();
                profile.Actions.Add(new() { Name = labels[i], Slot = slots[i], Clip = clip?.Id, ArmsClip = grip?.Id });
            }
            _profileId = profile.Id;
            Edit("Create first-person setup", p =>
            {
                if (profile.Actions.FirstOrDefault(a => a.Name == "Holster") is { Clip: { } drawId } holster)
                {
                    var reverse = AnimationEdits.Duplicate(p.Clips.Single(c => c.Id == drawId)); reverse.Name = profile.Role + " FP Holster";
                    AnimationEdits.Reverse(reverse); p.Clips.Add(reverse); holster.Clip = reverse.Id;
                }
                if (grip is not null) { p.Clips.Add(grip); p.Scene.Single(r => r.Id == arms!.Id).Clip = grip.Id; }
                p.Scene.Single(r => r.Id == main.Id).Transform = mount;
                p.Scene.Single(r => r.Id == main.Id).Clip = profile.Actions[0].Clip;
                p.FirstPersonProfiles.Add(profile);
            });
            Activate(); SelectAction(); _status.Text = profile.Role + " first-person setup ready.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { _status.Text = e.Message; }
    }
    private void SelectAction()
    {
        if (_updating || Profile is not { } profile || _action.Selected < 0 || _action.Selected >= profile.Actions.Count) return;
        FirstPersonAction action = profile.Actions[_action.Selected];
        string? clip = _part.Selected == 1 && profile.ArmsRig is not null ? action.ArmsClip : action.Clip;
        _stage.FirstPersonActionName = action.Name; _slot.Text = action.Slot;
        if (clip is not null) _state.SelectClip(clip);
        if (_stage.FirstPersonEye) _stage.FirstPerson();
    }
    private void NewAction()
    {
        if (_state.CurrentClip is not { } source) { _status.Text = "Select a motion in Project before creating an action clip."; return; }
        FirstPersonProfile? profile = Profile;
        OpenScct.Animation.AnimationClip clip = AnimationEdits.Duplicate(source);
        string name = profile is null ? source.Name + " action" : profile.Role + " FP " + _action.Text + (_part.Selected == 1 && profile.ArmsRig is not null ? " arms" : " weapon");
        clip.Name = name;
        for (int suffix = 2; _state.Project.Clips.Any(c => c.Name == clip.Name); suffix++) clip.Name = name + " " + suffix;
        Edit("New first-person action", p =>
        {
            p.Clips.Add(clip);
            if (profile is null) return;
            FirstPersonAction action = p.FirstPersonProfiles.Single(f => f.Id == profile.Id).Actions[_action.Selected];
            if (_part.Selected == 1 && profile.ArmsRig is not null) action.ArmsClip = clip.Id; else action.Clip = clip.Id;
        });
        _state.SelectClip(clip.Id);
        _showProject();
        _status.Text = profile is null ? "Created " + clip.Name + ". Load a setup to bind it to a first-person action." : "Created " + clip.Name + " for " + _action.Text + ".";
    }
    private void BindActions()
    {
        if (Profile is not { } profile) return;
        Edit("Bind first-person actions", p =>
        {
            FirstPersonProfile edited = p.FirstPersonProfiles.Single(f => f.Id == profile.Id); edited.Enabled = true;
            foreach (FirstPersonAction action in edited.Actions)
            {
                if (action.Clip is not null) Bind(edited.MainAsset, action.Slot, action.Clip);
                if (action.ArmsClip is not null && edited.ArmsAsset is { } asset) Bind(asset, action.Slot, action.ArmsClip);
            }
            void Bind(string target, string slot, string clip) { p.Bindings.RemoveAll(b => b.Target == target && b.Slot == slot); p.Bindings.Add(new() { Target = target, Slot = slot, Clip = clip }); }
        });
        _status.Text = "First-person actions bound as visual replacements.";
    }
    private void Refresh()
    {
        if (!IsNodeReady()) return;
        if (_projectId != _state.Project.Id)
        {
            _projectId = _state.Project.Id; _profileId = null; Active = false; _mode.Selected = 0;
            _stage.FirstPersonProfileId = null; _stage.FirstPersonEye = false;
            _stage.RefreshView();
        }
        _updating = true;
        foreach (Control child in GetChildren().OfType<Control>()) if (child != _mode) child.Visible = Active;
        if (_profileId is not null && Profile is null && Active) _profileId = _state.Project.FirstPersonProfiles.FirstOrDefault()?.Id;
        if (Profile is { } profile && _action.Selected >= 0 && _action.Selected < profile.Actions.Count) { _slot.Text = profile.Actions[_action.Selected].Slot; _fov.Value = profile.FieldOfView; }
        _newAction.Disabled = _state.CurrentClip is null;
        _newAction.TooltipText = _newAction.Disabled ? "Select or import a motion first" : "Create an editable copy of the selected motion and reveal it in Project";
        _bindActions.Disabled = Profile is null;
        _bindActions.TooltipText = Profile is null ? "Load a first-person setup before binding actions" : "Enable these first-person actions as explicit visual replacements";
        _slot.Editable = Profile is not null; _fov.Editable = Profile is not null;
        _updating = false;
    }
    private void Edit(string name, Action<AnimationProject> action) => _state.Session.Edit(name, action);
    private OptionButton Choice(string label, string[] values)
    {
        var group = new HBoxContainer(); AddChild(group); group.AddChild(new Label { Text = label });
        var options = new OptionButton(); group.AddChild(options); foreach (string value in values) options.AddItem(value); return options;
    }
}
