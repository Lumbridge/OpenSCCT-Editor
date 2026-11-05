using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;

namespace OpenScctEditor.Tools;

/// <summary>
/// Runs surface edits on the selected faces as undoable steps: the Surface panel, the surface tool and the Surface
/// menu all go through here. A merge key folds a drag (a spin box being dragged) into one step. Faces of brushes the
/// kept baked world holds are read-only (<see cref="BakedContent.SurfacesLocked"/>): edits skip them and say so.
/// </summary>
public static class SurfaceCommands
{
    private static EditorContext Ctx => EditorContext.Instance;

    private static GeometrySelection Geo => GeometrySelection.Instance;

    /// <summary>Applies a per-face edit to every selected face (or to <paramref name="faces"/>); false when there is
    /// no face to edit.</summary>
    public static bool Run(string name, Func<BrushFace, ShapeTransform, BrushFace> op, string? mergeKey = null, IReadOnlyList<FaceRef>? faces = null) =>
        Execute(name, faces ?? Geo.Faces, editable => FaceEdit.Apply(Ctx.Project, editable, name, op, mergeKey));

    /// <summary>Applies a whole-brush edit to each brush with its selected faces (cylinder alignment).</summary>
    public static bool RunBrush(string name, Func<Brush, IReadOnlyList<int>, Brush> op) =>
        Execute(name, Geo.Faces, editable => FaceEdit.ApplyBrush(Ctx.Project, editable, name, op));

    /// <summary>Where read-only surfaces become editable, for status messages that say why a face was left alone.</summary>
    public const string BuildRenderHint = "Build render from brushes… (Surface panel) unlocks it";

    /// <summary>True when a face's surface is read-only (its brush is held by the kept baked world).</summary>
    public static bool IsLocked(FaceRef f) => Ctx.Project.FindBrush(f.Brush) is { } b && BakedContent.SurfacesLocked(Ctx.Project, b);

    /// <summary>Sets <paramref name="material"/> on the picked faces (or on <paramref name="faces"/>). Faces that show it
    /// already are left alone, and when every face that could take it does, nothing is done (no undo step, the map stays
    /// saved) and the status bar says so.</summary>
    public static bool ApplyMaterial(string? material, IReadOnlyList<FaceRef>? faces = null)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            Ctx.SetStatus("Apply material: pick a material in the Assets panel first");
            return false;
        }

        material = material.Trim();
        string name = $"Apply {OpenScct.Editor.Core.Assets.AssetPaths.Split(material).Name}";
        IReadOnlyList<FaceRef> chosen = faces ?? Geo.Faces;
        // Read-only faces stay in, so the edit says they were skipped.
        List<FaceRef> changing = chosen.Where(f => IsLocked(f) || !string.Equals(FaceEdit.Find(Ctx.Project, f)?.Material, material,
            StringComparison.OrdinalIgnoreCase)).ToList();
        int same = chosen.Count - changing.Count;
        if (same == 0)
        {
            return Execute(name, chosen, editable => FaceEdit.Apply(Ctx.Project, editable, name, FaceEdit.SetMaterial(material)));
        }

        if (changing.All(IsLocked))
        {
            int locked = changing.Count;
            Ctx.SetStatus($"{name}: already on {(same == 1 ? "that face" : $"those {same} faces")}"
                + (locked > 0 ? $" ({locked} read-only face{(locked == 1 ? "" : "s")} of the imported baked world skipped; {BuildRenderHint})" : ""));
            return true;
        }

        return Execute(name, changing, editable => FaceEdit.Apply(Ctx.Project, editable, name, FaceEdit.SetMaterial(material)),
            $"; {same} already showed it");
    }

    public static void Copy()
    {
        if (Geo.PrimaryFace is not { } f || Ctx.Project.FindBrush(f.Brush) is not { } b || FaceEdit.Find(Ctx.Project, f) is not { } face)
        {
            Ctx.SetStatus("Copy surface: pick a face first");
            return;
        }

        Geo.Clipboard = (SurfaceStyle.Of(face), ShapeTransform.For(b));
        Ctx.SetStatus($"Copied surface of {f} ({face.Material ?? "default material"})");
    }

    /// <summary>Pastes the copied surface: material, flags and lightmap scale, plus the mapping when asked.</summary>
    public static void Paste(bool withMapping)
    {
        if (Geo.Clipboard is not { } clip)
        {
            Ctx.SetStatus("Paste surface: copy a surface first (Ctrl+Shift+C in the surface tool)");
            return;
        }

        Run(withMapping ? "Paste surface and alignment" : "Paste surface",
            (f, t) => TextureOps.Paste(f, t, clip.Style, clip.Transform, withMapping));
    }

    /// <summary>Selects every face of the brushes of the picked faces (stock "Matching Brush"), or of the selected
    /// brushes when no face is picked.</summary>
    public static void SelectAllFaces()
    {
        List<string> brushes = Geo.Faces.Select(f => f.Brush).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (brushes.Count == 0)
        {
            brushes = Ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Brush).Select(r => r.Id).ToList();
        }

        Geo.SetFaces(FaceEdit.AllFaces(Ctx.Project, brushes));
        Report();
    }

    /// <summary>Selects faces matching the primary face: same material, or same plane.</summary>
    public static void SelectMatching(string how)
    {
        if (Geo.PrimaryFace is not { } f || FaceEdit.Find(Ctx.Project, f) is not { } face)
        {
            Ctx.SetStatus("Pick a face first");
            return;
        }

        IEnumerable<FaceRef> hits = how == "material"
            ? FaceEdit.WithMaterial(Ctx.Project, face.Material)
            : FaceEdit.Coplanar(Ctx.Project, f);
        Dictionary<string, Brush> brushes = FaceEdit.BrushIndex(Ctx.Project);
        Geo.SetFaces(hits.Where(h => brushes.TryGetValue(h.Brush, out Brush? b) && Viewports.ObjectFilter.IsPickable(Ctx.Project, b.Editor)).Append(f));
        Report();
    }

    /// <summary>Adds every face connected to the picked ones through faces of <paramref name="kind"/> (all, coplanar,
    /// walls, floors or slants: the stock Select Surfaces > Adjacent commands).</summary>
    public static void SelectAdjacent(string kind)
    {
        if (Geo.Faces.Count == 0)
        {
            Ctx.SetStatus("Pick a face first");
            return;
        }

        MapProject p = Ctx.Project;
        List<FaceRef> hits = SurfaceSelect.Adjacent(p, Geo.Faces, kind, b => Viewports.ObjectFilter.IsPickable(p, b.Editor));
        FaceRef? primary = Geo.PrimaryFace;
        Geo.SetFaces(primary is { } f ? hits.Where(h => h != f).Append(f) : hits);
        Report();
    }

    /// <summary>Picks every face of the visible, unlocked brushes (stock Select All Surfaces).</summary>
    public static void SelectAllSurfaces()
    {
        Geo.SetFaces(PickableFaces(Ctx.Project));
        Report();
    }

    /// <summary>Picks the faces of the visible, unlocked brushes that are not picked, and drops the picked ones (stock
    /// Select Surfaces > Reverse).</summary>
    public static void ReverseSelection()
    {
        IReadOnlyList<FaceRef> picked = Geo.Faces;
        var had = new HashSet<FaceRef>(picked);
        Geo.SetFaces(PickableFaces(Ctx.Project).Where(f => !had.Contains(f)).ToList());
        Report();
    }

    /// <summary>
    /// Builds the render from the brushes after asking (the read-only note of the Surface panel): the views and the
    /// build then draw the brushes, so surface edits show. The imported render and its lightmaps stop being used; the
    /// collision stays as it is. One undo step.
    /// </summary>
    public static void BuildRenderFromBrushes(Godot.Node parent)
    {
        if (Ctx.Project.Baked is not { KeepRender: true } || !Ctx.Project.Brushes.Any(BakedContent.IsBaked))
        {
            return;
        }

        Shell.Dialogs.Ask(parent, "Build Render from Brushes", "Build the map's render from its brushes?",
            "The views and the build will draw the map's brushes instead of the render imported with the map, so surface "
            + "edits show. The imported render's lightmaps are no longer used: the map is lit by its own lighting build, "
            + "which can look different from the original. The collision is kept as it is.\n\n"
            + "Undo (Ctrl+Z) brings the imported render back. Map Settings (Baked world) sets this too.",
            "Build from Brushes", () =>
            {
                Ctx.Execute(new ProjectSettingsCommand("Build render from brushes", p =>
                {
                    if (p.Baked is { } b)
                    {
                        b.KeepRender = false;
                    }
                }));
                Ctx.SetStatus("The render is built from the brushes: their surfaces can be edited now");
            }, icon: "warning");
    }

    private static IEnumerable<FaceRef> PickableFaces(MapProject p) =>
        FaceEdit.AllFaces(p, p.Brushes.Where(b => Viewports.ObjectFilter.IsPickable(p, b.Editor)).Select(b => b.Id));

    /// <summary>Selects the walls, floors or ceilings of the brushes the selected faces (or objects) belong to.</summary>
    public static void SelectBySlope(string kind)
    {
        List<string> brushes = Geo.Faces.Select(f => f.Brush)
            .Concat(Ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Brush).Select(r => r.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Geo.SetFaces(FaceEdit.BySlope(Ctx.Project, brushes, kind));
        Report();
    }

    /// <summary>Selects the brushes that own the selected faces (stock "Select Brush" from a surface).</summary>
    public static void SelectBrushes()
    {
        Ctx.Selection.Set(Geo.Faces.Select(f => f.Brush).Distinct(StringComparer.OrdinalIgnoreCase).Select(ObjectRef.Brush));
        Ctx.SetStatus($"Selected {Ctx.Selection.Count} brushes");
    }

    /// <summary>
    /// Moves the picked faces along their normals by <paramref name="distance"/> (negative pulls them in), starting
    /// from <paramref name="from"/> (a drag passes the brushes as they were when it began; null: the live brushes).
    /// Faces of one brush move one after another; face indexes stay put. One undo step (merged under a key).
    /// </summary>
    public static bool Extrude(double distance, IReadOnlyList<Brush>? from = null, IReadOnlyList<FaceRef>? faces = null, string? mergeKey = null)
    {
        IReadOnlyList<FaceRef> picked = faces ?? Geo.Faces;
        if (picked.Count == 0)
        {
            Ctx.SetStatus("Extrude: no faces selected (surface tool, U)");
            return false;
        }

        string name = distance >= 0 ? "Extrude faces" : "Push faces in";
        var edit = new ReplaceObjectsCommand(name, mergeKey);
        var warnings = new List<string>();
        try
        {
            Dictionary<string, Brush> live = FaceEdit.BrushIndex(Ctx.Project);
            foreach ((Brush brush, List<int> indexes) in FaceEdit.Group(Ctx.Project, picked))
            {
                Brush current = from?.FirstOrDefault(b => string.Equals(b.Id, brush.Id, StringComparison.OrdinalIgnoreCase)) ?? brush;
                foreach (int face in indexes)
                {
                    VertexEditResult r = OpenScct.Editor.Core.Geometry.Edit.Extrude.Face(current, face, distance);
                    current = r.Brush;
                    if (r.Warning is { } w)
                    {
                        warnings.Add(w);
                    }
                }

                edit.Brush(live[brush.Id].Clone(), current);
            }
        }
        catch (ArgumentException ex)
        {
            Ctx.Log($"{name}: {ex.Message}", Severity.Warning);
            return false;
        }

        Ctx.Execute(edit);
        Ctx.SetStatus(warnings.Count > 0 ? warnings[0] : $"{name} by {Math.Abs(distance):0.##}");
        return true;
    }

    public static void Report()
    {
        int n = Geo.Faces.Count;
        Ctx.SetStatus(n == 0 ? "No faces selected" : n == 1 ? $"Face {Geo.Faces[0]}" : $"{Panels.PanelUi.Count(n, "face")} selected");
    }

    private static bool Execute(string name, IReadOnlyList<FaceRef> faces, Func<IReadOnlyList<FaceRef>, ReplaceObjectsCommand> build, string note = "")
    {
        if (faces.Count == 0)
        {
            Ctx.SetStatus(Geo.BakedSurface is not null
                ? $"{name}: the picked surface is read-only, part of the imported baked world: {BuildRenderHint}"
                : $"{name}: no faces selected (surface tool, U)");
            return false;
        }

        List<FaceRef> editable = faces.Where(f => !IsLocked(f)).ToList();
        int locked = faces.Count - editable.Count;
        if (editable.Count == 0)
        {
            // Alt+click passes the face under the cursor, which need not be picked.
            string what = (ReferenceEquals(faces, Geo.Faces), faces.Count == 1) switch
            {
                (true, true) => "The picked face is",
                (true, false) => "The picked faces are",
                (false, true) => "That face is",
                _ => "Those faces are",
            };
            Ctx.SetStatus($"{name}: read-only. {what} part of the imported baked world: {BuildRenderHint}");
            return false;
        }

        try
        {
            ReplaceObjectsCommand cmd = build(editable);
            if (!cmd.IsEmpty)
            {
                Ctx.Execute(cmd);
                string skipped = locked > 0
                    ? $" ({locked} read-only face{(locked == 1 ? "" : "s")} of the imported baked world skipped; {BuildRenderHint}{note})"
                    : note.Length > 0 ? $" ({note[2..]})" : "";
                Ctx.SetStatus($"{name}: {editable.Count} face{(editable.Count == 1 ? "" : "s")}{skipped}");
            }

            return true;
        }
        catch (ArgumentException ex)
        {
            // A face that went away (an undo while it was picked): said where the user looks, not only in the log.
            Ctx.Log($"{name}: {ex.Message}", Severity.Warning);
            Ctx.SetStatus($"{name}: {ex.Message}");
            return false;
        }
    }
}
