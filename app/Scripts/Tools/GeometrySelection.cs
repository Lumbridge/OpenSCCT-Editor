using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;

namespace OpenScctEditor.Tools;

/// <summary>A selected brush vertex: the brush and the vertex's world position.</summary>
public readonly record struct VertexRef(string Brush, Vec3 Position);

/// <summary>A surface of an imported map's baked world picked with the surface tool: read-only, shown for what it is.
/// <paramref name="Triangles"/> are its world triangles (three points each, UE units), the connected run of triangles
/// in the plane and material of the one clicked.</summary>
public sealed record BakedSurfacePick(string? Material, Vec3 Point, Vec3 Normal, IReadOnlyList<Vec3> Triangles);

/// <summary>
/// The sub-object selection of the geometry tools: brush faces (face mode, the Surface panel) and brush vertices
/// (vertex and edge mode), plus the surface clipboard. Lives beside the object selection; edits prune entries that
/// no longer exist, and opening another map clears it.
/// </summary>
public sealed class GeometrySelection
{
    private static GeometrySelection? s_instance;
    private readonly EditorContext _ctx;
    private readonly List<FaceRef> _faces = new();
    private readonly HashSet<FaceRef> _faceSet = new();
    private readonly List<VertexRef> _vertices = new();

    /// <summary>The selected positions per brush, so lookups stay cheap with thousands of vertices picked.</summary>
    private readonly Dictionary<string, List<Vec3>> _byBrush = new(StringComparer.OrdinalIgnoreCase);

    private GeometrySelection(EditorContext ctx)
    {
        _ctx = ctx;
        ctx.ProjectChanged += _ => Prune();
        ctx.SessionReplaced += () =>
        {
            BakedSurface = null;
            _faces.Clear();
            _faceSet.Clear();
            _vertices.Clear();
            _byBrush.Clear();
            Changed?.Invoke();
        };
    }

    public static GeometrySelection Instance => s_instance ??= new GeometrySelection(EditorContext.Instance);

    /// <summary>Raised when the faces or vertices change.</summary>
    public event Action? Changed;

    public IReadOnlyList<FaceRef> Faces => _faces;

    /// <summary>The last face picked (its values fill the Surface panel), or null.</summary>
    public FaceRef? PrimaryFace => _faces.Count > 0 ? _faces[^1] : null;

    public IReadOnlyList<VertexRef> Vertices => _vertices;

    /// <summary>The baked-world surface picked instead of brush faces, or null. Picking faces clears it.</summary>
    public BakedSurfacePick? BakedSurface { get; private set; }

    /// <summary>The copied surface and the transform of the brush it came from.</summary>
    public (SurfaceStyle Style, ShapeTransform Transform)? Clipboard { get; set; }

    public void SetFaces(IEnumerable<FaceRef> faces)
    {
        BakedSurface = null;
        _faces.Clear();
        _faceSet.Clear();
        foreach (FaceRef f in faces)
        {
            if (_faceSet.Add(f))
            {
                _faces.Add(f);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Adds faces; a face already picked moves to the end, so it becomes the primary face.</summary>
    public void AddFaces(IEnumerable<FaceRef> faces)
    {
        BakedSurface = null;
        foreach (FaceRef f in faces)
        {
            if (!_faceSet.Add(f))
            {
                _faces.Remove(f);
            }

            _faces.Add(f);
        }

        Changed?.Invoke();
    }

    public void ToggleFace(FaceRef f)
    {
        BakedSurface = null;
        if (_faceSet.Remove(f))
        {
            _faces.Remove(f);
        }
        else
        {
            _faceSet.Add(f);
            _faces.Add(f);
        }

        Changed?.Invoke();
    }

    public bool HasFace(FaceRef f) => _faceSet.Contains(f);

    /// <summary>Shows a baked-world surface (read-only) in place of the picked faces.</summary>
    public void SetBakedSurface(BakedSurfacePick pick)
    {
        _faces.Clear();
        _faceSet.Clear();
        BakedSurface = pick;
        Changed?.Invoke();
    }

    public void SetVertices(IEnumerable<VertexRef> vertices)
    {
        _vertices.Clear();
        _byBrush.Clear();
        foreach (VertexRef v in vertices)
        {
            if (!HasVertex(v))
            {
                AddVertex(v);
            }
        }

        Changed?.Invoke();
    }

    public void ToggleVertex(VertexRef v)
    {
        if (HasVertex(v))
        {
            _vertices.RemoveAll(x => SameVertex(x, v));
            _byBrush[v.Brush].RemoveAll(p => VertexEdit.Near(p, v.Position));
        }
        else
        {
            AddVertex(v);
        }

        Changed?.Invoke();
    }

    public bool HasVertex(VertexRef v) => _byBrush.TryGetValue(v.Brush, out List<Vec3>? list) && VertexEdit.IsSelected(list, v.Position);

    /// <summary>The selected vertex positions of one brush.</summary>
    public List<Vec3> PositionsOf(string brush) => _byBrush.TryGetValue(brush, out List<Vec3>? list) ? [.. list] : [];

    /// <summary>The brushes that have selected vertices, in map order.</summary>
    public List<Brush> VertexBrushes() =>
        _ctx.Project.Brushes.Where(b => _byBrush.TryGetValue(b.Id, out List<Vec3>? list) && list.Count > 0).ToList();

    public void Clear()
    {
        if (_faces.Count == 0 && _vertices.Count == 0 && BakedSurface is null)
        {
            return;
        }

        BakedSurface = null;
        _faces.Clear();
        _faceSet.Clear();
        _vertices.Clear();
        _byBrush.Clear();
        Changed?.Invoke();
    }

    private void AddVertex(VertexRef v)
    {
        _vertices.Add(v);
        if (!_byBrush.TryGetValue(v.Brush, out List<Vec3>? list))
        {
            _byBrush[v.Brush] = list = new List<Vec3>();
        }

        list.Add(v.Position);
    }

    private static bool SameVertex(VertexRef a, VertexRef b) =>
        string.Equals(a.Brush, b.Brush, StringComparison.OrdinalIgnoreCase) && VertexEdit.Near(a.Position, b.Position);

    /// <summary>Drops faces and vertices whose brush is gone (or that no longer exist on it).</summary>
    private void Prune()
    {
        MapProject p = _ctx.Project;
        if (BakedSurface is not null && p.Baked is not { KeepRender: true })
        {
            BakedSurface = null;
            Changed?.Invoke();
        }

        int before = _faces.Count + _vertices.Count;
        if (before == 0)
        {
            return;
        }

        Dictionary<string, Brush> brushes = FaceEdit.BrushIndex(p);
        if (_faces.RemoveAll(f => !brushes.TryGetValue(f.Brush, out Brush? b) || f.Face >= b.Shape.Faces.Count) > 0)
        {
            _faceSet.Clear();
            _faceSet.UnionWith(_faces);
        }

        if (_vertices.RemoveAll(v => !brushes.TryGetValue(v.Brush, out Brush? b) || !VertexEdit.IsSelected(BrushVertices.Of(b), v.Position)) > 0)
        {
            List<VertexRef> kept = [.. _vertices];
            _vertices.Clear();
            _byBrush.Clear();
            kept.ForEach(AddVertex);
        }

        if (_faces.Count + _vertices.Count != before)
        {
            Changed?.Invoke();
        }
    }
}
