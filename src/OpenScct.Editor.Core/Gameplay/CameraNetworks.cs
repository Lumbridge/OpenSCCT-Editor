using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>One camera of the mercenaries' network: its HUD name, links and turn limits.</summary>
/// <param name="PitchLimits">Rotator units below and above the placed pitch (<c>PitchConstraint</c> Y and Z).</param>
/// <param name="YawLimits">Rotator units left and right of the placed yaw (<c>YawConstraint</c> Y and Z).</param>
public sealed record NetworkCamera(string Id, string Name, string? Next, string? Prev, bool First, Vec3 Eye, Rot Rotation,
    (int Min, int Max) PitchLimits, (int Min, int Max) YawLimits)
{
    /// <summary>The game limits the tilt only when both ends are non-zero (cam-network.md rule 7).</summary>
    public bool PitchLimited => CameraNetworks.Limits((PitchLimits.Min, PitchLimits.Max));

    /// <summary>The game limits the turn only when both ends are non-zero.</summary>
    public bool YawLimited => CameraNetworks.Limits((YawLimits.Min, YawLimits.Max));
}

/// <summary>A connected set of network cameras in ring order, with what is wrong with its links.</summary>
/// <param name="Loop">The last camera's NextCam is the first: Next and Prev go round.</param>
public sealed record CameraNetwork(IReadOnlyList<NetworkCamera> Cameras, bool Loop, IReadOnlyList<string> Problems);

/// <summary>
/// The camera network workbench's data (spec/devices/cam-network.md): SCamNetwork cameras linked by <c>NextCam</c> and
/// <c>PrevCam</c>, one marked <c>bFirstCam</c>, viewed by mercenaries from anywhere with the CamNet gadget. Networks
/// are found from links in either direction, so a half-broken ring still shows as one; ordering and rewiring keep
/// every link and first-camera flag consistent (RE+ <c>CameraNetworkModel.h</c>).
/// </summary>
public static class CameraNetworks
{
    public const string Class = "SBase.SCamNetwork";

    /// <summary>The CamNet view's field of view (degrees, horizontal), the class default.</summary>
    public const double FieldOfView = 110;

    public static bool IsNetworkCamera(Actor a) => ClassDefaults.IsA(a, Class);

    public static NetworkCamera Camera(Actor a) => new(a.Id, a.Props.GetString("CamName") ?? (ClassDefaults.Catalog.Default(a.Class, "CamName") as SvJsonString)?.Value ?? "",
        PropValues.ActorId(a.Props["NextCam"]), PropValues.ActorId(a.Props["PrevCam"]), a.Props.GetBool("bFirstCam"), a.Location, a.Rotation,
        ReadLimits(a, "PitchConstraint"), ReadLimits(a, "YawConstraint"));

    /// <summary>
    /// The camera the CamNet gadget opens on (rule 3): the first network camera in level order with bFirstCam, else
    /// the last one in the level. Null when the map has none.
    /// </summary>
    public static string? EntryCamera(MapProject p)
    {
        List<Actor> cams = p.Actors.Where(IsNetworkCamera).ToList();
        return (cams.FirstOrDefault(c => c.Props.GetBool("bFirstCam")) ?? cams.LastOrDefault())?.Id;
    }

    /// <summary>Every network in the map: cameras joined by links either way, walked from the first camera (else a
    /// camera with no PrevCam) along NextCam. Problems name broken or one-way links and wrong first-camera flags.</summary>
    public static List<CameraNetwork> Networks(MapProject p)
    {
        List<NetworkCamera> cams = p.Actors.Where(IsNetworkCamera).Select(Camera).ToList();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cams.Count; i++)
        {
            index[cams[i].Id] = i;
        }

        var neighbours = cams.Select(_ => new HashSet<int>()).ToList();
        for (int i = 0; i < cams.Count; i++)
        {
            foreach (string? link in new[] { cams[i].Next, cams[i].Prev })
            {
                if (link is not null && index.TryGetValue(link, out int j))
                {
                    neighbours[i].Add(j);
                    neighbours[j].Add(i);
                }
            }
        }

        var used = new HashSet<int>();
        var result = new List<CameraNetwork>();
        for (int seed = 0; seed < cams.Count; seed++)
        {
            if (used.Contains(seed))
            {
                continue;
            }

            var members = new SortedSet<int>();
            var pending = new Stack<int>([seed]);
            while (pending.Count > 0)
            {
                int i = pending.Pop();
                if (members.Add(i))
                {
                    foreach (int n in neighbours[i])
                    {
                        pending.Push(n);
                    }
                }
            }

            used.UnionWith(members);
            int start = members.FirstOrDefault(i => cams[i].First, members.FirstOrDefault(i => cams[i].Prev is null || !index.ContainsKey(cams[i].Prev!), members.Min));
            var order = new List<int>();
            var seen = new HashSet<int>();
            void Walk(int i)
            {
                while (members.Contains(i) && seen.Add(i))
                {
                    order.Add(i);
                    if (cams[i].Next is not { } next || !index.TryGetValue(next, out i))
                    {
                        break;
                    }
                }
            }

            Walk(start);
            foreach (int i in members)
            {
                Walk(i);
            }

            List<NetworkCamera> ordered = order.Select(i => cams[i]).ToList();
            bool loop = ordered.Count > 1 && Same(ordered[^1].Next, ordered[0].Id);
            result.Add(new CameraNetwork(ordered, loop, Problems(ordered, loop, index)));
        }

        // The gadget opens on one camera for the whole map, so a second network can never be reached.
        if (result.Count > 1 && EntryCamera(p) is { } entry)
        {
            for (int i = 0; i < result.Count; i++)
            {
                if (!result[i].Cameras.Any(c => Same(c.Id, entry)))
                {
                    result[i] = result[i] with { Problems = [.. result[i].Problems, $"mercs cannot reach these cameras: the CamNet gadget opens on {entry}, in another network"] };
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Links the cameras in this order, one undo step: each NextCam is the following camera, each PrevCam the one
    /// before, round to the start when <paramref name="loop"/>; the first gets bFirstCam and the others lose it. Every
    /// network a listed camera belongs to must be listed whole (a camera left out would point into the new chain).
    /// </summary>
    public static ReplaceObjectsCommand Link(MapProject p, IReadOnlyList<string> order, bool loop)
    {
        if (order.Count == 0)
        {
            throw new ArgumentException("give the cameras in order");
        }

        if (order.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new ArgumentException($"{twice.Key} appears twice; a camera can only be in a network once");
        }

        foreach (string id in order)
        {
            if (p.FindActor(id) is not { } a || !IsNetworkCamera(a))
            {
                throw new ArgumentException($"'{id}' is not a network camera ({Class})");
            }
        }

        var listed = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
        foreach (CameraNetwork n in Networks(p).Where(n => n.Cameras.Any(c => listed.Contains(c.Id))))
        {
            if (n.Cameras.FirstOrDefault(c => !listed.Contains(c.Id)) is { } left)
            {
                throw new ArgumentException($"{left.Id} is linked to cameras in the list: list every camera of its network");
            }
        }

        var cmd = new ReplaceObjectsCommand(loop ? $"Link {order.Count} cameras in a ring" : $"Link {order.Count} cameras in a line");
        for (int i = 0; i < order.Count; i++)
        {
            Actor before = p.FindActor(order[i])!;
            Actor after = before.Clone();
            string? next = i + 1 < order.Count ? order[i + 1] : loop && order.Count > 1 ? order[0] : null;
            string? prev = i > 0 ? order[i - 1] : loop && order.Count > 1 ? order[^1] : null;
            after.Props["NextCam"] = next is null ? null : PropValues.ActorRef(p.FindActor(next)!.Id);
            after.Props["PrevCam"] = prev is null ? null : PropValues.ActorRef(p.FindActor(prev)!.Id);
            after.Props["bFirstCam"] = i == 0 ? SvJsonNode.True : null;
            if (!CanonicalJson.ToCanonicalString(before.Props.ToJson()).Equals(CanonicalJson.ToCanonicalString(after.Props.ToJson()), StringComparison.Ordinal))
            {
                cmd.Actor(before.Clone(), after);
            }
        }

        return cmd;
    }

    /// <summary>Takes cameras out of their networks, one undo step: their own links are cleared and their neighbours
    /// are joined round them, so the rest of each ring stays whole.</summary>
    public static ReplaceObjectsCommand Remove(MapProject p, IReadOnlyCollection<string> ids)
    {
        var gone = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var cmd = new ReplaceObjectsCommand(ids.Count == 1 ? $"Take {ids.First()} out of its network" : $"Take {ids.Count} cameras out of their networks");
        foreach (CameraNetwork n in Networks(p).Where(n => n.Cameras.Any(c => gone.Contains(c.Id))))
        {
            List<string> kept = n.Cameras.Where(c => !gone.Contains(c.Id)).Select(c => c.Id).ToList();
            foreach (NetworkCamera c in n.Cameras)
            {
                Actor before = p.FindActor(c.Id)!;
                Actor after = before.Clone();
                int i = kept.FindIndex(k => Same(k, c.Id));
                string? next = i < 0 ? null : i + 1 < kept.Count ? kept[i + 1] : n.Loop && kept.Count > 1 ? kept[0] : null;
                string? prev = i < 0 ? null : i > 0 ? kept[i - 1] : n.Loop && kept.Count > 1 ? kept[^1] : null;
                after.Props["NextCam"] = next is null ? null : PropValues.ActorRef(next);
                after.Props["PrevCam"] = prev is null ? null : PropValues.ActorRef(prev);
                after.Props["bFirstCam"] = i == 0 ? SvJsonNode.True : null;
                if (!CanonicalJson.ToCanonicalString(before.Props.ToJson()).Equals(CanonicalJson.ToCanonicalString(after.Props.ToJson()), StringComparison.Ordinal))
                {
                    cmd.Actor(before.Clone(), after);
                }
            }
        }

        return cmd;
    }

    /// <summary>Sets the name the mercenaries' HUD shows for a camera (<c>CamName</c>).</summary>
    public static ReplaceObjectsCommand Rename(MapProject p, string id, string name)
    {
        if (p.FindActor(id) is not { } a || !IsNetworkCamera(a))
        {
            throw new ArgumentException($"'{id}' is not a network camera ({Class})");
        }

        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], $"Name {id} \"{name}\"", actor: x => x.Props["CamName"] = SvJsonNode.String(name));
    }

    /// <summary>True when the game applies a limit with these ends: only when both are non-zero (rule 7), so a limit
    /// with one end at 0 leaves the camera free.</summary>
    public static bool Limits((int Min, int Max) limit) => limit.Min != 0 && limit.Max != 0;

    /// <summary>Sets how far a network camera turns either side of its placed rotation (rotator units). The game
    /// applies a limit only when both ends are non-zero. Edits with the same <paramref name="mergeKey"/> in a row
    /// make one undo step.</summary>
    public static ReplaceObjectsCommand SetLimits(MapProject p, string id, (int Min, int Max)? pitch, (int Min, int Max)? yaw, string? mergeKey = null)
    {
        if (p.FindActor(id) is not { } a || !IsNetworkCamera(a))
        {
            throw new ArgumentException($"'{id}' is not a network camera ({Class})");
        }

        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], $"Turn limits of {id}", mergeKey: mergeKey, actor: x =>
        {
            if (pitch is { } pl)
            {
                x.Props["PitchConstraint"] = Constraint(pl);
            }

            if (yaw is { } yl)
            {
                x.Props["YawConstraint"] = Constraint(yl);
            }
        });
    }

    /// <summary>
    /// The direction a merc looks through the camera after turning <paramref name="pitch"/> and <paramref name="yaw"/>
    /// units from where it was placed, clamped to its limits when both ends are non-zero (rules 6 to 9 with OpenSCCT's
    /// fix: the placed direction is kept on entry).
    /// </summary>
    public static Rot Look(NetworkCamera c, int pitch, int yaw)
    {
        static int Clamp(int v, (int Min, int Max) l) => Limits(l) ? Math.Clamp(v, Math.Min(l.Min, l.Max), Math.Max(l.Min, l.Max)) : v;
        return new Rot(c.Rotation.Pitch + Clamp(pitch, c.PitchLimits), c.Rotation.Yaw + Clamp(yaw, c.YawLimits), c.Rotation.Roll);
    }

    private static (int, int) ReadLimits(Actor a, string prop)
    {
        SvJsonNode? v = a.Props[prop] ?? ClassDefaults.Catalog.Default(a.Class, prop);
        return v is SvJsonObject o ? ((int)PropValues.Num(o["Y"]), (int)PropValues.Num(o["Z"])) : (0, 0);
    }

    private static SvJsonObject Constraint((int Min, int Max) l)
    {
        var o = new SvJsonObject();
        o.Add("X", SvJsonNode.Float(0));
        o.Add("Y", SvJsonNode.Float(l.Min));
        o.Add("Z", SvJsonNode.Float(l.Max));
        return o;
    }

    private static List<string> Problems(List<NetworkCamera> ordered, bool loop, Dictionary<string, int> index)
    {
        var problems = new List<string>();
        int firsts = ordered.Count(c => c.First);
        if (ordered.Count > 1 && firsts != 1)
        {
            problems.Add(firsts == 0 ? "no camera is marked first (the gadget then opens on the last network camera in the map)"
                : $"{firsts} cameras are marked first; only the first of them in the map is used");
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            NetworkCamera c = ordered[i];
            foreach ((string what, (int Min, int Max) l) in new[] { ("tilt", c.PitchLimits), ("turn", c.YawLimits) })
            {
                if ((l.Min == 0) != (l.Max == 0))
                {
                    problems.Add($"{c.Id}: its {what} limit has one end at 0, so the game does not limit it at all");
                }
            }

            string? next = i + 1 < ordered.Count ? ordered[i + 1].Id : loop ? ordered[0].Id : null;
            string? prev = i > 0 ? ordered[i - 1].Id : loop ? ordered[^1].Id : null;
            foreach ((string? link, string? want, string field) in new[] { (c.Next, next, "NextCam"), (c.Prev, prev, "PrevCam") })
            {
                if (link is not null && !index.ContainsKey(link))
                {
                    problems.Add($"{c.Id}.{field} names {link}, which is not a network camera");
                }
                else if (!Same(link, want) && !(link is null && want is null))
                {
                    problems.Add($"{c.Id}.{field} is {link ?? "None"}, expected {want ?? "None"}");
                }
            }
        }

        return problems;
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
