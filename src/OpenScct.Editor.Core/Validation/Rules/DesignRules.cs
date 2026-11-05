using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>
/// Design lint over parametric pieces, from their builder specs: step heights the walking physics
/// climbs, passages a body fits through, ramps it walks up, spiral head room and ledge-grab reach. One finding per
/// piece (on its first part).
/// </summary>
internal sealed class DesignRules : MapRule
{
    public const string StepTooHigh = "design.step-too-high";
    public const string PassageNarrow = "design.passage-narrow";
    public const string PassageLow = "design.passage-low";
    public const string SpyOnly = "design.spy-only";
    public const string VentTooTall = "design.vent-too-tall";
    public const string OpeningBlocked = "design.opening-blocked";
    public const string RampSteep = "design.ramp-steep";
    public const string SpiralHeadroom = "design.spiral-headroom";
    public const string LedgeReach = "design.ledge-reach";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(StepTooHigh, Severity.Warning, $"Stairs with steps over {Limits.MaxStepHeight} high: players cannot walk up them"),
        new(PassageNarrow, Severity.Warning, $"A corridor, vent, crawlway or opening narrower than {Limits.NarrowestDoor}"),
        new(PassageLow, Severity.Warning, $"A passage too low to walk ({Limits.StandClearance}) or, for vents, to crouch through ({Limits.SpyCrouchGap})"),
        new(SpyOnly, Severity.Info, $"A passage only crouched spies fit (under the merc crouch gap {Limits.MercCrouchGap})"),
        new(VentTooTall, Severity.Warning, $"A vent or crawlway at least {Limits.MercCrouchGap} high: crouched mercs fit through it too"),
        new(OpeningBlocked, Severity.Warning, "A doorway or window whose opening, or the space either side of it, is solid (brush maps only)"),
        new(RampSteep, Severity.Warning, "A ramp steeper than the walkable floor slope"),
        new(SpiralHeadroom, Severity.Warning, $"A spiral stair whose turn above leaves less than {Limits.StandClearance} head room"),
        new(LedgeReach, Severity.Info, $"A ledge piece whose top is outside ledge-grab reach ({Limits.LedgeReachMin}-{Limits.LedgeReachMax})"),
    ];

    public override void Run(CheckContext c)
    {
        var pieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Brush b in c.Project.Brushes)
        {
            if (b.Shape.Builder is not { } spec)
            {
                continue;
            }

            string piece = spec.Strings.GetValueOrDefault(BuilderArgs.PieceKey) ?? b.Id;
            if (pieces.Add(piece))
            {
                Check(c, b, spec);
            }
        }
    }

    private static void Check(CheckContext c, Brush b, BuilderSpec spec)
    {
        ObjectRef r = ObjectRef.Brush(b.Id);
        double Get(string key) => spec.Get(key, double.NaN);
        void Report(Severity s, string code, string message) => c.Report(s, code, $"{b.Id} ({spec.Kind}): {message}", b.Location, r);

        double step = spec.Kind == "stairs" ? StairsStep(spec) : Get("stepHeight");
        if (spec.Kind is "linearStair" or "curvedStair" or "spiralStair" or "stairs" && step > c.Design.MaxStepHeight)
        {
            Report(Severity.Warning, StepTooHigh, $"steps are {step:0.#} high; players climb at most {c.Design.MaxStepHeight}");
        }

        switch (spec.Kind)
        {
            case "corridor" or "vent" or "crawlway" or "doorway" or "window":
                double width = Get("width"), height = Get("height");
                if (width < c.Design.NarrowestDoor)
                {
                    Report(Severity.Warning, PassageNarrow, $"{width:0} wide; the narrowest passable opening is {c.Design.NarrowestDoor}");
                }

                bool crouchPiece = spec.Kind is "vent" or "crawlway" or "window";
                if (height < c.Design.SpyCrouchGap)
                {
                    Report(Severity.Warning, PassageLow, $"{height:0} high; even a crouched spy needs {c.Design.SpyCrouchGap}");
                }
                else if (!crouchPiece && height < c.Design.StandClearance)
                {
                    Report(Severity.Warning, PassageLow, $"{height:0} high; walking upright needs {c.Design.StandClearance}");
                }

                if (height >= c.Design.SpyCrouchGap && height < c.Design.MercCrouchGap)
                {
                    Report(Severity.Info, SpyOnly, $"{height:0} high: crouched spies fit, mercs ({c.Design.MercCrouchGap}) do not");
                }
                else if (spec.Kind is "vent" or "crawlway" && height >= c.Design.MercCrouchGap)
                {
                    Report(Severity.Warning, VentTooTall, $"{height:0} high: crouched mercs ({c.Design.MercCrouchGap}) fit through it too");
                }

                if (spec.Kind is "doorway" or "window" && BlockedSide(c, b, spec) is { } side)
                {
                    Report(Severity.Warning, OpeningBlocked, $"solid {side}");
                }

                break;
            case "ramp":
                double slope = Math.Atan2(Get("rise"), Get("length"));
                if (Math.Cos(slope) < c.Design.WalkableNormalZ)
                {
                    Report(Severity.Warning, RampSteep, $"{slope * 180 / Math.PI:0.#} degrees; floors are walkable up to {Math.Acos(c.Design.WalkableNormalZ) * 180 / Math.PI:0.#}");
                }

                break;
            case "spiralStair":
                double clear = Get("numStepsPer360") * Get("stepHeight") - Get("stepThickness");
                if (clear < c.Design.StandClearance)
                {
                    Report(Severity.Warning, SpiralHeadroom, $"{clear:0} head room under the turn above (needs {c.Design.StandClearance})");
                }

                break;
            case "stairs" when spec.Strings.GetValueOrDefault("layout") == "spiral":
                double clearance = Get("stepsPerTurn") * StairsStep(spec) - Get("stepThickness");
                if (clearance < c.Design.StandClearance)
                {
                    Report(Severity.Warning, SpiralHeadroom, $"{clearance:0} head room under the turn above (needs {c.Design.StandClearance})");
                }

                break;
            case "ledge":
                double top = Get("height");
                if (top < Limits.LedgeReachMin || top > Limits.LedgeReachMax)
                {
                    Report(Severity.Info, LedgeReach, $"top at {top:0}; ledge grabs reach {Limits.LedgeReachMin}-{Limits.LedgeReachMax}");
                }

                break;
        }
    }

    /// <summary>
    /// Where a doorway or window is blocked: its opening, or the space a body's width out from either face of the wall,
    /// at body-centre height (or mid-opening for low openings). Null when all three are open or the project has a baked
    /// world the brushes do not describe.
    /// </summary>
    private static string? BlockedSide(CheckContext c, Brush b, BuilderSpec spec)
    {
        if (c.Project.Baked is not null)
        {
            return null;
        }

        double reach = spec.Get("depth", 64) / 2 + c.Design.BodyRadius;
        double z = spec.Get("sillHeight", 0) + Math.Min(spec.Get("height", 0) / 2, c.Design.StandHalfHeight);
        foreach ((double x, string where) in new[] { (0.0, "in the opening"), (-reach, "behind the opening (-X)"), (reach, "in front of the opening (+X)") })
        {
            if (c.Solid.IsSolid(b.LocalToWorld(new Vec3(x, 0, z))))
            {
                return where;
            }
        }

        return null;
    }

    /// <summary>The actual step height of blockout stairs: the rise evened out over the steps.</summary>
    private static double StairsStep(BuilderSpec spec)
    {
        double rise = spec.Get("rise", Limits.StoreyHeight), max = spec.Get("stepHeight", 16);
        return max <= 0 ? rise : rise / Math.Max(1, Math.Ceiling(rise / max - 1e-9));
    }
}
