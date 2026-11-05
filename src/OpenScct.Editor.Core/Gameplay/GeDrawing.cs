using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>Creates the same traversal element from viewport clicks, CLI endpoints or automation.</summary>
public static class GeDrawing
{
    /// <summary>Vertical kinds use the first endpoint's XY and sort the heights; facing points toward the climber.
    /// The caller assigns a map-unique id before adding the returned GE. Null appearance makes only a GE.</summary>
    public static Ge Create(string kind, Vec3 start, Vec3 end, Vec3? facing = null, GeAppearance? appearance = null)
    {
        kind = GeKinds.All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown GE kind '{kind}'.", nameof(kind));
        if (!Finite(start) || !Finite(end) || facing is { } f && !Finite(f))
        {
            throw new ArgumentException("GE endpoints and facing must be finite.");
        }

        List<Vec3> points;
        if (kind is GeKinds.Ladder or GeKinds.Pipe or GeKinds.Pole)
        {
            Vec3 bottom = start with { Z = Math.Min(start.Z, end.Z) };
            double top = Math.Max(start.Z, end.Z);
            if (top - bottom.Z < 4)
            {
                throw new ArgumentException("A vertical GE needs at least 4 units of height.");
            }

            points = GeKinds.HasThirdPoint(kind)
                ? GeShapes.ClimbPoints(bottom, top, facing ?? Vec3.UnitX)
                : [bottom, bottom with { Z = top }];
        }
        else
        {
            points = [start, end];
        }

        if ((points[1] - points[0]).Length < 4)
        {
            throw new ArgumentException("GE endpoints must be at least 4 units apart.");
        }

        if (appearance is not null && !GeGeometry.Supports(kind))
        {
            throw new ArgumentException($"{kind} has no generated appearance; draw its GE on existing geometry.");
        }

        var ge = new Ge(GeShapes.Stem(kind) + "0", kind)
        {
            Points = points.Select(GeEdits.Round).ToList(),
            Appearance = appearance?.Clone(),
        };
        if (ge.Appearance is not null)
        {
            _ = GeGeometry.Build(ge);
        }

        return ge;
    }

    private static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
}
