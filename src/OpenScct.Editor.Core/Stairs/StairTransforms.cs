using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Stairs;

public static class StairTransforms
{
    public static void Resize(Brush before, Brush after, bool rebuild = true)
    {
        static double Ratio(double a, double b) => Math.Abs(b) > 1e-9 ? a / b : throw new ArgumentException("Cannot resize from zero scale");
        Vec3 factor = new(Ratio(after.MainScale.X, before.MainScale.X), Ratio(after.MainScale.Y, before.MainScale.Y), Ratio(after.MainScale.Z, before.MainScale.Z));
        if (Math.Min(Math.Abs(factor.X), Math.Min(Math.Abs(factor.Y), Math.Abs(factor.Z))) < .01)
            throw new ArgumentException("Stair dimensions cannot collapse to zero");
        var s = after.Stair!;
        s.Run *= Math.Abs(factor.X); s.Width *= Math.Abs(factor.Y); s.Rise *= Math.Abs(factor.Z);
        s.CentralColumnRadius *= Math.Abs(factor.X);
        s.LandingLength = Math.Max(s.Width, s.LandingLength * Math.Abs(factor.X)); s.InnerRadius *= Math.Abs(factor.X);
        // Keep the profile and texture scale in world units; mirrors remain in the transform.
        after.MainScale = new Vec3(Math.CopySign(Math.Abs(before.MainScale.X), after.MainScale.X),
            Math.CopySign(Math.Abs(before.MainScale.Y), after.MainScale.Y), Math.CopySign(Math.Abs(before.MainScale.Z), after.MainScale.Z));
        if (rebuild) StairGeometry.Rebuild(after);
    }
}
