using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Identity;

namespace OpenScct.Editor.Core.Assets;

/// <summary>The original engine's static colour approximation, shared by both original-game exporters.</summary>
public static class LegacyMaterialBake
{
    public static (double U, double V) TextureScale(LibraryMaterial? library)
    {
        if (library?.Texture is not { } file) return (1, 1);
        AuthoredMaterial? material = AuthoredMaterial.From(library.Flags["authored"]);
        file = material?.Textures.GetValueOrDefault("baseColor")?.File ?? file;
        var size = RgbaImage.SizeOf(File.ReadAllBytes(Import.TextureImporter.SafeFile(library.RenderDir, file)));
        if (size is null) return (1, 1);
        int width = size.Value.Width / (material?.Animation.Columns ?? 1), height = size.Value.Height / (material?.Animation.Rows ?? 1);
        return ((double)Math.Max(4, Interop.Original.DdsFile.PowerOfTwo(width)) / Math.Max(1, library.USize), (double)Math.Max(4, Interop.Original.DdsFile.PowerOfTwo(height)) / Math.Max(1, library.VSize));
    }

    public static Model.MapProject WithExportUvs(Model.MapProject source, AssetResolver resolver)
    {
        foreach (var face in source.Brushes.SelectMany(b => b.Shape.Faces).Concat(source.Actors.Where(a => a.Shape is not null).SelectMany(a => a.Shape!.Faces)))
            if (AuthoredMaterial.From(resolver.FindMaterial(face.Material ?? "")?.Flags["authored"]) is { Displacement: not 0 } m && (face.DisplacementCollision ?? m.DisplacementCollision))
                throw new InvalidOperationException((face.Material ?? "Surface") + ": disable displaced collision or substitute original-compatible geometry before original-game export.");
        foreach (var actor in source.Actors)
            if (Model.PropValues.AssetPath(actor.Props["StaticMesh"]) is { } path && resolver.FindMesh(path) is { } mesh && AuthoredMeshGeometry.HasCollision(AuthoredMeshGeometry.WithSkins(GlbCache.Load(mesh.GlbPath), actor, mesh.Mesh), mesh.Mesh, resolver, actor))
                throw new InvalidOperationException(actor.Id + ": disable displaced mesh collision or substitute original-compatible geometry before original-game export.");
        var result = Session.ProjectSnapshot.Take(source);
        foreach (var face in result.Brushes.SelectMany(b => b.Shape.Faces).Concat(result.Actors.Where(a => a.Shape is not null).SelectMany(a => a.Shape!.Faces)))
        {
            if (resolver.Project?.FindMaterial(face.Material ?? "") is not { } material) continue;
            var scale = TextureScale(material);
            var authored = AuthoredMaterial.From(material.Flags["authored"]) ?? new();
            double width = material.USize * scale.U, height = material.VSize * scale.V, cos = Math.Cos(authored.Rotation), sin = Math.Sin(authored.Rotation);
            Model.Vec3 u = face.TextureU / material.USize * authored.TileU, v = face.TextureV / material.VSize * authored.TileV;
            double panU = face.PanU / material.USize * authored.TileU + authored.OffsetU - .5, panV = face.PanV / material.VSize * authored.TileV + authored.OffsetV - .5;
            face.TextureU = (u * cos - v * sin) * width; face.TextureV = (u * sin + v * cos) * height;
            face.TextureBase = Geometry.TextureOps.FoldPans(face.TextureBase, face.TextureU, face.TextureV, (panU * cos - panV * sin + .5) * width, (panU * sin + panV * cos + .5) * height);
            face.PanU = 0; face.PanV = 0;
        }
        return result;
    }
    public static RgbaImage Base(LibraryMaterial library)
    {
        AuthoredMaterial? m = AuthoredMaterial.From(library.Flags["authored"]);
        if (m?.Problems() is { Count: > 0 } problems) throw new InvalidDataException(string.Join("; ", problems));
        string baseFile = m?.Textures.GetValueOrDefault("baseColor")?.File ?? library.Texture ?? throw new InvalidDataException("No base texture for " + library.Source);
        RgbaImage source = RgbaImage.Decode(File.ReadAllBytes(TextureImporter.SafeFile(library.RenderDir, baseFile)));
        if (m is null) return source;
        var result = new RgbaImage(source.Width / m.Animation.Columns, source.Height / m.Animation.Rows);
        var maps = m.Textures.Where(k => k.Key is "ao" or "opacity").ToDictionary(k => k.Key, k => new MaterialImage(File.ReadAllBytes(TextureImporter.SafeFile(library.RenderDir, k.Value.File))));
        double Scalar(string slot, int x, int y)
        {
            if (!maps.TryGetValue(slot, out var map)) return 1;
            var binding = m.Textures[slot];
            return map.Sample((x + .5) / result.Width / m.Animation.Columns, (y + .5) / result.Height / m.Animation.Rows, binding);
        }
        for (int y = 0; y < result.Height; y++) for (int x = 0; x < result.Width; x++)
        {
            int src = (y * source.Width + x) * 4, dst = (y * result.Width + x) * 4;
            double ao = 1 - m.AoStrength + m.AoStrength * Scalar("ao", x, y);
            for (int c = 0; c < 3; c++)
            {
                double value = source.Pixels[src + c] / 255.0;
                if (m.Textures.GetValueOrDefault("baseColor")?.Srgb != false) value = value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
                value *= m.BaseColor[c] * ao;
                value = value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
                result.Pixels[dst + c] = (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
            }
            result.Pixels[dst + 3] = m.Surface == "opaque" ? (byte)255 : (byte)Math.Clamp(Math.Round(source.Pixels[src + 3] * m.Opacity * Scalar("opacity", x, y)), 0, 255);
            if (m.Surface == "masked") result.Pixels[dst + 3] = result.Pixels[dst + 3] / 255.0 >= m.Cutoff ? (byte)255 : (byte)0;
        }
        return result;
    }
    public static RgbaImage FirstFrame(RgbaImage source, AuthoredMaterial material)
    {
        var result = new RgbaImage(source.Width / material.Animation.Columns, source.Height / material.Animation.Rows);
        for (int y = 0; y < result.Height; y++) source.Pixels.AsSpan(y * source.Width * 4, result.Width * 4).CopyTo(result.Pixels.AsSpan(y * result.Width * 4));
        return result;
    }
    public static IEnumerable<string> Differences(AuthoredMaterial m)
    {
        if (m.Textures.ContainsKey("normal") || m.BumpFromHeight || m.ParallaxDepth != 0 || m.Displacement != 0) yield return "normal/bump/parallax and visual displacement are flattened";
        if (m.Metallic > 0 || m.Clearcoat > 0 || m.Anisotropy > 0 || m.Refraction > 0 || m.Textures.ContainsKey("roughness")) yield return "PBR finishes use a legacy specular approximation";
        if (m.Animation.Frames > 1 || m.Animation.ScrollU != 0 || m.Animation.ScrollV != 0 || m.Animation.Rotate != 0) yield return "the legacy fallback uses the first animation frame";
        if (m.EmissionEnergy > 1) yield return "HDR emission is clamped to the original material range";
        if (m.Triplanar) yield return "triplanar projection uses the original surface UVs";
        if (m.DetailStrength > 0) yield return "detail layers are omitted from the original material";
    }
}
