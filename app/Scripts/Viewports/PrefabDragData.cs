using Godot;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>A captured prefab placement, accepted only by viewport drop targets.</summary>
public sealed partial class PrefabDragData : RefCounted
{
    public PrefabDragData() { }

    public PrefabDragData(string title, Action<Vec3> place, double surfaceOffset = 0)
    {
        Title = title;
        _place = place;
        SurfaceOffset = surfaceOffset;
    }

    private readonly Action<Vec3>? _place;
    public string Title { get; } = "Prefab";
    public double SurfaceOffset { get; }
    public void Place(Vec3 point) => _place?.Invoke(point);

    public static PrefabDragData? Read(Variant data) =>
        data.VariantType == Variant.Type.Object ? data.AsGodotObject() as PrefabDragData : null;
}
