using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScct.Editor.Core.Windows;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>Known original-game equivalents, applied only to a disposable export copy.</summary>
public static class SdcCompatibility
{
    public const string WindowMesh = "SGameplayObjects.Objects.F_vitreSolo01";
    public const string WindowFallback = "sdc.fallback.window";

    public static MapProject Prepare(MapProject project, string? contentRoot, string? gameRoot, List<Finding> findings)
    {
        if (!project.Actors.Any(a => a.Window is not null)) return project;
        var p = ProjectSnapshot.Take(project);
        var assets = AssetResolver.For(contentRoot, null);
        var stock = new SdcAssetImports(gameRoot);
        LibraryMesh? mesh = assets.FindViewMesh(WindowMesh);
        var used = p.AllRefs().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors.Where(a => a.Window is not null))
        {
            ObjectRef reference = ObjectRef.Actor(a.Id);
            try
            {
                var d = a.Window!;
                d.Validate();
                if (d.CutDepth > 0 && !WindowGeometry.CanCarve(p, d))
                    throw new ArgumentException("The window would cut retained baked geometry. Recover the map's brushes, or place the window in an existing opening.");
                if (stock.Find(WindowMesh) is not { ClassName: "StaticMesh" })
                    throw new ArgumentException($"The original-game window mesh {WindowMesh} is missing. Select an original game install containing SGameplayObjects.usx (--game or SVM_GAME_ROOT) and export again.");
                if (mesh is null)
                    throw new ArgumentException("The converted standard window mesh is missing. Build or import the original game's shared content, then export again.");

                var points = assets.Glb(mesh.GlbPath).ReadUeTriangles(mesh.Mesh).Positions;
                if (points.Count == 0) throw new ArgumentException("The standard window mesh has no vertices. Rebuild the shared content and export again.");
                Vec3 min = points.Aggregate(Vec3.Min), max = points.Aggregate(Vec3.Max);
                // The stock pane lies in X/Z, while authored windows lie in Y/Z.
                if (max.X - min.X <= .001 || max.Z - min.Z <= .001 || max.Y - min.Y > .01)
                    throw new ArgumentException("The standard window mesh has unexpected bounds. Rebuild the original shared content and export again.");
                var pane = WindowGeometry.Pane(d);
                (Vec3 lo, Vec3 hi) = pane.Bounds();
                // Permanent frame/cut brushes keep their authored transforms and order.
                if (!d.IsLowered)
                {
                    foreach (Brush part in WindowGeometry.Parts(a))
                    {
                        string stem = part.Id;
                        for (int suffix = 1; !used.Add(part.Id); suffix++) part.Id = stem + "_" + suffix;
                        foreach (BrushFace face in part.Shape.Faces)
                            if (face.Material == "Editor.WindowFrame") face.Material = null;
                        p.Brushes.Add(part);
                    }
                }
                Vec3 originalScale = a.DrawScale3D * a.DrawScale;
                Vec3 centre = (lo + hi) / 2;
                Vec3 location = a.LocalToWorld(centre);
                Rot rotation = Orientation.FromAxes(a.Rotation.Rotate(Vec3.UnitY), a.Rotation.Rotate(Vec3.UnitZ));
                a.DrawScale3D = new((hi.Y - lo.Y) / (max.X - min.X) * originalScale.Y,
                    originalScale.X, (hi.Z - lo.Z) / (max.Z - min.Z) * originalScale.Z);
                a.DrawScale = 1;
                a.PrePivot = (min + max) / 2;
                a.Location = location;
                a.Rotation = rotation;
                a.Class = "SBase.SVitre";
                a.Props.Set("StaticMesh", PropValues.AssetRef(WindowMesh));
                // Keep tags, events and breakable-glass settings; generated finishes have no stock counterpart.
                a.Props.Remove("Skins");
                a.Props.Remove("StaticMeshInstance");
                a.Props.Remove("StaticMeshInstanceOnXBOX");
                a.Props.Remove("StaticMeshInstanceOnPSX2");
                a.Window = null;
                findings.Add(Finding.Warning(WindowFallback,
                    $"{a.Id}: automatically replaced the editable {d.Shape}/{d.Finish} window with the original game's rectangular breakable window ({WindowMesh}), "
                    + "fitted to its pane bounds. Position, orientation, tags and events are preserved. Custom outline, thickness, tint and frosting use standard glass; "
                    + "frame/cut brushes are kept, with the standard material for generated frames. Check the fit of non-rectangular openings in the original game.", reference).At(location));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException)
            {
                findings.Add(Finding.Error("sdc.windowReplacement", $"{a.Id}: cannot create a compatible window. {ex.Message}", reference).At(a.Location));
            }
        }
        return p;
    }
}
