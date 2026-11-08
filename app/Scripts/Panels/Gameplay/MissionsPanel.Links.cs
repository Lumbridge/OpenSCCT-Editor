using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Gameplay;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Panels;

public partial class MissionsPanel
{
    private static MissionsPanel? s_instance;
    private string? _missionId;
    private string? _objectiveId;
    private Button _missionPicker = null!;
    private Button _objectivePicker = null!;
    private Label _linkHint = null!;
    private Label _paletteTarget = null!;
    private Control _completionBar = null!;
    private Control _completionDraft = null!;
    private Actor? Mission => _missionId is { } id ? Context.Project.FindActor(id) : null;
    private Actor? Objective => _objectiveId is { } id ? Context.Project.FindActor(id) : null;

    private void BuildLinks()
    {
        s_instance = this;
        var missions = new HBoxContainer { Name = "MissionRow" };
        AddChild(missions);
        missions.AddChild(new Label { Text = "1. Mission", MouseFilter = MouseFilterEnum.Pass });
        _missionPicker = Picker(missions, "Choose mission", ChooseMission);
        PanelUi.Button(missions, "New mission", "Create a Story mission, then add its objectives below", NewMission);
        WorkbenchUi.Guard(PanelUi.Button(missions, "Add objectives…", "Search and check existing objectives or sub-missions to add", () =>
            AddPicker(_missionPicker, Mission, objectives: true)), () => Mission is not null);
        WorkbenchUi.Guard(PanelUi.IconButton(missions, "link", "Link selected objectives to the chosen mission", () =>
            LinkSelected(Mission, objectives: true)), () => CanLinkSelection(Mission, true));

        var objectives = new HBoxContainer { Name = "ObjectiveRow" };
        AddChild(objectives);
        objectives.AddChild(new Label { Text = "2. Objective", MouseFilter = MouseFilterEnum.Pass });
        _objectivePicker = Picker(objectives, "Choose objective", ChooseObjective);
        WorkbenchUi.Guard(PanelUi.Button(objectives, "New objective", "Create an objective already linked to the chosen mission", NewObjective), () => Mission is not null);
        WorkbenchUi.Guard(PanelUi.Button(objectives, "New device…", "Choose a device, then click in a view to place it and link it to this objective", NewDevice), () => Objective is not null);
        WorkbenchUi.Guard(PanelUi.Button(objectives, "Add devices…", "Link computers, bomb targets or disks that complete this objective", () =>
            AddPicker(_objectivePicker, Objective, objectives: false)), () => Objective is not null);
        Button linkDevices = PanelUi.IconButton(objectives, "link", "Link selected devices to the chosen objective", () => LinkSelected(Objective, objectives: false));
        linkDevices.Name = "LinkSelectedDevices";
        WorkbenchUi.Guard(linkDevices, () => CanLinkSelection(Objective, false));
        var hints = new HBoxContainer();
        AddChild(hints);
        _linkHint = WorkbenchUi.Value(hints, "Choose a mission, add objectives, then connect their devices.", "Dim");
        PanelUi.Toggle(hints, "Completion actions", "Show what missions do when complete: doors, messages and timing", false, on =>
        { _completionBar.Visible = on; _completionDraft.Visible = on; });
        PanelUi.Button(hints, "Check", "Check mission links and other map rules", () => Context.Run("build.check"), flat: true);
        Context.SelectionChanged += FollowSelection;
        Context.SessionReplaced += () => { _missionId = null; _objectiveId = null; UpdateLinks(); };
    }

    private void BuildPalette(Control parent)
    {
        var palette = new VBoxContainer { Name = "MissionPlaceables", CustomMinimumSize = new Vector2(240, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        parent.AddChild(palette);
        PanelUi.Caption(palette, "Objectives palette · drag into a view", wrap: false);
        _paletteTarget = PanelUi.Caption(palette, "", wrap: false);
        VBoxContainer scroll = PanelUi.Scroll(palette);
        var flow = new HFlowContainer();
        scroll.AddChild(flow);
        foreach (Placeable entry in Placeables.Available(EditorServices.Classes).Where(p => p.Group == PlaceableGroup.Objectives))
        {
            var tile = new PlaceableTile(entry, false, null, ArmPlaceable, PlacementTargets);
            flow.AddChild(tile);
            tile.Retip(Context.Project.Actors.Count(a => a.Class == entry.Class));
        }
    }

    private IReadOnlyList<string> PlacementTargets(Placeable entry)
    {
        Actor? target = entry.ShortClass == "SMission" ? null : entry.ShortClass == "SObjective" ? Mission : Objective ?? Mission;
        return target is not null ? [target.Id] : [];
    }

    private void ArmPlaceable(Placeable entry)
    {
        IReadOnlyList<string> targets = PlacementTargets(entry);
        if (ToolManager.Instance is not { } tools) return;
        tools.ArmPalette(entry.Class, targets);
        tools.Activate("place");
        tools.RefreshOptions();
        Context.SetStatus($"Click in a view to place {entry.InSentence}. " + (targets.Count > 0
            ? $"New placements link to {string.Join(", ", targets)}. W returns to Move." : "Right-click the placed actor to link it."));
    }

    private void NewDevice()
    {
        List<string> classes = Placeables.Available(EditorServices.Classes)
            .Where(p => MissionLinks.IsTrigger(new Actor("candidate", p.Class))).Select(p => p.Class).ToList();
        ClassPicker.Open(_objectivePicker, classes, "", cls =>
        {
            if (Placeables.Find(cls, EditorServices.Classes) is { } entry) ArmPlaceable(entry);
        });
    }

    public static void OpenForSelection()
    {
        DockTabs.ShowPanel("Missions");
        s_instance?.FollowSelection(inferDevices: true);
    }

    internal void RevealCompletionControls(Control target)
    {
        if (target != _completionBar && target != _completionDraft && !_completionBar.IsAncestorOf(target) && !_completionDraft.IsAncestorOf(target)) return;
        _completionBar.Visible = _completionDraft.Visible = true;
        if (DiscoveryNavigation.FindControl(this, "Completion actions") is Button toggle) toggle.SetPressedNoSignal(true);
    }

    private static Button Picker(Control row, string text, Action picked)
    {
        Button button = PanelUi.Button(row, text, "Choose by name; this target stays chosen while you select actors in the map", picked);
        button.CustomMinimumSize = new Vector2(80, 0);
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return button;
    }

    private void ChooseMission()
    {
        MapProject project = Context.Project;
        // Capture the objective before the chooser changes the panel's target.
        List<Actor> selected = Context.Selection.Items.Where(r => r.Kind == ObjectKind.Actor)
            .Select(r => project.FindActor(r.Id)).OfType<Actor>().ToList();
        List<Actor> objectives = selected.Where(a => MissionLinks.IsObjective(a) && !MatchPlan.IsMission(a))
            .Concat(project.Actors.Where(a => MissionLinks.IsObjective(a) && !MatchPlan.IsMission(a)
                && selected.Any(d => MissionLinks.IsTrigger(d) && MissionLinks.Contains(a, "Triggers", d.Id))))
            .DistinctBy(a => a.Id).ToList();
        if (objectives.Count == 0 && selected.Any(MissionLinks.IsTrigger))
        {
            Context.SetStatus("This device has no objective yet. Use Choose objective to link it to an existing objective first.");
            DiscoveryNavigation.Reveal(_objectivePicker);
            return;
        }
        List<Actor> missions = project.Actors.Where(MatchPlan.IsMission)
            .Where(m => objectives.All(o => MissionLinks.Refusal(project, m, o) is null)).ToList();
        string title = objectives.Count == 0 ? "Choose a mission to view"
            : $"Choose the mission to link {string.Join(", ", objectives.Select(MissionLinks.Name))} to";
        MissionActorPicker.Open(_missionPicker, missions, title, false, a => ModeName(a), ids =>
        {
            if (!ReferenceEquals(project, Context.Project)) { Context.SetStatus("The map changed; choose the mission again."); return; }
            try
            {
                if (objectives.Count > 0)
                {
                    var command = MissionLinks.Add(project, ids[0], objectives.Select(o => o.Id));
                    if (command is not null) Context.Execute(command);
                    Context.SetStatus(command is null ? "These objectives are already linked to this mission."
                        : $"Linked {PanelUi.Count(objectives.Count, "objective")} to {MissionLinks.Name(project.FindActor(ids[0])!)}. Undo restores the previous links.");
                }
                SetMission(ids[0]);
                if (objectives.FirstOrDefault() is { } objective) { _objectiveId = objective.Id; UpdateLinks(); }
            }
            catch (ArgumentException ex) { Context.SetStatus(ex.Message); }
        });
    }

    /// <summary>Guides inspect existing links; they never create actors on the user's behalf.</summary>
    public static void RevealTutorialObjective(string? objectiveId, string field, string? deviceId = null)
    {
        DockTabs.ShowPanel("Missions");
        if (s_instance is not { } panel) return;
        if (objectiveId is not null && panel.Context.Project.FindActor(objectiveId) is { } objective)
        {
            panel.Context.Selection.Set([ObjectRef.Actor(objective.Id)]);
            panel.FollowSelection(inferDevices: true);
        }
        else if (deviceId is not null && panel.Context.Project.FindActor(deviceId) is { } device)
        {
            // An imported, unlinked device must not inherit a different task pinned in this panel.
            panel._missionId = panel._objectiveId = null;
            panel.Context.Selection.Set([ObjectRef.Actor(device.Id)]);
            panel.UpdateLinks();
        }
        DiscoveryNavigation.Reveal(field switch
        {
            "mission" => panel._missionPicker,
            "objective" => panel._objectivePicker,
            _ => panel._tree,
        }, lesson: true);
    }

    private void SetMission(string id)
    {
        _missionId = id;
        _objectiveId = Mission is { } mission ? MissionTree.Refs(mission.Props["Objectives"]).FirstOrDefault(x => Context.Project.FindActor(x) is not null) : null;
        // Disk Hunt counts its own devices, without an intermediate objective.
        if (_objectiveId is null && Mission is { } m && (int)ClassDefaults.Number(m, "GameMode", 0) == GameModes.DiskHunt) _objectiveId = id;
        UpdateLinks();
    }

    private void ChooseObjective()
    {
        MapProject project = Context.Project;
        List<Actor> devices = Context.Selection.Items.Where(r => r.Kind == ObjectKind.Actor)
            .Select(r => project.FindActor(r.Id)).OfType<Actor>().Where(MissionLinks.IsTrigger).ToList();
        List<Actor> actors = Mission is { } mission
            ? MissionTree.Refs(mission.Props["Objectives"]).Select(Context.Project.FindActor).OfType<Actor>().Where(MissionLinks.IsObjective).ToList()
            : Context.Project.Actors.Where(a => MissionLinks.IsObjective(a) && !MatchPlan.IsMission(a)).ToList();
        if (Mission is { } m) actors.Add(m);
        actors.RemoveAll(a => devices.Any(d => MissionLinks.Refusal(project, a, d) is not null));
        MissionActorPicker.Open(_objectivePicker, actors, devices.Count == 0 ? "Choose the objective whose devices to link"
                : $"Choose the objective to link {string.Join(", ", devices.Select(MissionLinks.Name))} to", false,
            a => MatchPlan.IsMission(a) ? "Mission's own devices" : DeviceCount(a), ids =>
            {
                if (!ReferenceEquals(project, Context.Project)) { Context.SetStatus("The map changed; choose the objective again."); return; }
                try
                {
                    if (devices.Count > 0)
                    {
                        var command = MissionLinks.Add(project, ids[0], devices.Select(d => d.Id));
                        if (command is not null) Context.Execute(command);
                        Context.SetStatus(command is null ? "These devices are already linked to this objective."
                            : $"Linked {PanelUi.Count(devices.Count, "device")} to {MissionLinks.Name(project.FindActor(ids[0])!)}. Undo restores the previous links.");
                    }
                    _objectiveId = ids[0];
                    UpdateLinks();
                }
                catch (ArgumentException ex) { Context.SetStatus(ex.Message); }
            });
    }

    private void AddPicker(Control anchor, Actor? parent, bool objectives)
    {
        if (parent is null) return;
        string parentId = parent.Id;
        List<Actor> actors = Context.Project.Actors.Where(a => (objectives ? MissionLinks.IsObjective(a) : MissionLinks.IsTrigger(a))
            && MissionLinks.Refusal(Context.Project, parent, a) is null && !MissionLinks.Contains(parent, objectives ? "Objectives" : "Triggers", a.Id)).ToList();
        MissionActorPicker.Open(anchor, actors, $"Link {(objectives ? "objectives" : "devices")} to {MissionLinks.Name(parent)}", true,
            a => Owners(a, objectives ? "Objectives" : "Triggers"), ids => AddLinks(parentId, ids));
    }

    private string Owners(Actor child, string property)
    {
        string[] owners = Context.Project.Actors.Where(a => MissionLinks.IsObjective(a) && MissionLinks.Contains(a, property, child.Id))
            .Select(MissionLinks.Name).ToArray();
        return owners.Length == 0 ? "Unlinked · " + child.ShortClass : "Also in: " + string.Join(", ", owners);
    }

    private IEnumerable<string> Selected(Actor parent, bool objectives) => Context.Selection.Items.Where(r => r.Kind == ObjectKind.Actor)
        .Select(r => Context.Project.FindActor(r.Id)).OfType<Actor>()
        .Where(a => (objectives ? MissionLinks.IsObjective(a) : MissionLinks.IsTrigger(a)) && MissionLinks.Refusal(Context.Project, parent, a) is null
            && !MissionLinks.Contains(parent, objectives ? "Objectives" : "Triggers", a.Id)).Select(a => a.Id);

    private bool CanLinkSelection(Actor? parent, bool objectives) => parent is not null && Selected(parent, objectives).Any();
    private void LinkSelected(Actor? parent, bool objectives) { if (parent is not null) AddLinks(parent.Id, Selected(parent, objectives).ToList()); }

    private void AddLinks(string parentId, IReadOnlyList<string> ids)
    {
        try
        {
            if (MissionLinks.Add(Context.Project, parentId, ids) is not { } command) return;
            Context.Execute(command);
            Context.SetStatus($"Linked {ids.Count} item(s) to {MissionLinks.Name(Context.Project.FindActor(parentId)!)}. Undo restores the previous links.");
            if (parentId == _missionId && ids.FirstOrDefault() is { } first && Context.Project.FindActor(first) is { } child && MissionLinks.IsObjective(child)) _objectiveId = first;
            UpdateLinks();
        }
        catch (ArgumentException ex) { Context.SetStatus(ex.Message); }
    }

    private void NewMission()
    {
        var actor = new Actor(Context.Project.NewId("Mission"), "SBase.SMission") { Location = Context.PlacementPoint?.Invoke() ?? Context.Project.Workspace.CameraLocation };
        actor.Props.Set("ObjectiveName", SvJsonNode.String("New mission"));
        actor.Props.Set("GameMode", SvJsonNode.Integer(5));
        actor.Props.Set("MinimumObjectives", SvJsonNode.Integer(0));
        Context.Execute(new AddObjectsCommand("Create Story mission", actors: [actor]));
        SetMission(actor.Id);
        Context.Selection.Set([ObjectRef.Actor(actor.Id)]);
        Context.SetStatus("Story mission created. Add objectives below; rename it and set its game mode in Inspector.");
    }

    private void NewObjective()
    {
        if (Mission is not { } mission) return;
        var actor = new Actor(Context.Project.NewId("Objective"), "SBase.SObjective") { Location = mission.Location };
        actor.Props.Set("ObjectiveName", SvJsonNode.String("New objective"));
        Context.Execute(MissionLinks.CreateObjective(Context.Project, mission.Id, actor));
        _objectiveId = actor.Id;
        Context.Selection.Set([ObjectRef.Actor(actor.Id)]);
        UpdateLinks();
        Context.SetStatus("Objective created and linked. Add its computer, bomb target or disk with Add devices.");
    }

    private void FollowSelection() => FollowSelection(inferDevices: false);

    private void FollowSelection(bool inferDevices)
    {
        if (Context.Selection.Items.Count != 1 || Context.Selection.Items.First() is not { Kind: ObjectKind.Actor } selected
            || Context.Project.FindActor(selected.Id) is not { } actor) return;
        if (MatchPlan.IsMission(actor)) SetMission(actor.Id);
        else if (MissionLinks.IsObjective(actor))
        {
            _objectiveId = actor.Id;
            if (Mission is not { } mission || !MissionLinks.Contains(mission, "Objectives", actor.Id))
                _missionId = Context.Project.Actors.FirstOrDefault(a => MatchPlan.IsMission(a) && MissionLinks.Contains(a, "Objectives", actor.Id))?.Id;
            UpdateLinks();
        }
        else if (MissionLinks.IsTrigger(actor))
        {
            if (!inferDevices && Objective is not null) return; // Device selection must not replace a pinned link target.
            Actor? owner = Context.Project.Actors.FirstOrDefault(a => MissionLinks.IsObjective(a) && MissionLinks.Contains(a, "Triggers", actor.Id));
            if (owner is null) return; // Keep the target pinned for unlinked devices.
            _objectiveId = owner.Id;
            _missionId = MatchPlan.IsMission(owner) ? owner.Id : Context.Project.Actors.FirstOrDefault(a => MatchPlan.IsMission(a) && MissionLinks.Contains(a, "Objectives", owner.Id))?.Id;
            UpdateLinks();
        }
    }

    private void UpdateLinks()
    {
        if (_missionPicker is null) return;
        if (Mission is null) _missionId = null;
        if (Objective is null) _objectiveId = null;
        _missionPicker.Text = Mission is { } mission ? MissionLinks.Name(mission) : "Choose mission…";
        _missionPicker.TooltipText = Mission is { } m ? MissionLinks.Label(m) + " · " + ModeName(m) : "Choose a mission by name, or create one";
        _missionPicker.TooltipText += "\nChoose links selected objectives (or the objectives of selected devices) to this mission. With neither selected, it only changes the viewed mission.";
        _objectivePicker.Text = Objective is { } objective ? MissionLinks.Name(objective) : "Choose objective…";
        _objectivePicker.TooltipText = Objective is { } o ? MissionLinks.Label(o) + " · " + DeviceCount(o) : "Choose the objective whose devices to link";
        _objectivePicker.TooltipText += "\nWith devices selected in the map, Choose links them to the chosen objective. With no devices selected, it only changes the viewed objective.";
        _linkHint.Text = Objective is { } target ? $"{MissionLinks.Name(target)}: {DeviceCount(target)}. × unlinks; arrows reorder."
            : Mission is { } chosen ? $"{MissionLinks.Name(chosen)}: {MissionTree.Refs(chosen.Props["Objectives"]).Count} objectives. Add existing objectives or create one."
            : "Choose a mission, add its objectives, then connect their devices. Every link can be undone.";
        _linkHint.TooltipText = _linkHint.Text;
        if (_paletteTarget is not null)
        {
            _paletteTarget.Text = Objective is { } into ? $"Devices link to {MissionLinks.Name(into)}"
                : Mission is { } intoMission ? $"Objectives join {MissionLinks.Name(intoMission)}" : "Choose a mission to link new placements";
            _paletteTarget.TooltipText = _paletteTarget.Text;
        }
    }

    private static string DeviceCount(Actor a) => PanelUi.Count(MissionTree.Refs(a.Props["Triggers"]).Count, "device");
    private static string ModeName(Actor a)
    {
        int mode = (int)ClassDefaults.Number(a, "GameMode", 0);
        return mode == 5 ? "Story" : mode >= 0 && mode < MissionTree.GameModes.Count ? MissionTree.GameModes[mode].Replace("GM_", "") : "Unknown mode";
    }
}
