using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Assets;

/// <summary>
/// Where a static mesh dropped on a surface goes, from the point hit, the surface's normal and the mesh's local box.
/// The box touches the surface and its footprint is centred on the point (a mesh's pivot is often at a corner, so a
/// pivot on the point would put the mesh beside the pointer); the pivot then goes to the grid when the caller snaps.
/// On a floor the mesh stands upright, tilted with the slope as Drop and Align to Slope tilts (a level floor leaves it
/// unturned); on a wall it stands upright with its front (+X) facing out of the wall; under a ceiling it hangs upright.
/// </summary>
public static class MeshDrop
{
    /// <summary>The least normal Z of a floor (45 degrees); a normal Z below minus this is a ceiling, between is a wall.</summary>
    public const double FloorZ = 0.7;

    /// <summary>A normal Z from which a floor counts as level, so baked collision's rounding does not tilt the mesh.</summary>
    private const double LevelZ = 0.999;

    /// <summary>The location and rotation of a mesh with local box <paramref name="min"/>..<paramref name="max"/> dropped
    /// at <paramref name="point"/> on a surface with outward <paramref name="normal"/>. <paramref name="snap"/> moves
    /// the pivot to the grid, kept to the surface's plane, before it is stood off the surface.</summary>
    public static (Vec3 Location, Rot Rotation) OnSurface(Vec3 point, Vec3 normal, Vec3 min, Vec3 max, Func<Vec3, Vec3>? snap = null)
    {
        Vec3 n = normal.Normalized();
        if (n == Vec3.Zero)
        {
            n = Vec3.UnitZ;
        }

        Rot rotation = n.Z >= LevelZ || n.Z <= -FloorZ ? Rot.Zero
            : n.Z >= FloorZ ? Orientation.AlignUp(Rot.Zero, n)
            : new Rot(0, Rot.Wrap((int)Math.Round(Math.Atan2(n.Y, n.X) * Rot.UnitsPerTurn / (2 * Math.PI))), 0);
        Vec3 centre = rotation.Rotate((min + max) * 0.5);
        Vec3 pivot = point - (centre - n * Vec3.Dot(centre, n));
        if (snap is not null)
        {
            // Only the snap's move along the surface is kept: on a slope or a slanted wall the grid point is off the
            // surface's plane, and the mesh would sink into it or float off it.
            Vec3 snapped = snap(pivot);
            pivot = snapped - n * Vec3.Dot(snapped - pivot, n);
        }

        return (pivot + n * Standoff(rotation, n, min, max), rotation);
    }

    /// <summary>How far along <paramref name="normal"/> (unit length) the pivot sits from the surface so the turned box
    /// just touches it.</summary>
    public static double Standoff(Rot rotation, Vec3 normal, Vec3 min, Vec3 max)
    {
        double least = double.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            least = Math.Min(least, Vec3.Dot(rotation.Rotate(corner), normal));
        }

        return -least;
    }
}
