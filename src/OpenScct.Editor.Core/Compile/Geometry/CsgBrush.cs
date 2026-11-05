using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// A brush ready for the CSG: its faces in world space as outward polygons, its volume as convex pieces (one for a
/// convex brush, the solid leaves of a BSP of its faces for a concave one, none for a sheet), its kind and bounds.
/// </summary>
public sealed class CsgBrush
{
    private CsgBrush(Brush source, int order, ulong fingerprint)
    {
        Source = source;
        Order = order;
        Fingerprint = fingerprint;
    }

    public Brush Source { get; }

    public string Id => Source.Id;

    /// <summary>Place in the CSG order (the project's brush list order).</summary>
    public int Order { get; }

    /// <summary>Hash of everything that shapes the brush's output (transform, faces, flags, CSG operation).</summary>
    public ulong Fingerprint { get; }

    public CsgOper Oper { get; private set; }

    public SurfaceKind Kind { get; private set; }

    public IReadOnlyList<Poly> Faces { get; private set; } = [];

    /// <summary>Convex pieces of the volume, each as outward planes. Empty for sheets.</summary>
    public IReadOnlyList<Plane3[]> Pieces { get; private set; } = [];

    public Aabb Bounds { get; private set; } = Aabb.Empty;

    public bool IsSheet => Pieces.Count == 0;

    public bool IsConvex => Pieces.Count == 1;

    /// <summary>Solid brushes with a volume decide what is empty and what is solid (UE2 add/subtract).</summary>
    public bool ShapesSpace => Kind == SurfaceKind.Solid && !IsSheet;

    /// <summary>Problems found while preparing (degenerate faces, a solid sheet...).</summary>
    public List<string> Problems { get; } = new();

    /// <summary>The fingerprint a brush would have, without preparing it.</summary>
    public static ulong FingerprintOf(Brush b)
    {
        var h = new Hash64();
        h.Add((int)b.Oper);
        h.Add(b.PolyFlags);
        h.Add(b.Location);
        h.Add(b.Rotation);
        h.Add(b.PrePivot);
        h.Add(b.MainScale);
        h.Add(b.PostScale);
        h.Add(b.Shape.Faces.Count);
        foreach (BrushFace f in b.Shape.Faces)
        {
            h.Add(f.Vertices.Count);
            foreach (Vec3 v in f.Vertices)
            {
                h.Add(v);
            }

            h.Add(f.Material);
            h.Add(f.Flags);
            h.Add(f.TextureBase);
            h.Add(f.TextureU);
            h.Add(f.TextureV);
            h.Add(f.PanU);
            h.Add(f.PanV);
            h.Add(f.LightMapScale);
        }

        return h.Value;
    }

    public static CsgBrush Prepare(Brush b, int order) => Prepare(b, order, FingerprintOf(b));

    /// <summary>The same prepared brush at another place in the CSG order (its faces and volume are shared).</summary>
    public CsgBrush WithOrder(Brush source, int order)
    {
        if (order == Order && ReferenceEquals(source, Source))
        {
            return this;
        }

        var copy = new CsgBrush(source, order, Fingerprint)
        {
            Oper = Oper,
            Kind = Kind,
            Faces = Faces,
            Pieces = Pieces,
            Bounds = Bounds,
        };
        copy.Problems.AddRange(Problems);
        return copy;
    }

    public static CsgBrush Prepare(Brush b, int order, ulong fingerprint)
    {
        var cb = new CsgBrush(b, order, fingerprint) { Oper = b.Oper };
        SurfaceKind kind = KindOf(b.PolyFlags);
        if (kind == SurfaceKind.Semisolid && b.Oper == CsgOper.Subtract)
        {
            cb.Problems.Add("a semisolid brush cannot subtract; built as a solid subtract");
            kind = SurfaceKind.Solid;
        }

        var faces = new List<Poly>();
        int index = 0;
        foreach (BrushFace f in b.WorldFaces())
        {
            int flags = f.Flags | b.PolyFlags;
            SurfaceKind faceKind = (flags & PolyFlags.Portal) != 0 ? SurfaceKind.Portal : kind;
            if (!Plane3.TryFromPolygon(f.Vertices, out Plane3 plane))
            {
                cb.Problems.Add($"face {index} is degenerate");
                index++;
                continue;
            }

            var surface = new Surface
            {
                BrushId = b.Id,
                FaceIndex = index,
                Material = f.Material,
                Flags = flags,
                Kind = faceKind,
                TextureBase = f.TextureBase,
                TextureU = TextureAxis(b, b.Shape.Faces[index].TextureU),
                TextureV = TextureAxis(b, b.Shape.Faces[index].TextureV),
                PanU = f.PanU,
                PanV = f.PanV,
                LightMapScale = f.LightMapScale,
            };
            Vec3[]? verts = Poly.Clean(f.Vertices);
            if (verts is null)
            {
                cb.Problems.Add($"face {index} is degenerate");
            }
            else
            {
                faces.Add(new Poly(verts, plane, surface));
            }

            index++;
        }

        cb.Faces = faces;
        cb.Bounds = Aabb.Of(faces.SelectMany(f => f.Verts));
        cb.Pieces = Volume(faces, cb.Problems);
        cb.Kind = kind;
        if (cb.Pieces.Count == 0 && faces.Count > 0 && kind is SurfaceKind.Solid or SurfaceKind.Semisolid)
        {
            // Sheets have no volume: UE2 builds them as non-solid surfaces.
            cb.Kind = SurfaceKind.NonSolid;
        }

        return cb;
    }

    /// <summary>A texture axis in world space. UE2 (FPoly::Transform) moves texture vectors by the inverse transpose of
    /// the point transform, so the texture keeps its place on the brush when the brush is scaled.</summary>
    public static Vec3 TextureAxis(Brush b, Vec3 local) => Divide(b.Rotation.Rotate(Divide(local, b.MainScale)), b.PostScale);

    private static Vec3 Divide(Vec3 v, Vec3 s) => new(Div(v.X, s.X), Div(v.Y, s.Y), Div(v.Z, s.Z));

    private static double Div(double a, double b) => Math.Abs(b) > 1e-12 ? a / b : 0;

    public static SurfaceKind KindOf(int brushFlags) =>
        (brushFlags & PolyFlags.Portal) != 0 ? SurfaceKind.Portal
        : (brushFlags & PolyFlags.NotSolid) != 0 ? SurfaceKind.NonSolid
        : (brushFlags & PolyFlags.Semisolid) != 0 ? SurfaceKind.Semisolid
        : SurfaceKind.Solid;

    /// <summary>The convex pieces of a closed brush: its own planes when it is convex, else the solid leaves of a BSP
    /// of its faces. Empty for a sheet (no volume).</summary>
    private static List<Plane3[]> Volume(List<Poly> faces, List<string> problems)
    {
        if (faces.Count < 4)
        {
            return [];
        }

        var planes = new List<Plane3>();
        foreach (Poly f in faces)
        {
            if (!planes.Exists(p => Vec3.Dot(p.N, f.Plane.N) > 1 - 1e-6 && Math.Abs(p.D - f.Plane.D) < Plane3.Epsilon))
            {
                planes.Add(f.Plane);
            }
        }

        bool convex = true;
        foreach (Plane3 p in planes)
        {
            bool strictlyBehind = false;
            foreach (Poly f in faces)
            {
                foreach (Vec3 v in f.Verts)
                {
                    double d = p.Dist(v);
                    if (d > 0.05)
                    {
                        convex = false;
                    }
                    else if (d < -Plane3.Epsilon)
                    {
                        strictlyBehind = true;
                    }
                }
            }

            if (!strictlyBehind)
            {
                // Every corner lies on this plane: no volume (a sheet or a flat brush).
                return [];
            }
        }

        if (convex)
        {
            return planes.Count >= 4 ? [planes.ToArray()] : [];
        }

        // Concave: decompose through a BSP of the brush's own faces (outside the brush is empty).
        BspTree tree = BspTree.Build(faces, rootEmpty: true);
        List<Plane3[]> pieces = tree.SolidRegions();
        if (pieces.Count == 0)
        {
            problems.Add("the brush is not closed (no inside found)");
        }

        return pieces;
    }
}
