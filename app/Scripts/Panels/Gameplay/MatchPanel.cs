using System.Globalization;
using Godot;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Match size: the layout of a match of N players in one game mode as the server lays it out (the mode's missions with
/// their objectives and targets, objectives left out, actors hidden, movers held), the counts where it changes,
/// problems, what each team size plays, and each mission's win targets from 1v1 to 12v12. "Grey out absent" fades in
/// the views the actors that match leaves out. Player ranges themselves are set per actor in the Inspector.
/// </summary>
[Panel("Match", DockSide.Workbench, Order = 7)]
public partial class MatchPanel : EditorPanel
{
    private VBoxContainer _content = null!;
    private VBoxContainer _layout = null!;
    private VBoxContainer _wins = null!;
    private SpinBox _players = null!;
    private OptionButton _mode = null!;
    private Button _grey = null!;
    private DeferredAction _update = null!;
    private MatchPreview? _preview;
    private bool _playersSet;

    // The win grid's boxes by mission and team size, kept while the missions stay the same so that an edit does not
    // rebuild the box being typed in.
    private readonly Dictionary<(string Mission, int Size), SpinBox> _winBoxes = [];
    private string[] _winMissions = [];

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        var players = new HBoxContainer();
        bar.AddChild(players);
        players.AddChild(new Label { Text = "Preview at", ThemeTypeVariation = "Dim", TooltipText = "Everyone in the match, bots included", MouseFilter = MouseFilterEnum.Pass });
        _players = PanelUi.Spin(players, 1, MatchRuleSet.MostPlayers, 1, 4, "Players in the previewed match");
        _players.ValueChanged += _ => _update.Queue();
        _mode = PanelUi.Options(players, "The game mode of the previewed match: a round plays only that mode's missions");
        _mode.CustomMinimumSize = new Vector2(EditorTheme.NumberFieldWidth + EditorTheme.SpaceXxl, 0);
        _mode.ItemSelected += _ => _update.Queue();
        PanelUi.Button(bar, "Select absent", "Select the actors this match leaves out (and, with a mode, the other modes' objects)", SelectAbsent);
        _grey = PanelUi.Toggle(bar, "Grey out absent",
            "Draw faded in the views the actors this match leaves out of play (and, with a mode, the other modes' objects)", false, on =>
            {
                if (on)
                {
                    _update.Queue();
                }
                else
                {
                    MatchGreying.Off();
                }
            });
        PanelUi.Button(bar, "Use for Play", "Play tests lay the map out for this many players (Map Settings' Players; 0 there counts the bots and you)", () =>
            Context.Execute(PanelCommands.Play(Context.Project, "Play players", o => o.Players = (int)_players.Value)));
        _content = PanelUi.Scroll(this);
        _layout = new VBoxContainer();
        _wins = new VBoxContainer();
        _content.AddChild(_layout);
        _content.AddChild(_wins);
        Context.ProjectChanged += _ => _update.Queue();
        // Another map turns greying off (MatchGreyingPart); the toggle follows.
        Context.SessionReplaced += () => _grey.SetPressedNoSignal(false);
    }

    public override void Refresh()
    {
        // The first look starts at the playtest's count; after that the preview keeps the user's own.
        if (!_playersSet)
        {
            _players.SetValueNoSignal(Context.Project.Workspace.Play.MatchPlayers);
            _playersSet = true;
        }

        _update.Queue();
    }

    private void Rebuild()
    {
        foreach (Node child in _layout.GetChildren())
        {
            _layout.RemoveChild(child);
            child.QueueFree();
        }

        MapProject p = Context.Project;
        SyncModes(p);
        string? mode = _mode.Selected > 0 ? ModeAt(_mode.Selected) : null;
        _preview = MatchPlan.Preview(p, (int)_players.Value, mode);
        if (_grey.ButtonPressed)
        {
            bool wasOn = MatchGreying.On;
            MatchGreying.Show(_preview.Players, mode);
            if (!wasOn)
            {
                Context.SetStatus($"Greying out the {PanelUi.Count(MatchGreying.Count, "actor")} a {_preview.Players}-player match{(mode is null ? "" : " of " + GameModeFilter.Display(mode))} leaves out");
            }
        }

        LayoutSection(p, _preview);
        SizesSection(p, mode, _preview.TeamSize);
        WinSection(p);
    }

    // "All modes", then each mode the map's top missions play; the choice is kept by name.
    private void SyncModes(MapProject p)
    {
        List<string> modes = MatchPlan.Modes(p);
        string? chosen = _mode.Selected > 0 ? ModeAt(_mode.Selected) : null;
        bool same = _mode.ItemCount == modes.Count + 1 && modes.Select((m, i) => ModeAt(i + 1) == m).All(x => x);
        if (same)
        {
            return;
        }

        _mode.Clear();
        _mode.AddItem("All modes");
        foreach (string m in modes)
        {
            _mode.AddItem(GameModeFilter.Display(m));
            _mode.SetItemMetadata(_mode.ItemCount - 1, m);
            _mode.SetItemTooltip(_mode.ItemCount - 1, m);
        }

        int keep = chosen is null ? -1 : modes.FindIndex(m => string.Equals(m, chosen, StringComparison.OrdinalIgnoreCase));
        // A map with one mode previews it; with several, the one kept or the first.
        _mode.Select(keep >= 0 ? keep + 1 : modes.Count > 0 ? 1 : 0);
    }

    /// <summary>The stored name of the mode at <paramref name="index"/> (the list shows display names).</summary>
    private string ModeAt(int index) => _mode.GetItemMetadata(index).AsString();

    private void LayoutSection(MapProject p, MatchPreview preview)
    {
        int size = preview.TeamSize;
        PanelUi.Header(_layout, "Layout");
        PanelUi.Caption(_layout, $"{PanelUi.Count(preview.Players, "player")} · {size}v{size}{(preview.Mode is { } mode ? " · " + GameModeFilter.Display(mode) : "")}");
        if (!MatchPlan.HasAny(p))
        {
            PanelUi.Caption(_layout, "No actor has a player range or win targets: every match plays the whole map. " +
                "Set a range in the Inspector's Match size rows.");
        }

        foreach (MatchMissionView m in preview.Missions)
        {
            string random = m.Random > 0 ? $", {m.Random} at random" : "";
            string modeName = preview.Mode is null ? $" ({GameModeFilter.Display(m.Mode)})" : "";
            Pick(m.Id, $"{m.Id}{modeName}: {m.Needed} of {PanelUi.Count(m.Objectives, "objective")}{random}", m.Needed > m.Objectives ? Severity.Error : null);
        }

        List(p, "Left out", preview.LeftOut, "Objectives and missions outside their range: out of the round");
        List(p, "Hidden", preview.Hidden, "Other actors left out: hidden, without collision, deaf to events; a player start is not used");
        if (preview.Movers.Count > 0)
        {
            PanelUi.Caption(_layout, "Movers");
            foreach ((string id, Hold hold) in preview.Movers.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
            {
                Pick(id, $"{id}: {Describe(hold)}", null);
            }
        }

        List<int> layouts = MatchPlan.Layouts(p);
        if (layouts.Count > 1)
        {
            HBoxContainer row = PanelUi.Row(_layout, "Layouts from", "The player counts where the layout changes; click to preview");
            PanelUi.Segmented(row, [.. layouts.Select(count => (count.ToString(CultureInfo.InvariantCulture), $"Preview {count} players"))],
                layouts.IndexOf(preview.Players), i => _players.Value = layouts[i]);
        }

        foreach (string problem in MatchPlan.Problems(p))
        {
            PanelUi.Callout(_layout, Severity.Error, problem);
        }

        foreach (string warning in MatchPlan.TargetWarnings(p))
        {
            PanelUi.Callout(_layout, Severity.Warning, warning);
        }
    }

    /// <summary>What each team size plays at full teams, sizes that play alike on one row; a row previews its size.</summary>
    private void SizesSection(MapProject p, string? mode, int shown)
    {
        if (!MatchPlan.HasAny(p))
        {
            return;
        }

        PanelUi.Header(_layout, "By team size");
        foreach (MatchSizeRow row in MatchPlan.BySize(p, mode))
        {
            MatchPreview at = row.Preview;
            string sizes = row.From == row.To ? $"{row.From}v{row.From}" : $"{row.From}v{row.From} to {row.To}v{row.To}";
            var parts = new List<string>(at.Missions.Select(m => $"{m.Id} {m.Needed} of {m.Objectives}"));
            int held = at.Movers.Count(m => m.Value.Kind == HoldKind.Key);
            if (at.LeftOut.Count > 0)
            {
                parts.Add($"{at.LeftOut.Count} left out");
            }

            if (at.Hidden.Count > 0)
            {
                parts.Add($"{at.Hidden.Count} hidden");
            }

            if (held > 0)
            {
                parts.Add($"{held} mover{(held == 1 ? "" : "s")} held");
            }

            int players = row.From * 2;
            Button b = PanelUi.Button(_layout, $"{sizes}:  {(parts.Count == 0 ? "the whole map" : string.Join(" · ", parts))}",
                $"Preview {players} players ({row.From}v{row.From})", () => _players.Value = players, flat: shown < row.From || shown > row.To);
            b.Alignment = HorizontalAlignment.Left;
            b.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
    }

    /// <summary>
    /// The objectives each mission needs at each team size (0 keeps the mission's own number): a row per team size and
    /// a column per mission, so a few missions fit the dock. Built again only when the missions change.
    /// </summary>
    private void WinSection(MapProject p)
    {
        List<Actor> missions = p.Actors.Where(MatchPlan.IsMission).ToList();
        string[] ids = [.. missions.Select(m => m.Id)];
        if (ids.SequenceEqual(_winMissions, StringComparer.Ordinal) && _wins.GetChildCount() > 0)
        {
            foreach (Actor m in missions)
            {
                int[] win = m.Match?.WinBySize() ?? new int[MatchRuleSet.TeamSizes];
                for (int s = 0; s < MatchRuleSet.TeamSizes; s++)
                {
                    _winBoxes[(m.Id, s)].SetValueNoSignal(win[s]);
                }
            }

            return;
        }

        foreach (Node child in _wins.GetChildren())
        {
            _wins.RemoveChild(child);
            child.QueueFree();
        }

        _winBoxes.Clear();
        _winMissions = ids;
        PanelUi.Header(_wins, "Win targets by team size");
        if (missions.Count == 0)
        {
            PanelUi.Caption(_wins, "The map has no missions (SMission).");
            return;
        }

        PanelUi.Caption(_wins, "Objectives each mission needs at each team size; 0 keeps the mission's own minimum. Hover a mission's column title for its full name.");
        // Many missions scroll sideways instead of widening the dock.
        var across = new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _wins.AddChild(across);
        var grid = new GridContainer { Columns = missions.Count + 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", EditorTheme.SpaceXs);
        across.AddChild(grid);
        grid.AddChild(new Label());
        int common = CommonPrefix(missions.Select(m => m.Id).ToList());
        foreach (Actor m in missions)
        {
            string id = m.Id;
            var name = new Button
            {
                Text = Short(id, common),
                ThemeTypeVariation = "FlatButton",
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                Alignment = HorizontalAlignment.Left,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(EditorTheme.SpinCompactWidth, 0),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = $"{id} ({GameModeFilter.Display(MatchPlan.ModeOf(m))})\nClick to select it",
            };
            name.Pressed += () => Context.Selection.Set([ObjectRef.Actor(id)]);
            grid.AddChild(name);
        }

        for (int s = 0; s < MatchRuleSet.TeamSizes; s++)
        {
            grid.AddChild(new Label { Text = $"{s + 1}v{s + 1}", ThemeTypeVariation = "Dim" });
            foreach (Actor m in missions)
            {
                string id = m.Id;
                int size = s;
                int[] win = m.Match?.WinBySize() ?? new int[MatchRuleSet.TeamSizes];
                SpinBox box = PanelUi.Spin(grid, 0, 255, 1, win[s], $"{id} at {s + 1}v{s + 1}", EditorTheme.SpinCompactWidth, scrub: true);
                box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                box.ValueChanged += v => SetWin(id, size, (int)v);
                _winBoxes[(id, s)] = box;
            }
        }
    }

    /// <summary>How many leading characters every mission name shares (SMission28821, SMission28822...).</summary>
    private static int CommonPrefix(IReadOnlyList<string> ids)
    {
        if (ids.Count < 2)
        {
            return 0;
        }

        int n = ids.Min(i => i.Length);
        for (int k = 0; k < n; k++)
        {
            if (ids.Any(i => i[k] != ids[0][k]))
            {
                return k;
            }
        }

        return n;
    }

    /// <summary>A column title narrow enough for the grid: the part that tells the missions apart (at least three
    /// characters), after an ellipsis. The tooltip has the whole name.</summary>
    private static string Short(string id, int common)
    {
        const int Shown = 3;
        int from = Math.Min(common, Math.Max(0, id.Length - Shown));
        return id.Length <= 8 || from == 0 ? id : "…" + id[from..];
    }

    private void SetWin(string id, int size, int count)
    {
        if (Context.Project.FindActor(id) is not { } mission || mission.Match?.WinBySize()[size] == count)
        {
            return;
        }

        // One undo step per mission while its targets are being typed in.
        Context.Execute(ReplaceObjectsCommand.Edit(Context.Project, [ObjectRef.Actor(id)], $"Win targets of {id}", actor: a =>
        {
            ActorMatch m = a.Match ?? new ActorMatch(PlayerRange.Any);
            int[] win = m.WinBySize();
            win[size] = count;
            a.Match = (m with { Win = win }).OrNull();
        }, mergeKey: $"match:win:{id}"));
    }

    private void SelectAbsent()
    {
        if (_preview is { } preview)
        {
            // The same actors "Grey out absent" fades: with a mode, the other modes' objects too.
            HashSet<string> absent = MatchPlan.Absent(Context.Project, preview);
            Context.Selection.Set(Context.Project.Actors.Where(a => absent.Contains(a.Id)).Select(a => ObjectRef.Actor(a.Id)));
        }
    }

    private void List(MapProject p, string title, IReadOnlyList<string> ids, string tooltip)
    {
        if (ids.Count == 0)
        {
            return;
        }

        Label caption = PanelUi.Caption(_layout, $"{title} ({ids.Count})");
        caption.TooltipText = tooltip;
        caption.MouseFilter = MouseFilterEnum.Pass;
        foreach (string id in ids)
        {
            Pick(id, $"{id}  ·  {p.FindActor(id)?.ShortClass}", null);
        }
    }

    private void Pick(string id, string text, Severity? problem)
    {
        Button b = PanelUi.Button(_layout, text, $"Select {id}", () =>
        {
            ObjectRef[] refs = [ObjectRef.Actor(id)];
            Context.Selection.Set(refs);
            Context.Focus(refs);
        }, flat: true);
        b.Alignment = HorizontalAlignment.Left;
        b.ClipText = true;
        b.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        b.TooltipText = $"{text}\nSelect {id}";
        if (problem is { } severity)
        {
            b.Icon = PanelIcons.Severity(severity);
        }
    }

    private static string Describe(Hold hold) => hold.Kind switch
    {
        HoldKind.Hidden => "hidden",
        HoldKind.Normal => "works as built",
        _ => hold.Key == 0 ? "held closed (key 0)" : $"held at key {hold.Key}",
    };
}
