using OpenScct.Content.Collision;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>Where an actor ends up in the compiled map (the converter's categories).</summary>
public enum ActorCategory
{
    /// <summary>In sim/gameplay.json (and collision when it collides).</summary>
    Gameplay,

    /// <summary>Drawn or lit only: render/ files (static meshes, lights, sounds, projectors).</summary>
    Render,

    /// <summary>Editor-only (cameras, the builder brush): dropped.</summary>
    Editor,

    /// <summary>Deleted or PS2-only: dropped.</summary>
    Skipped,
}

/// <summary>
/// The converter's actor rules for authored actors: categories, effective property values (actor overrides over class
/// defaults) and collision flags.
/// </summary>
public sealed class ActorRules
{
    /// <summary>Classes that are drawn or lit and never simulated (matched exactly).</summary>
    public static readonly IReadOnlySet<string> RenderClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Engine.StaticMeshActor", "Engine.Light", "Engine.Spotlight", "Engine.SunLight", "Engine.AmbientSound",
        "Engine.Projector", "Engine.DynamicProjector", "Engine.AntiPortalActor", "Engine.ERainVolume",
        "SoftBody.ESBPatchActor", "SoftBody.ESBStripDoorActor", "SBase.SFogVolume",
    };

    /// <summary>Classes only the editor uses.</summary>
    public static readonly IReadOnlySet<string> EditorClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Engine.Camera", "Engine.Brush",
    };

    /// <summary>Actor property → collider flag (converter FLAG_PROPS).</summary>
    private static readonly (string Prop, ColliderFlags Flag)[] s_flagProps =
    [
        ("bCollideActors", ColliderFlags.CollideActors),
        ("bBlockActors", ColliderFlags.BlockActors),
        ("bBlockPlayers", ColliderFlags.BlockPlayers),
        ("bBlockZeroExtentTraces", ColliderFlags.BlockZeroExtent),
        ("bBlockNonZeroExtentTraces", ColliderFlags.BlockNonZeroExtent),
        ("bBlockCamera", ColliderFlags.BlockCamera),
        ("bBulletGoTru", ColliderFlags.BulletGoesThrough),
    ];

    /// <summary>PLF_PSX2_ONLY: the actor exists on the PS2 build only.</summary>
    private const int PlatformPs2Only = 1;

    public ActorRules(ClassDefaults classes) => Classes = classes;

    public ClassDefaults Classes { get; }

    public ActorCategory CategoryOf(Actor a)
    {
        if (EditorClasses.Contains(a.Class))
        {
            return ActorCategory.Editor;
        }

        if (a.Props.GetBool("bDeleteMe") || a.Props.GetBool("bPendingDelete") || a.Props.GetInt("Platform") == PlatformPs2Only)
        {
            return ActorCategory.Skipped;
        }

        return RenderClasses.Contains(a.Class) ? ActorCategory.Render : ActorCategory.Gameplay;
    }

    /// <summary>The actor's own value, else the class default.</summary>
    public SvJsonNode? Value(Actor a, string prop) => a.Props[prop] ?? Classes.Default(a.Class, prop);

    public bool Flag(Actor a, string prop) => Value(a, prop) switch
    {
        null => false,
        SvJsonNumber n => n.TryGetInt64(out long v) ? v != 0 : n.IsFloat && n.FloatValue != 0,
        var v => v.Kind == SvJsonKind.True,
    };

    public double Number(Actor a, string prop, double fallback = 0) => Value(a, prop) is SvJsonNumber n ? PropValues.Num(n) : fallback;

    public bool IsA(Actor a, string ancestor) => Classes.IsA(a.Class, ancestor);

    // gameplay.json leaves out a zero PrePivot and a unit DrawScale / DrawScale3D, so the game then uses the class
    // default; colliders and render instances use the same values.

    /// <summary>The actor's PrePivot, else the class default.</summary>
    public Vec3 PrePivot(Actor a) => a.PrePivot != Vec3.Zero ? a.PrePivot : DefaultVector(a, "PrePivot", Vec3.Zero);

    /// <summary>DrawScale3D times DrawScale, each the actor's own or else the class default.</summary>
    public Vec3 Scale(Actor a) => DrawScale3D(a) * DrawScale(a);

    /// <summary>The actor's DrawScale, else the class default (a sticky cam's 2).</summary>
    public double DrawScale(Actor a) =>
        a.DrawScale != 1 ? a.DrawScale : Classes.Default(a.Class, "DrawScale") is SvJsonNumber n ? PropValues.Num(n) : 1;

    /// <summary>The actor's DrawScale3D, else the class default.</summary>
    public Vec3 DrawScale3D(Actor a) => a.DrawScale3D != Vec3.One ? a.DrawScale3D : DefaultVector(a, "DrawScale3D", Vec3.One);

    /// <summary>A local point to world with <see cref="PrePivot"/> and <see cref="Scale"/>.</summary>
    public Vec3 LocalToWorld(Actor a, Vec3 p) => a.Rotation.Rotate((p - PrePivot(a)).Scale(Scale(a))) + a.Location;

    /// <summary>A world point to the actor's local space (the inverse of <see cref="LocalToWorld"/>; a zero scale
    /// axis maps to 0).</summary>
    public Vec3 WorldToLocal(Actor a, Vec3 p)
    {
        Vec3 s = Scale(a);
        Vec3 local = a.Rotation.Unrotate(p - a.Location);
        return new Vec3(Div(local.X, s.X), Div(local.Y, s.Y), Div(local.Z, s.Z)) + PrePivot(a);

        static double Div(double v, double by) => Math.Abs(by) > 1e-12 ? v / by : 0;
    }

    private Vec3 DefaultVector(Actor a, string prop, Vec3 fallback) =>
        Classes.Default(a.Class, prop) is SvJsonObject o ? Interop.InteropValues.VectorOr(o, fallback) : fallback;

    public ColliderFlags FlagsOf(Actor a)
    {
        ColliderFlags flags = ColliderFlags.None;
        foreach ((string prop, ColliderFlags flag) in s_flagProps)
        {
            if (Flag(a, prop))
            {
                flags |= flag;
            }
        }

        return flags;
    }

    /// <summary>A zone actor (ZoneInfo and subclasses; the LevelInfo is zone 0 and not counted).</summary>
    public bool IsZoneActor(Actor a) => IsA(a, "Engine.ZoneInfo") && !IsA(a, "Engine.LevelInfo");

    public bool IsSkyZone(Actor a) => IsA(a, "Engine.SkyZoneInfo");

    public bool IsLight(Actor a) => IsA(a, "Engine.Light");
}
