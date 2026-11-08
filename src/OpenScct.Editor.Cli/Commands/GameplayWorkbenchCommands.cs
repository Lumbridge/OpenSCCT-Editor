using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary>
/// The gameplay workbenches on the command line: <c>ose mover</c>, <c>magic</c>, <c>camnet</c>, <c>props</c>,
/// <c>propset</c>, <c>tips</c>, <c>zone</c> and <c>modes</c>. Reads print the matching API query; edits run the
/// matching API op as one change document (one undo step) and save unless <c>--dry-run</c>. Output is JSON.
/// </summary>
internal static class GameplayWorkbenchCommands
{
    private const string Edits = "(edits: [--dry-run] [--author a])";

    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("mover", "mover <project> <id> [--keys x,y,z[/p,y,r];...] [--add-key true|x,y,z] [--delete-key n] [--start-key n] [--move-time s] " +
            "[--stay-open-time s] [--delay-time s] [--delay-time-last-key s] [--other-time s] [--eased true|false] [--legs key:time[:ease];...] " +
            "[--state S] [--show-key n] " + Edits,
            "A mover's keys (offsets from its base, up to 65,536 native stops; original-game exports allow eight), times, glide, start key and state. --legs gives " +
            "keys their own timing (OpenSCCT): the leg from the key before in seconds (mover: its MoveTime; empty: unchanged) and the " +
            "easing at the key (smooth, speed, mover: its glide; empty: unchanged), e.g. 1:4:smooth;2:0.5. With no edit options, prints " +
            "its keys, one activation's timeline with the time each key is reached, and what triggers it.",
            Guard(MoverAsync)),
        new("magic", "magic show <project> <id> [--play t[:untrigger],...] [--at s] | magic set <project> <id|new> --groups <json|file> [--at x,y,z --tag T] " +
            "[--stop-actor id|none] | magic export <project> <id> [--out file] | magic import <project> <id> <file> " + Edits,
            "SMagicEvent timelines. show lists the groups and plays activations through the game's logic (default one trigger " +
            "at 0), then what they do to the map by --at seconds (default: the end), through other SMagicEvents too: the events " +
            "delivered, the trigger lights switched and the movers started; set replaces the groups (or makes a new one with id new); export and import use the RE+ JSON exchange " +
            "(scct.smagic-event v1).", Guard(MagicAsync)),
        new("camnet", "camnet list <project> | camnet link <project> <id,id,...> [--loop true] | camnet remove <project> <id,...> | " +
            "camnet camera <project> <id> [--name N] [--pitch below,above] [--yaw below,above] " + Edits,
            "Security camera networks (SCamNetwork): list the rings with the camera the CamNet gadget opens on and link " +
            "problems; link whole networks in an order; take cameras out; name a camera and limit how far it turns.",
            Guard(CamNetAsync)),
        new("props", "props copy <project> <id> [--out file] | props paste <project> (--from A | --from-file copy.json) --to b,c [--keys k1,k2] " + Edits,
            "Copy properties: copy prints an actor's stored values as osmap.props/1; paste copies them onto other actors, from " +
            "an actor of the map or from a copy file (so values move between maps; default: everything but identity, " +
            "placement and links to other actors).", Guard(PropsAsync)),
        new("propset", "propset list [project] [--class C] | propset apply <project> <Class.Name> --to ids | propset save <project> <id> <name> " +
            "[--keys k1,k2] [--map true] [--note text] | propset delete <Class.Name> [--map project] | propset import-ini <PropertiesSets.ini> [--map project]",
            "Property sets (the stock editor's Use Properties Set): the map's propsets.json and the user library. import-ini " +
            "reads the original game's System/PropertiesSets.ini (into the library, or a map with --map).", Guard(PropSetAsync)),
        new("tips", "tips list <project> | tips add <project> --at x,y,z --spy text [--merc text] [--radius 150] [--height 100] [--marker false] " +
            "[--mesh path] [--ends-game true] | tips set <project> <id> [--spy] [--merc] [--radius] [--height] [--only-with-help b] " +
            "[--stay b] [--end-game-delay s] " + Edits,
            "Zone tips (hint cylinders shown with in-game help) and their help path markers.", Guard(TipsAsync)),
        new("zone", "zone list <project> [mission] | zone add <project> <mission> <actor> [--delay s] [--type trigger|untrigger|...] | " +
            "zone message <project> <mission> --spy text [--merc text] [--seconds 6] [--delay s] | zone set <project> <mission> <index> " +
            "[--delay s] [--type t] | zone remove <project> <mission> <index> " + Edits,
            "What a mission (zone) does when complete: doors, lights, sounds, HUD messages or any actor, with delays, through " +
            "the mission's own SMagicEvent relay (made when needed; other senders' wiring is never changed).", Guard(ZoneAsync)),
        new("modes", "modes <project> [mode]",
            "The game modes the map has missions for and how many actors only they use; with a mode (number or name), those ids.",
            Guard(ModesAsync)),
    ];

    private static Func<CliArgs, Task<int>> Guard(Func<CliArgs, int> run) => a =>
    {
        EnsureServices();
        try
        {
            return Task.FromResult(run(a));
        }
        catch (ApiException ex)
        {
            PrintJson(ApiJson.Obj(("ok", SvJsonNode.False), ("error", ex.ToJson())), a);
            return Task.FromResult(1);
        }
    };

    // ---- mover ------------------------------------------------------------------------------------------------------

    private static readonly (string Option, string Field)[] s_moverTimes =
    [
        ("move-time", "moveTime"), ("stay-open-time", "stayOpenTime"), ("delay-time", "delayTime"), ("delay-time-last-key", "delayTimeLastKey"),
        ("other-time", "otherTime"), ("start-key", "startKey"), ("delete-key", "deleteKey"), ("show-key", "showKey"),
    ];

    private static int MoverAsync(CliArgs a)
    {
        string path = Need(a.Positional(0), "the project folder"), id = Need(a.Positional(1), "the mover's id");
        var op = Op("mover", ("id", ApiJson.Str(id)));
        if (a.Get("keys") is { } keys)
        {
            op.Add("keys", ApiJson.Arr(keys.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k =>
            {
                string[] parts = k.Split('/');
                return (SvJsonNode)ApiJson.Obj(("at", Triple(parts[0], "--keys")), ("rot", parts.Length > 1 ? Triple(parts[1], "--keys") : null));
            })));
        }

        if (a.Get("add-key") is { } add)
        {
            op.Add("addKey", add == "true" ? SvJsonNode.True : Triple(add, "--add-key"));
        }

        foreach ((string option, string field) in s_moverTimes)
        {
            if (a.Get(option) is { } v)
            {
                op.Add(field, ApiJson.Num(Parsing.Number(v, "--" + option)));
            }
        }

        AddValue(op, a, "eased", "eased");
        if (a.Get("legs") is { } legs)
        {
            op.Add("legs", ApiJson.Arr(legs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(MoverLeg)));
        }

        AddValue(op, a, "state", "state");
        return op.Count == 2 ? Query(path, a, ("q", "mover"), ("id", id)) : Edit(path, a, op);
    }

    /// <summary>One <c>--legs</c> entry, <c>key:time[:ease]</c>, as the mover op's <c>{"key","time","smooth"}</c>.</summary>
    private static SvJsonNode MoverLeg(string text)
    {
        string[] parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || (parts[1].Length == 0 && (parts.Length == 2 || parts[2].Length == 0)))
        {
            throw new ArgumentException($"--legs: '{text}' is not key:time[:ease] (give a time, an easing or both)");
        }

        var leg = ApiJson.Obj(("key", ApiJson.Num(Parsing.Number(parts[0], "--legs key"))));
        switch (parts[1])
        {
            case "":
                break;
            case "mover":
                leg.Add("time", SvJsonNode.Null);
                break;
            default:
                leg.Add("time", ApiJson.Num(Parsing.Number(parts[1], "--legs time")));
                break;
        }

        switch (parts.Length == 3 ? parts[2] : "")
        {
            case "":
                break;
            case "smooth":
                leg.Add("smooth", SvJsonNode.True);
                break;
            case "speed":
                leg.Add("smooth", SvJsonNode.False);
                break;
            case "mover":
                leg.Add("smooth", SvJsonNode.Null);
                break;
            default:
                throw new ArgumentException($"--legs: the easing is smooth, speed or mover, not '{parts[2]}'");
        }

        return leg;
    }

    // ---- magic ------------------------------------------------------------------------------------------------------

    private static int MagicAsync(CliArgs a)
    {
        string verb = Need(a.Positional(0), "show, set, export or import");
        string path = Need(a.Positional(1), "the project folder"), id = Need(a.Positional(2), "the SMagicEvent's id");
        switch (verb)
        {
            case "show":
                var q = ApiJson.Obj(("q", ApiJson.Str("magic")), ("id", ApiJson.Str(id)));
                if (a.Get("play") is { } play)
                {
                    q.Add("play", ApiJson.Arr(play.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p =>
                    {
                        string[] parts = p.Split(':');
                        return (SvJsonNode)ApiJson.Obj(("t", ApiJson.Num(Parsing.Number(parts[0], "--play"))),
                            ("trigger", ApiJson.Bool(parts.Length < 2 || parts[1] != "untrigger")));
                    })));
                }

                if (a.Get("at") is { } seconds)
                {
                    q.Add("at", ApiJson.Num(Parsing.Number(seconds, "--at")));
                }

                PrintJson(MapQuery.Run(EditorSession.Open(path), q), a);
                return 0;
            case "set":
                var op = Op("magic", ("id", ApiJson.Str(id == "new" ? "$magic" : id)));
                if (a.Get("groups") is { } groups)
                {
                    op.Add("groups", JsonArg(groups, "--groups"));
                }

                if (a.Get("at") is { } at)
                {
                    op.Add("at", Triple(at, "--at"));
                }

                AddValue(op, a, "tag", "tag");
                if (a.Get("stop-actor") is { } stop)
                {
                    op.Add("stopActor", stop == "none" ? SvJsonNode.Null : ApiJson.Str(stop));
                }

                return Edit(path, a, op);
            case "export":
                EditorSession session = EditorSession.Open(path);
                Actor actor = session.Project.FindActor(id) is { } x && MagicEvents.IsMagicEvent(x) ? x : throw new ApiException($"'{id}' is not an SMagicEvent", "id");
                SvJsonObject doc = MagicEvents.Export(session.Project, actor);
                if (a.Get("out") is { } outFile)
                {
                    File.WriteAllText(outFile, CanonicalJson.ToPrettyString(doc) + "\n");
                    Console.WriteLine(outFile);
                }
                else
                {
                    PrintJson(doc, a);
                }

                return 0;
            case "import":
                string file = Need(a.Positional(3), "the exchange file");
                (List<MagicGroup>? imported, string? stopActor, bool hasStop) = MagicEvents.Import(SvJson.Parse(File.ReadAllBytes(file), file));
                var set = Op("magic", ("id", ApiJson.Str(id)));
                if (imported is not null)
                {
                    set.Add("groups", ApiJson.Arr(imported.Select(GroupJson)));
                }

                if (hasStop)
                {
                    set.Add("stopActor", stopActor is null ? SvJsonNode.Null : ApiJson.Str(stopActor));
                }

                return Edit(path, a, set);
            default:
                throw new ArgumentException($"unknown magic op '{verb}' (show, set, export, import)");
        }
    }

    private static SvJsonNode GroupJson(MagicGroup g) => ApiJson.Obj(
        ("actions", ApiJson.Arr(g.Actions.Select(x => (SvJsonNode)ApiJson.Obj(("event", ApiJson.Str(x.Event)), ("delay", ApiJson.Num(x.Delay)),
            ("type", ApiJson.Num((int)x.Type)), ("on", ApiJson.Num((int)x.On)))))),
        ("repeat", ApiJson.Num(g.Repeat)), ("sequence", ApiJson.Bool(g.Sequence)));

    // ---- camnet -----------------------------------------------------------------------------------------------------

    private static int CamNetAsync(CliArgs a)
    {
        string verb = Need(a.Positional(0), "list, link, remove or camera");
        string path = Need(a.Positional(1), "the project folder");
        switch (verb)
        {
            case "list":
                return Query(path, a, ("q", "camnets"));
            case "link":
                return Edit(path, a, Op("camnet", ("order", ApiJson.Strings(Ids(Need(a.Positional(2), "the cameras in order")))),
                    ("loop", ApiJson.Bool(a.Get("loop") == "true"))));
            case "remove":
                return Edit(path, a, Op("camnet", ("remove", ApiJson.Strings(Ids(Need(a.Positional(2), "the cameras to take out"))))));
            case "camera":
                var op = Op("camera", ("id", ApiJson.Str(Need(a.Positional(2), "the camera's id"))));
                AddValue(op, a, "name", "name");
                foreach (string limit in new[] { "pitch", "yaw" })
                {
                    if (a.Get(limit) is { } v)
                    {
                        op.Add(limit, ApiJson.Arr(v.Split(',').Select(s => ApiJson.Num(Parsing.Number(s.Trim(), "--" + limit)))));
                    }
                }

                return Edit(path, a, op);
            default:
                throw new ArgumentException($"unknown camnet op '{verb}' (list, link, remove, camera)");
        }
    }

    // ---- props and property sets ------------------------------------------------------------------------------------

    private static int PropsAsync(CliArgs a)
    {
        string verb = Need(a.Positional(0), "copy or paste");
        string path = Need(a.Positional(1), "the project folder");
        switch (verb)
        {
            case "copy":
                string id = Need(a.Positional(2), "the actor's id");
                Actor actor = EditorSession.Open(path).Project.FindActor(id) ?? throw new ApiException($"no actor '{id}'", "id");
                SvJsonObject copied = PropertyCopy.Copy(actor).ToJson();
                if (a.Get("out") is { } outFile)
                {
                    File.WriteAllText(outFile, CanonicalJson.ToPrettyString(copied) + "\n");
                }

                PrintJson(copied, a);
                return 0;
            case "paste":
                var op = Op("props.paste", ("ids", ApiJson.Strings(Ids(Need(a.Get("to"), "--to")))));
                if (a.Get("from-file") is { } file)
                {
                    op.Add("props", SvJson.Parse(File.ReadAllBytes(file), file));
                }
                else
                {
                    op.Add("from", ApiJson.Str(Need(a.Get("from"), "--from or --from-file")));
                }

                if (a.Get("keys") is { } keys)
                {
                    op.Add("keys", ApiJson.Strings(Ids(keys)));
                }

                return Edit(path, a, op);
            default:
                throw new ArgumentException($"unknown props op '{verb}' (copy, paste)");
        }
    }

    private static int PropSetAsync(CliArgs a)
    {
        string verb = Need(a.Positional(0), "list, apply, save, delete or import-ini");
        switch (verb)
        {
            case "list":
                if (a.Positional(1) is { } project)
                {
                    var q = ApiJson.Obj(("q", ApiJson.Str("propsets")));
                    AddValue(q, a, "class", "class");
                    PrintJson(MapQuery.Run(EditorSession.Open(project), q), a);
                    return 0;
                }

                PrintJson(ApiJson.Obj(("library", ApiJson.Str(PropertySets.LibraryFile)),
                    ("sets", ApiJson.Strings(PropertySets.All(null).Select(s => s.Key)))), a);
                return 0;
            case "apply":
                return Edit(Need(a.Positional(1), "the project folder"), a,
                    Op("propset", ("set", ApiJson.Str(Need(a.Positional(2), "the set (Class.Name)"))), ("ids", ApiJson.Strings(Ids(Need(a.Get("to"), "--to"))))));
            case "save":
                EditorSession session = EditorSession.Open(Need(a.Positional(1), "the project folder"));
                string id = Need(a.Positional(2), "the actor to take the values from");
                Actor actor = session.Project.FindActor(id) ?? throw new ApiException($"no actor '{id}'", "id");
                bool toMap = a.Get("map") == "true";
                PropertySet set = PropertySets.FromActor(actor, Need(a.Positional(3), "the set's name"), a.Get("keys") is { } k ? Ids(k) : null,
                    toMap ? PropertySetSource.Map : PropertySetSource.Library) with { Note = a.Get("note") };
                string file = toMap ? PropertySets.MapFile(session.Folder) ?? throw new ArgumentException("the project has no folder") : PropertySets.LibraryFile;
                PropertySets.Save(file, [set]);
                PrintJson(ApiJson.Obj(("saved", ApiJson.Str(set.Key)), ("file", ApiJson.Str(file)), ("props", set.Props.ToJson())), a);
                return 0;
            case "delete":
                string target = SetsFile(a);
                string key = Need(a.Positional(1), "the set (Class.Name)");
                bool removed = PropertySets.Delete(target, key);
                PrintJson(ApiJson.Obj(("deleted", ApiJson.Bool(removed)), ("set", ApiJson.Str(key)), ("file", ApiJson.Str(target))), a);
                return removed ? 0 : 1;
            case "import-ini":
                string ini = Need(a.Positional(1), "the PropertiesSets.ini file");
                List<PropertySet> sets = PropertySets.ImportIni(ini);
                string into = SetsFile(a);
                PropertySets.Save(into, sets);
                PrintJson(ApiJson.Obj(("imported", ApiJson.Num(sets.Count)), ("file", ApiJson.Str(into)),
                    ("sets", ApiJson.Strings(sets.Select(s => s.Key)))), a);
                return 0;
            default:
                throw new ArgumentException($"unknown propset op '{verb}' (list, apply, save, delete, import-ini)");
        }
    }

    /// <summary>The map's set file with <c>--map project</c>, else the user library.</summary>
    private static string SetsFile(CliArgs a) =>
        a.Get("map") is { } map ? PropertySets.MapFile(EditorSession.Open(map).Folder) ?? throw new ArgumentException("the project has no folder") : PropertySets.LibraryFile;

    // ---- tips, zones, modes -----------------------------------------------------------------------------------------

    private static readonly (string Option, string Field)[] s_tipOptions =
    [
        ("spy", "spy"), ("merc", "merc"), ("radius", "radius"), ("height", "height"), ("marker", "marker"), ("mesh", "mesh"),
        ("ends-game", "endsGame"), ("only-with-help", "onlyWithHelp"), ("stay", "stay"), ("end-game-delay", "endGameDelay"),
    ];

    private static int TipsAsync(CliArgs a)
    {
        string verb = Need(a.Positional(0), "list, add or set");
        string path = Need(a.Positional(1), "the project folder");
        if (verb == "list")
        {
            return Query(path, a, ("q", "tips"));
        }

        var op = verb switch
        {
            "add" => Op("tip", ("at", Triple(Need(a.Get("at"), "--at"), "--at"))),
            "set" => Op("tip", ("id", ApiJson.Str(Need(a.Positional(2), "the tip's id")))),
            _ => throw new ArgumentException($"unknown tips op '{verb}' (list, add, set)"),
        };
        foreach ((string option, string field) in s_tipOptions)
        {
            AddValue(op, a, option, field);
        }

        return Edit(path, a, op);
    }

    private static int ZoneAsync(CliArgs a)
    {
        string verb = Need(a.Positional(0), "list, add, message, set or remove");
        string path = Need(a.Positional(1), "the project folder");
        if (verb == "list")
        {
            return a.Positional(2) is { } one ? Query(path, a, ("q", "zone"), ("id", one)) : Query(path, a, ("q", "zone"));
        }

        var op = Op("zone.action", ("mission", ApiJson.Str(Need(a.Positional(2), "the mission's id"))));
        switch (verb)
        {
            case "add":
                op.Add("add", ApiJson.Str(Need(a.Positional(3), "the actor to fire")));
                AddValue(op, a, "type", "type");
                break;
            case "message":
                op.Add("message", ApiJson.Obj(("spy", ApiJson.Str(Need(a.Get("spy"), "--spy"))), ("merc", a.Get("merc") is { } merc ? ApiJson.Str(merc) : null),
                    ("seconds", a.Get("seconds") is { } s ? ApiJson.Num(Parsing.Number(s, "--seconds")) : null)));
                break;
            case "set":
                op.Add("set", ApiJson.Num(Parsing.Number(Need(a.Positional(3), "the action's index"), "index")));
                AddValue(op, a, "type", "type");
                break;
            case "remove":
                op.Add("remove", ApiJson.Num(Parsing.Number(Need(a.Positional(3), "the action's index"), "index")));
                break;
            default:
                throw new ArgumentException($"unknown zone op '{verb}' (list, add, message, set, remove)");
        }

        AddValue(op, a, "delay", "delay");
        return Edit(path, a, op);
    }

    private static int ModesAsync(CliArgs a)
    {
        string path = Need(a.Positional(0), "the project folder");
        return a.Positional(1) is { } mode ? Query(path, a, ("q", "modes"), ("mode", mode)) : Query(path, a, ("q", "modes"));
    }

    // ---- shared -----------------------------------------------------------------------------------------------------

    private static int Query(string path, CliArgs a, params (string Key, string Value)[] members)
    {
        var q = ApiJson.Obj(members.Select(m => (m.Key, (SvJsonNode?)(m.Key == "mode" ? Value(m.Value) : ApiJson.Str(m.Value)))).ToArray());
        SvJsonNode result = MapQuery.Run(EditorSession.Open(path), q);
        PrintJson(result, a);
        return result is SvJsonObject o && o["error"] is not null ? 1 : 0;
    }

    /// <summary>Runs one op as a change document on the project and saves it unless --dry-run.</summary>
    private static int Edit(string path, CliArgs a, SvJsonObject op)
    {
        EditorSession session = EditorSession.Open(path);
        bool dry = a.Has("dry-run");
        var doc = ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)), ("ops", ApiJson.Arr([op])), ("label", ApiJson.Str($"ose {ApiJson.GetString(op, "op")}")));
        ChangeResult result = ChangeApplier.Apply(session, doc, dryRun: dry, author: a.Get("author") ?? "ose");
        if (result.Ok && !dry && session.IsDirty)
        {
            session.Save();
            result.Json.Add("saved", ApiJson.Str(session.Folder!));
        }

        PrintJson(result.Json, a);
        return result.Ok ? 0 : 1;
    }

    private static SvJsonObject Op(string name, params (string Key, SvJsonNode? Value)[] members) =>
        ApiJson.Obj([("op", ApiJson.Str(name)), .. members]);

    private static void AddValue(SvJsonObject op, CliArgs a, string option, string field)
    {
        if (a.Get(option) is { } v)
        {
            op.Add(field, Value(v));
        }
    }

    /// <summary>An option value as JSON: a number, true/false, or text.</summary>
    private static SvJsonNode Value(string text) => text.ToLowerInvariant() switch
    {
        "true" => SvJsonNode.True,
        "false" => SvJsonNode.False,
        _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => ApiJson.Num(d),
        _ => ApiJson.Str(text),
    };

    private static SvJsonNode Triple(string text, string option)
    {
        double[] v = text.Split(',').Select(s => Parsing.Number(s.Trim(), option)).ToArray();
        return v.Length == 3 ? ApiJson.Arr(v.Select(ApiJson.Num)) : throw new ArgumentException($"{option} needs x,y,z");
    }

    /// <summary>JSON given inline or as a file path.</summary>
    private static SvJsonNode JsonArg(string text, string option) =>
        text.TrimStart().StartsWith('[') || text.TrimStart().StartsWith('{') ? ApiJson.Parse(text, option) : SvJson.Parse(File.ReadAllBytes(text), text);

    private static List<string> Ids(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Need(string? value, string what) => value is { Length: > 0 } v ? v : throw new ArgumentException($"give {what}");
}
