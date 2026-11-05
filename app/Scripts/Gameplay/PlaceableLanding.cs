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
/// <item>A surface placeable (cameras, lasers, sensors, alarms, switches, stations) goes on the wall or ceiling under
/// the pointer, its back to it and facing out, with the security tool's gaps and pitch
/// (<see cref="SecurityDevices.OnWall"/>): a camera 24 units off the wall, where the game's 200-unit trace back finds
/// the wall for its mount. Only a few (lasers, presence triggers, ammo crates: <see cref="Placeable.OnFloors"/>) also go
/// on a floor, facing up; for the others a floor is refused.</item>
/// <item>A floor placeable (objectives, disks, missions, movers) stands on the floor under the pointer; over a wall or
/// a ceiling it drops to the floor below it. Its mesh's bottom (else its collision cylinder's) rests on the floor, its
/// pivot snapped to the grid along the floor where the floor is still there, facing the viewer in the perspective
/// view.</item>
/// </list>
/// Traces meet the level's collision and the static meshes the views draw (<see cref="Trace"/>). The perspective view
/// traces along the pointer's ray. The top view finds the first floor below the pointer (on the
/// storey shown, on the level's collision only, so a mesh roof does not stop it); a surface placeable then goes on the nearest wall around that point at its
/// <see cref="Placeable.WallHeight"/>, else on the ceiling above. The front and side views take the point on the view
/// plane at the depth of the selection (else of what the view is centred on), as a mesh drop does: a floor placeable
/// stands on the floor below it, a surface placeable goes on the nearest wall or ceiling within reach of it.
/// </summary>
public static class PlaceableLanding
{
    private const double Reach = 65536;

    /// <summary>How far from the point the top, front and side views look for a wall or ceiling.</summary>
    private const double NearReach = 384;

    /// <summary>How far below the point the front and side views look for a floor, and up the top view for a ceiling.</summary>
    private const double FloorReach = 2048;

    /// <summary>Why <see cref="Find"/> has no spot yet while hovering: the collision world is still being built. A drop
    /// there is taken (it waits for the world); only the preview is missing.</summary>
    public const string Pending = "working out where it lands (the level's collision is being built)";

    /// <summary>The actors new placeables are wired to while pinned (the palette's "Keep Wiring To"), instead of the
    /// selection, which moves to each new actor; empty when nothing is pinned.</summary>
    public static IReadOnlyList<string> Pinned { get; private set; } = [];

    /// <summary>Raised when <see cref="Pinned"/> changes.</summary>
    public static event Action? PinnedChanged;

    /// <summary>Wires the next placeables to these actors, whatever is selected (an empty list unpins).</summary>
    public static void Pin(IReadOnlyList<string> ids)
    {
        Pinned = ids.ToList();
        PinnedChanged?.Invoke();
    }

    /// <summary>The pinned actors still in the map (deleted ones are dropped, and with none left nothing is pinned).</summary>
    public static IReadOnlyList<string> PinnedIn(MapProject p)
    {
        List<string> live = Pinned.Where(id => p.FindActor(id) is not null).ToList();
        if (live.Count != Pinned.Count)
        {
            Pin(live);
        }

        return live;
    }

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
        return cam.Kind switch
        {
            ViewKind.Perspective => Aimed(world, cam, at, entry),
            ViewKind.Top => FromAbove(world, cam, at, entry),
            _ => OnPlane(world, cam, at, entry),
        };
    }

    /// <summary>The perspective view: on the surface the pointer's ray meets.</summary>
    private static (PlaceableSpot? Spot, string Why) Aimed(TraversalWorld world, ViewCamera cam, Vector2 at, Placeable entry)
    {
        (Vec3 o, Vec3 d) = cam.Ray(at);
        if (Trace(world, o, o + d * Reach) is not { } h || h.Normal == Vec3.Zero)
        {
            return (null, entry.Mount == PlaceableMount.Surface
                ? $"no wall or ceiling under the pointer to put {entry.InSentence} on"
                : $"no floor under the pointer to stand {entry.InSentence} on");
        }

        Vec3 n = h.Normal.Normalized();
        if (entry.Mount == PlaceableMount.Surface)
        {
            return n.Z >= 0.7 && !entry.OnFloors
                ? (null, $"{entry.InSentence} goes on a wall or ceiling, not on the floor")
                : (OnSurface(entry, h.Point, n, Surface(n)), "");
        }

        if (n.Z >= 0.7)
        {
            return (Stand(world, cam, entry, h.Point, "the floor"), "");
        }

        // A wall or a ceiling: the floor below the point, a little out from the surface.
        Vec3 off = h.Point + n * 24;
        if (Trace(world, off, off - Vec3.UnitZ * 8192) is not { } below || below.Normal.Z < 0.7)
        {
            return (null, $"no floor below the {Surface(n)[4..]} under the pointer to stand {entry.InSentence} on");
        }

        return (Stand(world, cam, entry, below.Point, $"the floor below the {Surface(n)[4..]}"), "");
    }

    /// <summary>The top view: the first floor below the pointer; a surface placeable on the nearest wall around it,
    /// else on the ceiling above (or on the floor, for one that may go there).</summary>
    private static (PlaceableSpot? Spot, string Why) FromAbove(TraversalWorld world, ViewCamera cam, Vector2 at, Placeable entry)
    {
        if (Down(world, cam, at) is not { } floor)
        {
            return (null, $"no floor under the pointer to {(entry.Mount == PlaceableMount.Surface ? "find a wall from for" : "stand")} {entry.InSentence}{(entry.Mount == PlaceableMount.Surface ? "" : " on")}");
        }

        if (entry.Mount == PlaceableMount.Floor)
        {
            return (Stand(world, cam, entry, floor.Point, "the floor"), "");
        }

        Vec3 start = floor.Point + Vec3.UnitZ;
        TraceHit? ceiling = Trace(world, start, start + Vec3.UnitZ * FloorReach) is { } up && up.Normal.Z <= -0.7 && up.Distance >= 1 ? up : null;
        double room = ceiling?.Distance ?? 512;
        double height = Math.Max(16, Math.Min(entry.WallHeight, room - 24));
        if (NearestWall(world, floor.Point + Vec3.UnitZ * height, NearReach) is { } wall)
        {
            return (OnSurface(entry, wall.Point, Flat(wall.Normal), "the nearest wall"), "");
        }

        if (ceiling is { } c)
        {
            return (OnSurface(entry, c.Point, c.Normal.Normalized(), "the ceiling above"), "");
        }

        return entry.OnFloors
            ? (OnSurface(entry, floor.Point, Vec3.UnitZ, "the floor"), "")
            : (null, $"no wall within {NearReach:0} units of the pointer and no ceiling above it for {entry.InSentence}: drop it nearer a wall, or in the perspective view");
    }

    /// <summary>The front and side views: the point on the view plane at the selection's depth (else the view's centre);
    /// a floor placeable stands on the floor below it, a surface placeable goes on the nearest wall or ceiling (or floor,
    /// for one that may go there) within reach.</summary>
    private static (PlaceableSpot? Spot, string Why) OnPlane(TraversalWorld world, ViewCamera cam, Vector2 at, Placeable entry)
    {
        int axis = ViewAxes.DepthAxis(cam.Kind);
        double depth = PlaneDepth(cam, axis);
        Vec3 p = cam.PlanePoint(at, depth);
        string where = $"at the view's depth ({"XYZ"[axis]} {depth:0}, the selection's or the view centre's)";
        if (world.BoxOverlaps(p, new Vec3(2, 2, 2)))
        {
            return (null, $"the point {where} is inside solid: select something in the room first, or drop it in the perspective view");
        }

        TraceHit? floor = Trace(world, p, p - Vec3.UnitZ * FloorReach) is { Distance: >= 1 } down && down.Normal.Z >= 0.7 ? down : null;
        if (entry.Mount == PlaceableMount.Floor)
        {
            return floor is { } f
                ? (Stand(world, cam, entry, f.Point, "the floor"), "")
                : (null, $"no floor below the point {where} to stand {entry.InSentence} on");
        }

        var candidates = new List<(TraceHit Hit, Vec3 Normal, string On)>();
        if (NearestWall(world, p, NearReach) is { } wall)
        {
            candidates.Add((wall, Flat(wall.Normal), "the nearest wall"));
        }

        if (Trace(world, p, p + Vec3.UnitZ * NearReach) is { Distance: >= 1 } up && up.Normal.Z <= -0.7)
        {
            candidates.Add((up, up.Normal.Normalized(), "the ceiling above"));
        }

        if (entry.OnFloors && floor is { } below && below.Distance <= NearReach)
        {
            candidates.Add((below, Vec3.UnitZ, "the floor below"));
        }

        if (candidates.Count == 0)
        {
            return (null, $"no wall or ceiling within {NearReach:0} units of the point {where} for {entry.InSentence}");
        }

        (TraceHit hit, Vec3 normal, string on) = candidates.MinBy(c => c.Hit.Distance);
        return (OnSurface(entry, hit.Point, normal, on), "");
    }

    /// <summary>A surface placeable on a surface: the security tool's gap and pitch.</summary>
    private static PlaceableSpot OnSurface(Placeable entry, Vec3 point, Vec3 normal, string on)
    {
        (Vec3 location, Rot rotation) = SecurityDevices.OnWall(entry.Device, point, normal);
        return new PlaceableSpot(location, rotation, on);
    }

    /// <summary>A floor placeable standing on a floor point: its pivot snapped to the grid along the floor (kept where
    /// it is when the snapped point is off this floor: over a ledge, in a wall, on a step), facing the viewer.</summary>
    private static PlaceableSpot Stand(TraversalWorld world, ViewCamera cam, Placeable entry, Vec3 floor, string on)
    {
        ToolManager? tools = ToolManager.Instance;
        Vec3 snapped = tools?.Snap(floor, [0, 1]) ?? floor;
        Vec3 standOn = floor;
        if (snapped != floor)
        {
            Vec3 from = snapped with { Z = floor.Z + 32 };
            if (Trace(world, from, from - Vec3.UnitZ * 96) is { Distance: >= 1 } again && again.Normal.Z >= 0.7 && Math.Abs(again.Point.Z - floor.Z) <= 24)
            {
                standOn = again.Point;
            }
        }

        int yaw = cam.IsOrtho ? 0 : tools?.SnapRotation(cam.Yaw + Rot.UnitsPerTurn / 2) ?? (int)cam.Yaw + Rot.UnitsPerTurn / 2;
        var rotation = new Rot(0, yaw, 0).Normalized();
        var probe = new Actor("ghost", entry.Class) { Location = standOn with { Z = 0 }, Rotation = rotation };
        return new PlaceableSpot(standOn with { Z = standOn.Z + Lift(probe) }, rotation, on);
    }

    /// <summary>The nearest wall facing a point (horizontal rays in 16 directions within <paramref name="reach"/>; a ray
    /// from inside solid sees back faces, which do not count), as <see cref="SecurityDevices.SnapToWall"/> finds it.</summary>
    private static TraceHit? NearestWall(TraversalWorld world, Vec3 at, double reach)
    {
        TraceHit? best = null;
        for (int i = 0; i < 16; i++)
        {
            double a = i * Math.PI / 8;
            var dir = new Vec3(Math.Cos(a), Math.Sin(a), 0);
            if (Trace(world, at, at + dir * reach) is { } hit && Vec3.Dot(hit.Normal, dir) < -0.1 && Math.Abs(hit.Normal.Normalized().Z) < 0.7
                && (best is null || hit.Distance < best.Value.Distance))
            {
                best = hit;
            }
        }

        return best;
    }

    /// <summary>
    /// The first thing a line meets: the level's collision, or a static mesh the views draw that comes first. On a map
    /// whose meshes are its own actors (an editor-copy or recovered import) the collision world has none of them, so a
    /// device goes on a mesh wall and an objective stands on a mesh floor or desk only through this.
    /// </summary>
    private static TraceHit? Trace(TraversalWorld world, Vec3 from, Vec3 to)
    {
        TraceHit? hit = world.Trace(from, to);
        double length = (to - from).Length;
        if (length < 1e-6 || ToolManager.Instance?.Context.Project is not { } p)
        {
            return hit;
        }

        Vec3 d = (to - from) / length;
        return AssetDropTarget.NearestMesh(p, from, d, 0.5, hit?.Distance ?? length) is { } m
            ? new TraceHit(m.Point, m.Normal, m.Distance)
            : hit;
    }

    /// <summary>A wall's normal made level, so the device faces straight out of a sloping wall.</summary>
    private static Vec3 Flat(Vec3 n) => new Vec3(n.X, n.Y, 0).Normalized();

    /// <summary>The depth of the plane the front and side views place on: the selection's (as the place tool and a mesh
    /// drop use), else what the view is centred on.</summary>
    private static double PlaneDepth(ViewCamera cam, int axis) =>
        ToolManager.Instance?.Context is { } ctx && ctx.Selection.Primary is { } r && ObjectGeometry.Pivot(ctx.Project, r) is { } pivot
            ? pivot[axis]
            : cam.Center[axis];

    /// <summary>
    /// Places <paramref name="entry"/> at <paramref name="spot"/> as one undo step, wired by the pinned actors, else the
    /// selection (<see cref="PlaceableWiring"/>), selects it, and says in the status bar what still needs wiring (else
    /// what was wired) and in the log all of it and where it went.
    /// </summary>
    public static void Place(EditorContext ctx, Placeable entry, PlaceableSpot spot, ViewKind view)
    {
        IReadOnlyList<string> pinned = PinnedIn(ctx.Project);
        List<string> selected = pinned.Count > 0 ? pinned.ToList()
            : ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Actor).Select(r => r.Id).ToList();
        PlacedPlaceable placed = PlaceableWiring.Place(ctx.Project, entry, spot.Location, spot.Rotation, selected, EditorServices.Classes);
        ctx.Execute(placed.Command);
        ctx.Selection.Set([ObjectRef.Actor(placed.Id)]);
        string text = placed.Describe(entry.Title, $"{spot.On} at {ctx.Project.FindActor(placed.Id)?.Location} in the {view} view");
        ctx.Log(pinned.Count > 0 ? $"{text} The pinned {string.Join(", ", pinned)} stood for the selection." : text,
            placed.Needs.Count > 0 ? Severity.Warning : Severity.Info);
        ctx.SetStatus(placed.Status(entry.Title));
    }

    /// <summary>The faint copy of the placeable's mesh in the views (null hides it).</summary>
    public static void ShowGhost(Placeable? entry, PlaceableSpot? spot) =>
        ViewportLayout.Instance?.Scene.ShowPlacementGhost(entry is not null && spot is { } s
            ? new Actor("ghost", entry.Class) { Location = s.Location, Rotation = s.Rotation }
            : null, SceneStyle.DropTarget);

    /// <summary>Draws where the placeable would go over a view: a mark and its facing, a camera's cone and sweep and
    /// its wall mount, a laser's beam, a presence trigger's radius, and the class's icon for a class with no mesh to
    /// show.</summary>
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

        // No mesh to show (an alarm, a switch, or a device whose mesh ships nowhere): the class's icon marks the spot,
        // as the dot alone is easily missed.
        if (!StaticMeshDrawing.DrawsMesh(ghost) && cam.ToScreen(spot.Location) is { } at)
        {
            const int Size = EditorTheme.IconSizeTile;
            canvas.DrawTextureRect(ActorIcons.For(Placeables.Kind(entry, EditorServices.Classes), colour, Size),
                new Rect2(at - new Vector2(Size, Size) / 2, new Vector2(Size, Size)), false);
        }
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
        if (world.Top is not { } highest)
        {
            return null;
        }

        double top = highest + 64;
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
                return hit.Normal.Z >= 0.7 ? hit : null;
            }
        }

        return null;
    }

    /// <summary>"the wall", "the ceiling" or "the floor" for a surface normal.</summary>
    private static string Surface(Vec3 n) => n.Z >= 0.7 ? "the floor" : n.Z <= -0.7 ? "the ceiling" : "the wall";
}
