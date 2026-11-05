using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>
/// What security devices watch: camera view cones with the sweep either side, laser beams (stopped by walls once the
/// traversal world is built), presence and mine radii, and lines from motion sensors to the volumes they watch.
/// </summary>
public sealed class SecurityOverlay : IViewOverlay
{
    /// <summary>Camera view cones: a guide colour, at full strength for the security tool's placement ghost.</summary>
    public static readonly Color CameraColour = new(SceneStyle.OverlayGuide, 1);
    public static readonly Color LaserColour = new(1f, 0.2f, 0.2f);
    public static readonly Color SensorColour = new(0.4f, 0.9f, 1f);

    public string Id => "security";

    public string Title => "Security Devices";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        TraversalWorld? world = GameplayState.Instance.WorldIfBuilt;
        if (world is null && c.Project.Actors.Any(a => SecurityDevices.KindOf(a) == DeviceKind.Laser))
        {
            // Beams are drawn full length until the walls that stop them are known.
            GameplayState.Instance.Prewarm();
        }

        foreach (Actor a in c.Project.Actors.Where(a => c.IsVisible(a.Editor)))
        {
            if (SecurityDevices.KindOf(a) is not { } kind)
            {
                continue;
            }

            bool selected = c.IsSelected(ObjectRef.Actor(a.Id));
            switch (kind)
            {
                case DeviceKind.Camera:
                    Camera(batch, SecurityDevices.Camera(a), selected);
                    break;
                case DeviceKind.Laser:
                    (Vec3 from, Vec3 to) = SecurityDevices.LaserBeam(a, world);
                    batch.Line(from, to, LaserColour, selected ? 3 : 2);
                    break;
                case DeviceKind.MotionSensor:
                    Sensor(c, batch, a, selected);
                    break;
                case DeviceKind.PresenceTrigger or DeviceKind.Mine when SecurityDevices.Radius(a) is { } r:
                    batch.Ring(a.Location, Vec3.UnitX, Vec3.UnitY, r, SensorColour, selected ? 2 : 1);
                    break;
            }
        }
    }

    /// <summary>The cone (four edges and its rim at range) and, dashed, the horizontal arc its sweep covers.</summary>
    public static void Camera(OverlayBatch batch, CameraView v, bool selected)
    {
        Color col = SceneStyle.Guide(selected);
        (Vec3 fwd, Vec3 right, Vec3 up) = v.Rotation.Axes();
        double h = v.HalfAngleDegrees * Math.PI / 180;
        double range = v.Range;
        Vec3 centre = v.Eye + fwd * (range * Math.Cos(h));
        double rim = range * Math.Sin(h);
        foreach (double t in new[] { 0, Math.PI / 2, Math.PI, Math.PI * 1.5 })
        {
            batch.Line(v.Eye, centre + (right * Math.Cos(t) + up * Math.Sin(t)) * rim, col, selected ? 1.5f : 1);
        }

        batch.Ring(centre, right, up, rim, col, 1, segments: 32);
        if (v.SweepDegrees <= 0)
        {
            return;
        }

        // The camera turns its yaw by the sweep either side of where it was placed.
        int sweep = Rot.FromDegrees(v.SweepDegrees);
        var arc = new List<Vec3>();
        for (int i = 0; i <= 24; i++)
        {
            int yaw = v.Rotation.Yaw - sweep + 2 * sweep * i / 24;
            arc.Add(v.Eye + new Rot(v.Rotation.Pitch, yaw, 0).Axes().Forward * range);
        }

        batch.Polyline(arc, col, 1, dashed: true);
        batch.Line(v.Eye, arc[0], col, 1, dashed: true);
        batch.Line(v.Eye, arc[^1], col, 1, dashed: true);
    }

    private static void Sensor(OverlayContext c, OverlayBatch batch, Actor a, bool selected)
    {
        Color col = selected ? SensorColour : SceneStyle.Unselected(SensorColour);
        batch.Dot(a.Location, col, 4);
        foreach (string id in SecurityDevices.SensorVolumes(a))
        {
            if (c.Project.FindActor(id) is not { } volume)
            {
                continue;
            }

            batch.Line(a.Location, OverlayActors.ShapeCentre(volume), col, 1, dashed: true);
            if (selected)
            {
                foreach ((Vec3 p, Vec3 q) in OverlayActors.ShapeEdges(volume))
                {
                    batch.Line(p, q, col, 1.5f);
                }
            }
        }
    }
}

/// <summary>
/// Gameplay links of the selected actors: events they fire (solid arrows, labelled with the event), events that reach
/// them (dim arrows), plain references (dashed), and events that name no tag.
/// </summary>
public sealed class LinkOverlay : IViewOverlay
{
    public static readonly Color Fire = new(SceneStyle.OverlayLink, 1);
    public static readonly Color Incoming = EditorTheme.TimelineEvent;
    public static readonly Color Reference = new(0.75f, 0.75f, 0.8f);

    public string Id => "links";

    public string Title => "Event Links (Selection)";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        if (c.Selected.Count == 0)
        {
            return;
        }

        GameplayGraph graph = GameplayState.Instance.Graph;
        var drawn = new HashSet<GameplayLink>();
        foreach (ObjectRef r in c.Selected.Where(r => r.Kind == ObjectKind.Actor).Take(64))
        {
            foreach (GameplayLink link in graph.Out(r.Id).Concat(graph.In(r.Id)))
            {
                if (drawn.Add(link) && Ends(c.Project, link) is ({ } from, { } to))
                {
                    bool outgoing = string.Equals(link.From, r.Id, StringComparison.OrdinalIgnoreCase);
                    Color col = link.Kind == LinkKind.Ref ? Reference : outgoing ? Fire : Incoming;
                    batch.Arrow(from, to, col, outgoing ? 2 : 1.5f, dashed: link.Kind == LinkKind.Ref);
                    batch.Label(Vec3.Lerp(from, to, 0.5), link.Event is { } e ? $"{link.Via}: {e}" : link.Via, col, pinned: true);
                }
            }

            foreach (DanglingEvent d in graph.Dangling.Where(d => string.Equals(d.From, r.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (c.Project.FindActor(d.From) is { } a)
                {
                    batch.Label(a.Location + Vec3.UnitZ * 24, $"{d.Via} = {d.Event} reaches nothing", EditorTheme.Error, pinned: true);
                }
            }
        }
    }

    private static (Vec3?, Vec3?) Ends(MapProject p, GameplayLink link) =>
        (p.FindActor(link.From)?.Location, p.FindActor(link.To)?.Location);
}
