using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;

namespace OpenScctEditor.Tools;

/// <summary>Adds the RE+ wall pieces (the face tool's hover "+", and the Surface/Add on Wall menu) as one undo step
/// each, and selects what was added.</summary>
public static class WallPieceCommands
{
    private static EditorContext Ctx => EditorContext.Instance;

    /// <summary>Adds a piece on the picked face's wall, at the face's centre.</summary>
    public static bool AddOnPickedFace(string kind)
    {
        if (GeometrySelection.Instance.PrimaryFace is not { } face || WallPieces.Anchor(Ctx.Project, face) is not { } anchor)
        {
            Ctx.SetStatus($"Add {kind}: pick a wall of a carved (subtract) brush in face mode (U)");
            return false;
        }

        return Add(kind, anchor);
    }

    public static bool Add(string kind, WallAnchor anchor)
    {
        try
        {
            List<Brush> added = WallPieces.Build(Ctx.Project, anchor, kind);
            Ctx.Execute(new AddObjectsCommand($"Add {kind} on wall", added));
            Ctx.Selection.Set(added.Select(b => ObjectRef.Brush(b.Id)));
            Ctx.SetStatus($"Added {WallPieces.Describe(anchor, kind)}: {string.Join(", ", added.Select(b => b.Id))}");
            return true;
        }
        catch (ArgumentException ex)
        {
            Ctx.Log($"Add {kind}: {ex.Message}", Severity.Warning);
            return false;
        }
    }
}
