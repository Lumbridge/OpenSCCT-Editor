using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>Player starts: one per team, not on top of each other.</summary>
internal sealed class StartRules : MapRule
{
    public const string None = "start.none";
    public const string TeamMissing = "start.team-missing";
    public const string Crowded = "start.crowded";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(None, Severity.Error, "The map has no player start"),
        new(TeamMissing, Severity.Error, "No enabled player start for the spies (team 0) or the mercs (team 1)"),
        new(Crowded, Severity.Warning, $"Two enabled starts of one spawn set (team or merc deathmatch) closer than a body width ({Limits.BodyRadius * 2})"),
    ];

    public override void Run(CheckContext c)
    {
        List<Actor> starts = Starts(c).ToList();
        if (starts.Count == 0)
        {
            c.Report(Severity.Error, None, "The map has no player start (Engine.PlayerStart)", null);
            return;
        }

        foreach ((int team, string name) in new[] { (0, "spies"), (1, "mercs") })
        {
            if (!starts.Any(s => c.Int(s, "TeamNumber") == team && c.Bool(s, "bEnabled", true) && !c.Bool(s, "bMercDeathMatchStart")))
            {
                c.Report(Severity.Error, TeamMissing, $"No enabled player start for the {name} (TeamNumber {team})", null);
            }
        }

        // Stock maps stack a team start on a merc deathmatch start, or on a start enabled later: those never spawn together.
        List<Actor> live = starts.Where(s => c.Bool(s, "bEnabled", true)).ToList();
        for (int i = 0; i < live.Count; i++)
        {
            for (int j = i + 1; j < live.Count; j++)
            {
                if (c.Bool(live[i], "bMercDeathMatchStart") != c.Bool(live[j], "bMercDeathMatchStart"))
                {
                    continue;
                }

                Vec3 d = live[j].Location - live[i].Location;
                if (Math.Sqrt(d.X * d.X + d.Y * d.Y) < c.Design.BodyRadius * 2 && Math.Abs(d.Z) < c.Design.StandHeight)
                {
                    c.Report(Severity.Warning, Crowded, $"{live[i].Id} and {live[j].Id} are {Math.Sqrt(d.X * d.X + d.Y * d.Y):0} apart",
                        live[i].Location, ObjectRef.Actor(live[i].Id), ObjectRef.Actor(live[j].Id));
                }
            }
        }
    }

    public static IEnumerable<Actor> Starts(CheckContext c) => c.Project.Actors.Where(a => c.IsA(a, "Engine.PlayerStart"));

    /// <summary>Enabled team starts (merc deathmatch starts left out).</summary>
    public static IEnumerable<Actor> TeamStarts(CheckContext c, int team) =>
        Starts(c).Where(s => c.Int(s, "TeamNumber") == team && c.Bool(s, "bEnabled", true) && !c.Bool(s, "bMercDeathMatchStart"));
}

/// <summary>RE+ Check Design objective-flow rules: numbers for the designer, never errors.</summary>
internal sealed class FlowRules : MapRule
{
    public const string NoObjective = "flow.no-objective";
    public const string SpyStartNearObjective = "flow.spy-start-near-objective";
    public const string TeamStartsNear = "flow.team-starts-near";
    public const string ObjectiveFarFromMercs = "flow.objective-far-from-mercs";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(NoObjective, Severity.Info, "The map has no objective (SObjectiveTrigger): only deathmatch modes can be played"),
        new(SpyStartNearObjective, Severity.Info, $"A spy start within {Limits.SpyStartNearObjective} of an objective"),
        new(TeamStartsNear, Severity.Info, $"A spy start within {Limits.TeamStartsNear} of a merc start"),
        new(ObjectiveFarFromMercs, Severity.Info, $"An objective more than {Limits.ObjectiveFarFromMercs} from every merc start"),
    ];

    public override void Run(CheckContext c)
    {
        List<Actor> objectives = c.Project.Actors.Where(a => c.IsA(a, "SBase.SObjectiveTrigger")).ToList();
        List<Actor> spies = StartRules.TeamStarts(c, 0).ToList();
        List<Actor> mercs = StartRules.TeamStarts(c, 1).ToList();
        if (objectives.Count == 0)
        {
            c.Report(Severity.Info, NoObjective, "No objective (SObjectiveTrigger) in the map", null);
        }

        foreach (Actor s in spies)
        {
            if (Nearest(s, objectives) is ({ } o, double d) && d < Limits.SpyStartNearObjective)
            {
                c.Report(Severity.Info, SpyStartNearObjective, $"Spy start {s.Id} is {d:0} from objective {o.Id}", s.Location,
                    ObjectRef.Actor(s.Id), ObjectRef.Actor(o.Id));
            }

            if (Nearest(s, mercs) is ({ } m, double dm) && dm < Limits.TeamStartsNear)
            {
                c.Report(Severity.Info, TeamStartsNear, $"Spy start {s.Id} is {dm:0} from merc start {m.Id}", s.Location,
                    ObjectRef.Actor(s.Id), ObjectRef.Actor(m.Id));
            }
        }

        if (mercs.Count == 0)
        {
            return;
        }

        foreach (Actor o in objectives)
        {
            if (Nearest(o, mercs) is ({ } m, double d) && d > Limits.ObjectiveFarFromMercs)
            {
                c.Report(Severity.Info, ObjectiveFarFromMercs, $"Objective {o.Id} is {d:0} from the nearest merc start ({m.Id})", o.Location,
                    ObjectRef.Actor(o.Id));
            }
        }
    }

    private static (Actor? Actor, double Distance) Nearest(Actor from, List<Actor> to)
    {
        Actor? best = null;
        double bestD = double.MaxValue;
        foreach (Actor a in to)
        {
            double d = (a.Location - from.Location).Length;
            if (d < bestD)
            {
                best = a;
                bestD = d;
            }
        }

        return (best, bestD);
    }
}
