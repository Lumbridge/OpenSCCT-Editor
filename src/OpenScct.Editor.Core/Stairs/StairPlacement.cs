using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Scripting;

namespace OpenScct.Editor.Core.Stairs;

public static class StairPlacement
{
    /// <summary>Checks the clearance after the proposed cuts, without changing the authored map.</summary>
    public static string? Problem(MapProject p, Brush candidate, string? folder)
    {
        if (candidate.Stair is not { } s) return "Not a staircase";
        if (s.Carve && Compile.BakedContent.BakedWorldKept(p)) return "Disable clearance cutting for this retained baked world";
        var copy = Session.ProjectSnapshot.Take(p);
        copy.Brushes.RemoveAll(b => b.Id == candidate.Id); copy.Brushes.Add(candidate);
        var world = new WorldGeometry(copy, folder);
        double clearance = Validation.DesignNumbers.Of(p).StandClearance;
        foreach (var step in StairGeometry.Steps(s))
        {
            Vec3 middle = candidate.LocalToWorld((step.A + step.B + step.C + step.D) / 4 + Vec3.UnitZ * step.Top);
            if (world.IsSolid(middle + Vec3.UnitZ * clearance)) return "Not enough standing headroom along this staircase; increase clearance or move the stairs";
        }
        var steps = StairGeometry.Steps(s);
        Vec3 forward = ((steps[0].C + steps[0].D - steps[0].A - steps[0].B) / 2).Normalized();
        Vec3 exit = ((steps[^1].C + steps[^1].D - steps[^1].A - steps[^1].B) / 2).Normalized();
        Vec3 bottom = candidate.LocalToWorld(-forward * 48 + Vec3.UnitZ * (clearance / 2));
        Vec3 top = candidate.LocalToWorld((steps[^1].C + steps[^1].D) / 2 + exit * 48 + Vec3.UnitZ * (s.Rise + clearance / 2));
        if (world.IsSolid(bottom)) return "The bottom entrance is blocked by solid geometry; move the stairs or open the approach";
        if (world.IsSolid(top)) return "The upper exit is blocked by solid geometry; connect to an open upper floor";
        return null;
    }
}
