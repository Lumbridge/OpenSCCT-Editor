using System.Globalization;
using System.Text;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>A static mesh as ASE text, with what it holds.</summary>
/// <param name="SharedNames">Material names two different materials of the mesh share: the importer finds a material
/// by its name alone, so those slots all get one of them.</param>
public sealed record AseMesh(string Text, int Triangles, int CollisionTriangles, int Hulls, IReadOnlyList<string> SharedNames);

/// <summary>
/// Static meshes as ASCII Scene Export text, the way the stock editor's own ASE exporter writes them and its
/// <c>STATICMESH IMPORT</c> reads them back: one Multi/Sub-Object material whose
/// sub-materials are named by the leaf name of each material (the importer finds each by that name among the loaded
/// packages), the geometry in 3ds Max axes, and an <c>MCDCX_</c> object per convex collision hull.
/// </summary>
public static class AseFile
{
    /// <summary>The game marks each collision triangle a trace tests in one table of this many slots, for hulls and
    /// per-polygon sets alike, and never checks the index: a mesh with more crashes the original game.</summary>
    public const int CollisionTriangleCap = 5512;

    /// <summary>Corners whose normals stray this far from their face's (cosine) make the face part of a smooth
    /// surface (a smoothing group); the others are drawn flat.</summary>
    private const double SmoothCosine = 0.985;

    /// <summary>ASE smoothing groups are bits 1 to 32.</summary>
    private const int SmoothingGroups = 32;

    private readonly record struct Corner(Vec3 Position, double U, double V, Vec3 Normal);

    private readonly record struct Face(Corner A, Corner B, Corner C, int Slot, int Smoothing);

    /// <summary>The ASE of a mesh read from a glb (glTF space: metres, Y up, counter-clockwise fronts).</summary>
    /// <param name="collision">The mesh's collision mesh (its convex hulls, one connected piece each), or null for a
    /// mesh that collides with its own triangles.</param>
    public static AseMesh Write(string name, IReadOnlyList<MeshSection> render, IReadOnlyList<MeshSection>? collision)
    {
        // One slot per material; the slot is named by the material's leaf name, which the importer looks up.
        var materials = new List<string>();
        var faces = new List<Face>();
        foreach (MeshSection s in render)
        {
            string material = s.Material ?? "None";
            int slot = materials.FindIndex(m => m.Equals(material, StringComparison.OrdinalIgnoreCase));
            if (slot < 0)
            {
                materials.Add(material);
                slot = materials.Count - 1;
            }

            for (int t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                Corner a = CornerOf(s, s.Indices[t]), b = CornerOf(s, s.Indices[t + 1]), c = CornerOf(s, s.Indices[t + 2]);
                // The axis swap mirrors: in Unreal axes a glTF front is (c - a) x (b - a).
                Vec3 n = Vec3.Cross(c.Position - a.Position, b.Position - a.Position);
                double len = n.Length;
                bool smooth = len > 0 && new[] { a, b, c }.Any(k => k.Normal.Length > 0 && Vec3.Dot(k.Normal, n / len) / k.Normal.Length < SmoothCosine);
                faces.Add(new Face(a, b, c, slot, smooth ? 1 : 0));
            }
        }

        faces = SmoothingOf(faces);
        List<string> shared = materials.GroupBy(m => AssetPaths.Split(m).Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        var sb = new StringBuilder();
        Line(sb, "*3DSMAX_ASCIIEXPORT\t200");
        Line(sb, $"*COMMENT \"OpenSCCT Editor: {name}\"");
        Line(sb, "*MATERIAL_LIST {");
        Line(sb, "\t*MATERIAL_COUNT 1");
        Line(sb, "\t*MATERIAL 0 {");
        Line(sb, $"\t\t*MATERIAL_NAME \"{name}\"");
        Line(sb, "\t\t*MATERIAL_CLASS \"Multi/Sub-Object\"");
        Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t*NUMSUBMTLS {materials.Count}"));
        for (int i = 0; i < materials.Count; i++)
        {
            string leaf = AssetPaths.Split(materials[i]).Name;
            Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t*SUBMATERIAL {i} {{"));
            Line(sb, $"\t\t\t*MATERIAL_NAME \"{leaf}\"");
            Line(sb, "\t\t\t*MATERIAL_CLASS \"Standard\"");
            Line(sb, "\t\t\t*MATERIAL_DIFFUSE 1.0000\t1.0000\t1.0000");
            Line(sb, "\t\t\t*MAP_DIFFUSE {");
            Line(sb, $"\t\t\t\t*MAP_NAME \"{leaf}\"");
            Line(sb, "\t\t\t\t*MAP_CLASS \"Bitmap\"");
            Line(sb, $"\t\t\t\t*BITMAP \"{leaf}.tga\"");
            Line(sb, "\t\t\t\t*UVW_U_TILING 1.0000");
            Line(sb, "\t\t\t\t*UVW_V_TILING 1.0000");
            Line(sb, "\t\t\t}");
            Line(sb, "\t\t}");
        }

        Line(sb, "\t}");
        Line(sb, "}");
        GeomObject(sb, name, faces);

        int hulls = 0, hullTriangles = 0;
        foreach (List<Face> hull in Hulls(collision ?? []))
        {
            // The importer turns MCD objects half a turn about Z against the rest of the mesh (stock meshes export
            // their hulls turned so), so they are written turned the other way.
            List<Face> turned = hull.Select(f => f with { A = Turn(f.A), B = Turn(f.B), C = Turn(f.C), Slot = 0, Smoothing = 0 }).ToList();
            GeomObject(sb, string.Create(CultureInfo.InvariantCulture, $"MCDCX_{name}_{hulls:00}"), turned);
            hulls++;
            hullTriangles += hull.Count;
        }

        return new AseMesh(sb.ToString(), faces.Count, hulls > 0 ? hullTriangles : faces.Count, hulls, shared);
    }

    /// <summary>
    /// Smoothing groups for the smooth faces: faces that share a corner with the same normal are one smooth surface;
    /// surfaces that meet at a corner with different normals (a crease) get different groups, since the importer
    /// merges corners by place and averages the normals of faces of one group. Flat faces stay in no group.
    /// </summary>
    private static List<Face> SmoothingOf(List<Face> faces)
    {
        int[] parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        var byCorner = new Dictionary<(long, long, long, long, long, long), int>();
        var byPlace = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < faces.Count; i++)
        {
            if (faces[i].Smoothing == 0)
            {
                continue;
            }

            foreach (Corner k in new[] { faces[i].A, faces[i].B, faces[i].C })
            {
                Vec3 n = k.Normal.Length > 0 ? k.Normal / k.Normal.Length : Vec3.Zero;
                var corner = (Key(k.Position.X), Key(k.Position.Y), Key(k.Position.Z), (long)Math.Round(n.X * 1000), (long)Math.Round(n.Y * 1000), (long)Math.Round(n.Z * 1000));
                if (byCorner.TryGetValue(corner, out int other))
                {
                    parent[Find(i)] = Find(other);
                }
                else
                {
                    byCorner[corner] = i;
                }

                var place = (corner.Item1, corner.Item2, corner.Item3);
                if (!byPlace.TryGetValue(place, out List<int>? list))
                {
                    byPlace[place] = list = [];
                }

                list.Add(i);
            }
        }

        // Surfaces that touch, by surface; then the fewest groups so no two touching surfaces share one.
        var touching = new Dictionary<int, HashSet<int>>();
        foreach (List<int> list in byPlace.Values)
        {
            foreach (int a in list)
            {
                foreach (int b in list)
                {
                    int ra = Find(a), rb = Find(b);
                    if (ra != rb)
                    {
                        (touching.TryGetValue(ra, out HashSet<int>? set) ? set : touching[ra] = []).Add(rb);
                    }
                }
            }
        }

        var group = new Dictionary<int, int>();
        var result = new List<Face>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            if (faces[i].Smoothing == 0)
            {
                result.Add(faces[i]);
                continue;
            }

            int root = Find(i);
            if (!group.TryGetValue(root, out int g))
            {
                var taken = new HashSet<int>((touching.GetValueOrDefault(root) ?? []).Where(group.ContainsKey).Select(r => group[r]));
                g = Enumerable.Range(1, SmoothingGroups).FirstOrDefault(n => !taken.Contains(n), 1);
                group[root] = g;
            }

            result.Add(faces[i] with { Smoothing = g });
        }

        return result;
    }

    /// <summary>A corner in Unreal units and axes (glTF (x, y, z) metres is Unreal (x, z, y) / 100).</summary>
    private static Corner CornerOf(MeshSection s, int i)
    {
        var p = new Vec3(s.Positions[3 * i] * 100.0, s.Positions[3 * i + 2] * 100.0, s.Positions[3 * i + 1] * 100.0);
        var n = s.Normals.Length >= 3 * i + 3 ? new Vec3(s.Normals[3 * i], s.Normals[3 * i + 2], s.Normals[3 * i + 1]) : Vec3.Zero;
        double u = s.Uvs.Length >= 2 * i + 2 ? s.Uvs[2 * i] : 0, v = s.Uvs.Length >= 2 * i + 2 ? s.Uvs[2 * i + 1] : 0;
        return new Corner(p, u, v, n);
    }

    private static Corner Turn(Corner c) => c with { Position = new Vec3(-c.Position.X, -c.Position.Y, c.Position.Z) };

    /// <summary>The collision mesh's hulls: its connected pieces (each hull has corners of its own).</summary>
    private static IEnumerable<List<Face>> Hulls(IReadOnlyList<MeshSection> collision)
    {
        foreach (MeshSection s in collision)
        {
            int count = s.Positions.Length / 3;
            int[] parent = Enumerable.Range(0, count).ToArray();
            int Find(int x)
            {
                while (parent[x] != x)
                {
                    x = parent[x] = parent[parent[x]];
                }

                return x;
            }

            for (int t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                parent[Find(s.Indices[t + 1])] = Find(s.Indices[t]);
                parent[Find(s.Indices[t + 2])] = Find(s.Indices[t]);
            }

            var pieces = new SortedDictionary<int, List<Face>>();
            for (int t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                int root = Find(s.Indices[t]);
                if (!pieces.TryGetValue(root, out List<Face>? list))
                {
                    pieces[root] = list = [];
                }

                list.Add(new Face(CornerOf(s, s.Indices[t]), CornerOf(s, s.Indices[t + 1]), CornerOf(s, s.Indices[t + 2]), 0, 0));
            }

            foreach (List<Face> piece in pieces.Values)
            {
                yield return piece;
            }
        }
    }

    /// <summary>One GEOMOBJECT. Unreal (x, y, z) is written as Max (x, -y, z); the mirror reverses the winding, which
    /// cancels the glTF-to-Unreal axis swap, so a glTF triangle keeps its corner order. V is flipped (1 - v).</summary>
    private static void GeomObject(StringBuilder sb, string name, List<Face> faces)
    {
        var vertices = new List<Vec3>();
        var index = new Dictionary<(long, long, long), int>();
        int Id(Vec3 p)
        {
            (long, long, long) key = (Key(p.X), Key(p.Y), Key(p.Z));
            if (!index.TryGetValue(key, out int id))
            {
                id = index[key] = vertices.Count;
                vertices.Add(p);
            }

            return id;
        }

        var ids = faces.Select(f => (Id(f.A.Position), Id(f.B.Position), Id(f.C.Position))).ToList();
        Line(sb, "*GEOMOBJECT {");
        Line(sb, $"\t*NODE_NAME \"{name}\"");
        Line(sb, "\t*MESH {");
        Line(sb, "\t\t*TIMEVALUE 0");
        Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t*MESH_NUMVERTEX {vertices.Count}"));
        Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t*MESH_NUMFACES {faces.Count}"));
        Line(sb, "\t\t*MESH_VERTEX_LIST {");
        for (int i = 0; i < vertices.Count; i++)
        {
            Vec3 v = vertices[i];
            Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t\t*MESH_VERTEX {i}\t{F(v.X)}\t{F(-v.Y)}\t{F(v.Z)}"));
        }

        Line(sb, "\t\t}");
        Line(sb, "\t\t*MESH_FACE_LIST {");
        for (int i = 0; i < faces.Count; i++)
        {
            (int a, int b, int c) = ids[i];
            Line(sb, string.Create(CultureInfo.InvariantCulture,
                $"\t\t\t*MESH_FACE {i}:\tA: {a} B: {b} C: {c} AB: 1 BC: 1 CA: 1\t*MESH_SMOOTHING {faces[i].Smoothing}\t*MESH_MTLID {faces[i].Slot}"));
        }

        Line(sb, "\t\t}");
        Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t*MESH_NUMTVERTEX {3 * faces.Count}"));
        Line(sb, "\t\t*MESH_TVERTLIST {");
        for (int i = 0; i < faces.Count; i++)
        {
            Corner[] corners = [faces[i].A, faces[i].B, faces[i].C];
            for (int k = 0; k < 3; k++)
            {
                Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t\t*MESH_TVERT {3 * i + k}\t{F(corners[k].U)}\t{F(1.0 - corners[k].V)}\t0.0000"));
            }
        }

        Line(sb, "\t\t}");
        Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t*MESH_NUMTVFACES {faces.Count}"));
        Line(sb, "\t\t*MESH_TFACELIST {");
        for (int i = 0; i < faces.Count; i++)
        {
            Line(sb, string.Create(CultureInfo.InvariantCulture, $"\t\t\t*MESH_TFACE {i}\t{3 * i}\t{3 * i + 1}\t{3 * i + 2}"));
        }

        Line(sb, "\t\t}");
        Line(sb, "\t}");
        Line(sb, "\t*MATERIAL_REF 0");
        Line(sb, "}");
    }

    private static long Key(double x) => (long)Math.Round(x * 10000);

    private static string F(double x) => (Math.Abs(x) < 5e-5 ? 0.0 : x).ToString("0.0000", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder sb, string text) => sb.Append(text).Append("\r\n");
}
