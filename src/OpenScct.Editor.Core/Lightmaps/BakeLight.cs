using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Lighting;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// A Light actor as the lightmapper sees it: placement, the UE2 light properties, and the decoded bake terms.
/// <para>A baked texel or vertex adds <c>vis x Colour x f</c> in stored units (0..1 = byte 0..255, the game shows twice
/// that), where <c>Colour = FGetHSV(H, S, 255) x LightBrightness / 255 x M x LevelInfo.Brightness</c> (FUN_111af860:
/// <c>FDynamicLight.Color = C / 2</c>, added as <c>byte x 2 Color</c>) and <c>f</c> is the effect's term
/// (<see cref="Surface"/>, <see cref="Vertex"/>). So point, spot and sun all show as <c>2 C f</c>, like the actor term.</para>
/// </summary>
public sealed record BakeLight
{
    public Vec3? EmissionColour { get; init; }
    public Vec3? EmissionNormal { get; init; }
    public double EmissionArea { get; init; }
    public const int LE_NonIncidence = 13;
    public const int LE_Shell = 14;
    public const int LE_Cylinder = 17;

    /// <summary>The mesh line check's sun length (UStaticMesh::Illuminate, 0x110e8190).</summary>
    public const double SunTraceLength = 262144;

    /// <summary>The actor this light came from, when it came from the project (lighting.json reads the rest from it).</summary>
    public Actor? Actor { get; init; }

    public required string Id { get; init; }

    public Vec3 Location { get; init; }

    /// <summary>The light's forward axis (spots and suns shine along it).</summary>
    public Vec3 Direction { get; init; } = Vec3.UnitX;

    public double Brightness { get; init; } = 64;

    /// <summary>LightRadius as stored; the reach is <see cref="Radius"/>.</summary>
    public double LightRadius { get; init; } = 64;

    public int Hue { get; init; }

    public int Saturation { get; init; } = 255;

    public int Effect { get; init; }

    public byte Cone { get; init; } = 128;

    public int Zone { get; init; }

    public bool OwnZoneOnly { get; init; }

    /// <summary>LightType: 0 none, 1 steady, 2 pulse, 3 blink, 4 flicker, 5 strobe, 7 subtle pulse.</summary>
    public int Type { get; init; } = 1;

    public int Period { get; init; } = 32;

    public int Phase { get; init; }

    public bool Dynamic { get; init; }

    /// <summary>A switchable gameplay light (STriggerLight): the game lights it at run time instead.</summary>
    public bool Gameplay { get; init; }

    /// <summary>A light the bake uses but the build does not ship (<see cref="Compile.CompileContext.BakeOnlyLights"/>).</summary>
    public bool BakeOnly { get; init; }

    public bool ApplyStatic { get; init; } = true;

    public bool SpecialLit { get; init; }

    /// <summary>LevelInfo.Brightness, which scales every light colour (Level+0x484).</summary>
    public double LevelBrightness { get; init; } = 1;

    /// <summary>Baked into lightmaps and vertex colours (<see cref="Bakes"/>).</summary>
    public bool Baked => Bakes(Type, ApplyStatic, Dynamic, Gameplay, Effect);

    /// <summary>Baked into lightmaps and vertex colours: a static light with a light type, not bDynamicLight, not an
    /// Echelon spot shadow light (effect 22, shadow-mapped at run time). Gameplay lights are switched at run time.</summary>
    public static bool Bakes(int type, bool applyStatic, bool dynamic, bool gameplay, int effect) =>
        type != 0 && applyStatic && !dynamic && !gameplay && effect != ActorLightRules.LE_ESpotShadow;

    /// <summary>
    /// Drawn on the world at run time instead of baked: every bDynamicLight light that is not an Echelon spot shadow
    /// light and gives light, whatever its static flag (the game's WorldLights.IsDynamicWorldLight, docs/RENDERING.md
    /// "Dynamic lights").
    /// </summary>
    public static bool LightsWorldLive(bool dynamic, int effect, double brightness) =>
        dynamic && effect != ActorLightRules.LE_ESpotShadow && brightness > 0;

    public bool IsSun => Effect == ActorLightRules.LE_Sunlight;

    public bool IsSpot => ActorLightRules.IsSpot(Effect);

    public bool IsCylinder => Effect == LE_Cylinder;

    /// <summary>Reach in uu: 25 (LightRadius + 1).</summary>
    public double Radius => ActorLightRules.WorldRadius((float)LightRadius);

    /// <summary>The spot cone's cosine: k = 1 - LightCone / 256.</summary>
    public double ConeCos => 1 - (Cone / 256.0);

    /// <summary>A light type that changes over time: the bake takes its value at time 0 (flicker: its mean).</summary>
    public bool Animated => Type is >= 2 and <= 7;

    /// <summary>An effect the bake has no own term for (searchlight, disco, waves...): it bakes as a point light.</summary>
    public bool UnknownEffect => Effect is not (0 or ActorLightRules.LE_StaticSpot or ActorLightRules.LE_Spotlight or ActorLightRules.LE_Sunlight
        or LE_NonIncidence or LE_Shell or LE_Cylinder or ActorLightRules.LE_QuadraticNonIncidence or ActorLightRules.LE_PointAmbient);

    /// <summary>The light type's scale at bake time (FUN_111af860 at time 0); flicker takes its mean, not a random draw.</summary>
    public double Modulation => Type == 4 ? ActorLightRules.MeanModulation(4) : ActorLightRules.Modulation(Type, Period, Phase, 0f);

    /// <summary>The colour the terms scale (stored units per unit term), 0..1+ per channel.</summary>
    public Vec3 Colour
    {
        get
        {
            if (EmissionColour is { } emission) return emission;
            LightRgb c = ActorLightRules.Colour((byte)Math.Clamp(Hue, 0, 255), (byte)Math.Clamp(Saturation, 0, 255), (float)Brightness);
            return new Vec3(c.R, c.G, c.B) * (Modulation * LevelBrightness);
        }
    }

    /// <summary>
    /// The lightmap term (FUN_111b00c0) at surface point <paramref name="p"/> with normal <paramref name="n"/>, before
    /// shadows: point <c>S(x) cos</c>; spots also <c>((cos a - k) / (1 - k))^2</c>; sun <c>max(0, -n.Dir)</c> (gain 2, not
    /// the actor path's 1.75); NonIncidence <c>1 - x</c>; QuadraticNonIncidence <c>1 - x^2</c> (the 0xd and 0x14 branches
    /// call 0x10e2e730, which is Max(a, b), not a square: ScheU's 15 NonIncidence lights hold 1.05 of <c>1 - x</c> in the
    /// stock bake, 1.77 of the square); Shell
    /// <c>1 - 10 |x - 0.9|</c> on 0.8..1; Cylinder <c>1 - (dx^2 + dy^2) / R^2</c>. Two-sided surfaces take the absolute
    /// cosine. <c>x = d / R</c>, <c>S(x) = 1 - 3x^2 + 2x^3</c>.
    /// </summary>
    public double Surface(Vec3 p, Vec3 n, bool twoSided)
    {
        if (EmissionNormal is { } emitterNormal)
        {
            Vec3 offset = p - Location; double distance = offset.Length;
            if (distance >= Radius || distance < .001) return 0;
            Vec3 direction = offset / distance;
            double receiver = twoSided ? Math.Abs(Vec3.Dot(n, -direction)) : Math.Max(0, Vec3.Dot(n, -direction));
            return receiver * Math.Max(0, Vec3.Dot(emitterNormal, direction)) * EmissionArea / (Math.PI * (distance * distance + EmissionArea));
        }
        if (IsSun)
        {
            double c = -Vec3.Dot(n, Direction);
            return twoSided ? Math.Abs(c) : Math.Max(0, c);
        }

        if (IsCylinder)
        {
            return CylinderTerm(p);
        }

        Vec3 v = Location - p;
        double d = v.Length;
        double r = Radius;
        if (d >= r)
        {
            return 0;
        }

        double x = d / r;
        switch (Effect)
        {
            case LE_NonIncidence:
                return 1 - x;
            case ActorLightRules.LE_QuadraticNonIncidence:
                return 1 - (x * x);
            case LE_Shell:
                return x >= 0.8 ? 1 - (10 * Math.Abs(x - 0.9)) : 0;
        }

        return PointTerm(v, d, x, n, twoSided);
    }

    /// <summary>
    /// The static mesh vertex term (FDynamicLight::SampleIntensity, 0x111aed50, halved: the vertex colour stores half):
    /// point, spot, sun and Cylinder as <see cref="Surface"/>; NonIncidence <c>sqrt(1.02 - x)</c>, QuadraticNonIncidence
    /// <c>1.02 - x^2</c>.
    /// </summary>
    public double Vertex(Vec3 p, Vec3 n)
    {
        if (EmissionNormal is not null) return Surface(p, n, false);
        if (IsSun)
        {
            return Math.Max(0, -Vec3.Dot(n, Direction));
        }

        if (IsCylinder)
        {
            return CylinderTerm(p);
        }

        Vec3 v = Location - p;
        double d = v.Length;
        double r = Radius;
        if (d >= r)
        {
            return 0;
        }

        double x = d / r;
        return Effect switch
        {
            LE_NonIncidence => Math.Sqrt(1.02 - x),
            ActorLightRules.LE_QuadraticNonIncidence => 1.02 - (x * x),
            _ => PointTerm(v, d, x, n, false),
        };
    }

    private double PointTerm(Vec3 v, double d, double x, Vec3 n, bool twoSided)
    {
        if (d < 1e-6)
        {
            return 0;
        }

        double cos = Vec3.Dot(n, v) / d;
        cos = twoSided ? Math.Abs(cos) : cos;
        if (cos <= 0)
        {
            return 0;
        }

        double f = (1 - (3 * x * x) + (2 * x * x * x)) * cos;
        if (IsSpot)
        {
            // The UE1 cone, not the actor path's D3D cone.
            double k = ConeCos;
            double cosA = -Vec3.Dot(v, Direction) / d;
            if (cosA <= k || k >= 1)
            {
                return 0;
            }

            double t = (cosA - k) / (1 - k);
            f *= t * t;
        }

        return f;
    }

    private double CylinderTerm(Vec3 p)
    {
        double dx = p.X - Location.X, dy = p.Y - Location.Y, r = Radius;
        return Math.Max(0, 1 - (((dx * dx) + (dy * dy)) / (r * r)));
    }

    /// <summary>True when the light can add anything inside <paramref name="box"/> (its sphere, or its column for
    /// Cylinder lights; suns reach everywhere).</summary>
    public bool Reaches(Aabb box)
    {
        if (IsSun)
        {
            return true;
        }

        double r = Radius;
        double dx = Math.Max(0, Math.Max(box.Min.X - Location.X, Location.X - box.Max.X));
        double dy = Math.Max(0, Math.Max(box.Min.Y - Location.Y, Location.Y - box.Max.Y));
        double dz = IsCylinder ? 0 : Math.Max(0, Math.Max(box.Min.Z - Location.Z, Location.Z - box.Max.Z));
        return (dx * dx) + (dy * dy) + (dz * dz) < r * r;
    }

    /// <summary>A key of everything that decides where this light reaches and what shadows it casts (not its colour or
    /// brightness: recolouring a light re-adds cached shadows without tracing again). Own-zone lights leave texels of
    /// other zones out of their shadow masks, so the zone is part of it.</summary>
    public ulong ShapeKey()
    {
        var h = new Hash64();
        h.Add(Location);
        h.Add(EmissionNormal ?? Vec3.Zero);
        h.Add(EmissionArea);
        h.Add(Direction);
        h.Add(LightRadius);
        h.Add(Effect);
        h.Add(Cone);
        h.Add(OwnZoneOnly);
        h.Add(OwnZoneOnly ? Zone : -1);
        return h.Value;
    }
}
