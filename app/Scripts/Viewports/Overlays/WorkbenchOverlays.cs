using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>Camera networks: NextCam arrows round each ring with the cameras' HUD names; the camera the CamNet gadget
/// opens on is marked; a selected camera shows how far it turns left and right (when the game limits it).</summary>
public sealed class CameraNetworkOverlay : IViewOverlay
{
    public static readonly Color Net = new(0.45f, 1f, 0.85f);

    public string Id => "camnets";

    public string Title => "Camera Networks";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        string? entry = CameraNetworks.EntryCamera(c.Project);
        foreach (CameraNetwork n in CameraNetworks.Networks(c.Project))
        {
            for (int i = 0; i < n.Cameras.Count; i++)
            {
                NetworkCamera cam = n.Cameras[i];
                if (c.Project.FindActor(cam.Id) is not { } a || !c.IsVisible(a.Editor))
                {
                    continue;
                }

                bool selected = c.IsSelected(ObjectRef.Actor(cam.Id));
                Color col = selected ? Net : SceneStyle.Unselected(Net);
                string mark = string.Equals(cam.Id, entry, StringComparison.OrdinalIgnoreCase) ? " (CamNet opens here)" : "";
                batch.Label(cam.Eye + Vec3.UnitZ * 20, $"{i + 1}. {cam.Name}{mark}", col, selected);
                if (cam.Next is { } next && c.Project.FindActor(next) is { } to)
                {
                    batch.Arrow(cam.Eye, to.Location, col, selected ? 2 : 1, dashed: !selected);
                }

                if (selected && cam.YawLimited)
                {
                    double yaw = cam.Rotation.Yaw;
                    batch.Polyline(OverlayShapes.Arc(cam.Eye, Vec3.UnitX, Vec3.UnitY, 96, Rad(yaw + cam.YawLimits.Min), Rad(yaw + cam.YawLimits.Max), 24),
                        col, 1.5f);
                }
            }
        }
    }

    private static double Rad(double rotatorUnits) => rotatorUnits * Math.PI / 32768;
}

/// <summary>Zone tips as their cylinders (guides) with the start of their hint, and help path markers.</summary>
public sealed class TipsOverlay : IViewOverlay
{
    public string Id => "tips";

    public string Title => "Zone Tips and Help Paths";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        foreach (Actor a in c.Project.Actors.Where(a => c.IsVisible(a.Editor) && HelpTips.IsTip(a)))
        {
            ZoneTip t = HelpTips.Tip(a);
            bool selected = c.IsSelected(ObjectRef.Actor(a.Id));
            Color col = !t.EndsGame ? SceneStyle.Guide(selected) : selected ? EditorTheme.Error : SceneStyle.Unselected(EditorTheme.Error);
            Vec3 bottom = t.Center - Vec3.UnitZ * t.Height, top = t.Center + Vec3.UnitZ * t.Height;
            batch.Ring(bottom, Vec3.UnitX, Vec3.UnitY, t.Radius, col, 1, dashed: !selected, segments: 32);
            batch.Ring(top, Vec3.UnitX, Vec3.UnitY, t.Radius, col, 1, dashed: !selected, segments: 32);
            if (selected)
            {
                foreach (Vec3 side in new[] { Vec3.UnitX, -Vec3.UnitX, Vec3.UnitY, -Vec3.UnitY })
                {
                    batch.Line(bottom + side * t.Radius, top + side * t.Radius, col, 1);
                }
            }

            batch.Label(top, (t.EndsGame ? "Ends the game: " : "") + t.Spy, t.EndsGame ? EditorTheme.Error : SceneStyle.OverlayText, selected);
        }

        foreach (Actor a in c.Project.Actors.Where(a => c.IsVisible(a.Editor) && HelpTips.IsMarker(a)))
        {
            batch.Dot(a.Location, SceneStyle.OverlayGuide, 3);
        }
    }
}

/// <summary>A selected SMagicEvent's actions: an arrow to each receiver with its delay and type; while the workbench
/// previews it, what has fired by the preview time is drawn bright, with the events other SMagicEvents passed on and
/// each trigger light the events reached marked on or off.</summary>
public sealed class MagicEventOverlay : IViewOverlay
{
    private static readonly Color Pending = new(0.8f, 0.6f, 1f);
    private static readonly Color Fired = new(SceneStyle.OverlayLink, 1);
    private static readonly Color LightOff = SceneStyle.OverlayOff;

    public string Id => "magic";

    public string Title => "SMagicEvent Actions (Selection)";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        GameplayGraph graph = GameplayState.Instance.Graph;
        foreach (ObjectRef r in c.Selected.Where(r => r.Kind == ObjectKind.Actor).Take(16))
        {
            if (c.Project.FindActor(r.Id) is not { } a || !MagicEvents.IsMagicEvent(a))
            {
                continue;
            }

            bool previewing = string.Equals(MagicPreview.Id, a.Id, StringComparison.OrdinalIgnoreCase);
            List<MagicGroup> groups = MagicEvents.Read(a);
            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].Actions.Count; i++)
                {
                    MagicAction action = groups[g].Actions[i];
                    int group = g, index = i;
                    bool fired = previewing && MagicPreview.Firings.Any(f => f.Group == group && f.Action == index && f.Time <= MagicPreview.Time);
                    Color col = fired ? Fired : Pending;
                    foreach (string id in MagicEvents.Receivers(graph, action.Event))
                    {
                        if (c.Project.FindActor(id) is { } to)
                        {
                            batch.Arrow(a.Location, to.Location, col, fired ? 2.5f : 1.5f, dashed: !fired);
                            batch.Label(Vec3.Lerp(a.Location, to.Location, 0.5), $"{action.Delay:0.##} s {MagicEvents.TypeWords(action.Type)}", col, pinned: true);
                        }
                    }
                }
            }
        }

        if (MagicPreview.State is { } state)
        {
            PreviewState(c, batch, state);
        }
    }

    private static void PreviewState(OverlayContext c, OverlayBatch batch, EventState state)
    {
        // Events relays passed on, drawn from the relay (the previewed event's own are drawn above).
        foreach (EventDelivery d in state.Deliveries.Where(d => !string.Equals(d.From, MagicPreview.Id, StringComparison.OrdinalIgnoreCase)))
        {
            if (c.Project.FindActor(d.From) is { } from && c.Project.FindActor(d.Receiver) is { } to && c.IsVisible(to.Editor))
            {
                batch.Arrow(from.Location, to.Location, Fired, 1.5f, dashed: false);
                batch.Label(Vec3.Lerp(from.Location, to.Location, 0.5), $"{d.Time:0.##} s {(d.Trigger ? "trigger" : "untrigger")}", Fired, pinned: true);
            }
        }

        foreach (string id in state.Deliveries.Select(d => d.Receiver).Where(state.Lights.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (c.Project.FindActor(id) is { } light && c.IsVisible(light.Editor))
            {
                bool on = state.Lights[id];
                Color col = on ? Fired : LightOff;
                batch.Ring(light.Location, Vec3.UnitX, Vec3.UnitY, 40, col, on ? 2.5f : 1.5f, dashed: !on, segments: 20);
                batch.Label(light.Location + Vec3.UnitZ * 48, on ? "on" : "off", col, pinned: true);
            }
        }
    }
}
