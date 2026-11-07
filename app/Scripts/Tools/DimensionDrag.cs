using Godot;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Tools;

/// <summary>A dimension follows the pointer ray, with the same grid, neighbour and Alt rules as transforms.</summary>
public sealed class DimensionDrag
{
    private readonly ToolManager _tools;
    private readonly EditorViewport _view;
    private readonly Vec3 _point, _axis;
    private readonly double _unit, _startLine, _value, _multiplier;
    private readonly SnapFace _face;
    public NeighbourDrag Snap { get; }

    public DimensionDrag(ToolManager tools, EditorViewport view, Vector2 mouse, Vec3 point, Vec3 unitAxis,
        double value, double multiplier, ObjectRef owner, double extent)
    {
        _tools = tools; _view = view; _point = point; _unit = unitAxis.Length; _axis = unitAxis.Normalized();
        _value = value; _multiplier = multiplier;
        var ray = view.Cam.Ray(mouse); _startLine = DragMath.LineParam(point, _axis, ray.Origin, ray.Direction);
        var cross = Vec3.Cross(_axis, Math.Abs(_axis.Z) < .9 ? Vec3.UnitZ : Vec3.UnitY).Normalized() * Math.Max(16, extent);
        var other = Vec3.Cross(_axis, cross).Normalized() * Math.Max(16, extent);
        _face = SnapFace.Of(owner, 0, SnapSource.Brush, [point-cross-other, point+cross-other, point+cross+other, point-cross+other], normal: _axis)!;
        Snap = new NeighbourDrag(tools, view, SnapUse.Resize, [owner.Id], () => null);
    }

    public double Value(Vector2 mouse, bool alt, double minimum)
    {
        var ray = _view.Cam.Ray(mouse);
        double raw = DragMath.LineParam(_point, _axis, ray.Origin, ray.Direction) - _startLine;
        double distance = raw;
        if (_tools.GridSnap)
        {
            int axis = Enumerable.Range(0, 3).MaxBy(a => Math.Abs(_axis[a]));
            distance = Math.Abs(_axis[axis]) > .99999
                ? (_tools.SnapValue(_point[axis] + raw * _axis[axis]) - _point[axis]) / _axis[axis]
                : (_tools.SnapValue(_value + raw * _multiplier / _unit) - _value) * _unit / _multiplier;
        }
        Snap.AltHeld = alt;
        if (Snap.Extrude(_face, raw, (minimum - _value) * _unit / _multiplier) is { } stuck) distance = stuck.Value;
        return Math.Max(minimum, _value + distance * _multiplier / _unit);
    }
}
