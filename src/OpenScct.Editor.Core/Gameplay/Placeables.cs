using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Scripting;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>The groups of the placeables palette, in the order it shows them.</summary>
public enum PlaceableGroup
{
    Security,
    Objectives,
    Stations,
    Interactive,
}

/// <summary>How a placeable goes down where it is dropped.</summary>
public enum PlaceableMount
{
    /// <summary>On the surface under the pointer (a wall, a ceiling or a floor), its back to it and facing out: the
    /// security tool's wall placement (<see cref="SecurityDevices.OnWall"/>), with the device's gap and pitch.</summary>
    Surface,

    /// <summary>Upright on the aimed surface; in side views, at the view plane's depth.</summary>
    Floor,

    /// <summary>A logical marker placed at the view's working depth, without requiring geometry.</summary>
    Free,
}

/// <summary>
/// One tile of the placeables palette: a gameplay class, its English name and group, how it is placed, what it does,
/// and on how many of the stock maps it is placed (from the converted stock maps' <c>sim/gameplay.json</c>).
/// </summary>
/// <param name="Gap">How far off the surface a <see cref="PlaceableMount.Surface"/> placeable sits.</param>
/// <param name="Pitch">The pitch a surface placeable gets on a wall (rotator units; cameras look down a little).</param>
/// <param name="OnFloors">Prefer the floor in top view instead of searching farther for a wall or ceiling.
/// Every surface is allowed regardless of this preference.</param>
/// <param name="AlignToSurface">Align the authored mounting plane to floors and ceilings as well as walls.</param>
/// <param name="WallHeight">How high above the floor a surface placeable goes on a wall found for it in the top, front
/// or side view (below the ceiling there); <see cref="double.PositiveInfinity"/> puts it just under the ceiling.</param>
/// <param name="WallYaw">Yaw correction for a model whose authored facing differs from the security devices' +X.</param>
/// <param name="Icon">The tile's icon kind when it is not the class's own (a plain mover tile shown as a door).</param>
public sealed record Placeable(string Class, string Title, PlaceableGroup Group, PlaceableMount Mount, string Summary, int StockMaps,
    double Gap = 8, int Pitch = 0, bool OnFloors = false, double WallHeight = 112, ActorKind? Icon = null, int WallYaw = 0, bool AlignToSurface = false)
{
    public string ShortClass => Class[(Class.LastIndexOf('.') + 1)..];

    /// <summary>Its name in a sentence, with its article: "a security camera", "an alarm".</summary>
    public string InSentence => ("aeiou".Contains(char.ToLowerInvariant(Title[0])) ? "an " : "a ") + Title.ToLowerInvariant();

    /// <summary>The security tool's device of the class when it has one (its gap and pitch), else this placeable's.</summary>
    public DeviceSpec Device => SecurityDevices.Placeable.FirstOrDefault(d => string.Equals(d.Class, Class, StringComparison.OrdinalIgnoreCase))
        ?? new DeviceSpec(SecurityDevices.KindOf(Class) ?? DeviceKind.Other, Class, Title, Gap, Pitch);

    /// <summary>How it is placed, in words for tooltips.</summary>
    public string MountWords => (Mount == PlaceableMount.Free
        ? "freely at the view's working depth"
        : "on any aimed surface; " + (AlignToSurface ? "aligned to that surface" : Mount == PlaceableMount.Floor ? "upright" : "using its usual mounting orientation"))
        + "; hold Shift for free placement without surface snapping";

    /// <summary>Default surface orientation, independent of whether the surface is a preferred mount.</summary>
    public Rot RotationOn(Vec3 normal)
    {
        Rot facing = SecurityDevices.OnWall(Device, Vec3.Zero, normal).Rotation;
        if (AlignToSurface)
        {
            return RotatorMath.Compose(new Rot(0, WallYaw, 0), facing);
        }
        if (Group == PlaceableGroup.Security && Mount == PlaceableMount.Surface)
        {
            return facing;
        }
        return Math.Abs(normal.Z) >= 0.7 ? Rot.Zero : facing with { Yaw = Rot.Wrap(facing.Yaw + WallYaw) };
    }

    /// <summary>True when every word of <paramref name="query"/> is in its name, class, group or description.</summary>
    public bool Matches(string query)
    {
        string text = $"{Title} {Class} {Placeables.GroupTitle(Group)} {Summary}";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The palette of placeable gameplay classes: security devices, objectives, stations and interactive objects. The list
/// is the gameplay classes the stock maps place (counted over the converted stock maps: classes no stock map places,
/// such as the mines and sticky cameras the players throw, are left out), kept to those the class catalog has and can
/// place; a catalog subclass of one of them (a map's own class) joins its group.
/// </summary>
public static class Placeables
{
    /// <summary>The converted stock maps the counts are over.</summary>
    public const int StockMapCount = 85;

    public static readonly IReadOnlyList<Placeable> Stock =
    [
        // Security.
        new("SBase.SCamera", "Security camera", PlaceableGroup.Security, PlaceableMount.Surface,
            "Sweeps to and fro and fires its Event (usually an alarm) when it sees a spy. The game puts its wall mount on the "
            + "wall up to 200 units behind it.", 37, 24, -2048, WallHeight: double.PositiveInfinity),
        new("SBase.SCamNetwork", "Network camera", PlaceableGroup.Security, PlaceableMount.Surface,
            "A globe camera of the mercenaries' network: they look through it from anywhere with the CamNet gadget, stepping "
            + "round the ring its NextCam and PrevCam make.", 75, 16, -2048, WallHeight: double.PositiveInfinity),
        new("SBase.SLaserTrigger", "Laser tripwire", PlaceableGroup.Security, PlaceableMount.Surface,
            "A laser beam LaserLength long; a spy who breaks it fires its Event (usually an alarm).", 53, 4, OnFloors: true, WallHeight: 48),
        new("SBase.SVolumetricSensor", "Motion sensor", PlaceableGroup.Security, PlaceableMount.Surface,
            "Watches the volumes in its Volumes list and fires its Event when someone moves in them.", 33, WallHeight: 224),
        new("SBase.SPresenceTrigger", "Presence trigger", PlaceableGroup.Security, PlaceableMount.Surface,
            "Fires its Event when a player comes within its radius.", 77, 48, OnFloors: true, WallHeight: 48),
        new("SBase.SAlarm", "Alarm", PlaceableGroup.Security, PlaceableMount.Surface,
            "Goes off when a sender fires it: locks the movers and objectives in its lists, fires its Events and shows on the HUD.", 73, 16, WallHeight: 192),
        new("SBase.SGPOControl", "Security zone", PlaceableGroup.Security, PlaceableMount.Free,
            "A named area (“Library”) grouping security devices: a device that names it in its ERControl passes "
            + "a jam (chaff, sticky shocker) on to the zone.", 15),
        new("SBase.SProjoFixe", "Searchlight", PlaceableGroup.Security, PlaceableMount.Floor,
            "A fixed searchlight a mercenary mans to sweep a spotlight, within its turn limits.", 5),

        // Objectives.
        new("SBase.SComputerObjectiveTrigger", "Computer", PlaceableGroup.Objectives, PlaceableMount.Floor,
            "A laptop or terminal the spies hack to complete its objective.", 76),
        new("SBase.SBombTargetObjectiveTrigger", "Bomb target", PlaceableGroup.Objectives, PlaceableMount.Surface,
            "A spot the spies plant a bomb on to complete its objective.", 41, OnFloors: true),
        new("SBase.SFlag", "Disk to carry", PlaceableGroup.Objectives, PlaceableMount.Floor,
            "A hard disk in a server rack: a spy takes it and carries it to one of its drop zones (DropZone) to complete its objective.", 49),
        new("SBase.SFlagDropZone", "Disk drop zone", PlaceableGroup.Objectives, PlaceableMount.Floor,
            "A case a spy carrying a disk drops it in; the disk lists the zones it can be delivered to.", 48),
        new("SBase.SDisk", "Hunt disk", PlaceableGroup.Objectives, PlaceableMount.Floor,
            "A disk to pick up in a disk hunt: the disk hunt mission counts them (its Triggers).", 70),
        new("SBase.SObjective", "Objective", PlaceableGroup.Objectives, PlaceableMount.Free,
            "An objective of a mission: done when one of its Triggers (computers, bomb targets, disks) is; then fires its Event.", 80),
        new("SBase.SMission", "Mission", PlaceableGroup.Objectives, PlaceableMount.Free,
            "A game mode's mission: its Objectives, and how many of them win it (MinimumObjectives).", 80),

        // Pickups and stations.
        new("SBase.SAmmoStock", "Ammo stock", PlaceableGroup.Stations, PlaceableMount.Surface,
            "An ammo crate the mercenaries refill their weapon and bomb charges from.", 76, 4, OnFloors: true, WallHeight: 96),
        new("SBase.SInfirmary", "Infirmary", PlaceableGroup.Stations, PlaceableMount.Surface,
            "A wall medkit that heals either team and cures poison.", 76, 4, WallHeight: 96),

        // Interactive.
        new("SBase.SInterrupteur", "Switch", PlaceableGroup.Interactive, PlaceableMount.Surface,
            "A switch players use: it fires its Event (a door, a light, a lift).", 72, 4, OnFloors: true, WallHeight: 96, AlignToSurface: true),
        new("Engine.Mover", "Door", PlaceableGroup.Interactive, PlaceableMount.Floor,
            "A mover: a mesh that moves between its keys when triggered or bumped (doors, gates, shutters).", 77, Icon: ActorKind.Door),
        new("SBase.SLiftDoor", "Lift door", PlaceableGroup.Interactive, PlaceableMount.Floor,
            "A lift's door: a mover that opens and closes with its lift.", 26),
        new("SBase.SLift", "Lift", PlaceableGroup.Interactive, PlaceableMount.Floor,
            "A lift car: a mover that travels between floors, called by its switches.", 28),
        new("Engine.MoverRotating", "Rotating mover", PlaceableGroup.Interactive, PlaceableMount.Floor,
            "A mover that turns on and on (fans, wheels).", 5),
        new("SBase.SExtincteur", "Fire extinguisher", PlaceableGroup.Interactive, PlaceableMount.Surface,
            "A wall extinguisher that sprays a cloud and makes a bang when shot, then re-arms.", 39, OnFloors: true, WallHeight: 64),
        new("SBase.SVitre", "Window", PlaceableGroup.Interactive, PlaceableMount.Surface,
            "Breakable glass: it shatters when shot or crashed through.", 47, 0, OnFloors: true, WallHeight: 96, WallYaw: 16384, AlignToSurface: true),
        new("SBase.SSprinkler", "Sprinkler", PlaceableGroup.Interactive, PlaceableMount.Surface,
            "A ceiling sprinkler that rains and leaves puddles when its Tag is fired.", 5, 4, WallHeight: double.PositiveInfinity),
    ];

    /// <summary>The group's name for people.</summary>
    public static string GroupTitle(PlaceableGroup g) => g switch
    {
        PlaceableGroup.Security => "Security",
        PlaceableGroup.Objectives => "Objectives",
        PlaceableGroup.Stations => "Pickups and stations",
        _ => "Interactive",
    };

    private static readonly Lock s_lock = new();
    private static (IClassCatalog? Catalog, IReadOnlyList<Placeable> List)? s_available;

    /// <summary>
    /// The palette for <paramref name="catalog"/>: the stock list, without classes the catalog does not have or cannot
    /// place (abstract; not placeable, but movers are, as the original editor makes them from a brush), with the
    /// catalog's placeable subclasses of a listed class added to its group (named by the class). With no catalog, the
    /// stock list.
    /// </summary>
    public static IReadOnlyList<Placeable> Available(IClassCatalog? catalog)
    {
        lock (s_lock)
        {
            if (s_available is { } cached && ReferenceEquals(cached.Catalog, catalog))
            {
                return cached.List;
            }
        }

        var list = new List<Placeable>();
        if (catalog is null)
        {
            list.AddRange(Stock);
        }
        else
        {
            foreach (Placeable p in Stock)
            {
                if (catalog.Find(p.Class) is { } info && CanPlace(catalog, info))
                {
                    list.Add(p with { Class = info.Name });
                }
            }

            var known = new HashSet<string>(list.Select(p => p.Class), StringComparer.OrdinalIgnoreCase);
            foreach (ClassInfo info in catalog.All.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (known.Contains(info.Name) || !CanPlace(catalog, info))
                {
                    continue;
                }

                // The nearest listed class up its chain decides the group, mount and description.
                if (catalog.Chain(info.Name).Skip(1).Select(c => list.FirstOrDefault(p => string.Equals(p.Class, c.Name, StringComparison.OrdinalIgnoreCase)))
                        .FirstOrDefault(p => p is not null) is { } parent)
                {
                    list.Add(parent with { Class = info.Name, Title = info.ShortName, StockMaps = 0, Icon = null, Summary = $"A kind of {parent.Title.ToLowerInvariant()}: {parent.Summary}" });
                }
            }
        }

        IReadOnlyList<Placeable> result = list.OrderBy(p => p.Group).ToList();
        lock (s_lock)
        {
            s_available = (catalog, result);
        }

        return result;
    }

    /// <summary>The palette entry of a class (full or short name), or null when the palette has none.</summary>
    public static Placeable? Find(string className, IClassCatalog? catalog)
    {
        string name = catalog?.Find(className)?.Name ?? className;
        return Available(catalog).FirstOrDefault(p => string.Equals(p.Class, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.ShortClass, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The kind of a placeable, for its icon.</summary>
    public static ActorKind Kind(Placeable p, IClassCatalog? catalog) => p.Icon ?? ActorKinds.OfClass(p.Class, catalog);

    private static bool CanPlace(IClassCatalog catalog, ClassInfo info) =>
        !info.Abstract && (info.Placeable || catalog.IsA(info.Name, "Engine.Mover"));
}
