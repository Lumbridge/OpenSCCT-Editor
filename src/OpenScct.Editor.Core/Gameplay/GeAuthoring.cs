using System.Globalization;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>A problem with a GE's shape, with a stable code (Map Check uses the same codes).</summary>
public sealed record GeProblem(Severity Severity, string Code, string Message);

/// <summary>GE names, shape rules and point helpers shared by the GE tool, Map Check and the CLI.</summary>
public static class GeShapes
{
    public const string LedgeNotLevel = "ge.lg-not-level";
    public const string LedgeTooShort = "ge.lg-too-short";
    public const string ZipLevel = "ge.zl-level";
    public const string ZipSteep = "ge.zl-steep";
    public const string PoleNotVertical = "ge.pole-not-vertical";
    public const string HandOverHandShort = "ge.hoh-short";

    /// <summary>A ledge needs this length: the hands must stay 24.5 from both ends (CheckLedgeArms).</summary>
    public const double LedgeGrabbableLength = 49;

    /// <summary>A hand-over-hand pipe grab needs both ends over 35 from the hands (CheckHOHArms).</summary>
    public const double HandOverHandMinLength = 70;

    /// <summary>The steepest zip line before it is flagged (stock lines are about 10 degrees).</summary>
    public const double ZipSteepDegrees = 30;

    /// <summary>A ladder's or pipe's third point sits this far toward the climber (every stock one).</summary>
    public const double ClimbFacingDistance = 10;

    /// <summary>The engine's test for two GE ends that carry a shimmy: within 2 on every axis.</summary>
    public static bool Connected(Vec3 a, Vec3 b) => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Z - b.Z) <= 2;

    /// <summary>Id stem of a GE kind, as the original editor names them.</summary>
    public static string Stem(string kind) => kind switch
    {
        GeKinds.LedgeGrab => "LedgeGrab",
        GeKinds.ZipLine => "ZipLine",
        GeKinds.Pipe => "Pipe",
        GeKinds.Ladder => "Ladder",
        GeKinds.HandOverHand => "HandOverHand",
        GeKinds.Pole => "Pole",
        GeKinds.Fence => "Fence",
        _ => "GE",
    };

    /// <summary>What is wrong with the shape of a GE of <paramref name="kind"/> through these points (geometry aside).</summary>
    public static List<GeProblem> Problems(string kind, IReadOnlyList<Vec3> p)
    {
        var list = new List<GeProblem>();
        if (p.Count < 2)
        {
            return list;
        }

        Vec3 d = p[1] - p[0];
        double flat = Math.Sqrt(d.X * d.X + d.Y * d.Y), length = d.Length;
        switch (kind)
        {
            case GeKinds.LedgeGrab:
                if (Math.Abs(d.Z) > 2)
                {
                    list.Add(new(Severity.Warning, LedgeNotLevel, $"ledge is not level ({Math.Abs(d.Z):0} rise)"));
                }

                if (length < LedgeGrabbableLength)
                {
                    list.Add(new(Severity.Warning, LedgeTooShort, $"{length:0} long: too short to grab (hands stay 24.5 from both ends)"));
                }

                break;
            case GeKinds.ZipLine:
                double degrees = Math.Atan2(Math.Abs(d.Z), flat) * 180 / Math.PI;
                if (Math.Abs(d.Z) < Math.Max(8, length * 0.01))
                {
                    list.Add(new(Severity.Warning, ZipLevel, "zip line is level: there is no downhill end to slide to"));
                }
                else if (degrees > ZipSteepDegrees)
                {
                    list.Add(new(Severity.Info, ZipSteep, $"zip line slopes {degrees:0} degrees (stock lines are about 10)"));
                }

                break;
            case GeKinds.Pole when flat > 2:
                list.Add(new(Severity.Warning, PoleNotVertical, $"pole leans {flat:0} off vertical"));
                break;
            case GeKinds.HandOverHand when length < HandOverHandMinLength:
                list.Add(new(Severity.Warning, HandOverHandShort, $"{length:0} long: a grab needs both ends over 35 from the hands"));
                break;
        }

        return list;
    }

    /// <summary>A ladder's or pipe's three points from a bottom, a top height and the side the climber is on
    /// (bottom, top straight above it, the facing point 10 toward the climber at the bottom's height).</summary>
    public static List<Vec3> ClimbPoints(Vec3 bottom, double topZ, Vec3 towardClimber)
    {
        Vec3 n = new Vec3(towardClimber.X, towardClimber.Y, 0).Normalized();
        if (n == Vec3.Zero)
        {
            n = Vec3.UnitX;
        }

        return [bottom, bottom with { Z = Math.Max(topZ, bottom.Z + 1) }, bottom + n * ClimbFacingDistance];
    }

    /// <summary>Hands out ids no object of a map uses (ignoring case), several at a time.</summary>
    public static Func<string, string> IdSource(MapProject project)
    {
        var used = new HashSet<string>(project.AllRefs().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        return stem =>
        {
            for (int i = 0; ; i++)
            {
                string id = stem + i.ToString(CultureInfo.InvariantCulture);
                if (used.Add(id))
                {
                    return id;
                }
            }
        };
    }
}

/// <summary>Undoable GE edits: ledges from detected lips, flushing ledges onto their edges, joining corners.</summary>
public static class GeEdits
{
    /// <summary>New LGs for the lips, one undo step (null when there are none).</summary>
    public static AddObjectsCommand? AddLedges(MapProject p, IEnumerable<Lip> lips, string? layer = null, string label = "Add detected ledges")
    {
        Func<string, string> ids = GeShapes.IdSource(p);
        var ges = lips.Select(l => new Ge(ids(GeShapes.Stem(GeKinds.LedgeGrab)), GeKinds.LedgeGrab)
        {
            Points = [Round(l.A), Round(l.B)],
            Editor = new EditorMeta { Layer = layer ?? "" },
        }).ToList();
        return ges.Count == 0 ? null : new AddObjectsCommand($"{label} ({ges.Count})", ges: ges);
    }

    /// <summary>True when an existing LG already covers at least half of the lip, or half of itself lies on the lip
    /// (on its line and height).</summary>
    public static bool Covered(Lip lip, IEnumerable<Ge> ledges, double tolerance = 4) => Covering(lip, ledges, tolerance) is not null;

    /// <summary>The first LG that makes the lip <see cref="Covered"/>, or null.</summary>
    public static Ge? Covering(Lip lip, IEnumerable<Ge> ledges, double tolerance = 4) =>
        ledges.FirstOrDefault(g =>
        {
            double overlap = Overlap(lip, g, tolerance);
            return overlap > 0 && overlap >= Math.Min(lip.Length, (g.Points[1] - g.Points[0]).Length) * 0.5;
        });

    /// <summary>How much of an LG lies along a lip (0 when it is off the lip's line or height).</summary>
    public static double Overlap(Lip lip, Ge g, double tolerance)
    {
        if (g.Kind != GeKinds.LedgeGrab || g.Points.Count < 2)
        {
            return 0;
        }

        Vec3 d = lip.Dir;
        foreach (Vec3 q in g.Points.Take(2))
        {
            Vec3 r = q - lip.A;
            Vec3 across = r - d * Vec3.Dot(r, d);
            if (Math.Sqrt(across.X * across.X + across.Y * across.Y) > tolerance || Math.Abs(across.Z) > tolerance)
            {
                return 0;
            }
        }

        double a = Vec3.Dot(g.Points[0] - lip.A, d), b = Vec3.Dot(g.Points[1] - lip.A, d);
        return Math.Max(0, Math.Min(lip.Length, Math.Max(a, b)) - Math.Max(0, Math.Min(a, b)));
    }

    /// <summary>
    /// Moves each LG onto the lip next to it (<see cref="LedgeProbe.Fit"/>: across its line and to the top's
    /// height). LGs with no edge within <paramref name="reach"/> are listed in <paramref name="notFound"/>.
    /// </summary>
    public static ReplaceObjectsCommand Flush(MapProject p, TraversalWorld w, IEnumerable<string> ids, LedgeOptions o,
        List<string> notFound, double reach = 32)
    {
        var cmd = new ReplaceObjectsCommand("Flush ledges to edges");
        foreach (Ge g in ids.Select(p.FindGe).OfType<Ge>().Where(g => g.Kind == GeKinds.LedgeGrab && g.Points.Count >= 2))
        {
            if (LedgeProbe.Fit(w, g.Points[0], g.Points[1], o, reach) is not { } fit)
            {
                notFound.Add(g.Id);
                continue;
            }

            if (fit.Offset.Length < 0.05 && g.Points.Take(2).All(q => Math.Abs(q.Z - fit.TopZ) < 0.05))
            {
                continue;
            }

            Ge after = g.Clone();
            for (int i = 0; i < 2; i++)
            {
                Vec3 q = g.Points[i] + fit.Offset;
                after.Points[i] = Round(q with { Z = fit.TopZ });
            }

            cmd.Ge(g.Clone(), after);
        }

        return cmd;
    }

    /// <summary>Ledge ends closer than this are one corner with a gap (Map Check flags them, join closes them); further
    /// apart they are separate ledges.</summary>
    public const double CornerReach = 24;

    /// <summary>Moves LG ends that nearly meet onto their shared corner (two-point kinds connect only within 2). Ends
    /// that already connect stay where they are.</summary>
    public static ReplaceObjectsCommand JoinCorners(MapProject p, IEnumerable<string> ids, double maxDistance = CornerReach)
    {
        List<Ge> ges = ids.Select(p.FindGe).OfType<Ge>().Where(g => g.Kind == GeKinds.LedgeGrab && g.Points.Count >= 2).ToList();
        List<Lip> lips = ges.Select(g => new Lip(g.Points[0], g.Points[1], Vec3.Zero, 0)).ToList();
        List<Lip> joined = LedgeDetector.JoinCorners(lips, maxDistance, skipConnected: true);
        var cmd = new ReplaceObjectsCommand("Join ledge corners");
        for (int i = 0; i < ges.Count; i++)
        {
            if (!joined[i].Joined || ((joined[i].A - lips[i].A).Length < 0.01 && (joined[i].B - lips[i].B).Length < 0.01))
            {
                continue;
            }

            Ge after = ges[i].Clone();
            after.Points[0] = Round(joined[i].A);
            after.Points[1] = Round(joined[i].B);
            cmd.Ge(ges[i].Clone(), after);
        }

        return cmd;
    }

    /// <summary>Sets the GE ID bit of GEs (0 = always on, else 1, 2, 4 ... 128).</summary>
    public static ReplaceObjectsCommand SetMask(MapProject p, IEnumerable<string> ids, int mask) =>
        ReplaceObjectsCommand.Edit(p, ids.Select(ObjectRef.Ge), $"GE ID {mask}", ge: g => g.Mask = mask);

    /// <summary>GE platform names by value (original GEPlatform): every platform, PS2 only, Xbox only.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["common", "ps2", "xbox"];

    /// <summary>Sets the platform of GEs (0 common, 1 PS2 only, 2 Xbox only; kept for round trips to the original).</summary>
    public static ReplaceObjectsCommand SetPlatform(MapProject p, IEnumerable<string> ids, int platform) =>
        ReplaceObjectsCommand.Edit(p, ids.Select(ObjectRef.Ge), $"GE platform {Platforms[platform]}", ge: g => g.Platform = platform);

    /// <summary>Sets LevelInfo.GEActivationMask: which GE ID bits are on when the round starts.</summary>
    public static ProjectSettingsCommand SetActivationMask(int mask) =>
        new($"GE activation mask {mask}", proj => proj.LevelInfo.Set("GEActivationMask", OpenScct.Content.Json.SvJsonNode.Integer(mask & 0xFF)));

    /// <summary>Rounds to a hundredth, so files stay readable and float32 round trips do not drift.</summary>
    public static Vec3 Round(Vec3 v) => new(Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 2));
}
