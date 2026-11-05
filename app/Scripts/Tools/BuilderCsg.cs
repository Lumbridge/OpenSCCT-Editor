using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>What Intersect or Deintersect made: the new builder pieces (builder-local shapes), or why not.</summary>
public sealed record BuilderCsgResult(IReadOnlyList<BrushShape> Pieces, int SkippedConcave, string? Error);

/// <summary>
/// UnrealEd's Intersect and Deintersect for the builder brush. The world starts solid; brushes in CSG order carve
/// (subtract) and fill (add) it. Intersect keeps the part of the builder brush that is solid world, Deintersect the
/// part that is empty space. Like UE2 (which clips against the BSP, which semi-solid and non-solid brushes are not
/// part of), only solid brushes count. Works with convex brushes; concave ones (stairs, sheets) are skipped and
/// counted. The result is one convex piece per region, so Add makes clean convex brushes.
/// </summary>
public static class BuilderCsg
{
    /// <summary>Gives up past this many pieces (a builder brush across a whole detailed map).</summary>
    public const int MaxPieces = 4096;

    public static BuilderCsgResult Intersect(MapProject p, BuilderBrush builder) => Run(p, builder, keepSolid: true);

    public static BuilderCsgResult Deintersect(MapProject p, BuilderBrush builder) => Run(p, builder, keepSolid: false);

    private static BuilderCsgResult Run(MapProject p, BuilderBrush builder, bool keepSolid)
    {
        var result = new List<BrushShape>();
        int skipped = 0;
        foreach (Brush piece in builder.WorldPieces())
        {
            if (Solid(piece) is not { } region)
            {
                return new BuilderCsgResult([], 0, "the builder brush is not convex");
            }

            (List<BrushShape>? solid, int concave) = SolidWithin(p, region);
            skipped = Math.Max(skipped, concave);
            if (solid is null)
            {
                return new BuilderCsgResult([], skipped, "too many pieces; use a smaller builder brush");
            }

            if (keepSolid)
            {
                result.AddRange(solid);
            }
            else
            {
                List<BrushShape>? empty = Minus([region], solid);
                if (empty is null)
                {
                    return new BuilderCsgResult([], skipped, "too many pieces; use a smaller builder brush");
                }

                result.AddRange(empty);
            }
        }

        return new BuilderCsgResult(result.Select(s => ToLocal(s, builder)).ToList(), skipped, null);
    }

    /// <summary>The solid world inside a convex region, in CSG order; null when it splinters past <see cref="MaxPieces"/>.</summary>
    private static (List<BrushShape>? Solid, int SkippedConcave) SolidWithin(MapProject p, BrushShape region)
    {
        (Vec3 rmin, Vec3 rmax) = region.Bounds();
        var solid = new List<BrushShape> { region };
        int skipped = 0;
        foreach (Brush b in p.Brushes)
        {
            if (b.IsSemisolid || b.IsNotSolid || b.IsPortal)
            {
                continue;
            }

            WorldBrush w = BrushGeometry.Of(b);
            if (w.IsEmpty || !ShapeCsg.Overlaps(w.Min, w.Max, rmin, rmax))
            {
                continue;
            }

            if (Solid(b) is not { } brush)
            {
                skipped++;
                continue;
            }

            List<BrushShape>? next;
            if (b.Oper == CsgOper.Subtract)
            {
                next = Minus(solid, [brush]);
            }
            else if (ShapeCsg.Intersect(brush, region) is { } added && Minus([added], solid) is { } fresh)
            {
                next = [.. solid, .. fresh];
            }
            else
            {
                next = solid;
            }

            if (next is null || next.Count > MaxPieces)
            {
                return (null, skipped);
            }

            solid = next;
        }

        return (solid, skipped);
    }

    /// <summary>Every piece of <paramref name="from"/> minus every solid of <paramref name="cut"/>; null past the limit.</summary>
    private static List<BrushShape>? Minus(List<BrushShape> from, List<BrushShape> cut)
    {
        List<BrushShape> pieces = from;
        foreach (BrushShape c in cut)
        {
            (Vec3 cmin, Vec3 cmax) = c.Bounds();
            var next = new List<BrushShape>();
            foreach (BrushShape piece in pieces)
            {
                (Vec3 pmin, Vec3 pmax) = piece.Bounds();
                if (ShapeCsg.Overlaps(pmin, pmax, cmin, cmax))
                {
                    next.AddRange(ShapeCsg.Subtract(piece, c));
                }
                else
                {
                    next.Add(piece);
                }
            }

            if (next.Count > MaxPieces)
            {
                return null;
            }

            pieces = next;
        }

        return pieces;
    }

    /// <summary>The brush as a convex world-space shape, or null for concave brushes (stairs, sheets).</summary>
    private static BrushShape? Solid(Brush b) => ShapeCsg.World(b) is var shape && ShapeCsg.IsConvex(shape) ? shape : null;

    /// <summary>A world-space piece as a shape in the builder brush's frame, with default planar texture alignment.</summary>
    private static BrushShape ToLocal(BrushShape piece, BuilderBrush builder)
    {
        var shape = new BrushShape();
        foreach (BrushFace f in piece.Faces)
        {
            var face = new BrushFace
            {
                Material = f.Material,
                Flags = f.Flags,
                Vertices = f.Vertices.Select(v => builder.Rotation.Unrotate(v - builder.Location)).ToList(),
            };
            TextureAlign.Planar(face);
            shape.Faces.Add(face);
        }

        return shape;
    }
}
