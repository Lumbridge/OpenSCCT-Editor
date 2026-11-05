using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>
/// Player-count rules, win targets and outfits (SVMap 1.2). For the original game: the export does not carry them yet
/// (one warning each), and what RE+'s SMatchSizeRules and SCharacterOutfits could not express even once it does.
/// </summary>
internal sealed class MatchRules : MapRule
{
    public const string Problems = "match.problems";
    public const string Target = "match.target";
    public const string Range = "match.range";
    public const string RenderOnly = "match.render-only";
    public const string Conveyor = "match.conveyor";
    public const string Win = "match.win";
    public const string Alarm = "match.alarm";
    public const string Export = "match.export";
    public const string NotExported = "match.not-exported";
    public const string OutfitFile = "outfit.file";
    public const string OutfitSkeleton = "outfit.skeleton";
    public const string OutfitPolicy = "outfit.cosmetics";
    public const string OutfitSlots = "outfit.slots";
    public const string OutfitExport = "outfit.export";
    public const string OutfitNotExported = "outfit.not-exported";

    /// <summary>RE+'s SMatchSizeRules: 16 players, team sizes 1 to 8, 64 rows, 16 missions.</summary>
    private const int ExportPlayers = 16;
    private const int ExportTeamSizes = 8;
    private const int ExportRows = 64;
    private const int ExportMissions = 16;

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(Problems, Severity.Error, "At some player counts a top mission has no objectives left, so it cannot be won"),
        new(Target, Severity.Warning, "A win target asks for more objectives than the match has at that team size"),
        new(Range, Severity.Error, "A player range or state that cannot work (min above max, a key state on a non-mover, a key past the mover's last)"),
        new(RenderOnly, Severity.Error, "A light, static mesh or other render-only actor has a player range: it is baked into the lighting for every count"),
        new(Conveyor, Severity.Error, "A conveyor has a player range: its belt is part of the world, so it draws, collides and carries at every count"),
        new(Win, Severity.Warning, "Win targets on an actor that is not a mission"),
        new(Alarm, Severity.Error, "A gated mover is on an alarm's MoversToLock: in RE+ the alarm unlocks it and it reopens") { OriginalOnly = true },
        new(NotExported, Severity.Info, "The map has no RE+ player-count actor: the export for the original editor makes one; a plain T3D export carries none") { OriginalOnly = true },
        new(Export, Severity.Warning, "A player-count setting RE+'s SMatchSizeRules cannot express") { OriginalOnly = true },
        new(OutfitFile, Severity.Error, "An outfit texture or model file is missing or of the wrong type"),
        new(OutfitSkeleton, Severity.Error, "An outfit model lacks bones its team's animations drive"),
        new(OutfitPolicy, Severity.Warning, "Players' own cosmetics on an outfit with a model, where only texture outfits allow them"),
        new(OutfitSlots, Severity.Warning, "Body or head textures on a team whose own model does not draw the stock materials they replace"),
        new(OutfitNotExported, Severity.Info, "The map has no RE+ outfit actor: the export for the original editor makes one; a plain T3D export carries none") { OriginalOnly = true },
        new(OutfitExport, Severity.Warning, "An outfit setting RE+'s SCharacterOutfits cannot express") { OriginalOnly = true },
    ];

    public override void Run(CheckContext c)
    {
        MapProject p = c.Project;
        if (MatchPlan.HasAny(p))
        {
            CheckRules(c);
            foreach (string problem in MatchPlan.Problems(p))
            {
                c.Report(Severity.Error, Problems, problem, null);
            }

            foreach (string warning in MatchPlan.TargetWarnings(p))
            {
                c.Report(Severity.Warning, Target, warning, null);
            }

            if (c.Original)
            {
                // The T3D export writes them into the RE+ actor an imported map brought (LiftedWriteBack); for a map without
                // one the export for the original editor makes one, a plain T3D export carries none.
                if (!p.Actors.Any(a => BakedContent.LiftedData(a) == "match"))
                {
                    int gated = p.Actors.Count(a => a.Match is { IsEmpty: false });
                    c.Report(Severity.Info, NotExported,
                        $"the map has no RE+ player-count actor (SMatchSizeRules): the export for the original editor makes one for the {gated} " +
                        $"actor{(gated == 1 ? "" : "s")} with player counts (in <Map>-MapClasses.t3d); a plain T3D export carries none, and the original game then " +
                        "has them in every match", null);
                }

                CheckExport(c);
            }
        }

        if (!p.Outfits.IsEmpty)
        {
            CheckOutfits(c);
        }
    }

    private static void CheckRules(CheckContext c)
    {
        foreach (Actor a in c.Project.Actors)
        {
            if (a.Match is not { } m)
            {
                continue;
            }

            ObjectRef at = ObjectRef.Actor(a.Id);
            if (m.HasWin && !MatchPlan.IsMission(a))
            {
                c.Report(Severity.Warning, Win, $"{a.Id} has win targets but is not a mission; they are left out", a.Location, at);
            }

            if (!m.HasRule)
            {
                continue;
            }

            if (m.Players.Problem() is { } problem)
            {
                c.Report(Severity.Error, Range, $"{a.Id}: {problem}", a.Location, at);
            }

            if (MatchPlan.IsRenderOnly(a))
            {
                c.Report(Severity.Error, RenderOnly,
                    $"{a.Id} ({a.Class}) has a player range, but it is drawn and lit into the map for every player count; gate a mover or another gameplay actor instead",
                    a.Location, at);
            }

            if (ConveyorDesign.Is(a))
            {
                c.Report(Severity.Error, Conveyor,
                    $"{a.Id} is a conveyor with a player range, but its belt is part of the world and draws, collides and carries at every player count; " +
                    "gate the trigger or mover that switches it instead",
                    a.Location, at);
            }

            bool mover = MatchPlan.IsMover(a);
            int keys = Math.Max(c.Int(a, "NumKeys"), 1);
            foreach ((string side, Hold? hold) in new[] { ("outside", m.Outside), ("inside", m.Inside) })
            {
                if (hold is { Kind: HoldKind.Key, Key: int key })
                {
                    if (!mover)
                    {
                        c.Report(Severity.Error, Range, $"{a.Id}: the {side} state names key {key}, but only movers have keys", a.Location, at);
                    }
                    else if (key >= keys)
                    {
                        c.Report(Severity.Error, Range, $"{a.Id}: the {side} state names key {key}; the mover has {keys} key{(keys == 1 ? "" : "s")}", a.Location, at);
                    }
                }
            }

            if (mover && c.Original)
            {
                foreach (Actor alarm in c.Project.Actors.Where(x => c.IsA(x, "SBase.SAlarm") && LocksMover(x, a.Id)))
                {
                    c.Report(Severity.Error, Alarm,
                        $"{a.Id} is gated by player count and on {alarm.Id}'s MoversToLock: in RE+ the alarm unlocks it and it reopens whenever a merc walks into the alarm zone",
                        a.Location, at, ObjectRef.Actor(alarm.Id));
                }
            }
        }
    }

    /// <summary>What RE+'s SMatchSizeRules rows could not hold (warnings) and its hard caps (errors).</summary>
    private static void CheckExport(CheckContext c)
    {
        int rows = 0;
        foreach (Actor a in c.Project.Actors)
        {
            if (a.Match is not { } m)
            {
                continue;
            }

            ObjectRef at = ObjectRef.Actor(a.Id);
            if (m.HasRule)
            {
                rows++;
                bool mover = MatchPlan.IsMover(a);
                if (!mover && !MatchPlan.IsObjective(a) && !MatchPlan.IsMission(a))
                {
                    c.Report(Severity.Warning, Export, $"{a.Id}: RE+ gates only objectives, missions and movers; there it would be in every match", a.Location, at);
                }
                else if (mover && !ExportableMover(m, c.Int(a, "NumKeys")))
                {
                    c.Report(Severity.Warning, Export, $"{a.Id}: RE+ movers are closed (key 0) outside the range and normal or open inside; RE+ cannot hold these states",
                        a.Location, at);
                }
                else if (!mover && (m.Outside is { Kind: not HoldKind.Hidden } || m.Inside is { Kind: not HoldKind.Normal }))
                {
                    c.Report(Severity.Warning, Export, $"{a.Id}: RE+ removes an objective outside its range and keeps it inside; it has no other states",
                        a.Location, at);
                }

                if (m.Players.Min > ExportPlayers || m.Players.Max > ExportPlayers)
                {
                    c.Report(Severity.Warning, Export, $"{a.Id}: RE+ counts at most {ExportPlayers} players; the range {m.Players.Short()} goes past that",
                        a.Location, at);
                }
            }

            if (m.HasWin && MatchPlan.IsMission(a))
            {
                rows++;
                if (m.WinBySize().Skip(ExportTeamSizes).Any(t => t > 0))
                {
                    c.Report(Severity.Warning, Export, $"{a.Id}: RE+ has win targets up to {ExportTeamSizes}v{ExportTeamSizes}; it has no targets for 9v9 to 12v12",
                        a.Location, at);
                }
            }
        }

        if (rows > ExportRows)
        {
            c.Report(Severity.Error, Export, $"{rows} player-count rows; RE+'s SMatchSizeRules holds {ExportRows}", null);
        }

        int missions = c.Project.Actors.Count(a => a.Match is { HasWin: true } && MatchPlan.IsMission(a));
        if (missions > ExportMissions)
        {
            c.Report(Severity.Error, Export, $"{missions} missions with win targets; RE+'s SMatchSizeRules holds {ExportMissions}", null);
        }
    }

    /// <summary>True when RE+'s rules can hold a mover's states: RE+ closes a mover at key 0 outside the range, and inside
    /// leaves it alone or opens it (a leg to key 1 that runs on to the last key, key 0 on a one-key mover), as the
    /// converter reads it.</summary>
    public static bool ExportableMover(ActorMatch m, int keys) =>
        m.Outside is null or { Kind: HoldKind.Key, Key: 0 } &&
        (m.Inside is null or { Kind: HoldKind.Normal } || m.Inside is { Kind: HoldKind.Key } open && open.Key == Math.Max(keys, 1) - 1);

    // MoversToLock entries are structs: {MoverToLock: the mover, ActionBeforeLock}.
    private static bool LocksMover(Actor alarm, string mover) =>
        alarm.Props["MoversToLock"] is SvJsonArray list &&
        list.Any(e => string.Equals(PropValues.ActorId((e as SvJsonObject)?["MoverToLock"]), mover, StringComparison.OrdinalIgnoreCase));

    private static void CheckOutfits(CheckContext c)
    {
        MapOutfits outfits = c.Project.Outfits;
        foreach (OutfitProblem problem in ProjectOutfits.Problems(outfits, c.Options.ProjectFolder, EditorServices.ContentRoot))
        {
            c.Report(problem.Error ? Severity.Error : Severity.Warning, problem.Code, problem.Message, null);
        }

        if (!c.Original)
        {
            return;
        }

        if (!c.Project.Actors.Any(a => BakedContent.LiftedData(a) == "outfits"))
        {
            c.Report(Severity.Info, OutfitNotExported,
                "the map has no RE+ outfit actor (SCharacterOutfits): the export for the original editor makes one (in <Map>-MapClasses.t3d); a plain T3D "
                + "export carries none, and in the original game both teams then wear the stock look", null);
        }

        foreach (bool spy in new[] { true, false })
        {
            if (outfits.For(spy) is not { IsEmpty: false } team)
            {
                continue;
            }

            string name = ProjectOutfits.TeamName(spy);
            if (team.Cosmetics != OutfitCosmetics.Map)
            {
                c.Report(Severity.Warning, OutfitExport, $"the {name} outfit's cosmetics policy ({OutfitsDoc.PolicyName(team.Cosmetics)}) is OpenSCCT's own; RE+ always uses the map's", null);
            }

            if (team.Model is { Glb: { } glb })
            {
                c.Report(Severity.Warning, OutfitExport, $"the {name} model {Path.GetFileName(glb)} is a glb; RE+ needs it as a skeletal mesh package, which the export does not write", null);
            }
        }
    }
}
