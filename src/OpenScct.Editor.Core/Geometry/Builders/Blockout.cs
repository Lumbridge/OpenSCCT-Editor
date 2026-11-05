using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Geometry.Builders;

// The RE+ Map Design blockout kit. Every piece is sized from the SCCT body (Validation.Limits) and has its origin on
// the floor at the centre of its footprint (length along X, width along Y), so drop-to-floor puts it in place.
// Carve pieces subtract the walkable space from the solid world; shell pieces add walls of a thickness around it, for
// building inside space that is already open. Parts come in CSG order (carves before the solids inside them).

/// <summary>Base of the blockout pieces: carve or shell construction and a wall thickness.</summary>
public abstract class BlockoutKind : BuilderKind
{
    public const string Carve = "carve";
    public const string Shell = "shell";

    public override bool IsBlockout => true;

    public override string Origin => "floor at the centre of the footprint (length along X, width along Y)";

    protected static BuilderParam ModeParam(string def = Carve) =>
        BuilderParam.Choice("mode", def, [Carve, Shell], "carve = subtract the space, shell = add walls around it");

    protected static BuilderParam WallParam(double def = 32) =>
        BuilderParam.Number("wallThickness", def, 1, 4096, "Wall thickness of shell construction");

    protected static bool IsShell(BuilderArgs a) => a.Text("mode") == Shell;

    protected static Part Carved(Vec3 min, Vec3 max) => BoxPart(min, max, CsgOper.Subtract);
}

/// <summary>A room: one carved box, or floor, ceiling and four walls.</summary>
public sealed class RoomPiece : BlockoutKind
{
    public override string Kind => "room";

    public override string Summary => "Room: carved box, or shell of floor, ceiling and walls";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("length", 1024, 1, 262144, "Inside size along X"),
        BuilderParam.Number("width", 1024, 1, 262144, "Inside size along Y"),
        BuilderParam.Number("height", 384, 1, 65536, $"Inside height (a standing player needs {Limits.StandClearance})"),
        ModeParam(),
        WallParam(),
        BuilderParam.Flag("floor", true, "Shell: build the floor slab"),
        BuilderParam.Flag("ceiling", true, "Shell: build the ceiling slab"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double l = a.Num("length") / 2, w = a.Num("width") / 2, h = a.Num("height"), t = a.Num("wallThickness");
        if (!IsShell(a))
        {
            yield return Carved(new Vec3(-l, -w, 0), new Vec3(l, w, h));
            yield break;
        }

        if (a.Flag("floor"))
        {
            yield return BoxPart(new Vec3(-l - t, -w - t, -t), new Vec3(l + t, w + t, 0));
        }

        if (a.Flag("ceiling"))
        {
            yield return BoxPart(new Vec3(-l - t, -w - t, h), new Vec3(l + t, w + t, h + t));
        }

        yield return BoxPart(new Vec3(l, -w - t, 0), new Vec3(l + t, w + t, h));
        yield return BoxPart(new Vec3(-l - t, -w - t, 0), new Vec3(-l, w + t, h));
        yield return BoxPart(new Vec3(-l, w, 0), new Vec3(l, w + t, h));
        yield return BoxPart(new Vec3(-l, -w - t, 0), new Vec3(l, -w, h));
    }
}

/// <summary>A straight passage along X, open at both ends: carved, or floor, ceiling and side walls.</summary>
public abstract class PassagePiece : BlockoutKind
{
    protected abstract double DefaultWidth { get; }

    protected abstract double DefaultHeight { get; }

    protected abstract double DefaultWall { get; }

    protected abstract string HeightNote { get; }

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("length", 512, 1, 262144, "Length along X"),
        BuilderParam.Number("width", DefaultWidth, 1, 65536, $"Inside width (a body is {Limits.BodyRadius * 2} wide)"),
        BuilderParam.Number("height", DefaultHeight, 1, 65536, HeightNote),
        ModeParam(),
        WallParam(DefaultWall),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double l = a.Num("length") / 2, w = a.Num("width") / 2, h = a.Num("height"), t = a.Num("wallThickness");
        if (!IsShell(a))
        {
            yield return Carved(new Vec3(-l, -w, 0), new Vec3(l, w, h));
            yield break;
        }

        yield return BoxPart(new Vec3(-l, -w - t, -t), new Vec3(l, w + t, 0));
        yield return BoxPart(new Vec3(-l, -w - t, h), new Vec3(l, w + t, h + t));
        yield return BoxPart(new Vec3(-l, w, 0), new Vec3(l, w + t, h));
        yield return BoxPart(new Vec3(-l, -w - t, 0), new Vec3(l, -w, h));
    }
}

public sealed class CorridorPiece : PassagePiece
{
    public override string Kind => "corridor";

    public override string Summary => "Corridor both teams walk through standing";

    protected override double DefaultWidth => 160;

    protected override double DefaultHeight => 224;

    protected override double DefaultWall => 32;

    protected override string HeightNote => $"Inside height (standing needs {Limits.StandClearance})";

    public override IEnumerable<(string Name, double Value)> BodyDefaults(DesignNumbers body) => [("height", body.StandClearance + 32)];
}

public sealed class VentPiece : PassagePiece
{
    public override string Kind => "vent";

    public override string Summary => $"Crouch vent ({Limits.VentHeight} high): spies crouch through, mercs ({Limits.MercCrouchGap}) do not fit";

    protected override double DefaultWidth => 96;

    protected override double DefaultHeight => Limits.VentHeight;

    protected override double DefaultWall => 16;

    protected override string HeightNote => $"Inside height (crouched spy {Limits.SpyCrouchHeight}, gap {Limits.SpyCrouchGap}; merc {Limits.MercCrouchGap})";

    public override IEnumerable<(string Name, double Value)> BodyDefaults(DesignNumbers body) => [("height", body.VentHeight)];
}

public sealed class CrawlwayPiece : PassagePiece
{
    public override string Kind => "crawlway";

    public override string Summary => $"Low slow-walk crawlway ({Limits.CrawlwayHeight} high), spies only";

    protected override double DefaultWidth => 96;

    protected override double DefaultHeight => Limits.CrawlwayHeight;

    protected override double DefaultWall => 16;

    protected override string HeightNote => $"Inside height (crouched spy {Limits.SpyCrouchHeight})";

    public override IEnumerable<(string Name, double Value)> BodyDefaults(DesignNumbers body) => [("height", body.CrawlwayHeight)];
}

/// <summary>An opening through a wall: carved, or a wall with the opening left in it (shell). The wall runs along Y
/// and is <c>depth</c> thick along X. With <c>portal</c> a zone portal sheet fills the opening in the middle of the
/// wall, so the rooms either side can be separate zones.</summary>
public abstract class OpeningPiece : BlockoutKind
{
    protected abstract double DefaultWidth { get; }

    protected abstract double DefaultHeight { get; }

    protected abstract double DefaultSill { get; }

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("width", DefaultWidth, 1, 65536, $"Opening width along Y (narrowest door {Limits.NarrowestDoor})"),
        BuilderParam.Number("height", DefaultHeight, 1, 65536, "Opening height"),
        BuilderParam.Number("sillHeight", DefaultSill, 0, 65536, "Height of the opening's bottom over the floor"),
        BuilderParam.Number("depth", 64, 1, 65536, "Wall thickness cut through (along X)"),
        ModeParam(),
        BuilderParam.Number("wallLength", 512, 1, 262144, "Shell: length of the wall along Y"),
        BuilderParam.Number("wallHeight", 384, 1, 65536, "Shell: height of the wall"),
        BuilderParam.Flag("portal", false, "Fill the opening with a zone portal sheet (the rooms either side become separate zones when each has a ZoneInfo)"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double w = a.Num("width") / 2, h = a.Num("height"), sill = a.Num("sillHeight"), d = a.Num("depth") / 2;
        if (!IsShell(a))
        {
            yield return Carved(new Vec3(-d, -w, sill), new Vec3(d, w, sill + h));
        }
        else
        {
            double wl = Math.Max(a.Num("wallLength") / 2, w), wh = Math.Max(a.Num("wallHeight"), sill + h);
            yield return BoxPart(new Vec3(-d, -wl, 0), new Vec3(d, -w, wh));       // left of the opening
            yield return BoxPart(new Vec3(-d, w, 0), new Vec3(d, wl, wh));         // right of it
            yield return BoxPart(new Vec3(-d, -w, sill + h), new Vec3(d, w, wh));  // above
            yield return BoxPart(new Vec3(-d, -w, 0), new Vec3(d, w, sill));       // below (windows)
        }

        if (a.Flag("portal"))
        {
            // After the carve in CSG order, so the sheet sits in open space; it is neither drawn nor solid.
            List<Vec3> sheet = [new(0, -w, sill), new(0, w, sill), new(0, w, sill + h), new(0, -w, sill + h)];
            yield return new Part([sheet], CsgOper.Add, PolyFlags.Portal);
        }
    }
}

public sealed class DoorwayPiece : OpeningPiece
{
    public override string Kind => "doorway";

    public override string Summary => "Doorway through a wall: carved opening, or a wall with the opening";

    protected override double DefaultWidth => 128;

    protected override double DefaultHeight => 224;

    protected override double DefaultSill => 0;

    public override IEnumerable<(string Name, double Value)> BodyDefaults(DesignNumbers body) => [("height", body.StandClearance + 32)];
}

public sealed class WindowPiece : OpeningPiece
{
    public override string Kind => "window";

    public override string Summary => $"Window through a wall; the default fits a crouched spy ({Limits.SpyCrouchGap} gap)";

    protected override double DefaultWidth => 160;

    protected override double DefaultHeight => 128;

    protected override double DefaultSill => 104;

    public override IEnumerable<(string Name, double Value)> BodyDefaults(DesignNumbers body) => [("sillHeight", body.SpyCrouchGap)];
}

/// <summary>Stairs from the floor up one storey: straight, L, U or spiral, with the stairwell carved above them.</summary>
public sealed class StairsPiece : BlockoutKind
{
    public override string Kind => "stairs";

    public override string Summary => "Stairs up one storey (straight, l, u or spiral); carve also clears the stairwell";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Choice("layout", "straight", ["straight", "l", "u", "spiral"], "Plan shape"),
        BuilderParam.Number("rise", Limits.StoreyHeight, 1, 65536, "Height climbed"),
        BuilderParam.Number("width", 192, Limits.BodyRadius * 2, 65536, "Width of the flight (spiral: tread width)"),
        BuilderParam.Number("stepHeight", 16, 1, 256, $"Largest rise per step (players climb up to {Limits.MaxStepHeight}; Map Check uses the map's movement limits); steps are evened out to fit the rise"),
        BuilderParam.Number("stepLength", 32, 8, 1024, "Tread depth (straight, l, u)"),
        BuilderParam.Number("headroom", 224, 64, 65536, "Clear height over every step"),
        BuilderParam.Choice("turn", "right", ["right", "left"], "Which way l and u stairs turn (spiral: right = clockwise from above)"),
        BuilderParam.Number("wellRadius", 48, 0, 4096, "Spiral: radius of the central column"),
        BuilderParam.Int("stepsPerTurn", 16, 6, 64, "Spiral: steps per full turn"),
        BuilderParam.Number("stepThickness", 32, 4, 1024, "Spiral: thickness of the floating steps"),
        ModeParam(),
    ];

    public override IEnumerable<(string Name, double Value)> BodyDefaults(DesignNumbers body) => [("headroom", body.StandClearance + 32)];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double rise = a.Num("rise");
        int n = Math.Max(1, (int)Math.Ceiling(rise / a.Num("stepHeight") - 1e-9));
        double sh = rise / n;
        string layout = a.Text("layout");
        if (layout == "spiral")
        {
            return Spiral(a, n, sh);
        }

        if (n < 2)
        {
            layout = "straight";
        }

        double len = a.Num("stepLength"), w = a.Num("width"), top = rise + a.Num("headroom");
        var carve = new List<(Vec3 Min, Vec3 Max)>();
        var steps = new List<(Vec3 Min, Vec3 Max)>();
        int n1 = n / 2, n2 = n - n1;
        switch (layout)
        {
            case "l":
                for (int i = 0; i < n1; i++)
                {
                    steps.Add((new Vec3(i * len, 0, 0), new Vec3((i + 1) * len, w, (i + 1) * sh)));
                }

                steps.Add((new Vec3(n1 * len, 0, 0), new Vec3(n1 * len + w, w, n1 * sh))); // landing
                for (int j = 0; j < n2; j++)
                {
                    steps.Add((new Vec3(n1 * len, w + j * len, 0), new Vec3(n1 * len + w, w + (j + 1) * len, (n1 + j + 1) * sh)));
                }

                carve.Add((new Vec3(0, 0, 0), new Vec3(n1 * len + w, w, top)));
                carve.Add((new Vec3(n1 * len, w, 0), new Vec3(n1 * len + w, w + n2 * len, top)));
                break;
            case "u":
                for (int i = 0; i < n1; i++)
                {
                    steps.Add((new Vec3(i * len, 0, 0), new Vec3((i + 1) * len, w, (i + 1) * sh)));
                }

                steps.Add((new Vec3(n1 * len, 0, 0), new Vec3(n1 * len + w, 2 * w, n1 * sh))); // landing across both flights
                for (int j = 0; j < n2; j++)
                {
                    steps.Add((new Vec3((n1 - j - 1) * len, w, 0), new Vec3((n1 - j) * len, 2 * w, (n1 + j + 1) * sh)));
                }

                carve.Add((new Vec3(Math.Min(0, (n1 - n2) * len), 0, 0), new Vec3(n1 * len + w, 2 * w, top)));
                break;
            default:
                for (int i = 0; i < n; i++)
                {
                    steps.Add((new Vec3(i * len, 0, 0), new Vec3((i + 1) * len, w, (i + 1) * sh)));
                }

                carve.Add((new Vec3(0, 0, 0), new Vec3(n * len, w, top)));
                break;
        }

        // Centre the footprint on the origin and mirror for a left turn.
        (Vec3 min, Vec3 max) = BrushOps.BoundsOf(steps.SelectMany(s => new[] { s.Min, s.Max }));
        var centre = new Vec3((min.X + max.X) / 2, (min.Y + max.Y) / 2, 0);
        bool left = a.Text("turn") == "left" && layout != "straight";
        (Vec3, Vec3) Place((Vec3 Min, Vec3 Max) b)
        {
            Vec3 lo = b.Min - centre, hi = b.Max - centre;
            return left ? (lo with { Y = -hi.Y }, hi with { Y = -lo.Y }) : (lo, hi);
        }

        var parts = new List<Part>();
        if (!IsShell(a))
        {
            parts.AddRange(carve.Select(Place).Select(b => Carved(b.Item1, b.Item2)));
        }

        parts.AddRange(steps.Select(Place).Select(b => BoxPart(b.Item1, b.Item2)));
        return parts;
    }

    private static List<Part> Spiral(BuilderArgs a, int n, double sh)
    {
        double r0 = a.Num("wellRadius"), r1 = r0 + a.Num("width"), thick = a.Num("stepThickness");
        double step = 2 * Math.PI / a.Int("stepsPerTurn") * (a.Text("turn") == "left" ? -1 : 1);
        var parts = new List<Part>();
        if (!IsShell(a))
        {
            // Wider than the treads so the outer edge never scrapes the wall, open from the floor up.
            parts.Add(Solid(ConvexSolid.Prism(Ring.Points(16, r1 + 16, true), 0, a.Num("rise") + a.Num("headroom")), CsgOper.Subtract));
        }

        if (r0 > 0)
        {
            parts.Add(Solid(ConvexSolid.Prism(Ring.Points(12, r0, true), 0, a.Num("rise"))));
        }

        for (int i = 0; i < n; i++)
        {
            double top = (i + 1) * sh;
            double bottom = Math.Max(0, top - thick);
            parts.Add(Solid(StepSolid.Sector(r0, r1, i * step, (i + 1) * step, bottom, bottom, top, top)));
        }

        return parts;
    }
}

/// <summary>A ramp climbing along +X (a wedge); carve also clears the space above it.</summary>
public sealed class RampPiece : BlockoutKind
{
    public override string Kind => "ramp";

    public override string Summary => $"Ramp (walkable up to {Math.Round(Math.Acos(Limits.WalkableNormalZ) * 180 / Math.PI, 1)} degrees)";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("length", 512, 1, 262144, "Run along X"),
        BuilderParam.Number("width", 192, 1, 65536, "Width along Y"),
        BuilderParam.Number("rise", 128, 1, 65536, "Height at the top end (+X)"),
        BuilderParam.Number("headroom", 224, 1, 65536, "Carve: clear height above the ramp"),
        ModeParam(Shell),
    ];

    public override IEnumerable<(string Name, double Value)> BodyDefaults(DesignNumbers body) => [("headroom", body.StandClearance + 32)];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double l = a.Num("length") / 2, w = a.Num("width") / 2, rise = a.Num("rise");
        if (!IsShell(a))
        {
            yield return Carved(new Vec3(-l, -w, 0), new Vec3(l, w, rise + a.Num("headroom")));
        }

        yield return Solid(ConvexSolid.Hull([
            new Vec3(-l, -w, 0), new Vec3(-l, w, 0), new Vec3(l, -w, 0), new Vec3(l, w, 0), new Vec3(l, -w, rise), new Vec3(l, w, rise),
        ]));
    }
}

/// <summary>A raised platform: solid to the floor, or a slab of a thickness at the top.</summary>
public sealed class PlatformPiece : BlockoutKind
{
    public override string Kind => "platform";

    public override string Summary => $"Raised platform (spies jump onto {Limits.SpyJumpOnto}, mercs {Limits.MercJumpOnto})";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("length", 256, 1, 262144, "Size along X"),
        BuilderParam.Number("width", 256, 1, 262144, "Size along Y"),
        BuilderParam.Number("height", 128, 1, 65536, "Height of the top"),
        BuilderParam.Number("thickness", 0, 0, 65536, "Slab thickness (0 = solid to the floor)"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double l = a.Num("length") / 2, w = a.Num("width") / 2, h = a.Num("height"), t = a.Num("thickness");
        yield return BoxPart(new Vec3(-l, -w, t > 0 && t < h ? h - t : 0), new Vec3(l, w, h));
    }
}

/// <summary>A block whose top edge is a ledge to grab (default height within ledge-grab reach).</summary>
public sealed class LedgePiece : BlockoutKind
{
    public override string Kind => "ledge";

    public override string Summary => $"Block with a grabbable top (ledge grab reach {Limits.LedgeReachMin}-{Limits.LedgeReachMax})";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("length", 256, 1, 262144, $"Length of the ledge along X (at least {Limits.LedgeMinLength})"),
        BuilderParam.Number("depth", 64, 1, 65536, "Depth along Y"),
        BuilderParam.Number("height", 256, 1, 65536, "Height of the top"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double l = a.Num("length") / 2, d = a.Num("depth") / 2;
        yield return BoxPart(new Vec3(-l, -d, 0), new Vec3(l, d, a.Num("height")));
    }
}
