using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>Rotators from directions (the inverse of <see cref="Rot.Axes"/>).</summary>
public static class Orientation
{
    /// <summary>The rotator whose forward axis is <paramref name="forward"/> and whose up axis is as close to
    /// <paramref name="up"/> as a rotation allows.</summary>
    public static Rot FromAxes(Vec3 forward, Vec3 up)
    {
        Vec3 f = forward.Normalized();
        if (f == Vec3.Zero)
        {
            return Rot.Zero;
        }

        Vec3 right = Vec3.Cross(up, f);
        if (right.Length < 1e-9)
        {
            right = Vec3.Cross(Vec3.UnitZ, f).Length > 1e-9 ? Vec3.Cross(Vec3.UnitZ, f) : Vec3.UnitY;
        }

        right = right.Normalized();
        Vec3 u = Vec3.Cross(f, right);
        double pitch = Math.Atan2(f.Z, Math.Sqrt(f.X * f.X + f.Y * f.Y));
        double yaw = Math.Atan2(f.Y, f.X);
        double roll = Math.Atan2(-right.Z, u.Z);
        return new Rot(Units(pitch), Units(yaw), Units(roll)).Normalized();

        static int Units(double radians) => (int)Math.Round(radians * Rot.UnitsPerTurn / (2 * Math.PI));
    }

    /// <summary>Tilts a rotation so its up axis is <paramref name="normal"/>, keeping its heading as far as possible
    /// (RE+ "align to slope").</summary>
    public static Rot AlignUp(Rot rotation, Vec3 normal)
    {
        Vec3 n = normal.Normalized();
        if (n == Vec3.Zero)
        {
            return rotation;
        }

        Vec3 heading = new Rot(0, rotation.Yaw, 0).Axes().Forward;
        Vec3 forward = heading - n * Vec3.Dot(heading, n);
        if (forward.Length < 1e-6)
        {
            forward = Vec3.Cross(n, Vec3.UnitY);
        }

        return FromAxes(forward, n);
    }
}

/// <summary>
/// Drop to floor and ceiling (RE+ End, Ctrl+End) against the map's geometry: the CSG brushes in order plus an
/// imported map's baked collision. The actor's half height decides where its centre ends up.
/// </summary>
public static class ActorPlacement
{
    /// <summary>The farthest a drop looks.</summary>
    public const double MaxDrop = 65536;

    /// <summary>
    /// Where an actor at <paramref name="location"/> lands: casts from its centre straight down (or up for the
    /// ceiling) and stands its bottom (top) on the first surface. Null when nothing is in the way.
    /// </summary>
    public static (Vec3 Location, Vec3 Normal)? Drop(WorldGeometry world, Vec3 location, double halfHeight, bool ceiling = false)
    {
        Vec3 dir = ceiling ? Vec3.UnitZ : -Vec3.UnitZ;
        if (world.Raycast(location, dir, MaxDrop) is not { } hit)
        {
            return null;
        }

        double z = ceiling ? hit.Point.Z - halfHeight : hit.Point.Z + halfHeight;
        return (location with { Z = Poly.Tidy(z) }, hit.Normal);
    }

    /// <summary>How high above the floor a grounded play start is put: inside the game's 200 uu floor probe, which
    /// then stands the pawn on that floor, and above the tallest step a player walks up.</summary>
    public const double PlayStartAboveFloor = 96;

    /// <summary>Surfaces at least this level are floors a player stands on (the game walks a normal Z of 0.7).</summary>
    private const double WalkableNormalZ = 0.7;

    /// <summary>How far back from the surface it looks at a start comes down, so the player does not start in it.</summary>
    private const double LookBack = 48;

    /// <summary>
    /// A play start moved onto a floor the player can stand on, keeping its view. An editor camera floats well above
    /// the floor, and the game only looks 200 uu down, so the start goes to the floor under it when it is in the open;
    /// a camera outside the level (in solid, or over nothing) starts the player on the floor under the spot it looks
    /// at. Throws <see cref="ArgumentException"/> when neither has a floor.
    /// </summary>
    public static PlayStart OnFloor(WorldGeometry world, PlayStart start)
    {
        if (FloorUnder(world, start.Location) is { } here)
        {
            return start with { Location = here };
        }

        (Vec3 forward, _, _) = start.Rotation.Axes();
        if (world.Raycast(start.Location, forward) is { } hit && FloorUnder(world, hit.Point - forward * LookBack + Vec3.UnitZ) is { } there)
        {
            return start with { Location = there };
        }

        throw new ArgumentException(FormattableString.Invariant(
            $"no floor to start on under {start.Location} or where it looks (the start is outside the level, or the map has no floor there)"));
    }

    /// <summary>The start point on the walkable floor straight under <paramref name="at"/>, or null when
    /// <paramref name="at"/> is in solid or nothing walkable is under it.</summary>
    private static Vec3? FloorUnder(WorldGeometry world, Vec3 at)
    {
        if (world.IsSolid(at) || world.Raycast(at, -Vec3.UnitZ, MaxDrop) is not { } floor || floor.Normal.Z < WalkableNormalZ)
        {
            return null;
        }

        return floor.Point with { Z = Poly.Tidy(floor.Point.Z + PlayStartAboveFloor) };
    }

    /// <summary>
    /// How far an actor reaches below its location, for dropping: a shaped actor (volume, mover) by its shape, others
    /// by their collision cylinder (stored CollisionHeight, else the class default; static meshes scaled by their draw
    /// scale), else <paramref name="fallback"/>. The scripting API's <c>on</c> placement stands actors the same way.
    /// </summary>
    public static double HalfHeight(Actor a, double fallback = 16)
    {
        if (a.Shape is { Faces.Count: > 0 })
        {
            return Math.Max(0, a.Location.Z - MapObjects.ActorBounds(a).Min.Z);
        }

        double height = MapObjects.CollisionSize(a).Height;
        if (height <= 0)
        {
            return fallback;
        }

        return a.ShortClass.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase)
            ? height * Math.Abs(a.DrawScale3D.Z * a.DrawScale)
            : height;
    }
}

/// <summary>A measured span between two points, in units, player heights and running time.</summary>
public sealed record Measurement(Vec3 From, Vec3 To, double Distance, double Horizontal, Vec3 Delta, double PlayerHeights,
    double SpySeconds, double MercSeconds)
{
    public static Measurement Between(Vec3 a, Vec3 b, MovementLimits m)
    {
        Vec3 d = b - a;
        double horizontal = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        return new Measurement(a, b, d.Length, horizontal, d, m.StandHeight > 0 ? d.Length / m.StandHeight : 0,
            m.SpyRunSpeed > 0 ? horizontal / m.SpyRunSpeed : 0, m.MercRunSpeed > 0 ? horizontal / m.MercRunSpeed : 0);
    }

    public string Summary() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{Distance:0.#} u ({PlayerHeights:0.##} player heights) · dX {Delta.X:0.#} dY {Delta.Y:0.#} dZ {Delta.Z:0.#} · " +
        $"run spy {SpySeconds:0.##} s, merc {MercSeconds:0.##} s");
}
