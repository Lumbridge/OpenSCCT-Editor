using System.Text.Json;
using OpenScct.Content.Collision;
using OpenScct.Core;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Physics.Collision;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>
/// The light on the world's surfaces, for the background a merc sees a spy against: the lightmap model (the zone's
/// lightmap ambient plus each baked light's lightmap term, <see cref="BakeLight.Surface"/>, with a shadow line to it)
/// and the run-time (bDynamicLight) lights that are on, trigger lights among them, which the game adds to lit surfaces
/// as it draws them (<see cref="BakeLight.LightsWorldLive"/>; a trigger light without bDynamicLight lights pawns only).
/// Read from the compiled map's lighting.json; shadows by the collision world. Values are in stored units like a
/// lightmap texel (the screen doubles them). It stands in where the map has no real lightmap
/// (<see cref="AnalysisMap.Lightmaps"/>) and for static meshes, an approximation of their vertex light.
/// </summary>
public sealed class SurfaceLightField
{
    /// <summary>How far off the surface the shadow lines start, so they do not hit the surface itself.</summary>
    private const float Lift = 2f;

    /// <summary>A shadow line stopped this close to its light hit the light's own fitting.</summary>
    private const float FittingReach = 24f;

    /// <summary>World geometry only: the level, its static meshes and movers block light; pawns do not.</summary>
    private const ETraceFlags ShadowFlags = ETraceFlags.TRACE_World;

    private readonly CollisionWorld _world;
    private readonly CollisionData _data;
    private readonly List<BakeLight> _lights;
    private readonly Dictionary<string, Vec3> _ambient;
    private readonly int _skyZone;

    private SurfaceLightField(CollisionWorld world, CollisionData data, List<BakeLight> lights, Dictionary<string, Vec3> ambient, int skyZone,
        FVector? skyView)
    {
        _world = world;
        _data = data;
        _lights = lights;
        _ambient = ambient;
        _skyZone = skyZone;
        SkyView = skyView;
    }

    /// <summary>Lights that light the world (baked, or drawn live as bDynamicLight lights).</summary>
    public int LightCount => _lights.Count;

    /// <summary>Where the sky zone is drawn from (its SkyZoneInfo), which a sky face shows; null when the map has none.</summary>
    public FVector? SkyView { get; }

    /// <summary>Reads the field of <paramref name="map"/> (empty when it has no lighting).</summary>
    public static SurfaceLightField Parse(AnalysisMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        CollisionData data = map.SvMap.Collision;
        var lights = new List<BakeLight>();
        var ambient = new Dictionary<string, Vec3>(StringComparer.OrdinalIgnoreCase);
        int sky = -1;
        FVector? skyView = null;
        if (map.LightingJson.Length > 0)
        {
            using JsonDocument json = JsonDocument.Parse(map.LightingJson.ToArray());
            JsonElement root = json.RootElement;
            if (root.TryGetProperty("lights", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement l in list.EnumerateArray())
                {
                    BakeLight light = Light(l, data);
                    if (light.Type != 0 && (light.Baked || BakeLight.LightsWorldLive(light.Dynamic, light.Effect, light.Brightness)))
                    {
                        lights.Add(light);
                    }
                }
            }

            if (root.TryGetProperty("zones", out JsonElement zones) && zones.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement z in zones.EnumerateArray())
                {
                    // A lightmap texel's ambient is half the zone's m_RefAmbientColor (LightColour.LightmapAmbient).
                    if (z.TryGetProperty("name", out JsonElement name) && z.TryGetProperty("ambient", out JsonElement a) && a.GetArrayLength() >= 3)
                    {
                        ambient[name.GetString() ?? ""] = new Vec3(Math.Floor(a[0].GetDouble() / 2), Math.Floor(a[1].GetDouble() / 2),
                            Math.Floor(a[2].GetDouble() / 2)) / 255;
                    }
                }
            }

            if (root.TryGetProperty("sky", out JsonElement s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("zone", out JsonElement sz))
            {
                sky = sz.GetInt32();
                if (s.TryGetProperty("location", out JsonElement at) && at.GetArrayLength() >= 3)
                {
                    skyView = new FVector(at[0].GetSingle(), at[1].GetSingle(), at[2].GetSingle());
                }
            }
        }

        return new SurfaceLightField(map.Collision, data, lights, ambient, sky, skyView);
    }

    private static BakeLight Light(JsonElement l, CollisionData data)
    {
        Vec3 at = Vector(l, "location");
        int[] rot = l.TryGetProperty("rotation", out JsonElement r) && r.GetArrayLength() >= 3 ? [r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32()] : [0, 0, 0];
        FVector forward = new FRotator(rot[0], rot[1], rot[2]).Vector();
        return new BakeLight
        {
            Id = Str(l, "name") ?? "",
            Location = at,
            Direction = new Vec3(forward.X, forward.Y, forward.Z),
            Brightness = Num(l, "brightness", 64),
            LightRadius = Num(l, "radius", 64),
            Hue = (int)Num(l, "hue", 0),
            Saturation = (int)Num(l, "saturation", 255),
            Effect = (int)Num(l, "effect", 0),
            Cone = (byte)Math.Clamp(Num(l, "cone", 128), 0, 255),
            Zone = BakedSource.ZoneAt(data, at),
            OwnZoneOnly = Flag(l, "own_zone_only"),
            Type = (int)Num(l, "ltype", 1),
            Period = (int)Num(l, "light_period", 32),
            Phase = (int)Num(l, "light_phase", 0),
            Dynamic = Flag(l, "dynamic"),
            Gameplay = Flag(l, "gameplay"),
            ApplyStatic = !l.TryGetProperty("apply_static", out JsonElement s) || s.ValueKind != JsonValueKind.False,
            SpecialLit = Flag(l, "special_lit"),
        };
    }

    /// <summary>
    /// The light (luminance, stored units) on the surface at <paramref name="p"/> facing <paramref name="normal"/>, with
    /// the lights named in <paramref name="off"/> switched off (baked lights stay in the lightmap whatever their state).
    /// </summary>
    public float At(FVector p, FVector normal, IReadOnlySet<string> off) => Sum(p, normal, off, lightmap: true);

    /// <summary>
    /// The light the game adds over a surface's lightmap as it draws it: the run-time (bDynamicLight) lights that are on,
    /// trigger lights among them (not the zone's ambient nor the baked lights, which the lightmap holds).
    /// </summary>
    public float Added(FVector p, FVector normal, IReadOnlySet<string> off) => Sum(p, normal, off, lightmap: false);

    private float Sum(FVector p, FVector normal, IReadOnlySet<string> off, bool lightmap)
    {
        var at = new Vec3(p.X, p.Y, p.Z);
        var n = new Vec3(normal.X, normal.Y, normal.Z);
        FVector from = p + (normal * Lift);
        int zone = BakedSource.ZoneAt(_data, new Vec3(from.X, from.Y, from.Z));
        string? zoneActor = zone < _data.Zones.Length ? _data.GetString(_data.Zones[zone].Actor) : null;
        Vec3 sum = lightmap && zoneActor is not null && _ambient.TryGetValue(zoneActor, out Vec3 a) ? a : default;
        foreach (BakeLight l in _lights)
        {
            if ((l.Baked ? !lightmap : off.Contains(l.Id)) || (l.OwnZoneOnly && l.Zone != zone))
            {
                continue;
            }

            double term = l.Surface(at, n, twoSided: false);
            if (term > 0 && Lit(l, from))
            {
                sum += l.Colour * term;
            }
        }

        return (float)((0.2126 * sum.X) + (0.7152 * sum.Y) + (0.0722 * sum.Z));
    }

    /// <summary>A clear line from the surface to the light (a sun: out to the sky zone).</summary>
    private bool Lit(BakeLight l, FVector from)
    {
        if (l.IsSun)
        {
            FVector away = new FVector((float)-l.Direction.X, (float)-l.Direction.Y, (float)-l.Direction.Z);
            FVector end = from + (away * (float)BakeLight.SunTraceLength);
            if (!_world.LineCheck(out FCheckResult sunHit, null, end, from, default, ShadowFlags))
            {
                return true;
            }

            FVector past = sunHit.Location + (away * Lift);
            return _skyZone >= 0 && BakedSource.ZoneAt(_data, new Vec3(past.X, past.Y, past.Z)) == _skyZone;
        }

        // A hit right at the light is its own fitting (a lamp's mesh), which the lightmapper buries, not a shadow.
        var light = new FVector((float)l.Location.X, (float)l.Location.Y, (float)l.Location.Z);
        return !_world.LineCheck(out FCheckResult hit, null, light, from, default, ShadowFlags) || (hit.Location - light).Size() <= FittingReach;
    }

    private static Vec3 Vector(JsonElement o, string key) =>
        o.TryGetProperty(key, out JsonElement v) && v.GetArrayLength() >= 3 ? new Vec3(v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble()) : default;

    private static double Num(JsonElement o, string key, double fallback) =>
        o.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static bool Flag(JsonElement o, string key) => o.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement o, string key) => o.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
