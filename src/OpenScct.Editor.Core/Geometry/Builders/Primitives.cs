using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Builders;

// The UE2 brush builders (Editor.u CubeBuilder, CylinderBuilder, ConeBuilder, SheetBuilder, TetrahedronBuilder,
// VolumetricBuilder) with the stock parameter names. Hollow variants are built as convex wall pieces, one brush each,
// because the CSG wants convex brushes; together they fill the same space as the stock hollow brush.

/// <summary>UE2 cube: Breadth (X) x Width (Y) x Height (Z), centred, optionally hollow or tessellated.</summary>
public sealed class CubeBuilder : BuilderKind
{
    public override string Kind => "cube";

    public override string Summary => "Box (UE2 cube builder); hollow makes six wall brushes";

    public override string Origin => "centre";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("breadth", 256, 1, 262144, "Size along X"),
        BuilderParam.Number("width", 256, 1, 262144, "Size along Y"),
        BuilderParam.Number("height", 256, 1, 262144, "Size along Z"),
        BuilderParam.Number("wallThickness", 16, 1, 65536, "Wall thickness when hollow"),
        BuilderParam.Flag("hollow", false, "Walls only (six brushes)"),
        BuilderParam.Flag("tessellated", false, "Split each side into two triangles"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        var h = new Vec3(a.Num("breadth"), a.Num("width"), a.Num("height")) / 2;
        double t = a.Num("wallThickness");
        if (!a.Flag("hollow") || t >= Math.Min(h.X, Math.Min(h.Y, h.Z))) // walls that meet in the middle: solid
        {
            List<List<Vec3>> box = ConvexSolid.Box(-h, h);
            yield return Solid(a.Flag("tessellated") ? Triangles(box) : box);
            yield break;
        }

        Vec3 i = h - new Vec3(t, t, t);
        yield return BoxPart(new Vec3(-h.X, -h.Y, -h.Z), new Vec3(h.X, h.Y, -i.Z));   // floor
        yield return BoxPart(new Vec3(-h.X, -h.Y, i.Z), new Vec3(h.X, h.Y, h.Z));     // ceiling
        yield return BoxPart(new Vec3(i.X, -h.Y, -i.Z), new Vec3(h.X, h.Y, i.Z));     // +X
        yield return BoxPart(new Vec3(-h.X, -h.Y, -i.Z), new Vec3(-i.X, h.Y, i.Z));   // -X
        yield return BoxPart(new Vec3(-i.X, i.Y, -i.Z), new Vec3(i.X, h.Y, i.Z));     // +Y
        yield return BoxPart(new Vec3(-i.X, -h.Y, -i.Z), new Vec3(i.X, -i.Y, i.Z));   // -Y
    }

    private static List<List<Vec3>> Triangles(List<List<Vec3>> faces) =>
        faces.SelectMany(f => Enumerable.Range(1, f.Count - 2).Select(k => new List<Vec3> { f[0], f[k], f[k + 1] })).ToList();
}

/// <summary>UE2 cylinder: an N-sided prism, centred; hollow makes one wall brush per side.</summary>
public sealed class CylinderBuilder : BuilderKind
{
    public override string Kind => "cylinder";

    public override string Summary => "N-sided cylinder (UE2 cylinder builder); hollow makes a ring of wall brushes";

    public override string Origin => "centre";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("height", 256, 1, 262144, "Height (Z)"),
        BuilderParam.Number("outerRadius", 512, 1, 262144, "Outer radius"),
        BuilderParam.Number("innerRadius", 384, 0, 262144, "Inner radius when hollow"),
        BuilderParam.Int("sides", 8, 3, 64, "Number of sides"),
        BuilderParam.Flag("alignToSide", true, "Radius measured to the middle of a side, so a side faces each axis"),
        BuilderParam.Flag("hollow", false, "Ring of walls (one brush per side)"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        int sides = a.Int("sides");
        double z = a.Num("height") / 2;
        List<(double X, double Y)> outer = Ring.Points(sides, a.Num("outerRadius"), a.Flag("alignToSide"));
        double inner = Math.Min(a.Num("innerRadius"), a.Num("outerRadius") - 1);
        if (!a.Flag("hollow") || inner <= 0)
        {
            yield return Solid(ConvexSolid.Prism(outer, -z, z));
            yield break;
        }

        List<(double X, double Y)> ring = Ring.Points(sides, inner, a.Flag("alignToSide"));
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            yield return Solid(ConvexSolid.Prism([outer[i], outer[j], ring[j], ring[i]], -z, z));
        }
    }
}

/// <summary>UE2 cone: base on z = 0, apex at Height; hollow makes one wall brush per side.</summary>
public sealed class ConeBuilder : BuilderKind
{
    public override string Kind => "cone";

    public override string Summary => "N-sided cone (UE2 cone builder); hollow makes a ring of wall brushes";

    public override string Origin => "centre of the base";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("height", 256, 1, 262144, "Apex height"),
        BuilderParam.Number("capHeight", 128, 0, 262144, "Inner apex height when hollow (below height)"),
        BuilderParam.Number("outerRadius", 512, 1, 262144, "Base radius"),
        BuilderParam.Number("innerRadius", 384, 0, 262144, "Inner base radius when hollow"),
        BuilderParam.Int("sides", 8, 3, 64, "Number of sides"),
        BuilderParam.Flag("alignToSide", true, "Radius measured to the middle of a side"),
        BuilderParam.Flag("hollow", false, "Shell of walls (one brush per side)"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        int sides = a.Int("sides");
        double height = a.Num("height");
        List<(double X, double Y)> outer = Ring.Points(sides, a.Num("outerRadius"), a.Flag("alignToSide"));
        var apex = new Vec3(0, 0, height);
        if (!a.Flag("hollow"))
        {
            yield return Solid(ConvexSolid.Hull(outer.Select(p => new Vec3(p.X, p.Y, 0)).Append(apex)));
            yield break;
        }

        double inner = a.Num("innerRadius"), cap = a.Num("capHeight");
        if (inner >= a.Num("outerRadius") || cap >= height)
        {
            throw new ArgumentException("cone: a hollow cone needs innerRadius below outerRadius and capHeight below height");
        }

        List<(double X, double Y)> ring = Ring.Points(sides, inner, a.Flag("alignToSide"));
        var innerApex = new Vec3(0, 0, cap);
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            Vec3[] pts = [new(outer[i].X, outer[i].Y, 0), new(outer[j].X, outer[j].Y, 0), new(ring[i].X, ring[i].Y, 0), new(ring[j].X, ring[j].Y, 0), apex, innerApex];
            yield return Solid(ConvexSolid.Hull(inner > 0 ? pts : pts.Where((_, k) => k is not (2 or 3))));
        }
    }
}

/// <summary>UE2 sheet: a flat, two-sided, non-solid plane (for glass, decals, fences), centred.</summary>
public sealed class SheetBuilder : BuilderKind
{
    public override CsgOper DefaultOper => CsgOper.Add;

    public override string Kind => "sheet";

    public override string Summary => "Flat two-sided non-solid sheet (UE2 sheet builder)";

    public override string Origin => "centre";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("width", 256, 1, 262144, "Size along the first axis (X, or Y for the x axis sheet)"),
        BuilderParam.Number("height", 256, 1, 262144, "Size along the second axis (Y for horizontal, else Z)"),
        BuilderParam.Int("horizBreaks", 1, 1, 64, "Divisions along the width"),
        BuilderParam.Int("vertBreaks", 1, 1, 64, "Divisions along the height"),
        BuilderParam.Choice("axis", "horizontal", ["horizontal", "x", "y"], "horizontal (faces +Z), x (faces +X) or y (faces +Y)"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double w = a.Num("width"), h = a.Num("height");
        int nu = a.Int("horizBreaks"), nv = a.Int("vertBreaks");
        string axis = a.Text("axis");
        Vec3 P(double s, double t) => axis switch
        {
            "x" => new Vec3(0, s, t),
            "y" => new Vec3(s, 0, t),
            _ => new Vec3(s, t, 0),
        };

        var faces = new List<List<Vec3>>(nu * nv);
        for (int i = 0; i < nu; i++)
        {
            for (int j = 0; j < nv; j++)
            {
                double s0 = -w / 2 + w * i / nu, s1 = -w / 2 + w * (i + 1) / nu;
                double t0 = -h / 2 + h * j / nv, t1 = -h / 2 + h * (j + 1) / nv;
                List<Vec3> q = [P(s0, t0), P(s1, t0), P(s1, t1), P(s0, t1)];
                Vec3 want = axis switch { "x" => Vec3.UnitX, "y" => Vec3.UnitY, _ => Vec3.UnitZ };
                if (Vec3.Dot(Poly.Newell(q), want) < 0)
                {
                    q.Reverse();
                }

                faces.Add(q);
            }
        }

        yield return new Part(faces, CsgOper.Add, PolyFlags.TwoSided | PolyFlags.NotSolid);
    }
}

/// <summary>UE2 tetrahedron builder: an octahedron split <c>sphereExtrapolation - 1</c> times towards a sphere.</summary>
public sealed class TetrahedronBuilder : BuilderKind
{
    public override string Kind => "tetrahedron";

    public override string Summary => "Sphere approximation (UE2 tetrahedron builder: octahedron subdivided)";

    public override string Origin => "centre";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("radius", 256, 1, 262144, "Sphere radius"),
        BuilderParam.Int("sphereExtrapolation", 2, 1, 5, "1 = octahedron, each step splits every triangle in four"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double r = a.Num("radius");
        Vec3[] v = [new(r, 0, 0), new(-r, 0, 0), new(0, r, 0), new(0, -r, 0), new(0, 0, r), new(0, 0, -r)];
        int[][] tris = [[0, 2, 4], [2, 1, 4], [1, 3, 4], [3, 0, 4], [2, 0, 5], [1, 2, 5], [3, 1, 5], [0, 3, 5]];
        var faces = new List<List<Vec3>>();
        foreach (int[] t in tris)
        {
            Subdivide(v[t[0]], v[t[1]], v[t[2]], a.Int("sphereExtrapolation"), r, faces);
        }

        yield return Solid(faces);
    }

    private static void Subdivide(Vec3 a, Vec3 b, Vec3 c, int count, double r, List<List<Vec3>> faces)
    {
        if (count <= 1)
        {
            List<Vec3> tri = [a, b, c];
            if (Vec3.Dot(Poly.Newell(tri), a + b + c) < 0)
            {
                tri.Reverse();
            }

            faces.Add(tri);
            return;
        }

        Vec3 ab = ((a + b) / 2).Normalized() * r, bc = ((b + c) / 2).Normalized() * r, ca = ((c + a) / 2).Normalized() * r;
        Subdivide(a, ab, ca, count - 1, r, faces);
        Subdivide(b, bc, ab, count - 1, r, faces);
        Subdivide(c, ca, bc, count - 1, r, faces);
        Subdivide(ab, bc, ca, count - 1, r, faces);
    }
}

/// <summary>UE2 volumetric: crossed two-sided sheets round the Z axis (light shafts, fog, foliage), centred.</summary>
public sealed class VolumetricBuilder : BuilderKind
{
    public override CsgOper DefaultOper => CsgOper.Add;

    public override string Kind => "volumetric";

    public override string Summary => "Crossed vertical sheets (UE2 volumetric builder)";

    public override string Origin => "centre";

    protected override IEnumerable<BuilderParam> DeclareParams() =>
    [
        BuilderParam.Number("height", 128, 1, 262144, "Height (Z)"),
        BuilderParam.Number("radius", 64, 1, 262144, "Half width of each sheet"),
        BuilderParam.Int("numSheets", 2, 2, 16, "Number of sheets"),
    ];

    protected override IEnumerable<Part> Make(BuilderArgs a)
    {
        double h = a.Num("height") / 2, r = a.Num("radius");
        int n = a.Int("numSheets");
        var faces = new List<List<Vec3>>(n);
        for (int i = 0; i < n; i++)
        {
            double phi = Math.PI * i / n;
            var d = new Vec3(Math.Cos(phi), Math.Sin(phi), 0) * r;
            faces.Add([new Vec3(-d.X, -d.Y, -h), new Vec3(d.X, d.Y, -h), new Vec3(d.X, d.Y, h), new Vec3(-d.X, -d.Y, h)]);
        }

        yield return new Part(faces, CsgOper.Add, PolyFlags.TwoSided | PolyFlags.NotSolid);
    }
}

/// <summary>Points of a regular polygon in the UE2 builders' layout: corner i at angle (2i + ofs) pi / sides from +Y
/// towards +X (x = r sin, y = r cos); aligned to a side, the radius reaches the middle of each side.</summary>
internal static class Ring
{
    public static List<(double X, double Y)> Points(int sides, double radius, bool alignToSide)
    {
        int ofs = alignToSide ? 1 : 0;
        double r = alignToSide ? radius / Math.Cos(Math.PI / sides) : radius;
        var pts = new List<(double X, double Y)>(sides);
        for (int i = 0; i < sides; i++)
        {
            double a = (2 * i + ofs) * Math.PI / sides;
            pts.Add((Poly.Tidy(r * Math.Sin(a)), Poly.Tidy(r * Math.Cos(a))));
        }

        return pts;
    }
}
