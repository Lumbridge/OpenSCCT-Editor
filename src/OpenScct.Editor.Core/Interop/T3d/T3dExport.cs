using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>
/// Turns a project into a T3D map the stock or RE+ editor imports (File &gt; Import, <c>MAP IMPORT</c>): the LevelInfo,
/// a builder brush, the brushes in CSG order, then the actors, then the GE list. With a selection it writes only the
/// chosen objects, without LevelInfo and builder brush (the Edit &gt; Copy form, for pasting into another map).
/// Brush models get fresh unique names. Engine-derived properties that would point at objects the import does not
/// create (zone regions, static mesh lighting instances, path lists) are left out; the editor rebuilds them. Conveyors
/// become RE+'s conveyor belts (<see cref="ReConveyors"/>), apart from those an .sdc import converted from RE+ belts
/// that are still as imported, with their belts (<see cref="ConveyorHandoff"/>): those belts are written as imported.
/// </summary>
public static partial class T3dExport
{
    /// <summary>Properties not written: the name is the Begin line, the brush model is the shape, the rest is derived.</summary>
    private static readonly HashSet<string> s_skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Brush", "Region", "StaticMeshInstance", "StaticMeshInstanceOnPSX2", "StaticMeshInstanceOnXBOX", "NavigationPointList", "bHiddenEdGroup",
        "HideWhileInactive", // OpenSCCT visibility override; absent in the original engine.
    };

    /// <summary>True for a property a T3D carries (the others the original editor works out itself).</summary>
    public static bool IsWritten(string key) => !s_skipped.Contains(key);

    /// <param name="only">The objects to write (a selection), or null for the whole map.</param>
    /// <param name="leaveOut">Actors to leave out (the export for the original editor writes the actors of the map's
    /// own classes to a second file).</param>
    /// <param name="projectFolder">The project's folder, for the outfit files a lifted outfit actor names.</param>
    /// <param name="added">Actors that are not the map's, written after its own (the RE+ actors the export for the
    /// original editor makes, <see cref="LiftedWriteBack.Made"/>).</param>
    public static T3dDocument FromProject(MapProject p, UeSchema? schema, IReadOnlyCollection<ObjectRef>? only = null, Func<Actor, bool>? leaveOut = null,
        string? projectFolder = null, IReadOnlyList<Actor>? added = null)
    {
        if (p.Actors.Any(a => a.Door is not null && (only is null || only.Contains(ObjectRef.Actor(a.Id)))))
            throw new ArgumentException("Editable doors currently require an OpenSCCT build");
        if (p.Actors.Any(a => a.Window is not null && (only is null || only.Contains(ObjectRef.Actor(a.Id)))))
            throw new ArgumentException("Editable shaped windows currently require an OpenSCCT build; original-game window export is not supported");
        if (only is not null && p.Brushes.Any(b => b.Stair is not null))
        {
            var selected = new HashSet<ObjectRef>(only, ObjectRefComparer.Instance);
            var generated = p.Brushes.Where(b => b.Stair is not null && (selected.Contains(ObjectRef.ForBrush(b)) || selected.Contains(ObjectRef.Brush(b.Id))))
                .Select(Stairs.StairGeometry.Generate).ToList();
            only = only.Concat(generated.SelectMany(g => g.Cuts.Concat(g.Solids)).Select(ObjectRef.ForBrush))
                .Concat(generated.SelectMany(g => g.Smoothing).Select(a => ObjectRef.Actor(a.Id))).ToList();
            p = Stairs.StairGeometry.Lower(p);
        }
        // A copied GE brings its generated look with it. Other GEs' geometry is not part of a selection export.
        if (only is not null && p.Ges.Any(g => g.Appearance is not null))
        {
            var selection = new HashSet<ObjectRef>(only, ObjectRefComparer.Instance);
            p = Session.ProjectSnapshot.Take(p);
            foreach (Ge g in p.Ges.Where(g => !selection.Contains(ObjectRef.Ge(g.Id))))
            {
                g.Appearance = null;
            }

            int authoredBrushes = p.Brushes.Count;
            p = Stairs.StairGeometry.Lower(GeGeometry.Expand(p));
            only = only.Concat(p.Brushes.Skip(authoredBrushes).Select(b => ObjectRef.ForBrush(b))).ToList();
        }
        else
        {
            p = Stairs.StairGeometry.Lower(GeGeometry.Expand(p));
        }

        p = BrushTransformExport.Prepare(p);
        var doc = new T3dDocument();
        var types = new T3dTypes(schema, p.Id);
        SvJsonObject extra = p.Extra[T3dImport.ExtraKey] as SvJsonObject ?? new SvJsonObject();
        if (extra["refClasses"] is SvJsonArray refs)
        {
            foreach (SvJsonNode pair in refs)
            {
                if (pair is SvJsonArray { Count: 2 } kv && kv[0] is SvJsonString path && kv[1] is SvJsonString cls)
                {
                    types.RefClasses[path.Value] = cls.Value;
                }
            }
        }

        string levelInfoName = LevelInfoName(p);
        var classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [levelInfoName] = "LevelInfo" };
        foreach (Brush b in p.Brushes)
        {
            classes.TryAdd(b.Id, "Brush");
        }

        foreach (Actor a in p.Actors)
        {
            classes.TryAdd(a.Id, a.ShortClass);
        }

        types.ActorClass = id => classes.GetValueOrDefault(id);
        var names = new HashSet<string>(classes.Keys, StringComparer.OrdinalIgnoreCase);
        HashSet<ObjectRef>? picked = only is null ? null : new HashSet<ObjectRef>(only, ObjectRefComparer.Instance);
        bool Picked(ObjectRef r) => picked is null || picked.Contains(r);

        if (picked is null)
        {
            doc.Actors.Add(LevelInfoActor(p, levelInfoName, types));
            doc.Actors.Add(BuilderBrush(Unique("Brush", names)));
        }

        foreach (Brush b in p.Brushes.Where(b => Picked(ObjectRef.ForBrush(b))))
        {
            doc.Actors.Add(BrushActor(b, p.Id, types, names));
        }

        // Converted conveyors still as imported are written as their RE+ belts (copied with them when only the conveyor is
        // picked); the others are lowered, and their imported belts left out.
        ConveyorHandoff handoff = ConveyorHandoff.Of(p);
        HashSet<string> withBelts = picked is null ? [] : handoff.BeltsOf(p.Actors.Where(a => Picked(ObjectRef.Actor(a.Id))));
        List<Actor> written = p.Actors.Where(a => (Picked(ObjectRef.Actor(a.Id)) || withBelts.Contains(a.Id)) && handoff.Writes(a)
            && leaveOut?.Invoke(a) != true).ToList();
        var writtenIds = new HashSet<string>(written.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LoweredConveyor> lowered = LowerConveyors(p, written, names);
        foreach (Actor a in written)
        {
            if (lowered.TryGetValue(a.Id, out LoweredConveyor? chain))
            {
                string? next = ConveyorDesign.NextId(a) is { } n ? FirstBelt(p, n, lowered, handoff, writtenIds) : null;
                doc.Actors.AddRange(ConveyorBelts(p, chain, next));
            }
            else
            {
                doc.Actors.Add(ActorBlock(p, a, types, names, projectFolder));
            }
        }

        foreach (Actor a in added ?? [])
        {
            names.Add(a.Id);
            doc.Actors.Add(ActorBlock(p, a, types, names, projectFolder));
        }

        if (picked is null && extra["blocks"] is SvJsonArray blocks)
        {
            doc.OtherBlocks.AddRange(blocks.OfType<SvJsonArray>().Select(Lines));
        }

        foreach (Ge g in p.Ges.Where(g => Picked(ObjectRef.Ge(g.Id))))
        {
            doc.GeLines.Add(GeLine(g));
        }

        return doc;
    }

    /// <summary>The LevelInfo's object name: as imported, else the UE2 default.</summary>
    public static string LevelInfoName(MapProject p) =>
        (p.Extra[T3dImport.ExtraKey] as SvJsonObject)?["levelInfo"] is SvJsonString t ? t.Value
        : (p.Extra[Svmap.SvMapImport.ExtraKey] as SvJsonObject)?["levelInfo"] is SvJsonString s ? s.Value
        : "LevelInfo0";

    private static T3dActor LevelInfoActor(MapProject p, string name, T3dTypes types)
    {
        var props = EditorGroups.LevelInfo(p);
        // The title defaults to the map id on import; only a real one goes back into the LevelInfo. The shared catalog's
        // name an import gave the map (IdentityImport) gives way to the title the map came with, until the map is renamed.
        string title = Svmap.IdentityImport.ImportedTitle(p);
        if (title.Length > 0 && (props.GetString("Title") is not null || title != p.Id))
        {
            props.Set("Title", SvJsonNode.String(title));
        }

        var a = new T3dActor("LevelInfo", name);
        a.Props.AddRange(types.FromProps("Engine.LevelInfo", props.Where(kv => !s_skipped.Contains(kv.Key))));
        return a;
    }

    /// <summary>The builder brush: an untextured 256 cube, as a new map's red brush.</summary>
    private static T3dActor BuilderBrush(string name)
    {
        var a = new T3dActor("Brush", name);
        a.Props.Add(SheerField("MainScale", Vec3.One));
        a.Props.Add(SheerField("PostScale", Vec3.One));
        var brush = new T3dBrush(name + "Model");
        foreach (BrushFace f in Geometry.Box.Shape(new Vec3(256, 256, 256)).Faces)
        {
            f.Material = null;
            brush.Polygons.Add(Polygon(f));
        }

        a.Brush = brush;
        return a;
    }

    private static T3dActor BrushActor(Brush b, string mapId, T3dTypes types, HashSet<string> names)
    {
        var a = new T3dActor("Brush", b.Id);
        a.Props.Add(new T3dField("CsgOper", null, new T3dAtom(b.Oper == CsgOper.Add ? "CSG_Add" : "CSG_Subtract")));
        a.Props.Add(ScaleField("MainScale", b.MainScale, b.Props["MainScale"], types));
        a.Props.Add(ScaleField("PostScale", b.PostScale, b.Props["PostScale"], types));
        if (b.PolyFlags != 0)
        {
            a.Props.Add(new T3dField("PolyFlags", null, T3dAtom.Int(b.PolyFlags)));
        }

        AddTransform(a, b.Location, b.Rotation, 1, Vec3.One, b.PrePivot);
        a.Props.AddRange(types.FromProps("Engine.Brush", b.Props.Where(kv => !s_skipped.Contains(kv.Key) && kv.Key is not ("MainScale" or "PostScale"))));
        a.Brush = Model(b.Shape, ModelName(b.Props["Brush"], b.Id, mapId, names), types);
        return a;
    }

    private static T3dActor ActorBlock(MapProject p, Actor actor, T3dTypes types, HashSet<string> names, string? projectFolder)
    {
        var a = new T3dActor(actor.ShortClass, actor.Id);
        AddTransform(a, actor.Location, actor.Rotation, actor.DrawScale, actor.DrawScale3D, actor.PrePivot);
        // A lifted RE+ actor carries the outfits or match rules as they are now (as imported when unchanged).
        a.Props.AddRange(types.FromProps(actor.Class, LiftedWriteBack.Props(p, actor, projectFolder: projectFolder).Where(kv => !s_skipped.Contains(kv.Key))));
        List<List<string>> blocks = T3dImport.ObjectsOf(actor);
        if (blocks.Count > 0)
        {
            AddSubObjects(a, blocks, names);
        }
        else if (Compile.EmitterBuild.SystemsOf(actor, p) is { } systems)
        {
            AddParticleSystems(a, types, systems, names);
        }
        if (actor.Shape is not null)
        {
            a.Brush = Model(actor.Shape, ModelName(actor.Props["Brush"], actor.Id, p.Id, names), types);
        }

        return a;
    }

    private static void AddParticleSystems(T3dActor actor, T3dTypes types, SvJsonArray systems, HashSet<string> names)
    {
        actor.Props.RemoveAll(f => f.Name.Equals("Emitters", StringComparison.OrdinalIgnoreCase));
        int nextIndex = 0;
        foreach (SvJsonObject system in Compile.EmitterBuild.ValidSystems(systems))
        {
            string cls = Compile.EmitterBuild.Text(system["class"])!;
            string shortClass = InteropValues.ShortClass(cls);
            string name = Unique(shortClass, names);
            var props = (SvJsonObject)system["props"]!;
            var block = new List<string> { $"Begin Object Class={shortClass} Name={name}" };
            block.AddRange(types.FromProps(cls, props.Where(kv => !kv.Key.Equals("Name", StringComparison.OrdinalIgnoreCase)))
                .Select(field => "    " + field));
            block.Add($"    Name=\"{name}\"");
            block.Add("End Object");
            actor.Objects.Add(block);

            int index = system["index"] is SvJsonNumber n && n.TryGetInt32(out int slot) && slot >= nextIndex ? slot : nextIndex;
            actor.Props.Add(new T3dField("Emitters", index, new T3dRef(shortClass, T3dTypes.LevelPackage + "." + name)));
            nextIndex = index + 1;
        }
    }

    /// <summary>
    /// Adds an actor's sub-objects (Begin Object blocks). Their names live in the level package, so the sub-objects of
    /// a copied actor get fresh names, and the actor's references to them follow.
    /// </summary>
    private static void AddSubObjects(T3dActor a, List<List<string>> blocks, HashSet<string> names)
    {
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (List<string> block in blocks)
        {
            if (block.Count > 0 && T3dReader.HeaderValues(block[0]).GetValueOrDefault("Name") is { Length: > 0 } name && !names.Add(name))
            {
                renamed[name] = Unique(name, names);
            }
        }

        if (renamed.Count == 0)
        {
            a.Objects.AddRange(blocks);
            return;
        }

        Dictionary<string, string> paths = renamed.ToDictionary(kv => T3dTypes.LevelPackage + "." + kv.Key,
            kv => T3dTypes.LevelPackage + "." + kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (List<string> block in blocks)
        {
            a.Objects.Add(block.Select(line => RenameInLine(line, renamed, paths)).ToList());
        }

        for (int i = 0; i < a.Props.Count; i++)
        {
            a.Props[i] = a.Props[i] with { Value = RenameRefs(a.Props[i].Value, paths) };
        }
    }

    private static string RenameInLine(string line, Dictionary<string, string> names, Dictionary<string, string> paths)
    {
        foreach ((string from, string to) in paths)
        {
            line = line.Replace("'" + from + "'", "'" + to + "'", StringComparison.OrdinalIgnoreCase);
        }

        if (!line.StartsWith("Begin Object", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        // The header's Name= token, matched whole.
        return string.Join(' ', line.Split(' ').Select(token =>
            token.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) && names.TryGetValue(token[5..], out string? to) ? "Name=" + to : token));
    }

    private static T3dValue RenameRefs(T3dValue value, Dictionary<string, string> paths) => value switch
    {
        T3dRef r when paths.TryGetValue(r.Path, out string? to) => r with { Path = to },
        T3dAtom a when paths.TryGetValue(a.Text, out string? to) => new T3dAtom(to),
        T3dStruct s => new T3dStruct(s.Fields.Select(f => f with { Value = RenameRefs(f.Value, paths) }).ToList()),
        T3dList l => new T3dList(l.Items.Select(i => RenameRefs(i, paths)).ToList()),
        _ => value,
    };

    private static void AddTransform(T3dActor a, Vec3 location, Rot rotation, double drawScale, Vec3 drawScale3D, Vec3 prePivot)
    {
        if (location != Vec3.Zero)
        {
            a.Props.Add(new T3dField("Location", null, Vector(location)));
        }

        if (rotation != Rot.Zero)
        {
            a.Props.Add(new T3dField("Rotation", null, new T3dStruct(
            [
                new T3dField("Pitch", null, T3dAtom.Int(rotation.Pitch)),
                new T3dField("Yaw", null, T3dAtom.Int(rotation.Yaw)),
                new T3dField("Roll", null, T3dAtom.Int(rotation.Roll)),
            ])));
        }

        if (drawScale != 1)
        {
            a.Props.Add(new T3dField("DrawScale", null, T3dAtom.Float(drawScale)));
        }

        if (drawScale3D != Vec3.One)
        {
            a.Props.Add(new T3dField("DrawScale3D", null, Vector(drawScale3D)));
        }

        if (prePivot != Vec3.Zero)
        {
            a.Props.Add(new T3dField("PrePivot", null, Vector(prePivot)));
        }
    }

    private static T3dStruct Vector(Vec3 v) => new(
    [
        new T3dField("X", null, T3dAtom.Float(v.X)),
        new T3dField("Y", null, T3dAtom.Float(v.Y)),
        new T3dField("Z", null, T3dAtom.Float(v.Z)),
    ]);

    /// <summary>An FScale as the editor writes it: the scale (when not 1) and SHEER_ZX, or the stored sheer.</summary>
    private static T3dField ScaleField(string name, Vec3 scale, SvJsonNode? sheer, T3dTypes types)
    {
        if (sheer is not SvJsonObject s)
        {
            return SheerField(name, scale);
        }

        var fields = new List<T3dField>();
        if (scale != Vec3.One)
        {
            fields.Add(new T3dField("Scale", null, Vector(scale)));
        }

        UeProp? rateType = types.Schema?.FindMember("Core.Object.Scale", "SheerRate");
        UeProp? axisType = types.Schema?.FindMember("Core.Object.Scale", "SheerAxis");
        fields.Add(new T3dField("SheerRate", null, types.FromJson(s["SheerRate"] ?? SvJsonNode.Float(0), rateType)));
        fields.Add(new T3dField("SheerAxis", null, types.FromJson(s["SheerAxis"] ?? SvJsonNode.Integer(0), axisType)));
        return new T3dField(name, null, new T3dStruct(fields));
    }

    private static T3dField SheerField(string name, Vec3 scale)
    {
        var fields = new List<T3dField>();
        if (scale != Vec3.One)
        {
            fields.Add(new T3dField("Scale", null, Vector(scale)));
        }

        fields.Add(new T3dField("SheerAxis", null, new T3dAtom("SHEER_ZX")));
        return new T3dField(name, null, new T3dStruct(fields));
    }

    private static T3dBrush Model(BrushShape shape, string name, T3dTypes types)
    {
        var brush = new T3dBrush(name);
        foreach (BrushFace f in shape.Faces)
        {
            T3dPolygon poly = Polygon(f);
            poly.Texture = poly.Texture is { } t ? types.ToLevelPath(t) : null;
            brush.Polygons.Add(poly);
        }

        return brush;
    }

    public static T3dPolygon Polygon(BrushFace f)
    {
        // UnrealEd folds a Pan line along each axis on its own, which is wrong when U and V are not square: fold here.
        bool fold = Geometry.TextureOps.PansNeedExactFold(f.TextureU, f.TextureV, f.PanU, f.PanV);
        var poly = new T3dPolygon
        {
            Item = f.ItemName,
            Texture = f.Material,
            Flags = f.Flags,
            Link = f.Link >= 0 ? f.Link : null,
            LightMapScale = Math.Abs(f.LightMapScale - 32) > 1e-6 ? (float)f.LightMapScale : null,
            LightMapScalePsx2 = f.LightMapScalePsx2 is double ps2 ? (float)ps2 : null,
            Origin = fold ? Geometry.TextureOps.FoldPans(f.TextureBase, f.TextureU, f.TextureV, f.PanU, f.PanV) : f.TextureBase,
            Normal = f.Normal(),
            TextureU = f.TextureU,
            TextureV = f.TextureV,
            PanU = fold ? 0 : f.PanU,
            PanV = fold ? 0 : f.PanV,
        };
        poly.Vertices.AddRange(f.Vertices);
        return poly;
    }

    /// <summary>The model name: the original one when free, else <c>&lt;Id&gt;Model</c> made unique.</summary>
    private static string ModelName(SvJsonNode? brushRef, string ownerId, string mapId, HashSet<string> names)
    {
        if (PropValues.AssetPath(brushRef) is { } path)
        {
            string local = path.StartsWith(mapId + ".", StringComparison.OrdinalIgnoreCase) ? path[(mapId.Length + 1)..]
                : path.StartsWith(T3dTypes.LevelPackage + ".", StringComparison.OrdinalIgnoreCase) ? path[(T3dTypes.LevelPackage.Length + 1)..]
                : path;
            if (InteropValues.IsValidName(local) && names.Add(local))
            {
                return local;
            }
        }

        return Unique(ownerId + "Model", names);
    }

    private static string Unique(string stem, HashSet<string> names)
    {
        if (names.Add(stem))
        {
            return stem;
        }

        for (int i = 0; ; i++)
        {
            string candidate = stem + i.ToString(CultureInfo.InvariantCulture);
            if (names.Add(candidate))
            {
                return candidate;
            }
        }
    }

    public static string GeLine(Ge g)
    {
        var parts = new List<string> { "GE", "ADD", g.Kind };
        for (int i = 0; i < g.Points.Count; i++)
        {
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            parts.Add($"X{n}={T3dFormat.Float(g.Points[i].X)}");
            parts.Add($"Y{n}={T3dFormat.Float(g.Points[i].Y)}");
            parts.Add($"Z{n}={T3dFormat.Float(g.Points[i].Z)}");
        }

        SvJsonObject? options = null;
        if (g.Source is { Length: > 0 } src && src[0] == '{')
        {
            try
            {
                options = (SvJson.Parse(src) as SvJsonObject)?["options"] as SvJsonObject;
            }
            catch (OpenScct.Content.SvMapFormatException)
            {
                options = null;
            }
        }

        foreach (string key in new[] { "NOSLIDE", "NOHOIST", "NOGETOUT" })
        {
            string value = options?[key] is SvJsonString s ? s.Value : "UNUSED";
            parts.Add($"{key}={value}");
        }

        // The editor's own keys for what GE ADD cannot say; the stock command reads only the kind and the points.
        foreach ((string key, int value) in new[] { (T3dImport.GeMaskKey, g.Mask), (T3dImport.GePlatformKey, g.Platform), (T3dImport.GeFlagsKey, g.Flags) })
        {
            if (value != 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{key}={value}"));
            }
        }

        return string.Join(' ', parts);
    }

    private static List<string> Lines(SvJsonArray block) => block.OfType<SvJsonString>().Select(s => s.Value).ToList();
}
