using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// Sorts the project's actors into gameplay and render ones and writes <c>sim/gameplay.json</c> and
/// <c>sim/classes.json</c>: the LevelInfo, every gameplay actor with its stored props plus its transform, effective
/// Tag, Level and Region, the map settings, and the derived indexes (tag index, event edges, mission tree) rebuilt by
/// OpenSCCT's own reader; and the class defaults of the classes those actors use, which OpenSCCT's renderer reads for
/// what an actor does not store (a camera's or laser trigger's DrawType and StaticMesh).
/// </summary>
public static class GameplayBuild
{
    /// <summary>
    /// The SVMap version of a compiled map, written alike in <c>manifest.json</c>, <c>sim/gameplay.json</c> and
    /// <c>sim/traversal.json</c> (the spec asks one version across them). Every build writes <c>ui/identity.json</c>
    /// (<see cref="IdentityBuild"/>), a 1.2 file, so every compiled map is 1.2, with or without match rules, outfits
    /// or conveyors.
    /// </summary>
    public const string FormatVersion = "1.2.0";

    /// <summary>Fills <see cref="CompileContext.Gameplay"/>, <see cref="CompileContext.Render"/> and the id set.</summary>
    public static void SortActors(CompileContext ctx)
    {
        ctx.GameplayIds.Add(ctx.LevelInfoId);
        int editor = 0, skipped = 0, cookedOut = 0, lifted = 0;
        foreach (Actor a in ctx.Project.Actors)
        {
            // The cook drops these actors whole, so the build ships none of them. The lights among them are static
            // lights the stock editor bakes into the lightmaps before cooking: a bake here lights with them too (the
            // relight, added brushes and meshes, or the whole world on a rebuild without the baked render), but like the
            // cooked map the game never sees them (BakeOnlyLights). The others (light-typed meshes and the like) give
            // nothing else: the lighting takes only Engine.Light actors.
            if (BakedContent.IsCookedOut(a))
            {
                if (ctx.Rules.IsLight(a) && ctx.Rules.CategoryOf(a) is ActorCategory.Render or ActorCategory.Gameplay)
                {
                    ctx.BakeOnlyLights.Add(a);
                }

                cookedOut++;
                continue;
            }

            // RE+ actors the conversion made native data: the project's match rules and outfits stand for them.
            if (BakedContent.IsLifted(a))
            {
                lifted++;
                continue;
            }

            switch (ctx.Rules.CategoryOf(a))
            {
                case ActorCategory.Editor:
                    editor++;
                    continue;
                case ActorCategory.Skipped:
                    skipped++;
                    continue;
                case ActorCategory.Render:
                    ctx.Render.Add(a);
                    continue;
            }

            if (!IsIdentifier(a.Id))
            {
                ctx.Error("gameplay.id", $"actor id '{a.Id}' is not an identifier (letters, digits and _, at most 63)", ObjectRef.Actor(a.Id));
            }
            else if (!PropJsonClassPath(a.Class))
            {
                ctx.Error("gameplay.class", $"actor {a.Id} has class '{a.Class}', which is not Package.Class", ObjectRef.Actor(a.Id));
            }
            else if (!ctx.GameplayIds.Add(a.Id))
            {
                ctx.Error("gameplay.duplicate", $"actor id '{a.Id}' is used twice (ids ignore case)", ObjectRef.Actor(a.Id));
            }
            else
            {
                ctx.Gameplay.Add(a);
                if (ctx.Classes.HasData && !ctx.Classes.Known(a.Class))
                {
                    ctx.Warn("gameplay.unknown-class", $"actor {a.Id}: class {a.Class} is not in the class data", ObjectRef.Actor(a.Id));
                }
            }
        }

        ctx.Count("actors.gameplay", ctx.Gameplay.Count);
        ctx.Count("actors.render", ctx.Render.Count);
        ctx.Count("actors.dropped", editor + skipped + cookedOut + lifted);
        if (cookedOut > 0)
        {
            ctx.Count("actors.cookedOut", cookedOut);
        }

        if (lifted > 0)
        {
            ctx.Count("actors.lifted", lifted);
        }
    }

    public static void Write(CompileContext ctx)
    {
        var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ctx.LevelInfoId] = ctx.LevelInfoId };
        foreach (Actor a in ctx.Gameplay)
        {
            exact[a.Id] = a.Id;
        }

        var renderIds = new HashSet<string>(ctx.Render.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var refs = new RefResolver(ctx, exact, renderIds);

        var root = new SvJsonObject();
        root.Add("formatVersion", SvJsonNode.String(FormatVersion));
        root.Add("map", SvJsonNode.String(ctx.MapId));
        root.Add("levelInfo", LevelInfo(ctx, refs));
        var actors = new SvJsonArray();
        foreach (Actor a in ctx.Gameplay)
        {
            actors.Add(ActorJson(ctx, a, refs));
        }

        root.Add("actors", actors);
        root.Add("mapSettings", MapSettings(ctx));
        root.Add("tagIndex", new SvJsonObject());
        root.Add("eventEdges", new SvJsonArray());
        root.Add("missionTree", new SvJsonArray());

        try
        {
            GameplayDoc doc = GameplayDoc.FromJson(root).WithDerived();
            foreach (string problem in doc.CheckReferences())
            {
                ctx.Warn("gameplay.reference", problem);
            }

            ctx.Files[GameplayDoc.RelativePath] = CanonicalJson.ToFileBytes(doc.ToJson());
            ctx.Count("gameplay.tags", doc.TagIndex.Count);
            ctx.Count("gameplay.eventEdges", doc.EventEdges.Count);
            ctx.Count("gameplay.missions", doc.MissionTree.Count);
        }
        catch (SvMapFormatException e)
        {
            ctx.Error("gameplay.invalid", e.Message);
        }

        if (ctx.Options.WriteClassTable)
        {
            WriteClassTable(ctx);
        }
    }

    private static SvJsonObject LevelInfo(CompileContext ctx, RefResolver refs)
    {
        PropSet props = ctx.Project.LevelInfo.Clone();
        if (props["GEActivationMask"] is not SvJsonNumber mask || !mask.TryGetInt32(out int m) || m is < 0 or > 255)
        {
            int fallback = ctx.Classes.Default(ctx.LevelInfoClass, "GEActivationMask") is SvJsonNumber d && d.TryGetInt32(out int dv) ? dv : 0;
            props.Set("GEActivationMask", SvJsonNode.Integer(fallback is >= 0 and <= 255 ? fallback : 0));
        }

        if (props.GetString("Title") is null)
        {
            props.Set("Title", SvJsonNode.String(ctx.Project.Title));
        }

        if (props.GetString("Tag") is null)
        {
            props.Set("Tag", SvJsonNode.String("LevelInfo"));
        }

        props.Remove("Region");
        StrategicMapBuild.AddLevelInfoProps(ctx, props);
        var obj = new SvJsonObject();
        obj.Add("id", SvJsonNode.String(ctx.LevelInfoId));
        obj.Add("class", SvJsonNode.String(ctx.LevelInfoClass));
        obj.Add("props", refs.Resolve(props.ToJson(), ctx.LevelInfoId));
        return obj;
    }

    private static SvJsonObject ActorJson(CompileContext ctx, Actor a, RefResolver refs)
    {
        PropSet props = a.Props.Clone();
        foreach (string key in new[] { "Location", "Rotation", "DrawScale", "DrawScale3D", "PrePivot", "Region", "Level", "Tag" })
        {
            props.Remove(key);
        }

        if (MoverBase.IsMover(a) && Gameplay.MoverSetup.Read(a).Keys.Count > Gameplay.MoverSetup.OriginalMaxKeys)
        {
            var m = Gameplay.MoverSetup.Read(a);
            props.Set("NumKeys", SvJsonNode.Integer(Gameplay.MoverSetup.OriginalMaxKeys));
            props.Set("KeyNum", SvJsonNode.Integer(Math.Min(m.StartKey, Gameplay.MoverSetup.OriginalMaxKeys - 1)));
            foreach (string property in new[] { "KeyPos", "KeyRot" })
            {
                var elements = new SvJsonObject();
                foreach (var pair in Gameplay.MoverSetup.Elements(a.Props[property]).Where(kv => kv.Key < Gameplay.MoverSetup.OriginalMaxKeys))
                    elements.Add(pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), pair.Value);
                if (elements.Count == 0) props.Remove(property);
                else { var sparse = new SvJsonObject(); sparse.Add("$elements", elements); props.Set(property, sparse); }
            }
        }
        props.Set("Location", PropValues.Vector(a.Location));
        if (a.Rotation != default)
        {
            props.Set("Rotation", PropValues.Rotator(a.Rotation));
        }

        if (a.DrawScale != 1)
        {
            props.Set("DrawScale", SvJsonNode.Float((float)a.DrawScale));
        }

        if (a.DrawScale3D != Vec3.One)
        {
            props.Set("DrawScale3D", PropValues.Vector(a.DrawScale3D));
        }

        if (a.PrePivot != Vec3.Zero)
        {
            props.Set("PrePivot", PropValues.Vector(a.PrePivot));
        }

        if (a.Shape is not null && props["Brush"] is null)
        {
            string asset = $"{ctx.MapId}.{a.Id}Brush";
            if (AssetRef.IsValidPath(asset))
            {
                props.Set("Brush", PropValues.AssetRef(asset));
            }
        }

        props.Set("Tag", SvJsonNode.String(EffectiveTag(ctx, a)));
        props.Set("Level", PropValues.ActorRef(ctx.LevelInfoId));
        var region = new SvJsonObject();
        region.Add("Zone", PropValues.ActorRef(ctx.ZoneActorAt(a.Location)));
        region.Add("ZoneNumber", SvJsonNode.Integer(ctx.ZoneAt(a.Location)));
        region.Add("iLeaf", SvJsonNode.Integer(-1));
        props.Set("Region", region);

        var obj = new SvJsonObject();
        obj.Add("id", SvJsonNode.String(a.Id));
        obj.Add("class", SvJsonNode.String(a.Class));
        obj.Add("props", refs.Resolve(props.ToJson(), a.Id));
        return obj;
    }

    /// <summary>The actor's Tag, else its class default, else its bare class name.</summary>
    public static string EffectiveTag(CompileContext ctx, Actor a) =>
        a.Props.GetString("Tag") is { Length: > 0 } t ? t
        : ctx.Classes.Default(a.Class, "Tag") is SvJsonString d && d.Value.Length > 0 ? d.Value
        : a.ShortClass;

    private static SvJsonObject MapSettings(CompileContext ctx)
    {
        var obj = new SvJsonObject();
        foreach ((string mode, PropSet props) in ctx.Project.MapSettings)
        {
            if (mode.Length == 0 || !char.IsAsciiLetter(mode[0]) || !IsIdentifier(mode))
            {
                ctx.Error("gameplay.mode", $"map settings mode '{mode}' is not a game mode name");
                continue;
            }

            obj.Add(mode, props.ToJson());
        }

        return obj;
    }

    /// <summary>True when OpenSCCT draws <paramref name="a"/> with a static mesh it takes from the class table: DrawType 8
    /// and a StaticMesh, at least one of them a class default the actor does not store.</summary>
    private static bool DrawsClassMesh(CompileContext ctx, Actor a) =>
        a.Shape is null && (a.Props["DrawType"] is null || a.Props["StaticMesh"] is null) &&
        (int)ctx.Rules.Number(a, "DrawType") == 8 && PropValues.AssetPath(ctx.Rules.Value(a, "StaticMesh")) is not null;

    private static void WriteClassTable(CompileContext ctx)
    {
        if (ctx.Classes.ClassFile is not { } file)
        {
            // Only a map with an actor that draws its class's mesh loses something it shows; without any class data
            // that cannot be told, so any gameplay actor counts.
            Actor? drawn = ctx.Classes.HasData ? ctx.Gameplay.FirstOrDefault(a => DrawsClassMesh(ctx, a)) : ctx.Gameplay.FirstOrDefault();
            if (drawn is not null)
            {
                ctx.Warn("classes.missing", "no shared/classes.json was found, so the map has no class table (sim/classes.json): OpenSCCT will draw " +
                    "none of the gameplay objects that show their class's mesh (cameras, laser triggers, objectives), though they still work",
                    ObjectRef.Actor(drawn.Id));
            }
            else
            {
                ctx.Info("classes.missing", "no shared/classes.json was found, so the map has no class table (sim/classes.json)");
            }

            return;
        }

        var used = new SortedSet<string>(StringComparer.Ordinal) { ctx.LevelInfoClass };
        foreach (Actor a in ctx.Gameplay)
        {
            used.Add(a.Class);
        }

        var classes = new SortedDictionary<string, SvJsonObject>(StringComparer.Ordinal);
        foreach (string cls in used)
        {
            for (string? c = cls; c is not null; c = file[c] is SvJsonObject e && e["super"] is SvJsonString s ? s.Value : null)
            {
                if (file[c] is not SvJsonObject entry)
                {
                    break;
                }

                if (!classes.TryGetValue(c, out SvJsonObject? outEntry))
                {
                    outEntry = new SvJsonObject();
                    outEntry.Add("super", entry["super"] ?? SvJsonNode.Null);
                    classes[c] = outEntry;
                }

                if (c == cls && !outEntry.ContainsKey("defaults") && entry["defaults"] is SvJsonObject d)
                {
                    outEntry.Add("defaults", d);
                }
            }
        }

        var root = new SvJsonObject();
        root.Add("format", SvJsonNode.String(ClassTable.FormatId));
        var list = new SvJsonObject();
        foreach ((string c, SvJsonObject e) in classes)
        {
            list.Add(c, e);
        }

        root.Add("classes", list);
        try
        {
            ClassTable.FromJson(root);
            ctx.Files[ClassTable.RelativePath] = CanonicalJson.ToFileBytes(root);
            ctx.Count("classes", classes.Count);
        }
        catch (SvMapFormatException e)
        {
            ctx.Error("classes.invalid", e.Message);
        }
    }

    /// <summary><c>^[A-Za-z_][A-Za-z0-9_]*$</c>, at most 63 characters (gameplay.json ids and names).</summary>
    public static bool IsIdentifier(string s)
    {
        if (s.Length is 0 or > 63 || !(char.IsAsciiLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PropJsonClassPath(string s)
    {
        int dot = s.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && s.IndexOf('.', dot + 1) < 0 && IsIdentifier(s[..dot]) && IsIdentifier(s[(dot + 1)..]);
    }

    /// <summary>Rewrites actor references: gameplay ids to their exact spelling, render-only actors to assets of the
    /// map (as the converter does), unknown ones to null.</summary>
    private sealed class RefResolver(CompileContext ctx, Dictionary<string, string> exact, HashSet<string> renderIds)
    {
        public SvJsonNode Resolve(SvJsonNode node, string owner)
        {
            switch (node)
            {
                case SvJsonObject o when o.Count == 1 && o[0].Key == "$actor" && o[0].Value is SvJsonString id:
                    if (exact.TryGetValue(id.Value, out string? spelled))
                    {
                        return PropValues.ActorRef(spelled);
                    }

                    if (renderIds.Contains(id.Value) && AssetRef.IsValidPath($"{ctx.MapId}.{id.Value}"))
                    {
                        return PropValues.AssetRef($"{ctx.MapId}.{id.Value}");
                    }

                    ctx.Warn("gameplay.dangling", $"actor {owner} refers to {id.Value}, which is not in the map; written as None",
                        ObjectRef.Actor(owner));
                    return SvJsonNode.Null;
                case SvJsonObject o:
                {
                    var copy = new SvJsonObject();
                    foreach ((string key, SvJsonNode value) in o)
                    {
                        // A value the import could not type means nothing to the game.
                        if (!PropValues.IsRaw(value))
                        {
                            copy.Add(key, Resolve(value, owner));
                        }
                    }

                    return copy;
                }

                case SvJsonArray a:
                    return new SvJsonArray(a.Select(item => Resolve(item, owner)));
                default:
                    return node;
            }
        }
    }
}
