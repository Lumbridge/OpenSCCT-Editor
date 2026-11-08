using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// Gameplay workbench ops: <c>mover</c> (keys, start key, times, easing, state), <c>magic</c> (an SMagicEvent's
/// timeline), <c>camnet</c> (link cameras into a network, or take them out), <c>camera</c> (a network camera's name
/// and turn limits), <c>props.paste</c> (copy properties from one actor to others), <c>propset</c> (apply a
/// property set), <c>tip</c> (zone tips with their help marker) and <c>zone.action</c> (what a mission does when
/// complete). They run the same edits as the editor's workbenches.
/// </summary>
public static partial class ChangeApplier
{
    private static string[] MoverKeys => ["id", "keys", "addKey", "deleteKey", "startKey", "moveTime", "stayOpenTime", "delayTime", "delayTimeLastKey", "otherTime", "eased", "legs", "state", "showKey"];

    private static string[] MagicKeys => ["id", "at", "tag", "groups", "stopActor"];

    private static string[] TipKeys => ["id", "at", "spy", "merc", "radius", "height", "marker", "mesh", "endsGame", "onlyWithHelp", "stay", "endGameDelay"];

    /// <summary>mover: edits one mover. <c>keys</c> replaces the keys (<c>[{"at":[x,y,z],"rot":[p,y,r]}]</c>, offsets
    /// from the base, up to 65,536 stops); <c>addKey</c> true or a world point; <c>deleteKey</c> an index; then start key, times
    /// (seconds), <c>eased</c>, <c>legs</c> (each key's own timing, <c>[{"key":2,"time":0.5,"smooth":false}]</c>: the
    /// leg from the key before in seconds and the easing at the key, null for the mover's own) and <c>state</c>
    /// (InitialState); last <c>showKey</c> draws it at a key, so a following <c>move</c> or <c>rotate</c> of the mover
    /// places that key (the start key draws it where the round starts).</summary>
    private static void MoverOp(ChangeContext ctx, SvJsonObject op)
    {
        string id = ActorId(ctx, op, "id");
        Guard("id", () => MoverEdits.Read(ctx.Project, id));
        if (op["keys"] is { } keysNode)
        {
            if (keysNode is not SvJsonArray arr)
            {
                throw new ApiException("keys is a list of {\"at\":[x,y,z],\"rot\":[p,y,r]}", "keys");
            }

            var keys = new List<MoverKey>();
            for (int i = 0; i < arr.Count; i++)
            {
                SvJsonObject k = arr[i] as SvJsonObject ?? throw new ApiException("a key is {\"at\":[x,y,z],\"rot\":[p,y,r]}", $"keys[{i}]");
                Vec3 at = k["at"] is { } a ? ApiJson.RequireVec(a, $"keys[{i}].at") : Vec3.Zero;
                Rot turn = Rot.Zero;
                if (k["rot"] is { } r && !ApiJson.TryRot(r, out turn))
                {
                    throw new ApiException("rot is [pitch, yaw, roll] in rotator units", $"keys[{i}].rot");
                }

                keys.Add(new MoverKey(at, turn));
            }

            Track(ctx, Guard("keys", () => MoverEdits.SetKeys(ctx.Project, id, keys)));
        }

        if (op["addKey"] is { } add && !(add.Kind == SvJsonKind.False))
        {
            Vec3? at = add.Kind == SvJsonKind.True ? null : ctx.Point(add, "addKey");
            Track(ctx, Guard("addKey", () => MoverEdits.AddKey(ctx.Project, id, at)));
        }

        if (op["deleteKey"] is { } del)
        {
            int index = (int)ApiJson.RequireNumber(del, "deleteKey");
            Track(ctx, Guard("deleteKey", () => MoverEdits.DeleteKey(ctx.Project, id, index)));
        }

        if (op["startKey"] is { } start)
        {
            int index = (int)ApiJson.RequireNumber(start, "startKey");
            Track(ctx, Guard("startKey", () => MoverEdits.SetStartKey(ctx.Project, id, index)));
        }

        foreach ((string field, string prop) in new[] { ("moveTime", "MoveTime"), ("stayOpenTime", "StayOpenTime"), ("delayTime", "DelayTime"),
            ("delayTimeLastKey", "DelayTimeLastKey"), ("otherTime", "OtherTime") })
        {
            if (op[field] is { } t)
            {
                double seconds = ApiJson.RequireNumber(t, field);
                Track(ctx, Guard(field, () => MoverEdits.SetTime(ctx.Project, id, prop, seconds)));
            }
        }

        if (ApiJson.RequireBool(op, "eased") is { } eased)
        {
            Track(ctx, MoverEdits.SetEased(ctx.Project, id, eased));
        }

        if (op["legs"] is { } legsNode)
        {
            MoverLegsOp(ctx, id, legsNode);
        }

        if (ApiJson.GetString(op, "state") is { } state)
        {
            Track(ctx, Guard("state", () => MoverEdits.SetState(ctx.Project, id, state)));
        }

        if (op["showKey"] is { } show)
        {
            int index = (int)ApiJson.RequireNumber(show, "showKey");
            Track(ctx, Guard("showKey", () => MoverEdits.ShowKey(ctx.Project, id, index)));
        }
    }

    /// <summary>The mover op's <c>legs</c>: per key, <c>time</c> and <c>smooth</c> set when given, cleared by null, kept
    /// when left out.</summary>
    private static void MoverLegsOp(ChangeContext ctx, string id, SvJsonNode node)
    {
        if (node is not SvJsonArray list)
        {
            throw new ApiException("legs is a list of {\"key\":1,\"time\":2.5,\"smooth\":true}", "legs");
        }

        for (int i = 0; i < list.Count; i++)
        {
            string field = $"legs[{i}]";
            SvJsonObject leg = list[i] as SvJsonObject ?? throw new ApiException("a leg is {\"key\":1,\"time\":2.5,\"smooth\":true}", field);
            double keyNumber = ApiJson.RequireNumber(leg["key"] ?? throw new ApiException("give the key", field + ".key"), field + ".key");
            if (keyNumber != Math.Floor(keyNumber))
            {
                throw new ApiException($"key is a key number, not {keyNumber}", field + ".key");
            }

            if (leg["time"] is null && leg["smooth"] is null)
            {
                throw new ApiException("a leg sets time, smooth or both", field);
            }

            int key = (int)keyNumber;
            if (leg["time"] is { } time)
            {
                double? seconds = ApiJson.IsNull(time) ? null : ApiJson.RequireNumber(time, field + ".time");
                Track(ctx, Guard(field + ".time", () => MoverEdits.SetLegTime(ctx.Project, id, key, seconds)));
            }

            if (leg["smooth"] is { } smooth)
            {
                bool? value = smooth.Kind switch
                {
                    SvJsonKind.Null => null,
                    SvJsonKind.True => true,
                    SvJsonKind.False => false,
                    _ => throw new ApiException("smooth is true, false or null (the mover's own glide)", field + ".smooth"),
                };
                Track(ctx, Guard(field + ".smooth", () => MoverEdits.SetKeySmooth(ctx.Project, id, key, value)));
            }
        }
    }

    /// <summary>magic: sets an SMagicEvent's <c>groups</c> (<c>[{"actions":[{"event","delay","type","on"}],"repeat",
    /// "sequence"}]</c>) and <c>stopActor</c>. With no <c>id</c>, or a new <c>$symbol</c>, it makes a new one at
    /// <c>at</c>, tagged <c>tag</c>; an <c>id</c> that names no actor is an error.</summary>
    private static void MagicOp(ChangeContext ctx, SvJsonObject op)
    {
        string? requested = ApiJson.GetString(op, "id");
        string? id = requested is null ? null : ctx.TryActorId(requested);
        if (id is null && requested is not null && !requested.StartsWith('$'))
        {
            throw new ApiException($"no actor '{requested}' (leave id out, or give a new $symbol, to make an SMagicEvent)", "id");
        }

        List<MagicGroup>? groups = op["groups"] is { } g ? ReadGroups(g) : null;
        if (id is null)
        {
            Vec3 at = op["at"] is { } point ? ctx.Point(point, "at") : throw new ApiException("a new SMagicEvent needs at (or give an existing id)", "at");
            string tag = ApiJson.GetString(op, "tag") ?? throw new ApiException("a new SMagicEvent needs the tag its senders fire", "tag");
            AddObjectsCommand create = Guard("groups", () => MagicEvents.Create(ctx.Project, at, tag, groups ?? []));
            Track(ctx, create);
            id = create.Added.First().Id;
            if (requested is not null && requested.StartsWith('$'))
            {
                ctx.Bind(requested, id, "id");
            }
        }
        else
        {
            if (ctx.Project.FindActor(id) is not { } a || !MagicEvents.IsMagicEvent(a))
            {
                throw new ApiException($"'{requested}' is not an SMagicEvent", "id");
            }

            if (groups is not null)
            {
                Track(ctx, Guard("groups", () => MagicEvents.SetGroups(ctx.Project, id, groups)));
            }

            if (ApiJson.GetString(op, "tag") is { } tag)
            {
                ctx.Edit([ObjectRef.Actor(id)], $"Tag {id}", actor: x => x.Props.Set("Tag", SvJsonNode.String(tag)));
            }
        }

        if (op["stopActor"] is { } stop)
        {
            string? stopId = ApiJson.IsNull(stop) ? null : ActorId(ctx, op, "stopActor");
            Track(ctx, Guard("stopActor", () => MagicEvents.SetStopActor(ctx.Project, id, stopId)));
        }
    }

    private static List<MagicGroup> ReadGroups(SvJsonNode node)
    {
        if (node is not SvJsonArray arr)
        {
            throw new ApiException("groups is a list of {\"actions\":[...],\"repeat\":0,\"sequence\":false}", "groups");
        }

        var groups = new List<MagicGroup>();
        for (int i = 0; i < arr.Count; i++)
        {
            string field = $"groups[{i}]";
            SvJsonObject g = arr[i] as SvJsonObject ?? throw new ApiException("a group is {\"actions\":[...]}", field);
            var actions = new List<MagicAction>();
            SvJsonArray list = g["actions"] as SvJsonArray ?? [];
            for (int j = 0; j < list.Count; j++)
            {
                string f = $"{field}.actions[{j}]";
                SvJsonObject x = list[j] as SvJsonObject ?? throw new ApiException("an action is {\"event\":\"Door1\",\"delay\":1.5}", f);
                string ev = ApiJson.GetString(x, "event") ?? throw new ApiException("an action needs event (the Tag it fires)", f + ".event");
                actions.Add(new MagicAction(ev, ApiJson.Number(x, "delay", 0), MagicTypeOf(x["type"], f + ".type"), MagicTypeOf(x["on"], f + ".on")));
            }

            groups.Add(new MagicGroup(actions, (int)ApiJson.Number(g, "repeat", 0), ApiJson.GetBool(g, "sequence", false)));
        }

        return groups;
    }

    /// <summary>An EEventType by name (trigger, untrigger, triggerUntrigger, untriggerTrigger, or EVT_...) or number.</summary>
    private static MagicType MagicTypeOf(SvJsonNode? node, string field)
    {
        if (node is null)
        {
            return MagicType.Trigger;
        }

        if (ApiJson.TryNumber(node, out double n) && n is >= 0 and <= 3)
        {
            return (MagicType)(int)n;
        }

        string text = (node as SvJsonString)?.Value.Replace("EVT_", "", StringComparison.OrdinalIgnoreCase) ?? "";
        return Enum.TryParse(text, ignoreCase: true, out MagicType t) && Enum.IsDefined(t)
            ? t
            : throw new ApiException("type is trigger, untrigger, triggerUntrigger or untriggerTrigger", field);
    }

    /// <summary>camnet: <c>order</c> links cameras into one network in that order (whole networks only; <c>loop</c>
    /// closes the ring); <c>remove</c> takes cameras out, joining their neighbours.</summary>
    private static void CamNetOp(ChangeContext ctx, SvJsonObject op)
    {
        if (ApiJson.StringList(op["remove"], "remove") is { } remove)
        {
            List<string> ids = remove.Select((s, i) => ctx.ResolveId(s, $"remove[{i}]")).ToList();
            Track(ctx, Guard("remove", () => CameraNetworks.Remove(ctx.Project, ids)));
        }

        if (ApiJson.StringList(op["order"], "order") is { } order)
        {
            List<string> ids = order.Select((s, i) => ctx.ResolveId(s, $"order[{i}]")).ToList();
            bool loop = ApiJson.GetBool(op, "loop", false);
            Track(ctx, Guard("order", () => CameraNetworks.Link(ctx.Project, ids, loop)));
        }
    }

    /// <summary>camera: a network camera's HUD <c>name</c> and turn limits (<c>pitch</c>/<c>yaw</c> as
    /// <c>[below, above]</c> rotator units from the placed rotation; the game limits only when both are non-zero, so
    /// a 0 at either end leaves it free).</summary>
    private static void CameraOp(ChangeContext ctx, SvJsonObject op)
    {
        string id = ActorId(ctx, op, "id");
        if (ApiJson.GetString(op, "name") is { } name)
        {
            Track(ctx, Guard("name", () => CameraNetworks.Rename(ctx.Project, id, name)));
        }

        (int, int)? pitch = Range(op, "pitch"), yaw = Range(op, "yaw");
        if (pitch is not null || yaw is not null)
        {
            Track(ctx, Guard("pitch", () => CameraNetworks.SetLimits(ctx.Project, id, pitch, yaw)));
        }
    }

    private static (int, int)? Range(SvJsonObject op, string key)
    {
        if (op[key] is null)
        {
            return null;
        }

        return op[key] is SvJsonArray { Count: 2 } a && ApiJson.TryNumber(a[0], out double min) && ApiJson.TryNumber(a[1], out double max)
            ? ((int)min, (int)max)
            : throw new ApiException($"{key} is [below, above] in rotator units", key);
    }

    /// <summary>props.paste: copies stored values onto the targets, from the actor <c>from</c> in this map or from
    /// <c>props</c>, an <c>osmap.props/1</c> document (what <c>ose props copy</c> prints and the editor's Copy
    /// Properties puts on the clipboard, so values move between maps). <c>keys</c> picks the properties (default: all
    /// but identity, placement and links to other actors). Values a target's class lacks are warnings.</summary>
    private static void PastePropsOp(ChangeContext ctx, SvJsonObject op)
    {
        CopiedProperties copy = op["props"] is { } doc
            ? CopiedProperties.FromJson(doc) ?? throw new ApiException($"props is an {CopiedProperties.Format} document", "props")
            : PropertyCopy.Copy(ctx.Project.FindActor(ActorId(ctx, op, "from"))!);
        List<string> keys = ApiJson.StringList(op["keys"], "keys") ?? PropertyCopy.DefaultKeys(copy);
        if (keys.FirstOrDefault(k => !copy.Props.TryGet(k, out _)) is { } missing)
        {
            throw new ApiException($"{copy.From} has no stored '{missing}' (it stores {string.Join(", ", copy.Props.Select(kv => kv.Key))})", "keys");
        }

        var skipped = new List<string>();
        Track(ctx, PropertyCopy.Paste(ctx.Project, copy, keys, Actors(ctx, op), skipped));
        if (skipped.Count > 0)
        {
            ctx.Warn("keys", "not pasted (the class has no such property): " + string.Join(", ", skipped));
        }
    }

    /// <summary>propset: applies the property set <c>set</c> (<c>SCamera.blinde</c>: the map's sets, then the user
    /// library) to the targets.</summary>
    private static void PropSetOp(ChangeContext ctx, SvJsonObject op)
    {
        string key = ApiJson.GetString(op, "set") ?? throw new ApiException("set is the property set (Class.Name)", "set");
        PropertySet set = PropertySets.Find(key, ctx.Folder)
            ?? throw new ApiException($"no property set '{key}' (sets: {string.Join(", ", PropertySets.All(ctx.Folder).Select(s => s.Key))})", "set");
        var skipped = new List<string>();
        Track(ctx, PropertySets.Apply(ctx.Project, set, Actors(ctx, op), skipped));
        if (skipped.Count > 0)
        {
            ctx.Warn("set", $"not a {InteropShort(set.Class)}: {string.Join(", ", skipped)}");
        }
    }

    /// <summary>tip: edits the zone tip <c>id</c>, or places a new one at <c>at</c> (with its help marker 100 below
    /// unless <c>marker</c> is false; <c>endsGame</c> makes an SEndZoneTip).</summary>
    private static void TipOp(ChangeContext ctx, SvJsonObject op)
    {
        string? requested = ApiJson.GetString(op, "id");
        string? id = requested is null ? null : ctx.TryActorId(requested);
        double? radius = op["radius"] is { } r ? ApiJson.RequireNumber(r, "radius") : null;
        double? height = op["height"] is { } h ? ApiJson.RequireNumber(h, "height") : null;
        bool isNew = id is null;
        if (id is null)
        {
            Vec3 at = op["at"] is { } point ? ctx.Point(point, "at") : throw new ApiException("a new tip needs at (or give an existing id)", "at");
            string spy = ApiJson.GetString(op, "spy") ?? throw new ApiException("a new tip needs spy (the hint text)", "spy");
            bool marker = ApiJson.GetBool(op, "marker", true);
            string? mesh = ApiJson.GetString(op, "mesh")
                ?? (marker ? HelpTips.DefaultMarkerMesh(ctx.Project, () => AssetCatalog.For(EditorServices.ContentRoot, ctx.Folder)) : null);
            AddObjectsCommand add = Guard("at", () => HelpTips.Place(ctx.Project, at, spy, ApiJson.GetString(op, "merc"), radius ?? 150, height ?? 100,
                marker, mesh, ApiJson.GetBool(op, "endsGame", false)));
            Track(ctx, add);
            id = add.Added.First().Id;
            if (requested is not null && requested.StartsWith('$'))
            {
                ctx.Bind(requested, id, "id");
            }
        }

        bool? help = ApiJson.RequireBool(op, "onlyWithHelp"), stay = ApiJson.RequireBool(op, "stay");
        int? delay = op["endGameDelay"] is { } d ? (int)ApiJson.RequireNumber(d, "endGameDelay") : null;
        // A new tip already has its texts and size; what is left is the flags.
        if (!isNew || help is not null || stay is not null || delay is not null)
        {
            string tipId = id;
            Track(ctx, Guard("id", () => HelpTips.Set(ctx.Project, tipId, isNew ? null : ApiJson.GetString(op, "spy"), isNew ? null : ApiJson.GetString(op, "merc"),
                isNew ? null : radius, isNew ? null : height, help, stay, delay)));
        }
    }

    /// <summary>zone.action: on a mission's completion, <c>add</c> fires an actor (after <c>delay</c> seconds, by
    /// <c>type</c>), <c>message</c> shows a HUD message (<c>{"spy","merc","seconds"}</c>), <c>set</c> gives the action
    /// at that index a new <c>delay</c> or <c>type</c> (what it leaves out stays), <c>remove</c> drops the action at that index (indexes as the
    /// query <c>zone</c> lists them).</summary>
    private static void ZoneActionOp(ChangeContext ctx, SvJsonObject op)
    {
        string mission = ActorId(ctx, op, "mission");
        double delay = ApiJson.Number(op, "delay", 0);
        if (op["set"] is { } setIndex)
        {
            // set changes only what the op gives; the rest of the action stays.
            int i = (int)ApiJson.RequireNumber(setIndex, "set");
            double? newDelay = op["delay"] is { } d ? ApiJson.RequireNumber(d, "delay") : null;
            MagicType? type = op["type"] is { } t ? MagicTypeOf(t, "type") : null;
            Track(ctx, Guard("set", () => ZoneActions.SetTiming(ctx.Project, GameplayGraph.Build(ctx.Project), mission, i, newDelay, type)));
        }

        if (op["remove"] is { } index)
        {
            int i = (int)ApiJson.RequireNumber(index, "remove");
            Track(ctx, Guard("remove", () => ZoneActions.Remove(ctx.Project, GameplayGraph.Build(ctx.Project), mission, i)));
        }

        if (op["add"] is not null)
        {
            string receiver = ActorId(ctx, op, "add");
            MagicType type = MagicTypeOf(op["type"], "type");
            Track(ctx, Guard("add", () => ZoneActions.Add(ctx.Project, mission, [receiver], delay, type)));
        }

        if (op["message"] is { } m)
        {
            SvJsonObject msg = m as SvJsonObject ?? throw new ApiException("message is {\"spy\":\"...\",\"merc\":\"...\",\"seconds\":6}", "message");
            string spy = ApiJson.GetString(msg, "spy") ?? throw new ApiException("message needs spy (the spies' text)", "message.spy");
            Track(ctx, Guard("message", () => ZoneActions.AddMessage(ctx.Project, mission, spy, ApiJson.GetString(msg, "merc") ?? spy,
                (int)ApiJson.Number(msg, "seconds", 6), delay)));
        }
    }

    private static string InteropShort(string cls) => cls[(cls.LastIndexOf('.') + 1)..];

    /// <summary>An existing actor named by an op field (id or symbol).</summary>
    private static string ActorId(ChangeContext ctx, SvJsonObject op, string field)
    {
        string text = ApiJson.GetString(op, field) ?? throw new ApiException($"give {field}", field);
        return ctx.TryActorId(text) ?? throw new ApiException($"no actor '{text}'", field);
    }

    private static List<string> Actors(ChangeContext ctx, SvJsonObject op) =>
        ctx.Targets(op).Where(r => r.Kind == ObjectKind.Actor).Select(r => r.Id).ToList();

    /// <summary>Runs a workbench edit, recording what it changes and adds for the change summary, and stamps
    /// provenance on those objects (the workbenches build their own commands, which do not stamp).</summary>
    private static void Track(ChangeContext ctx, IEditCommand cmd)
    {
        var touched = new List<ObjectRef>();
        void Note(IEditCommand c)
        {
            switch (c)
            {
                case CompositeCommand composite:
                    foreach (IEditCommand part in composite.Parts)
                    {
                        Note(part);
                    }

                    break;
                case ReplaceObjectsCommand replace:
                    foreach (ObjectRef r in replace.Changed)
                    {
                        ctx.Touch(r);
                        touched.Add(r);
                    }

                    break;
                case AddObjectsCommand add:
                    foreach (ObjectRef r in add.Added)
                    {
                        ctx.Created(r);
                        touched.Add(r);
                    }

                    break;
            }
        }

        if (cmd is ReplaceObjectsCommand { IsEmpty: true })
        {
            return;
        }

        Note(cmd);
        ctx.Run(cmd, touchesBrushes: false);
        // A later part of the same edit may have deleted what an earlier part touched (a relay a removal empties).
        ctx.Edit([.. touched.Distinct().Where(r => ctx.Project.MetaOf(r) is not null)], "Stamp", brush: _ => { }, actor: _ => { }, ge: _ => { });
    }

    /// <summary>Turns a workbench's refusal into an error on the op's field.</summary>
    private static T Guard<T>(string field, Func<T> edit)
    {
        try
        {
            return edit();
        }
        catch (ArgumentException ex)
        {
            throw new ApiException(ex.Message, field);
        }
    }
}
