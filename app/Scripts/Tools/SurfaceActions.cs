using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Tools;

/// <summary>
/// The Surface menu: Surface Properties (F5: the surface tool and the Surface panel), applying the Assets panel's
/// material, face selection (same material, coplanar, adjacent, the whole brush, all surfaces, reverse, walls, floors, ceilings, the owning
/// brushes), copy and paste surface, alignment, flips, rotate, scale and pan steps, flag toggles, extruding faces, and
/// whether scaling a brush keeps its texture size.
/// Every action works on the face selection, so it runs from menus, the palette and <c>--run</c> alike.
/// </summary>
[ShellPart(Order = -24)]
public sealed class SurfaceActions : IShellPart
{
    private EditorContext _ctx = null!;

    private static GeometrySelection Geo => GeometrySelection.Instance;

    /// <summary>True (the default) when scaling a brush, or resizing a builder brush through its parameters, keeps its
    /// textures the size they were instead of stretching them with it (<see cref="TextureLock"/>). Stored per user in
    /// settings.cfg; also in Preferences > General.</summary>
    public static bool ScaleKeepsTextures
    {
        get => AppSettings.GetBool("editing", "scale_keeps_textures", true);
        set => AppSettings.Set("editing", "scale_keeps_textures", value);
    }

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        bool HasFaces() => Geo.Faces.Count > 0;
        Add("surface.displacedCollision", "Use Displaced Collision", "Surface", 25,
            () => SurfaceCommands.Run("Use displaced collision", (face, _) => { face.DisplacementCollision = true; return face; }), HasFaces, null,
            "Build movement, bullet and camera collision from the selected faces' displaced shape");
        Add("surface.flatCollision", "Use Original Collision", "Surface", 26,
            () => SurfaceCommands.Run("Use original collision", (face, _) => { face.DisplacementCollision = false; return face; }), HasFaces, null,
            "Keep the selected faces' depth visual and use their original collision");
        Add("surface.materialCollision", "Use Material Collision Setting", "Surface", 27,
            () => SurfaceCommands.Run("Use material collision setting", (face, _) => { face.DisplacementCollision = null; return face; }), HasFaces, null,
            "Use each selected face's material default for displaced collision");
        Geo.Changed += () => Viewports.ViewportLayout.Instance?.RedrawAll();

        Add("surface.properties", "Surface Properties", "Surface", 10, ShowProperties, null, "F5",
            "Pick surfaces with the surface tool (U) and edit them in the Surface panel");
        Add("surface.applyMaterial", "Apply Material from Assets", "Surface", 20,
            () => SurfaceCommands.ApplyMaterial(Panels.AssetBrowserPanel.CurrentMaterial),
            () => HasFaces() && Panels.AssetBrowserPanel.CurrentMaterial is not null, null,
            "Set the material last picked in the Assets panel on the picked faces (Alt+click a face in the surface tool)");

        Add("surface.selectSameMaterial", "Matching Material", "Surface/Select", 100, () => SurfaceCommands.SelectMatching("material"), HasFaces,
            null, "Every face of the map with the picked face's material");
        Add("surface.selectCoplanar", "Matching Plane (Coplanar)", "Surface/Select", 110, () => SurfaceCommands.SelectMatching("coplanar"), HasFaces,
            null, "Every face of the map in the picked face's plane");
        Add("surface.selectAllFaces", "Matching Brush (All Faces)", "Surface/Select", 120, SurfaceCommands.SelectAllFaces, null, null,
            "Every face of the picked faces' brushes (or of the selected brushes)");
        Add("surface.selectAdjacent", "Adjacent All", "Surface/Select", 200, () => SurfaceCommands.SelectAdjacent("all"), HasFaces,
            null, "Every face connected to the picked ones");
        Add("surface.selectAdjacentCoplanar", "Adjacent Coplanars", "Surface/Select", 210, () => SurfaceCommands.SelectAdjacent("coplanar"), HasFaces,
            null, "Faces connected to the picked ones in the same plane");
        Add("surface.selectAdjacentWalls", "Adjacent Walls", "Surface/Select", 220, () => SurfaceCommands.SelectAdjacent("walls"), HasFaces,
            null, "Walls connected to the picked faces through walls");
        Add("surface.selectAdjacentFloors", "Adjacent Floors/Ceilings", "Surface/Select", 230, () => SurfaceCommands.SelectAdjacent("floors"), HasFaces,
            null, "Floors and ceilings connected to the picked faces through floors and ceilings");
        Add("surface.selectAdjacentSlants", "Adjacent Slants", "Surface/Select", 240, () => SurfaceCommands.SelectAdjacent("slants"), HasFaces,
            null, "Sloped faces connected to the picked faces through sloped faces");
        Add("surface.selectWalls", "Walls of Brushes", "Surface/Select", 300, () => SurfaceCommands.SelectBySlope("walls"));
        Add("surface.selectFloors", "Floors of Brushes", "Surface/Select", 310, () => SurfaceCommands.SelectBySlope("floors"));
        Add("surface.selectCeilings", "Ceilings of Brushes", "Surface/Select", 320, () => SurfaceCommands.SelectBySlope("ceilings"));
        Add("surface.selectAll", "All Surfaces", "Surface/Select", 330, SurfaceCommands.SelectAllSurfaces, null, null,
            "Every face of the visible, unlocked brushes");
        Add("surface.selectReverse", "Reverse", "Surface/Select", 340, SurfaceCommands.ReverseSelection, null, null,
            "Pick the faces that are not picked and drop the ones that are");
        Add("surface.selectBrush", "Select Brushes of Faces", "Surface/Select", 400, SurfaceCommands.SelectBrushes, HasFaces);
        Add("surface.clearFaces", "Clear Face Selection", "Surface/Select", 410, () => Geo.SetFaces([]), () => HasFaces() || Geo.BakedSurface is not null);

        Add("surface.copy", "Copy Surface", "Surface", 100, SurfaceCommands.Copy, HasFaces, "Ctrl+Shift+C",
            "Copy the picked face's material, flags and alignment");
        Add("surface.paste", "Paste Surface", "Surface", 110, () => SurfaceCommands.Paste(false), HasFaces, null,
            "Paste material, flags and lightmap scale, keeping each face's alignment (Ctrl+Shift+V in face mode)");
        Add("surface.pasteAlignment", "Paste Surface and Alignment", "Surface", 120, () => SurfaceCommands.Paste(true), HasFaces, null,
            "Paste the copied surface with its texture alignment (Ctrl+Alt+Shift+V in face mode)");
        Add("surface.scaleKeepsTextures", "Scale Keeps Texture Size", "Surface", 200, () => ScaleKeepsTextures = !ScaleKeepsTextures, null, null,
            "Scaling or resizing a brush keeps its textures the same size, so a stretched face shows more repeats; off, they stretch with the brush",
            () => ScaleKeepsTextures);

        Add("surface.alignDefault", "Default (Box)", "Surface/Align", 100,
            () => SurfaceCommands.Run("Align default", (f, t) => TextureOps.AlignDefault(f, t)), HasFaces);
        Add("surface.alignFloor", "Floor", "Surface/Align", 110, () => SurfaceCommands.Run("Align floor", (f, t) => TextureOps.AlignFloor(f, t)), HasFaces);
        Add("surface.alignWall", "Wall", "Surface/Align", 120, () => SurfaceCommands.Run("Align wall", (f, t) => TextureOps.AlignWall(f, t)), HasFaces);
        Add("surface.alignFace", "Planar (Face)", "Surface/Align", 130, () => SurfaceCommands.Run("Align to face", (f, t) => TextureOps.AlignFace(f, t)), HasFaces);
        Add("surface.alignFit", "Fit", "Surface/Align", 140, () => SurfaceCommands.Run("Fit texture", FaceEdit.Fit()), HasFaces);
        Add("surface.alignCylinder", "Cylinder", "Surface/Align", 150,
            () => SurfaceCommands.RunBrush("Align cylinder", (b, faces) => TextureOps.AlignCylinder(b, faces)), HasFaces);

        Add("surface.flipU", "Flip U", "Surface/Adjust", 100, () => SurfaceCommands.Run("Flip U", (f, _) => TextureOps.FlipU(f)), HasFaces);
        Add("surface.flipV", "Flip V", "Surface/Adjust", 110, () => SurfaceCommands.Run("Flip V", (f, _) => TextureOps.FlipV(f)), HasFaces);
        Add("surface.rotateCw", "Rotate 90° Clockwise", "Surface/Adjust", 120, () => Rotate(90), HasFaces);
        Add("surface.rotateCcw", "Rotate 90° Anticlockwise", "Surface/Adjust", 130, () => Rotate(-90), HasFaces);
        Add("surface.rotate45", "Rotate 45°", "Surface/Adjust", 140, () => Rotate(45), HasFaces);
        Add("surface.scaleUp", "Texture Twice as Big", "Surface/Adjust", 150,
            () => SurfaceCommands.Run("Scale texture x2", (f, _) => TextureOps.Scale(f, 2, 2)), HasFaces);
        Add("surface.scaleDown", "Texture Half as Big", "Surface/Adjust", 160,
            () => SurfaceCommands.Run("Scale texture x0.5", (f, _) => TextureOps.Scale(f, 0.5, 0.5)), HasFaces);
        Add("surface.panLeft", "Pan U −16", "Surface/Adjust", 170, () => Pan(-16, 0), HasFaces);
        Add("surface.panRight", "Pan U +16", "Surface/Adjust", 171, () => Pan(16, 0), HasFaces);
        Add("surface.panUp", "Pan V −16", "Surface/Adjust", 172, () => Pan(0, -16), HasFaces);
        Add("surface.panDown", "Pan V +16", "Surface/Adjust", 173, () => Pan(0, 16), HasFaces);
        Add("surface.resetPan", "Reset Pan", "Surface/Adjust", 180, () => SurfaceCommands.Run("Reset pan", FaceEdit.SetPan(0, 0)), HasFaces);

        int order = 100;
        foreach ((string name, int bit, string help) in SurfaceFlags.All)
        {
            int b = bit;
            Add("surface.flag." + name, name, "Surface/Flags", order++, () => ToggleFlag(b), HasFaces, null, help,
                () => Geo.PrimaryFace is { } f && FaceEdit.Find(_ctx.Project, f) is { } face && (face.Flags & b) != 0);
        }

        Add("surface.extrude", "Extrude Face Out by Grid", "Surface/Extrude", 100, () => ExtrudeFaces(1), HasFaces);
        Add("surface.extrudeIn", "Push Face In by Grid", "Surface/Extrude", 110, () => ExtrudeFaces(-1), HasFaces);
        Add("surface.extrudeNew", "New Brush from Face by Grid", "Surface/Extrude", 120, ExtrudeNew, HasFaces,
            null, "Grow a new brush of the same CSG type out of each picked face");

        bool OnWall() => Geo.PrimaryFace is { } f && WallPieces.Anchor(_ctx.Project, f) is { Vertical: false };
        bool OnFloorOrCeiling() => Geo.PrimaryFace is { } f && WallPieces.Anchor(_ctx.Project, f) is { Vertical: true };
        Add("surface.roomOpening", "Cut Opening When Adding Rooms", "Surface", 95,
            () => WallPieceCommands.CutRoomOpening = !WallPieceCommands.CutRoomOpening, null, null,
            "Also cut a doorway or hatch when adding a room from a surface. Off by default: keep the separating wall or floor.",
            () => WallPieceCommands.CutRoomOpening);
        order = 100;
        foreach (string kind in WallPieces.Kinds)
        {
            string k = kind;
            Add("surface.wall." + k, "Add " + WallPieces.Title(k), "Surface/Add on Wall", order++,
                () => WallPieceCommands.AddOnPickedFace(k), OnWall, null,
                $"RE+ wall '+': a {WallPieces.Title(k).ToLowerInvariant()} at the centre of the picked wall of a carved room (hover a wall in face mode for the + under the cursor)");
        }
        foreach (string kind in WallPieces.VerticalKinds)
        {
            string k = kind;
            Add("surface.vertical." + k, k switch { "hatch" => "Add Hatch Opening", "shaft" => "Extend Shaft", "room" => "Add Room", _ => "Connect Stairs…" },
                "Surface/Add on Floor or Ceiling", order++, () => WallPieceCommands.AddOnPickedFace(k), OnFloorOrCeiling, null,
                "Build a connector through the selected floor or ceiling; hover the surface for its contextual + menu");
        }
    }

    /// <summary>F5: the surface tool and the Surface panel, as the stock Surface Properties opens its sheet.</summary>
    private void ShowProperties()
    {
        ToolManager.Instance?.Activate("face");
        _ctx.SetStatus(DockTabs.ShowPanel("Surface")
            ? "Surface tool: click a surface to edit it in the Surface panel"
            : "Surface tool: click a surface to pick it (the Surface panel is not in any dock)");
    }

    private void Add(string id, string title, string menu, int order, Action run, Func<bool>? enabled = null, string? shortcut = null,
        string? description = null, Func<bool>? isChecked = null) =>
        _ctx.AddAction(new EditorAction(id, title, menu, run)
        {
            Order = order,
            IsEnabled = enabled ?? (() => true),
            Shortcut = shortcut,
            Description = description,
            IsChecked = isChecked,
        });

    private static void Rotate(double degrees) =>
        SurfaceCommands.Run($"Rotate texture {degrees}°", (f, t) => TextureOps.Rotate(f, t, degrees));

    private static void Pan(int du, int dv) => SurfaceCommands.Run("Pan texture", (f, _) => TextureOps.Pan(f, du, dv));

    /// <summary>Turns a flag on for every picked face, or off when the picked face already has it.</summary>
    private void ToggleFlag(int bit)
    {
        bool on = Geo.PrimaryFace is { } f && FaceEdit.Find(_ctx.Project, f) is { } face && (face.Flags & bit) != 0;
        string name = SurfaceFlags.Names(bit).FirstOrDefault() ?? "flag";
        SurfaceCommands.Run($"{(on ? "Clear" : "Set")} {name}", on ? FaceEdit.SetFlags(0, bit) : FaceEdit.SetFlags(bit, 0));
    }

    private double Step => ToolManager.Instance?.GridSize ?? 16;

    private void ExtrudeFaces(int direction) => SurfaceCommands.Extrude(Step * direction);

    private void ExtrudeNew()
    {
        var ids = new FreshIds(_ctx.Project);
        var added = new List<Brush>();
        try
        {
            foreach (FaceRef f in Geo.Faces)
            {
                Brush b = _ctx.Project.FindBrush(f.Brush)!;
                added.Add(Extrude.NewBrush(b, f.Face, Step, ids.Next(b.Id)));
            }
        }
        catch (ArgumentException ex)
        {
            _ctx.Log($"Extrude: {ex.Message}", Severity.Warning);
            return;
        }

        var cmd = new AddObjectsCommand($"Extrude {added.Count} new brush{(added.Count == 1 ? "" : "es")}", added);
        _ctx.Execute(cmd);
        // The far face of each new brush is face 0: keep extruding from there.
        Geo.SetFaces(added.Select(b => new FaceRef(b.Id, 0)));
        _ctx.SetStatus($"New brushes: {string.Join(", ", added.Select(b => b.Id))}");
    }
}
