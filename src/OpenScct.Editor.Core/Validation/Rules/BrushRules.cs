using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>Brush shapes: closed, convex, planar, outward, and with a scale that is not zero. Brush-shaped actors
/// (volumes, movers) are checked too.</summary>
internal sealed class BrushRules : MapRule
{
    public const string Empty = "brush.empty";
    public const string DegenerateFace = "brush.degenerate-face";
    public const string ZeroAreaFace = "brush.zero-area-face";
    public const string NonPlanar = "brush.non-planar";
    public const string FaceNotConvex = "brush.face-not-convex";
    public const string NotConvex = "brush.not-convex";
    public const string NotClosed = "brush.not-closed";
    public const string InsideOut = "brush.inside-out";
    public const string ZeroScale = "brush.zero-scale";
    public const string UnknownBuilder = "brush.unknown-builder";
    public const string BakedEdited = "brush.baked-edited";
    public const string BakedDeleted = "brush.baked-deleted";
    public const string BakedOneLayer = "brush.baked-one-layer";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(Empty, Severity.Error, "A brush with no faces"),
        new(DegenerateFace, Severity.Error, "A face with fewer than three vertices"),
        new(ZeroAreaFace, Severity.Warning, $"A face with no area (under {ShapeAnalysis.ZeroArea} square units)"),
        new(NonPlanar, Severity.Warning, $"A face whose vertices are more than {ShapeAnalysis.PlanarTolerance} off its plane"),
        new(FaceNotConvex, Severity.Warning, "A face whose outline is not convex"),
        new(NotConvex, Severity.Warning, "A solid brush that is not convex (the CSG and the original editor want convex brushes)"),
        new(NotClosed, Severity.Warning, "A solid brush with open edges (holes or T-junctions)"),
        new(InsideOut, Severity.Error, "A solid brush wound inside out (negative volume)"),
        new(ZeroScale, Severity.Error, "A brush scale component of zero (the brush has no size)"),
        new(UnknownBuilder, Severity.Warning, "A builder spec of a kind no builder knows (the piece cannot regenerate)"),
        new(BakedEdited, Severity.Warning, "A brush imported with the baked world was edited, but the kept baked layer still holds it as it was"),
        new(BakedDeleted, Severity.Warning, "A brush imported with the baked world was deleted, but the kept baked layer still holds it"),
        new(BakedOneLayer, Severity.Warning, "Only one of the baked render and collision is kept and the map has no brushes to rebuild the "
            + "other from, so it gets no world for the other"),
    ];

    public override void Run(CheckContext c)
    {
        bool bakedKept = BakedContent.BakedWorldKept(c.Project);
        foreach (Brush b in c.Project.Brushes)
        {
            Vec3 s = b.MainScale.Scale(b.PostScale);
            if (s.X == 0 || s.Y == 0 || s.Z == 0)
            {
                c.Report(Severity.Error, ZeroScale, $"{b.Id}: scale {s} has a zero component", b.Location, ObjectRef.ForBrush(b));
            }

            CheckShape(c, b.Shape, ObjectRef.ForBrush(b), b.Id, CheckContext.Centre(b));
            if (bakedKept && BakedContent.IsBaked(b) && BakedContent.ImportedHash(b) is { } imported && imported != BakedContent.Hash(b))
            {
                c.Report(Severity.Warning, BakedEdited,
                    $"{b.Id}: the kept baked {KeptLayers(c.Project.Baked!)} still has this brush as imported; build it from the brushes instead (Map Settings, Baked world)",
                    CheckContext.Centre(b), ObjectRef.ForBrush(b));
            }
        }

        if (bakedKept)
        {
            CheckBakedWorld(c, c.Project.Baked!);
        }

        foreach (Actor a in c.Project.Actors)
        {
            if (a.Shape is { } shape)
            {
                CheckShape(c, shape, ObjectRef.Actor(a.Id), a.Id, a.Location);
            }
        }
    }

    private static void CheckBakedWorld(CheckContext c, BakedWorld baked)
    {
        List<string> deleted = baked.Brushes.Where(id => c.Project.FindBrush(id) is not { } b || !BakedContent.IsBaked(b)).ToList();
        if (deleted.Count > 0)
        {
            string list = string.Join(", ", deleted.Take(10)) + (deleted.Count > 10 ? ", ..." : "");
            c.Report(Severity.Warning, BakedDeleted,
                $"{deleted.Count} brush(es) imported with the baked world were deleted ({list}), but the kept baked {KeptLayers(baked)} still has them; "
                + "build it from the brushes instead (Map Settings, Baked world)", null);
        }

        // With baked brushes the layer not kept is rebuilt from them (BakedContent.Render/Collision); without, it is empty.
        if (baked.OneLayer && !BakedContent.LayersDiffer(c.Project))
        {
            string what = baked.KeepRender ? "Only the baked render is kept: the map has no world collision" : "Only the baked collision is kept: the map's world is not drawn";
            c.Report(Severity.Warning, BakedOneLayer, what + " (it has no brushes to rebuild it from); keep both", null);
        }
    }

    private static string KeptLayers(BakedWorld baked) => baked switch
    {
        { KeepRender: true, KeepCollision: true } => "render and collision",
        { KeepRender: true } => "render",
        _ => "collision",
    };

    private static void CheckShape(CheckContext c, BrushShape shape, ObjectRef r, string id, Vec3 at)
    {
        if (shape.Faces.Count == 0)
        {
            c.Report(Severity.Error, Empty, $"{id} has no faces", at, r);
            return;
        }

        if (shape.Builder is { } spec && BuilderCatalog.Find(spec.Kind) is null)
        {
            c.Report(Severity.Warning, UnknownBuilder, $"{id}: no builder makes '{spec.Kind}'", at, r);
        }

        ShapeReport report = ShapeAnalysis.Analyze(shape);
        if (report.TooFewVertices.Count > 0)
        {
            c.Report(Severity.Error, DegenerateFace, $"{id}: faces {List(report.TooFewVertices)} have fewer than 3 vertices", at, r);
        }

        if (report.ZeroAreaFaces.Count > 0)
        {
            c.Report(Severity.Warning, ZeroAreaFace, $"{id}: faces {List(report.ZeroAreaFaces)} have no area", at, r);
        }

        if (report.NonPlanarFaces.Count > 0)
        {
            c.Report(Severity.Warning, NonPlanar, $"{id}: faces {List(report.NonPlanarFaces)} are not flat (up to {report.MaxPlanarError:0.###} off)", at, r);
        }

        if (report.NonConvexFaces.Count > 0)
        {
            c.Report(Severity.Warning, FaceNotConvex, $"{id}: faces {List(report.NonConvexFaces)} are not convex", at, r);
        }

        if (report.IsSheet)
        {
            return;
        }

        if (report.Volume < 0)
        {
            c.Report(Severity.Error, InsideOut, $"{id} is wound inside out (volume {report.Volume:0})", at, r);
        }

        if (!report.Closed)
        {
            c.Report(Severity.Warning, NotClosed, $"{id} has {report.OpenEdges} open edges", at, r);
        }

        if (!report.Convex && report.Volume >= 0) // an inside-out convex brush also fails the outward test: one finding is enough
        {
            c.Report(Severity.Warning, NotConvex, $"{id} is not convex", at, r);
        }
    }

    private static string List(IReadOnlyList<int> faces) =>
        string.Join(",", faces.Take(8)) + (faces.Count > 8 ? $"... ({faces.Count})" : "");
}
