using Godot;

namespace OpenScctEditor.Panels;

/// <summary>Drag the row under the pointer, rather than a previously selected class.</summary>
public sealed partial class ClassDragTree : Tree
{
    public Func<string, Variant>? BeginDrag { get; set; }

    public override Variant _GetDragData(Vector2 at)
    {
        if (GetItemAtPosition(at) is not { } item || !item.IsSelectable(0)
            || item.GetMetadata(0).AsString() is not { Length: > 0 } cls) return default;
        item.Select(0);
        return BeginDrag?.Invoke(cls) ?? default;
    }
}
