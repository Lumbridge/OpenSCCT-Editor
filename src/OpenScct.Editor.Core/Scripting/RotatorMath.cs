using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>Rotator helpers in UE2 conventions (65536 units per turn, FRotationMatrix axes).</summary>
public static class RotatorMath
{
    private const double UnitsPerRadian = Rot.UnitsPerTurn / (2 * Math.PI);

    /// <summary>The rotator of a rotation given by its axes (UE2 FMatrix::Rotator).</summary>
    public static Rot FromAxes(Vec3 forward, Vec3 right, Vec3 up)
    {
        double pitch = Math.Atan2(forward.Z, Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y));
        double yaw = Math.Atan2(forward.Y, forward.X);
        var noRoll = new Rot(Units(pitch), Units(yaw), 0);
        Vec3 sy = noRoll.Axes().Right;
        double roll = Math.Atan2(Vec3.Dot(up, sy), Vec3.Dot(right, sy));
        return Tidy(new Rot(Units(pitch), Units(yaw), Units(roll)));
    }

    /// <summary><paramref name="rotation"/> followed by a world-space rotation <paramref name="delta"/>.</summary>
    public static Rot Compose(Rot rotation, Rot delta)
    {
        if (delta.Pitch == 0 && delta.Roll == 0 && rotation.Pitch == 0 && rotation.Roll == 0)
        {
            return Tidy(new Rot(0, rotation.Yaw + delta.Yaw, 0));
        }

        (Vec3 f, Vec3 r, Vec3 u) = rotation.Axes();
        return FromAxes(delta.Rotate(f), delta.Rotate(r), delta.Rotate(u));
    }

    /// <summary>The rotator that looks from <paramref name="from"/> at <paramref name="to"/> (no roll).</summary>
    public static Rot LookAt(Vec3 from, Vec3 to)
    {
        Vec3 d = to - from;
        return Tidy(new Rot(Units(Math.Atan2(d.Z, Math.Sqrt(d.X * d.X + d.Y * d.Y))), Units(Math.Atan2(d.Y, d.X)), 0));
    }

    /// <summary>The yaw that faces along a horizontal direction.</summary>
    public static int YawOf(Vec3 direction) => Rot.Wrap(Units(Math.Atan2(direction.Y, direction.X)));

    /// <inheritdoc cref="Rot.Tidy"/>
    public static Rot Tidy(Rot r) => r.Tidy();

    public static int Units(double radians) => (int)Math.Round(radians * UnitsPerRadian);
}
