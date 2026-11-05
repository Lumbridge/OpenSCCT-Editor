using Godot;

namespace OpenScctEditor.Viewports;

/// <summary>Collects coloured line segments and makes one line mesh of them.</summary>
public sealed class LineBuilder
{
    private readonly List<Vector3> _points = new();
    private readonly List<Color> _colors = new();

    public int Count => _points.Count / 2;

    public void Line(Vector3 a, Vector3 b, Color c)
    {
        _points.Add(a);
        _points.Add(b);
        _colors.Add(c);
        _colors.Add(c);
    }

    public void Polyline(IReadOnlyList<Vector3> pts, Color c, bool closed)
    {
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            Line(pts[i], pts[i + 1], c);
        }

        if (closed && pts.Count > 2)
        {
            Line(pts[^1], pts[0], c);
        }
    }

    /// <summary>The 12 edges of a box given by its 8 corners (index bits: x, y, z).</summary>
    public void Box(IReadOnlyList<Vector3> corners, Color c)
    {
        int[] edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < edges.Length; i += 2)
        {
            Line(corners[edges[i]], corners[edges[i + 1]], c);
        }
    }

    public ArrayMesh? Build()
    {
        if (_points.Count == 0)
        {
            return null;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _points.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }
}

/// <summary>Collects convex polygons, fan-triangulated and wound so <c>visibleNormal</c> is their front side.</summary>
public sealed class SurfaceBuilder
{
    private readonly List<Vector3> _points = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colors = new();

    public int Count => _points.Count / 3;

    public void Polygon(IReadOnlyList<Vector3> pts, Vector3 visibleNormal, Color c)
    {
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            Vector3 a = pts[0], b = pts[i], d = pts[i + 1];
            // Godot front faces are clockwise as seen from the front, so (b - a) x (d - a) must point away from the viewer.
            if ((b - a).Cross(d - a).Dot(visibleNormal) > 0)
            {
                (b, d) = (d, b);
            }

            _points.Add(a);
            _points.Add(b);
            _points.Add(d);
            for (int k = 0; k < 3; k++)
            {
                _normals.Add(visibleNormal);
                _colors.Add(c);
            }
        }
    }

    public ArrayMesh? Build()
    {
        if (_points.Count == 0)
        {
            return null;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _points.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
