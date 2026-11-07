using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Stairs;

public static class StairFit
{
    public static Brush Between(string id, StairDesign template, Vec3 bottom, Vec3 top)
    {
        StairDesign s = template.Clone(); s.Rise = top.Z - bottom.Z;
        Vec3 delta = (top - bottom) with { Z = 0 }; double distance = delta.Length;
        if (s.Rise <= 0) throw new ArgumentException("Pick a top connection above the bottom connection");
        if (s.Layout == "straight") s.Run = distance;
        else if (s.Layout is "curved" or "spiral")
        {
            double chord = 2 * Math.Abs(Math.Sin(s.TurnDegrees * Math.PI / 360));
            if (chord < 1e-5)
            { if (distance > 1) throw new ArgumentException("A full spiral turn ends above its bottom; reduce total turn to reach an offset floor"); }
            else s.InnerRadius = distance / chord - s.Width / 2;
        }
        else if (s.Layout == "u")
        {
            double netRun = (s.Count / 2 - (s.Count - s.Count / 2)) * s.Run / s.Count;
            if (distance <= Math.Abs(netRun)) throw new ArgumentException("These connections are too close for this U flight; reduce run or use an even step count");
            double requiredWidth = Math.Sqrt(distance * distance - netRun * netRun);
            if (Math.Abs(requiredWidth - s.Width) > 1) throw new ArgumentException($"Set this U staircase's width to {requiredWidth:0.##} to match the selected floors");
        }
        else
        {
            double lo = Math.Max(32, Math.Max(s.Smooth ? s.Rise / .75 : 32, 2 * (s.Overhang + s.EdgeRadius) * s.Count)), hi = Math.Max(65536, distance * 4);
            for (int k = 0; k < 40; k++)
            {
                s.Run = (lo + hi) / 2;
                Vec3 end = End(s);
                if (end.Length < distance) lo = s.Run; else hi = s.Run;
            }
            s.Run = (lo + hi) / 2;
        }
        s.Validate(); Vec3 localEnd = End(s);
        if (Math.Abs(localEnd.Length - distance) > 1) throw new ArgumentException("This layout cannot fit these connections with the current width and landing size");
        double yaw = distance < 1 ? 0 : Math.Atan2(delta.Y, delta.X) - Math.Atan2(localEnd.Y, localEnd.X);
        return StairGeometry.Create(id, s, bottom, new Rot(0, (int)Math.Round(yaw * Rot.UnitsPerTurn / (2 * Math.PI)), 0));
    }

    private static Vec3 End(StairDesign s)
    {
        var step = StairGeometry.Steps(s)[^1]; return (step.C + step.D) / 2;
    }
}
