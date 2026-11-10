using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>
/// Map Check rules for GE shapes and for GEs against the level geometry: level and long enough ledges, zip lines with
/// a downhill end and a landing, upright poles, ledges flush on their edge with room to hang, ledge ends that meet at
/// corners, and ladder and pipe tops about 40 over the floor they lead to.
/// </summary>
internal sealed class GeGameplayRules : MapRule
{
    public const string CornerGap = "ge.lg-corner-gap";
    public const string NotFlush = "ge.lg-not-flush";
    public const string NoEdge = "ge.lg-no-edge";
    public const string NoHang = "ge.lg-no-hang";
    public const string LedgeLow = "ge.lg-low";
    public const string ZipLanding = "ge.zl-no-landing";
    public const string ClimbTop = "ge.climb-top";
    public const string Appearance = "ge.appearance";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(GeShapes.LedgeNotLevel, Severity.Warning, "A ledge grab whose ends are not at one height"),
        new(GeShapes.LedgeTooShort, Severity.Warning, $"A ledge grab shorter than {GeShapes.LedgeGrabbableLength}: the hands must stay 24.5 from both ends"),
        new(GeShapes.ZipLevel, Severity.Warning, "A level zip line: it has no downhill end to slide to"),
        new(GeShapes.ZipSteep, Severity.Info, $"A zip line steeper than {GeShapes.ZipSteepDegrees} degrees"),
        new(GeShapes.PoleNotVertical, Severity.Warning, "A pole that leans off vertical"),
        new(GeShapes.HandOverHandShort, Severity.Warning, $"A hand-over-hand pipe shorter than {GeShapes.HandOverHandMinLength}"),
        new(CornerGap, Severity.Warning, $"Two ledge ends within {GeEdits.CornerReach} that do not meet (the shimmy only carries round a corner when the ends are within 2)"),
        new(NotFlush, Severity.Warning, "A ledge grab off its edge or above or below the top it hangs from (ose ge flush fixes it)"),
        new(NoEdge, Severity.Info, "A ledge grab with no edge of the level geometry under it (it may sit on a mover or a mesh without collision)"),
        new(NoHang, Severity.Warning, "A ledge grab with no room to hang below it (the engine's two clearance boxes are blocked)"),
        new(LedgeLow, Severity.Warning, $"A ledge grab over a drop under {new LedgeOptions().MinDrop}: a step the walk climbs, not a ledge to hang from"),
        new(ZipLanding, Severity.Warning, $"A zip line with no floor a safe drop under its low end (feet hang {Limits.ZipLineHang} under the line)"),
        new(ClimbTop, Severity.Info, $"A ladder or pipe whose top is not about {Limits.LadderTopOverFloor} over the floor it leads onto"),
        new(Appearance, Severity.Error, "A GE's generated geometry has invalid dimensions or exceeds the repeat limit"),
    ];

    public override void Run(CheckContext c)
    {
        bool invalidAppearance = false;
        foreach (Ge ge in c.Project.Ges.Where(g => g.Appearance is not null))
        {
            try
            {
                GeGeometry.Validate(ge);
            }
            catch (ArgumentException ex)
            {
                invalidAppearance = true;
                c.Report(Severity.Error, Appearance, $"{ge.Id}: {ex.Message}", ge.Points.FirstOrDefault(), ObjectRef.Ge(ge.Id));
            }
        }

        List<Ge> ges = c.Project.Ges.Where(g => GeKinds.All.Contains(g.Kind) && g.Points.Count >= (GeKinds.HasThirdPoint(g.Kind) ? 3 : 2)).ToList();
        List<Ge> ledgeList = ges.Where(g => g.Kind == GeKinds.LedgeGrab).ToList();
        foreach (Ge g in ges)
        {
            // A short ledge joined at both ends is a corner piece the shimmy carries across, not one to grab.
            bool cornerPiece = g.Kind == GeKinds.LedgeGrab && g.Points.Take(2).All(p =>
                ledgeList.Any(o => !ReferenceEquals(o, g) && o.Points.Take(2).Any(q => GeShapes.Connected(p, q))));
            foreach (GeProblem problem in GeShapes.Problems(g.Kind, g.Points).Where(p => !(cornerPiece && p.Code == GeShapes.LedgeTooShort)))
            {
                c.Report(problem.Severity, problem.Code, $"{g.Id}: {problem.Message}", g.Points[0], ObjectRef.Ge(g.Id));
            }
        }

        CornerGaps(c, ledgeList);
        if (invalidAppearance || !new[] { NotFlush, NoEdge, NoHang, LedgeLow, ZipLanding, ClimbTop }.Any(c.Wants) || ges.Count == 0)
        {
            return;
        }

        TraversalWorld world = TraversalWorld.Build(c.Project);
        if (!world.HasGeometry)
        {
            return;
        }

        var options = new LedgeOptions();
        foreach (Ge g in ges)
        {
            switch (g.Kind)
            {
                case GeKinds.LedgeGrab:
                    Ledge(c, world, g, options);
                    break;
                case GeKinds.ZipLine:
                    ZipEnd(c, world, g);
                    break;
                case GeKinds.Ladder or GeKinds.Pipe:
                    Climb(c, world, g);
                    break;
            }
        }
    }

    /// <summary>Free ledge ends (connected to no other ledge end) close to another ledge's free end at a corner they
    /// could share: the gaps <see cref="GeEdits.JoinCorners"/> closes.</summary>
    private static void CornerGaps(CheckContext c, List<Ge> ledges)
    {
        var ends = ledges.SelectMany(g => g.Points.Take(2).Select(p => (Ge: g, At: p))).ToList();
        var free = ends.Where(e => !ends.Any(o => !ReferenceEquals(o.Ge, e.Ge) && GeShapes.Connected(o.At, e.At))).ToList();
        var reported = new HashSet<(string, string)>();
        for (int i = 0; i < free.Count; i++)
        {
            for (int j = i + 1; j < free.Count; j++)
            {
                (Ge ga, Vec3 a) = free[i];
                (Ge gb, Vec3 b) = free[j];
                Vec3 d = b - a;
                if (!ReferenceEquals(ga, gb) && d.Length <= GeEdits.CornerReach && Math.Abs(d.Z) <= 4 &&
                    LedgeDetector.Corner(LipOf(ga), LipOf(gb), a, b, GeEdits.CornerReach) is not null && reported.Add((ga.Id, gb.Id)))
                {
                    c.Report(Severity.Warning, CornerGap, $"{ga.Id} and {gb.Id} end {d.Length:0.#} apart (ose ge join closes it)",
                        a, ObjectRef.Ge(ga.Id), ObjectRef.Ge(gb.Id));
                }
            }
        }
    }

    private static Lip LipOf(Ge g) => new(g.Points[0], g.Points[1], Vec3.Zero, 0);


    private static void Ledge(CheckContext c, TraversalWorld w, Ge g, LedgeOptions o)
    {
        ObjectRef r = ObjectRef.Ge(g.Id);
        Vec3 a = g.Points[0], b = g.Points[1];
        if (LedgeProbe.Fit(w, a, b, o) is not { } fit)
        {
            c.Report(Severity.Info, NoEdge, $"{g.Id}: no ledge edge within 32 of it", a, r);
            return;
        }

        double dz = fit.TopZ - (a.Z + b.Z) / 2;
        if (Math.Abs(fit.Across) > 1 || Math.Abs(dz) > 1)
        {
            c.Report(Severity.Warning, NotFlush, $"{g.Id}: {Math.Abs(fit.Across):0.#} off the edge, {dz:+0.#;-0.#;0} to the top", a, r);
        }

        Vec3 mid = (a + b) / 2 + fit.Offset;
        mid = mid with { Z = fit.TopZ };
        switch (LedgeProbe.Evaluate(w, mid, fit.HangSide, o))
        {
            case (LipProblem.NoHang, _):
                c.Report(Severity.Warning, NoHang, $"{g.Id}: no room to hang below its middle", mid, r);
                break;
            case (LipProblem.Step, double drop):
                c.Report(Severity.Warning, LedgeLow, $"{g.Id}: only {drop:0} drop beside it (a step, not a ledge)", mid, r);
                break;
        }
    }

    private static void ZipEnd(CheckContext c, TraversalWorld w, Ge g)
    {
        Vec3 low = g.Points[0].Z <= g.Points[1].Z ? g.Points[0] : g.Points[1];
        double reach = Limits.ZipLineHang + Limits.SpyUnhurtDrop;
        if (w.DropBelow(low - Vec3.UnitZ, reach) is null)
        {
            c.Report(Severity.Warning, ZipLanding, $"{g.Id}: no floor within {reach:0} under its low end", low, ObjectRef.Ge(g.Id));
        }
    }

    private static void Climb(CheckContext c, TraversalWorld w, Ge g)
    {
        Vec3 bottom = g.Points[0], top = g.Points[1];
        Vec3 n = new Vec3(g.Points[2].X - bottom.X, g.Points[2].Y - bottom.Y, 0).Normalized();
        if (n == Vec3.Zero)
        {
            return;
        }

        // The climber leaves the top forwards, away from the facing point, onto the floor there.
        Vec3 probe = top - n * (Limits.BodyRadius + 13) + Vec3.UnitZ * 96;
        if (w.Trace(probe, probe - Vec3.UnitZ * 256) is not { } floor)
        {
            return;
        }

        double over = top.Z - floor.Point.Z;
        if (Math.Abs(over - Limits.LadderTopOverFloor) > 24)
        {
            c.Report(Severity.Info, ClimbTop, $"{g.Id}: top is {over:0} over the floor it leads onto (about {Limits.LadderTopOverFloor} works)",
                top, ObjectRef.Ge(g.Id));
        }
    }
}

/// <summary>Map Check rules over the event graph and the mission tree.</summary>
internal sealed class WiringRules : MapRule
{
    public const string Ring = "event.ring";
    public const string ObjectiveNoTrigger = "mission.objective-no-trigger";
    public const string MinimumTooHigh = "mission.minimum-too-high";
    public const string ObjectiveUnlisted = "mission.objective-unlisted";
    public const string NoMission = "mission.none";
    public const string ZoneGameMode = "mission.zone-game-mode";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(Ring, Severity.Warning, "Actors that fire each other round in a loop (an event ring)"),
        new(ObjectiveNoTrigger, Severity.Warning, "An objective (SObjective) with no trigger: it can never be completed"),
        new(MinimumTooHigh, Severity.Error, "A mission needing more objectives than it has"),
        new(ObjectiveUnlisted, Severity.Warning, "An objective (SObjective) that no mission lists"),
        new(NoMission, Severity.Info, "Objective triggers but no mission (SMission) to win with them"),
        new(ZoneGameMode, Severity.Warning, "A nested zone with its own game mode can start before its parent unlocks it"),
    ];

    public override void Run(CheckContext c)
    {
        if (c.Wants(Ring))
        {
            foreach (List<string> ring in GameplayGraph.Build(c.Project).Rings())
            {
                Actor? first = c.Project.FindActor(ring[0]);
                c.Report(Severity.Warning, Ring, $"event ring: {string.Join(" -> ", ring)}", first?.Location, [.. ring.Select(ObjectRef.Actor)]);
            }
        }

        MissionTree tree = MissionTree.Build(c.Project, c.Classes);
        var nested = tree.Missions.SelectMany(m => m.Objectives.Select(o => o.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (MissionNode m in tree.Missions)
        {
            Vec3? at = c.Project.FindActor(m.Id)?.Location;
            if (m.GameMode != 0 && nested.Contains(m.Id))
                c.Report(Severity.Warning, ZoneGameMode, $"{m.Id} is nested inside a mission but has its own game mode. Set Mode to Zone (parent) in Missions so its parent controls activation.", at, ObjectRef.Actor(m.Id));
            foreach (ObjectiveNode o in m.Objectives.Where(o => o.Triggers.Count == 0
                && c.Project.FindActor(o.Id) is { } actor && !MatchPlan.IsMission(actor)))
            {
                c.Report(Severity.Warning, ObjectiveNoTrigger, $"{o.Id} ({m.Id}) has no trigger", c.Project.FindActor(o.Id)?.Location,
                    ObjectRef.Actor(o.Id));
            }

            if ((!m.Chained || m.GameMode == GameModes.DiskHunt) && m.MinimumObjectives > m.Completers && m.Completers > 0)
            {
                c.Report(Severity.Error, MinimumTooHigh, $"{m.Id} needs {m.MinimumObjectives} objectives but has {m.Completers}", at, ObjectRef.Actor(m.Id));
            }
        }

        foreach (string id in tree.Unlisted)
        {
            c.Report(Severity.Warning, ObjectiveUnlisted, $"{id} is in no mission", c.Project.FindActor(id)?.Location, ObjectRef.Actor(id));
        }

        if (tree.Missions.Count == 0 && c.Project.Actors.Any(a => c.IsA(a, "SBase.SObjectiveTrigger")))
        {
            c.Report(Severity.Info, NoMission, "objective triggers but no SMission", null);
        }
    }
}
