using OpenScct.Content.Json;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>One brush a shape spec makes: its shape, its CSG op when the builder decides it, and its offset from the
/// piece's location (multi-brush pieces such as blockout rooms).</summary>
internal sealed record ShapePart(BrushShape Shape, CsgOper? Oper, Vec3 Offset);

/// <summary>
/// Reads brush shape specs: a builder kind (<c>"cube"</c>) or an object: <c>{"kind":"cylinder","radius":256,...}</c>
/// (any registered builder and its parameters), <c>{"size":[x,y,z]}</c> (a box), <c>{"min":[..],"max":[..]}</c> (a box
/// by world corners, which also places it), or <c>{"faces":[[[x,y,z],...],...]}</c> (a convex polyhedron in local space).
/// </summary>
internal static class BrushSpecs
{
    /// <param name="body">The map's body numbers: blockout pieces take their body-following defaults from them.</param>
    public static (List<ShapePart> Parts, Vec3? Center) Build(SvJsonNode spec, string field, DesignNumbers body, BuilderSpec? merge = null)
    {
        if (spec is SvJsonString kindOnly)
        {
            return ([.. FromBuilder(new SvJsonObject(), kindOnly.Value, field, merge, body)], null);
        }

        if (spec is not SvJsonObject o)
        {
            throw new ApiException("a shape is a builder kind (\"cube\") or an object ({\"size\":[x,y,z]}, {\"min\":..,\"max\":..}, {\"kind\":..,...}, {\"faces\":[...]})", field);
        }

        if (o["faces"] is { } faces)
        {
            return ([new ShapePart(Faces(faces, field + ".faces"), null, Vec3.Zero)], null);
        }

        if (o["min"] is not null || o["max"] is not null)
        {
            Vec3 a = ApiJson.RequireVec(o["min"], field + ".min"), b = ApiJson.RequireVec(o["max"], field + ".max");
            Vec3 min = Vec3.Min(a, b), max = Vec3.Max(a, b);
            Vec3 size = max - min;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
            {
                throw new ApiException("min and max must differ on every axis", field);
            }

            var cube = new SvJsonObject { { "breadth", ApiJson.Num(size.X) }, { "width", ApiJson.Num(size.Y) }, { "height", ApiJson.Num(size.Z) } };
            return ([.. FromBuilder(cube, "cube", field, null, body)], (min + max) / 2);
        }

        string kind = ApiJson.GetString(o, "kind") ?? merge?.Kind ?? "cube";
        var parms = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            if (kv.Key is "kind")
            {
                continue;
            }

            if (kv.Key == "size")
            {
                Vec3 s = kv.Value is SvJsonNumber ? Vec3.One * ApiJson.RequireNumber(kv.Value, field + ".size") : ApiJson.RequireVec(kv.Value, field + ".size");
                parms.Add("breadth", ApiJson.Num(s.X));
                parms.Add("width", ApiJson.Num(s.Y));
                parms.Add("height", ApiJson.Num(s.Z));
                continue;
            }

            parms.Add(kv.Key, kv.Value);
        }

        return ([.. FromBuilder(parms, kind, field, string.Equals(merge?.Kind, kind, StringComparison.OrdinalIgnoreCase) ? merge : null, body)], null);
    }

    /// <summary>Runs a registered builder (or the built-in box for <c>cube</c>), with parameters checked by name.</summary>
    private static IEnumerable<ShapePart> FromBuilder(SvJsonObject parms, string kind, string field, BuilderSpec? merge, DesignNumbers body)
    {
        IShapeBuilder? builder = EditorServices.ShapeBuilder(kind);
        BuilderSpec spec;
        if (builder is not null)
        {
            spec = builder.Defaults(builder.Kinds.First(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)));
            if (Geometry.Builders.BuilderCatalog.Find(spec.Kind) is { } known)
            {
                foreach ((string name, double value) in known.BodyDefaults(body))
                {
                    spec.Numbers[name] = value;
                }
            }
        }
        else if (string.Equals(kind, "cube", StringComparison.OrdinalIgnoreCase))
        {
            spec = new BuilderSpec("cube") { Numbers = { ["breadth"] = 256, ["width"] = 256, ["height"] = 256 } };
        }
        else
        {
            string kinds = string.Join(", ", new[] { "cube" }.Concat(EditorServices.ShapeBuilders.SelectMany(b => b.Kinds)).Distinct(StringComparer.OrdinalIgnoreCase));
            throw new ApiException($"no brush builder '{kind}' (have: {kinds})", field + ".kind");
        }

        if (merge is not null)
        {
            foreach (KeyValuePair<string, double> kv in merge.Numbers)
            {
                spec.Numbers[kv.Key] = kv.Value;
            }

            foreach (KeyValuePair<string, string> kv in merge.Strings)
            {
                spec.Strings[kv.Key] = kv.Value;
            }
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in parms)
        {
            string key = spec.Numbers.Keys.Concat(spec.Strings.Keys).FirstOrDefault(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiException($"builder '{spec.Kind}' has no parameter '{kv.Key}' (has: {string.Join(", ", spec.Numbers.Keys.Concat(spec.Strings.Keys))})",
                    $"{field}.{kv.Key}");
            if (spec.Numbers.ContainsKey(key))
            {
                spec.Numbers[key] = ApiJson.RequireNumber(kv.Value, $"{field}.{kv.Key}");
            }
            else
            {
                spec.Strings[key] = kv.Value is SvJsonString s ? s.Value : throw new ApiException("expected a string", $"{field}.{kv.Key}");
            }
        }

        if (builder is null)
        {
            var size = new Vec3(spec.Get("breadth", 256), spec.Get("width", 256), spec.Get("height", 256));
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
            {
                throw new ApiException("a cube needs positive breadth, width and height", field);
            }

            yield return new ShapePart(Box.Shape(size), null, Vec3.Zero);
            yield break;
        }

        IReadOnlyList<(BrushShape Shape, CsgOper Oper, Vec3 Offset)> built;
        try
        {
            built = builder.Build(spec);
        }
        catch (ArgumentException ex)
        {
            throw new ApiException($"builder '{spec.Kind}': {ex.Message}", field);
        }

        if (built.Count == 0)
        {
            throw new ApiException($"builder '{spec.Kind}' made no brush from these parameters", field);
        }

        foreach ((BrushShape shape, CsgOper oper, Vec3 offset) in built)
        {
            shape.Builder ??= spec.Clone();
            yield return new ShapePart(shape, built.Count > 1 ? oper : null, offset);
        }
    }

    /// <summary>A convex polyhedron from face polygons; windings are turned to face outwards.</summary>
    private static BrushShape Faces(SvJsonNode node, string field)
    {
        if (node is not SvJsonArray list || list.Count < 4)
        {
            throw new ApiException("faces is a list of at least 4 polygons, each [[x,y,z],...] or {\"verts\":[...],\"mat\":...}", field);
        }

        var shape = new BrushShape();
        for (int i = 0; i < list.Count; i++)
        {
            string f = $"{field}[{i}]";
            SvJsonNode verts = list[i] is SvJsonObject fo ? fo["verts"] ?? throw new ApiException("missing verts", f) : list[i];
            if (verts is not SvJsonArray va || va.Count < 3)
            {
                throw new ApiException("a face needs at least 3 vertices", f);
            }

            BrushFace face = Box.Face(list[i] is SvJsonObject m ? ApiJson.GetString(m, "mat") : null,
                va.Select((v, k) => ApiJson.RequireVec(v, $"{f}[{k}]")).ToArray());
            if (face.Normal() == Vec3.Zero)
            {
                throw new ApiException("the face is degenerate (no area)", f);
            }

            shape.Faces.Add(face);
        }

        Vec3 c = Vec3.Zero;
        int n = 0;
        foreach (Vec3 v in shape.Faces.SelectMany(x => x.Vertices))
        {
            c += v;
            n++;
        }

        c /= n;
        foreach (BrushFace face in shape.Faces)
        {
            Vec3 fc = face.Vertices.Aggregate(Vec3.Zero, (a, b) => a + b) / face.Vertices.Count;
            if (Vec3.Dot(face.Normal(), fc - c) < 0)
            {
                face.Vertices.Reverse();
                TextureAlign.Planar(face);
            }
        }

        return shape;
    }

    /// <summary>Applies <c>mat</c> (every face) and <c>mats</c> (<c>top</c>/<c>bottom</c>/<c>sides</c> by outward normal,
    /// or face indexes) to a shape.</summary>
    public static void Materials(SvJsonObject op, BrushShape shape, string field = "")
    {
        if (ApiJson.GetString(op, "mat") is { } all)
        {
            foreach (BrushFace f in shape.Faces)
            {
                f.Material = all;
            }
        }

        if (op["mats"] is not SvJsonObject mats)
        {
            return;
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in mats)
        {
            string mat = kv.Value is SvJsonString s ? s.Value : throw new ApiException("expected a material path", $"{field}mats.{kv.Key}");
            Func<int, BrushFace, bool> pick = kv.Key.ToLowerInvariant() switch
            {
                "top" => (_, f) => f.Normal().Z > 0.7,
                "bottom" => (_, f) => f.Normal().Z < -0.7,
                "sides" => (_, f) => Math.Abs(f.Normal().Z) <= 0.7,
                _ when int.TryParse(kv.Key, out int idx) && idx >= 0 && idx < shape.Faces.Count => (i, _) => i == idx,
                _ => throw new ApiException($"unknown face group '{kv.Key}' (top, bottom, sides or a face index 0..{shape.Faces.Count - 1})", $"{field}mats.{kv.Key}"),
            };
            for (int i = 0; i < shape.Faces.Count; i++)
            {
                if (pick(i, shape.Faces[i]))
                {
                    shape.Faces[i].Material = mat;
                }
            }
        }
    }

    private static readonly (string Name, int Bit)[] s_flagNames =
    [
        ("invisible", PolyFlags.Invisible), ("masked", PolyFlags.Masked), ("translucent", PolyFlags.Translucent),
        ("notsolid", PolyFlags.NotSolid), ("environment", PolyFlags.Environment), ("semisolid", PolyFlags.Semisolid),
        ("modulated", PolyFlags.Modulated), ("fakebackdrop", PolyFlags.FakeBackdrop), ("twosided", PolyFlags.TwoSided),
        ("nosmooth", PolyFlags.NoSmooth), ("speciallit", PolyFlags.SpecialLit), ("unlit", PolyFlags.Unlit),
        ("mirrored", PolyFlags.Mirrored), ("portal", PolyFlags.Portal), ("antiportal", PolyFlags.AntiPortal),
    ];

    /// <summary>Poly flags from a number or a list of names (semisolid, notsolid, portal...).</summary>
    public static int Flags(SvJsonNode node, string field)
    {
        if (ApiJson.TryNumber(node, out double n))
        {
            return (int)n;
        }

        int bits = 0;
        foreach (string name in ApiJson.StringList(node, field) ?? [])
        {
            (string Name, int Bit) hit = s_flagNames.FirstOrDefault(x => x.Name.Equals(name.Replace("_", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
            bits |= hit.Name is not null ? hit.Bit : throw new ApiException($"unknown flag '{name}' ({string.Join(", ", s_flagNames.Select(x => x.Name))})", field);
        }

        return bits;
    }

    public static IEnumerable<string> FlagNames(int bits) => s_flagNames.Where(x => (bits & x.Bit) != 0).Select(x => x.Name);

    public static CsgOper Csg(SvJsonNode? node, CsgOper fallback, string field) => node switch
    {
        null => fallback,
        SvJsonString { Value: "add" or "Add" or "solid" } => CsgOper.Add,
        SvJsonString { Value: "subtract" or "Subtract" or "sub" or "carve" } => CsgOper.Subtract,
        _ => throw new ApiException("csg is \"add\" or \"subtract\"", field),
    };
}
