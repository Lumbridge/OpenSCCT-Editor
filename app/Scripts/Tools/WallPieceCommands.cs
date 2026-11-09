using OpenScct.Editor.Core.Commands;
using OpenScctEditor.Viewports;
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

    /// <summary>Surface room additions keep the separating wall/slab unless explicitly enabled.</summary>
    private static bool _cutRoomOpening;
    public static bool CutRoomOpening
    {
        get => _cutRoomOpening;
        set { if (_cutRoomOpening == value) return; _cutRoomOpening = value; RoomOpeningChanged?.Invoke(); }
    }
    public static event Action? RoomOpeningChanged;

    /// <summary>Adds a piece on the picked face's wall, at the face's centre.</summary>
    public static bool AddOnPickedFace(string kind)
    {
        if (GeometrySelection.Instance.PrimaryFace is not { } face || WallPieces.Anchor(Ctx.Project, face) is not { } anchor)
        {
            Ctx.SetStatus($"Add {kind}: pick an editable room wall, floor or ceiling in Surface mode (U)");
            return false;
        }

        return Add(kind, anchor);
    }

    public static bool Add(string kind, WallAnchor anchor)
    {
        try
        {
            if (kind == "stairs" && anchor.Vertical && StairTools.Tool is { } stairs)
            {
                var point = anchor.Point;
                if (anchor.Into.Z > 0)
                {
                    var world = new OpenScct.Editor.Core.Scripting.WorldGeometry(Ctx.Project, Ctx.Session.Folder);
                    if (world.Raycast(point - Vec3.UnitZ * 2, -Vec3.UnitZ, 65536) is not { Normal.Z: > .5 } floor)
                    { Ctx.SetStatus("Connect stairs: select a floor, or a ceiling with a clear floor beneath it"); return false; }
                    point = floor.Point;
                }
                stairs.StartConnection(point);
                return true;
            }
            List<Brush> added = WallPieces.Build(Ctx.Project, anchor, kind, cutOpening: CutRoomOpening);
            Ctx.Execute(new AddObjectsCommand($"Add {kind} on {anchor.Surface}", added));
            Ctx.Selection.Set(added.Select(b => ObjectRef.ForBrush(b)));
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
