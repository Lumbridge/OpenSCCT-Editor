using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

internal sealed class DoorRules : MapRule
{
    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new("door.design", Severity.Error, "Invalid door dimensions, movement or actor class"),
        new("door.bakedCut", Severity.Error, "A door would cut retained baked geometry"),
        new("door.originalUnsupported", Severity.Error, "Editable doors require an OpenSCCT build") { OriginalOnly = true },
    ];
    public override void Run(CheckContext c)
    {
        foreach (var a in c.Project.Actors.Where(a => a.Door is not null))
        {
            var reference = ObjectRef.Actor(a.Id); var d = a.Door!;
            try { d.Clone().Validate(); if (!c.Classes.IsA(a.Class, "Engine.Mover")) throw new ArgumentException("Editable doors must use Engine.Mover"); }
            catch (ArgumentException ex) { c.Report(Severity.Error, "door.design", ex.Message, a.Location, reference); }
            if (d.CutDepth > 0 && !DoorGeometry.CanCarve(c.Project, d)) c.Report(Severity.Error, "door.bakedCut", "This door cannot cut retained baked geometry. Place it in an existing opening.", a.Location, reference);
            if (c.Original) c.Report(Severity.Error, "door.originalUnsupported", "Editable doors require an OpenSCCT build.", a.Location, reference);
        }
    }
}
