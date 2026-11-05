using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation;
using OpenScct.Game.Svm;
using OpenScct.Physics.Conveyors;

namespace OpenScct.Editor.Core.Interop;

/// <summary>One RE+ <c>ConveyorBelt</c>: its pivot is the start of its riding surface, X its run and pitch its slope.</summary>
public sealed record ReBelt(string Name, Vec3 Start, Rot Rotation, double Length, bool LeftWall, bool RightWall);

/// <summary>A conveyor lowered to a chain of RE+ belts linked by <c>NextBelt</c>, and the settings every belt shares.</summary>
public sealed record ReConveyorChain(
    string ConveyorId,
    IReadOnlyList<ReBelt> Belts,
    bool Loop,
    double Width,
    double Speed,
    bool CarryPlayers,
    bool Driver,
    bool ShowRollers,
    IReadOnlyList<string> ItemMeshes,
    double ItemSpacing,
    double FadeDistance,
    double ItemScale);

/// <summary>
/// Lowers <c>Svm.Conveyor</c> to RE+ 2.8.0's conveyor belts for the original game (RE+
/// <c>ConveyorModel.h</c>): one belt per straight run, chords for arcs, RE+'s square corner piece at a right-angle
/// turn, and a driver conveyor as AeroD's hidden drivers. What RE+ belts cannot do is reported.
/// </summary>
public static class ReConveyors
{
    /// <summary>Belts in one chain whose items RE+ runs (RE+ 2.8.0 <c>Conveyor::MaxChain</c>).</summary>
    public const int MaxChain = 64;

    /// <summary>Items one chain holds (<c>Conveyor::MaxItems</c>).</summary>
    public const int MaxItems = 128;

    /// <summary>Item meshes a belt holds (<c>Conveyor::ItemSlots</c>).</summary>
    public const int ItemSlots = 8;

    /// <summary>Largest gap between an arc and its chords.</summary>
    public const double ChordError = 4;

    /// <summary>A sharp corner within this many degrees of a right angle gets RE+'s corner piece.</summary>
    private const double RightAngleSlack = 2;

    /// <summary>A turn between two straight runs smaller than this is not a corner.</summary>
    private const double CornerAngle = 1;

    public const string Code = "conveyor.re";

    /// <summary>The findings <see cref="Lower"/> reports (Map Check lists them for the original game).</summary>
    public static IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new($"{Code}.empty", Severity.Warning, "A conveyor with no path exports no belts") { OriginalOnly = true },
        new($"{Code}.chain", Severity.Warning, $"More than {MaxChain} belts in one RE+ chain") { OriginalOnly = true },
        new($"{Code}.item-meshes", Severity.Warning, $"More than {ItemSlots} item meshes") { OriginalOnly = true },
        new($"{Code}.items", Severity.Warning, $"More than {MaxItems} items on one chain") { OriginalOnly = true },
        new($"{Code}.corner", Severity.Warning, "A sharp corner that is not a level right angle with room for RE+'s corner piece") { OriginalOnly = true },
        new($"{Code}.off", Severity.Warning, "A conveyor that starts stopped (RE+ belts cannot be started)") { OriginalOnly = true },
        new($"{Code}.events", Severity.Warning, "A conveyor that receives events (RE+ belts cannot be switched or reversed)") { OriginalOnly = true },
        new($"{Code}.scroll", Severity.Warning, "Scroll targets (RE+ has no scrolling for them)") { OriginalOnly = true },
        new($"{Code}.look", Severity.Warning, "A treadmill or escalator look (RE+ has only its framed belt)") { OriginalOnly = true },
        new($"{Code}.objects", Severity.Info, "A conveyor that carries dropped objects (RE+ belts do not)") { OriginalOnly = true },
        new($"{Code}.next", Severity.Info, "A conveyor that hands its items to the next (one RE+ chain, with its first belt's item settings)")
            { OriginalOnly = true },
        new($"{Code}.walls", Severity.Info, "Auto walls opened for a join lose the whole belt's wall on that side (RE+ walls are per belt)")
            { OriginalOnly = true },
    ];

    /// <summary>The chain for one conveyor; what RE+ cannot express goes to <paramref name="findings"/>.</summary>
    public static ReConveyorChain Lower(MapProject project, Actor a, List<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(findings);
        Conveyor c = ConveyorDesign.Read(a);
        ConveyorPath path = c.BuildWorldPath();
        ObjectRef r = ObjectRef.Actor(a.Id);
        void Warn(string what, string message) => findings.Add(Finding.Warning($"{Code}.{what}", $"{a.Id}: {message}", r).At(a.Location));

        bool driver = c.Look == EConveyorLook.CL_Driver;
        bool left = !driver && c.Walls is EConveyorWalls.CW_Both or EConveyorWalls.CW_Auto or EConveyorWalls.CW_Left;
        bool right = !driver && c.Walls is EConveyorWalls.CW_Both or EConveyorWalls.CW_Auto or EConveyorWalls.CW_Right;
        List<ConveyorJoin>? joins = c.Walls == EConveyorWalls.CW_Auto ? ConveyorJoins.Find(a, project.Actors) : null;
        var belts = new List<ReBelt>();
        List<Run> runs = Runs(path);
        int bare = 0, opened = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            (Vec3 from, Vec3 to, _, double s0, double s1) = runs[i];
            // RE+ walls are per belt: a join anywhere along this run opens that side for the whole belt.
            bool openLeft = left && ConveyorJoins.Opens(joins, -1, s0, s1), openRight = right && ConveyorJoins.Opens(joins, 1, s0, s1);
            opened += (openLeft ? 1 : 0) + (openRight ? 1 : 0);
            Vec3 along = Flat(to - from);
            if (Corner(runs, i - 1, i, path.IsLoop, c.Width) is not null)
            {
                from += along * (c.Width / 2);
            }

            int? turn = Corner(runs, i, i + 1, path.IsLoop, c.Width);
            if (turn is not null)
            {
                to -= along * (c.Width / 2);
            }
            else if (SharpTurn(runs, i, path.IsLoop))
            {
                bare++;
            }

            belts.Add(Belt(a.Id, belts.Count, from, to, left && !openLeft, right && !openRight));
            if (turn is { } side)
            {
                // RE+'s Add Next turn: a level square of the belt's width round the corner point, heading the new way and
                // open on the side the belt comes in from.
                (Vec3 nextFrom, Vec3 nextTo, _, _, _) = runs[(i + 1) % runs.Count];
                Vec3 heading = Flat(nextTo - nextFrom), corner = runs[i].To;
                belts.Add(Belt(a.Id, belts.Count, corner - heading * (c.Width / 2), corner + heading * (c.Width / 2), left && side > 0,
                    right && side < 0));
            }
        }

        var meshes = c.ItemMeshes.Where(m => m.Path is not null).Select(m => m.Path!).ToList();
        int items = ConveyorLayout.ItemCount(c, path);
        if (path.Length <= 0f)
        {
            Warn("empty", "the path has fewer than two points; nothing is exported");
        }

        if (bare > 0)
        {
            Warn("corner", $"{bare} sharp corner{(bare == 1 ? " is" : "s are")} not a level right angle with room for RE+'s corner piece "
                + "(both runs longer than the width); the belts meet at the corner point with a gap outside it (a corner radius avoids it)");
        }

        if (belts.Count > MaxChain)
        {
            Warn("chain", $"{belts.Count} belts; RE+ runs items over at most {MaxChain} belts of one chain (fewer arcs or larger radii help)");
        }

        if (meshes.Count > ItemSlots)
        {
            Warn("item-meshes", $"{meshes.Count} item meshes; RE+ belts hold {ItemSlots}, the rest are dropped");
            meshes = meshes.Take(ItemSlots).ToList();
        }

        if (items > MaxItems)
        {
            Warn("items", $"{items} items; RE+ holds {MaxItems} per chain");
        }

        if (!c.bOn)
        {
            Warn("off", "starts stopped; RE+ belts cannot be started, so it is exported with speed 0");
        }

        if (Triggered(project, a) is { } sender)
        {
            Warn("events", $"{sender} sends it events; RE+ belts cannot be switched or reversed");
        }

        if (c.ScrollTargets.Count > 0)
        {
            Warn("scroll", $"{c.ScrollTargets.Count} scroll targets; RE+ has no scrolling for them, their textures stay still");
        }

        if (c.Look is EConveyorLook.CL_Treadmill or EConveyorLook.CL_Escalator)
        {
            Warn("look", $"RE+ has one look: the {(c.Look == EConveyorLook.CL_Treadmill ? "treadmill" : "escalator")} becomes a framed belt");
        }

        if (c.bCarryObjects)
        {
            findings.Add(Finding.Info($"{Code}.objects", $"{a.Id}: RE+ belts do not carry dropped objects", r).At(a.Location));
        }

        if (ConveyorDesign.NextId(a) is { } next)
        {
            findings.Add(Finding.Info($"{Code}.next", $"{a.Id}: its last RE+ belt leads on to {next}'s first (NextBelt), so RE+ runs them as "
                + $"one chain: with the first belt's item settings, over at most {MaxChain} belts", r).At(a.Location));
        }

        if (opened > 0)
        {
            findings.Add(Finding.Info($"{Code}.walls",
                $"{a.Id}: {opened} belt side{(opened == 1 ? " has" : "s have")} no wall where another conveyor joins; RE+ walls are per belt, "
                + "so the whole belt's side is open", r).At(a.Location));
        }

        return new ReConveyorChain(a.Id, belts, path.IsLoop, c.Width, c.bOn ? c.Speed : 0, c.bCarryPlayers, driver,
            c.Look == EConveyorLook.CL_Frame, meshes, items > 0 ? c.ItemSpacing : 0, c.ItemFade, c.ItemScale);
    }

    /// <summary>
    /// A straight run of the path; <paramref name="Arc"/> marks a chord of an arc. <paramref name="S0"/> and
    /// <paramref name="S1"/> are where it starts and ends along the path.
    /// </summary>
    private readonly record struct Run(Vec3 From, Vec3 To, bool Arc, double S0, double S1);

    /// <summary>The path as straight runs: lines as they are, each arc as chords at most <see cref="ChordError"/> off it.</summary>
    private static List<Run> Runs(ConveyorPath path)
    {
        var runs = new List<Run>();
        double along = 0;
        foreach (ConveyorPieceInfo piece in path.Pieces())
        {
            Vec3 start = ConveyorDesign.V(piece.Start), end = ConveyorDesign.V(piece.End);
            double s = along;
            along += piece.Length;
            if (!piece.IsArc)
            {
                runs.Add(new Run(start, end, false, s, along));
                continue;
            }

            double sweep = Math.Abs(piece.Sweep);
            double step = piece.Radius > ChordError ? 2 * Math.Acos(1 - ChordError / piece.Radius) : sweep;
            int n = Math.Max(1, (int)Math.Ceiling(sweep / step));
            Vec3 centre = ConveyorDesign.V(piece.Centre);
            double a0 = Math.Atan2(start.Y - centre.Y, start.X - centre.X);
            Vec3 previous = start;
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n, angle = a0 + piece.Sweep * t;
                Vec3 p = k == n ? end : new Vec3(centre.X + piece.Radius * Math.Cos(angle), centre.Y + piece.Radius * Math.Sin(angle),
                    start.Z + (end.Z - start.Z) * t);
                runs.Add(new Run(previous, p, true, s + piece.Length * (k - 1) / n, s + piece.Length * k / n));
                previous = p;
            }
        }

        return runs;
    }

    /// <summary>
    /// The turn (+1 right, -1 left) where run <paramref name="i"/> meets run <paramref name="j"/> at a level right angle
    /// with room for RE+'s corner piece; null elsewhere.
    /// </summary>
    private static int? Corner(List<Run> runs, int i, int j, bool loop, double width)
    {
        if (runs.Count < 2 || (!loop && (i < 0 || j >= runs.Count)))
        {
            return null;
        }

        (Vec3 aFrom, Vec3 aTo, _, _, _) = runs[(i + runs.Count) % runs.Count];
        (Vec3 bFrom, Vec3 bTo, _, _, _) = runs[j % runs.Count];
        double turn = Turn(aTo - aFrom, bTo - bFrom);
        bool level = Math.Abs(aTo.Z - aFrom.Z) < 0.5 && Math.Abs(bTo.Z - bFrom.Z) < 0.5;
        bool room = FlatLength(aTo - aFrom) > width && FlatLength(bTo - bFrom) > width;
        return level && room && Math.Abs(Math.Abs(turn) - 90) <= RightAngleSlack ? Math.Sign(turn) : null;
    }

    /// <summary>
    /// Whether run <paramref name="i"/> ends in a corner between two lines. Chords of an arc turn too, but by little and
    /// with the arc's own small gaps.
    /// </summary>
    private static bool SharpTurn(List<Run> runs, int i, bool loop)
    {
        if (runs.Count < 2 || (!loop && i + 1 >= runs.Count))
        {
            return false;
        }

        Run a = runs[i], b = runs[(i + 1) % runs.Count];
        return !a.Arc && !b.Arc && Math.Abs(Turn(a.To - a.From, b.To - b.From)) > CornerAngle;
    }

    /// <summary>The flat turn from <paramref name="din"/> to <paramref name="dout"/> in degrees, positive to the right.</summary>
    private static double Turn(Vec3 din, Vec3 dout)
    {
        Vec3 a = Flat(din), b = Flat(dout);
        return Math.Atan2(a.X * b.Y - a.Y * b.X, Vec3.Dot(a, b)) * 180 / Math.PI;
    }

    private static ReBelt Belt(string id, int index, Vec3 from, Vec3 to, bool left, bool right)
    {
        Vec3 d = to - from;
        int yaw = Rot.Wrap((int)Math.Round(Math.Atan2(d.Y, d.X) * Rot.UnitsPerTurn / (2 * Math.PI)));
        int pitch = Rot.Wrap((int)Math.Round(Math.Atan2(d.Z, FlatLength(d)) * Rot.UnitsPerTurn / (2 * Math.PI)));
        return new ReBelt($"{id}_Belt{index}", from, new Rot(pitch, yaw, 0), Math.Round(d.Length, 2), left, right);
    }

    /// <summary>An actor that sends this conveyor's Tag an event, or null.</summary>
    private static string? Triggered(MapProject project, Actor a) =>
        a.Props.GetString("Tag") is { Length: > 0 } tag
            ? project.Actors.FirstOrDefault(o => !ReferenceEquals(o, a) && string.Equals(o.Props.GetString("Event"), tag, StringComparison.OrdinalIgnoreCase))?.Id
            : null;

    private static Vec3 Flat(Vec3 v) => new Vec3(v.X, v.Y, 0) is var f && f.LengthSquared > 1e-12 ? f.Normalized() : Vec3.UnitX;

    private static double FlatLength(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
}
