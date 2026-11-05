using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Core.Uc;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;
using OpenScct.Physics.Conveyors;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>A point of a conveyor's path in world space, and the radius that rounds the corner there (0 is sharp).</summary>
public readonly record struct ConveyorPoint(Vec3 At, double Radius);

/// <summary>
/// Reads and writes OpenSCCT's native conveyor (<c>Svm.Conveyor</c>, spec/additions/conveyors.md) on editor actors. The
/// editor builds the same runtime object the game does, so its path, the built look and the item preview come from
/// the game's own code.
/// </summary>
public static class ConveyorDesign
{
    public const string ClassName = "Svm.Conveyor";

    public static bool Is(Actor a) => string.Equals(a.Class, ClassName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The conveyor as the game creates it: the actor's stored props over the class's own defaults.</summary>
    public static Conveyor Read(Actor a)
    {
        ArgumentNullException.ThrowIfNull(a);
        PropSet p = a.Props;
        var c = new Conveyor
        {
            Location = F(a.Location),
            Rotation = new FRotator(a.Rotation.Pitch, a.Rotation.Yaw, a.Rotation.Roll),
        };
        c.bLoop = p.GetBool("bLoop", c.bLoop);
        c.Width = p.GetFloat("Width", c.Width);
        c.Speed = p.GetFloat("Speed", c.Speed);
        c.bOn = p.GetBool("bOn", c.bOn);
        c.bCarryPlayers = p.GetBool("bCarryPlayers", c.bCarryPlayers);
        c.bCarryObjects = p.GetBool("bCarryObjects", c.bCarryObjects);
        c.Look = (EConveyorLook)p.GetInt("Look", (int)c.Look);
        c.Walls = (EConveyorWalls)p.GetInt("Walls", (int)c.Walls);
        c.TriggerAction = (EConveyorTrigger)p.GetInt("TriggerAction", (int)c.TriggerAction);
        c.ItemSpacing = p.GetFloat("ItemSpacing", c.ItemSpacing);
        c.ItemScale = p.GetFloat("ItemScale", c.ItemScale);
        c.ItemFade = p.GetFloat("ItemFade", c.ItemFade);
        c.ItemSeed = p.GetInt("ItemSeed", c.ItemSeed);
        c.ItemYawJitter = p.GetInt("ItemYawJitter", c.ItemYawJitter);
        c.BeltMaterial = new AssetRef(PropValues.AssetPath(p["BeltMaterial"]));
        c.FrameMaterial = new AssetRef(PropValues.AssetPath(p["FrameMaterial"]));
        foreach (SvJsonObject o in Items(p["Path"]))
        {
            PropValues.TryVector(o["Loc"], out Vec3 loc);
            c.Path.Add(new FConveyorPoint { Loc = F(loc), Radius = (float)PropValues.Num(o["Radius"]) });
        }

        foreach (SvJsonNode n in p["ItemMeshes"] is SvJsonArray meshes ? meshes : [])
        {
            if (PropValues.AssetPath(n) is { } path)
            {
                c.ItemMeshes.Add(new AssetRef(path));
            }
        }

        foreach (SvJsonObject o in Items(p["ScrollTargets"]))
        {
            c.ScrollTargets.Add(new FConveyorScroll
            {
                Target = o["Target"] is SvJsonString s ? s.Value : "",
                UnitsPerTile = (float)PropValues.Num(o["UnitsPerTile"]),
                Axis = (byte)PropValues.Num(o["Axis"]),
                Material = new AssetRef(PropValues.AssetPath(o["Material"])),
            });
        }

        return c;
    }

    /// <summary>The id of the conveyor <paramref name="a"/>'s end hands its items to (<c>Next</c>), or null.</summary>
    public static string? NextId(Actor a) => PropValues.ActorId(a.Props["Next"]);

    /// <summary>
    /// Every conveyor among <paramref name="actors"/> as the game creates it, in order, with <c>Next</c> linking the
    /// created objects as the map loader links them (a link to an actor that is not a conveyor here is dropped).
    /// </summary>
    public static List<(Actor Actor, Conveyor Conveyor)> ReadAll(IEnumerable<Actor> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        List<(Actor Actor, Conveyor Conveyor)> all = actors.Where(Is).Select(a => (a, Read(a))).ToList();
        var byId = new Dictionary<string, Conveyor>(StringComparer.OrdinalIgnoreCase);
        foreach ((Actor a, Conveyor c) in all)
        {
            byId.TryAdd(a.Id, c);
        }

        foreach ((Actor a, Conveyor c) in all)
        {
            c.Next = NextId(a) is { } next ? byId.GetValueOrDefault(next) : null;
        }

        return all;
    }

    /// <summary>The stored path points in world space.</summary>
    public static IReadOnlyList<ConveyorPoint> WorldPoints(Actor a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var points = new List<ConveyorPoint>();
        foreach (SvJsonObject o in Items(a.Props["Path"]))
        {
            PropValues.TryVector(o["Loc"], out Vec3 loc);
            points.Add(new ConveyorPoint(a.Rotation.Rotate(loc) + a.Location, PropValues.Num(o["Radius"])));
        }

        return points;
    }

    /// <summary>The <c>Path</c> value for world points, stored relative to the actor's location and rotation.</summary>
    public static SvJsonNode PathValue(Actor a, IEnumerable<ConveyorPoint> world)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(world);
        var items = new List<SvJsonNode>();
        foreach (ConveyorPoint p in world)
        {
            var o = new SvJsonObject();
            o.Add("Loc", PropValues.Vector(a.Rotation.Unrotate(p.At - a.Location)));
            if (p.Radius > 0)
            {
                o.Add("Radius", SvJsonNode.Float((float)p.Radius));
            }

            items.Add(o);
        }

        return PropValues.Array(items);
    }

    /// <summary>
    /// The <c>ScrollTargets</c> value: the mesh actors whose textures scroll with a driver conveyor, and the one material
    /// that scrolls when a target names it (a converted RE+ tread; otherwise all of them do).
    /// </summary>
    public static SvJsonNode ScrollValue(IEnumerable<FConveyorScroll> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return PropValues.Array(targets.Select(t =>
        {
            var o = new SvJsonObject();
            o.Add("Target", SvJsonNode.String(t.Target));
            o.Add("UnitsPerTile", SvJsonNode.Float(t.UnitsPerTile));
            o.Add("Axis", SvJsonNode.Integer(t.Axis));
            if (t.Material.Path is { } material)
            {
                o.Add("Material", PropValues.AssetRef(material));
            }

            return (SvJsonNode)o;
        }));
    }

    /// <summary>Look names as the tool and the ops write them, in enum order.</summary>
    public static readonly IReadOnlyList<string> Looks = ["frame", "driver", "treadmill", "escalator"];

    /// <summary>
    /// Wall names, in enum order: which sides (facing the direction of travel) have a wall. Auto is both sides, open
    /// where another conveyor's end meets this one's side.
    /// </summary>
    public static readonly IReadOnlyList<string> WallNames = ["both", "left", "right", "none", "auto"];

    /// <summary>What an event does, in enum order.</summary>
    public static readonly IReadOnlyList<string> Triggers = ["toggle", "on", "off", "reverse"];

    /// <summary>
    /// How far above a floor the riding surface goes when a path is drawn on it: a frame stands on the floor with its
    /// body under the belt, a treadmill and an escalator sit just above it (so their surface does not fight the
    /// floor's), and a driver lies on the meshes it drives.
    /// </summary>
    public static double SurfaceLift(EConveyorLook look) => look switch
    {
        EConveyorLook.CL_Frame => ConveyorLayout.FrameDepth,
        EConveyorLook.CL_Driver => 0,
        _ => ConveyorLayout.EdgeHeight,
    };

    /// <summary>The path the game builds (world space); empty for fewer than two distinct points.</summary>
    public static ConveyorPath WorldPath(Actor a) => Read(a).BuildWorldPath();

    public static Vec3 V(FVector v) => new(v.X, v.Y, v.Z);

    public static FVector F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static IEnumerable<SvJsonObject> Items(SvJsonNode? node) =>
        node is SvJsonArray array ? array.OfType<SvJsonObject>() : [];
}
