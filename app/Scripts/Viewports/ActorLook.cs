using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Viewports;

/// <summary>How an actor is drawn: a class-coloured box (its collision cylinder's bounds when it has one) with a
/// direction arrow, an octahedron for lights, or its own brush shape for volumes and movers.</summary>
public readonly record struct ActorLook(Color Color, Vec3 HalfExtent, bool Diamond)
{
    public static readonly Color Default = new(0.85f, 0.85f, 0.88f);

    /// <summary>The look of an actor; class families are matched by name so it works without the class catalog.</summary>
    public static ActorLook For(Actor a)
    {
        string c = a.ShortClass;
        bool Has(string part) => c.Contains(part, StringComparison.OrdinalIgnoreCase);
        Color color;
        double radius = 16, half = 16;
        bool diamond = false;
        if (Has("PlayerStart"))
        {
            color = a.Props.GetInt("TeamNumber") == 1 ? SceneStyle.TeamMerc : SceneStyle.TeamSpy;
            radius = 35;
            half = 87.5;
        }
        else if (Has("Light"))
        {
            // A pale cream, not yellow: yellow is the selection's.
            color = new Color(0.98f, 0.94f, 0.8f);
            diamond = true;
        }
        else if (Has("Objective") || Has("Mission"))
        {
            color = new Color(0.95f, 0.4f, 0.95f);
            radius = half = 24;
        }
        else if (Has("Camera") || Has("Laser") || Has("Sensor") || Has("Alarm") || Has("Mine"))
        {
            color = new Color(1f, 0.35f, 0.45f);
        }
        else if (Has("Pickup") || Has("Inventory") || Has("Ammo"))
        {
            color = new Color(0.55f, 1f, 0.45f);
        }
        else if (Has("Trigger") || Has("Volume") || Has("Zone"))
        {
            color = new Color(0.4f, 0.9f, 0.85f);
        }
        else if (Has("Mover") || Has("Door") || Has("Elevator"))
        {
            color = new Color(0.75f, 0.5f, 1f);
        }
        else if (Has("Emitter") || Has("Sound") || Has("Ambient"))
        {
            color = new Color(1f, 0.65f, 0.3f);
        }
        else if (Has("StaticMesh"))
        {
            color = new Color(0.62f, 0.66f, 0.72f);
            radius = half = 32;
        }
        else
        {
            color = Default;
        }

        radius = Collision(a, "CollisionRadius", radius);
        half = Collision(a, "CollisionHeight", half);
        return new ActorLook(color, new Vec3(radius, radius, half), diamond);
    }

    private static double Collision(Actor a, string prop, double fallback)
    {
        if (a.Props[prop] is SvJsonNumber)
        {
            return Math.Max(4, a.Props.GetFloat(prop));
        }

        return EditorServices.Classes?.Default(a.Class, prop) is SvJsonNumber n && PropValues.Num(n) is var v && v > 0
            ? Math.Max(4, v)
            : fallback;
    }
}
