using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// A trace of how the native reader sees an .sdc (<c>ose sdc-info</c>): container, summary, layout, actors by class,
/// brushes and faces, every Model, Polys and StaticMeshInstance export and whether it reads to its last byte, the
/// world model and the GEs.
/// </summary>
public static class SdcInfo
{
    public static SvJsonObject Inspect(string path, UeSchema? schema)
    {
        UPackage pkg = UPackage.Load(path);
        var o = new SvJsonObject();
        o.Add("file", SvJsonNode.String(Path.GetFullPath(path)));
        o.Add("package", SvJsonNode.String(pkg.Name));
        o.Add("version", SvJsonNode.Integer(pkg.Version));
        o.Add("licensee", SvJsonNode.Integer(pkg.Licensee));
        o.Add("flags", SvJsonNode.String($"0x{pkg.Flags:X}"));
        o.Add("chunks", SvJsonNode.Integer(pkg.Chunks));
        o.Add("unpacked", SvJsonNode.Integer(pkg.Data.Length));
        o.Add("obfuscated", SvJsonNode.Bool(pkg.Obfuscated));
        o.Add("recoveredSummary", SvJsonNode.Bool(pkg.Recovered));
        o.Add("names", SvJsonNode.Integer(pkg.Names.Count));
        o.Add("imports", SvJsonNode.Integer(pkg.Imports.Count));
        o.Add("exports", SvJsonNode.Integer(pkg.Exports.Count));

        SdcLayout layout = SdcLayoutDetector.Detect(pkg);
        o.Add("layout", SvJsonNode.String(layout == SdcLayout.Editor ? "editor" : "cooked"));
        o.Add("models", Exact(pkg, "Model", e => new UModelReader(layout).Read(pkg, e, summary: true)));
        o.Add("polys", Exact(pkg, "Polys", e => UPolysReader.Read(pkg, e)));
        o.Add("staticMeshInstances", Exact(pkg, "StaticMeshInstance", e => UStaticMeshInstanceReader.Read(pkg, e, layout)));

        SdcMap map = SdcMapReader.Read(pkg, schema);
        ULevel level = map.LevelRecord;
        var world = new SvJsonObject();
        world.Add("modelXbox", Name(pkg, level.ModelXbox));
        world.Add("modelPsx2", Name(pkg, level.ModelPsx2));
        world.Add("world", Name(pkg, level.WorldModel));
        o.Add("level", world);
        o.Add("levelActors", SvJsonNode.Integer(map.Actors.Count));
        o.Add("actorsByClass", Counts(map.Actors.Select(a => a.Class)));
        o.Add("actorsByCategory", Counts(map.Actors.Select(a => a.Category)));

        List<SdcActor> brushes = map.Actors.Where(IsCsgBrush).ToList();
        o.Add("brushes", SvJsonNode.Integer(brushes.Count));
        o.Add("builderBrushes", SvJsonNode.Integer(map.Actors.Count(a => IsBrushClass(a) && !IsCsgBrush(a))));
        o.Add("brushFaces", SvJsonNode.Integer(brushes.Sum(b => b.BrushPolys?.Count ?? 0)));
        o.Add("brushShapes", SvJsonNode.Integer(map.Actors.Count(a => !IsBrushClass(a) && a.BrushPolys is { Count: > 0 })));
        o.Add("orphanBrushes", SvJsonNode.Integer(map.OrphanBrushes));
        o.Add("deletedActors", SvJsonNode.Integer(map.DeletedActors));
        o.Add("ges", SvJsonNode.Integer(level.Ges.Count));

        var props = new SvJsonObject();
        props.Add("tags", SvJsonNode.Integer(map.Stats.Tags));
        props.Add("undeclared", SvJsonNode.Integer(map.Stats.Unknown));
        props.Add("raw", SvJsonNode.Integer(map.Stats.Raw));
        props.Add("sizeMismatch", SvJsonNode.Integer(map.Stats.SizeMismatch));
        props.Add("typeMismatch", SvJsonNode.Integer(map.Stats.TypeMismatch));
        props.Add("nonFinite", SvJsonNode.Integer(map.Stats.NonFinite));
        props.Add("actorProblems", new SvJsonArray(map.Actors.Where(a => a.Problem is not null).Take(20)
            .Select(a => SvJsonNode.String($"{a.Name}: {a.Problem}"))));
        props.Add("notes", new SvJsonArray(map.Stats.Notes.Take(20).Select(SvJsonNode.String)));
        o.Add("properties", props);
        return o;
    }

    /// <summary>A Brush-class actor (not a volume or mover).</summary>
    public static bool IsBrushClass(SdcActor a) => a.Class.Equals("Engine.Brush", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Brush actor that adds or subtracts (CsgOper 1 or 2); the builder brush is CSG_Active (0).</summary>
    public static bool IsCsgBrush(SdcActor a) =>
        IsBrushClass(a) && a.Props["CsgOper"] is SvJsonNumber n && n.TryGetInt32(out int oper) && oper is 1 or 2;

    private static SvJsonObject Exact(UPackage pkg, string className, Action<UExport> read)
    {
        int total = 0, exact = 0;
        var errors = new SvJsonArray();
        foreach (UExport e in pkg.ExportsOfClass(className).Where(e => e.Size > 0))
        {
            total++;
            try
            {
                read(e);
                exact++;
            }
            catch (InvalidDataException ex)
            {
                if (errors.Count < 10)
                {
                    errors.Add(SvJsonNode.String(ex.Message));
                }
            }
        }

        var o = new SvJsonObject();
        o.Add("total", SvJsonNode.Integer(total));
        o.Add("exact", SvJsonNode.Integer(exact));
        o.Add("errors", errors);
        return o;
    }

    private static SvJsonNode Name(UPackage pkg, int objref) => objref == 0 ? SvJsonNode.Null : SvJsonNode.String(pkg.ObjectName(objref));

    private static SvJsonObject Counts(IEnumerable<string> keys)
    {
        var o = new SvJsonObject();
        foreach (IGrouping<string, string> g in keys.GroupBy(k => k, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            o.Add(g.Key, SvJsonNode.Integer(g.Count()));
        }

        return o;
    }
}
