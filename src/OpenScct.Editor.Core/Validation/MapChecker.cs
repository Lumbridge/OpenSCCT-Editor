using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation.Rules;

namespace OpenScct.Editor.Core.Validation;

/// <summary>
/// Map Check: every lint rule over a project, with stable codes (<c>start.team-missing</c>, <c>brush.not-convex</c>,
/// <c>limit.zones</c>...) so the app can link findings to objects and scripts can fix them by id. Findings come out in
/// a stable order: worst first, then by code, object and message. Engine caps of the original game are errors only
/// when the target is the original game.
/// </summary>
[EditorService]
public sealed class MapChecker : IMapChecker
{
    private static readonly IReadOnlyList<MapRule> s_rules =
    [
        new StartRules(),
        new ClassRules(),
        new ReferenceRules(),
        new IdRules(),
        new BrushRules(),
        new WorldRules(),
        new GeRules(),
        new LimitRules(),
        new MatchRules(),
        new MoverRules(),
        new PlacementRules(),
        new DesignRules(),
        new ConveyorRules(),
        new FlowRules(),
        new IdentityRules(),
        new Gameplay.GeGameplayRules(),
        new Gameplay.WiringRules(),
        new DeepRules(),
    ];

    /// <summary>The catalog used when no class catalog service is registered (loaded once).</summary>
    private static readonly Lazy<IClassCatalog> s_fallbackClasses = new(() => ClassCatalog.FromContent(EditorServices.ContentRoot));

    /// <summary>Every rule's code, severity and summary, sorted by code.</summary>
    public static IReadOnlyList<RuleInfo> Rules { get; } = s_rules.SelectMany(r => r.Rules).OrderBy(r => r.Code, StringComparer.Ordinal).ToList();

    public IReadOnlyList<Finding> Check(MapProject project, CheckOptions options)
    {
        IClassCatalog classes = EditorServices.Classes ?? s_fallbackClasses.Value;
        var context = new CheckContext(project, options, classes);
        foreach (MapRule rule in s_rules)
        {
            if (rule.Rules.Any(r => context.Wants(r.Code)))
            {
                rule.Run(context);
            }
        }

        return context.Findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .ThenBy(f => f.Objects.Count > 0 ? f.Objects[0].Id : "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ToList();
    }
}
