using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>Visible traversal pieces derived from a GE, shared by drawing, builds and original-game exports.</summary>
public static class GeGeometry
{
    public const int MaxRepeats = 256;

    /// <summary>Stock chain-link material with bClimbable, also used by TestRange's fence.</summary>
    public const string FenceMaterial = "drpoc_chantier.SM.moa_grillage";

    public static bool Supports(string kind) => kind is GeKinds.Ladder or GeKinds.Pipe or GeKinds.Pole
        or GeKinds.ZipLine or GeKinds.HandOverHand or GeKinds.Fence;

    public static GeAppearance Defaults(string kind) => kind switch
    {
        GeKinds.Ladder => new() { Width = 48, Radius = 3, Spacing = 64 },
        GeKinds.Pipe => new() { Radius = 6 },
        GeKinds.Pole => new() { Radius = 8 },
        GeKinds.ZipLine => new() { Radius = 2 },
        GeKinds.HandOverHand => new() { Radius = 4 },
        GeKinds.Fence => new() { Width = 256, Radius = 3, Spacing = 128, Material = FenceMaterial },
        _ => new(),
    };

    /// <summary>Rejects settings that cannot be drawn safely. A zero-length drag has no geometry yet.</summary>
    public static void Validate(Ge ge)
    {
        if (ge.Appearance is not { } look)
        {
            return;
        }

        if (!Supports(ge.Kind))
        {
            throw new ArgumentException($"{ge.Kind} has no generated geometry");
        }

        Range(look.Width, 4, 512, "width");
        Range(look.Radius, 0.5, 32, "radius");
        Range(look.Spacing, 4, 256, "spacing");
        if (ge.Points.Any(p => !Finite(p)))
        {
            throw new ArgumentException("GE geometry needs finite points");
        }

        if (ge.Points.Any(p => Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) > 1048576))
        {
            throw new ArgumentException("GE geometry points must stay within 1048576 units of the map origin");
        }

        if (ge.Points.Count < 2)
        {
            return;
        }

        double length = (ge.Points[1] - ge.Points[0]).Length;
        if (!double.IsFinite(length) || length > 262144)
        {
            throw new ArgumentException("GE geometry length must be at most 262144 units");
        }

        if (ge.Kind is GeKinds.Ladder or GeKinds.Fence && Math.Ceiling(length / look.Spacing) > MaxRepeats)
        {
            throw new ArgumentException($"GE geometry needs at most {MaxRepeats} repeated pieces; increase spacing or shorten the line");
        }

        if (ge.Kind == GeKinds.Ladder && look.Width <= look.Radius * 2)
        {
            throw new ArgumentException("ladder width must be larger than its rail diameter");
        }
    }

    /// <summary>
    /// Ordinary brushes in world space, with stable names local to this GE. Pipes are solid and sit behind the
    /// grab line so their front surface leaves room for the climbing cylinder. Fences have a two-unit solid panel (a wider
    /// panel blocks the native top-out sweep); their material must be climbable for climbing the fence body.
    /// </summary>
    public static IReadOnlyList<Brush> Build(Ge ge)
    {
        if (ge.Appearance is not { } look || !Supports(ge.Kind))
        {
            return [];
        }

        Validate(ge);
        if (ge.Points.Count < 2 || (ge.Points[1] - ge.Points[0]).Length < 0.01)
        {
            return [];
        }

        var pieces = new List<Brush>();
        Vec3 a = ge.Points[0], b = ge.Points[1], direction = (b - a).Normalized();
        double length = (b - a).Length;
        switch (ge.Kind)
        {
            case GeKinds.Ladder:
                Vec3 face = ge.Points.Count >= 3 ? (ge.Points[2] - a).With(2, 0).Normalized() : Vec3.UnitX;
                if (face == Vec3.Zero)
                {
                    face = Vec3.UnitX;
                }

                Vec3 across = Vec3.Cross(direction, face).Normalized();
                if (across == Vec3.Zero)
                {
                    across = Poly.Basis(direction).U;
                }

                Vec3 half = across * (look.Width / 2);
                Tube(a - half, b - half, look.Radius);
                Tube(a + half, b + half, look.Radius);
                // Native ladder holds repeat every 64 units at bottom + 28. Other spacing is an artistic override.
                double first = Math.Min(28, look.Spacing / 2);
                for (double distance = first; distance < length; distance += look.Spacing)
                {
                    Vec3 rung = a + direction * distance;
                    Tube(rung - half, rung + half, look.Radius);
                }

                break;
            case GeKinds.Pipe:
                Vec3 climber = ge.Points.Count >= 3 ? (ge.Points[2] - a).With(2, 0).Normalized() : Vec3.UnitX;
                if (climber == Vec3.Zero) { climber = Vec3.UnitX; }
                // The standing cylinder has radius 35 and the pipe hold is 37 from the GE. Keeping the
                // front surface on the line blocks walking through without trapping the climb in the pole.
                // Generated brush collision uses an axis-aligned box, whose reach along diagonal facing
                // directions is wider than 35. Account for it as well as the visual pole radius.
                double boxReach = 35 * (Math.Abs(climber.X) + Math.Abs(climber.Y));
                Vec3 offset = -climber * (look.Radius + boxReach - 35);
                Tube(a + offset, b + offset, look.Radius, solid: true);
                break;
            case GeKinds.Pole:
                Tube(a, b, look.Radius, solid: true);
                break;
            case GeKinds.Fence:
                Vec3 side = Vec3.Cross(direction, Vec3.UnitZ).Normalized();
                if (side == Vec3.Zero)
                {
                    return [];
                }

                Vec3 down = Vec3.UnitZ * look.Width;
                Add(ConvexSolid.Hull(new[] { a - side, a + side, b - side, b + side,
                    a - side - down, a + side - down, b - side - down, b + side - down }), solid: true);
                Tube(a, b, look.Radius);
                Tube(a - down, b - down, look.Radius);
                int bays = Math.Max(1, (int)Math.Ceiling(length / look.Spacing));
                for (int i = 0; i <= bays; i++)
                {
                    Vec3 top = a + (b - a) * (i / (double)bays);
                    Tube(top - down, top, look.Radius);
                }

                break;
            default:
                Tube(a, b, look.Radius);
                break;
        }

        return pieces;

        void Tube(Vec3 from, Vec3 to, double radius, bool solid = false)
        {
            Vec3 axis = (to - from).Normalized();
            (Vec3 u, Vec3 v) = Poly.Basis(axis);
            const int sides = 8;
            Vec3[] ring = Enumerable.Range(0, sides)
                .Select(i => (u * Math.Cos(i * Math.Tau / sides) + v * Math.Sin(i * Math.Tau / sides)) * radius).ToArray();
            var polys = new List<List<Vec3>>
            {
                ring.Reverse().Select(p => from + p).ToList(),
                ring.Select(p => to + p).ToList(),
            };
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                polys.Add([from + ring[i], from + ring[j], to + ring[j], to + ring[i]]);
            }

            // Face winding is explicit so a basis implementation change cannot turn a generated part inside out.
            Vec3 centre = (from + to) / 2;
            foreach (List<Vec3> poly in polys)
            {
                if (Vec3.Dot(Poly.Normal(poly), Poly.Average(poly) - centre) < 0)
                {
                    poly.Reverse();
                }
            }

            Add(polys, solid);
        }

        void Add(List<List<Vec3>> polygons, bool solid = false)
        {
            pieces.Add(new Brush($"{ge.Id}_Geometry{pieces.Count}")
            {
                Oper = CsgOper.Add,
                PolyFlags = solid ? 0 : PolyFlags.NotSolid,
                Shape = new BrushShape { Faces = FaceKit.Make(look.Material, polygons) },
                Editor = ge.Editor.Clone(),
            });
        }
    }

    /// <summary>
    /// Lowers generated appearances into ordinary brushes without changing the source project. Names are unique
    /// across all object kinds. The copied GEs lose only their appearance, making this operation idempotent.
    /// </summary>
    public static MapProject Expand(MapProject project) => Expand(project, skipInvalid: false);

    /// <summary>Preview lowering: omits invalid appearances while Map Check reports their errors.</summary>
    public static MapProject ExpandValid(MapProject project) => Expand(project, skipInvalid: true);

    private static MapProject Expand(MapProject project, bool skipInvalid)
    {
        if (!project.Ges.Any(g => g.Appearance is not null))
        {
            return project;
        }

        MapProject expanded = ProjectSnapshot.Take(project);
        expanded.Prefabs = project.Prefabs.Select(p => p.Clone()).ToList();
        expanded.LoadNotes.AddRange(project.LoadNotes);
        var used = expanded.AllRefs().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Ge ge in expanded.Ges.Where(g => g.Appearance is not null))
        {
            IReadOnlyList<Brush> brushes;
            try
            {
                Validate(ge);
                brushes = Build(ge);
            }
            catch (ArgumentException) when (skipInvalid)
            {
                ge.Appearance = null;
                continue;
            }

            foreach (Brush brush in brushes)
            {
                string stem = brush.Id;
                for (int n = 1; !used.Add(brush.Id); n++)
                {
                    brush.Id = $"{stem}_{n}";
                }

                expanded.Brushes.Add(brush);
            }

            ge.Appearance = null;
        }

        return expanded;
    }

    private static bool Finite(Vec3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private static void Range(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new ArgumentException($"GE geometry {name} must be {min}..{max}");
        }
    }
}
