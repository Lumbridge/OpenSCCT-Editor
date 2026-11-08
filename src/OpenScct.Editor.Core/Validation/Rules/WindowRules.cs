using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Windows;

namespace OpenScct.Editor.Core.Validation.Rules;

internal sealed class WindowRules : MapRule
{
    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new("window.design", Severity.Error, "Invalid window dimensions, outline or actor class"),
        new("window.refit", Severity.Warning, "A linked pane needs Refit or Detach"),
        new("window.bakedCut", Severity.Error, "A window would cut retained baked geometry"),
        new("window.originalFallback", Severity.Warning, "SDC exports replace editable windows with standard original-game glass") { OriginalOnly = true },
    ];
    public override void Run(CheckContext c)
    {
        foreach (var a in c.Project.Actors.Where(a => a.Window is not null))
        {
            var reference = ObjectRef.Actor(a.Id); var d = a.Window!;
            try
            {
                d.Clone().Validate(); WindowGeometry.Pane(d.Clone());
                if (!c.Classes.IsA(a.Class, "SBase.SVitre")) throw new ArgumentException("Editable windows must use SBase.SVitre");
            }
            catch (ArgumentException ex) { c.Report(Severity.Error, "window.design", ex.Message, a.Location, reference); }
            if (d.Binding is { } binding && (binding.NeedsRefit || binding.Sources.Any(id => WindowFit.Transform(c.Project, id) is null)))
                c.Report(Severity.Warning, "window.refit", "The frame changed or is missing. Use Refit, or Detach to keep this pane independently.", a.Location, reference);
            if (d.CutDepth > 0 && !WindowGeometry.CanCarve(c.Project, d))
                c.Report(Severity.Error, "window.bakedCut", "This window cannot cut retained baked geometry. Place it in an existing opening.", a.Location, reference);
            if (c.Original) c.Report(Severity.Warning, "window.originalFallback", "SDC export automatically replaces this window with a rectangular, breakable original-game window fitted to its pane bounds. Custom shape, thickness, tint and frosting become standard glass. T3D export still requires replacing editable windows manually.", a.Location, reference);
        }
    }
}
