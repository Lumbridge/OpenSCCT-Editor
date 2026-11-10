using OpenScct.Content.Json;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Interop;

/// <summary>UnrealEd's CSG rebuild can use stale brush transform caches after loading a generated map.
/// Store transformed polygons with identity brush transforms so loading and rebuilding agree.</summary>
internal static class BrushTransformExport
{
    public static MapProject Prepare(MapProject project)
    {
        bool Needs(Brush b) => (b.Rotation != Rot.Zero || b.MainScale != Vec3.One || b.PostScale != Vec3.One || b.PrePivot != Vec3.Zero)
            && !Sheared(b.Props["MainScale"]) && !Sheared(b.Props["PostScale"]);
        if (!project.Brushes.Any(Needs)) return project;
        MapProject copy = ProjectSnapshot.Take(project);
        foreach (Brush b in copy.Brushes.Where(Needs))
        {
            ShapeTransform transform = ShapeTransform.For(b);
            foreach (BrushFace face in b.Shape.Faces)
            {
                face.Vertices = transform.FaceToWorld(face).Select(p => p - b.Location).ToList();
                face.TextureBase = transform.ToWorld(face.TextureBase) - b.Location;
                face.TextureU = transform.TextureAxisToWorld(face.TextureU);
                face.TextureV = transform.TextureAxisToWorld(face.TextureV);
            }
            b.Rotation = Rot.Zero;
            b.MainScale = b.PostScale = Vec3.One;
            b.PrePivot = Vec3.Zero;
            b.Props.Remove("MainScale"); b.Props.Remove("PostScale");
            foreach (string key in InteropValues.TransformProps) b.Props.Remove(key);
        }
        return copy;
    }

    private static bool Sheared(SvJsonNode? value) => value is SvJsonObject o && o["SheerRate"] is SvJsonNumber rate && PropValues.Num(rate) != 0;
}
