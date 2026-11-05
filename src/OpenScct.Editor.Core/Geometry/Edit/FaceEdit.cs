using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>
/// Surface edits over a set of faces that may span many brushes: material, poly flags, lightmap scale and every
/// texture operation of <see cref="TextureOps"/>. Each call builds one <see cref="ReplaceObjectsCommand"/> (one undo
/// step; give a merge key to fold a drag into one step). Faces of missing brushes or out of range are an error.
/// </summary>
public static class FaceEdit
{
    /// <summary>Default texture size when the content library does not know a material.</summary>
    public const int DefaultTextureSize = 256;

    /// <summary>Applies a per-face edit to the faces.</summary>
    public static ReplaceObjectsCommand Apply(MapProject p, IEnumerable<FaceRef> faces, string name,
        Func<BrushFace, ShapeTransform, BrushFace> op, string? mergeKey = null) =>
        ApplyBrush(p, faces, name, (b, idx) => TextureOps.Apply(b, idx, op), mergeKey);

    /// <summary>Applies an edit that needs the whole brush (cylinder alignment) to each brush and its chosen faces.</summary>
    public static ReplaceObjectsCommand ApplyBrush(MapProject p, IEnumerable<FaceRef> faces, string name,
        Func<Brush, IReadOnlyList<int>, Brush> op, string? mergeKey = null)
    {
        var cmd = new ReplaceObjectsCommand(name, mergeKey);
        foreach ((Brush brush, List<int> indexes) in Group(p, faces))
        {
            Brush after = op(brush, indexes);
            after.Id = brush.Id;
            cmd.Brush(brush.Clone(), after);
        }

        return cmd;
    }

    /// <summary>The faces grouped by brush, in map order. Throws for a missing brush or face.</summary>
    public static List<(Brush Brush, List<int> Faces)> Group(MapProject p, IEnumerable<FaceRef> faces)
    {
        Dictionary<string, Brush> brushes = BrushIndex(p);
        var byBrush = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (FaceRef f in faces)
        {
            Brush b = brushes.GetValueOrDefault(f.Brush) ?? throw new ArgumentException($"no brush '{f.Brush}'");
            if (f.Face < 0 || f.Face >= b.Shape.Faces.Count)
            {
                throw new ArgumentException($"brush {b.Id} has no face {f.Face} (it has {b.Shape.Faces.Count})");
            }

            if (!byBrush.TryGetValue(b.Id, out List<int>? list))
            {
                byBrush[b.Id] = list = new List<int>();
            }

            if (!list.Contains(f.Face))
            {
                list.Add(f.Face);
            }
        }

        return p.Brushes.Where(b => byBrush.ContainsKey(b.Id)).Select(b => (b, byBrush[b.Id])).ToList();
    }

    /// <summary>The map's brushes by id (ignoring case), for edits over many faces: <see cref="MapProject.FindBrush"/>
    /// is a linear search.</summary>
    public static Dictionary<string, Brush> BrushIndex(MapProject p)
    {
        var index = new Dictionary<string, Brush>(p.Brushes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (Brush b in p.Brushes)
        {
            index.TryAdd(b.Id, b);
        }

        return index;
    }

    /// <summary>The face a reference names, or null.</summary>
    public static BrushFace? Find(MapProject p, FaceRef f) =>
        p.FindBrush(f.Brush) is { } b && f.Face >= 0 && f.Face < b.Shape.Faces.Count ? b.Shape.Faces[f.Face] : null;

    /// <summary>Every face of the given brushes.</summary>
    public static IEnumerable<FaceRef> AllFaces(MapProject p, IEnumerable<string> brushIds) =>
        brushIds.Select(p.FindBrush).OfType<Brush>().SelectMany(b => Enumerable.Range(0, b.Shape.Faces.Count).Select(i => new FaceRef(b.Id, i)));

    // ---- per-face edits ---------------------------------------------------------------------------------------

    public static Func<BrushFace, ShapeTransform, BrushFace> SetMaterial(string? material) => (f, _) =>
    {
        BrushFace o = f.Clone();
        o.Material = string.IsNullOrWhiteSpace(material) ? null : material.Trim();
        return o;
    };

    /// <summary>Turns flag bits on (<paramref name="set"/>) and off (<paramref name="clear"/>).</summary>
    public static Func<BrushFace, ShapeTransform, BrushFace> SetFlags(int set, int clear) => (f, _) =>
    {
        BrushFace o = f.Clone();
        o.Flags = (f.Flags & ~clear) | set;
        return o;
    };

    public static Func<BrushFace, ShapeTransform, BrushFace> SetLightMapScale(double scale)
    {
        if (scale <= 0 || !double.IsFinite(scale))
        {
            throw new ArgumentException("lightmap scale must be positive");
        }

        return (f, _) =>
        {
            BrushFace o = f.Clone();
            o.LightMapScale = scale;
            return o;
        };
    }

    /// <summary>Sets the pan absolutely (null keeps that axis).</summary>
    public static Func<BrushFace, ShapeTransform, BrushFace> SetPan(int? u, int? v) => (f, _) =>
    {
        BrushFace o = f.Clone();
        o.PanU = u ?? f.PanU;
        o.PanV = v ?? f.PanV;
        return o;
    };

    /// <summary>Sets the texture scale absolutely (world units per texel; null keeps that axis).</summary>
    public static Func<BrushFace, ShapeTransform, BrushFace> SetScale(double? u, double? v) => (f, t) =>
    {
        SurfaceInfo info = SurfaceInfo.Of(f, t);
        return TextureOps.ScaleTo(f, t, u ?? info.ScaleU, v ?? info.ScaleV);
    };

    /// <summary>Sets the rotation absolutely (degrees, as <see cref="SurfaceInfo.Rotation"/> reports it).</summary>
    public static Func<BrushFace, ShapeTransform, BrushFace> SetRotation(double degrees) => (f, t) =>
        TextureOps.Rotate(f, t, degrees - SurfaceInfo.Of(f, t).Rotation);

    /// <summary>Fit using the material's texture size from the content library (or 256x256).</summary>
    public static Func<BrushFace, ShapeTransform, BrushFace> Fit(double repeatU = 1, double repeatV = 1) => (f, t) =>
    {
        (int w, int h) = TextureSize(f.Material);
        return TextureOps.Fit(f, t, w, h, repeatU, repeatV);
    };

    /// <summary>The texture size of a material from the content library, or 256x256.</summary>
    public static (int Width, int Height) TextureSize(string? material)
    {
        if (material is null || Services.EditorServices.ContentRoot is not { } root
            || ContentLibrary.For(root).FindMaterial(material) is not { USize: > 0, VSize: > 0 } m)
        {
            return (DefaultTextureSize, DefaultTextureSize);
        }

        return (m.USize, m.VSize);
    }

    // ---- reading ----------------------------------------------------------------------------------------------

    /// <summary>The materials the map's brushes use, most used first (null is the default material).</summary>
    public static List<(string? Material, int Faces)> MaterialsUsed(MapProject p) =>
        p.Brushes.SelectMany(b => b.Shape.Faces).GroupBy(f => f.Material, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Every face of the map with this material (ignoring case).</summary>
    public static IEnumerable<FaceRef> WithMaterial(MapProject p, string? material) =>
        p.Brushes.SelectMany(b => b.Shape.Faces.Select((f, i) => (f, i))
            .Where(x => string.Equals(x.f.Material, material, StringComparison.OrdinalIgnoreCase))
            .Select(x => new FaceRef(b.Id, x.i)));

    /// <summary>Every face of the map lying in the same world plane as <paramref name="face"/>, facing the same way.</summary>
    public static IEnumerable<FaceRef> Coplanar(MapProject p, FaceRef face, double tolerance = 0.5)
    {
        if (p.FindBrush(face.Brush) is not { } src || face.Face >= src.Shape.Faces.Count
            || Plane3.FromPolygon(ShapeTransform.For(src).FaceToWorld(src.Shape.Faces[face.Face])) is not { } plane)
        {
            yield break;
        }

        foreach (Brush b in p.Brushes)
        {
            ShapeTransform t = ShapeTransform.For(b);
            for (int i = 0; i < b.Shape.Faces.Count; i++)
            {
                if (Plane3.FromPolygon(t.FaceToWorld(b.Shape.Faces[i])) is { } q && q.NearlyEquals(plane, 1e-3, tolerance))
                {
                    yield return new FaceRef(b.Id, i);
                }
            }
        }
    }

    /// <summary>Faces of the given brushes by slope: walls (|n.z| &lt; 0.3), floors (n.z &gt; 0.7 seen from the room
    /// side) or ceilings. A subtractive brush's faces bound empty space, so its floor faces point down.</summary>
    public static IEnumerable<FaceRef> BySlope(MapProject p, IEnumerable<string> brushIds, string kind)
    {
        foreach (Brush b in brushIds.Select(p.FindBrush).OfType<Brush>())
        {
            ShapeTransform t = ShapeTransform.For(b);
            double sign = b.Oper == CsgOper.Subtract ? -1 : 1;
            for (int i = 0; i < b.Shape.Faces.Count; i++)
            {
                double z = Poly.Normal(t.FaceToWorld(b.Shape.Faces[i])).Z * sign;
                bool match = kind switch
                {
                    "wall" or "walls" => Math.Abs(z) < 0.3,
                    "floor" or "floors" => z > 0.7,
                    "ceiling" or "ceilings" => z < -0.7,
                    _ => throw new ArgumentException($"slope kind must be walls, floors or ceilings, not '{kind}'"),
                };
                if (match)
                {
                    yield return new FaceRef(b.Id, i);
                }
            }
        }
    }
}
