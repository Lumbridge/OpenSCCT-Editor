using System.Numerics;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Interop.Original;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>SCCT UStaticMesh render buffers, editable source triangles and native collision trees.</summary>
internal static class UStaticMeshWriter
{
    private readonly record struct Vertex(Vector3 P, Vector3 N, Vector2 Uv);
    private readonly record struct Triangle(int A, int B, int C, int Material);
    private readonly record struct Section(int First, int StartVertex, int LastVertex, int Faces);
    private sealed class Geometry
    {
        public List<Vertex> Vertices { get; } = [];
        public List<Triangle> Triangles { get; } = [];
        public List<Section> Sections { get; } = [];
    }
    private sealed record Node(int Triangle, int Coplanar = -1, int Back = -1, int Front = -1);

    public static Action<UWriter> Prepare(IReadOnlyList<MeshSection> render, IReadOnlyList<MeshSection>? collision, int[] materials)
    {
        Geometry mesh = Convert(render);
        if (mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0) throw new InvalidDataException("The static mesh contains no triangles.");
        Geometry? hull = collision is { Count: > 0 } ? Convert(collision) : null;
        Geometry solid = hull ?? mesh;
        if (solid.Triangles.Count > AseFile.CollisionTriangleCap)
            throw new InvalidDataException($"The mesh has {solid.Triangles.Count} collision triangles; the original game supports at most {AseFile.CollisionTriangleCap}. Simplify its collision before exporting.");
        List<Vector4> planes = solid.Triangles.Select(t => Plane(solid, t)).ToList();
        List<Node> tree = Tree(solid, planes);
        Vector3 min = mesh.Vertices.Select(v => v.P).Aggregate(Vector3.Min), max = mesh.Vertices.Select(v => v.P).Aggregate(Vector3.Max);
        Vector3 centre = (min + max) / 2;
        float radius = mesh.Vertices.Max(v => Vector3.Distance(v.P, centre));
        return w =>
        {
            // Materials is an array of tagged structs. Write its native tags even without generated class data.
            void MaterialArray(UWriter body)
            {
                body.Compact(materials.Length);
                foreach (int material in materials)
                {
                    body.Name("EnableCollision"); body.U8(0x83);
                    body.Name("Material"); body.U8(0x75); body.I32(UWriter.CompactSize(material)); body.ObjRef(material);
                    body.Name("None");
                }
            }
            var scratch = new UWriter(w.Package); MaterialArray(scratch);
            w.Name("Materials"); w.U8(0x79); w.I32(scratch.Pos - scratch.NameExtra); MaterialArray(w); w.Name("None");
            w.Box(min, max, true); w.Vector4(new Vector4(centre, radius));
            w.Compact(mesh.Sections.Count);
            foreach (Section section in mesh.Sections)
            {
                w.I32(0); w.U16((ushort)section.First); w.U16((ushort)section.StartVertex);
                w.U16((ushort)section.LastVertex); w.U16((ushort)section.Faces); w.U16((ushort)section.Faces);
            }
            Vertices(w, mesh);
            // Native default colour/alpha streams are empty; lighting instances are built in RE+.
            w.Compact(0); w.I32(0); w.Compact(0); w.I32(0);
            w.Compact(1); w.Compact(mesh.Vertices.Count);
            foreach (Vertex v in mesh.Vertices) { w.F32(v.Uv.X); w.F32(v.Uv.Y); }
            w.I32(0); w.I32(0);
            Indices(w, mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray());
            Indices(w, Edges(mesh));
            Collision(w, hull is null ? mesh : null, hull is null ? planes : [], hull is null ? tree : []);
            if (hull is not null) Vertices(w, hull); else { w.Compact(0); w.I32(0); }
            Collision(w, hull, hull is not null ? planes : [], hull is not null ? tree : []);
            Raw(w, hull); Indices(w, hull is null ? [] : Edges(hull)); Raw(w, mesh);
            // Native build revision used by the shipped SCCT static meshes.
            w.I32(8); w.I32(0);
        };
    }

    private static Geometry Convert(IReadOnlyList<MeshSection> sections)
    {
        var g = new Geometry();
        foreach ((MeshSection section, int material) in sections.Select((s, i) => (s, i)))
        {
            int first = g.Triangles.Count * 3, start = g.Vertices.Count;
            if (section.Positions.Length % 3 != 0 || section.Indices.Length % 3 != 0)
                throw new InvalidDataException("The mesh has an incomplete vertex or triangle.");
            int count = section.Positions.Length / 3;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = new(section.Positions[i * 3] * 100, section.Positions[i * 3 + 2] * 100, section.Positions[i * 3 + 1] * 100);
                Vector3 n = section.Normals.Length >= i * 3 + 3 ? new(section.Normals[i * 3], section.Normals[i * 3 + 2], section.Normals[i * 3 + 1]) : Vector3.UnitZ;
                Vector2 uv = section.Uvs.Length >= i * 2 + 2 ? new(section.Uvs[i * 2], section.Uvs[i * 2 + 1]) : Vector2.Zero;
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || !float.IsFinite(n.X) || !float.IsFinite(n.Y) || !float.IsFinite(n.Z) || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                    throw new InvalidDataException("The mesh contains non-finite coordinates.");
                g.Vertices.Add(new(p, n, uv));
            }
            for (int i = 0; i < section.Indices.Length; i += 3)
            {
                int a = section.Indices[i], b = section.Indices[i + 2], c = section.Indices[i + 1];
                if (a < 0 || b < 0 || c < 0 || a >= count || b >= count || c >= count) throw new InvalidDataException("The mesh has an invalid triangle index.");
                if (Vector3.Cross(g.Vertices[start + b].P - g.Vertices[start + a].P, g.Vertices[start + c].P - g.Vertices[start + a].P).LengthSquared() < 1e-12f)
                    throw new InvalidDataException("The mesh contains a collapsed collision triangle. Remove degenerate faces before exporting.");
                g.Triangles.Add(new(start + a, start + b, start + c, material));
            }
            if (g.Vertices.Count > ushort.MaxValue || first > ushort.MaxValue || section.Indices.Length / 3 > ushort.MaxValue)
                throw new InvalidDataException("The mesh exceeds the original engine's 16-bit vertex/section limits. Split it into smaller meshes.");
            g.Sections.Add(new(first, start, g.Vertices.Count - 1, section.Indices.Length / 3));
        }
        return g;
    }

    private static void Vertices(UWriter w, Geometry g)
    {
        w.Compact(g.Vertices.Count);
        foreach (Vertex v in g.Vertices) { w.Vector(v.P); w.Vector(v.N); }
        w.I32(0);
    }
    private static void Indices(UWriter w, int[] indices)
    {
        w.Compact(indices.Length); foreach (int i in indices) w.U16((ushort)i); w.I32(0);
    }
    private static int[] Edges(Geometry g) => g.Triangles.SelectMany(t => new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
        .Select(e => e.Item1 < e.Item2 ? e : (e.Item2, e.Item1)).Distinct().SelectMany(e => new[] { e.Item1, e.Item2 }).ToArray();
    private static Vector4 Plane(Geometry g, Triangle t)
    {
        Vector3 a = g.Vertices[t.A].P, b = g.Vertices[t.B].P, c = g.Vertices[t.C].P;
        Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        return new(normal, Vector3.Dot(normal, a));
    }
    private static void Collision(UWriter w, Geometry? g, List<Vector4> planes, List<Node> tree)
    {
        w.Compact(planes.Count); foreach (Vector4 plane in planes) w.Vector4(plane);
        w.Compact(g?.Triangles.Count ?? 0);
        foreach (Triangle t in g?.Triangles ?? []) { w.U16((ushort)t.A); w.U16((ushort)t.B); w.U16((ushort)t.C); w.U16((ushort)t.Material); }
        w.Compact(tree.Count);
        foreach (Node n in tree) { w.Compact(n.Triangle); w.Compact(n.Coplanar); w.Compact(n.Back); w.Compact(n.Front); w.U32(0x0000fffe); }
    }
    private static void Raw(UWriter w, Geometry? g)
    {
        int head = w.Pos; w.I32(0); w.Compact(g?.Triangles.Count ?? 0);
        foreach (Triangle t in g?.Triangles ?? [])
        {
            int[] corners = [t.A, t.B, t.C];
            foreach (int i in corners) w.Vector(g!.Vertices[i].P);
            w.I32(1); foreach (int i in corners) { w.F32(g!.Vertices[i].Uv.X); w.F32(g.Vertices[i].Uv.Y); }
            foreach (int _ in corners) w.U32(uint.MaxValue);
            w.I32(t.Material); w.U32(1);
        }
        w.PatchI32(head, w.Pos);
    }

    private static List<Node> Tree(Geometry g, List<Vector4> planes)
    {
        var nodes = new List<Node>();
        float Distance(int triangle, Vector4 plane, int corner)
        {
            Triangle t = g.Triangles[triangle]; Vector3 p = g.Vertices[corner == 0 ? t.A : corner == 1 ? t.B : t.C].P;
            return Vector3.Dot(new(plane.X, plane.Y, plane.Z), p) - plane.W;
        }
        (bool Front, bool Back) Side(int t, Vector4 p) => (Enumerable.Range(0, 3).Any(c => Distance(t, p, c) > .01f), Enumerable.Range(0, 3).Any(c => Distance(t, p, c) < -.01f));
        int Build(List<int> tris, int depth)
        {
            if (tris.Count == 0) return -1;
            if (depth > 128 || nodes.Count + tris.Count > 32767) throw new InvalidDataException("The mesh collision tree exceeds the original game's limits. Simplify its collision.");
            int selected = tris[0], best = int.MaxValue;
            foreach (int candidate in tris.Where((_, i) => i % Math.Max(1, tris.Count / 16) == 0).Take(16))
            {
                int front = 0, back = 0, split = 0;
                foreach (int t in tris) { var side = Side(t, planes[candidate]); if (side.Front) front++; if (side.Back) back++; if (side.Front && side.Back) split++; }
                int score = Math.Abs(front - back) + 4 * split;
                if (score < best) { selected = candidate; best = score; }
            }
            int index = nodes.Count; nodes.Add(new(selected));
            var ahead = new List<int>(); var behind = new List<int>(); int coplanar = index;
            foreach (int t in tris.Where(t => t != selected))
            {
                var side = Side(t, planes[selected]);
                if (side.Front) ahead.Add(t); if (side.Back) behind.Add(t);
                if (!side.Front && !side.Back) { nodes[coplanar] = nodes[coplanar] with { Coplanar = nodes.Count }; coplanar = nodes.Count; nodes.Add(new(t)); }
            }
            int b = Build(behind, depth + 1), f = Build(ahead, depth + 1);
            nodes[index] = nodes[index] with { Back = b, Front = f };
            return index;
        }
        Build(Enumerable.Range(0, g.Triangles.Count).ToList(), 0);
        return nodes;
    }
}
