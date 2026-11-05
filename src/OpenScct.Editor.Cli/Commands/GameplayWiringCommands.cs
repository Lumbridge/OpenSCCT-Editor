using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary>
/// <c>ose wire</c> (link a sender to a receiver, unlink, rename a tag) and <c>ose security</c> (list devices with what
/// they watch, place one on the nearest wall, snap devices to walls): the editor's wiring and security workbench
/// for scripts. Edits are one undo step each and are saved unless <c>--dry-run</c> is given.
/// </summary>
internal static class GameplayWiringCommands
{
    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("wire", "wire link <project> --from A --to B[,C...] | wire unlink <project> --ids a,b | wire unlink <project> --from A --to B | " +
            "wire tag <project> --old X --new Y   ([--dry-run])",
            "Event wiring, one undo step each. link makes A fire B (A.Event = B's Tag, giving B its own Tag when its tag is " +
            "shared; several receivers share the first one's Tag); an alarm instead adds a mover to MoversToLock, an objective " +
            "trigger to ObjectiveTriggersToLock, or anything else to its Events. unlink --ids clears Event (and an alarm's " +
            "Events); unlink --from --to removes just the links from A to B. tag renames a Tag on every actor that carries " +
            "it and every event that names it.",
            WireAsync),
        new("security", "security list <project> [--json] | security place <project> --kind camera|laser|motionsensor|presencetrigger|alarm " +
            "--at x,y,z [--reach 512] | security snap <project> --ids a,b [--reach 512]   (edits: [--dry-run])",
            "Security devices. list shows each camera's cone, range and sweep, each laser's beam (stopped by walls), " +
            "presence and mine radii and the volumes motion sensors watch. place puts a device on the wall nearest a point, " +
            "facing out; snap moves devices onto their nearest wall.", SecurityAsync),
    ];

    private static Task<int> WireAsync(CliArgs a)
    {
        EnsureServices();
        string op = a.Positional(0)?.ToLowerInvariant() ?? throw new ArgumentException("give an op: link, unlink or tag");
        EditorSession session = EditorSession.Open(a.Positional(1) ?? throw new ArgumentException("give the project folder"));
        MapProject p = session.Project;
        (ReplaceObjectsCommand? cmd, string what) = op switch
        {
            "link" => Link(p, Need(a, "from"), Need(a, "to")),
            "unlink" when a.Get("from") is not null => (Wiring.Unlink(p, Need(a, "from"), Need(a, "to")), $"unlinked {a.Get("from")} from {a.Get("to")}"),
            "unlink" => (Wiring.Unlink(p, IdList(a)), "unlinked"),
            "tag" => (Wiring.RenameTag(p, Need(a, "old"), Need(a, "new")), $"tag {a.Get("old")} renamed to {a.Get("new")}"),
            _ => throw new ArgumentException($"unknown wire op '{op}' (link, unlink, tag)"),
        };
        return Task.FromResult(Save(session, a, cmd, what));
    }

    private static (ReplaceObjectsCommand?, string) Link(MapProject p, string from, string to)
    {
        if (new[] { from }.Concat(to.Split(',', StringSplitOptions.TrimEntries)).FirstOrDefault(id => p.FindActor(id) is null) is { } unknown)
        {
            throw new ArgumentException($"no actor '{unknown}'");
        }

        List<string> receivers = to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return (Wiring.Link(p, from, receivers), Wiring.Describe(p, from, receivers));
    }

    private static Task<int> SecurityAsync(CliArgs a)
    {
        EnsureServices();
        string op = a.Positional(0)?.ToLowerInvariant() ?? throw new ArgumentException("give an op: list, place or snap");
        EditorSession session = EditorSession.Open(a.Positional(1) ?? throw new ArgumentException("give the project folder"));
        return Task.FromResult(op switch
        {
            "list" => List(session, a),
            "place" => Place(session, a),
            "snap" => Snap(session, a),
            _ => throw new ArgumentException($"unknown security op '{op}' (list, place, snap)"),
        });
    }

    private static int List(EditorSession session, CliArgs a)
    {
        MapProject p = session.Project;
        TraversalWorld world = TraversalWorld.Build(p, session.Folder);
        var items = new SvJsonArray();
        foreach (Actor d in p.Actors)
        {
            if (SecurityDevices.KindOf(d) is not { } kind)
            {
                continue;
            }

            var o = new SvJsonObject { { "id", SvJsonNode.String(d.Id) }, { "kind", SvJsonNode.String(kind.ToString()) }, { "at", Vec(d.Location) } };
            string detail = "";
            switch (kind)
            {
                case DeviceKind.Camera:
                    CameraView v = SecurityDevices.Camera(d);
                    o.Add("halfAngle", Num(v.HalfAngleDegrees));
                    o.Add("range", Num(v.Range));
                    o.Add("sweep", Num(v.SweepDegrees));
                    detail = $"cone {Inv(v.HalfAngleDegrees * 2)} deg, range {Inv(v.Range)}, sweep +/-{Inv(v.SweepDegrees)} deg";
                    break;
                case DeviceKind.Laser:
                    (Vec3 from, Vec3 to) = SecurityDevices.LaserBeam(d, world);
                    o.Add("beamEnd", Vec(to));
                    detail = $"beam {Inv((to - from).Length)} long";
                    break;
                case DeviceKind.MotionSensor:
                    List<string> volumes = SecurityDevices.SensorVolumes(d);
                    o.Add("volumes", Array(volumes));
                    detail = volumes.Count == 0 ? "watches no volume" : "watches " + string.Join(", ", volumes);
                    break;
                case DeviceKind.PresenceTrigger or DeviceKind.Mine when SecurityDevices.Radius(d) is { } r:
                    o.Add("radius", Num(r));
                    detail = $"radius {Inv(r)}";
                    break;
            }

            items.Add(o);
            if (!a.Has("json"))
            {
                Console.WriteLine($"{d.Id,-28} {kind,-16} {detail}");
            }
        }

        if (a.Has("json"))
        {
            PrintJson(items, a);
        }
        else
        {
            Console.WriteLine($"{items.Count} devices");
        }

        return 0;
    }

    private static int Place(EditorSession session, CliArgs a)
    {
        string kind = Need(a, "kind");
        DeviceSpec spec = SecurityDevices.Placeable.FirstOrDefault(d => d.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"unknown --kind '{kind}' ({string.Join(", ", SecurityDevices.Placeable.Select(d => d.Kind.ToString().ToLowerInvariant()))})");
        Vec3 at = Point(Need(a, "at"));
        TraversalWorld world = TraversalWorld.Build(session.Project, session.Folder);
        if (SecurityDevices.SnapToWall(world, spec, at, Reach(a)) is not { } spot)
        {
            Console.Error.WriteLine($"no wall within {Inv(Reach(a))} of {V(at)}");
            return 1;
        }

        AddObjectsCommand add = SecurityDevices.Place(session.Project, spec, spot.Location, spot.Rotation);
        session.Execute(add);
        if (!a.Has("dry-run"))
        {
            session.Save();
        }

        Actor actor = session.Project.FindActor(add.Added.First().Id)!;
        Console.WriteLine($"placed {actor.Id} at {V(actor.Location)} facing {actor.Rotation}" + (a.Has("dry-run") ? " (not saved)" : " (saved)"));
        return 0;
    }

    private static int Snap(EditorSession session, CliArgs a)
    {
        MapProject p = session.Project;
        TraversalWorld world = TraversalWorld.Build(p, session.Folder);
        var missed = new List<string>();
        List<string> ids = IdList(a);
        if (ids.FirstOrDefault(id => p.FindActor(id) is null) is { } unknown)
        {
            throw new ArgumentException($"no actor '{unknown}'");
        }

        ReplaceObjectsCommand cmd = SecurityDevices.Snap(p, world, ids, missed, Reach(a));
        int code = Save(session, a, cmd, "snapped to walls");
        if (missed.Count > 0)
        {
            Console.WriteLine($"  no wall near: {string.Join(", ", missed)}");
        }

        return code;
    }

    private static int Save(EditorSession session, CliArgs a, ReplaceObjectsCommand? cmd, string what)
    {
        if (cmd is null || cmd.IsEmpty)
        {
            Console.WriteLine($"nothing to change ({what})");
            return 0;
        }

        session.Execute(cmd);
        if (!a.Has("dry-run"))
        {
            session.Save();
        }

        Console.WriteLine($"{cmd.Name}: {what}" + (a.Has("dry-run") ? " (not saved)" : " (saved)"));
        return 0;
    }

    private static string Need(CliArgs a, string key) => a.Get(key) is { Length: > 0 } v ? v : throw new ArgumentException($"give --{key}");

    private static List<string> IdList(CliArgs a) =>
        Need(a, "ids").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static double Reach(CliArgs a) => a.Get("reach") is { } s ? Parsing.Number(s, "--reach") : 512;

    private static Vec3 Point(string text)
    {
        double[] v = text.Split(',').Select(s => Parsing.Number(s.Trim(), "--at")).ToArray();
        return v.Length == 3 ? new Vec3(v[0], v[1], v[2]) : throw new ArgumentException("--at needs x,y,z");
    }

    private static SvJsonArray Vec(Vec3 v) => new([Num(v.X), Num(v.Y), Num(v.Z)]);

    private static string V(Vec3 v) => $"({Inv(v.X)}, {Inv(v.Y)}, {Inv(v.Z)})";
}
