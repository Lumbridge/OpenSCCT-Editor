using Godot;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Missions and objectives: each SMission with its game mode and how many objectives win it, its objectives with the
/// triggers that complete them (missing ones in red), its own triggers, what it does when complete (zone on-complete
/// actions: doors, lights, sounds, messages, with delays), objectives no mission lists, and the actors with player-count
/// rules or win targets (the Match panel previews them). Click selects, double-click frames.
/// </summary>
[Panel("Missions", DockSide.Bottom, Order = 4)]
public partial class MissionsPanel : EditorPanel
{
    private Tree _tree = null!;
    private Control _empty = null!;
    private Label _draftTitle = null!;
    private DeferredAction _update = null!;
    private ScrollContainer _headerScroll = null!;
    private VBoxContainer _headerBody = null!;
    private readonly Dictionary<ulong, (string Parent, string Property, string Child)> _relationships = [];

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        BuildLinks();
        // One toolbar row: the check, the commands that add on-complete actions, and at its end the commands on the
        // action chosen in the tree.
        var bar = PanelUi.Toolbar(this);
        _completionBar = bar;
        bar.Visible = false;
        Button check = PanelUi.Button(bar, "Check missions", "Run Map Check (mission rules included)", () => Context.Run("build.check"));
        check.Icon = Icons.Get("success", EditorTheme.Text);
        bar.AddChild(new VSeparator());
        WorkbenchUi.Action(bar, "Fire selected", "On complete, the selected mission fires the other selected actors, with the delay and type below",
            "gameplay.zone.fireSelected", Context);
        WorkbenchUi.Action(bar, "Add message", "On complete, the selected mission shows the messages below", "gameplay.zone.message", Context);
        WorkbenchUi.Guard(PanelUi.Button(bar, "Set timing", "Give the chosen on-complete action the delay and type below", SetTiming), () => ChosenAction is not null);
        WorkbenchUi.Guard(PanelUi.IconButton(bar, "trash", "Remove the chosen on-complete action", RemoveAction, danger: true), () => ChosenAction is not null);

        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 50),
        };
        PanelUi.Columns(_tree, ("Mission / Objective", 160, true), ("Detail", 160, true));
        _tree.ItemSelected += () => Pick(focus: false);
        _tree.ItemActivated += () => Pick(focus: true);
        _tree.AllowRmbSelect = true;
        _tree.ItemMouseSelected += (_, button) =>
        {
            if (button != (long)MouseButton.Right || _tree.GetSelected()?.GetMetadata(0).AsString() is not { Length: > 0 } id) return;
            MissionLinkGuide.Menu(_tree, Context, id);
        };
        _tree.ButtonClicked += (item, _, button, _) => EditRelationship(item, (int)button);
        var content = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(content);
        content.AddChild(_tree);
        _tree.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _tree.SizeFlagsStretchRatio = 3;
        BuildPalette(content);
        _empty = PanelUi.EmptyState(_tree, "type-objective", "No missions",
            "Start with New mission above, add objectives, then link the computers, bomb targets or disks that complete them.");

        // What a new on-complete action gets, and what Set timing gives the chosen one: pinned under the tree. The
        // timing and the two messages wrap as groups in a narrow dock; the messages share their line.
        var draft = new HFlowContainer();
        _completionDraft = draft;
        draft.Visible = false;
        draft.AddThemeConstantOverride("h_separation", EditorTheme.SpaceM);
        draft.AddThemeConstantOverride("v_separation", EditorTheme.SpaceXs);
        AddChild(draft);
        var timing = new HBoxContainer();
        timing.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        draft.AddChild(timing);
        _draftTitle = new Label { ThemeTypeVariation = "GroupLabel", MouseFilter = MouseFilterEnum.Pass };
        timing.AddChild(_draftTitle);
        timing.AddChild(new Label { Text = "Delay", ThemeTypeVariation = "Dim", TooltipText = "Seconds after completion (the relay's delay)", MouseFilter = MouseFilterEnum.Pass });
        SpinBox delay = PanelUi.Spin(timing, 0, 3600, 0.05, ZoneActionDraft.Delay, "Seconds after completion", EditorTheme.NumberFieldWidth);
        delay.Suffix = "s";
        delay.ValueChanged += v => ZoneActionDraft.Delay = v;
        OptionButton type = PanelUi.Options(timing, "What the action sends");
        type.CustomMinimumSize = new Vector2(EditorTheme.PickerWidth, 0);
        foreach (MagicType t in Enum.GetValues<MagicType>())
        {
            type.AddItem(PanelUi.Words(MagicEvents.TypeWords(t)));
        }

        type.Selected = (int)ZoneActionDraft.Type;
        type.ItemSelected += i => ZoneActionDraft.Type = (MagicType)(int)i;
        var messages = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(EditorTheme.MessagesMinWidth, 0) };
        messages.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        draft.AddChild(messages);
        // Each message field has its own label: the two lines often start the same.
        messages.AddChild(new Label { Text = "Spy", ThemeTypeVariation = "Dim", TooltipText = "The spies' line of a new on-complete message", MouseFilter = MouseFilterEnum.Pass });
        messages.AddChild(DraftText(ZoneActionDraft.Spy, t => ZoneActionDraft.Spy = t, "Spies read"));
        messages.AddChild(new Label { Text = "Merc", ThemeTypeVariation = "Dim", TooltipText = "The mercenaries' line of a new on-complete message", MouseFilter = MouseFilterEnum.Pass });
        messages.AddChild(DraftText(ZoneActionDraft.Merc, t => ZoneActionDraft.Merc = t, "Mercenaries read"));
        _tree.ItemSelected += ShowDraftTitle;
        _tree.NothingSelected += ShowDraftTitle;
        ShowDraftTitle();
        Context.ProjectChanged += _ => _update.Queue();
        // Only the controls scroll when the dock is short. The mission tree always fills the remaining height.
        var header = GetChildren().OfType<Control>().Where(c => c != content).ToList();
        _headerScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        _headerBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_headerScroll);
        MoveChild(_headerScroll, 0);
        _headerScroll.AddChild(_headerBody);
        foreach (Control control in header) { RemoveChild(control); _headerBody.AddChild(control); }
        Resized += FitHeader;
        _headerBody.MinimumSizeChanged += FitHeader;
        FitHeader();
    }

    private void FitHeader()
    {
        float height = Math.Min(_headerBody.GetCombinedMinimumSize().Y, Math.Max(EditorTheme.RowHeight, Size.Y * 0.5f));
        if (Math.Abs(_headerScroll.CustomMinimumSize.Y - height) > 0.5f)
            _headerScroll.CustomMinimumSize = new Vector2(0, height);
    }

    public override void Refresh() => _update.Queue();

    private void Rebuild()
    {
        _tree.Clear();
        _relationships.Clear();
        TreeItem root = _tree.CreateItem();
        MissionTree t = GameplayState.Instance.Missions;
        if (_missionId is null && _objectiveId is null && t.Missions.Count == 1) SetMission(t.Missions[0].Id);
        UpdateLinks();
        bool empty = t.Missions.Count == 0 && t.Unlisted.Count == 0 && t.MatchSize.Count == 0 && !Context.Project.Actors.Any(MissionLinks.IsTrigger);
        _empty.Visible = empty;
        _tree.ColumnTitlesVisible = !empty;
        if (empty)
        {
            return;
        }

        if (t.Missions.Count == 0)
        {
            Row(root, "No missions (SMission)", "", "").SetCustomColor(0, EditorTheme.TextDim);
        }

        foreach (MissionNode m in t.Missions)
        {
            string chained = m.Chained ? ", chained" : "";
            string required = m.MinimumObjectives == 0 ? "all" : m.MinimumObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TreeItem mission = Row(root, Display(m.Id), $"{(m.GameMode == 5 ? "Story" : m.ModeName)}: {required} of {PanelUi.Count(m.Completers, "objective")}{chained}", m.Id);
            mission.SetIcon(0, PanelIcons.Type(Context.Project.FindActor(m.Id)?.Class ?? "SMission"));
            if (m.MinimumObjectives > m.Completers && m.Completers > 0)
            {
                mission.SetIcon(1, PanelIcons.Severity(Severity.Error));
            }

            if (m.Description.Length > 0)
            {
                mission.SetTooltipText(0, m.Description);
            }

            foreach (ObjectiveNode o in m.Objectives)
            {
                TreeItem objective = Row(mission, Display(o.Id), PanelUi.Count(o.Triggers.Count, "device"), o.Id);
                Relationship(objective, m.Id, "Objectives", o.Id, reorder: true);
                if (Context.Project.FindActor(o.Id) is not { } objectiveActor)
                {
                    objective.SetIcon(1, PanelIcons.Severity(Severity.Error));
                    objective.SetText(1, "Missing objective — unlink or replace it");
                }
                else if (o.Triggers.Count == 0 && !MatchPlan.IsMission(objectiveActor))
                {
                    objective.SetIcon(1, PanelIcons.Severity(Severity.Warning));
                    objective.SetText(1, "No devices linked");
                    objective.SetTooltipText(1, "Add a new device, or right-click this objective and choose Link to devices.");
                }

                foreach (string trigger in o.Triggers)
                {
                    bool missing = o.Missing.Contains(trigger, StringComparer.OrdinalIgnoreCase);
                    TreeItem item = Row(objective, Display(trigger), missing ? "Not in the map" : ClassOf(trigger), missing ? "" : trigger);
                    Relationship(item, o.Id, "Triggers", trigger);
                    if (missing)
                    {
                        item.SetIcon(0, PanelIcons.Severity(Severity.Error));
                    }
                }

                OnComplete(objective, o.Id);
            }

            foreach (string trigger in m.Triggers)
            {
                TreeItem item = Row(mission, Display(trigger), "Mission device · " + ClassOf(trigger), trigger);
                Relationship(item, m.Id, "Triggers", trigger);
            }

            OnComplete(mission, m.Id);
        }

        if (t.Unlisted.Count > 0)
        {
            TreeItem unlisted = Row(root, "Objectives in no mission", PanelUi.Count(t.Unlisted.Count, "objective"), "");
            unlisted.SetIcon(0, PanelIcons.Severity(Severity.Warning));
            foreach (string id in t.Unlisted)
            {
                TreeItem objective = Row(unlisted, Display(id), ClassOf(id), id);
                foreach (string trigger in MissionTree.Refs(Context.Project.FindActor(id)?.Props["Triggers"]))
                {
                    TreeItem item = Row(objective, Display(trigger), ClassOf(trigger), trigger);
                    Relationship(item, id, "Triggers", trigger);
                }
            }
        }

        var linked = Context.Project.Actors.Where(MissionLinks.IsObjective)
            .SelectMany(a => MissionTree.Refs(a.Props["Triggers"])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<Actor> unlinked = Context.Project.Actors.Where(a => MissionLinks.IsTrigger(a) && !linked.Contains(a.Id)).ToList();
        if (unlinked.Count > 0)
        {
            TreeItem devices = Row(root, "Devices needing an objective", PanelUi.Count(unlinked.Count, "device"), "");
            devices.SetIcon(0, PanelIcons.Severity(Severity.Warning));
            foreach (Actor actor in unlinked) Row(devices, Display(actor.Id), ClassOf(actor.Id) + " · right-click to link", actor.Id);
        }

        TreeItem rules = Row(root, "Player-count rules", t.MatchSize.Count == 0 ? "None: every match plays the whole map" : "", "");
        rules.SetCustomColor(0, EditorTheme.TextDim);
        foreach (MatchSizeRule r in t.MatchSize)
        {
            TreeItem rule = Row(rules, r.Id, r.Detail, r.Id);
            if (!r.Native)
            {
                rule.SetIcon(1, PanelIcons.Severity(Severity.Warning));
            }
        }
    }

    private static LineEdit DraftText(string text, Action<string> changed, string tip)
    {
        var e = new LineEdit { Text = text, PlaceholderText = tip, TooltipText = tip, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(80, 0) };
        e.TextChanged += t => changed(t);
        return e;
    }

    /// <summary>What the mission does when complete, each action removable by its index.</summary>
    private void OnComplete(TreeItem mission, string id)
    {
        if (Context.Project.FindActor(id) is not { } m)
        {
            return;
        }

        List<ZoneAction> actions = ZoneActions.Of(Context.Project, GameplayState.Instance.Graph, m);
        if (actions.Count == 0)
        {
            return;
        }

        string relay = ZoneActions.OwnRelay(Context.Project, GameplayState.Instance.Graph, m)?.Id ?? "";
        TreeItem node = Row(mission, "When complete", relay.Length > 0 ? $"Through its relay {relay}" : "Fires its Event", relay);
        for (int i = 0; i < actions.Count; i++)
        {
            ZoneAction a = actions[i];
            string reaches = a.Receivers.Count == 0 ? "reaches nothing" : WorkbenchUi.Join(a.Receivers);
            string shared = a.Own ? "" : a.Relay is { } other ? $" (in {other}, shared with other senders)" : " (Tag shared with other senders)";
            TreeItem item = Row(node, $"{a.Kind}: {reaches}", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{MagicEvents.TypeWords(a.Type)} {a.Event} at {a.Delay:0.##} s{shared}"), a.Receivers.FirstOrDefault() ?? "");
            item.SetMetadata(1, $"{id}|{i}");
            item.SetTooltipText(0, a.Receivers.Count == 0 ? "No actor has this Tag" : $"{a.Kind}: {string.Join(", ", a.Receivers)}");
            if (a.Receivers.Count == 0)
            {
                item.SetIcon(0, PanelIcons.Severity(Severity.Warning));
            }
            else if (!a.Own)
            {
                item.SetCustomColor(1, EditorTheme.TextDim);
            }
        }
    }

    /// <summary>Names what the draft row's values are for: the chosen on-complete action, or a new one.</summary>
    private void ShowDraftTitle()
    {
        bool chosen = ChosenAction is not null;
        _draftTitle.Text = chosen ? "Chosen action" : "New action";
        _draftTitle.TooltipText = chosen
            ? "Set timing gives the chosen on-complete action this delay and type"
            : "Fire selected and Add message give a new on-complete action these values";
    }

    /// <summary>The on-complete action chosen in the tree ("mission|index"), or null.</summary>
    private string? ChosenAction => _tree.GetSelected()?.GetMetadata(1).AsString() is { Length: > 0 } tag ? tag : null;

    private void RemoveAction() => OnChosenAction((mission, index) => ZoneActions.Remove(Context.Project, GameplayState.Instance.Graph, mission, index));

    private void SetTiming() => OnChosenAction((mission, index) =>
        ZoneActions.SetTiming(Context.Project, GameplayState.Instance.Graph, mission, index, ZoneActionDraft.Delay, ZoneActionDraft.Type));

    /// <summary>Runs an edit of the on-complete action chosen in the tree, reporting a refusal on the status line.</summary>
    private void OnChosenAction(Func<string, int, OpenScct.Editor.Core.Commands.IEditCommand> edit)
    {
        if (_tree.GetSelected()?.GetMetadata(1).AsString() is not { Length: > 0 } tag || tag.Split('|') is not [string mission, string index])
        {
            Context.SetStatus("Choose an action under a mission's When complete first");
            return;
        }

        try
        {
            Context.Execute(edit(mission, int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch (ArgumentException ex)
        {
            Context.SetStatus(ex.Message);
        }
    }

    private string ClassOf(string id) => Context.Project.FindActor(id) is { } actor ? ClassNames.Title(actor.Class) : "";

    private string Display(string id) => Context.Project.FindActor(id) is { } a ? MissionLinks.Label(a) : id;

    private void Relationship(TreeItem item, string parent, string property, string child, bool reorder = false)
    {
        _relationships[item.GetInstanceId()] = (parent, property, child);
        if (reorder)
        {
            List<string> ids = MissionTree.Refs(Context.Project.FindActor(parent)?.Props[property]);
            int index = ids.FindIndex(id => string.Equals(id, child, StringComparison.OrdinalIgnoreCase));
            item.AddButton(1, Icons.Get("arrow-up", EditorTheme.TextDim), 1, index <= 0, "Move objective earlier (for chained missions)");
            item.AddButton(1, Icons.Get("arrow-down", EditorTheme.TextDim), 2, index < 0 || index >= ids.Count - 1, "Move objective later (for chained missions)");
        }
        item.AddButton(1, Icons.Get("close", EditorTheme.TextDim), 0, false, "Unlink from this parent. Keeps the actor and its other links.");
    }

    private void EditRelationship(TreeItem item, int button)
    {
        if (!_relationships.TryGetValue(item.GetInstanceId(), out var link)) return;
        try
        {
            var command = button == 0 ? MissionLinks.Remove(Context.Project, link.Parent, link.Property, link.Child)
                : MissionLinks.Move(Context.Project, link.Parent, link.Child, button == 1 ? -1 : 1);
            if (command is not null) Context.Execute(command);
            if (button == 0 && link.Property == "Objectives" && link.Parent == _missionId && link.Child == _objectiveId)
                SetMission(link.Parent);
        }
        catch (ArgumentException ex) { Context.SetStatus(ex.Message); }
    }

    private TreeItem Row(TreeItem parent, string text, string detail, string actorId)
    {
        TreeItem item = _tree.CreateItem(parent);
        item.SetText(0, text);
        item.SetText(1, detail);
        // The cells clip in a narrow dock: their tooltips have the whole text.
        item.SetTooltipText(0, text);
        item.SetTooltipText(1, detail);
        item.SetMetadata(0, actorId);
        return item;
    }

    private void Pick(bool focus)
    {
        if (_tree.GetSelected()?.GetMetadata(0).AsString() is not { Length: > 0 } id || Context.Project.FindActor(id) is null)
        {
            return;
        }

        ObjectRef[] refs = [ObjectRef.Actor(id)];
        Context.Selection.Set(refs);
        if (focus)
        {
            Context.Focus(refs);
        }
    }
}
