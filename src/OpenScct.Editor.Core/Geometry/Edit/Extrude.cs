using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>
/// Extruding brush faces (UE2 face drag and extrude): push a face out or in, or grow a new brush from it.
/// </summary>
public static class Extrude
{
    /// <summary>
    /// Moves a face along its normal by <paramref name="distance"/> (negative pulls it in). A convex brush stays
    /// convex: pushing out takes the hull with the moved face, pulling in clips at the moved plane. Other brushes
    /// move the face's corners and split side faces that bend. Face indexes stay where they were (the moved face
    /// keeps its index), the moved face keeps its surface, and new side faces take their neighbour's material.
    /// </summary>
    public static VertexEditResult Face(Brush b, int face, double distance)
    {
        BrushFace f = FaceOf(b, face);
        ShapeTransform t = ShapeTransform.For(b);
        List<Vec3> world = t.FaceToWorld(f);
        Vec3 n = Poly.Normal(world);
        if (n == Vec3.Zero)
        {
            throw new ArgumentException($"extrude: face {face} of {b.Id} has no area");
        }

        if (Math.Abs(distance) < 1e-9)
        {
            return new VertexEditResult(b.Clone(), 0, 0, ShapeAnalysis.IsConvexSolid(b.Shape));
        }

        if (!ShapeAnalysis.IsConvexSolid(b.Shape))
        {
            return VertexEdit.Move(b, world, n * distance);
        }

        List<Plane3?> before = b.Shape.Faces.Select(x => Plane3.FromPolygon(x.Vertices)).ToList();
        List<BrushFace> faces;
        Plane3? movedPlane;
        if (distance < 0)
        {
            Plane3 plane = Plane3.FromPointNormal(world[0] + n * distance, n);
            Brush clipped = BrushOps.Clip(b, plane, f.Material)
                ?? throw new ArgumentException("extrude: pulling the face in that far removes the whole brush");
            // The cap is the face in its new place: it keeps the face's surface (same plane direction, same mapping).
            faces = clipped.Shape.Faces.Select(x => IsNew(x, before) ? WithVertices(f, x.Vertices) : x).ToList();
            movedPlane = faces.Select(x => Plane3.FromPolygon(x.Vertices)).FirstOrDefault(p => p is { } q && !Matches(q, before));
        }
        else
        {
            BrushFace moved = WithVertices(f, world.Select(v => Poly.Tidy(t.ToLocal(v + n * distance))).ToList());
            if (t.Mirrors)
            {
                moved.Vertices.Reverse();
            }

            var shape = new BrushShape { Faces = [.. b.Shape.Faces.Select(x => x.Clone()), moved] };
            movedPlane = Plane3.FromPolygon(moved.Vertices);
            // New side faces (slanted neighbours) take the surface of the side face they grew from.
            faces = BrushOps.ConvexHull(shape).Faces
                .Select(x => IsNew(x, before) && !(Plane3.FromPolygon(x.Vertices) is { } q && movedPlane is { } mp && q.NearlyEquals(mp, 1e-3, 0.05))
                    ? StyleFromNeighbour(x, b.Shape.Faces, face)
                    : x)
                .ToList();
        }

        List<Plane3?> wanted = before.Select((p, i) => i == face ? movedPlane : p).ToList();
        Brush o = b.Clone();
        o.Shape = new BrushShape { Faces = KeepOrder(faces, wanted) };
        return new VertexEditResult(o, 0, 0, ShapeAnalysis.IsConvexSolid(o.Shape));
    }

    /// <summary>
    /// A new brush grown from a face: the face swept <paramref name="distance"/> along its normal (backwards when
    /// negative). It has the source brush's CSG type and flags, sits at the face's centre with no rotation, and
    /// takes the face's material; its far face keeps the face's mapping.
    /// </summary>
    public static Brush NewBrush(Brush b, int face, double distance, string newId)
    {
        BrushFace f = FaceOf(b, face);
        BrushFace wf = b.WorldFaces().ElementAt(face);
        Vec3 n = wf.Normal();
        if (n == Vec3.Zero || Math.Abs(distance) < 1e-9)
        {
            throw new ArgumentException("extrude: the face has no area or the distance is zero");
        }

        List<Vec3> baseRing = wf.Vertices;
        Vec3 dir = n * distance;
        Vec3 origin = Poly.Tidy(Poly.Average(baseRing) + dir / 2);
        List<Vec3> near = baseRing.Select(v => Poly.Tidy(v - origin)).ToList();
        List<Vec3> far = near.Select(v => Poly.Tidy(v + dir)).ToList();
        Vec3 centre = Vec3.Zero;

        var faces = new List<BrushFace>();
        void Add(List<Vec3> ring, BrushFace? mapping)
        {
            if (Vec3.Dot(Poly.Newell(ring), Poly.Average(ring) - centre) < 0)
            {
                ring.Reverse(); // wind outwards
            }

            BrushFace face0 = FaceKit.Make(f.Material, ring);
            if (mapping is not null)
            {
                face0.TextureU = mapping.TextureU;
                face0.TextureV = mapping.TextureV;
                face0.TextureBase = Poly.Tidy(mapping.TextureBase - origin + dir);
                face0.PanU = mapping.PanU;
                face0.PanV = mapping.PanV;
            }

            faces.Add(face0);
        }

        Add(far, wf);
        Add([.. near], null);
        for (int i = 0; i < near.Count; i++)
        {
            int j = (i + 1) % near.Count;
            Add([near[i], near[j], far[j], far[i]], null);
        }

        var nb = new Brush(newId)
        {
            Oper = b.Oper,
            PolyFlags = b.PolyFlags,
            Location = origin,
            Shape = new BrushShape { Faces = faces },
        };
        nb.Editor.Layer = b.Editor.Layer;
        nb.Editor.Folder = b.Editor.Folder;
        return nb;
    }

    /// <summary>The faces ordered so each one lying on <paramref name="wanted"/>[i] comes at index i; slots whose
    /// plane is gone take the new faces in order, and any faces left over follow.</summary>
    private static List<BrushFace> KeepOrder(List<BrushFace> faces, List<Plane3?> wanted)
    {
        var rest = new List<BrushFace>(faces);
        var slots = new BrushFace?[wanted.Count];
        for (int i = 0; i < wanted.Count; i++)
        {
            int hit = wanted[i] is { } pl ? rest.FindIndex(f => Plane3.FromPolygon(f.Vertices) is { } q && q.NearlyEquals(pl, 1e-3, 0.05)) : -1;
            if (hit >= 0)
            {
                slots[i] = rest[hit];
                rest.RemoveAt(hit);
            }
        }

        for (int i = 0; i < slots.Length && rest.Count > 0; i++)
        {
            if (slots[i] is null)
            {
                slots[i] = rest[0];
                rest.RemoveAt(0);
            }
        }

        return [.. slots.OfType<BrushFace>(), .. rest];
    }

    private static bool Matches(Plane3 p, List<Plane3?> planes) => planes.Any(q => q is { } x && x.NearlyEquals(p, 1e-3, 0.05));

    private static bool IsNew(BrushFace f, List<Plane3?> planes) => Plane3.FromPolygon(f.Vertices) is { } p && !Matches(p, planes);

    /// <summary>A new hull face with the material, flags and lightmap scale of the original face it grew from: the one
    /// sharing the most corners with it, the closest in direction on a tie (skipping <paramref name="skip"/>, the
    /// extruded face). Its mapping stays the default.</summary>
    private static BrushFace StyleFromNeighbour(BrushFace face, List<BrushFace> original, int skip)
    {
        Vec3 n = face.Normal();
        BrushFace? best = null;
        double bestScore = 1.5;
        for (int i = 0; i < original.Count; i++)
        {
            if (i == skip)
            {
                continue;
            }

            int shared = original[i].Vertices.Count(v => face.Vertices.Any(w => VertexEdit.Near(v, w)));
            double score = shared + Vec3.Dot(n, original[i].Normal()) * 0.5;
            if (score > bestScore)
            {
                best = original[i];
                bestScore = score;
            }
        }

        if (best is null)
        {
            return face;
        }

        BrushFace o = face.Clone();
        o.Material = best.Material;
        o.Flags = best.Flags;
        o.LightMapScale = best.LightMapScale;
        return o;
    }

    private static BrushFace WithVertices(BrushFace f, List<Vec3> vertices)
    {
        BrushFace c = f.Clone();
        c.Vertices = vertices;
        return c;
    }

    private static BrushFace FaceOf(Brush b, int face) =>
        face >= 0 && face < b.Shape.Faces.Count ? b.Shape.Faces[face] : throw new ArgumentException($"brush {b.Id} has no face {face}");
}
