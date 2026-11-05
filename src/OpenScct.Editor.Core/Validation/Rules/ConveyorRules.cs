using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.Svm;
using OpenScct.Physics.Collision;
using OpenScct.Physics.Conveyors;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>
/// Native conveyors (<c>Svm.Conveyor</c>, spec/additions/conveyors.md): paths that cannot carry anyone, slopes and joints
/// pawns cannot walk, belts faster than running, belts that run into a wall, into another conveyor's fixed wall or
/// through a damage volume, <c>Next</c> links items cannot follow, and for the original game what RE+'s belts cannot
/// express.
/// </summary>
internal sealed class ConveyorRules : MapRule
{
    public const string Path = "conveyor.path";
    public const string Slope = "conveyor.slope";
    public const string StepRise = "conveyor.step-rise";
    public const string Joint = "conveyor.joint-step";
    public const string Speed = "conveyor.speed";
    public const string IntoWall = "conveyor.into-wall";
    public const string JoinWall = "conveyor.join-wall";
    public const string Damage = "conveyor.damage";
    public const string Next = "conveyor.next";

    private const string DamageVolumeClass = "SBase.SDamageVolume";

    /// <summary>A conveyor end this far above or below another's surface is on another level, not a joint.</summary>
    private const float JointBand = 128;

    /// <summary>How far apart riders' places along a conveyor are tested for damage volumes.</summary>
    private const float DamageStep = 32;

    /// <summary>What stops a rider in the compiled world: the level and world geometry (static meshes), not movers.</summary>
    private const ETraceFlags WallFlags = ETraceFlags.TRACE_Level | ETraceFlags.TRACE_LevelGeometry;

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(Path, Severity.Error, "A conveyor whose path has fewer than two points"),
        new(Slope, Severity.Warning, "A conveyor run steeper than a walkable slope"),
        new(StepRise, Severity.Warning, $"An escalator whose steps rise more than {ConveyorLayout.StepRise} (steeper than 30 degrees)"),
        new(Joint, Severity.Warning, "Where a conveyor's end meets another conveyor, a step up (the way riders go) higher than a pawn can step"),
        new(Speed, Severity.Info, "A conveyor faster than a spy runs: riders cannot walk against it"),
        new(IntoWall, Severity.Warning, "A conveyor that carries riders into solid geometry at its end (baked maps: deep check only)"),
        new(JoinWall, Severity.Warning, "A conveyor whose end meets another conveyor's side where that one has a fixed wall (auto walls open)"),
        new(Damage, Severity.Warning, "A conveyor that carries riders into or through a damage volume (a note when the volume starts off)"),
        new(Next, Severity.Warning,
            "A conveyor whose Next is not another conveyor the end its items leave by meets, so they vanish there (a note with a fix when that end meets one and Next is not set)"),
        .. ReConveyors.Rules,
    ];

    public override void Run(CheckContext c)
    {
        List<Actor> conveyors = c.Project.Actors.Where(ConveyorDesign.Is).ToList();
        var paths = conveyors.ToDictionary(a => a, a => (Conveyor: ConveyorDesign.Read(a), Path: ConveyorDesign.WorldPath(a)));
        foreach (Actor a in conveyors)
        {
            ObjectRef r = ObjectRef.Actor(a.Id);
            (Conveyor conveyor, ConveyorPath path) = paths[a];
            if (path.Length <= 0f)
            {
                c.Report(Severity.Error, Path, $"{a.Id}: the path needs at least two points", a.Location, r);
                continue;
            }

            CheckJoinWalls(c, a, conveyor, conveyors);
            CheckNext(c, a, conveyor, path, paths);
            CheckDamage(c, a, conveyor, path);

            CheckSlopes(c, a, conveyor, path);
            if (Math.Abs(conveyor.Speed) > c.Project.Workspace.Movement.SpyRunSpeed)
            {
                c.Report(Severity.Info, Speed,
                    $"{a.Id}: {Math.Abs(conveyor.Speed):0} uu/s is faster than a spy runs ({c.Project.Workspace.Movement.SpyRunSpeed:0})", a.Location, r);
            }

            if (path.IsLoop)
            {
                continue;
            }

            foreach ((Actor other, (Conveyor otherConveyor, ConveyorPath otherPath)) in paths.Where(p => !ReferenceEquals(p.Key, a)))
            {
                CheckJoint(c, a, conveyor, path, last: false, other, otherConveyor, otherPath);
                CheckJoint(c, a, conveyor, path, last: true, other, otherConveyor, otherPath);
            }

            CheckEnd(c, a, conveyor, path);
        }

        if (c.Original)
        {
            var findings = new List<Finding>();
            foreach (Actor a in conveyors)
            {
                ReConveyors.Lower(c.Project, a, findings);
            }

            foreach (Finding f in findings.Where(f => c.Wants(f.Code)))
            {
                c.Report(f);
            }
        }
    }

    private static void CheckSlopes(CheckContext c, Actor a, Conveyor conveyor, ConveyorPath path)
    {
        double walkable = c.Design.WalkableSlopeDegrees;
        foreach (ConveyorPieceInfo piece in path.Pieces())
        {
            double flat = Math.Sqrt(Math.Max(0, (double)piece.Length * piece.Length - Math.Pow(piece.End.Z - piece.Start.Z, 2)));
            double degrees = Math.Atan2(Math.Abs(piece.End.Z - piece.Start.Z), flat) * 180 / Math.PI;
            Vec3 at = ConveyorDesign.V(piece.Start);
            if (degrees > walkable)
            {
                c.Report(Severity.Warning, Slope, $"{a.Id}: a run climbs {degrees:0.#} degrees; pawns walk up to {walkable:0.#}", at, ObjectRef.Actor(a.Id));
                return;
            }

            double rise = ConveyorLayout.StepLength * Math.Sin(degrees * Math.PI / 180);
            if (conveyor.Look == EConveyorLook.CL_Escalator && rise > ConveyorLayout.StepRise)
            {
                c.Report(Severity.Warning, StepRise, $"{a.Id}: its steps rise {rise:0.#} uu each ({degrees:0.#} degrees)", at, ObjectRef.Actor(a.Id));
                return;
            }
        }
    }

    /// <summary>
    /// One end of <paramref name="a"/> on another conveyor's surface: the step up the way riders go there. An end
    /// <paramref name="a"/> carries towards hands riders on to the other conveyor; at the other end they step on from
    /// it. A step down is harmless.
    /// </summary>
    private static void CheckJoint(CheckContext c, Actor a, Conveyor conveyor, ConveyorPath path, bool last, Actor other, Conveyor otherConveyor,
        ConveyorPath otherPath)
    {
        path.Evaluate(last ? path.Length : 0f, out FVector end, out _);
        if (!otherPath.Project(end, otherConveyor.HalfWidth, JointBand, JointBand, 0f, out ConveyorProjection onOther))
        {
            return;
        }

        bool handsOn = last == conveyor.Speed >= 0;
        double step = handsOn ? onOther.Surface.Z - end.Z : end.Z - onOther.Surface.Z;
        if (step > c.Design.MaxStepHeight)
        {
            c.Report(Severity.Warning, Joint,
                $"{a.Id} meets {other.Id} with a step up of {step:0.#} for riders {(handsOn ? $"going on to {other.Id}" : $"coming from {other.Id}")}; "
                + $"pawns step up to {c.Design.MaxStepHeight:0}",
                ConveyorDesign.V(end), ObjectRef.Actor(a.Id), ObjectRef.Actor(other.Id));
        }
    }

    /// <summary>
    /// Other conveyors ending at <paramref name="a"/>'s side where it has a fixed wall: a frame's wall (24 uu) and an
    /// escalator's balustrade are higher than the carry lifts a rider (18 uu), so riders are pinned against it.
    /// </summary>
    private static void CheckJoinWalls(CheckContext c, Actor a, Conveyor conveyor, List<Actor> conveyors)
    {
        if (conveyor.Walls is EConveyorWalls.CW_Auto or EConveyorWalls.CW_None ||
            conveyor.Look is not (EConveyorLook.CL_Frame or EConveyorLook.CL_Escalator))
        {
            return;
        }

        foreach (ConveyorJoin join in ConveyorJoins.Find(a, conveyors))
        {
            bool walled = conveyor.Walls == EConveyorWalls.CW_Both || (join.Side < 0 ? conveyor.Walls == EConveyorWalls.CW_Left : conveyor.Walls == EConveyorWalls.CW_Right);
            if (walled)
            {
                c.Report(Severity.Warning, JoinWall,
                    $"{join.Other} meets {a.Id}'s {(join.Side < 0 ? "left" : "right")} wall: riders are pinned against it. Set {a.Id}'s walls to auto, "
                    + "which opens them where conveyors join", a.Location, ObjectRef.Actor(a.Id), ObjectRef.Actor(join.Other));
            }
        }
    }

    /// <summary>
    /// The end riders are carried to: something solid within a body's radius beyond it, at waist height, is a wall. The
    /// deep check traces the compiled world (baked maps, static meshes); without it a brush map's own CSG answers.
    /// </summary>
    private static void CheckEnd(CheckContext c, Actor a, Conveyor conveyor, ConveyorPath path)
    {
        if (!conveyor.bCarryPlayers || conveyor.Speed == 0)
        {
            return;
        }

        (Vec3 end, Vec3 waist, Vec3 ahead) = CarriedTo(c, conveyor, path);
        bool wall;
        if (c.Options.Compiled is { } map)
        {
            lock (map.Gate)
            {
                wall = map.Collision.LineCheck(out _, null, AnalysisMap.F(ahead), AnalysisMap.F(waist), default, WallFlags);
            }
        }
        else if (c.Project.Baked is null && c.Project.Brushes.Count > 0)
        {
            wall = c.Solid.IsSolid(ahead);
        }
        else
        {
            return;
        }

        if (wall)
        {
            c.Report(Severity.Warning, IntoWall, $"{a.Id} carries riders into a wall at its end", end, ObjectRef.Actor(a.Id));
        }
    }

    /// <summary>
    /// The end of an open conveyor riders are carried to, a rider's waist above it, and that point a body's radius
    /// further on.
    /// </summary>
    private static (Vec3 End, Vec3 Waist, Vec3 Ahead) CarriedTo(CheckContext c, Conveyor conveyor, ConveyorPath path)
    {
        float along = conveyor.Speed > 0 ? path.Length : 0f;
        path.Evaluate(along, out FVector end, out FVector tangent);
        Vec3 at = ConveyorDesign.V(end);
        Vec3 waist = at + new Vec3(0, 0, c.Design.StandHalfHeight);
        Vec3 ahead = waist + new Vec3(tangent.X, tangent.Y, 0).Normalized() * (Math.Sign(conveyor.Speed) * c.Design.BodyRadius);
        return (at, waist, ahead);
    }

    /// <summary>
    /// <c>Next</c> names the conveyor items go on to from the end they leave by: the path's end, or its start for a belt
    /// that starts running backwards. It must be another conveyor, this one must have ends (not be a loop), and that end
    /// must meet it; otherwise the items shrink away there as with no Next. With no Next, an end items leave by that
    /// meets another conveyor is a note whose fix links them.
    /// </summary>
    private static void CheckNext(CheckContext c, Actor a, Conveyor conveyor, ConveyorPath path,
        Dictionary<Actor, (Conveyor Conveyor, ConveyorPath Path)> paths)
    {
        bool backwards = conveyor.Speed < 0;
        string end = backwards ? "start (it runs backwards)" : "end";
        path.Evaluate(backwards ? 0f : path.Length, out FVector outAt, out _);
        Vec3 at = ConveyorDesign.V(outAt);
        if (ConveyorDesign.NextId(a) is not { } id)
        {
            SuggestNext(c, a, conveyor, path, backwards, end, at, paths);
            return;
        }

        Actor? target = c.Project.FindActor(id);
        string? problem = target switch
        {
            null => $"names {id}, which is not in the map",
            _ when ReferenceEquals(target, a) => "names itself",
            _ when !paths.ContainsKey(target) => $"names {id}, a {target.ShortClass}, not a conveyor",
            _ when path.IsLoop => $"names {id}, but a loop has no end to hand items on from",
            _ when ConveyorItemFlow.HandsOnAt(path, backwards, paths[target].Path, paths[target].Conveyor.HalfWidth, out _) => null,
            _ when ConveyorItemFlow.HandsOnAt(path, !backwards, paths[target].Path, paths[target].Conveyor.HalfWidth, out _) =>
                $"names {id}, which only its {(backwards ? "end" : "start")} meets, but items leave by its {end}",
            _ => $"names {id}, but its {end} is too far from {id} to hand items on",
        };
        if (problem is null)
        {
            return;
        }

        ObjectRef[] refs = target is null || ReferenceEquals(target, a) ? [ObjectRef.Actor(a.Id)] : [ObjectRef.Actor(a.Id), ObjectRef.Actor(target.Id)];
        c.Report(Severity.Warning, Next, $"{a.Id}'s Next {problem}; its items vanish at its {(backwards ? "start" : "end")}", at, refs);
    }

    /// <summary>
    /// A conveyor with items and no Next whose out end meets another conveyor: a note, with a fix that sets Next to the
    /// first such conveyor.
    /// </summary>
    private static void SuggestNext(CheckContext c, Actor a, Conveyor conveyor, ConveyorPath path, bool backwards, string end, Vec3 at,
        Dictionary<Actor, (Conveyor Conveyor, ConveyorPath Path)> paths)
    {
        if (path.IsLoop || conveyor.ItemMeshes.Count == 0 || conveyor.ItemSpacing <= 0f)
        {
            return;
        }

        foreach ((Actor other, (Conveyor otherConveyor, ConveyorPath otherPath)) in paths)
        {
            if (ReferenceEquals(other, a) || !ConveyorItemFlow.HandsOnAt(path, backwards, otherPath, otherConveyor.HalfWidth, out _))
            {
                continue;
            }

            var op = new SvJsonObject();
            op.Add("op", SvJsonNode.String("conveyor"));
            op.Add("id", SvJsonNode.String(a.Id));
            op.Add("next", SvJsonNode.String(other.Id));
            var doc = new SvJsonObject();
            doc.Add("format", SvJsonNode.String("osmap.changes/1"));
            doc.Add("ops", new SvJsonArray([op]));
            var finding = new Finding(Severity.Info, Next, $"{a.Id}'s {end} meets {other.Id}, but its Next is not set, so its items vanish there",
                [ObjectRef.Actor(a.Id), ObjectRef.Actor(other.Id)], at);
            c.Report(finding.WithFix(new FindingFix($"Set {a.Id}'s Next to {other.Id}", doc) { Was = [(a.Id, a.Location)] }));
            return;
        }
    }

    /// <summary>
    /// Damage volumes riders pass through: an upright body (feet to head, its radius around) standing at each place
    /// along the path and, for an open conveyor, a body's radius past the end they are carried to. A volume that starts
    /// off is a note (an event may switch it on).
    /// </summary>
    private static void CheckDamage(CheckContext c, Actor a, Conveyor conveyor, ConveyorPath path)
    {
        if (!conveyor.bCarryPlayers || conveyor.Speed == 0)
        {
            return;
        }

        var volumes = c.Project.Actors.Where(v => v.Shape is { Faces.Count: >= 4 } && c.IsA(v, DamageVolumeClass))
            .Select(v => (Volume: v, Zone: ConvexZone.Of(v))).ToList();
        if (volumes.Count == 0)
        {
            return;
        }

        var feet = new List<Vec3>();
        int n = Math.Max(1, (int)Math.Ceiling(path.Length / DamageStep));
        for (int i = 0; i <= n; i++)
        {
            path.Evaluate(path.Length * i / n, out FVector p, out _);
            feet.Add(ConveyorDesign.V(p));
        }

        if (!path.IsLoop)
        {
            (Vec3 end, _, Vec3 ahead) = CarriedTo(c, conveyor, path);
            feet.Add(new Vec3(ahead.X, ahead.Y, end.Z));
        }

        double height = 2 * c.Design.StandHalfHeight, radius = c.Design.BodyRadius;
        foreach ((Actor volume, ConvexZone zone) in volumes)
        {
            int first = feet.FindIndex(f => zone.Touches(f, height, radius));
            if (first < 0)
            {
                continue;
            }

            bool active = c.Bool(volume, "bActive");
            string message = $"{a.Id} carries riders into {volume.Id}, a damage volume ({c.Int(volume, "HitPoints", 5)} damage a hit)"
                + (active ? "" : "; it starts off");
            c.Report(active ? Severity.Warning : Severity.Info, Damage, message, feet[first], ObjectRef.Actor(a.Id), ObjectRef.Actor(volume.Id));
        }
    }

    /// <summary>
    /// A volume's shape in world space as its faces' planes, each facing away from the shape's middle: a point behind
    /// all of them is inside (volumes are convex).
    /// </summary>
    private sealed record ConvexZone(List<(Vec3 Normal, double D)> Planes)
    {
        public static ConvexZone Of(Actor volume)
        {
            List<List<Vec3>> faces = volume.Shape!.Faces.Where(f => f.Vertices.Count >= 3)
                .Select(f => f.Vertices.Select(volume.LocalToWorld).ToList()).ToList();
            List<Vec3> corners = faces.SelectMany(f => f).ToList();
            Vec3 middle = corners.Aggregate(Vec3.Zero, (sum, p) => sum + p) / Math.Max(1, corners.Count);
            var planes = new List<(Vec3, double)>();
            foreach (List<Vec3> f in faces)
            {
                Vec3 normal = new BrushFace { Vertices = f }.Normal();
                if (normal.LengthSquared < 0.5)
                {
                    continue;
                }

                double d = Vec3.Dot(normal, f[0]);
                planes.Add(Vec3.Dot(normal, middle) > d ? (-normal, -d) : (normal, d));
            }

            return new ConvexZone(planes);
        }

        /// <summary>
        /// Whether an upright body standing at <paramref name="feet"/> touches the shape: its centre line from the feet
        /// up <paramref name="height"/> passes through the shape grown by <paramref name="radius"/> (generous at the
        /// shape's edges and corners, by at most the radius).
        /// </summary>
        public bool Touches(Vec3 feet, double height, double radius)
        {
            if (Planes.Count < 4)
            {
                return false;
            }

            // The part of the line (0 at the feet, 1 at the head) behind every grown plane.
            double from = 0, to = 1;
            foreach ((Vec3 normal, double d) in Planes)
            {
                double over = Vec3.Dot(normal, feet) - d - radius, rate = normal.Z * height;
                if (Math.Abs(rate) < 1e-9)
                {
                    if (over > 0)
                    {
                        return false;
                    }

                    continue;
                }

                double t = -over / rate;
                if (rate > 0)
                {
                    to = Math.Min(to, t);
                }
                else
                {
                    from = Math.Max(from, t);
                }

                if (from > to)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
