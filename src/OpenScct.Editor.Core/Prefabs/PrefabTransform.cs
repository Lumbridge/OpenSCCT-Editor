using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Prefabs;

/// <summary>
/// Moves objects between prefab space and the map: locations, rotations, GE points, and the world-space properties
/// movers keep (BasePos, BaseRot, and KeyPos offsets).
/// </summary>
public readonly record struct PrefabTransform(Func<Vec3, Vec3> Point, Func<Vec3, Vec3> Direction, Func<Rot, Rot> Turn)
{
    /// <summary>Prefab space to the map: the pivot goes to <paramref name="location"/>, turned by <paramref name="rotation"/>.</summary>
    public static PrefabTransform Place(Vec3 location, Rot rotation) => new(
        p => Clean(location + rotation.Rotate(p)),
        v => Clean(rotation.Rotate(v)),
        r => RotatorMath.Compose(r, rotation));

    /// <summary>The map to prefab space: <paramref name="pivot"/> becomes the origin and a group turned by
    /// <paramref name="yaw"/> faces +X again.</summary>
    public static PrefabTransform Local(Vec3 pivot, int yaw)
    {
        var turn = new Rot(0, yaw, 0);
        var back = new Rot(0, -yaw, 0);
        return new(p => Clean(turn.Unrotate(p - pivot)), v => Clean(turn.Unrotate(v)), r => RotatorMath.Compose(r, back));
    }

    /// <summary>Drops the rounding noise of a turn (a quarter turn gives 3.9e-15 for 0).</summary>
    private static Vec3 Clean(Vec3 v) => new(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));

    public void Apply(Brush b)
    {
        b.Location = Point(b.Location);
        b.Rotation = Turn(b.Rotation);
    }

    public void Apply(Actor a)
    {
        a.Location = Point(a.Location);
        a.Rotation = Turn(a.Rotation);
        if (PropValues.TryVector(a.Props["BasePos"], out Vec3 basePos))
        {
            a.Props["BasePos"] = PropValues.Vector(Point(basePos));
        }

        if (PropValues.TryRotator(a.Props["BaseRot"], out Rot baseRot))
        {
            a.Props["BaseRot"] = PropValues.Rotator(Turn(baseRot));
        }

        MoverBase.MapKeyPos(a, Direction);
    }

    public void Apply(Ge g) => g.Points = g.Points.Select(Point).ToList();
}
