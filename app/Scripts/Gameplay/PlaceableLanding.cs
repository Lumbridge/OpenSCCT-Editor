using System.Runtime.CompilerServices;
using Godot;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Overlays;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Gameplay;

/// <summary>Where a palette placeable goes: its location and rotation, and what it is on ("the wall"), for the status
/// bar.</summary>
public readonly record struct PlaceableSpot(Vec3 Location, Rot Rotation, string On);

/// <summary>
/// Where a placeable of the palette (<see cref="Placeables"/>) lands for a point of a view, by its class's placement
/// rule, over the level's collision (<see cref="GameplayState.World"/>: brushes and an imported map's baked collision,
/// meshes included), and the one-step placement with its wiring (<see cref="PlaceableWiring"/>). Both the views' drop
/// pipeline (<see cref="AssetDropTarget"/>) and the place tool use it.
/// <list type="bullet">
/// <item>A surface placeable (cameras, lasers, sensors, alarms, switches, stations) goes on the wall, ceiling or floor
/// under the pointer, its back to it and facing out, with the security tool's gaps and pitch
/// (<see cref="SecurityDevices.OnWall"/>): a camera 24 units off the wall, where the game's 200-unit trace back finds
/// the wall for its mount.</item>
/// <item>A floor placeable (objectives, disks, missions, movers) stands on the floor under the pointer; over a wall or
/// a ceiling it drops to the floor below it. Its mesh's bottom (else its collision cylinder's) rests on the floor, its
/// pivot snapped to the grid along the floor, facing the viewer in the perspective view.</item>
/// </list>
/// The perspective, front and side views trace along the pointer's ray; the top view traces down from the storey shown
/// (else the top of the map), past any solid an imported map is wrapped in.
/// </summary>
public static class PlaceableLanding
{
    private const double Reach = 65536;

    /// <summary>Why <see cref="Find"/> has no spot yet while hovering: the collision world is still being built. A drop
    /// there is taken (it waits for the world); only the preview is missing.</summary>
    public const string Pending = "working out where it lands (the level's collision is being built)";

    /// <summary>The highest point of each collision world, for the top view's downward trace.</summary>
    private static readonly ConditionalWeakTable<TraversalWorld, StrongBox<double>> s_tops = new();

    /// <summary>
    /// The spot for <paramref name="entry"/> at a view point, or null and why not ("no wall under the pointer").
    /// A hover (<paramref name="wait"/> false) never waits for the collision world, it starts building it; a drop or a
    /// click waits.
    /// </summary>
    public static (PlaceableSpot? Spot, string Why) Find(EditorViewport view, Vector2 at, Placeable entry, bool wait)
    {
        GameplayState state = GameplayState.Instance;
        TraversalWorld? world = wait ? state.World : state.WorldIfBuilt;
        if (world is null)
        {
            state.Prewarm();
            return (null, Pending);
        }

        if (!world.HasGeometry)
        {
            return (null, $"the map has no geometry to put {entry.InSentence} on yet");
        }

        ViewCamera cam = view.Cam;
        TraceHit? hit = cam.Kind == ViewKind.Top ? Down(world, cam, at) : GameplayPicking.Surface(world, cam, at);
        if (hit is not { } h || h.Normal == Vec3.Zero)
        {
            return (null, entry.Mount == PlaceableMount.Surface
                ? $"no wall, ceiling or floor under the pointer to put {entry.InSentence} on"
                : $"no floor under the pointer to stand {entry.InSentence} on");
        }

        Vec3 n = h.Normal.Normalized();
        if (entry.Mount == PlaceableMount.Surface)
        {
            (Vec3 location, Rot rotation) = SecurityDevices.OnWall(entry.Device, h.Point, n);
            return (new PlaceableSpot(location, rotation, Surface(n)), "");
        }

        Vec3 floor = h.Point;
        string on = "the floor";
        if (n.Z < 0.7)
        {
            // A wall or a ceiling: the floor below the point, a little out from the surface.
            Vec3 off = h.Point + n * 24;
            if (world.Trace(off, off - Vec3.UnitZ * 8192) is not { } below || below.Normal.Z < 0.7)
            {
                return (null, $"no floor below the {Surface(n)[4..]} under the pointer to stand {entry.InSentence} on");
            }

            floor = below.Point;
            on = $"the floor below the {Surface(n)[4..]}";
        }

        ToolManager? tools = ToolManager.Instance;
        Vec3 snapped = tools?.Snap(floor, [0, 1]) ?? floor;
        int yaw = cam.IsOrtho ? 0 : tools?.SnapRotation(cam.Yaw + Rot.UnitsPerTurn / 2) ?? (int)cam.Yaw + Rot.UnitsPerTurn / 2;
        var rotation2 = new Rot(0, yaw, 0).Normalized();
        var probe = new Actor("ghost", entry.Class) { Location = snapped with { Z = 0 }, Rotation = rotation2 };
        return (new PlaceableSpot(snapped with { Z = floor.Z + Lift(probe) }, rotation2, on), "");
    }

    /// <summary>
    /// Places <paramref name="entry"/> at <paramref name="spot"/> as one undo step, wired by the selection
    /// (<see cref="PlaceableWiring"/>), selects it, and says in the status bar and the log where it went, what was wired
    /// and what still needs wiring.
    /// </summary>
    public static void Place(EditorContext ctx, Placeable entry, PlaceableSpot spot, ViewKind view)
    {
        List<string> selected = ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Actor).Select(r => r.Id).ToList();
        PlacedPlaceable placed = PlaceableWiring.Place(ctx.Project, entry, spot.Location, spot.Rotation, selected, EditorServices.Classes);
        ctx.Execute(placed.Command);
        ctx.Selection.Set([ObjectRef.Actor(placed.Id)]);
        string text = placed.Describe(entry.Title, $"{spot.On} at {ctx.Project.FindActor(placed.Id)?.Location} in the {view} view");
        ctx.Log(text, placed.Needs.Count > 0 ? Severity.Warning : Severity.Info);
        ctx.SetStatus(text);
    }

    /// <summary>The faint copy of the placeable's mesh in the views (null hides it).</summary>
    public static void ShowGhost(Placeable? entry, PlaceableSpot? spot) =>
        ViewportLayout.Instance?.Scene.ShowPlacementGhost(entry is not null && spot is { } s
            ? new Actor("ghost", entry.Class) { Location = s.Location, Rotation = s.Rotation }
            : null, SceneStyle.DropTarget);

    /// <summary>Draws where the placeable would go over a view: a mark and its facing, a camera's cone and sweep and
    /// its wall mount, a laser's beam, a presence trigger's radius.</summary>
    public static void Draw(ViewCamera cam, Control canvas, Placeable entry, PlaceableSpot spot)
    {
        var batch = new OverlayBatch();
        var ghost = new Actor("ghost", entry.Class) { Location = spot.Location, Rotation = spot.Rotation };
        Color colour = SceneStyle.DropTarget;
        batch.Dot(spot.Location, colour, 5);
        TraversalWorld? world = GameplayState.Instance.WorldIfBuilt;
        switch (SecurityDevices.KindOf(ghost))
        {
            case DeviceKind.Camera:
                SecurityOverlay.Camera(batch, SecurityDevices.Camera(ghost), true);
                (Vec3 mount, _) = SecurityDevices.CameraMount(ghost, world);
                batch.Line(spot.Location, mount, colour, 1.5f, dashed: true);
                batch.Dot(mount, colour, 3);
                break;
            case DeviceKind.Laser:
                (Vec3 beamStart, Vec3 beamEnd) = SecurityDevices.LaserBeam(ghost, world);
                batch.Line(beamStart, beamEnd, SecurityOverlay.LaserColour, 2);
                break;
            case DeviceKind.PresenceTrigger when SecurityDevices.Radius(ghost) is { } r:
                batch.Ring(spot.Location, Vec3.UnitX, Vec3.UnitY, r, SecurityOverlay.SensorColour, 1.5f);
                break;
            default:
                if (ActorKinds.ShowsDirection(ActorIcons.KindOf(ghost), ghost, ClassDefaults.Catalog))
                {
                    batch.Arrow(spot.Location, spot.Location + spot.Rotation.Axes().Forward * 48, colour, 2);
                }

                break;
        }

        batch.Draw(cam, canvas);
    }

    /// <summary>How far above the floor the actor's location goes so it stands on it: its mesh's bottom when the mesh
    /// has loaded (it starts loading here), else half its collision cylinder.</summary>
    private static double Lift(Actor a)
    {
        if (StaticMeshDrawing.WorldBounds(a) is { } box)
        {
            return a.Location.Z - box.Min.Z;
        }

        if (StaticMeshDrawing.MeshPath(a) is { } path)
        {
            StaticMeshes.Request(path, _ => { });
        }

        return ClassDefaults.Number(a, "CollisionHeight", 0);
    }

    /// <summary>The first floor below the pointer in the top view: traced down from the storey shown, else from above
    /// the map, and moved on past any solid it starts in (an imported map is solid outside its rooms).</summary>
    private static TraceHit? Down(TraversalWorld world, ViewCamera cam, Vector2 at)
    {
        double top = s_tops.GetValue(world, w => new StrongBox<double>(w.Surfaces.Count == 0 ? 0
            : w.Surfaces.SelectMany(s => s.Verts).Max(v => v.Z) + 64)).Value;
        double bottom = top - Reach;
        if (StoreyFilter.Current is { } storey)
        {
            top = Math.Min(top, storey.Top - 1);
            bottom = storey.Bottom;
        }

        Vec3 origin = cam.PlanePoint(at, 0).With(2, top);
        const double Step = 64;
        for (double t = 0; top - t > bottom; t += Step)
        {
            Vec3 start = origin - Vec3.UnitZ * t;
            if (world.Trace(start, start with { Z = bottom }) is not { } hit)
            {
                return null;
            }

            if (hit.Distance >= 1)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>"the wall", "the ceiling" or "the floor" for a surface normal.</summary>
    private static string Surface(Vec3 n) => n.Z >= 0.7 ? "the floor" : n.Z <= -0.7 ? "the ceiling" : "the wall";
}
