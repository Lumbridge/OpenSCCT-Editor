using System.Globalization;
using System.Numerics;

namespace OpenScct.Editor.Core.Assets.Import;

/// <summary>
/// Reads a Wavefront OBJ (with its MTL materials) into a <see cref="SourceMesh"/>. Positions are taken as Unreal
/// units unless scaled; <paramref name="zUp"/> reads a Z-up file (Blender's own axes)
/// instead of the usual Y-up. Polygons are fanned into triangles; missing normals become flat ones.
/// </summary>
public static class ObjReader
{
    public static SourceMesh Read(string path, bool zUp)
    {
        var mesh = new SourceMesh();
        var v = new List<Vector3>();
        var vt = new List<Vector2>();
        var vn = new List<Vector3>();
        SourcePart part = new(Path.GetFileNameWithoutExtension(path));
        mesh.Parts.Add(part);
        string? material = null;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        int lineNo = 0;
        foreach (string raw in File.ReadLines(path))
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "v" when t.Length >= 4:
                    v.Add(Axis(new Vector3(F(t[1]), F(t[2]), F(t[3])), zUp) / 100f);
                    break;
                case "vt" when t.Length >= 3:
                    vt.Add(new Vector2(F(t[1]), 1 - F(t[2])));
                    break;
                case "vn" when t.Length >= 4:
                    vn.Add(Vector3.Normalize(Axis(new Vector3(F(t[1]), F(t[2]), F(t[3])), zUp)));
                    break;
                case "o" or "g" when t.Length >= 2:
                    string name = string.Join(' ', t[1..]);
                    if (part.Triangles.Count == 0 && part.Positions.Count == 0)
                    {
                        mesh.Parts.Remove(part);
                    }

                    part = mesh.Parts.Find(p => p.Name == name) ?? new SourcePart(name);
                    if (!mesh.Parts.Contains(part))
                    {
                        mesh.Parts.Add(part);
                    }

                    break;
                case "usemtl" when t.Length >= 2:
                    material = string.Join(' ', t[1..]);
                    if (!mesh.Materials.ContainsKey(material))
                    {
                        mesh.Materials[material] = new SourceMaterial(material);
                    }

                    break;
                case "mtllib" when t.Length >= 2:
                    ReadMtl(Path.Combine(dir, string.Join(' ', t[1..])), mesh);
                    break;
                case "f" when t.Length >= 4:
                    Face(t, v, vt, vn, part, material, mesh, lineNo);
                    break;
            }
        }

        mesh.Parts.RemoveAll(p => p.Triangles.Count == 0);
        return mesh;
    }

    private static Vector3 Axis(Vector3 p, bool zUp) => zUp ? new Vector3(p.X, p.Z, -p.Y) : p;

    private static void Face(string[] t, List<Vector3> v, List<Vector2> vt, List<Vector3> vn, SourcePart part, string? material, SourceMesh mesh, int lineNo)
    {
        var corners = new List<(Vector3 P, Vector2 Uv, Vector3? N)>();
        for (int i = 1; i < t.Length; i++)
        {
            string[] k = t[i].Split('/');
            if (Index(k[0], v.Count) is not int pi)
            {
                mesh.Notes.Add($"line {lineNo}: bad vertex index '{t[i]}'");
                return;
            }

            Vector2 uv = k.Length > 1 && Index(k[1], vt.Count) is int ti ? vt[ti] : Vector2.Zero;
            Vector3? n = k.Length > 2 && Index(k[2], vn.Count) is int ni ? vn[ni] : null;
            corners.Add((v[pi], uv, n));
        }

        // Newell's normal of the polygon, for corners without one (OBJ fronts are counter-clockwise, as glTF's).
        Vector3 flat = Vector3.Zero;
        for (int i = 0; i < corners.Count; i++)
        {
            Vector3 a = corners[i].P, b = corners[(i + 1) % corners.Count].P;
            flat += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }

        flat = flat.LengthSquared() > 0 ? Vector3.Normalize(flat) : Vector3.UnitY;
        int first = -1, prev = -1;
        for (int i = 0; i < corners.Count; i++)
        {
            int index = part.AddVertex(corners[i].P, corners[i].N ?? flat, corners[i].Uv);
            if (i == 0)
            {
                first = index;
            }
            else if (i >= 2)
            {
                part.Triangles.Add((first, prev, index, material));
            }

            prev = index;
        }
    }

    /// <summary>A 1-based (or negative, from the end) OBJ index as 0-based, or null.</summary>
    private static int? Index(string s, int count)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) || i == 0)
        {
            return null;
        }

        int r = i > 0 ? i - 1 : count + i;
        return r >= 0 && r < count ? r : null;
    }

    private static void ReadMtl(string path, SourceMesh mesh)
    {
        if (!File.Exists(path))
        {
            mesh.Notes.Add($"material library {Path.GetFileName(path)} not found");
            return;
        }

        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        SourceMaterial? m = null;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2 || t[0].StartsWith('#'))
            {
                continue;
            }

            switch (t[0])
            {
                case "newmtl":
                    string name = string.Join(' ', t[1..]);
                    m = mesh.Materials.GetValueOrDefault(name) ?? new SourceMaterial(name);
                    mesh.Materials[name] = m;
                    break;
                case "Kd" when m is not null && t.Length >= 4:
                    m.Color = new Vector3(F(t[1]), F(t[2]), F(t[3]));
                    break;
                case "map_Kd" when m is not null:
                    // Options (-s, -o ...) come before the file name; the name is the last token(s) that exist.
                    string file = Path.Combine(dir, t[^1]);
                    m.ImageFile = File.Exists(file) ? file : Path.Combine(dir, string.Join(' ', t[1..]));
                    break;
                case "d" when m is not null && t.Length >= 2 && F(t[1]) < 0.999f:
                    m.AlphaMode = "BLEND";
                    break;
                case "map_d" when m is not null:
                    m.AlphaMode ??= "MASK";
                    break;
            }
        }
    }

    private static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0;
}
