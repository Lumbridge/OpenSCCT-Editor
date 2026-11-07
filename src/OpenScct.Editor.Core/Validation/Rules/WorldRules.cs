using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>The world as a whole: something to stand in, and everything inside the world box.</summary>
internal sealed class WorldRules : MapRule
{
    public const string NoGeometry = "world.no-geometry";
    public const string NoCarve = "world.no-carve";
    public const string Outside = "world.outside";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(NoGeometry, Severity.Error, "No brushes and no baked world: there is nothing to stand on"),
        new(NoCarve, Severity.Warning, "No subtractive brush: the UE2 world starts solid, so no space is open"),
        new(Outside, Severity.Error, $"An object past the world limit ({Limits.HalfWorld} from the origin on an axis)"),
    ];

    public override void Run(CheckContext c)
    {
        MapProject p = c.Project;
        if (p.Brushes.Count == 0 && p.Baked is null)
        {
            c.Report(Severity.Error, NoGeometry, "The map has no brushes and no baked world", null);
        }
        else if (p.Baked is null && !p.Brushes.Any(b => b.Oper == CsgOper.Subtract))
        {
            c.Report(Severity.Warning, NoCarve, "No subtractive brush carves open space out of the solid world", null);
        }

        foreach (Actor a in p.Actors.Where(a => IsOutside(a.Location)))
        {
            c.Report(Severity.Error, Outside, $"{a.Id} is outside the world at {a.Location}", a.Location, ObjectRef.Actor(a.Id));
        }

        foreach (Brush b in p.Brushes)
        {
            (Vec3 min, Vec3 max) = BrushOps.WorldBounds(b);
            if (IsOutside(min) || IsOutside(max))
            {
                c.Report(Severity.Error, Outside, $"{b.Id} reaches outside the world ({min} to {max})", CheckContext.Centre(b), ObjectRef.ForBrush(b));
            }
        }

        foreach (Ge g in p.Ges.Where(g => g.Points.Any(IsOutside)))
        {
            c.Report(Severity.Error, Outside, $"{g.Id} has a point outside the world", g.Points[0], ObjectRef.Ge(g.Id));
        }
    }

    private static bool IsOutside(Vec3 v) =>
        Math.Abs(v.X) > Limits.HalfWorld || Math.Abs(v.Y) > Limits.HalfWorld || Math.Abs(v.Z) > Limits.HalfWorld;
}

/// <summary>Geometric events (traversal lines): kinds, points, activation masks and the ledge-grab length rule.</summary>
internal sealed class GeRules : MapRule
{
    public const string UnknownKind = "ge.unknown-kind";
    public const string Points = "ge.points";
    public const string ZeroLength = "ge.zero-length";
    public const string Mask = "ge.mask";
    public const string Inactive = "ge.inactive";
    public const string Duplicate = "ge.duplicate";
    public const string LedgeShort = "ge.lg-short";
    public const string ClimbShape = "ge.climb-shape";
    public const string ActivationMask = "level.ge-activation-mask";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(UnknownKind, Severity.Error, $"A GE kind other than {string.Join(", ", GeKinds.All)}"),
        new(Points, Severity.Error, "A GE with the wrong number of points (2; ladders and pipes 3)"),
        new(ZeroLength, Severity.Error, "A GE whose first two points are the same"),
        new(Mask, Severity.Error, "A GE ID that is not 0 or a single bit 1, 2, 4 ... 128"),
        new(Inactive, Severity.Info, "A GE whose ID bit is not in LevelInfo.GEActivationMask (off until an event turns it on)"),
        new(Duplicate, Severity.Warning, "Two GEs of one kind on the same points"),
        new(LedgeShort, Severity.Info, $"A ledge grab shorter than {Limits.LedgeMinLength} (design rule; short corner pieces are common in stock maps)"),
        new(ClimbShape, Severity.Warning, "A ladder or pipe whose line is not vertical and bottom-first, or whose third point (the facing) is not level with the bottom (every stock one is vertical, with the facing 10 out)"),
        new(ActivationMask, Severity.Error, "LevelInfo.GEActivationMask outside 0..255"),
    ];

    public override void Run(CheckContext c)
    {
        int activation = c.Project.LevelInfo.GetInt("GEActivationMask");
        if (activation is < 0 or > 255)
        {
            c.Report(Severity.Error, ActivationMask, $"GEActivationMask is {activation}; it must be 0..255", null);
        }

        var seen = new Dictionary<string, Ge>(StringComparer.Ordinal);
        foreach (Ge g in c.Project.Ges)
        {
            ObjectRef r = ObjectRef.Ge(g.Id);
            Vec3? at = g.Points.Count > 0 ? g.Points[0] : null;
            if (!GeKinds.All.Contains(g.Kind))
            {
                c.Report(Severity.Error, UnknownKind, $"{g.Id}: unknown GE kind '{g.Kind}'", at, r);
                continue;
            }

            int want = GeKinds.HasThirdPoint(g.Kind) ? 3 : 2;
            if (g.Points.Count != want)
            {
                c.Report(Severity.Error, Points, $"{g.Id}: a {g.Kind} has {want} points, this one {g.Points.Count}", at, r);
                continue;
            }

            double length = (g.Points[1] - g.Points[0]).Length;
            if (length < 0.01)
            {
                c.Report(Severity.Error, ZeroLength, $"{g.Id}: the {g.Kind} has no length", at, r);
            }
            else if (g.Kind == GeKinds.LedgeGrab && length < Limits.LedgeMinLength)
            {
                c.Report(Severity.Info, LedgeShort, $"{g.Id}: ledge grab {length:0} long", at, r);
            }

            if (GeKinds.HasThirdPoint(g.Kind) && ClimbProblem(g.Points) is { } problem)
            {
                c.Report(Severity.Warning, ClimbShape, $"{g.Id}: the {g.Kind} {problem}", at, r);
            }

            if (g.Mask != 0 && (g.Mask < 0 || g.Mask > 128 || (g.Mask & (g.Mask - 1)) != 0))
            {
                c.Report(Severity.Error, Mask, $"{g.Id}: GE ID {g.Mask} is not a single bit", at, r);
            }
            else if (g.Mask != 0 && (activation & g.Mask) == 0)
            {
                c.Report(Severity.Info, Inactive, $"{g.Id}: GE ID {g.Mask} is off at start (GEActivationMask {activation})", at, r);
            }

            string key = g.Kind + ":" + string.Join(";", g.Points.Select(p => p.ToFloat32().ToString()));
            if (seen.TryGetValue(key, out Ge? first))
            {
                c.Report(Severity.Warning, Duplicate, $"{g.Id} repeats {first.Id}", at, r, ObjectRef.Ge(first.Id));
            }
            else
            {
                seen[key] = g;
            }
        }
    }

    /// <summary>What is wrong with a ladder's or pipe's three points (bottom, top, facing), or null.</summary>
    private static string? ClimbProblem(List<Vec3> p)
    {
        Vec3 axis = p[1] - p[0], facing = p[2] - p[0];
        if (Math.Abs(axis.X) > 1 || Math.Abs(axis.Y) > 1)
        {
            return "line is not vertical";
        }

        if (axis.Z <= 0)
        {
            return "top point is not above the bottom point";
        }

        if (Math.Abs(facing.Z) > 1 || Math.Sqrt(facing.X * facing.X + facing.Y * facing.Y) < 0.5)
        {
            return "facing point is not level with the bottom and off the line";
        }

        return null;
    }
}
