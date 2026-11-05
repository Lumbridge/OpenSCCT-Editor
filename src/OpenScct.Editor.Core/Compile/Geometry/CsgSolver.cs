using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// The UE2 CSG for one brush: which parts of its faces end up as world surfaces, given the brushes around it.
/// <para>The world starts solid; each solid brush, in order, makes its volume solid (add) or empty (subtract), so a
/// point's state is the operation of the last brush holding it. A face part of a solid brush is a surface when the
/// state just inside the brush (its own operation, as no later brush covers that part) differs from the state just
/// outside (the last earlier brush holding the outside, else solid). Parts lying in or on a later brush are dropped:
/// the later brush's face wins on coplanar faces, as the stock editor's CSG keeps the last brush's texture. Surfaces
/// face the empty side: an add brush's faces point out, a subtract brush's are turned inwards.</para>
/// <para>Semisolid and non-solid faces never cut anything: their parts are kept where the space in front of them is
/// empty and not inside another semisolid brush. Portal faces are kept whole for the zone builder.</para>
/// Each brush depends only on the brushes whose bounds touch its own, which lets the preview rebuild one brush and its
/// neighbours after an edit.
/// </summary>
public static class CsgSolver
{
    /// <summary>The output polygons of <paramref name="b"/>, each facing the empty side. <paramref name="neighbours"/>
    /// are the brushes whose bounds overlap <paramref name="b"/>'s (any order; <paramref name="b"/> itself is skipped).
    /// <paramref name="worldSolid"/> is the state outside every brush: solid for a UE2 level, empty when the brushes
    /// are added to a baked world.</summary>
    public static List<Poly> Solve(CsgBrush b, IReadOnlyList<CsgBrush> neighbours, bool worldSolid = true)
    {
        var output = new List<Poly>();
        if (b.Faces.Count == 0)
        {
            return output;
        }

        List<CsgBrush> spaceAll = neighbours.Where(n => n != b && n.ShapesSpace).OrderBy(n => n.Order).ToList();
        foreach (Poly face in b.Faces)
        {
            switch (face.Surface.Kind)
            {
                case SurfaceKind.Portal:
                    output.Add(face);
                    break;
                case SurfaceKind.Solid when b.ShapesSpace:
                    SolveSolidFace(b, face, spaceAll, worldSolid, output);
                    break;
                default:
                    SolveDecoration(b, face, spaceAll, neighbours, worldSolid, output);
                    break;
            }
        }

        return output;
    }

    private static void SolveSolidFace(CsgBrush b, Poly face, List<CsgBrush> space, bool worldSolid, List<Poly> output)
    {
        var parts = new List<Poly> { face };
        foreach (CsgBrush later in space)
        {
            if (later.Order > b.Order && parts.Count > 0 && later.Bounds.Overlaps(face.Bounds()))
            {
                parts = Outside(parts, later, sameSideInside: true, oppositeInside: true);
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        bool insideSolid = b.Oper == CsgOper.Add;
        for (int i = space.Count - 1; i >= 0 && parts.Count > 0; i--)
        {
            CsgBrush earlier = space[i];
            if (earlier.Order >= b.Order || !earlier.Bounds.Overlaps(face.Bounds()))
            {
                continue;
            }

            // The outer side of a part lies in the earlier brush when the part is inside its volume, or on one of its
            // faces that looks the other way (two faces back to back).
            (List<Poly> inside, List<Poly> outside) = SplitByVolume(parts, earlier, face.Plane.N);
            bool outerSolid = earlier.Oper == CsgOper.Add;
            if (outerSolid != insideSolid)
            {
                Emit(inside, b.Oper, output);
            }

            parts = outside;
        }

        // Outside every earlier brush: the world's own state.
        if (insideSolid != worldSolid)
        {
            Emit(parts, b.Oper, output);
        }
    }

    private static void Emit(List<Poly> parts, CsgOper oper, List<Poly> output)
    {
        foreach (Poly p in parts)
        {
            output.Add(oper == CsgOper.Add ? p : p.Reversed());
        }
    }

    /// <summary>Semisolid, non-solid and sheet faces: the parts whose front lies in empty space and not inside another
    /// semisolid brush.</summary>
    private static void SolveDecoration(CsgBrush b, Poly face, List<CsgBrush> space, IReadOnlyList<CsgBrush> neighbours, bool worldSolid,
        List<Poly> output)
    {
        var parts = new List<Poly> { face };
        Aabb fb = face.Bounds();
        foreach (CsgBrush other in neighbours)
        {
            if (other != b && other.Kind == SurfaceKind.Semisolid && !other.IsSheet && parts.Count > 0 && other.Bounds.Overlaps(fb))
            {
                parts = SplitByVolume(parts, other, face.Plane.N).Outside;
            }
        }

        for (int i = space.Count - 1; i >= 0 && parts.Count > 0; i--)
        {
            CsgBrush k = space[i];
            if (!k.Bounds.Overlaps(fb))
            {
                continue;
            }

            (List<Poly> inside, List<Poly> outside) = SplitByVolume(parts, k, face.Plane.N);
            if (k.Oper == CsgOper.Subtract)
            {
                output.AddRange(inside);
            }

            parts = outside;
        }

        // The rest has the world in front of it: shown when the world is empty there.
        if (!worldSolid)
        {
            output.AddRange(parts);
        }
    }

    /// <summary>The parts of <paramref name="polys"/> outside a brush's volume. A part on a face of the volume counts
    /// as inside when it faces the same way (<paramref name="sameSideInside"/>) or the other way
    /// (<paramref name="oppositeInside"/>).</summary>
    public static List<Poly> Outside(List<Poly> polys, CsgBrush vol, bool sameSideInside, bool oppositeInside)
    {
        var result = new List<Poly>();
        foreach (Poly p in polys)
        {
            var pending = new List<Poly> { p };
            foreach (Plane3[] piece in vol.Pieces)
            {
                var next = new List<Poly>();
                foreach (Poly q in pending)
                {
                    ClipToPiece(q, piece, p.Plane.N, sameSideInside, oppositeInside, next, null);
                }

                pending = next;
                if (pending.Count == 0)
                {
                    break;
                }
            }

            result.AddRange(pending);
        }

        return result;
    }

    /// <summary>Splits polygons into the parts whose outer side (just in front of them, along
    /// <paramref name="faceNormal"/>) is inside the brush's volume, and the rest.</summary>
    public static (List<Poly> Inside, List<Poly> Outside) SplitByVolume(List<Poly> polys, CsgBrush vol, Vec3 faceNormal)
    {
        var inside = new List<Poly>();
        var outside = new List<Poly>();
        foreach (Poly p in polys)
        {
            var pending = new List<Poly> { p };
            foreach (Plane3[] piece in vol.Pieces)
            {
                var next = new List<Poly>();
                foreach (Poly q in pending)
                {
                    // A coplanar part's outer side is outside this plane when the plane faces the way the face does.
                    ClipToPiece(q, piece, faceNormal, sameSideInside: false, oppositeInside: true, next, inside);
                }

                pending = next;
                if (pending.Count == 0)
                {
                    break;
                }
            }

            outside.AddRange(pending);
        }

        return (inside, outside);
    }

    /// <summary>Clips <paramref name="q"/> by one convex piece: parts outside go to <paramref name="outside"/>, the
    /// part inside to <paramref name="inside"/> (or is dropped when it is null).</summary>
    private static void ClipToPiece(Poly q, Plane3[] piece, Vec3 faceNormal, bool sameSideInside, bool oppositeInside,
        List<Poly> outside, List<Poly>? inside)
    {
        Poly? rest = q;
        foreach (Plane3 plane in piece)
        {
            switch (rest.Classify(plane))
            {
                case PlaneSide.Front:
                    outside.Add(rest);
                    return;
                case PlaneSide.On:
                    bool same = Vec3.Dot(plane.N, faceNormal) > 0;
                    if (same ? !sameSideInside : !oppositeInside)
                    {
                        outside.Add(rest);
                        return;
                    }

                    break;
                case PlaneSide.Split:
                    (Poly? f, Poly? b) = rest.Split(plane);
                    if (f is not null)
                    {
                        outside.Add(f);
                    }

                    if (b is null)
                    {
                        return;
                    }

                    rest = b;
                    break;
            }
        }

        inside?.Add(rest);
    }
}
