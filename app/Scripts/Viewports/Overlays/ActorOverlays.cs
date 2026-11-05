using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>Class tests and property readers the actor overlays share.</summary>
internal static class OverlayActors
{
    public static bool IsA(Actor a, string fullClass) => ClassDefaults.IsA(a, fullClass);

    /// <summary>The world edges of a brush-shaped actor (volumes, zone brushes).</summary>
    public static IEnumerable<(Vec3 A, Vec3 B)> ShapeEdges(Actor a)
    {
        if (a.Shape is null)
        {
            yield break;
        }

        foreach (BrushFace f in a.Shape.Faces)
        {
            for (int i = 0; i < f.Vertices.Count; i++)
            {
                yield return (a.LocalToWorld(f.Vertices[i]), a.LocalToWorld(f.Vertices[(i + 1) % f.Vertices.Count]));
            }
        }
    }

    public static Vec3 ShapeCentre(Actor a)
    {
        if (a.Shape is not { Faces.Count: > 0 } shape)
        {
            return a.Location;
        }

        List<Vec3> all = shape.Faces.SelectMany(f => f.Vertices).Select(a.LocalToWorld).ToList();
        return all.Aggregate(Vec3.Zero, (s, v) => s + v) / all.Count;
    }
}

/// <summary>Player starts as rings in their team's colour with a facing arrow: spies green, mercenaries red,
/// mercenary deathmatch starts orange, disabled starts dimmed.</summary>
public sealed class TeamStartOverlay : IViewOverlay
{
    public static readonly Color Spy = SceneStyle.TeamSpy;
    public static readonly Color Merc = SceneStyle.TeamMerc;
    public static readonly Color MercDeathmatch = new(1f, 0.6f, 0.2f);

    public string Id => "starts";

    public string Title => "Team Starts";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        foreach (Actor a in c.Project.Actors.Where(a => c.IsVisible(a.Editor) && OverlayActors.IsA(a, "Engine.PlayerStart")))
        {
            int team = a.Props.GetInt("TeamNumber");
            bool selected = c.IsSelected(ObjectRef.Actor(a.Id));
            bool mdm = a.Props.GetBool("bMercDeathMatchStart");
            Color col = mdm ? MercDeathmatch : team == 0 ? Spy : Merc;
            if (!a.Props.GetBool("bEnabled", true))
            {
                col = col.Darkened(0.5f);
            }

            Vec3 feet = a.Location;
            batch.Ring(feet, Vec3.UnitX, Vec3.UnitY, 28, col, 2, segments: 24);
            Vec3 fwd = new Rot(0, a.Rotation.Yaw, 0).Axes().Forward;
            batch.Arrow(feet, feet + fwd * 56, col, 2);
            batch.Label(feet + Vec3.UnitZ * 40, mdm ? "MDM" : team == 0 ? "Spy" : team == 1 ? "Merc" : $"Team {team}", col, selected);
        }
    }
}

/// <summary>Mover paths: the line through the key positions (BasePos + KeyPos[n]) with each key numbered; the key the
/// round starts at is ringed and, for a selected mover, each key shows when one activation reaches it (the mover's
/// shape at its other keys is drawn faintly by <c>MoverGhosts</c> while this overlay is on).</summary>
public sealed class MoverOverlay : IViewOverlay
{
    private static readonly Color Path = new(0.55f, 0.85f, 1f);

    public string Id => "movers";

    public string Title => "Mover Paths";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        foreach (Actor a in c.Project.Actors.Where(a => c.IsVisible(a.Editor) && MoverBase.IsMover(a)))
        {
            MoverSetup m = MoverSetup.Read(a);
            if (m.Keys.Count < 2)
            {
                continue;
            }

            bool selected = c.IsSelected(ObjectRef.Actor(a.Id));
            Color col = selected ? Path : new Color(Path, 0.55f);
            IReadOnlyList<double?> reached = selected ? MoverTimeline.Build(m).KeyTimes() : [];
            for (int i = 0; i < m.Keys.Count; i++)
            {
                Vec3 at = m.KeyPose(i).Location;
                batch.Dot(at, col, i == 0 ? 4 : 3);
                if (i == m.StartKey)
                {
                    batch.Ring(at, Vec3.UnitX, Vec3.UnitY, 10, col, 1.5f, segments: 16);
                }

                if (i > 0)
                {
                    batch.Arrow(m.KeyPose(i - 1).Location, at, col, selected ? 2 : 1, dashed: !selected);
                }

                if (selected)
                {
                    string when = reached[i] is { } t ? $" at {t:0.##} s" : "";
                    batch.Label(at, $"Key {i}{(i == m.StartKey ? " (start)" : "")}{(i == m.OpenKey ? " (last)" : "")}{when}", col, pinned: true);
                }
            }
        }
    }
}

/// <summary>Volume outlines (blocking, damage, physics and other volumes) with their class name; selected ones brighter.</summary>
public sealed class VolumeOverlay : IViewOverlay
{
    public string Id => "volumes";

    public string Title => "Volume Shapes";

    public bool OnByDefault => false;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        foreach (Actor a in c.Project.Actors.Where(a => a.Shape is { Faces.Count: > 0 } && c.IsVisible(a.Editor) && OverlayActors.IsA(a, "Engine.Volume")))
        {
            bool selected = c.IsSelected(ObjectRef.Actor(a.Id));
            Color col = selected ? ColourOf(a) : SceneStyle.Unselected(ColourOf(a));

            foreach ((Vec3 p, Vec3 q) in OverlayActors.ShapeEdges(a))
            {
                batch.Line(p, q, col, 1);
            }

            batch.Label(OverlayActors.ShapeCentre(a), a.ShortClass, col, selected);
        }
    }

    private static Color ColourOf(Actor a) => a.ShortClass switch
    {
        "BlockingVolume" => new Color(0.75f, 0.75f, 0.8f),
        var s when s.Contains("Damage", StringComparison.OrdinalIgnoreCase) => new Color(1f, 0.35f, 0.3f),
        var s when s.Contains("Physics", StringComparison.OrdinalIgnoreCase) || s.Contains("Water", StringComparison.OrdinalIgnoreCase)
            => new Color(0.35f, 0.6f, 1f),
        _ => new Color(0.6f, 0.95f, 0.75f),
    };
}

/// <summary>Zones and portals: each ZoneInfo's name where it stands, and the portal brushes that split the zones.</summary>
public sealed class ZoneOverlay : IViewOverlay
{
    public string Id => "zones";

    public string Title => "Zones and Portals";

    public bool OnByDefault => false;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        foreach (Actor a in c.Project.Actors.Where(a => c.IsVisible(a.Editor) && OverlayActors.IsA(a, "Engine.ZoneInfo")))
        {
            bool selected = c.IsSelected(ObjectRef.Actor(a.Id));
            batch.Dot(a.Location, SceneStyle.Guide(selected), 4);
            string name = a.Props.GetString("LocationName") is { Length: > 0 } n ? $"{a.Id}: {n}" : a.Id;
            batch.Label(a.Location, name, SceneStyle.OverlayText, selected);
        }

        foreach (Brush b in c.Project.Brushes.Where(b => b.IsPortal && c.IsVisible(b.Editor)))
        {
            WorldBrush world = BrushGeometry.Of(b);
            foreach (WorldFace f in world.Faces)
            {
                batch.Polyline(f.Vertices, SceneStyle.PortalBrush, 2, closed: true);
            }
        }
    }
}

/// <summary>The reach of selected lights: R = 25 x (LightRadius + 1) units, as a sphere's three circles (a guide, so
/// it never reads as part of the selection).</summary>
public sealed class LightRadiusOverlay : IViewOverlay
{
    public string Id => "lights";

    public string Title => "Light Radius (Selected)";

    public bool OnByDefault => true;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        foreach (ObjectRef r in c.Selected.Where(r => r.Kind == ObjectKind.Actor))
        {
            if (c.Project.FindActor(r.Id) is not { } a || !OverlayActors.IsA(a, "Engine.Light"))
            {
                continue;
            }

            double radius = 25 * (ClassDefaults.Number(a, "LightRadius", 64) + 1);
            batch.Sphere(a.Location, radius, SceneStyle.Guide(selected: true));
            batch.Label(a.Location + Vec3.UnitZ * radius, $"Radius {radius:0}", SceneStyle.OverlayText, pinned: true);
        }
    }
}
