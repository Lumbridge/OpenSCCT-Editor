using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>
/// Turns a T3D map into a project: the LevelInfo into <see cref="MapProject.LevelInfo"/>, <c>Brush</c> actors into CSG
/// brushes (the first one, the builder brush, is skipped), every other actor into an <see cref="Actor"/> with all its
/// stored properties, brush models of volumes and movers into <see cref="Actor.Shape"/>, and the GE list into GEs.
/// What the model does not hold is kept so a T3D export writes it back: an actor's sub-objects (emitters' particle
/// systems) travel with the actor in its <see cref="Actor.Source"/> (so they survive renames and copies), and unknown
/// map blocks, reference classes and the LevelInfo name go under <c>Extra["t3d"]</c>.
/// </summary>
public static class T3dImport
{
    /// <summary>The key of the T3D round-trip data in <see cref="MapProject.Extra"/>.</summary>
    public const string ExtraKey = "t3d";

    /// <summary>Property lines the importer does not keep: the name is the id, and the brush model is the shape.</summary>
    private static readonly HashSet<string> s_dropped = new(StringComparer.OrdinalIgnoreCase) { "Name" };

    /// <summary>The key of an actor's T3D sub-objects in its <see cref="Actor.Source"/> JSON.</summary>
    public const string ObjectsKey = "t3dObjects";

    /// <summary>The editor's own GE ADD keys (GE ID, platform, flag word), which the stock command skips.</summary>
    public const string GeMaskKey = "MASK", GePlatformKey = "PLATFORM", GeFlagsKey = "FLAGS";

    /// <param name="hasBuilder">True for a whole map (its first Brush actor is the builder brush and is skipped); false
    /// for a selection (Edit &gt; Copy text), where every Brush actor is a CSG brush.</param>
    /// <param name="otherActors">Actors outside the file that its <c>MyLevel.X</c> references may name (the open map's,
    /// when the file is added to it).</param>
    public static MapProject ToProject(T3dDocument doc, string mapId, UeSchema? schema, List<Finding> findings, bool hasBuilder = true,
        IEnumerable<string>? otherActors = null)
    {
        var types = new T3dTypes(schema, mapId);
        types.ActorNames.UnionWith(doc.Actors.Select(a => a.Name));
        types.ActorNames.UnionWith(otherActors ?? []);

        var p = new MapProject { Id = mapId, Title = mapId };
        p.Layers.Add(new Layer("Default"));
        var extra = new SvJsonObject();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool builderSeen = !hasBuilder;
        int unknownClasses = 0;

        foreach (T3dActor a in doc.Actors)
        {
            string cls = InteropValues.ResolveClass(schema, a.Class);
            if (schema is not null && schema.ResolveClass(a.Class) is null)
            {
                unknownClasses++;
                findings.Add(Finding.Warning("import.unknownClass", $"actor {a.Name}: class '{a.Class}' is not in the class table", ObjectRef.Actor(a.Name)));
                if (!a.Class.Contains('.', StringComparison.Ordinal))
                {
                    // A class the class data does not know is defined in the map: the stock import finds it in the map's
                    // own package.
                    cls = $"{mapId}.{a.Class}";
                }
            }

            IEnumerable<T3dField> fields = a.Props.Where(f => !s_dropped.Contains(f.Name));
            string shortClass = InteropValues.ShortClass(cls);
            // The first LevelInfo is the map's (the export writes it first); later ones are copies placed as actors.
            if (IsLevelInfo(cls, schema) && !extra.ContainsKey("levelInfo"))
            {
                p.LevelInfo = types.ToProps(cls, fields);
                p.Title = p.LevelInfo.GetString("Title") is { Length: > 0 } title ? title : mapId;
                extra.Add("levelInfo", SvJsonNode.String(a.Name));
                continue;
            }

            if (shortClass.Equals("Brush", StringComparison.OrdinalIgnoreCase))
            {
                if (!builderSeen)
                {
                    builderSeen = true;
                    continue;
                }

                if (ToBrush(a, cls, fields, types, findings) is { } brush)
                {
                    brush.Id = UniqueId(brush.Id, used, findings);
                    // The shape as the file had it, so the round-trip lint can tell a face the source already had.
                    var t3d = new SvJsonObject();
                    t3d.Add("hash", SvJsonNode.String(Compile.BakedContent.Hash(brush)));
                    var src = new SvJsonObject();
                    src.Add("t3d", t3d);
                    brush.Source = InteropValues.Compact(src);
                    p.Brushes.Add(brush);
                }

                continue;
            }

            Actor actor = ToActor(a, cls, fields, types);
            actor.Id = UniqueId(actor.Id, used, findings);
            p.Actors.Add(actor);
        }

        ReadGes(doc, p, findings);

        if (doc.OtherBlocks.Count > 0)
        {
            extra.Add("blocks", new SvJsonArray(doc.OtherBlocks.Select(b => (SvJsonNode)new SvJsonArray(b.Select(SvJsonNode.String)))));
        }

        if (types.RefClasses.Count > 0)
        {
            // [path, class] pairs, not an object: SVMap object keys must be ASCII and asset paths need not be.
            var refs = new SvJsonArray(types.RefClasses.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (SvJsonNode)new SvJsonArray([SvJsonNode.String(kv.Key), SvJsonNode.String(kv.Value)])));

            extra.Add("refClasses", refs);
        }

        p.Extra.Add(ExtraKey, extra);
        CameraFromLevelInfo(p);

        if (schema is null)
        {
            findings.Add(Finding.Info("import.noSchema", "no shared/classes.json found: property types were guessed from the text and enums kept as names"));
        }

        foreach (string problem in doc.Problems.Take(50))
        {
            findings.Add(Finding.Warning("import.t3dSyntax", problem));
        }

        if (doc.Problems.Count > 50)
        {
            findings.Add(Finding.Warning("import.t3dSyntax", $"{doc.Problems.Count - 50} more syntax problems not listed"));
        }

        if (!builderSeen)
        {
            findings.Add(Finding.Info("import.noBuilder", "the file has no builder brush (no Brush actor)"));
        }

        findings.Add(Finding.Info("import.summary",
            $"{p.Brushes.Count} brushes, {p.Actors.Count} actors, {p.Ges.Count} GEs" + (unknownClasses > 0 ? $", {unknownClasses} unknown classes" : "")));
        return p;
    }

    /// <summary>True for the LevelInfo actor (and its subclasses).</summary>
    public static bool IsLevelInfo(string className, UeSchema? schema) =>
        InteropValues.ShortClass(className).Equals("LevelInfo", StringComparison.OrdinalIgnoreCase)
        || (schema?.IsA(className, "Engine.LevelInfo") ?? false);

    private static Brush? ToBrush(T3dActor a, string cls, IEnumerable<T3dField> fields, T3dTypes types, List<Finding> findings) =>
        BrushImport.ToBrush(a.Name, cls, types.ToProps(cls, fields), a.Brush is null ? null : ShapeOf(a.Brush, types), findings);

    private static Actor ToActor(T3dActor a, string cls, IEnumerable<T3dField> fields, T3dTypes types)
    {
        var actor = new Actor(a.Name, cls);
        PropSet props = types.ToProps(cls, fields);
        InteropValues.TakeTransform(props, actor);
        if (a.Brush is not null)
        {
            actor.Shape = ShapeOf(a.Brush, types);
        }

        actor.Props = props;
        if (a.Objects.Count > 0)
        {
            var src = new SvJsonObject();
            src.Add(ObjectsKey, new SvJsonArray(a.Objects.Select(o => (SvJsonNode)new SvJsonArray(o.Select(SvJsonNode.String)))));
            actor.Source = InteropValues.Compact(src);
        }

        // The shape as the file had it, so the round-trip lint can tell a face the source already had.
        Compile.BakedContent.MarkShape(actor);
        return actor;
    }

    /// <summary>The T3D sub-object blocks kept in an actor's source (raw lines, Begin and End included).</summary>
    public static List<List<string>> ObjectsOf(Actor actor)
    {
        var blocks = new List<List<string>>();
        if (actor.Source is not { Length: > 0 } src || src[0] != '{' || !src.Contains(ObjectsKey, StringComparison.Ordinal))
        {
            return blocks;
        }

        try
        {
            if (SvJson.Parse(src) is SvJsonObject o && o[ObjectsKey] is SvJsonArray list)
            {
                blocks.AddRange(list.OfType<SvJsonArray>().Select(b => b.OfType<SvJsonString>().Select(s => s.Value).ToList()));
            }
        }
        catch (OpenScct.Content.SvMapFormatException)
        {
            // A hand-edited source that is not JSON carries no sub-objects.
        }

        return blocks;
    }

    /// <summary>A brush's faces; a level-local material (<c>MyLevel.X</c>) becomes the map's own (<c>&lt;Map&gt;.X</c>),
    /// as property references do.</summary>
    private static BrushShape ShapeOf(T3dBrush brush, T3dTypes types)
    {
        var shape = new BrushShape();
        foreach (T3dPolygon poly in brush.Polygons)
        {
            shape.Faces.Add(new BrushFace
            {
                Vertices = new List<Vec3>(poly.Vertices),
                Material = poly.Texture is { } texture ? types.FromLevelPath(texture) : null,
                Flags = poly.Flags,
                TextureBase = poly.Origin,
                TextureU = poly.TextureU,
                TextureV = poly.TextureV,
                PanU = poly.PanU,
                PanV = poly.PanV,
                LightMapScale = poly.LightMapScale ?? 32,
                LightMapScalePsx2 = poly.LightMapScalePsx2 is float ps2 && ps2 != (poly.LightMapScale ?? 32) ? ps2 : null,
                ItemName = poly.Item,
                Link = poly.Link ?? -1,
            });
        }

        return shape;
    }

    /// <summary>
    /// <c>GE ADD KIND X1= Y1= Z1= X2= Y2= Z2= [X3= Y3= Z3=] NOSLIDE= NOHOIST= NOGETOUT=</c>. The stock GE ADD carries
    /// no GE ID or platform, so its GEs are always on (mask 0); the editor's own export adds them (MASK=, PLATFORM=,
    /// FLAGS=). Options other than UNUSED are kept in the GE's source.
    /// </summary>
    private static void ReadGes(T3dDocument doc, MapProject p, List<Finding> findings)
    {
        bool ownKeys = false;
        for (int i = 0; i < doc.GeLines.Count; i++)
        {
            string[] parts = doc.GeLines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[0].Equals("GE", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("ADD", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding.Warning("import.geSyntax", $"GE line {i + 1}: not a GE ADD line"));
                continue;
            }

            string kind = parts[2].ToUpperInvariant();
            if (!GeKinds.All.Contains(kind))
            {
                findings.Add(Finding.Warning("import.geKind", $"GE line {i + 1}: unknown kind '{parts[2]}'"));
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in parts.Skip(3))
            {
                int eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    values[part[..eq]] = part[(eq + 1)..];
                }
            }

            var ge = new Ge($"GE{i.ToString(CultureInfo.InvariantCulture)}", kind);
            int count = GeKinds.HasThirdPoint(kind) ? 3 : 2;
            for (int n = 1; n <= count; n++)
            {
                ge.Points.Add(new Vec3(Coord(values, "X", n), Coord(values, "Y", n), Coord(values, "Z", n)));
            }

            ge.Mask = TakeInt(values, GeMaskKey, ref ownKeys);
            ge.Platform = TakeInt(values, GePlatformKey, ref ownKeys);
            ge.Flags = TakeInt(values, GeFlagsKey, ref ownKeys);
            var options = new SvJsonObject();
            foreach (KeyValuePair<string, string> kv in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                bool coordinate = kv.Key.Length == 2 && (char.ToUpperInvariant(kv.Key[0]) is 'X' or 'Y' or 'Z') && char.IsAsciiDigit(kv.Key[1]);
                if (!coordinate && !kv.Value.Equals("UNUSED", StringComparison.OrdinalIgnoreCase))
                {
                    options.Add(kv.Key.ToUpperInvariant(), SvJsonNode.String(kv.Value));
                }
            }

            var src = new SvJsonObject();
            src.Add("kind", SvJsonNode.String("t3d"));
            src.Add("index", SvJsonNode.Integer(i));
            if (options.Count > 0)
            {
                src.Add("options", options);
            }

            ge.Source = InteropValues.Compact(src);
            p.Ges.Add(ge);
        }

        if (p.Ges.Count > 0 && !ownKeys)
        {
            findings.Add(Finding.Info("import.geMask", "T3D GE lines carry no GE ID: every GE was imported with mask 0 (always on)"));
        }
    }

    /// <summary>Removes an integer key from a GE line's values: its value, else 0.</summary>
    private static int TakeInt(Dictionary<string, string> values, string key, ref bool found)
    {
        if (values.Remove(key, out string? text) && T3dFormat.TryParseInt(text, out long v))
        {
            found = true;
            return unchecked((int)v);
        }

        return 0;
    }

    private static double Coord(Dictionary<string, string> values, string axis, int n) =>
        values.TryGetValue(axis + n.ToString(CultureInfo.InvariantCulture), out string? s) && T3dFormat.TryParseFloat(s, out float v) ? v : 0;

    private static string UniqueId(string id, HashSet<string> used, List<Finding> findings)
    {
        string unique = id;
        for (int i = 1; !used.Add(unique); i++)
        {
            unique = id + "_" + i.ToString(CultureInfo.InvariantCulture);
        }

        if (unique != id)
        {
            findings.Add(Finding.Warning("import.duplicateName", $"name {id} is used twice (names ignore case); the second is {unique}"));
        }

        return unique;
    }

    /// <summary>Starts the editor camera where the original editor's perspective view was.</summary>
    internal static void CameraFromLevelInfo(MapProject p)
    {
        if (p.LevelInfo["CameraLocationDynamic"] is SvJsonObject loc)
        {
            p.Workspace.CameraLocation = InteropValues.VectorOr(loc, Vec3.Zero);
        }

        if (PropValues.TryRotator(p.LevelInfo["CameraRotationDynamic"], out Rot rot))
        {
            p.Workspace.CameraRotation = rot.Normalized();
        }
    }
}
