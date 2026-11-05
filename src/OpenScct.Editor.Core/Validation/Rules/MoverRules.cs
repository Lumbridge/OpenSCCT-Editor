using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>
/// Movers' own per-key timing (OpenSCCT's mover legs, SVMap 1.2 <c>sim/movers.json</c>): timing on an actor that is not
/// a mover or on keys it does not have, and for the original game, which runs every leg in one MoveTime.
/// </summary>
internal sealed class MoverRules : MapRule
{
    public const string Legs = "mover.legs";
    public const string NotExported = "mover.legs-not-exported";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(Legs, Severity.Warning, "Leg times or easing on an actor that is not a mover, or on keys the mover does not have"),
        new(NotExported, Severity.Warning, "The original game runs every leg of a mover in its one MoveTime and glide: per-key timing is not exported") { OriginalOnly = true },
    ];

    public override void Run(CheckContext c)
    {
        var timed = new List<Actor>();
        foreach (Actor a in c.Project.Actors)
        {
            if (a.Legs is not { } legs)
            {
                continue;
            }

            ObjectRef at = ObjectRef.Actor(a.Id);
            if (!MatchPlan.IsMover(a))
            {
                c.Report(Severity.Warning, Legs, $"{a.Id} is a {a.ShortClass}, not a mover: its leg times and easing do nothing", a.Location, at);
                continue;
            }

            int keys = MoverSetup.Read(a).Keys.Count;
            if (legs.Keys.Count > keys)
            {
                c.Report(Severity.Warning, Legs, $"{a.Id} has timing for key {legs.Keys.Count - 1} but only keys 0 to {keys - 1}: the extra keys are never reached",
                    a.Location, at);
            }

            timed.Add(a);
        }

        if (c.Original && timed.Count > 0)
        {
            foreach (Actor a in timed)
            {
                MoverSetup m = MoverSetup.Read(a);
                c.Report(Severity.Warning, NotExported,
                    $"{a.Id}: the original game runs every leg in {m.MoveTime:0.###} s {(m.Eased ? "eased" : "at constant speed")}; its own leg times and easing are left out",
                    a.Location, ObjectRef.Actor(a.Id));
            }
        }
    }
}
