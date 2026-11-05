using Godot;
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

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        // One toolbar row: the check, the commands that add on-complete actions, and at its end the commands on the
        // action chosen in the tree.
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        AddChild(bar);
        Button check = PanelUi.Button(bar, "Check missions", "Run Map Check (mission rules included)", () => Context.Run("build.check"));
        check.Icon = Icons.Get("success", EditorTheme.Text);
        bar.AddChild(new VSeparator());
        WorkbenchUi.Action(bar, "Fire selected", "On complete, the selected mission fires the other selected actors, with the delay and type below",
            "gameplay.zone.fireSelected", Context);
        WorkbenchUi.Action(bar, "Add message", "On complete, the selected mission shows the messages below", "gameplay.zone.message", Context);
        WorkbenchUi.Spacer(bar);
        WorkbenchUi.Guard(PanelUi.Button(bar, "Set timing", "Give the chosen on-complete action the delay and type below", SetTiming), () => ChosenAction is not null);
        WorkbenchUi.Guard(PanelUi.IconButton(bar, "trash", "Remove the chosen on-complete action", RemoveAction, danger: true), () => ChosenAction is not null);

        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        PanelUi.Columns(_tree, ("Mission / Objective", 160, true), ("Detail", 160, true));
        _tree.ItemSelected += () => Pick(focus: false);
        _tree.ItemActivated += () => Pick(focus: true);
        AddChild(_tree);
        _empty = PanelUi.EmptyState(_tree, "type-objective", "No missions",
            "The map has no SMission, no objective outside one and no player-count rules. Place a mission and its objectives to see them here.");

        // What a new on-complete action gets, and what Set timing gives the chosen one: pinned under the tree. The
        // timing and the two messages wrap as groups in a narrow dock; the messages share their line.
        var draft = new HFlowContainer();
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
    }

    public override void Refresh() => _update.Queue();

    private void Rebuild()
    {
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        MissionTree t = GameplayState.Instance.Missions;
        bool empty = t.Missions.Count == 0 && t.Unlisted.Count == 0 && t.MatchSize.Count == 0;
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
            TreeItem mission = Row(root, m.Id, $"{m.ModeName}: {m.MinimumObjectives} of {PanelUi.Count(m.Completers, "objective")}{chained}", m.Id);
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
                TreeItem objective = Row(mission, o.Id, o.Name.Length > 0 ? o.Name : PanelUi.Count(o.Triggers.Count, "trigger"), o.Id);
                if (o.Triggers.Count == 0)
                {
                    objective.SetIcon(1, PanelIcons.Severity(Severity.Warning));
                    objective.SetText(1, "No trigger: it can never be completed");
                    objective.SetTooltipText(1, objective.GetText(1));
                }

                foreach (string trigger in o.Triggers)
                {
                    bool missing = o.Missing.Contains(trigger, StringComparer.OrdinalIgnoreCase);
                    TreeItem item = Row(objective, trigger, missing ? "Not in the map" : ClassOf(trigger), missing ? "" : trigger);
                    if (missing)
                    {
                        item.SetIcon(0, PanelIcons.Severity(Severity.Error));
                    }
                }

                OnComplete(objective, o.Id);
            }

            foreach (string trigger in m.Triggers)
            {
                Row(mission, trigger, "Mission trigger · " + ClassOf(trigger), trigger);
            }

            OnComplete(mission, m.Id);
        }

        if (t.Unlisted.Count > 0)
        {
            TreeItem unlisted = Row(root, "Objectives in no mission", PanelUi.Count(t.Unlisted.Count, "objective"), "");
            unlisted.SetIcon(0, PanelIcons.Severity(Severity.Warning));
            foreach (string id in t.Unlisted)
            {
                Row(unlisted, id, ClassOf(id), id);
            }
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

    private string ClassOf(string id) => Context.Project.FindActor(id)?.ShortClass ?? "";

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
