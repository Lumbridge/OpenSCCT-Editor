using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Panels;

public partial class MissionsPanel
{
    private CheckBox _chained = null!;
    private OptionButton _missionMode = null!;
    private SpinBox _minimum = null!;
    private SpinBox _random = null!;
    private Label _sequenceHint = null!;
    private CheckBox _showHud = null!;
    private CheckBox _awardScore = null!;
    private CheckBox _hideInactive = null!;
    private Actor? VisibilityTarget => Context.Selection.Items.Count == 1 && Context.Selection.Items.First() is { Kind: ObjectKind.Actor } selected
        && Context.Project.FindActor(selected.Id) is { } device && MissionLinks.IsTrigger(device) ? device : Objective;

    private void BuildMissionSettings()
    {
        var row = PanelUi.Toolbar(this);
        row.Name = "MissionSettings";
        _parentMission = PanelUi.Button(row, "Parent mission", "Return to this zone's parent to add or reorder zones", ChooseParent);
        _parentMission.Visible = false;
        _chained = SettingFlag(row, "Chained: one at a time", "Chained",
            "On by default. Only the first objective is available when this mission starts. Completing it unlocks the next, in tree order. Turn off to allow objectives together.",
            on => SetProperty(Mission, "bChained", SvJsonNode.Bool(on), "Change mission chaining"));
        var mode = new HBoxContainer();
        row.AddChild(mode);
        mode.AddChild(new Label { Text = "Mode", MouseFilter = MouseFilterEnum.Pass });
        _missionMode = PanelUi.Options(mode, "The game mode that activates this mission. Undefined is for sub-missions activated by a parent.");
        _missionMode.Name = "GameMode";
        _missionMode.CustomMinimumSize = new Vector2(145, 0);
        for (int i = 0; i < MissionTree.GameModes.Count; i++)
            _missionMode.AddItem(i == 0 ? "Zone (parent)" : i == 5 ? "Story" : PanelUi.Words(MissionTree.GameModes[i].Replace("GM_", "")));
        _missionMode.ItemSelected += value => SetProperty(Mission, "GameMode", SvJsonNode.Integer(value), "Change mission game mode");
        _minimum = SettingNumber(row, "To win (0 = all)", "MinimumObjectives",
            "Objectives needed to complete an unchained mission; 0 means all active objectives. Ignored while chained. In Disk Hunt, the number of disks to activate.",
            value => SetProperty(Mission, "MinimumObjectives", SvJsonNode.Integer(value), "Change objectives to win"));
        _random = SettingNumber(row, "Random active (0 = all)", "RandomObjectives",
            "Choose this many objectives at random when an unchained mission starts; 0 means all. Ignored while chained and in Disk Hunt.",
            value => SetProperty(Mission, "RandomObjectives", SvJsonNode.Integer(value), "Change random objectives"));
        _sequenceHint = PanelUi.Caption(this, "");
        _sequenceHint.Name = "SequenceHint";
        _sequenceHint.MouseFilter = MouseFilterEnum.Pass;
    }

    private void BuildObjectiveSettings(Control parent)
    {
        var row = PanelUi.Toolbar(parent);
        row.Name = "ObjectiveSettings";
        _showHud = SettingFlag(row, "Show on HUD", "ShowOnHud", "Show the chosen objective's status on the players' HUD.",
            on => SetProperty(Objective, "bShowStatusOnHud", SvJsonNode.Bool(on), "Change objective HUD visibility"));
        _awardScore = SettingFlag(row, "Award score", "AwardScore", "Award points to the player who completes the chosen objective.",
            on => SetProperty(Objective, "bScore", SvJsonNode.Bool(on), "Change objective scoring"));
        _hideInactive = SettingFlag(row, "Hide while inactive", "HideWhileInactive", "",
            on => SetProperty(VisibilityTarget, ObjectiveVisibility.Property, SvJsonNode.Bool(on), "Change inactive objective visibility"));
        Context.SelectionChanged += UpdateSettings;
    }

    private static CheckBox SettingFlag(Control parent, string text, string name, string tip, Action<bool> changed)
    {
        var check = new CheckBox { Name = name, Text = text, TooltipText = tip };
        check.Toggled += on => changed(on);
        parent.AddChild(check);
        return check;
    }

    private static SpinBox SettingNumber(Control parent, string text, string name, string tip, Action<long> changed)
    {
        var group = new HBoxContainer();
        parent.AddChild(group);
        group.AddChild(new Label { Text = text, TooltipText = tip, MouseFilter = MouseFilterEnum.Pass });
        SpinBox spin = PanelUi.Spin(group, 0, int.MaxValue, 1, 0, tip, 64);
        spin.Name = name;
        spin.ValueChanged += value => changed((long)value);
        return spin;
    }

    private void UpdateSettings()
    {
        if (_chained is null) return;
        Actor? mission = Mission;
        List<Actor> parents = Parents;
        _parentMission.Visible = parents.Count > 0;
        _parentMission.TooltipText = "Return to parent: " + string.Join(", ", parents.Select(MissionLinks.Name)) + ". Add the next zone or change zone order there.";
        bool chained = mission is not null && ClassDefaults.Flag(mission, "bChained");
        bool diskHunt = mission is not null && (int)ClassDefaults.Number(mission, "GameMode", 0) == GameModes.DiskHunt;
        _chained.Disabled = mission is null || diskHunt;
        _chained.SetPressedNoSignal(chained);
        _missionMode.Disabled = mission is null;
        int mode = mission is null ? -1 : (int)ClassDefaults.Number(mission, "GameMode", 0);
        _missionMode.Select(mode >= 0 && mode < _missionMode.ItemCount ? mode : -1);
        _minimum.Editable = mission is not null && (!chained || diskHunt);
        _random.Editable = mission is not null && !chained && !diskHunt;
        _minimum.SetValueNoSignal(mission is null ? 0 : ClassDefaults.Number(mission, "MinimumObjectives", 0));
        _random.SetValueNoSignal(mission is null ? 0 : ClassDefaults.Number(mission, "RandomObjectives", 0));
        _sequenceHint.Text = mission is null ? "Choose a mission to set when its objectives become available. New missions are chained by default."
            : diskHunt ? "Disk Hunt uses the mission's disks directly. Chaining and random objective selection do not apply."
            : chained ? "One at a time: only objective 1 starts available; each completion unlocks the next. All are required. Use the tree arrows to change play order."
            : ClassDefaults.Number(mission, "RandomObjectives", 0) > 0
                ? "Any order: a random selection starts available together. To win sets how many must be completed; 0 means all active objectives."
                : "Any order: all objectives start available together. To win sets how many must be completed; 0 means all.";
        _sequenceHint.TooltipText = _sequenceHint.Text;
        if (mission is not null && MissionTree.Refs(mission.Props["Objectives"]).Any(id => Context.Project.FindActor(id) is { } child && MatchPlan.IsMission(child)))
        {
            _sequenceHint.Text = chained
                ? "Zones unlock in tree order: finish each zone's required objectives to unlock the next. Select a zone to set its To win count; arrows change zone order."
                : "Zones are available together. Turn Chained on here to unlock zones one at a time; select each zone to set how many objectives finish it.";
            _sequenceHint.TooltipText = _sequenceHint.Text;
        }
        if (parents.Count > 0)
        {
            _sequenceHint.Text = $"Inside {string.Join(", ", parents.Select(MissionLinks.Name))}. " + _sequenceHint.Text;
            _sequenceHint.TooltipText = _sequenceHint.Text;
        }
        _showHud.Disabled = _awardScore.Disabled = Objective is null;
        _showHud.SetPressedNoSignal(Objective is { } objective && ClassDefaults.Flag(objective, "bShowStatusOnHud"));
        _awardScore.SetPressedNoSignal(Objective is { } target && ClassDefaults.Flag(target, "bScore"));
        Actor? visibility = VisibilityTarget;
        _hideInactive.Disabled = visibility is null;
        _hideInactive.SetPressedNoSignal(visibility is not null && ObjectiveVisibility.Hidden(Context.Project, visibility));
        _hideInactive.TooltipText = (visibility is null ? "Choose an objective or device." : $"For {MissionLinks.Label(visibility)}.")
            + " Off by default: inactive and unlinked devices stay visible but unusable. Turn on to hide them while inactive. A selected device overrides its objective's setting. OpenSCCT only.";
    }

    private void SetProperty(Actor? actor, string property, SvJsonNode value, string label)
    {
        if (actor is null) return;
        Context.Execute(ReplaceObjectsCommand.Edit(Context.Project, [ObjectRef.Actor(actor.Id)], label,
            actor: after => after.Props.Set(property, value)));
        UpdateSettings();
    }
}
