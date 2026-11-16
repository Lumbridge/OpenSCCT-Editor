using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Scripting;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>The selected assembly, shared by door mode and palette drag/drop.</summary>
public sealed class DoorPrefabPlacement(string name, string? folder, Func<SvJsonObject> parameters, Action<SvJsonObject, Vec3, Rot> place)
{
    private string? _key;
    private MapProject? _local;
    private MapProject? _preview;
    private Vec3 _point;
    private Rot _rotation;
    private SvJsonObject _given = new();
    public string? Problem { get; private set; }
    public Vec3 Point => _point;
    public int LeafCount => _preview?.Actors.Count(a => a.Door is not null) ?? 0;
    public MapProject? PreviewProject => _preview;
    public Rot Rotation => _rotation;
    public void ClearPreview() => _preview = null;

    public bool Preview(EditorViewport view, Vector2 at, int yaw, NeighbourDrag snap)
    {
        _given = parameters();
        string key = ApiJson.Write(_given);
        if (_local is null || key != _key)
        {
            _key = key;
            _local = PrefabPreview.Expand(name, folder, out var why, _given);
            if (_local is null) { Problem = why; _preview = null; return false; }
        }
        var leaves = _local.Actors.Where(a => a.Door is not null).ToArray();
        if (leaves.Length == 0) { Problem = "This prefab contains no editable door."; return false; }
        var vertices = leaves.SelectMany(a => DoorGeometry.Leaf(a.Door!).SelectMany(p => p.Shape.Faces).SelectMany(f => f.Vertices).Select(a.LocalToWorld)).ToArray();
        Vec3 min = vertices.Aggregate(Vec3.Min), max = vertices.Aggregate(Vec3.Max);
        var design = leaves[0].Door!.Clone();
        design.Width = Math.Max(48, max.Y - min.Y);
        design.Height = leaves.Max(a => a.Door!.Height);
        design.Framed = false;
        var (anchor, problem) = DoorTool.Preview(view, at, design, yaw);
        Problem = problem;
        if (anchor is null) { _preview = null; return false; }
        _rotation = anchor.Rotation;
        _point = anchor.Location - _rotation.Rotate(design.Pivot) - Vec3.UnitZ * design.Height / 2;
        _preview = ProjectSnapshot.Take(_local);
        var transform = PrefabTransform.Place(_point, _rotation);
        foreach (var actor in _preview.Actors) transform.Apply(actor);
        foreach (var brush in _preview.Brushes) transform.Apply(brush);
        Vec3 offset = DoorTool.SnapPreview(view, _preview.Actors, _preview.Brushes, snap, _rotation);
        _point += offset;
        foreach (var actor in _preview.Actors) { var move = PrefabTransform.Place(offset, Rot.Zero); move.Apply(actor); }
        foreach (var brush in _preview.Brushes) brush.Location += offset;
        return Problem is null;
    }

    public void Commit() { if (_preview is not null && Problem is null) place(_given, _point, _rotation); }
    public void Draw(EditorViewport view, Control canvas)
    {
        if (_preview is null) return;
        foreach (var actor in _preview.Actors.Where(a => a.Door is not null)) DoorTool.DrawPreview(view, canvas, actor, Problem is not null);
        foreach (var brush in _preview.Brushes)
        foreach (var face in brush.WorldFaces())
            FacePicking.DrawPolygon(view.Cam, canvas, face.Vertices, Colors.Transparent,
                Problem is not null ? Colors.OrangeRed : brush.Oper == CsgOper.Subtract ? new Color(.3f, .9f, .5f, .4f) : Colors.LightSkyBlue);
    }
}
