using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>
/// Gameplay actors against the brush geometry (design rule "actors in solid space, no floor"): starts and objectives
/// inside solid, starts with nothing to land on. Only for maps made of brushes alone: a baked world's geometry is not
/// in the brush list, so these rules stay quiet when the project has one.
/// </summary>
internal sealed class PlacementRules : MapRule
{
    public const string StartInSolid = "place.start-in-solid";
    public const string StartNoFloor = "place.start-no-floor";
    public const string ObjectiveInSolid = "place.objective-in-solid";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(StartInSolid, Severity.Error, "An enabled player start inside solid geometry (brush maps only)"),
        new(StartNoFloor, Severity.Warning, $"A player start with no floor within {Limits.SpyUnhurtDrop} below it (brush maps only)"),
        new(ObjectiveInSolid, Severity.Warning, "An objective (SObjectiveTrigger) inside solid geometry (brush maps only)"),
    ];

    public override void Run(CheckContext c)
    {
        if (c.Project.Baked is not null || c.Project.Brushes.Count == 0)
        {
            return;
        }

        SolidQuery solid = c.Solid;
        foreach (Actor s in StartRules.Starts(c).Where(s => c.Bool(s, "bEnabled", true)))
        {
            ObjectRef r = ObjectRef.Actor(s.Id);
            if (solid.IsSolid(s.Location))
            {
                c.Report(Severity.Error, StartInSolid, $"{s.Id} is inside solid geometry", s.Location, r);
            }
            else if (solid.DropToFloor(s.Location, Limits.SpyUnhurtDrop) is null)
            {
                c.Report(Severity.Warning, StartNoFloor, $"{s.Id} has no floor within {Limits.SpyUnhurtDrop} below it", s.Location, r);
            }
        }

        foreach (Actor o in c.Project.Actors.Where(a => c.IsA(a, "SBase.SObjectiveTrigger") && solid.IsSolid(a.Location)))
        {
            c.Report(Severity.Warning, ObjectiveInSolid, $"{o.Id} is inside solid geometry", o.Location, ObjectRef.Actor(o.Id));
        }
    }
}
