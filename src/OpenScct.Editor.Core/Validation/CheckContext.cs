using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation;

/// <summary>A Map Check rule's documentation: its stable code, usual severity and what it looks for.</summary>
public sealed record RuleInfo(string Code, Severity Severity, string Summary)
{
    /// <summary>Only reported when exporting to the original game (an engine cap OpenSCCT does not have).</summary>
    public bool OriginalOnly { get; init; }

    /// <summary>Only run by a deep check, on the compiled map (<see cref="CheckOptions.Compiled"/>).</summary>
    public bool Deep { get; init; }
}

/// <summary>A group of Map Check rules.</summary>
internal abstract class MapRule
{
    public abstract IReadOnlyList<RuleInfo> Rules { get; }

    public abstract void Run(CheckContext c);
}

/// <summary>What a rule sees: the project, the options, the class catalog and the findings so far.</summary>
internal sealed class CheckContext
{
    private readonly List<Finding> _findings = new();
    private readonly Dictionary<string, bool> _isA = new(StringComparer.OrdinalIgnoreCase);
    private Geometry.SolidQuery? _solid;

    public CheckContext(MapProject project, CheckOptions options, IClassCatalog classes)
    {
        Project = project;
        Options = options;
        Classes = classes;
        Design = DesignNumbers.Of(project);
    }

    public MapProject Project { get; }

    public CheckOptions Options { get; }

    public IClassCatalog Classes { get; }

    /// <summary>Body and clearance numbers from the project's movement limits.</summary>
    public DesignNumbers Design { get; }

    public bool Original => Options.Target == TargetGame.Original;

    /// <summary>Open/solid queries over the project's brushes, built on first use and shared by the rules.</summary>
    public Geometry.SolidQuery Solid => _solid ??= Geometry.SolidQuery.Of(Project);

    public IReadOnlyList<Finding> Findings => _findings;

    /// <summary>True when the options ask for rules with this code.</summary>
    public bool Wants(string code) =>
        Options.Only is not { Count: > 0 } only || only.Any(p => code.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public void Report(Finding f)
    {
        if (Wants(f.Code))
        {
            _findings.Add(f);
        }
    }

    public void Report(Severity severity, string code, string message, Vec3? at, params ObjectRef[] objects) =>
        Report(new Finding(severity, code, message, objects, at));

    /// <summary>Error for the original game, the given severity for OpenSCCT.</summary>
    public Severity ForTarget(Severity openScct) => Original ? Severity.Error : openScct;

    public bool IsA(Actor a, string ancestor)
    {
        string key = a.Class + "|" + ancestor;
        if (!_isA.TryGetValue(key, out bool v))
        {
            v = string.Equals(a.Class, ancestor, StringComparison.OrdinalIgnoreCase) || Classes.IsA(a.Class, ancestor);
            _isA[key] = v;
        }

        return v;
    }

    /// <summary>An actor's int/byte property: stored, else the class default, else <paramref name="fallback"/>.</summary>
    public int Int(Actor a, string prop, int fallback = 0) =>
        (a.Props[prop] ?? Classes.Default(a.Class, prop)) is SvJsonNumber n && n.TryGetInt32(out int i) ? i : fallback;

    public bool Bool(Actor a, string prop, bool fallback = false)
    {
        SvJsonNode? v = a.Props[prop] ?? Classes.Default(a.Class, prop);
        return v is SvJsonLiteral l ? l.Kind == SvJsonKind.True : fallback;
    }

    /// <summary>A name/string property (stored or default), empty for None.</summary>
    public string Name(Actor a, string prop)
    {
        string? s = (a.Props[prop] ?? Classes.Default(a.Class, prop)) is SvJsonString str ? str.Value : null;
        return s is null || s.Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : s;
    }

    /// <summary>The world-space centre of a brush's bounds.</summary>
    public static Vec3 Centre(Brush b)
    {
        (Vec3 min, Vec3 max) = Geometry.BrushOps.WorldBounds(b);
        return (min + max) / 2;
    }
}
