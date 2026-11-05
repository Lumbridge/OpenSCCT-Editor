using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Tools;

/// <summary>
/// Rotator arithmetic the transform tools need: turning an object about a world axis. A UE2 rotator applies roll,
/// then pitch, then yaw, so only a yaw turn is a plain addition; turns about world X or Y compose the matrices and
/// read the rotator back the way UE2's <c>FMatrix::Rotator</c> does.
/// </summary>
public static class RotationMath
{
    /// <summary>The rotator that turns about one world axis (0 X, 1 Y, 2 Z) by <paramref name="units"/>.</summary>
    public static Rot About(int axis, int units) => axis switch
    {
        0 => new Rot(0, 0, units),
        1 => new Rot(units, 0, 0),
        _ => new Rot(0, units, 0),
    };

    /// <summary><paramref name="r"/> turned about a world axis. Yaw turns stay exact (no wrap or rounding).</summary>
    public static Rot TurnAbout(Rot r, int axis, int units)
    {
        if (units == 0)
        {
            return r;
        }

        if (axis == 2)
        {
            return r with { Yaw = r.Yaw + units };
        }

        Rot turn = About(axis, units);
        (Vec3 f, Vec3 right, Vec3 up) = r.Axes();
        return FromAxes(turn.Rotate(f), turn.Rotate(right), turn.Rotate(up));
    }

    /// <summary>The rotator of a rotation given by its forward, right and up axes (UE2 FMatrix::Rotator).</summary>
    public static Rot FromAxes(Vec3 forward, Vec3 right, Vec3 up)
    {
        double pitch = Math.Atan2(forward.Z, Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y));
        double yaw = Math.Atan2(forward.Y, forward.X);
        // The right axis the pitch and yaw alone would give; roll is the angle from it.
        var flatRight = new Vec3(-Math.Sin(yaw), Math.Cos(yaw), 0);
        double roll = Math.Atan2(Vec3.Dot(up, flatRight), Vec3.Dot(right, flatRight));
        return new Rot(ToUnits(pitch), ToUnits(yaw), ToUnits(roll));
    }

    /// <summary>The local axis (0 X, 1 Y, 2 Z) of <paramref name="r"/> that lies closest to a world axis, so a
    /// world-axis scale can go into a local scale without shearing.</summary>
    public static int LocalAxisNearest(Rot r, int worldAxis)
    {
        if (r == Rot.Zero)
        {
            return worldAxis;
        }

        (Vec3 f, Vec3 right, Vec3 up) = r.Axes();
        double[] along = [Math.Abs(f[worldAxis]), Math.Abs(right[worldAxis]), Math.Abs(up[worldAxis])];
        return along[0] >= along[1] && along[0] >= along[2] ? 0 : along[1] >= along[2] ? 1 : 2;
    }

    private static int ToUnits(double radians) => (int)Math.Round(radians * Rot.UnitsPerTurn / (Math.PI * 2));
}
