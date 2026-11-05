using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>
/// Tells open space from solid by replaying a project's brushes in CSG order: the UE2 world starts solid, every
/// subtractive brush opens the space inside it and every additive brush fills it again. Not-solid and portal brushes
/// and sheets change nothing; semisolid additive brushes still block. A concave brush is the union of the convex pieces
/// the compiler's CSG splits it into. It knows nothing of a baked world, static meshes or movers.
/// </summary>
public sealed class SolidQuery
{
    private readonly List<Volume> _volumes;

    public SolidQuery(IEnumerable<Brush> brushes) =>
        _volumes = brushes.Where(Counts).Select(Volume.Of).Where(v => v.Pieces.Count > 0).ToList();

    public static SolidQuery Of(MapProject project) => new(project.Brushes);

    /// <summary>True when the world point is inside solid geometry.</summary>
    public bool IsSolid(Vec3 p)
    {
        bool solid = true;
        foreach (Volume v in _volumes)
        {
            if (v.Contains(p))
            {
                solid = v.Oper == CsgOper.Add;
            }
        }

        return solid;
    }

    /// <summary>
    /// How far straight down from <paramref name="p"/> the first solid point is (0 when <paramref name="p"/> is in
    /// solid), or null when there is none within <paramref name="maxDrop"/>. Exact: the vertical line is cut at every
    /// brush boundary and each piece between two cuts is classified once.
    /// </summary>
    public double? DropToFloor(Vec3 p, double maxDrop)
    {
        double bottom = p.Z - maxDrop;
        var cuts = new List<double> { p.Z, bottom };
        foreach (Volume v in _volumes)
        {
            foreach ((double lo, double hi) in v.VerticalSpans(p.X, p.Y))
            {
                foreach (double z in new[] { lo, hi })
                {
                    if (z < p.Z && z > bottom)
                    {
                        cuts.Add(z);
                    }
                }
            }
        }

        cuts.Sort((a, b) => b.CompareTo(a));
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            if (cuts[i] - cuts[i + 1] > 1e-6 && IsSolid(new Vec3(p.X, p.Y, (cuts[i] + cuts[i + 1]) / 2)))
            {
                return p.Z - cuts[i];
            }
        }

        return null;
    }

    private static bool Counts(Brush b) =>
        !b.IsNotSolid && !b.IsPortal && b.Shape.Faces.Count >= 4 && !ShapeAnalysis.IsSheetShape(b.Shape);

    /// <summary>A brush as convex pieces of world-space half-spaces, with its bounds.</summary>
    private sealed record Volume(CsgOper Oper, List<Plane3[]> Pieces, Vec3 Min, Vec3 Max)
    {
        private const double Tolerance = 0.01;

        public static Volume Of(Brush b)
        {
            List<Plane3[]> pieces = Compile.CsgBrush.Prepare(b, 0).Pieces
                .Select(piece => piece.Select(q => new Plane3(q.N, q.D)).ToArray()).ToList();
            (Vec3 min, Vec3 max) = BrushOps.BoundsOf(b.WorldFaces().SelectMany(f => f.Vertices));
            return new Volume(b.Oper, pieces, min, max);
        }

        /// <summary>Strictly inside a piece (points on a face count as outside, so a floor's top surface is open).</summary>
        public bool Contains(Vec3 p)
        {
            if (p.X < Min.X || p.Y < Min.Y || p.Z < Min.Z || p.X > Max.X || p.Y > Max.Y || p.Z > Max.Z)
            {
                return false;
            }

            return Pieces.Any(piece => piece.All(plane => plane.Distance(p) <= -Tolerance));
        }

        /// <summary>The heights where the vertical line through (x, y) enters and leaves each piece.</summary>
        public IEnumerable<(double Lo, double Hi)> VerticalSpans(double x, double y)
        {
            if (x < Min.X || y < Min.Y || x > Max.X || y > Max.Y)
            {
                yield break;
            }

            foreach (Plane3[] piece in Pieces)
            {
                if (Span(piece, x, y) is { } span)
                {
                    yield return span;
                }
            }
        }

        private static (double Lo, double Hi)? Span(Plane3[] piece, double x, double y)
        {
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            foreach (Plane3 plane in piece)
            {
                // n.z * z <= D - n.x x - n.y y
                double rest = plane.D - plane.Normal.X * x - plane.Normal.Y * y;
                if (Math.Abs(plane.Normal.Z) < 1e-9)
                {
                    if (rest < 0)
                    {
                        return null; // the line runs outside this side plane
                    }
                }
                else if (plane.Normal.Z > 0)
                {
                    hi = Math.Min(hi, rest / plane.Normal.Z);
                }
                else
                {
                    lo = Math.Max(lo, rest / plane.Normal.Z);
                }
            }

            return lo < hi && double.IsFinite(lo) && double.IsFinite(hi) ? (lo, hi) : null;
        }
    }
}
