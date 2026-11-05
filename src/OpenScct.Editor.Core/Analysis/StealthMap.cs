using OpenScct.Content;
using OpenScct.Content.Collision;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.AI;
using OpenScct.Game.SBase;
using OpenScct.Physics.Collision;
using Actor = OpenScct.Core.Engine.Actor;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>How dark a floor cell is for a spy.</summary>
public enum StealthBand
{
    Hidden,
    Partial,
    Exposed,
}

/// <summary>Which lights are on: the start of a round, or a switched state.</summary>
public sealed record LightState
{
    /// <summary>"start", "blackout" (every trigger light off), "lit" (every trigger light on) or "switch:&lt;id&gt;".</summary>
    public string Name { get; init; } = "start";

    /// <summary>Lights (actor ids) switched off on top of <see cref="Name"/>.</summary>
    public IReadOnlyList<string> Off { get; init; } = [];

    /// <summary>Lights (actor ids) switched on on top of <see cref="Name"/>.</summary>
    public IReadOnlyList<string> On { get; init; } = [];

    public static LightState Start { get; } = new();
}

/// <summary>Options of a stealth map.</summary>
public sealed record StealthOptions
{
    /// <summary>Cell size across (units); floors come from the spy navigation graph (32 apart), one per cell and storey.</summary>
    public double Spacing { get; init; } = 64;

    public LightState Lights { get; init; } = LightState.Start;

    /// <summary>The stance the band is read for: crouched (how a spy hides) or standing.</summary>
    public bool Crouched { get; init; } = true;

    /// <summary>Also work out which cells a merc can watch with the motion tracker and the torch, and what he sees
    /// behind the spy there (slower).</summary>
    public bool MercSight { get; init; } = true;

    /// <summary>Read the bands as a merc in night vision sees them: every lit surface and pawn lifted by the vision's
    /// ambient (<see cref="StealthMap.NightBoost"/>), then the screen's gain (<see cref="StealthMap.NightDisplayGain"/>).</summary>
    public bool Night { get; init; }
}

/// <summary>
/// One floor cell: the light on a spy there standing and crouched (luminance of the actor light term on its brightest
/// side, animated lights at their mean, 0 to 2), its band, and the nearest merc-reachable spot with a clear line to it
/// (null when none is within the tracker's range): within 2600 the motion tracker locks a spy moving at 150 or more
/// (walking, or crouch-walking at full speed, 221), within 1500 a merc torch lights the spy (<see cref="Torch"/>, aimed
/// straight at it).
/// </summary>
public readonly record struct StealthCell(Vec3 Floor, float Stand, float Crouch, StealthBand Band, float? MercDistance, float Torch)
{
    public bool Tracked => MercDistance is { } d && d <= StealthMap.TrackerRange;

    public bool InTorchReach => Torch > 0f;

    /// <summary>
    /// What the mercs who see the spy here see right behind it, in the same units as the spy's light (screen brightness
    /// over 127.5, so mid grey lit 1 reads 1): the surface's mean albedo times twice its light (the map's lightmap where
    /// it has a real one, else the light model, plus the run-time (bDynamicLight) lights that are on, trigger lights
    /// among them), capped at 2 like the game's light term; an unlit surface at its texture's colour, and a sky face as
    /// the sky zone shows it. The median over up to <see cref="StealthMap.BackgroundViews"/> viewpoints. Null when no merc sees the cell or nothing lies
    /// within <see cref="StealthMap.BackgroundReach"/> behind.
    /// </summary>
    public float? Background { get; init; }

    /// <summary>The mean albedo of the surface behind at the median (0.5 when its material has no texture).</summary>
    public float BackAlbedo { get; init; }

    /// <summary>A spy not exposed here stands out dark against a bright background (at or above the exposed edge).</summary>
    public bool Silhouette { get; init; }
}

/// <summary>Which part of the lighting a light takes part in, when it is only one of the two.</summary>
public enum LightRoleKind
{
    /// <summary>Lighting the world (baked, or drawn live as a dynamic light) but not pawns: the place looks lit, yet a
    /// spy there is as dark as the rest of the light on him.</summary>
    WorldOnly,

    /// <summary>Lighting pawns but neither baked nor drawn on the world: a spy is lit where the world does not look it.</summary>
    PawnsOnly,
}

/// <summary>A light that lights only the world or only pawns: where it is, its reach (uu) and whether it is a sun
/// (which reaches the whole map).</summary>
public sealed record LightRole(string Name, Vec3 Location, double Radius, LightRoleKind Kind, bool Sun);

/// <summary>A stealth map: the cells, the light state and switches it was made with, and band counts.</summary>
public sealed record StealthResult(IReadOnlyList<StealthCell> Cells, string State, IReadOnlyList<string> LightsOff, IReadOnlyList<LightSwitch> Switches,
    bool HasLightData, double Spacing, bool Crouched)
{
    /// <summary>The bands and backgrounds are as night vision shows them.</summary>
    public bool Night { get; init; }

    /// <summary>Merc viewpoints the sight lines were tried from.</summary>
    public int Viewpoints { get; init; }

    /// <summary>Lights that light pawns in game (bApplyToInGameLighting or bDynamicLight, with a light type).</summary>
    public int PawnLights { get; init; }

    /// <summary>Spot shadow lights (LE_ESpotShadow).</summary>
    public int SpotLights { get; init; }

    /// <summary>Lights that light only the world or only pawns (<see cref="AnalysisMap.LightRoles"/>).</summary>
    public IReadOnlyList<LightRole> LightRoles { get; init; } = [];
}

/// <summary>
/// An actor whose first use reaches trigger lights (a switch, a breaker, a trigger, an objective, an alarm), directly
/// or through SMagicEvent relays: whether it sends a trigger or an untrigger, and the lights it reaches.
/// </summary>
public sealed record LightSwitch(string Id, string Class, bool Untrigger, IReadOnlyList<string> Lights);

/// <summary>
/// The stealth light map: how lit a spy is on every floor, by OpenSCCT's own model of it (<see cref="AiLightField"/>,
/// the rule the renderer lights pawns with and the bots see by: zone ambient plus the four strongest in-game or dynamic
/// lights, spot shadow lights with their shadow line, trigger lights in the chosen state), plus where a merc's motion
/// tracker and torch reach, and the background a merc sees behind the spy (the world light the lightmaps hold, which
/// never lights the spy, times the surface's mean albedo).
/// </summary>
public static class StealthMap
{
    /// <summary>
    /// The bands' edges in light level: RE+'s Light and Shadow Map calls a spot hidden under 40 and exposed from 100 of
    /// 255 screen brightness; on a mid-grey body (0.5) that is a level of 40 / 127.5 and 100 / 127.5. Heuristics, not
    /// decoded game values, so the raw levels are given with every cell.
    /// </summary>
    public const float HiddenBelow = 40f / 127.5f;

    public const float ExposedFrom = 100f / 127.5f;

    /// <summary>SMotionTrackingVision MaxDetectedDistance 2600 plus a spy's collision radius (GetActors).</summary>
    public const float TrackerRange = 2600f + AiK.PawnRadius;

    /// <summary>Merc viewpoints are taken one per this many units across, from the floors mercs reach.</summary>
    private const float ViewpointSpacing = 256f;

    /// <summary>How far behind the spy the background is looked for, along the merc's line of sight.</summary>
    public const float BackgroundReach = 4096f;

    /// <summary>
    /// The background is read from up to this many merc viewpoints that see the spy and the median kept: the wall behind
    /// a spy changes with where the merc stands.
    /// </summary>
    public const int BackgroundViews = 5;

    /// <summary>The nearest viewpoints in range tried for the background's extra views.</summary>
    private const int BackgroundCandidates = 16;

    /// <summary>
    /// Night vision's light lift on every lit surface and pawn: 2 x 15 / 255 (OpenSCCT's RenderK.NightSceneBoost,
    /// decoded from the original's pre-render step for RendMap 34).
    /// </summary>
    public const float NightBoost = 2f * 15f / 255f;

    /// <summary>
    /// Night vision's screen: out = saturate(4 n L tint) over the scene's luminance L, with a grain n of mean 0.5 and a
    /// green tint of 1 (OpenSCCT's RenderK.NightVisionGain and NightVisionTint, decoded from the post pass), so the green
    /// a merc sees is twice the scene's, capped. The grain's spread and the vision's own glow are left out.
    /// </summary>
    public const float NightDisplayGain = 2f;

    /// <summary>The mean albedo taken for a surface whose material has no texture.</summary>
    private const float GreyAlbedo = 0.5f;

    /// <summary>UE2 PF_Unlit: a BSP surface drawn at its texture's colour.</summary>
    private const uint PolyFlagUnlit = 0x00400000;

    public static StealthBand BandOf(float level) => level < HiddenBelow ? StealthBand.Hidden : level >= ExposedFrom ? StealthBand.Exposed : StealthBand.Partial;

    /// <summary>Builds the map. Call under <see cref="AnalysisMap.Gate"/>.</summary>
    public static StealthResult Build(AnalysisMap map, StealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        AiLightField field = map.NewLightField();
        IReadOnlyList<LightSwitch> switches = Switches(map);
        List<string> off = ApplyState(map, field, options.Lights, switches);

        ReachMap spy = Reachability.FromStarts(map, new ReachProfile(TeamSide.Spy), map.ActivationMask);
        List<FVector> floors = Cells(spy, options.Spacing);
        List<FVector> viewpoints = [];
        Dictionary<long, List<int>> buckets = [];
        if (options.MercSight)
        {
            ReachMap merc = Reachability.FromStarts(map, new ReachProfile(TeamSide.Merc), map.ActivationMask);
            viewpoints = Viewpoints(merc);
            buckets = Bucket(viewpoints);
        }

        SurfaceLightField? world = options.MercSight ? SurfaceLightField.Parse(map) : null;
        var offSet = new HashSet<string>(off, StringComparer.OrdinalIgnoreCase);
        var cells = new List<StealthCell>(floors.Count);
        int hint = -1;
        foreach (FVector floor in floors)
        {
            FVector standCentre = floor + new FVector(0f, 0f, AiK.StandingHeight);
            float stand = field.LevelAround(standCentre, AiK.PawnRadius);
            float crouch = field.LevelAround(floor + new FVector(0f, 0f, AiK.SpyCrouchHeight), AiK.PawnRadius);
            float level = options.Crouched ? crouch : stand;
            StealthBand band = BandOf(options.Night ? NightScreen(level + NightBoost) : MathF.Min(2f, level));
            if (world is null)
            {
                cells.Add(new StealthCell(AnalysisMap.V(floor), stand, crouch, band, null, 0f));
                continue;
            }

            // The tracker locks the standing body; the background is read behind the body in the chosen stance.
            (float? distance, float torch, List<int> eyes) = MercSight(map.Collision, viewpoints, buckets, standCentre, hint);
            hint = eyes.Count > 0 ? eyes[0] : hint;
            FVector body = options.Crouched ? floor + new FVector(0f, 0f, AiK.SpyCrouchHeight) : standCentre;
            (float? background, float albedo) = Median([.. eyes.Select(e => Background(map, world, viewpoints[e], body, offSet, options.Night))]);
            cells.Add(new StealthCell(AnalysisMap.V(floor), stand, crouch, band, distance, torch)
            {
                Background = background,
                BackAlbedo = albedo,
                Silhouette = band != StealthBand.Exposed && background >= ExposedFrom,
            });
        }

        return new StealthResult(cells, options.Lights.Name, off, switches, field.HasData, options.Spacing, options.Crouched)
        {
            PawnLights = field.LightCount,
            SpotLights = field.SpotLightCount,
            LightRoles = map.LightRoles,
            Night = options.Night,
            Viewpoints = viewpoints.Count,
        };
    }

    /// <summary>What night vision's screen shows for a scene <paramref name="level"/> (<see cref="NightDisplayGain"/>).</summary>
    private static float NightScreen(float level) => MathF.Min(2f, NightDisplayGain * MathF.Min(2f, level));

    /// <summary>The median of the backgrounds found (the upper one of an even count), with its albedo.</summary>
    private static (float? Level, float Albedo) Median(List<(float? Level, float Albedo)> views)
    {
        List<(float? Level, float Albedo)> found = [.. views.Where(v => v.Level is not null).OrderBy(v => v.Level)];
        return found.Count == 0 ? (null, GreyAlbedo) : found[found.Count / 2];
    }

    /// <summary>
    /// The surface a merc at <paramref name="eye"/> sees right behind the spy's <paramref name="body"/>: the first world
    /// surface on his line past the spy (through a sky face, the sky zone seen from its SkyZoneInfo along the same
    /// line), as screen brightness over 127.5: its material's mean albedo times its light, or its texture's colour when
    /// it is drawn unlit.
    /// </summary>
    private static (float? Level, float Albedo) Background(AnalysisMap map, SurfaceLightField world, FVector eye, FVector body,
        IReadOnlySet<string> off, bool night)
    {
        FVector dir = body - eye;
        float length = dir.Size();
        if (length < 1f)
        {
            return (null, GreyAlbedo);
        }

        dir /= length;
        FVector start = body + (dir * (AiK.PawnRadius + 1f));
        if (!map.Collision.LineCheck(out FCheckResult hit, null, start + (dir * BackgroundReach), start, default, ETraceFlags.TRACE_World))
        {
            return (null, GreyAlbedo);
        }

        if (hit.bFakeBackdrop && (world.SkyView is not { } sky ||
            !map.Collision.LineCheck(out hit, null, sky + (dir * BackgroundReach), sky, default, ETraceFlags.TRACE_World)))
        {
            return (null, GreyAlbedo);
        }

        CollisionData data = map.SvMap.Collision;
        string? material = hit.Material >= 0 && hit.Material < data.Materials.Length ? data.GetString(data.Materials[hit.Material].Name) : null;
        float albedo = material is not null && map.Albedo.TryGetValue(material, out float a) ? a : GreyAlbedo;
        if ((hit.PolyFlags & PolyFlagUnlit) != 0 || (material is not null && map.Unlit.Contains(material)))
        {
            return (night ? NightScreen(2f * albedo) : 2f * albedo, albedo);
        }

        // The lightmap holds the zone's ambient and the baked lights; the game adds the run-time (bDynamicLight) lights,
        // trigger lights among them, as it draws the surface. Static meshes and movers are not in the lightmap: the light model stands in.
        float stored = map.Lightmaps?.At(hit.Location, hit.Normal) is { } baked
            ? baked + world.Added(hit.Location, hit.Normal, off)
            : world.At(hit.Location, hit.Normal, off);
        float level = 2f * albedo * MathF.Min(2f, (2f * stored) + (night ? NightBoost : 0f));
        return (night ? NightScreen(level) : level, albedo);
    }

    /// <summary>One floor per cell of <paramref name="spacing"/> and storey (64 up), from the nodes spies reach.</summary>
    private static List<FVector> Cells(ReachMap spy, double spacing)
    {
        var seen = new HashSet<(int, int, int)>();
        var floors = new List<FVector>();
        for (int i = 0; i < spy.Graph.NodeCount; i++)
        {
            FVector f = spy.Graph.Node(i).Floor;
            if (spy.Reached(i) && seen.Add(((int)Math.Floor(f.X / spacing), (int)Math.Floor(f.Y / spacing), (int)Math.Floor(f.Z / 64f))))
            {
                floors.Add(f);
            }
        }

        return floors;
    }

    /// <summary>A merc's eye over the floors mercs reach, one per <see cref="ViewpointSpacing"/> and storey.</summary>
    private static List<FVector> Viewpoints(ReachMap merc)
    {
        var seen = new HashSet<(int, int, int)>();
        var eyes = new List<FVector>();
        var eye = new FVector(0f, 0f, AiK.StandingHeight + AiK.EyeHeightStanding);
        for (int i = 0; i < merc.Graph.NodeCount; i++)
        {
            FVector f = merc.Graph.Node(i).Floor;
            if (merc.Reached(i) && seen.Add(((int)MathF.Floor(f.X / ViewpointSpacing), (int)MathF.Floor(f.Y / ViewpointSpacing), (int)MathF.Floor(f.Z / 128f))))
            {
                eyes.Add(f + eye);
            }
        }

        return eyes;
    }

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    private static Dictionary<long, List<int>> Bucket(List<FVector> points)
    {
        var buckets = new Dictionary<long, List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            long k = Key((int)MathF.Floor(points[i].X / TrackerRange), (int)MathF.Floor(points[i].Y / TrackerRange));
            if (!buckets.TryGetValue(k, out List<int>? list))
            {
                buckets[k] = list = [];
            }

            list.Add(i);
        }

        return buckets;
    }

    /// <summary>
    /// The nearest merc viewpoint within the tracker's range whose line to the spy's body is clear (the tracker's sight
    /// line: movers, level, level geometry), the torch level from it when it is within the torch's range, and the
    /// viewpoints that see the body (that one first, then up to <see cref="BackgroundViews"/> in all among the
    /// <see cref="BackgroundCandidates"/> nearest; none when none sees it). Every viewpoint in range counts. The one that
    /// saw the last cell (<paramref name="hint"/>) is tried first: neighbouring cells are mostly seen from the same place,
    /// and when it sees this one too only the nearer viewpoints still need a line.
    /// </summary>
    private static (float? Distance, float Torch, List<int> Eyes) MercSight(CollisionWorld world, List<FVector> viewpoints,
        Dictionary<long, List<int>> buckets, FVector body, int hint)
    {
        int bx = (int)MathF.Floor(body.X / TrackerRange), by = (int)MathF.Floor(body.Y / TrackerRange);
        var near = new List<(float D, int I)>();
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!buckets.TryGetValue(Key(bx + dx, by + dy), out List<int>? list))
                {
                    continue;
                }

                foreach (int i in list)
                {
                    float d = (viewpoints[i] - body).Size();
                    if (d <= TrackerRange)
                    {
                        near.Add((d, i));
                    }
                }
            }
        }

        near.Sort((a, b) => a.D.CompareTo(b.D));
        int limit = near.Count;
        int hinted = hint < 0 ? -1 : near.FindIndex(n => n.I == hint);
        if (hinted >= 0 && Clear(world, viewpoints[hint], body))
        {
            limit = hinted;
        }
        else
        {
            hinted = -1;
        }

        int first = hinted;
        for (int k = 0; k < limit; k++)
        {
            if (Clear(world, viewpoints[near[k].I], body))
            {
                first = k;
                break;
            }
        }

        if (first < 0)
        {
            return (null, 0f, []);
        }

        // Every viewpoint nearer than the first was tried and is blocked.
        List<int> eyes = [near[first].I];
        for (int k = first + 1; k < Math.Min(near.Count, BackgroundCandidates) && eyes.Count < BackgroundViews; k++)
        {
            if (Clear(world, viewpoints[near[k].I], body))
            {
                eyes.Add(near[k].I);
            }
        }

        FVector to = body - viewpoints[eyes[0]];
        float torch = near[first].D <= AiK.TorchRange ? AiLightField.TorchLevel(viewpoints[eyes[0]], to.Rotation(), body, clear: true) : 0f;
        return (near[first].D, torch, eyes);
    }

    /// <summary>
    /// The tracker's sight line is open: from the merc to the spy, as the game traces it. Any hit blocks it, so the
    /// trace stops at the first one it finds.
    /// </summary>
    private static bool Clear(CollisionWorld world, FVector eye, FVector body) =>
        !world.LineCheck(out _, null, body, eye, default, SMotionTrackingVisionK.SightFlags | ETraceFlags.TRACE_StopAtFirstHit);

    /// <summary>
    /// The actors whose first use reaches a trigger light: what they send by the game's rules (a gameplay object by its
    /// TriggerMethode, an objective by bUntriggerEvent; touch triggers, sensors, cameras and alarms a trigger) and the
    /// lights the event reaches, through relays too.
    /// </summary>
    public static IReadOnlyList<LightSwitch> Switches(AnalysisMap map)
    {
        var events = new MapEvents(map);
        var switches = new List<LightSwitch>();
        foreach ((string id, Actor a) in events.Actors)
        {
            if (MapEvents.FirstUse(a) is ({ Count: > 0 } names, bool untrigger))
            {
                var reached = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                Switch(events, names, untrigger, reached);
                if (reached.Count > 0)
                {
                    switches.Add(new LightSwitch(id, a.GetType().Name, untrigger, [.. reached.Keys.Order(StringComparer.OrdinalIgnoreCase)]));
                }
            }
        }

        return switches;
    }

    /// <summary>Applies what each trigger light <paramref name="events"/> reach does to <paramref name="on"/>, by
    /// STriggerLight.Trigger and UnTrigger (a light missing from it counts as off, and is added).</summary>
    private static void Switch(MapEvents map, IEnumerable<string> events, bool untrigger, Dictionary<string, bool> on)
    {
        foreach ((string id, Actor receiver, bool un) in map.Deliver(events, untrigger))
        {
            if (receiver is STriggerLight light)
            {
                bool lit = on.GetValueOrDefault(id);
                on[id] = !un && light.TriggerMethode switch
                {
                    TriggerType.TriggerControl => true,
                    TriggerType.TriggerToggle => !lit,
                    _ => lit,
                };
            }
        }
    }

    /// <summary>Switches the field's trigger lights to the state; returns the trigger lights that end up off.</summary>
    private static List<string> ApplyState(AnalysisMap map, AiLightField field, LightState state, IReadOnlyList<LightSwitch> switches)
    {
        var events = new MapEvents(map);
        var on = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, Actor a) in events.Actors)
        {
            if (a is STriggerLight light)
            {
                on[id] = light.bInitialyOn && light.LightType != ELightType.LT_None;
            }
        }

        string name = state.Name;
        if (name.Equals("blackout", StringComparison.OrdinalIgnoreCase) || name.Equals("lit", StringComparison.OrdinalIgnoreCase))
        {
            bool lit = name.Equals("lit", StringComparison.OrdinalIgnoreCase);
            foreach (string id in on.Keys.ToList())
            {
                on[id] = lit;
            }
        }
        else if (name.StartsWith("switch:", StringComparison.OrdinalIgnoreCase))
        {
            string id = name["switch:".Length..];
            LightSwitch sw = switches.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{id} reaches no trigger light ({(switches.Count == 0 ? "the map has no light switches" : "switches: " + string.Join(", ", switches.Select(s => s.Id)))})");
            Actor actor = events.Actors.First(x => x.Id.Equals(sw.Id, StringComparison.OrdinalIgnoreCase)).Actor;
            Switch(events, MapEvents.FirstUse(actor)!.Value.Events, sw.Untrigger, on);
        }
        else if (!name.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"light state is start, blackout, lit or switch:<id>, not '{name}'");
        }

        // Explicit lights win over the state; plain lights (not trigger lights) can be named too.
        if (state.Off.Concat(state.On).FirstOrDefault(id => !map.LightNames.Contains(id)) is { } unknown)
        {
            throw new ArgumentException($"{unknown} is not a light of the map's lighting");
        }

        foreach (string id in state.Off)
        {
            on[id] = false;
        }

        foreach (string id in state.On)
        {
            on[id] = true;
        }

        foreach ((string id, bool lit) in on)
        {
            field.SetOn(id, lit);
        }

        return on.Where(kv => !kv.Value).Select(kv => kv.Key).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
