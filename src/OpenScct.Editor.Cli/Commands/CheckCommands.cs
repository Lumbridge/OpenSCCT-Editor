using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Validation;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose check</c>: Map Check from the command line. Exit code 1 when there are errors.</summary>
internal static class CheckCommands
{
    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("check", "check <project> [--target openscct|original] [--only code,prefix...] [--min error|warning|info] [--deep] [--fix] [--json] | check --rules",
            "Runs Map Check (stable rule codes; --rules lists them). --deep also compiles the map and runs the deep rules (zone leaks, " +
            "actors in the void, floating meshes, mesh collision caps, reachability). --fix applies every fix a rule offers and saves " +
            "the project. Exit code 1 when any error is found.", CheckAsync),
    ];

    private static Task<int> CheckAsync(CliArgs a)
    {
        EnsureServices();
        if (a.Has("rules"))
        {
            PrintRules(a);
            return Task.FromResult(0);
        }

        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        EditorSession session = EditorSession.Open(folder);
        MapProject project = session.Project;
        TargetGame target = a.Get("target")?.ToLowerInvariant() switch
        {
            null or "openscct" or "svmap" => TargetGame.OpenScct,
            "original" or "sdc" or "ue2" => TargetGame.Original,
            string t => throw new ArgumentException($"--target must be openscct or original, not '{t}'"),
        };
        Severity min = a.Get("min")?.ToLowerInvariant() switch
        {
            null or "info" => Severity.Info,
            "warning" => Severity.Warning,
            "error" => Severity.Error,
            string m => throw new ArgumentException($"--min must be error, warning or info, not '{m}'"),
        };
        string[]? only = a.Get("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IMapChecker checker = EditorServices.Checker ?? new MapChecker();
        IReadOnlyList<Finding> Run() => checker.Check(project, new CheckOptions
        {
            Target = target,
            Only = only,
            ProjectFolder = Path.GetFullPath(folder),
            Compiled = a.Has("deep") ? AnalysisMap.Build(project, session.Folder, line => Console.Error.WriteLine($"ose check: {line}")) : null,
        });

        IReadOnlyList<Finding> all = Run();
        if (a.Has("fix") && Fix(session, all) > 0)
        {
            // What is left after the fixes is what gets printed and decides the exit code.
            all = Run();
        }

        List<Finding> shown = all.Where(f => f.Severity <= min).ToList();
        int errors = all.Count(f => f.Severity == Severity.Error);
        if (a.Has("json"))
        {
            PrintJson(ToJson(project, target, all, shown), a);
        }
        else
        {
            foreach (Finding f in shown)
            {
                Console.WriteLine(f);
            }

            Console.WriteLine($"{project.Id}: {errors} errors, {all.Count(f => f.Severity == Severity.Warning)} warnings, " +
                $"{all.Count(f => f.Severity == Severity.Info)} notes (target {target.ToString().ToLowerInvariant()})");
        }

        return Task.FromResult(errors > 0 ? 1 : 0);
    }

    /// <summary>
    /// Applies each finding's fix as its own undo step (skipping one whose actor an earlier fix already moved), then
    /// saves the project. Returns how many were applied.
    /// </summary>
    private static int Fix(EditorSession session, IReadOnlyList<Finding> findings)
    {
        int applied = 0;
        foreach (Finding f in findings.Where(f => f.Fix is not null))
        {
            if (!f.Fix!.IsCurrent(session.Project))
            {
                Console.Error.WriteLine($"ose check: skipped {f.Code}: {f.Fix.Label} (an earlier fix moved it)");
                continue;
            }

            ChangeResult r = ChangeApplier.Apply(session, f.Fix.Changes, dryRun: false, author: "ose check --fix");
            Console.Error.WriteLine($"ose check: {(r.Ok ? "fixed" : "could not fix")} {f.Code}: {f.Fix.Label}");
            applied += r.Ok ? 1 : 0;
        }

        if (applied > 0)
        {
            session.Save();
        }

        Console.Error.WriteLine($"ose check: {applied} fixes applied{(applied > 0 ? " and saved; the findings below are from a check after them" : "")}");
        return applied;
    }

    private static SvJsonObject ToJson(MapProject p, TargetGame target, IReadOnlyList<Finding> all, List<Finding> shown)
    {
        var counts = new SvJsonObject();
        foreach (Severity s in Enum.GetValues<Severity>())
        {
            counts.Add(s.ToString().ToLowerInvariant(), SvJsonNode.Integer(all.Count(f => f.Severity == s)));
        }

        var o = new SvJsonObject();
        o.Add("map", SvJsonNode.String(p.Id));
        o.Add("target", SvJsonNode.String(target.ToString().ToLowerInvariant()));
        o.Add("counts", counts);
        o.Add("findings", new SvJsonArray(shown.Select(FindingJson)));
        return o;
    }

    /// <summary>One finding: severity, code, message, objects as <c>Kind:Id</c>, and where.</summary>
    public static SvJsonObject FindingJson(Finding f)
    {
        var o = new SvJsonObject();
        o.Add("sev", SvJsonNode.String(f.Severity.ToString().ToLowerInvariant()));
        o.Add("code", SvJsonNode.String(f.Code));
        o.Add("msg", SvJsonNode.String(f.Message));
        if (f.Fix is { } fix)
        {
            o.Add("fix", SvJsonNode.String(fix.Label));
        }

        if (f.Objects.Count > 0)
        {
            o.Add("objects", Array(f.Objects.Select(r => r.ToString())));
        }

        if (f.Location is { } at)
        {
            o.Add("at", ModelJson.Vec(at));
        }

        if (MapQuery.ShapesJson(f) is { } shapes)
        {
            o.Add("shapes", shapes);
        }

        return o;
    }

    private static void PrintRules(CliArgs a)
    {
        if (a.Has("json"))
        {
            PrintJson(new SvJsonArray(MapChecker.Rules.Select(r =>
            {
                var o = new SvJsonObject();
                o.Add("code", SvJsonNode.String(r.Code));
                o.Add("sev", SvJsonNode.String(r.Severity.ToString().ToLowerInvariant()));
                o.Add("summary", SvJsonNode.String(r.Summary));
                if (r.OriginalOnly)
                {
                    o.Add("originalOnly", SvJsonNode.True);
                }

                if (r.Deep)
                {
                    o.Add("deep", SvJsonNode.True);
                }

                return o;
            })), a);
            return;
        }

        foreach (RuleInfo r in MapChecker.Rules)
        {
            Console.WriteLine($"  {r.Code,-34} {r.Severity.ToString().ToLowerInvariant(),-8} {r.Summary}{(r.OriginalOnly ? " (original only)" : "")}{(r.Deep ? " (deep)" : "")}");
        }
    }
}
