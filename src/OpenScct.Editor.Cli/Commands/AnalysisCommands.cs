using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Session;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary>
/// <c>ose analyze</c>: the analyses of the compiled map (stealth light map, reachability, routes, the bots' graph) from
/// the command line. Each one is the API query of the same name with the options turned into its fields, so the
/// output is the query's JSON.
/// </summary>
internal static class AnalysisCommands
{
    private static readonly string[] s_kinds = ["stealth", "reach", "route", "nav"];

    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("analyze", "analyze stealth|reach|route|nav <project.osmap> [--lights start|blackout|lit|switch:ID] [--off ids] [--on ids] " +
            "[--stance crouch|stand] [--night] [--spacing N] [--no-merc-sight] [--cells] [--side spy|merc] [--drops survivable|unhurt|bot] " +
            "[--mask N|all] [--from x,y,z|id] [--to x,y,z|id] [--near x,y,z|id] [--radius N] [--limit N] [--pretty]",
            "Analyses the compiled map with OpenSCCT's own loader, light model and bot navigation: stealth (how dark the floors spies " +
            "reach are, merc tracker and torch reach, the background behind the spy; --night as night vision shows it), reach (each side's reach from its starts, spy-only areas, unreachable " +
            "objectives, doors and lifts), route (can a side get from --from to --to, through which doors and lifts) and nav (the bots' graph). Prints the JSON of the " +
            "query of the same name.", AnalyzeAsync),
    ];

    private static Task<int> AnalyzeAsync(CliArgs a)
    {
        string kind = a.Positional(0)?.ToLowerInvariant() ?? throw new ArgumentException($"give the analysis ({string.Join(", ", s_kinds)})");
        if (!s_kinds.Contains(kind))
        {
            throw new ArgumentException($"unknown analysis '{kind}' ({string.Join(", ", s_kinds)})");
        }

        string folder = a.Positional(1) ?? throw new ArgumentException("give the project folder");
        EnsureServices();
        EditorSession session = EditorSession.Open(folder);
        SvJsonNode result = MapQuery.Run(session, Query(kind, a));
        PrintJson(result, a);
        return Task.FromResult(result is SvJsonObject o && o["error"] is not null ? 1 : 0);
    }

    /// <summary>The query object of the options: names as the query spells them, lists and points parsed.</summary>
    private static SvJsonObject Query(string kind, CliArgs a)
    {
        var q = new SvJsonObject();
        q.Add("q", SvJsonNode.String(kind));
        // Points pass through as text: the query reads "x,y,z" and "ActorId@anchor+dx,dy,dz".
        foreach (string key in new[] { "lights", "stance", "side", "drops", "from", "to", "near" })
        {
            if (a.Get(key) is { } text)
            {
                q.Add(key, SvJsonNode.String(text));
            }
        }

        foreach (string key in new[] { "off", "on" })
        {
            if (a.Get(key) is { } list)
            {
                q.Add(key, Array(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
        }

        foreach (string key in new[] { "spacing", "radius", "limit" })
        {
            if (a.Get(key) is { } number)
            {
                q.Add(key, Num(double.Parse(number, CultureInfo.InvariantCulture)));
            }
        }

        if (a.Get("mask") is { } mask)
        {
            q.Add("mask", mask.Equals("all", StringComparison.OrdinalIgnoreCase) ? SvJsonNode.String("all")
                : SvJsonNode.Integer(int.Parse(mask, CultureInfo.InvariantCulture)));
        }

        if (a.Has("no-merc-sight"))
        {
            q.Add("mercSight", SvJsonNode.False);
        }

        if (a.Has("night"))
        {
            q.Add("night", SvJsonNode.True);
        }

        if (a.Has("cells"))
        {
            q.Add("cells", SvJsonNode.True);
        }

        return q;
    }
}
