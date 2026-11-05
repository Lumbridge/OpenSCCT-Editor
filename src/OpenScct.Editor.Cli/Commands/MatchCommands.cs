using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose match</c> (player-count rules, win targets and the layout at a count) and <c>ose outfit</c> (what a
/// team wears): the match and outfits ops and queries of the scripting API on the command line. Edits run as one change
/// document and save the project unless <c>--dry-run</c>.</summary>
internal static class MatchCommands
{
    private const string MatchUsage =
        "match show <project> [--players N] [--mode Sabotage] | match set <project> <ids...> [--players min,max|any] [--outside hidden|key:N|default] " +
        "[--inside normal|hidden|key:N|default] [--win n,n,...|none] | match clear <project> <ids...>  (edits: [--dry-run] [--author a])";

    private const string OutfitUsage =
        "outfit show <project> | outfit set <project> --team spy|merc [--body file.png|none] [--head file.png|none] " +
        "[--model file.glb|Package.Mesh|none] [--goggle-bone name] [--goggle-offset x,y,z] [--cosmetics map|player|none] | " +
        "outfit clear <project> --team spy|merc  (edits: [--dry-run] [--author a])";

    [CliProvider]
    public static IEnumerable<CliCommand> Provide() =>
    [
        new("match", MatchUsage,
            "Shows and sets player-count rules (which actors are in a match of N players) and missions' win targets; prints JSON.",
            a => Guard(a, Match)),
        new("outfit", OutfitUsage,
            "Shows and sets a team's outfit on the map: body and head textures, a replacement model and the cosmetics policy; prints JSON.",
            a => Guard(a, Outfit)),
    ];

    private static Task<int> Guard(CliArgs a, Func<CliArgs, int> run)
    {
        GeometryCliSupport.EnsureServices();
        try
        {
            return Task.FromResult(run(a));
        }
        catch (ApiException ex)
        {
            GeometryCliSupport.PrintJson(ApiJson.Obj(("ok", SvJsonNode.False), ("error", ex.ToJson())), a);
            return Task.FromResult(1);
        }
    }

    private static int Match(CliArgs a)
    {
        switch (a.Positional(0))
        {
            case "show":
                var q = ApiJson.Obj(("q", ApiJson.Str("match")), ("players", a.Get("players") is { } n ? ApiJson.Num(Int(n, "players")) : null),
                    ("mode", a.Get("mode") is { } mode ? ApiJson.Str(mode) : null));
                GeometryCliSupport.PrintJson(MapQuery.Run(Open(a), q), a);
                return 0;
            case "set":
                return Edit(a, ApiJson.Obj(
                    ("op", ApiJson.Str("match")),
                    ("ids", Ids(a)),
                    ("players", a.Get("players") is { } players ? Range(players) : null),
                    ("outside", a.Get("outside") is { } outside ? Hold(outside) : null),
                    ("inside", a.Get("inside") is { } inside ? Hold(inside) : null),
                    ("win", a.Get("win") is { } win ? Win(win) : null)));
            case "clear":
                return Edit(a, ApiJson.Obj(("op", ApiJson.Str("match")), ("ids", Ids(a)), ("clear", SvJsonNode.True)));
            default:
                throw new ArgumentException("ose match show|set|clear (see ose match --help)");
        }
    }

    private static int Outfit(CliArgs a)
    {
        switch (a.Positional(0))
        {
            case "show":
                GeometryCliSupport.PrintJson(MapQuery.Run(Open(a), ApiJson.Obj(("q", ApiJson.Str("outfits")))), a);
                return 0;
            case "set":
                SvJsonObject? goggles = a.Get("goggle-bone") is null && a.Get("goggle-offset") is null ? null : ApiJson.Obj(
                    ("bone", a.Get("goggle-bone") is { } bone ? ApiJson.Str(bone) : null),
                    ("offset", a.Get("goggle-offset") is { } offset ? Numbers(offset, "goggle-offset") : null));
                return Edit(a, ApiJson.Obj(
                    ("op", ApiJson.Str("outfits")),
                    ("team", ApiJson.Str(Team(a))),
                    ("body", a.Get("body") is { } body ? FileOrNone(body) : null),
                    ("head", a.Get("head") is { } head ? FileOrNone(head) : null),
                    ("model", a.Get("model") is { } model ? Model(model) : null),
                    ("goggles", goggles),
                    ("cosmetics", a.Get("cosmetics") is { } cosmetics ? ApiJson.Str(cosmetics) : null)));
            case "clear":
                return Edit(a, ApiJson.Obj(("op", ApiJson.Str("outfits")), ("team", ApiJson.Str(Team(a))), ("clear", SvJsonNode.True)));
            default:
                throw new ArgumentException("ose outfit show|set|clear (see ose outfit --help)");
        }
    }

    private static EditorSession Open(CliArgs a) => EditorSession.Open(a.Positional(1) ?? throw new ArgumentException("give the project folder"));

    private static int Edit(CliArgs a, SvJsonObject op)
    {
        EditorSession session = Open(a);
        bool dry = a.Has("dry-run");
        var doc = ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)), ("ops", ApiJson.Arr([op])), ("label", ApiJson.Str($"ose {op["op"]}")));
        ChangeResult result = ChangeApplier.Apply(session, doc, dryRun: dry, author: a.Get("author") ?? "ose");
        if (result.Ok && !dry && session.IsDirty)
        {
            session.Save();
            result.Json.Add("saved", ApiJson.Str(session.Folder!));
        }

        GeometryCliSupport.PrintJson(result.Json, a);
        return result.Ok ? 0 : 1;
    }

    private static SvJsonNode Ids(CliArgs a)
    {
        List<string> ids = a.PositionalArgs.Skip(2).SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        return ids.Count > 0 ? ApiJson.Strings(ids) : throw new ArgumentException("give the actor ids after the project");
    }

    private static string Team(CliArgs a) => a.Get("team") ?? throw new ArgumentException("give --team spy or --team merc");

    private static SvJsonNode Range(string text) =>
        text.Equals("any", StringComparison.OrdinalIgnoreCase) ? ApiJson.Str("any") : Numbers(text, "players", count: 2);

    /// <summary>"hidden", "normal", "default" or "key:N".</summary>
    private static SvJsonNode Hold(string text) =>
        text.StartsWith("key:", StringComparison.OrdinalIgnoreCase)
            ? ApiJson.Obj(("key", ApiJson.Num(Int(text[4..], "key"))))
            : text.Equals("default", StringComparison.OrdinalIgnoreCase) ? SvJsonNode.Null : ApiJson.Str(text.ToLowerInvariant());

    private static SvJsonNode Win(string text) => text.Equals("none", StringComparison.OrdinalIgnoreCase) ? SvJsonNode.Null : Numbers(text, "win");

    private static SvJsonNode FileOrNone(string text) =>
        text.Equals("none", StringComparison.OrdinalIgnoreCase) ? SvJsonNode.Null : ApiJson.Str(Path.GetFullPath(text));

    private static SvJsonNode Model(string text) => text switch
    {
        _ when text.Equals("none", StringComparison.OrdinalIgnoreCase) => SvJsonNode.Null,
        _ when text.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) => ApiJson.Obj(("glb", ApiJson.Str(Path.GetFullPath(text)))),
        _ => ApiJson.Obj(("stock", ApiJson.Str(text))),
    };

    private static SvJsonArray Numbers(string text, string field, int? count = null)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (count is { } n && parts.Length != n)
        {
            throw new ArgumentException($"--{field} takes {n} comma-separated numbers");
        }

        return ApiJson.Arr(parts.Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? ApiJson.Num(d)
            : throw new ArgumentException($"--{field}: '{p}' is not a number")));
    }

    private static int Int(string text, string field) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : throw new ArgumentException($"--{field}: '{text}' is not a whole number");
}
