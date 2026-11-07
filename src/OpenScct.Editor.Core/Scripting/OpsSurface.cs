using OpenScct.Content.Json;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// The <c>surface</c> op: the face tool's surface edits for scripts, on the faces of target brushes.
/// <c>faces</c> picks them: a list of face indexes, or <c>"all"</c> (the default), <c>"walls"</c>, <c>"floors"</c>,
/// <c>"ceilings"</c>. Edits, applied in this order: <c>mat</c> (null: the default material), <c>flags</c> and
/// <c>clearFlags</c> (lists of names such as <c>"invisible"</c>, or numbers), <c>lightmap</c>, <c>align</c>
/// (<c>default</c>, <c>floor</c>, <c>wall</c>, <c>face</c>, <c>fit</c>, <c>cylinder</c>), <c>scale</c> (world units per
/// texel: a number or <c>[u,v]</c>), <c>rotate</c> (degrees from wall/floor alignment), <c>pan</c> (<c>[u,v]</c>
/// texels), <c>flip</c> (<c>"u"</c>, <c>"v"</c> or <c>"uv"</c>). Each target brush is one change of the document's
/// undo step.
/// </summary>
public static partial class ChangeApplier
{
    private static string[] SurfaceKeys =>
        ["id", "ids", "where", "faces", "mat", "flags", "clearFlags", "lightmap", "align", "scale", "rotate", "pan", "flip"];

    private static void Surface(ChangeContext ctx, SvJsonObject op)
    {
        List<ObjectRef> targets = ctx.Targets(op);
        foreach (ObjectRef r in targets.Where(r => r.Kind is not (ObjectKind.Brush or ObjectKind.Stair)))
        {
            ctx.Warn("id", $"{r.Id} is not a brush and has no faces");
        }

        (Func<BrushFace, ShapeTransform, BrushFace> perFace, bool cylinder, List<string> names) = SurfaceEdits(op);
        if (names.Count == 0)
        {
            throw new ApiException("give at least one of mat, flags, clearFlags, lightmap, align, scale, rotate, pan, flip", "op");
        }

        foreach (ObjectRef r in targets.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)))
        {
            Brush live = ctx.Project.FindBrush(r.Id)!;
            List<int> faces = SurfaceFaces(ctx.Project, live, op["faces"]);
            if (faces.Count == 0)
            {
                ctx.Warn("faces", $"{live.Id} has no such faces");
                continue;
            }

            ctx.Edit([r], $"Surface {live.Id}: {string.Join(", ", names)}", brush: b =>
            {
                Brush after = cylinder ? TextureOps.AlignCylinder(b, faces) : b;
                b.Shape = TextureOps.Apply(after, faces, perFace).Shape;
            });
        }
    }

    /// <summary>The op's surface edits composed into one per-face edit, whether it asks for cylinder alignment (a
    /// whole-brush edit run first), and the edits' names for the undo step.</summary>
    private static (Func<BrushFace, ShapeTransform, BrushFace> Edit, bool Cylinder, List<string> Names) SurfaceEdits(SvJsonObject op)
    {
        var steps = new List<Func<BrushFace, ShapeTransform, BrushFace>>();
        var names = new List<string>();
        bool cylinder = false;
        void Step(string name, Func<BrushFace, ShapeTransform, BrushFace> edit)
        {
            steps.Add(edit);
            names.Add(name);
        }

        try
        {
            if (op["mat"] is { } mat)
            {
                Step("material", FaceEdit.SetMaterial(mat is SvJsonString s ? s.Value : ApiJson.IsNull(mat) ? null
                    : throw new ApiException("mat is a material path or null", "mat")));
            }

            int set = SurfaceFlagBits(op["flags"], "flags"), clear = SurfaceFlagBits(op["clearFlags"], "clearFlags");
            if (op["flags"] is not null || op["clearFlags"] is not null)
            {
                Step("flags", FaceEdit.SetFlags(set, clear));
            }

            if (op["lightmap"] is { } lm)
            {
                Step("lightmap", FaceEdit.SetLightMapScale(ApiJson.RequireNumber(lm, "lightmap")));
            }

            if (ApiJson.GetString(op, "align") is { } align)
            {
                switch (align.ToLowerInvariant())
                {
                    case "default":
                        Step("align default", (f, t) => TextureOps.AlignDefault(f, t));
                        break;
                    case "floor":
                        Step("align floor", (f, t) => TextureOps.AlignFloor(f, t));
                        break;
                    case "wall":
                        Step("align wall", (f, t) => TextureOps.AlignWall(f, t));
                        break;
                    case "face":
                        Step("align face", (f, t) => TextureOps.AlignFace(f, t));
                        break;
                    case "fit":
                        Step("fit", FaceEdit.Fit());
                        break;
                    case "cylinder":
                        cylinder = true;
                        names.Add("align cylinder");
                        break;
                    default:
                        throw new ApiException($"align must be default, floor, wall, face, fit or cylinder, not '{align}'", "align");
                }
            }

            if (op["scale"] is { } scale)
            {
                Vec3 s = ApiJson.TryNumber(scale, out double u) ? new Vec3(u, u, 0) : TwoNumbers(scale, "scale");
                Step("scale", FaceEdit.SetScale(s.X, s.Y));
            }

            if (op["rotate"] is { } rotate)
            {
                Step("rotate", FaceEdit.SetRotation(ApiJson.RequireNumber(rotate, "rotate")));
            }

            if (op["pan"] is { } pan)
            {
                Vec3 p = TwoNumbers(pan, "pan");
                Step("pan", FaceEdit.SetPan((int)Math.Round(p.X), (int)Math.Round(p.Y)));
            }

            if (ApiJson.GetString(op, "flip") is { } flip)
            {
                string f = flip.ToLowerInvariant();
                if (f is not ("u" or "v" or "uv"))
                {
                    throw new ApiException($"flip must be u, v or uv, not '{flip}'", "flip");
                }

                Step("flip " + f, (face, _) =>
                {
                    BrushFace o = f.Contains('u') ? TextureOps.FlipU(face) : face;
                    return f.Contains('v') ? TextureOps.FlipV(o) : o;
                });
            }
        }
        catch (ArgumentException ex)
        {
            throw new ApiException(ex.Message, "op");
        }

        return ((face, t) => steps.Aggregate(face, (x, step) => step(x, t)), cylinder, names);
    }

    private static List<int> SurfaceFaces(MapProject p, Brush b, SvJsonNode? node)
    {
        switch (node)
        {
            case null:
            case SvJsonString { Value: "all" }:
                return Enumerable.Range(0, b.Shape.Faces.Count).ToList();
            case SvJsonString s:
                try
                {
                    return FaceEdit.BySlope(p, [b.Id], s.Value.ToLowerInvariant()).Select(f => f.Face).ToList();
                }
                catch (ArgumentException)
                {
                    throw new ApiException($"faces is a list of indexes, or all, walls, floors or ceilings, not '{s.Value}'", "faces");
                }

            case SvJsonArray list:
                var faces = new List<int>();
                foreach (SvJsonNode item in list)
                {
                    int i = (int)ApiJson.RequireNumber(item, "faces");
                    if (i < 0 || i >= b.Shape.Faces.Count)
                    {
                        throw new ApiException($"{b.Id} has no face {i} (it has {b.Shape.Faces.Count})", "faces");
                    }

                    faces.Add(i);
                }

                return faces;
            default:
                throw new ApiException("faces is a list of indexes, or all, walls, floors or ceilings", "faces");
        }
    }

    private static int SurfaceFlagBits(SvJsonNode? node, string field)
    {
        try
        {
            return node switch
            {
                null => 0,
                SvJsonArray list => list.Aggregate(0, (bits, item) => bits | (ApiJson.TryNumber(item, out double n) ? (int)n
                    : SurfaceFlags.Parse(item is SvJsonString s ? s.Value : throw new ApiException("flag names are strings", field)))),
                SvJsonString s => SurfaceFlags.Parse(s.Value),
                _ when ApiJson.TryNumber(node, out double n) => (int)n,
                _ => throw new ApiException("give flag names (a list or a comma-separated string) or a number", field),
            };
        }
        catch (ArgumentException ex)
        {
            throw new ApiException(ex.Message, field);
        }
    }

    private static Vec3 TwoNumbers(SvJsonNode node, string field) =>
        node is SvJsonArray { Count: 2 } a ? new Vec3(ApiJson.RequireNumber(a[0], field), ApiJson.RequireNumber(a[1], field), 0)
            : throw new ApiException($"{field} is [u,v]", field);
}
