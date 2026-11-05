using OpenScct.Core;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;
using OpenScct.Physics.Conveyors;
using L = OpenScct.Game.Svm.ConveyorLayout;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>The pieces of a conveyor's built look (spec/svmap/1.2/conveyors.md).</summary>
public enum ConveyorPart
{
    /// <summary>Static: frame walls and body, treadmill edges, escalator balustrades. Drawn with FrameMaterial.</summary>
    Frame,

    /// <summary>The belt surface (frame, treadmill): its texture slides along the path. BeltMaterial.</summary>
    Belt,

    /// <summary>The escalator's steps, baked at belt phase 0; the game moves them along the path. BeltMaterial.</summary>
    Steps,

    /// <summary>The frame's end drums: texture turns round them. BeltMaterial.</summary>
    Rollers,
}

/// <summary>
/// Triangles in world space (UE axes) with a normal and texture coordinates per vertex. Triangles wind as editor
/// polygons do: the cross product of (b - a) and (c - a) points out of the surface.
/// </summary>
public sealed class ConveyorMesh
{
    public List<Vec3> Positions { get; } = [];

    public List<Vec3> Normals { get; } = [];

    public List<(double U, double V)> Uvs { get; } = [];

    public List<int> Indices { get; } = [];

    public int TriangleCount => Indices.Count / 3;

    /// <summary>A quad a-b-c-d (in order round its edge) facing <paramref name="normal"/>.</summary>
    internal void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 normal, (double, double) ta, (double, double) tb, (double, double) tc,
        (double, double) td)
    {
        int i = Positions.Count;
        Add(a, normal, ta);
        Add(b, normal, tb);
        Add(c, normal, tc);
        Add(d, normal, td);
        Vec3 face = Vec3.Cross(b - a, c - a) + Vec3.Cross(c - a, d - a);
        if (Vec3.Dot(face, normal) >= 0)
        {
            Indices.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
        }
        else
        {
            Indices.AddRange([i, i + 2, i + 1, i, i + 3, i + 2]);
        }
    }

    internal void Triangle(Vec3 a, Vec3 b, Vec3 c, Vec3 normal, (double, double) ta, (double, double) tb, (double, double) tc)
    {
        int i = Positions.Count;
        Add(a, normal, ta);
        Add(b, normal, tb);
        Add(c, normal, tc);
        bool outward = Vec3.Dot(Vec3.Cross(b - a, c - a), normal) >= 0;
        Indices.AddRange(outward ? [i, i + 1, i + 2] : [i, i + 2, i + 1]);
    }

    private void Add(Vec3 p, Vec3 n, (double U, double V) t)
    {
        Positions.Add(p);
        Normals.Add(n);
        Uvs.Add(t);
    }
}

/// <summary>
/// Builds a conveyor's look from its path with <see cref="ConveyorLayout"/>'s sizes: OpenSCCT's map build bakes it into
/// render/world.glb and sim/collision.bin, and the editor previews it. Texture coordinates follow
/// spec/additions/conveyors.md#look, so the game's scrolling matches the belt's speed.
/// </summary>
public sealed class ConveyorGeometry
{
    /// <summary>Longest straight segment between stations (the lighting is baked per vertex).</summary>
    private const double MaxSegment = 64;

    /// <summary>Largest turn of one arc segment, in radians.</summary>
    private const double MaxArcStep = Math.PI / 12;

    private const int DrumSegments = 8;

    private readonly Dictionary<ConveyorPart, ConveyorMesh> _parts = new();

    private ConveyorGeometry()
    {
    }

    /// <summary>The parts this conveyor has (a driver conveyor has none).</summary>
    public IReadOnlyDictionary<ConveyorPart, ConveyorMesh> Parts => _parts;

    /// <summary>The escalator's collision slope, which is not drawn (its steps are).</summary>
    public ConveyorMesh? Slope { get; private set; }

    /// <param name="joins">
    /// Where other conveyors meet this one's sides (<see cref="ConveyorJoins.Find"/>): a <c>CW_Auto</c> wall opens there.
    /// </param>
    public static ConveyorGeometry Build(Conveyor c, IReadOnlyList<ConveyorJoin>? joins = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var g = new ConveyorGeometry();
        ConveyorPath path = c.BuildWorldPath();
        if (c.Look == EConveyorLook.CL_Driver || path.Length <= 0f || c.Width <= 0f)
        {
            return g;
        }

        IReadOnlyList<ConveyorJoin>? openings = c.Walls == EConveyorWalls.CW_Auto ? joins : null;
        List<Station> stations = Stations(path, openings);
        double half = c.Width * 0.5;
        if (c.Look == EConveyorLook.CL_Escalator)
        {
            g.Slope = new ConveyorMesh();
            Surface(g.Slope, stations, half, L.BeltTile);
            g._parts[ConveyorPart.Steps] = StepsAt(c, path, 0);
        }
        else
        {
            Surface(g.Part(ConveyorPart.Belt), stations, half, L.BeltTile);
        }

        double wall = c.Look switch
        {
            EConveyorLook.CL_Treadmill => L.EdgeHeight,
            EConveyorLook.CL_Escalator => L.BalustradeHeight,
            _ => L.WallHeight,
        };
        g.Body(c, stations, half, wall, openings);
        if (!path.IsLoop)
        {
            g.Ends(c, stations, half, wall);
        }

        return g;
    }

    private ConveyorMesh Part(ConveyorPart part)
    {
        if (!_parts.TryGetValue(part, out ConveyorMesh? mesh))
        {
            mesh = new ConveyorMesh();
            _parts[part] = mesh;
        }

        return mesh;
    }

    /// <summary>
    /// A cross-section of the path: its distance along it, centre point, sideways vector (flat, pointing right of the
    /// direction of travel, lengthened at a sharp corner so both runs keep their width) and the surface's up.
    /// </summary>
    private readonly record struct Station(double S, Vec3 P, Vec3 Right, Vec3 Out, Vec3 Up);

    /// <summary>Cross-sections along the path; the ends of wall openings are cross-sections too, so walls stop there.</summary>
    private static List<Station> Stations(ConveyorPath path, IReadOnlyList<ConveyorJoin>? openings)
    {
        var along = new List<double> { 0 };
        double at = 0;
        foreach (ConveyorPieceInfo piece in path.Pieces())
        {
            int n = Math.Max(1, (int)Math.Ceiling(piece.Length / MaxSegment));
            if (piece.IsArc)
            {
                n = Math.Max(n, (int)Math.Ceiling(Math.Abs(piece.Sweep) / MaxArcStep));
            }

            for (int k = 1; k <= n; k++)
            {
                along.Add(at + piece.Length * k / n);
            }

            at += piece.Length;
        }

        foreach (ConveyorJoin j in openings ?? [])
        {
            along.Add(j.From);
            along.Add(j.To);
        }

        along.Sort();
        along = along.Where((s, i) => i == 0 || s - along[i - 1] > 0.5).ToList();

        const float Eps = 0.05f;
        var stations = new List<Station>(along.Count);
        foreach (double s in along)
        {
            path.Evaluate((float)s, out FVector p, out _);
            path.Evaluate((float)s - Eps, out _, out FVector tIn);
            path.Evaluate((float)s + Eps, out _, out FVector tOut);
            Vec3 before = ConveyorDesign.V(tIn), after = ConveyorDesign.V(tOut);
            Vec3 rIn = RightOf(before), rOut = RightOf(after);
            Vec3 r = (rIn + rOut).LengthSquared > 1e-6 ? (rIn + rOut).Normalized() : rOut;
            double miter = 1 / Math.Max(0.25, Vec3.Dot(r, rOut));
            Vec3 t = (before + after).LengthSquared > 1e-6 ? (before + after).Normalized() : after;
            Vec3 up = Vec3.Cross(t, r).Normalized();
            stations.Add(new Station(s, ConveyorDesign.V(p), r * miter, Flat(t), up.Z < 0 ? -up : up));
        }

        return stations;
    }

    /// <summary>The flat right-hand side of a direction of travel (+Y of +X in UE's frame).</summary>
    private static Vec3 RightOf(Vec3 t) => new Vec3(-t.Y, t.X, 0) is var r && r.LengthSquared > 1e-12 ? r.Normalized() : Vec3.UnitY;

    private static Vec3 Flat(Vec3 v) => new Vec3(v.X, v.Y, 0) is var f && f.LengthSquared > 1e-12 ? f.Normalized() : Vec3.UnitX;

    private static Vec3 Z(double z) => new(0, 0, z);

    /// <summary>The riding surface: V runs along the path, one repeat per <paramref name="tile"/>.</summary>
    private static void Surface(ConveyorMesh mesh, List<Station> st, double half, double tile)
    {
        double u = 2 * half / tile;
        for (int i = 0; i + 1 < st.Count; i++)
        {
            Station a = st[i], b = st[i + 1];
            double va = a.S / tile, vb = b.S / tile;
            mesh.Quad(a.P - a.Right * half, a.P + a.Right * half, b.P + b.Right * half, b.P - b.Right * half, (a.Up + b.Up).Normalized(),
                (0, va), (u, va), (u, vb), (0, vb));
        }
    }

    /// <summary>
    /// The escalator's steps at belt <paramref name="phase"/> (<see cref="ConveyorSteps"/>, the game's own placement):
    /// the map build bakes them at phase 0, and the views move them while the conveyor tool previews the belts. Pawns
    /// walk on the slope through the steps' inner corners (<see cref="Slope"/>), which stays under the steps wherever
    /// they are.
    /// </summary>
    public static ConveyorMesh StepsAt(Conveyor c, ConveyorPath path, double phase)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(path);
        var mesh = new ConveyorMesh();
        var faces = new List<ConveyorStepFace>();
        ConveyorSteps.Build(path, c.HalfWidth, phase, faces);
        double u = c.Width / L.BeltTile;
        foreach (ConveyorStepFace f in faces)
        {
            mesh.Quad(ConveyorDesign.V(f.A), ConveyorDesign.V(f.B), ConveyorDesign.V(f.C), ConveyorDesign.V(f.D), ConveyorDesign.V(f.Normal),
                (0, f.V0), (u, f.V0), (u, f.V1), (0, f.V1));
        }

        return mesh;
    }

    private static bool HasWall(EConveyorWalls walls, int side) => walls switch
    {
        EConveyorWalls.CW_Both or EConveyorWalls.CW_Auto => true,
        EConveyorWalls.CW_Left => side < 0,
        EConveyorWalls.CW_Right => side > 0,
        _ => false,
    };

    /// <summary>Side walls (or plain sides) and the underside, along the whole path; a wall stops at an opening.</summary>
    private void Body(Conveyor c, List<Station> st, double half, double wall, IReadOnlyList<ConveyorJoin>? openings)
    {
        ConveyorMesh mesh = Part(ConveyorPart.Frame);
        double depth = L.FrameDepth, thick = L.WallThickness, tile = L.BeltTile;
        // Escalator balustrades reach down to the underside, closing the sides of the steps.
        double innerBottom = c.Look == EConveyorLook.CL_Escalator ? -depth : 0;
        bool Walled(int side, int segment) => HasWall(c.Walls, side) && !ConveyorJoins.Opens(openings, side, st[segment].S, st[segment + 1].S);
        for (int i = 0; i + 1 < st.Count; i++)
        {
            Station a = st[i], b = st[i + 1];
            double ua = a.S / tile, ub = b.S / tile;
            double extentLeft = half, extentRight = half;
            foreach (int side in (int[])[-1, 1])
            {
                Vec3 outward = Flat(a.Right + b.Right) * side;
                Vec3 ea = a.P + a.Right * (half * side), eb = b.P + b.Right * (half * side);
                if (!Walled(side, i))
                {
                    mesh.Quad(ea + Z(-depth), eb + Z(-depth), eb, ea, outward, (ua, -depth / tile), (ub, -depth / tile), (ub, 0), (ua, 0));
                    continue;
                }

                // Close the wall's end where an opening starts or ends beside it.
                if (i > 0 && !Walled(side, i - 1))
                {
                    WallEnd(mesh, a, side, half, innerBottom, wall, -a.Out);
                }

                if (i + 2 < st.Count && !Walled(side, i + 1))
                {
                    WallEnd(mesh, b, side, half, innerBottom, wall, b.Out);
                }

                Vec3 oa = a.P + a.Right * ((half + thick) * side), ob = b.P + b.Right * ((half + thick) * side);
                mesh.Quad(ea + Z(innerBottom), eb + Z(innerBottom), eb + Z(wall), ea + Z(wall), -outward, (ua, innerBottom / tile),
                    (ub, innerBottom / tile), (ub, wall / tile), (ua, wall / tile));
                mesh.Quad(ea + Z(wall), eb + Z(wall), ob + Z(wall), oa + Z(wall), Vec3.UnitZ, (ua, 0), (ub, 0), (ub, thick / tile),
                    (ua, thick / tile));
                mesh.Quad(oa + Z(-depth), ob + Z(-depth), ob + Z(wall), oa + Z(wall), outward, (ua, -depth / tile), (ub, -depth / tile),
                    (ub, wall / tile), (ua, wall / tile));
                if (side < 0)
                {
                    extentLeft = half + thick;
                }
                else
                {
                    extentRight = half + thick;
                }
            }

            Vec3 la = a.P - a.Right * extentLeft + Z(-depth), lb = b.P - b.Right * extentLeft + Z(-depth);
            Vec3 ra = a.P + a.Right * extentRight + Z(-depth), rb = b.P + b.Right * extentRight + Z(-depth);
            mesh.Quad(la, ra, rb, lb, -Vec3.UnitZ, (ua, 0), (ua, (extentLeft + extentRight) / tile), (ub, (extentLeft + extentRight) / tile),
                (ub, 0));
        }
    }

    /// <summary>The cut end of a wall at <paramref name="s"/>, facing <paramref name="facing"/>.</summary>
    private static void WallEnd(ConveyorMesh mesh, Station s, int side, double half, double bottom, double wall, Vec3 facing)
    {
        double thick = L.WallThickness, tile = L.BeltTile;
        Vec3 e = s.P + s.Right * (half * side), o = s.P + s.Right * ((half + thick) * side);
        mesh.Quad(e + Z(bottom), o + Z(bottom), o + Z(wall), e + Z(wall), facing, (0, bottom / tile), (thick / tile, bottom / tile),
            (thick / tile, wall / tile), (0, wall / tile));
    }

    /// <summary>The two ends of an open path: drums under a frame's belt, a plain end elsewhere, and the walls' ends.</summary>
    private void Ends(Conveyor c, List<Station> st, double half, double wall)
    {
        ConveyorMesh frame = Part(ConveyorPart.Frame);
        double depth = L.FrameDepth, thick = L.WallThickness, tile = L.BeltTile;
        foreach ((Station s, double sign) in new[] { (st[0], -1.0), (st[^1], 1.0) })
        {
            Vec3 outward = s.Out * sign;
            if (c.Look == EConveyorLook.CL_Frame)
            {
                Drum(s, outward, half, start: sign < 0);
            }
            else
            {
                frame.Quad(s.P - s.Right * half + Z(-depth), s.P + s.Right * half + Z(-depth), s.P + s.Right * half, s.P - s.Right * half,
                    outward, (0, -depth / tile), (2 * half / tile, -depth / tile), (2 * half / tile, 0), (0, 0));
            }

            foreach (int side in (int[])[-1, 1])
            {
                if (!HasWall(c.Walls, side))
                {
                    continue;
                }

                Vec3 e = s.P + s.Right * (half * side), o = s.P + s.Right * ((half + thick) * side);
                frame.Quad(e + Z(-depth), o + Z(-depth), o + Z(wall), e + Z(wall), outward, (0, -depth / tile), (thick / tile, -depth / tile),
                    (thick / tile, wall / tile), (0, wall / tile));
            }
        }
    }

    /// <summary>
    /// Half a drum round which the belt turns at an open end: top at the belt, bottom at the frame's underside. U runs
    /// the way the belt moves round it (over the top and down at the end, up from the bottom at the
    /// <paramref name="start"/>), so the rollers' one scroll turns both drums with the belt.
    /// </summary>
    private void Drum(Station s, Vec3 outward, double half, bool start)
    {
        ConveyorMesh mesh = Part(ConveyorPart.Rollers);
        double r = L.RollerRadius;
        Vec3 centre = s.P + Z(-r);
        Vec3 right = s.Right.Normalized();
        double v = 2 * half / L.BeltTile;
        Vec3 Rim(int k)
        {
            double a = Math.PI * k / DrumSegments;
            return Vec3.UnitZ * (r * Math.Cos(a)) + outward * (r * Math.Sin(a));
        }

        for (int k = 0; k < DrumSegments; k++)
        {
            Vec3 r0 = Rim(k), r1 = Rim(k + 1);
            int k0 = start ? DrumSegments - k : k, k1 = start ? k0 - 1 : k0 + 1;
            double u0 = 0.5 * k0 / DrumSegments, u1 = 0.5 * k1 / DrumSegments;
            Vec3 normal = (r0 + r1).Normalized();
            mesh.Quad(centre - right * half + r0, centre + right * half + r0, centre + right * half + r1, centre - right * half + r1, normal,
                (u0, 0), (u0, v), (u1, v), (u1, 0));
            foreach (int side in (int[])[-1, 1])
            {
                Vec3 hub = centre + right * (half * side);
                mesh.Triangle(hub, hub + r0, hub + r1, right * side, (0.25, 0), (u0, 0), (u1, 0));
            }
        }
    }
}
