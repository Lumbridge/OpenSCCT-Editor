using System.Globalization;

namespace OpenScct.Editor.Core.Model;

/// <summary>A point or direction in Unreal units and axes (X forward, Y right, Z up; 100 uu = 1 m). Doubles while editing,
/// float32 on disk and in compiled maps.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 One = new(1, 1, 1);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    /// <summary>Component-wise product (scales).</summary>
    public Vec3 Scale(Vec3 s) => new(X * s.X, Y * s.Y, Z * s.Z);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double LengthSquared => X * X + Y * Y + Z * Z;

    public Vec3 Normalized()
    {
        double len = Length;
        return len > 1e-12 ? this / len : Zero;
    }

    public static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    public static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

    /// <summary>Snaps every component to a multiple of <paramref name="grid"/> (no-op for grid &lt;= 0).</summary>
    public Vec3 Snap(double grid) =>
        grid <= 0 ? this : new(Math.Round(X / grid) * grid, Math.Round(Y / grid) * grid, Math.Round(Z / grid) * grid);

    /// <summary>Rounds through float32, as the file and the engine store it.</summary>
    public Vec3 ToFloat32() => new((float)X, (float)Y, (float)Z);

    public double this[int axis] => axis switch { 0 => X, 1 => Y, 2 => Z, _ => throw new ArgumentOutOfRangeException(nameof(axis)) };

    public Vec3 With(int axis, double value) => axis switch
    {
        0 => this with { X = value },
        1 => this with { Y = value },
        2 => this with { Z = value },
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###}, {Z:0.###})");
}

/// <summary>An Unreal rotator: 65536 units per turn. Yaw turns about Z, pitch about Y, roll about X.</summary>
public readonly record struct Rot(int Pitch, int Yaw, int Roll)
{
    public static readonly Rot Zero = new(0, 0, 0);

    public const int UnitsPerTurn = 65536;

    public static double ToRadians(int units) => units * (Math.PI * 2.0 / UnitsPerTurn);

    public static int FromDegrees(double degrees) => (int)Math.Round(degrees * UnitsPerTurn / 360.0);

    public static double ToDegrees(int units) => units * 360.0 / UnitsPerTurn;

    /// <summary>The rotation matrix axes as UE2 builds them (FRotationMatrix / GetAxes): forward, right, up.</summary>
    public (Vec3 Forward, Vec3 Right, Vec3 Up) Axes()
    {
        double sp = Math.Sin(ToRadians(Pitch)), cp = Math.Cos(ToRadians(Pitch));
        double sy = Math.Sin(ToRadians(Yaw)), cy = Math.Cos(ToRadians(Yaw));
        double sr = Math.Sin(ToRadians(Roll)), cr = Math.Cos(ToRadians(Roll));
        var forward = new Vec3(cp * cy, cp * sy, sp);
        var right = new Vec3(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp);
        var up = new Vec3(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp);
        return (forward, right, up);
    }

    /// <summary>Rotates a local vector into world space (row vector times the UE rotation matrix).</summary>
    public Vec3 Rotate(Vec3 v)
    {
        if (this == Zero)
        {
            return v;
        }

        (Vec3 f, Vec3 r, Vec3 u) = Axes();
        return f * v.X + r * v.Y + u * v.Z;
    }

    /// <summary>The inverse of <see cref="Rotate"/>.</summary>
    public Vec3 Unrotate(Vec3 v)
    {
        if (this == Zero)
        {
            return v;
        }

        (Vec3 f, Vec3 r, Vec3 u) = Axes();
        return new Vec3(Vec3.Dot(v, f), Vec3.Dot(v, r), Vec3.Dot(v, u));
    }

    public Rot Normalized() => new(Wrap(Pitch), Wrap(Yaw), Wrap(Roll));

    /// <summary>The same rotation written the usual way: yaw in 0..65535, pitch and roll in -32768..32767 (a camera
    /// looking down keeps a small negative pitch).</summary>
    public Rot Tidy() => new(Signed(Pitch), Wrap(Yaw), Signed(Roll));

    /// <summary>Units wrapped into 0..65535.</summary>
    public static int Wrap(int units) => ((units % UnitsPerTurn) + UnitsPerTurn) % UnitsPerTurn;

    private static int Signed(int units) => Wrap(units + UnitsPerTurn / 2) - UnitsPerTurn / 2;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"(P {Pitch}, Y {Yaw}, R {Roll})");
}
