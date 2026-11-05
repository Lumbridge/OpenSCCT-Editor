using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose prefab list|show|place|save|update|break</c>: the prefab ops of the scripting API on the command line.
/// Edits run as one change document (one undo step) and save the project unless <c>--dry-run</c>.</summary>
internal static class PrefabCommands
{
    private const string Usage =
        "prefab list [project] | show <name> [project] | place <project> <name> [--at x,y,z|Id@anchor] [--wall nearest|+x|-y..] [--on true] " +
        "[--lift z] [--offset x,y,z] [--pivot anchor] [--rot-deg yaw] [--face x,y,z] [--id Name] [--layer L] [--<param> value ...] | " +
        "save <project> <name> <ids...> [--pivot x,y,z] [--yaw-deg d] [--title t] [--summary s] [--category c] [--library] [--overwrite] [--link] | " +
        "update <project> [instance ids...] [--name prefab] [--all] | break <project> <instance ids...> [--delete]  (edits: [--dry-run] [--author a])";

    private static readonly HashSet<string> s_placeOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "at", "offset", "near", "dist", "wall", "gap", "reach", "on", "lift", "pivot", "rot-deg", "face", "keepRot", "id", "layer", "folder", "note",
        "dry-run", "author", "pretty",
    };

    [CliProvider]
    public static IEnumerable<CliCommand> Provide() =>
    [
        new("prefab", Usage,
            "Lists, places, saves, updates and unlinks prefabs (saved groups of brushes, actors and GEs with parameters); prints JSON.",
            Run),
    ];

    private static Task<int> Run(CliArgs a)
    {
        GeometryCliSupport.EnsureServices();
        try
        {
            return Task.FromResult(a.Positional(0) switch
            {
                "list" => List(a),
                "show" => Show(a),
                "place" => Edit(a, Place(a)),
                "save" => Edit(a, Save(a)),
                "update" => Edit(a, Update(a)),
                "break" => Edit(a, Break(a)),
                _ => throw new ArgumentException("ose prefab list|show|place|save|update|break (see ose prefab --help)"),
            });
        }
        catch (ApiException ex)
        {
            GeometryCliSupport.PrintJson(ApiJson.Obj(("ok", SvJsonNode.False), ("error", ex.ToJson())), a);
            return Task.FromResult(1);
        }
    }

    private static int List(CliArgs a)
    {
        EditorSession? session = a.Positional(1) is { } path ? EditorSession.Open(path) : null;
        SvJsonNode result = session is null
            ? ApiJson.Obj(("prefabs", ApiJson.Arr(PrefabStore.All(null).Prefabs.Select(Brief))), ("library", ApiJson.Str(PrefabStore.LibraryFolder)))
            : MapQuery.Run(session, ApiJson.Obj(("q", ApiJson.Str("prefabs"))));
        GeometryCliSupport.PrintJson(result, a);
        return 0;
    }

    private static int Show(CliArgs a)
    {
        string name = a.Positional(1) ?? throw new ArgumentException("give the prefab's name");
        if (a.Positional(2) is { } path)
        {
            GeometryCliSupport.PrintJson(MapQuery.Run(EditorSession.Open(path), ApiJson.Obj(("q", ApiJson.Str("prefabs")), ("name", ApiJson.Str(name)))), a);
            return 0;
        }

        PrefabDefinition def = PrefabStore.Find(name, null) ?? throw new ApiException($"no prefab '{name}' in the library or built in", "name");
        GeometryCliSupport.PrintJson(def.ToJson(), a);
        return 0;
    }

    private static SvJsonNode Brief(PrefabDefinition d) => ApiJson.Obj(
        ("name", ApiJson.Str(d.Name)),
        ("title", d.Title is null ? null : ApiJson.Str(d.Title)),
        ("source", ApiJson.Str(d.Source.ToString().ToLowerInvariant())),
        ("params", d.Params.Count == 0 ? null : ApiJson.Obj(d.Params.Select(p => (p.Name, (SvJsonNode?)p.Default)).ToArray())));

    private static SvJsonObject Place(CliArgs a)
    {
        var op = Op("prefab.place", ("name", ApiJson.Str(a.Positional(2) ?? throw new ArgumentException("give the prefab's name"))));
        foreach (string key in new[] { "at", "offset", "near", "pivot", "face", "id", "layer", "folder", "note" })
        {
            if (a.Get(key) is { } v)
            {
                op.Add(key, ApiJson.Str(v));
            }
        }

        foreach (string key in new[] { "dist", "gap", "reach", "lift" })
        {
            if (a.Get(key) is { } v)
            {
                op.Add(key, Value(v));
            }
        }

        if (a.Get("wall") is { } wall)
        {
            op.Add("wall", Value(wall));
        }

        if (a.Get("on") is { } on)
        {
            op.Add("on", Value(on));
        }

        if (a.Has("keepRot"))
        {
            op.Add("keepRot", SvJsonNode.True);
        }

        if (a.Get("rot-deg") is { } yaw)
        {
            op.Add("rotDeg", ApiJson.Arr([ApiJson.Num(0), ApiJson.Num(Parsing.Number(yaw, "--rot-deg")), ApiJson.Num(0)]));
        }

        var parms = new SvJsonObject();
        foreach (string key in a.OptionKeys.Where(k => !s_placeOptions.Contains(k)).Order(StringComparer.Ordinal))
        {
            parms.Add(key, Value(a.Get(key)!));
        }

        if (parms.Count > 0)
        {
            op.Add("params", parms);
        }

        return op;
    }

    private static SvJsonObject Save(CliArgs a)
    {
        string name = a.Positional(2) ?? throw new ArgumentException("give the new prefab's name");
        List<string> ids = a.PositionalArgs.Skip(3).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("give the ids of the objects to save");
        }

        var op = Op("prefab.save", ("name", ApiJson.Str(name)), ("ids", ApiJson.Strings(ids)));
        foreach (string key in new[] { "pivot", "title", "summary", "category" })
        {
            if (a.Get(key) is { } v)
            {
                op.Add(key, ApiJson.Str(v));
            }
        }

        if (a.Get("yaw-deg") is { } yaw)
        {
            op.Add("rotDeg", ApiJson.Arr([ApiJson.Num(0), ApiJson.Num(Parsing.Number(yaw, "--yaw-deg")), ApiJson.Num(0)]));
        }

        if (a.Has("library"))
        {
            op.Add("to", ApiJson.Str("library"));
        }

        if (a.Has("overwrite"))
        {
            op.Add("overwrite", SvJsonNode.True);
        }

        if (a.Has("link"))
        {
            op.Add("link", SvJsonNode.True);
        }

        return op;
    }

    private static SvJsonObject Update(CliArgs a)
    {
        var op = Instances(a, "prefab.update");
        if (op.Count == 1)
        {
            throw new ArgumentException("give instance ids, --name <prefab> or --all");
        }

        return op;
    }

    private static SvJsonObject Break(CliArgs a)
    {
        var op = Instances(a, "prefab.break");
        if (a.Has("delete"))
        {
            op.Add("delete", SvJsonNode.True);
        }

        return op;
    }

    private static SvJsonObject Instances(CliArgs a, string name)
    {
        var op = Op(name);
        List<string> ids = a.PositionalArgs.Skip(2).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (ids.Count > 0)
        {
            op.Add("ids", ApiJson.Strings(ids));
        }

        if (a.Get("name") is { } prefab)
        {
            op.Add("name", ApiJson.Str(prefab));
        }

        if (a.Has("all"))
        {
            op.Add("all", SvJsonNode.True);
        }

        return op;
    }

    /// <summary>Runs one op as a change document on the project and saves it unless --dry-run.</summary>
    private static int Edit(CliArgs a, SvJsonObject op)
    {
        string path = a.Positional(1) ?? throw new ArgumentException("give the project folder");
        EditorSession session = EditorSession.Open(path);
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

    private static SvJsonObject Op(string name, params (string Key, SvJsonNode? Value)[] members) =>
        ApiJson.Obj([("op", ApiJson.Str(name)), .. members]);

    /// <summary>An option value as JSON: a number, true/false, or text.</summary>
    private static SvJsonNode Value(string text) => text.ToLowerInvariant() switch
    {
        "true" => SvJsonNode.True,
        "false" => SvJsonNode.False,
        _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => ApiJson.Num(d),
        _ => ApiJson.Str(text),
    };
}
