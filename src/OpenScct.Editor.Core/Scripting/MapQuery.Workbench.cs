using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>The gameplay workbenches' queries: what the mover, SMagicEvent, camera network, property set, tip, zone
/// and game mode workbenches show.</summary>
public static partial class MapQuery
{
    /// <summary>mover: a mover's keys (offsets and world poses, the time of the leg into each and whether it eases
    /// there, with the key's own settings when it has them), times, state, one activation's timeline with the time each
    /// key is reached, and what triggers it.</summary>
    private static SvJsonNode Mover(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        string id = QueryActor(ctx, q);
        MoverSetup m = s.Project.FindActor(id) is { } a && MoverBase.IsMover(a) ? MoverSetup.Read(a) : throw new ApiException($"'{id}' is not a mover", "id");
        MoverTimeline timeline = MoverTimeline.Build(m);
        IReadOnlyList<double?> reached = timeline.KeyTimes();
        var keys = new SvJsonArray();
        for (int i = 0; i < m.Keys.Count; i++)
        {
            MoverPose pose = m.KeyPose(i);
            OpenScct.Core.Engine.MoverKeyTiming own = m.Legs is { } legs && i < legs.Keys.Count ? legs.Keys[i] : default;
            keys.Add(ApiJson.Obj(("offset", ApiJson.Vec(m.Keys[i].Offset)), ("rot", ApiJson.Rotator(m.Keys[i].Turn)),
                ("world", ApiJson.Vec(pose.Location)), ("reached", reached[i] is { } t ? ApiJson.Num(t) : null),
                ("legTime", i > 0 ? ApiJson.Num(m.LegTime(i - 1, i)) : null), ("smooth", ApiJson.Bool(m.SmoothAt(i))),
                ("ownTime", own.Time is { } ownTime ? ApiJson.Num(ownTime) : null), ("ownSmooth", own.Smooth is { } ownSmooth ? ApiJson.Bool(ownSmooth) : null)));
        }

        MoverTriggers triggers = MoverTriggers.Of(s.Project, GameplayGraph.Build(s.Project), s.Project.FindActor(id)!);
        return ApiJson.Obj(("id", ApiJson.Str(id)), ("keys", keys), ("maxKeys", ApiJson.Num(MoverSetup.MaxKeys)),
            ("startKey", ApiJson.Num(m.StartKey)), ("moveTime", ApiJson.Num(m.MoveTime)), ("stayOpenTime", ApiJson.Num(m.StayOpenTime)),
            ("delayTime", ApiJson.Num(m.DelayTime)), ("delayTimeLastKey", ApiJson.Num(m.DelayTimeLastKey)), ("otherTime", ApiJson.Num(m.OtherTime)),
            ("eased", ApiJson.Bool(m.Eased)), ("state", ApiJson.Str(m.State)), ("stateTitle", ApiJson.Str(m.StateInfo?.Title ?? m.State)),
            ("tag", ApiJson.Str(m.Tag)),
            ("timeline", ApiJson.Arr(timeline.Steps.Select(x => ApiJson.Obj(("start", ApiJson.Num(x.Start)), ("end", ApiJson.Num(x.End)),
                ("from", ApiJson.Num(x.From)), ("to", ApiJson.Num(x.To)), ("label", ApiJson.Str(x.Label)))))),
            ("duration", ApiJson.Num(timeline.Duration)), ("openAt", timeline.OpenAt is { } open ? ApiJson.Num(open) : null),
            ("problems", ApiJson.Strings(m.Problems())),
            ("shownAtKey", MoverEdits.ShownKey(s.Project.FindActor(id)!) is { } shown ? ApiJson.Num(shown) : null),
            ("triggeredBy", ApiJson.Arr(triggers.Senders.Select(l => ApiJson.Obj(("from", ApiJson.Str(l.From)), ("via", ApiJson.Str(l.Via)))))),
            ("lockedBy", ApiJson.Arr(triggers.Locks.Select(l => ApiJson.Obj(("from", ApiJson.Str(l.From)), ("via", ApiJson.Str(l.Via)))))),
            ("overrides", ApiJson.Arr(triggers.Overrides.Select(o => ApiJson.Obj(("sender", ApiJson.Str(o.Sender)), ("state", ApiJson.Str(o.State)),
                ("moveTime", ApiJson.Num(o.MoveTime)))))));
    }

    /// <summary>magic: an SMagicEvent's groups, what each action reaches, and the firings of activations played
    /// through the game's logic (<c>play</c>: <c>[{"t":0,"trigger":true}]</c>, default one trigger at 0); then what they
    /// do to the map by <c>at</c> seconds (default: once everything has played), followed through other SMagicEvents
    /// (<see cref="EventPreview"/>): every event delivered, the trigger lights they switch (on at that time and at the
    /// start) and the movers a trigger has started.</summary>
    private static SvJsonNode Magic(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        string id = QueryActor(ctx, q);
        Actor a = s.Project.FindActor(id)!;
        if (!MagicEvents.IsMagicEvent(a))
        {
            throw new ApiException($"'{id}' is not an SMagicEvent", "id");
        }

        GameplayGraph graph = GameplayGraph.Build(s.Project);
        List<MagicGroup> groups = MagicEvents.Read(a);
        var activations = new List<MagicActivation>();
        if (q["play"] is SvJsonArray play)
        {
            foreach (SvJsonObject x in play.OfType<SvJsonObject>())
            {
                activations.Add(new MagicActivation(ApiJson.Number(x, "t", 0), ApiJson.GetBool(x, "trigger", true), ApiJson.GetBool(x, "fromStopActor", false)));
            }
        }
        else
        {
            activations.Add(new MagicActivation(0));
        }

        List<MagicFiring> firings = MagicEvents.Simulate(groups, activations);
        double at = q["at"] is { } atNode ? ApiJson.RequireNumber(atNode, "at") : double.MaxValue;
        EventPreview preview = EventPreview.Build(s.Project, graph, id, activations);
        EventState state = preview.At(at);
        var reachedLights = state.Deliveries.Select(d => d.Receiver).Where(state.Lights.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase);
        return ApiJson.Obj(("id", ApiJson.Str(id)), ("tag", ApiJson.Str(a.Tag)),
            ("stopActor", PropValues.ActorId(a.Props["StopActor"]) is { } stop ? ApiJson.Str(stop) : null),
            ("groups", ApiJson.Arr(groups.Select(g => ApiJson.Obj(("repeat", ApiJson.Num(g.Repeat)), ("sequence", ApiJson.Bool(g.Sequence)),
                ("actions", ApiJson.Arr(g.Actions.Select(x => ApiJson.Obj(("event", ApiJson.Str(x.Event)), ("delay", ApiJson.Num(x.Delay)),
                    ("type", ApiJson.Str(MagicEvents.TypeNames[(int)x.Type])), ("on", ApiJson.Str(MagicEvents.TypeNames[(int)x.On])),
                    ("reaches", ApiJson.Strings(MagicEvents.Receivers(graph, x.Event))))))))))),
            ("firings", ApiJson.Arr(firings.Select(f => ApiJson.Obj(("t", ApiJson.Num(Math.Round(f.Time, 3))),
                ("group", ApiJson.Num(f.Group)), ("action", ApiJson.Num(f.Action)), ("event", ApiJson.Str(f.Event)), ("trigger", ApiJson.Bool(f.Trigger)))))),
            ("at", at == double.MaxValue ? null : ApiJson.Num(at)),
            ("deliveries", ApiJson.Arr(state.Deliveries.Select(d => ApiJson.Obj(("t", ApiJson.Num(Math.Round(d.Time, 3))),
                ("to", ApiJson.Str(d.Receiver)), ("event", ApiJson.Str(d.Event)), ("trigger", ApiJson.Bool(d.Trigger)), ("from", ApiJson.Str(d.From)))))),
            ("lights", ApiJson.Arr(reachedLights.Select(l => ApiJson.Obj(("id", ApiJson.Str(l)), ("on", ApiJson.Bool(state.Lights[l])),
                ("start", ApiJson.Bool(preview.StartLights[l])))))),
            ("movers", ApiJson.Obj(state.Movers.Select(m => (m.Key, (SvJsonNode?)ApiJson.Num(Math.Round(m.Value, 3)))).ToArray())));
    }

    /// <summary>camnets: every camera network in ring order, the camera the CamNet gadget opens on, and problems.</summary>
    private static SvJsonNode CamNets(MapProject p) =>
        ApiJson.Obj(("entry", CameraNetworks.EntryCamera(p) is { } e ? ApiJson.Str(e) : null),
            ("networks", ApiJson.Arr(CameraNetworks.Networks(p).Select(n => ApiJson.Obj(("loop", ApiJson.Bool(n.Loop)),
                ("cameras", ApiJson.Arr(n.Cameras.Select(c => ApiJson.Obj(("id", ApiJson.Str(c.Id)), ("name", ApiJson.Str(c.Name)),
                    ("first", c.First ? SvJsonNode.True : null), ("at", ApiJson.Vec(c.Eye)),
                    ("pitch", Limits(c.PitchLimits)), ("yaw", Limits(c.YawLimits)))))),
                ("problems", ApiJson.Strings(n.Problems)))))));

    private static SvJsonNode? Limits((int Min, int Max) l) =>
        l == (0, 0) ? null : new SvJsonArray([SvJsonNode.Integer(l.Min), SvJsonNode.Integer(l.Max)]);

    /// <summary>propsets: the property sets (the map's, then the user library), or those that fit <c>class</c> or
    /// the actor <c>id</c>.</summary>
    private static SvJsonNode PropSets(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        string? cls = ApiJson.GetString(q, "class");
        if (ApiJson.GetString(q, "id") is not null)
        {
            cls = s.Project.FindActor(QueryActor(ctx, q))!.Class;
        }

        List<PropertySet> sets = cls is null ? PropertySets.All(s.Folder) : PropertySets.For(ClassDefaults.Catalog.Find(cls)?.Name ?? cls, s.Folder);
        return ApiJson.Arr(sets.Select(x => ApiJson.Obj(("set", ApiJson.Str(x.Key)), ("class", ApiJson.Str(x.Class)),
            ("source", ApiJson.Str(x.Source.ToString().ToLowerInvariant())), ("note", x.Note is null ? null : ApiJson.Str(x.Note)),
            ("props", x.Props.ToJson()))));
    }

    /// <summary>tips: zone tips (texts, cylinder, flags) and help path markers.</summary>
    private static SvJsonNode Tips(MapProject p) =>
        ApiJson.Obj(
            ("tips", ApiJson.Arr(HelpTips.Tips(p).Select(t => ApiJson.Obj(("id", ApiJson.Str(t.Id)), ("at", ApiJson.Vec(t.Center)),
                ("radius", ApiJson.Num(t.Radius)), ("height", ApiJson.Num(t.Height)), ("spy", ApiJson.Str(t.Spy)), ("merc", ApiJson.Str(t.Merc)),
                ("onlyWithHelp", ApiJson.Bool(t.OnlyWithHelp)), ("stay", ApiJson.Bool(t.StayPermanently)),
                ("endsGame", t.EndsGame ? SvJsonNode.True : null), ("endGameDelay", t.EndsGame ? ApiJson.Num(t.EndGameDelay) : null))))),
            ("markers", ApiJson.Arr(HelpTips.Markers(p).Select(m => ApiJson.Obj(("id", ApiJson.Str(m.Id)), ("at", ApiJson.Vec(m.Location)),
                ("mesh", ApiJson.Str(m.Mesh)))))));

    /// <summary>zone: what missions do when complete (<c>id</c> for one mission, else every mission and objective
    /// that fires something), in the order <c>zone.action</c> remove counts.</summary>
    private static SvJsonNode Zone(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        GameplayGraph graph = GameplayGraph.Build(s.Project);
        IEnumerable<Actor> missions = ApiJson.GetString(q, "id") is not null
            ? [s.Project.FindActor(QueryActor(ctx, q))!]
            : s.Project.Actors.Where(a => ClassDefaults.IsA(a, "SBase.SObjective") && a.Props.GetString("Event") is { Length: > 0 });
        return ApiJson.Arr(missions.Select(m => ApiJson.Obj(("mission", ApiJson.Str(m.Id)),
            ("relay", ZoneActions.OwnRelay(s.Project, graph, m)?.Id is { } r ? ApiJson.Str(r) : null),
            ("actions", ApiJson.Arr(ZoneActions.Of(s.Project, graph, m).Select((x, i) => ApiJson.Obj(("index", ApiJson.Num(i)),
                ("kind", ApiJson.Str(x.Kind)), ("event", ApiJson.Str(x.Event)), ("reaches", ApiJson.Strings(x.Receivers)),
                ("delay", ApiJson.Num(x.Delay)), ("type", ApiJson.Str(MagicEvents.TypeNames[(int)x.Type])),
                ("relay", x.Relay is { } relay ? ApiJson.Str(relay) : null), ("own", ApiJson.Bool(x.Own)))))))));
    }

    /// <summary>modes: the game modes the map has missions for, with how many actors belong only to them; with
    /// <c>mode</c> (number or name), the ids of that mode's actors.</summary>
    private static SvJsonNode Modes(EditorSession s, ChangeContext ctx, SvJsonObject q)
    {
        GameModes modes = GameModes.Build(s.Project, MissionTree.Build(s.Project, ClassDefaults.Catalog));
        if (q["mode"] is not { } wanted)
        {
            return ApiJson.Arr(modes.Present.Select(m => ApiJson.Obj(("mode", ApiJson.Num(m.Mode)), ("name", ApiJson.Str(m.Name)),
                ("actors", ApiJson.Num(modes.Count(m.Mode))))));
        }

        int mode = ApiJson.TryNumber(wanted, out double n) ? (int)n
            : modes.Present.FirstOrDefault(m => string.Equals(m.Name, (wanted as SvJsonString)?.Value, StringComparison.OrdinalIgnoreCase)).Mode;
        if (mode == 0)
        {
            throw new ApiException($"no such mode in this map (modes: {string.Join(", ", modes.Present.Select(m => m.Name))})", "mode");
        }

        return ApiJson.Strings(s.Project.Actors.Where(a => modes.Of(a.Id).Contains(mode)).Select(a => a.Id));
    }

    private static string QueryActor(ChangeContext ctx, SvJsonObject q)
    {
        string text = ApiJson.GetString(q, "id") ?? throw new ApiException("give id", "id");
        return ctx.TryActorId(text) ?? throw new ApiException($"no actor '{text}'", "id");
    }
}
