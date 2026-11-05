using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>The kinds of security device the workbench knows.</summary>
public enum DeviceKind
{
    Camera,
    Laser,
    MotionSensor,
    PresenceTrigger,
    Mine,
    Alarm,

    /// <summary>Any other actor snapped to a wall.</summary>
    Other,
}

/// <summary>A placeable device: its class, kind and how far it sits off the wall it is snapped to.</summary>
public sealed record DeviceSpec(DeviceKind Kind, string Class, string Title, double WallGap, int Pitch = 0);

/// <summary>A camera's view: where it looks from, the cone's half angle, its range and the sweep either side.</summary>
public sealed record CameraView(Vec3 Eye, Rot Rotation, double HalfAngleDegrees, double Range, double SweepDegrees);

/// <summary>
/// The security workbench's data: device kinds by class, their view cones, beams and ranges with the game's own
/// numbers (spec/devices: SCamera turns PatrolAngle x 91 units either side and sees VisibilityConeAngle x 91 units off
/// its axis; SLaserTrigger's beam runs LaserLength ahead), and wall snapping (the device's back on the wall, facing out).
/// </summary>
public static class SecurityDevices
{
    public static readonly IReadOnlyList<DeviceSpec> Placeable =
    [
        new(DeviceKind.Camera, "SBase.SCamera", "Camera", 24, Pitch: -2048),
        new(DeviceKind.Laser, "SBase.SLaserTrigger", "Laser", 4),
        new(DeviceKind.MotionSensor, "SBase.SVolumetricSensor", "Motion sensor", 8),
        new(DeviceKind.PresenceTrigger, "SBase.SPresenceTrigger", "Presence trigger", 48),
        new(DeviceKind.Alarm, "SBase.SAlarm", "Alarm", 16),
    ];

    /// <summary>SCamera angle units: rotator units per (half) degree, as the game reads PatrolAngle and the cone.</summary>
    public const int CameraUnitsPerDegree = 91;

    private static readonly (string Class, DeviceKind Kind)[] s_kinds =
    [
        ("SBase.SCamera", DeviceKind.Camera),
        ("SBase.SLaserTrigger", DeviceKind.Laser),
        ("SBase.SVolumetricSensor", DeviceKind.MotionSensor),
        ("SBase.SPresenceTrigger", DeviceKind.PresenceTrigger),
        ("SBase.SMines", DeviceKind.Mine),
        ("SBase.SAlarm", DeviceKind.Alarm),
    ];

    public static DeviceKind? KindOf(Actor a) => KindOf(a.Class);

    /// <summary>How an actor sits on a wall: its device's placement, else a small gap and level facing.</summary>
    public static DeviceSpec SpecFor(Actor a) =>
        Placeable.FirstOrDefault(d => d.Kind == KindOf(a)) ?? new DeviceSpec(KindOf(a) ?? DeviceKind.Other, a.Class, a.ShortClass, 8);

    /// <summary>A new device of <paramref name="spec"/> at a spot (id from its class name), as one undo step.</summary>
    public static AddObjectsCommand Place(MapProject p, DeviceSpec spec, Vec3 location, Rot rotation)
    {
        string id = GeShapes.IdSource(p)(spec.Class[(spec.Class.LastIndexOf('.') + 1)..]);
        var actor = new Actor(id, spec.Class) { Location = GeEdits.Round(location), Rotation = rotation };
        return new AddObjectsCommand($"Place {spec.Title}", actors: [actor]);
    }

    /// <summary>Moves the actors onto their nearest wall (one undo step); ids with no wall within reach are listed in
    /// <paramref name="missed"/>.</summary>
    public static ReplaceObjectsCommand Snap(MapProject p, TraversalWorld w, IEnumerable<string> ids, List<string> missed, double reach = 512)
    {
        var cmd = new ReplaceObjectsCommand("Snap to wall");
        foreach (Actor actor in ids.Select(p.FindActor).OfType<Actor>())
        {
            if (SnapToWall(w, SpecFor(actor), actor.Location, reach) is not { } spot)
            {
                missed.Add(actor.Id);
                continue;
            }

            Actor after = actor.Clone();
            after.Location = GeEdits.Round(spot.Location);
            after.Rotation = spot.Rotation;
            if ((after.Location - actor.Location).Length > 0.01 || after.Rotation != actor.Rotation)
            {
                cmd.Actor(actor.Clone(), after);
            }
        }

        return cmd;
    }

    public static DeviceKind? KindOf(string className)
    {
        foreach ((string cls, DeviceKind kind) in s_kinds)
        {
            if (string.Equals(className, cls, StringComparison.OrdinalIgnoreCase) || ClassDefaults.Catalog.IsA(className, cls))
            {
                return kind;
            }
        }

        return null;
    }

    public static CameraView Camera(Actor a) => new(a.Location, a.Rotation,
        ClassDefaults.Number(a, "VisibilityConeAngle", 40) * CameraUnitsPerDegree * 360.0 / Rot.UnitsPerTurn,
        ClassDefaults.Number(a, "VisibilityMaxDistance", 2000),
        ClassDefaults.Number(a, "PatrolAngle", 90) * CameraUnitsPerDegree * 360.0 / Rot.UnitsPerTurn);

    /// <summary>How far SCamera.PreBeginPlay traces back from the camera for the wall its mount goes on.</summary>
    public const double CameraWallProbe = 200;

    /// <summary>How far off that wall the game puts the camera and its mount.</summary>
    public const double CameraWallOffset = 24;

    /// <summary>
    /// Where the game puts a security camera's wall mount (SCamera.PreBeginPlay with bAutoWallSupport, OpenSCCT
    /// spec/devices/security-camera.md, start-up rule 1): a trace 200 uu straight back along the camera's facing; on a
    /// hit the camera and its mount go 24 uu off the surface, the mount facing out of it (the normal's rotation: yaw 0
    /// on a flat ceiling or floor); with no hit, or no <paramref name="world"/>, the mount stays at the camera with its
    /// yaw only.
    /// </summary>
    public static (Vec3 Location, Rot Rotation) CameraMount(Actor a, TraversalWorld? world)
    {
        Vec3 back = a.Location - a.Rotation.Axes().Forward * CameraWallProbe;
        if (world?.Trace(a.Location, back) is not { } hit || hit.Normal == Vec3.Zero)
        {
            return (a.Location, new Rot(0, a.Rotation.Yaw, 0));
        }

        Vec3 n = hit.Normal.Normalized();
        // FVector.Rotation: yaw from the normal's horizontal part, which a flat ceiling or floor does not have.
        Rot facing = Math.Sqrt(n.X * n.X + n.Y * n.Y) < 1e-4 ? new Rot(n.Z > 0 ? Rot.UnitsPerTurn / 4 : -Rot.UnitsPerTurn / 4, 0, 0)
            : RotatorMath.LookAt(Vec3.Zero, n);
        return (hit.Point + n * CameraWallOffset, facing);
    }

    /// <summary>A laser's beam: from the device along its facing for LaserLength, cut short by the first wall when a
    /// world is given.</summary>
    public static (Vec3 From, Vec3 To) LaserBeam(Actor a, TraversalWorld? world = null)
    {
        Vec3 dir = a.Rotation.Axes().Forward;
        Vec3 end = a.Location + dir * ClassDefaults.Number(a, "LaserLength", 300);
        if (world?.Trace(a.Location + dir * 2, end) is { } hit)
        {
            end = hit.Point;
        }

        return (a.Location, end);
    }

    /// <summary>The radius a device notices players within (presence triggers, mines), or null.</summary>
    public static double? Radius(Actor a) => KindOf(a) switch
    {
        DeviceKind.PresenceTrigger => ClassDefaults.Number(a, "CollisionRadius", 40),
        DeviceKind.Mine => ClassDefaults.Number(a, "fDetectionRadius", ClassDefaults.Number(a, "fDistanceMax", 0)) is > 0 and double r ? r : null,
        _ => null,
    };

    /// <summary>The volumes a motion sensor watches.</summary>
    public static List<string> SensorVolumes(Actor a) => MissionTree.Refs(a.Props["Volumes"]);

    /// <summary>Where a device goes on a wall hit at <paramref name="point"/> with surface normal <paramref name="normal"/>:
    /// its gap off the wall, facing out of it.</summary>
    public static (Vec3 Location, Rot Rotation) OnWall(DeviceSpec spec, Vec3 point, Vec3 normal)
    {
        Vec3 n = normal.Normalized();
        Rot facing = RotatorMath.LookAt(Vec3.Zero, n);
        if (Math.Abs(n.Z) < 0.7)
        {
            facing = new Rot(spec.Pitch, RotatorMath.YawOf(n), 0);
        }

        return (point + n * spec.WallGap, facing);
    }

    /// <summary>Snaps a placed device to the nearest wall around it (horizontal rays in 16 directions within
    /// <paramref name="reach"/>), or null when no wall is that close.</summary>
    public static (Vec3 Location, Rot Rotation)? SnapToWall(TraversalWorld w, DeviceSpec spec, Vec3 at, double reach = 512)
    {
        TraceHit? best = null;
        for (int i = 0; i < 16; i++)
        {
            double a = i * Math.PI / 8;
            var dir = new Vec3(Math.Cos(a), Math.Sin(a), 0);
            // Only faces turned toward the device count (a ray from inside solid sees back faces).
            if (w.Trace(at, at + dir * reach) is { } hit && Vec3.Dot(hit.Normal, dir) < -0.1 && (best is null || hit.Distance < best.Value.Distance))
            {
                best = hit;
            }
        }

        if (best is not { } h)
        {
            return null;
        }

        Vec3 n = new Vec3(h.Normal.X, h.Normal.Y, 0).Normalized();
        return n == Vec3.Zero ? null : OnWall(spec, h.Point, n);
    }
}
