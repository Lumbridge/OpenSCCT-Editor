using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Builders;

// The UE2 stair builders with the stock parameter names. Each step is its own convex brush (the stock builders make
// one non-convex brush); together they fill the same space. Angles turn from +X towards +Y, which is clockwise seen
// from above in Unreal's axes, like positive yaw.

/// <summary>UE2 linear stair: steps climbing along +X, solid down to the floor.</summary>
public sealed class LinearStairBuilder : BuilderKind
{
    public override string Kind => "linearStair";

    public override string Summary => "Straight stairs (UE2 linear stair builder), one brush per step";

    public override string Origin => "floor at the front edge of the first step, centred across the width";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("stepLength", 32, 1, 4096, "Tread depth (along X)"),
        BuilderParam.Number("stepHeight", 16, 1, 4096, "Rise per step (players climb up to 34)"),
        BuilderParam.Number("stepWidth", 256, 1, 65536, "Width (along Y)"),
        BuilderParam.Int("numSteps", 8, 1, 256, "Number of steps"),
        BuilderParam.Number("addToFirstStep", 0, 0, 65536, "Extra height under every step"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double len = a.Num("stepLength"), rise = a.Num("stepHeight"), w = a.Num("stepWidth") / 2, add = a.Num("addToFirstStep");
        for (int i = 0; i < a.Int("numSteps"); i++)
        {
            yield return BoxPart(new Vec3(i * len, -w, 0), new Vec3((i + 1) * len, w, (i + 1) * rise + add));
        }
    }
}

/// <summary>UE2 curved stair: steps round a centre, solid down to the floor.</summary>
public sealed class CurvedStairBuilder : BuilderKind
{
    public override string Kind => "curvedStair";

    public override string Summary => "Curved stairs (UE2 curved stair builder), one brush per step";

    public override string Origin => "floor at the centre of the curve";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("innerRadius", 240, 0, 65536, "Radius of the inside edge"),
        BuilderParam.Number("stepHeight", 16, 1, 4096, "Rise per step (players climb up to 34)"),
        BuilderParam.Number("stepWidth", 256, 1, 65536, "Tread width from the inside edge out"),
        BuilderParam.Number("angleOfCurve", 90, 1, 360, "Total turn in degrees"),
        BuilderParam.Int("numSteps", 8, 1, 256, "Number of steps"),
        BuilderParam.Number("addToFirstStep", 0, 0, 65536, "Extra height under every step"),
        BuilderParam.Flag("counterClockwise", false, "Turn the other way (counter-clockwise seen from above)"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        int n = a.Int("numSteps");
        double r0 = a.Num("innerRadius"), r1 = r0 + a.Num("stepWidth");
        double step = a.Num("angleOfCurve") / n * Math.PI / 180 * (a.Flag("counterClockwise") ? -1 : 1);
        for (int i = 0; i < n; i++)
        {
            double top = (i + 1) * a.Num("stepHeight") + a.Num("addToFirstStep");
            yield return Solid(StepSolid.Sector(r0, r1, i * step, (i + 1) * step, 0, 0, top, top));
        }
    }
}

/// <summary>UE2 spiral stair: floating steps of a given thickness winding round a centre.</summary>
public sealed class SpiralStairBuilder : BuilderKind
{
    public override string Kind => "spiralStair";

    public override string Summary => "Spiral stairs (UE2 spiral stair builder), floating steps, one brush per step";

    public override string Origin => "floor at the centre of the spiral";

    // Defaults are sized for SCCT bodies: 16 steps of 16 per turn leave 256 - 32 = 224 head room (a standing player
    // needs 192), and 192 wide treads fit a 35-radius body with room to spare.
    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("innerRadius", 64, 0, 65536, "Radius of the inside edge (the well)"),
        BuilderParam.Number("stepWidth", 192, 1, 65536, "Tread width from the inside edge out"),
        BuilderParam.Number("stepHeight", 16, 1, 4096, "Rise per step (players climb up to 34)"),
        BuilderParam.Number("stepThickness", 32, 1, 4096, "Thickness of each step"),
        BuilderParam.Int("numStepsPer360", 16, 3, 128, "Steps per full turn"),
        BuilderParam.Int("numSteps", 16, 1, 1024, "Number of steps"),
        BuilderParam.Flag("slopedCeiling", true, "Slope the underside of each step"),
        BuilderParam.Flag("slopedFloor", false, "Slope the top of each step (a ramp)"),
        BuilderParam.Flag("counterClockwise", false, "Turn the other way (counter-clockwise seen from above)"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double r0 = a.Num("innerRadius"), r1 = r0 + a.Num("stepWidth");
        double rise = a.Num("stepHeight"), thick = a.Num("stepThickness");
        double step = 2 * Math.PI / a.Int("numStepsPer360") * (a.Flag("counterClockwise") ? -1 : 1);
        bool slopedFloor = a.Flag("slopedFloor"), slopedCeiling = a.Flag("slopedCeiling");
        for (int i = 0; i < a.Int("numSteps"); i++)
        {
            double top = (i + 1) * rise, bottom = top - thick;
            yield return Solid(StepSolid.Sector(r0, r1, i * step, (i + 1) * step,
                slopedCeiling ? bottom - rise : bottom, bottom, slopedFloor ? top - rise : top, top));
        }
    }
}

/// <summary>Step solids shared by the stair builders and blockout stairs.</summary>
internal static class StepSolid
{
    /// <summary>A ring sector from angle <paramref name="a0"/> to <paramref name="a1"/> (radians from +X towards +Y),
    /// with its bottom and top heights given at the start and end angle (equal heights make a flat step).</summary>
    public static List<List<Vec3>> Sector(double r0, double r1, double a0, double a1, double bottom0, double bottom1, double top0, double top1)
    {
        var pts = new List<Vec3>(8);
        foreach ((double a, double b, double t) in new[] { (a0, bottom0, top0), (a1, bottom1, top1) })
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            foreach (double r in new[] { r0, r1 })
            {
                pts.Add(Poly.Tidy(new Vec3(r * c, r * s, b)));
                pts.Add(Poly.Tidy(new Vec3(r * c, r * s, t)));
            }
        }

        return ConvexSolid.Hull(pts);
    }
}
