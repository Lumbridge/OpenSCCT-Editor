using OpenScct.Core.Match;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>A top mission at one player count: its game mode, what it plays and asks for.</summary>
public sealed record MatchMissionView(string Id, string Mode, int Objectives, int Needed, int Random);

/// <summary>
/// What a match of <see cref="Players"/> in <see cref="Mode"/> (null: every mode) plays, as the server lays it out: the
/// top missions of that mode, the objectives and missions left out under them, the other gated actors left out, and
/// where gated movers are held.
/// </summary>
public sealed record MatchPreview(int Players, string? Mode, MatchSummary Summary, IReadOnlyList<MatchMissionView> Missions,
    IReadOnlyList<string> LeftOut, IReadOnlyList<string> Hidden, IReadOnlyDictionary<string, Hold> Movers)
{
    /// <summary>The team size: 3 for 3v3.</summary>
    public int TeamSize => MatchRuleSet.TeamSize(Players);

    /// <summary>True when the actor is not in this match (left out, hidden, or a mover held hidden).</summary>
    public bool IsAbsent(string id) =>
        LeftOut.Contains(id, StringComparer.OrdinalIgnoreCase) || Hidden.Contains(id, StringComparer.OrdinalIgnoreCase) ||
        (Movers.TryGetValue(id, out Hold h) && h.Kind == HoldKind.Hidden);
}

/// <summary>A run of team sizes (<see cref="From"/>v<see cref="From"/> to <see cref="To"/>v<see cref="To"/>) whose
/// matches, at full teams, play the same layout; <see cref="Preview"/> is the first of them.</summary>
public sealed record MatchSizeRow(int From, int To, MatchPreview Preview);

/// <summary>
/// The project's match-size rules (SVMap 1.2 <c>sim/match.json</c>) through OpenSCCT's own layout code
/// (<see cref="MatchLayout"/>), so the editor's preview and checks always agree with the server.
/// </summary>
public static class MatchPlan
{
    /// <summary>The rules and win targets the project's actors carry, in level order.</summary>
    public static MatchRuleSet RuleSet(MapProject p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var rules = new List<MatchRule>();
        var win = new List<WinTargets>();
        foreach (Actor a in p.Actors)
        {
            if (a.Match is not { } m)
            {
                continue;
            }

            if (m.HasRule)
            {
                rules.Add(m.Rule(a.Id));
            }

            if (m.HasWin)
            {
                win.Add(new WinTargets(a.Id, m.WinBySize()));
            }
        }

        return new MatchRuleSet(rules, win);
    }

    /// <summary>True when some actor has a rule or a win target.</summary>
    public static bool HasAny(MapProject p) => p.Actors.Any(a => a.Match is { IsEmpty: false });

    /// <summary>The project's missions as authored (SMission and subclasses): objectives, MinimumObjectives and RandomObjectives.</summary>
    public static List<MatchMission> Missions(MapProject p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var missions = new List<MatchMission>();
        foreach (Actor m in p.Actors.Where(IsMission))
        {
            var objectives = new List<string>();
            if (m.Props["Objectives"] is OpenScct.Content.Json.SvJsonArray list)
            {
                // A None entry keeps its place, as the game's list does.
                objectives.AddRange(list.Select(o => PropValues.ActorId(o) ?? ""));
            }

            missions.Add(new MatchMission(m.Id, objectives, (int)ClassDefaults.Number(m, "MinimumObjectives", 0),
                (int)ClassDefaults.Number(m, "RandomObjectives", 0)));
        }

        return missions;
    }

    public static bool IsMission(Actor a) => ClassDefaults.IsA(a, "SBase.SMission");

    public static bool IsObjective(Actor a) => ClassDefaults.IsA(a, "SBase.SObjective");

    public static bool IsMover(Actor a) => ClassDefaults.IsA(a, "Engine.Mover");

    /// <summary>
    /// True for a light, static mesh or other actor that is drawn or lit into the map (a render class or a subclass of
    /// one): its light and shadows are baked once for every player count, so it cannot be gated yet.
    /// </summary>
    public static bool IsRenderOnly(Actor a) => ActorRules.RenderClasses.Any(c => ClassDefaults.IsA(a, c));

    /// <summary>"6+ players, outside: hidden, win targets" for lists.</summary>
    public static string Describe(ActorMatch m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var parts = new List<string>();
        if (m.HasRule)
        {
            parts.Add(m.Players.Short());
        }

        if (m.Outside is { } outside)
        {
            parts.Add($"outside: {outside}");
        }

        if (m.Inside is { } inside)
        {
            parts.Add($"inside: {inside}");
        }

        if (m.HasWin)
        {
            parts.Add("win targets");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The game mode of a mission (<c>GameMode</c>, by name: Sabotage).</summary>
    public static string ModeOf(Actor mission)
    {
        int mode = (int)ClassDefaults.Number(mission, "GameMode", 0);
        return mode >= 0 && mode < MissionTree.GameModes.Count ? MissionTree.GameModes[mode].Replace("GM_", "") : $"mode {mode}";
    }

    /// <summary>The game modes the map's top missions play, in level order.</summary>
    public static List<string> Modes(MapProject p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return MatchLayout.Roots(Missions(p)).Select(id => ModeOf(p.FindActor(id)!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The layout of a match of <paramref name="players"/> (everyone in the match, bots included) in
    /// <paramref name="mode"/>, the only missions a round plays (null: every mode's).
    /// </summary>
    public static MatchPreview Preview(MapProject p, int players, string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        MatchRuleSet rules = RuleSet(p);
        List<MatchMission> missions = Missions(p);
        MatchOutcome outcome = MatchLayout.Apply(missions, rules, players);
        var views = new List<MatchMissionView>();
        var shown = new List<MatchMission>();
        foreach (string root in MatchLayout.Roots(missions))
        {
            string rootMode = ModeOf(p.FindActor(root)!);
            if (mode is not null && !string.Equals(rootMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            MatchMission one = outcome.Mission(root)!;
            List<MatchMission> tree = [.. missions.Where(m => Reaches(missions, root, m.Id))];
            shown.AddRange(tree);
            MatchSummary s = MatchLayout.Summarize(tree, rules, players);
            views.Add(new MatchMissionView(root, rootMode, s.Objectives, MatchLayout.Needs(one), one.RandomObjectives));
        }

        // Another mode's objectives are not in this round at all, so they are not "left out" of it.
        IReadOnlyList<string> leftOut = mode is null
            ? outcome.LeftOut
            : [.. outcome.LeftOut.Where(id => views.Any(v => Reaches(missions, v.Id, id)))];

        var hidden = new List<string>();
        var movers = new Dictionary<string, Hold>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors)
        {
            if (a.Match is not { HasRule: true } m)
            {
                continue;
            }

            // As the round start does (OpenScct.Game.Modes.MatchSizeRules): movers are held, objectives outside their
            // range leave the match, anything else not "normal" is hidden.
            MatchRule rule = m.Rule(a.Id);
            bool inside = rule.Players.Keeps(players);
            if (IsMover(a))
            {
                movers[a.Id] = inside ? rule.InsideFor() : rule.OutsideFor(mover: true);
            }
            else if (!inside && IsObjective(a))
            {
                if (!outcome.IsLeftOut(a.Id))
                {
                    hidden.Add(a.Id);
                }
            }
            else if ((inside ? rule.InsideFor() : rule.OutsideFor(mover: false)).Kind != HoldKind.Normal)
            {
                hidden.Add(a.Id);
            }
        }

        return new MatchPreview(players, mode, MatchLayout.Summarize(mode is null ? missions : shown, rules, players), views, leftOut, hidden, movers);
    }

    /// <summary>
    /// The actors a match of the preview's size and mode leaves out of play: what its layout leaves out or hides, and
    /// with a mode chosen, the missions, objectives and triggers only other modes use (a round plays one mode).
    /// </summary>
    public static HashSet<string> Absent(MapProject p, MatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(preview);
        var absent = new HashSet<string>(p.Actors.Where(a => preview.IsAbsent(a.Id)).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        int mode = preview.Mode is { } name
            ? MissionTree.GameModes.ToList().FindIndex(m => string.Equals(m.Replace("GM_", ""), name, StringComparison.OrdinalIgnoreCase))
            : -1;
        if (mode > 0)
        {
            GameModes modes = GameModes.Build(p, MissionTree.Build(p, ClassDefaults.Catalog));
            absent.UnionWith(p.Actors.Where(a => !modes.InMode(a.Id, mode)).Select(a => a.Id));
        }

        return absent;
    }

    /// <summary>
    /// What each team size plays in <paramref name="mode"/> (null: every mode), 1v1 to 12v12 at full teams, with the
    /// sizes that play the same layout joined into one row.
    /// </summary>
    public static List<MatchSizeRow> BySize(MapProject p, string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        var rows = new List<MatchSizeRow>();
        for (int size = 1; size <= MatchRuleSet.TeamSizes; size++)
        {
            MatchPreview at = Preview(p, size * 2, mode);
            if (rows.Count > 0 && SameLayout(rows[^1].Preview, at))
            {
                rows[^1] = rows[^1] with { To = size };
            }
            else
            {
                rows.Add(new MatchSizeRow(size, size, at));
            }
        }

        return rows;
    }

    private static bool SameLayout(MatchPreview a, MatchPreview b) =>
        a.Missions.SequenceEqual(b.Missions) &&
        a.LeftOut.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.LeftOut) &&
        a.Hidden.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Hidden) &&
        a.Movers.Count == b.Movers.Count && a.Movers.All(m => b.Movers.TryGetValue(m.Key, out Hold h) && h == m.Value);

    /// <summary>Runs of player counts where a top mission has nothing left (Map Check errors).</summary>
    public static List<string> Problems(MapProject p) => MatchLayout.Problems(Missions(p), RuleSet(p));

    /// <summary>Team sizes where a win target asks for more than the match can give (Map Check warnings).</summary>
    public static List<string> TargetWarnings(MapProject p) => MatchLayout.TargetWarnings(Missions(p), RuleSet(p));

    /// <summary>The player counts at which the layout changes: the starts of the ranges that differ (1 always first).</summary>
    public static List<int> Layouts(MapProject p)
    {
        var counts = new SortedSet<int> { 1 };
        foreach (Actor a in p.Actors)
        {
            if (a.Match is { HasRule: true } m)
            {
                if (m.Players.Min > 1)
                {
                    counts.Add(m.Players.Min);
                }

                if (m.Players.Max is > 0 and < MatchRuleSet.MostPlayers)
                {
                    counts.Add(m.Players.Max + 1);
                }
            }

            if (a.Match is { HasWin: true } w)
            {
                int[] sizes = w.WinBySize();
                for (int s = 1; s < sizes.Length; s++)
                {
                    if (sizes[s] != sizes[s - 1])
                    {
                        counts.Add(s * 2 + 1);
                    }
                }
            }
        }

        return [.. counts];
    }

    // The mission is the root or below it (guarded against loops).
    private static bool Reaches(List<MatchMission> missions, string root, string id)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            string at = stack.Pop();
            if (string.Equals(at, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (seen.Add(at) && missions.Find(m => string.Equals(m.Id, at, StringComparison.OrdinalIgnoreCase)) is { } m)
            {
                foreach (string child in m.Objectives.Where(c => c.Length > 0))
                {
                    stack.Push(child);
                }
            }
        }

        return false;
    }
}
