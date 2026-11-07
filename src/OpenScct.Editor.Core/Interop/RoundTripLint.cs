using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Interop;

/// <summary>
/// "Round-trip compatible": the checks a project must pass for the stock editor to import its T3D and the original
/// game to play the result. Errors are what the original rejects or loses; warnings are what it may change.
/// Codes start with <c>roundtrip.</c>.
/// </summary>
public static class RoundTripLint
{
    /// <summary>UE2 FPoly MAX_VERTICES.</summary>
    public const int MaxPolyVertices = 16;

    /// <summary>UE2 FBspNode zone limit (zone 0 included).</summary>
    public const int MaxZones = 64;

    /// <summary>Half of UE2 WORLD_MAX.</summary>
    public const double WorldExtent = 262144;

    /// <summary>Plane and convexity tolerance (uu), the stock FPoly import's own.</summary>
    public const double Tolerance = 0.1;

    /// <param name="wholeMap">False for a selection (copied objects): only the checks of the objects themselves run, not
    /// the map's (name, zone count, baked world, map settings).</param>
    /// <param name="assets">The asset catalogue (content plus the project's own assets) the references are resolved
    /// against; null skips the asset checks.</param>
    /// <param name="resolver">Where the map's own assets come from (<see cref="Original.LocalAssets.Resolver"/>).</param>
    /// <param name="projectFolder">The project's folder, for the outfit files the outfits name.</param>
    /// <param name="carriesAssets">True for the export for the original editor, which imports the map's own assets it
    /// has files for and makes the RE+ actors an authored map lacks; a plain T3D carries neither.</param>
    /// <param name="handoff">For a selection, the whole map's conveyor handoff (what its converted conveyors are written
    /// as); null works it out from <paramref name="p"/>.</param>
    public static List<Finding> Check(MapProject p, UeSchema? schema, bool wholeMap = true, AssetCatalog? assets = null, AssetResolver? resolver = null,
        string? projectFolder = null, bool carriesAssets = false, ConveyorHandoff? handoff = null)
    {
        var f = new List<Finding>();
        if (wholeMap)
        {
            CheckMapName(p, f);
        }

        CheckNames(p, f);
        foreach (Brush b in p.Brushes)
        {
            CheckShape(b.Shape, ObjectRef.ForBrush(b), b.LocalToWorld, f, AsImported(b), BakedContent.IsRecovered(b));
        }

        foreach (Actor a in p.Actors.Where(a => a.Shape is not null))
        {
            CheckShape(a.Shape!, ObjectRef.Actor(a.Id), a.LocalToWorld, f, BakedContent.ShapeAsImported(a));
        }

        CheckText(p, wholeMap, f);
        CheckLimits(p, schema, wholeMap, handoff ?? ConveyorHandoff.Of(p), f);
        CheckMapClasses(p, schema, f);
        CheckGes(p, f);
        if (assets is not null)
        {
            CheckAssets(p, assets, resolver ?? AssetResolver.Empty, f, projectFolder, carriesAssets);
        }

        if (!wholeMap)
        {
            return f;
        }

        if (p.Baked is not null)
        {
            f.Add(Finding.Warning("roundtrip.bakedWorld",
                $"the level geometry is a baked world from {Path.GetFileName(p.Baked.SourceMap.TrimEnd('/', '\\'))}: T3D carries brushes only, so the export has "
                + $"{p.Brushes.Count} brushes and none of the baked BSP"));
        }

        CheckLifted(p, f, projectFolder, carriesAssets);
        f.Add(Finding.Info("roundtrip.mapSettings",
            "map settings per mode, interface pictures and briefing texts live in the <Map>-i.utc package: set them in the stock editor after import"));
        return f;
    }

    /// <summary>The project's outfits and match rules: T3D carries them only as RE+ actors, the ones an .sdc import
    /// lifted (rewritten when the data changed, <see cref="T3d.LiftedWriteBack"/>) or, in the export for the original
    /// editor, ones it makes for a map without them. (Converted conveyors edited since import are lowered instead:
    /// CheckConveyors.)</summary>
    private static void CheckLifted(MapProject p, List<Finding> f, string? projectFolder, bool carriesAssets)
    {
        var notes = new List<Finding>();
        foreach ((string kind, string what, bool has) in new (string, string, bool)[]
                 {
                     ("outfits", "outfits", !p.Outfits.IsEmpty),
                     ("match", "player counts", p.Actors.Any(a => a.Match is not null && !BakedContent.IsLifted(a))),
                 })
        {
            List<Actor> lifted = p.Actors.Where(a => BakedContent.LiftedData(a) == kind).ToList();
            if (lifted.Count > 0)
            {
                if (T3d.LiftedWriteBack.Changed(p, kind))
                {
                    f.Add(Finding.Info("roundtrip.liftedData",
                        $"the map's {what} changed after import: the export writes them into {string.Join(", ", lifted.Select(a => a.Id))}",
                        lifted.Select(a => ObjectRef.Actor(a.Id)).ToArray()));
                    foreach (Actor a in lifted)
                    {
                        T3d.LiftedWriteBack.Props(p, a, notes, projectFolder);
                    }
                }
            }
            else if (has && !carriesAssets)
            {
                f.Add(Finding.Info(kind == "outfits" ? "roundtrip.outfits" : "roundtrip.matchRules",
                    $"the map's {what} are OpenSCCT data that T3D carries only in RE+'s actors, which this map does not have: a plain T3D gives the original "
                    + $"editor's map none (the export for the original editor makes {(kind == "outfits" ? T3d.LiftedWriteBack.OutfitsClass : T3d.LiftedWriteBack.MatchClass)}0 for them)"));
            }
        }

        if (carriesAssets)
        {
            List<Actor> made = T3d.LiftedWriteBack.Made(p, notes, projectFolder);
            foreach (Actor a in made)
            {
                f.Add(Finding.Info("roundtrip.madeActor",
                    $"the map has no RE+ {(BakedContent.LiftedData(a) == "outfits" ? "outfit" : "player-count")} actor: the export makes {a.Id} ({a.ShortClass}) for its "
                    + $"{(BakedContent.LiftedData(a) == "outfits" ? "outfits" : "player counts")}, in <Map>-MapClasses.t3d"));
            }
        }

        // Two RE+ actors of one kind (an outfit actor and its older name) give the same notes: one each, with every object.
        foreach (IGrouping<(string, string), Finding> g in notes.GroupBy(n => (n.Code, n.Message)))
        {
            Finding first = g.First();
            f.Add(new Finding(first.Severity, first.Code, first.Message, g.SelectMany(n => n.Objects).Distinct(ObjectRefComparer.Instance).ToArray()));
        }
    }

    /// <summary>True when a brush is exactly as an imported map had it (the hash its import stored still matches). A
    /// brush recovered from a compiled BSP is not the map's own, so never counts.</summary>
    public static bool AsImported(Brush b)
    {
        if (b.Source is not { Length: > 0 } s || s[0] != '{' || !s.Contains("hash", StringComparison.Ordinal) || BakedContent.IsRecovered(b))
        {
            return false;
        }

        try
        {
            var src = SvJson.Parse(s) as SvJsonObject;
            string? hash = ((src?["sdc"] as SvJsonObject)?["hash"] ?? (src?["t3d"] as SvJsonObject)?["hash"]) is SvJsonString h ? h.Value : null;
            return hash is not null && hash == BakedContent.Hash(b);
        }
        catch (OpenScct.Content.SvMapFormatException)
        {
            return false;
        }
    }

    /// <summary>The map's own classes (an .sdc import keeps their source), RE+'s lifted actors and actors of the
    /// map's package: the export hands them over with their sources instead of failing on them.</summary>
    public static bool IsOwnClass(MapProject p, Actor a, IReadOnlyDictionary<string, MapClass> own) =>
        BakedContent.IsLifted(a) || MapClasses.IsMapClass(p, a, own)
        || Sdc.SdcOverlay.LiftedClasses.Contains(a.ShortClass, StringComparer.OrdinalIgnoreCase);

    /// <summary>Actors of classes the map defines itself: the stock editor's new map has none of them, so they go in a
    /// second file to paste once the classes exist.</summary>
    private static void CheckMapClasses(MapProject p, UeSchema? schema, List<Finding> f)
    {
        Dictionary<string, MapClass> own = MapClasses.Of(p);
        foreach (IGrouping<string, Actor> g in p.Actors.Where(a => IsMapClassActor(p, a, schema, own) && !IsLiftedBelt(a)).GroupBy(a => a.ShortClass, StringComparer.OrdinalIgnoreCase))
        {
            ObjectRef[] refs = g.Select(a => ObjectRef.Actor(a.Id)).ToArray();
            MapClass? known = own.GetValueOrDefault(g.Key);
            string how = MapClassHint(g.Key, known);
            // RE+'s own tools make their current classes; any other is compiled from its source, which is not sure to work.
            bool covered = how.StartsWith("RE+", StringComparison.Ordinal);
            f.Add(new Finding(covered ? Severity.Info : Severity.Warning, "roundtrip.mapClass",
                string.Create(CultureInfo.InvariantCulture,
                    $"{refs.Length} actor(s) of {g.Key}, a class the map defines itself: a new map in the stock editor lacks the class, and its "
                    + $"import skips them until {how} (the export for the original editor puts them in <Map>-MapClasses.t3d for that)"),
                refs));
        }
    }

    /// <summary>True for an actor whose class the stock editor's new map lacks until the map's classes are made.</summary>
    public static bool IsMapClassActor(MapProject p, Actor a, UeSchema? schema, IReadOnlyDictionary<string, MapClass> own) =>
        !ConveyorDesign.Is(a) && IsOwnClass(p, a, own) && (schema?.ResolveClass(a.Class) is null || MapClasses.IsMapClass(p, a, own));

    /// <summary>How the original editor gets a map class back. RE+'s tools make only their current classes
    /// (SCharacterOutfits, SMatchSizeRules), not the older names an older RE+ left in a map.</summary>
    public static string MapClassHint(string cls, MapClass? known) =>
        cls.Equals(T3d.LiftedWriteBack.OutfitsClass, StringComparison.OrdinalIgnoreCase)
            ? "RE+ Tools > Character Skins has made the class"
            : cls.Equals(T3d.LiftedWriteBack.MatchClass, StringComparison.OrdinalIgnoreCase)
                ? "RE+'s player-count rules have made the class"
                : known?.Script is not null
                    ? $"the class is compiled from Classes\\{cls}.uc (CLASS LOAD, then SCRIPT MAKE; RE+ compiles map classes through its own patch of the "
                      + "compiler, so check the class exists before importing its actors)"
                    : "the class exists (the import kept no source for it: bring it from the original map)";

    private static void CheckMapName(MapProject p, List<Finding> f)
    {
        if (!Limits.IsMapName(p.Id))
        {
            f.Add(Finding.Error("roundtrip.mapName",
                $"the original game lists only maps whose name is exactly 5 letters or digits (e.g. ShipD); this map is '{p.Id}'"));
        }
    }

    private static void CheckNames(MapProject p, List<Finding> f)
    {
        var seen = new Dictionary<string, ObjectRef>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectRef r in p.Brushes.Select(b => ObjectRef.ForBrush(b)).Concat(p.Actors.Select(a => ObjectRef.Actor(a.Id))))
        {
            if (!InteropValues.IsValidName(r.Id))
            {
                f.Add(Finding.Error("roundtrip.name", $"'{r.Id}' is not a UE2 object name (ASCII letters, digits and _, at most 63, not starting with a digit)", r));
            }

            if (!seen.TryAdd(r.Id, r))
            {
                f.Add(Finding.Error("roundtrip.duplicateName", $"'{r.Id}' is used by two objects (UE2 names ignore case)", seen[r.Id], r));
            }
        }

        foreach (Ge g in p.Ges.Where(g => !g.Points.All(InBounds)))
        {
            f.Add(Finding.Warning("roundtrip.outsideWorld", $"GE {g.Id} is outside the UE2 world (±{WorldExtent})", ObjectRef.Ge(g.Id)));
        }
    }

    /// <param name="asImported">The brush is as an imported map had it: what the original editor itself saved is a
    /// warning, not an error.</param>
    /// <param name="recovered">The brush was recovered from a compiled BSP (not the original's own): its findings stay
    /// errors, worded as such.</param>
    private static void CheckShape(BrushShape shape, ObjectRef owner, Func<Vec3, Vec3> toWorld, List<Finding> f, bool asImported, bool recovered = false)
    {
        if (shape.Faces.Count == 0)
        {
            f.Add(Finding.Warning("roundtrip.emptyBrush", $"{owner.Id} has no faces", owner));
            return;
        }

        int degenerate = 0, tooMany = 0, nonPlanar = 0, nonConvex = 0, slivers = 0, badMaterial = 0;
        foreach (BrushFace face in shape.Faces)
        {
            List<Vec3> v = face.Vertices;
            Vec3 n = face.Normal();
            if (v.Count < 3 || n == Vec3.Zero)
            {
                degenerate++;
                continue;
            }

            if (v.Count > MaxPolyVertices)
            {
                tooMany++;
            }

            double w = Vec3.Dot(n, v[0]);
            if (v.Any(p => Math.Abs(Vec3.Dot(n, p) - w) > Tolerance))
            {
                nonPlanar++;
            }

            for (int i = 0; i < v.Count; i++)
            {
                Vec3 a = v[i], b = v[(i + 1) % v.Count], c = v[(i + 2) % v.Count];
                if ((b - a).Length < Tolerance)
                {
                    slivers++;
                }

                // Clockwise seen from outside: each turn's cross product points along the normal.
                if (Vec3.Dot(Vec3.Cross(b - a, c - b), n) < -Tolerance)
                {
                    nonConvex++;
                    break;
                }
            }

            // Spaces are written in quotes; a quote or a non-ASCII character cannot be.
            if (face.Material is { } m && (!m.All(ch => ch is >= ' ' and < (char)127 and not '\'' and not '"') || m.Length == 0))
            {
                badMaterial++;
            }
        }

        Severity shapeError = asImported ? Severity.Warning : Severity.Error;
        string original = asImported ? ", as in the imported map (written back unchanged)"
            : recovered ? ", as recovered from the compiled BSP (float noise of its cooked points)" : "";
        Add(f, degenerate, shapeError, "roundtrip.degenerateFace", $"{owner.Id}: {{0}} face(s) with fewer than 3 distinct corners{original}", owner);
        Add(f, tooMany, Severity.Error, "roundtrip.faceVertices", $"{owner.Id}: {{0}} face(s) with more than {MaxPolyVertices} corners (UE2 FPoly limit)", owner);
        Add(f, nonPlanar, shapeError, "roundtrip.nonPlanarFace", $"{owner.Id}: {{0}} face(s) not flat within {Tolerance} uu{original}", owner);
        Add(f, nonConvex, shapeError, "roundtrip.nonConvexFace", $"{owner.Id}: {{0}} face(s) not convex{original}", owner);
        Add(f, slivers, Severity.Warning, "roundtrip.sliver", $"{owner.Id}: {{0}} edge(s) shorter than {Tolerance} uu (the stock import deletes them)", owner);
        Add(f, badMaterial, Severity.Error, "roundtrip.materialName", $"{owner.Id}: {{0}} face material path(s) with quotes or non-ASCII characters", owner);

        // A convex brush has every corner behind every face plane.
        List<Vec3> corners = shape.Faces.SelectMany(x => x.Vertices).ToList();
        bool concave = shape.Faces.Any(face =>
        {
            Vec3 n = face.Normal();
            if (n == Vec3.Zero || face.Vertices.Count == 0)
            {
                return false;
            }

            double w = Vec3.Dot(n, face.Vertices[0]);
            return corners.Any(c => Vec3.Dot(n, c) - w > Tolerance);
        });
        if (concave)
        {
            f.Add(Finding.Warning("roundtrip.concaveBrush", $"{owner.Id} is not convex: the stock CSG may build it wrongly; split it into convex pieces", owner));
        }

        if (!corners.Select(toWorld).All(InBounds))
        {
            f.Add(Finding.Warning("roundtrip.outsideWorld", $"{owner.Id} reaches outside the UE2 world (±{WorldExtent})", owner));
        }
    }

    private static void CheckText(MapProject p, bool wholeMap, List<Finding> f)
    {
        IEnumerable<(ObjectRef? Owner, PropSet Props)> sets = p.Actors.Select(a => ((ObjectRef?)ObjectRef.Actor(a.Id), a.Props))
            .Concat(p.Brushes.Select(b => ((ObjectRef?)ObjectRef.ForBrush(b), b.Props)))
            .Concat(wholeMap ? [(null, p.LevelInfo)] : []);
        foreach ((ObjectRef? owner, PropSet props) in sets)
        {
            foreach ((string key, SvJsonNode value) in props)
            {
                string? text = Strings(value).FirstOrDefault(s => s.Any(c => c > 127));
                if (text is null)
                {
                    continue;
                }

                bool latin1 = text.All(c => c <= 255);
                string where = owner is { } o ? o.Id : "LevelInfo";
                var finding = new Finding(latin1 ? Severity.Warning : Severity.Error, "roundtrip.text",
                    latin1 ? $"{where}.{key} has non-ASCII text; the stock editor reads it in the system code page"
                           : $"{where}.{key} has characters outside Latin-1, which the T3D file cannot carry",
                    owner is { } r ? [r] : []);
                f.Add(finding);
            }
        }
    }

    private static IEnumerable<string> Strings(SvJsonNode node) => node switch
    {
        SvJsonString s => [s.Value],
        SvJsonArray a => a.SelectMany(Strings),
        SvJsonObject o => o.SelectMany(kv => Strings(kv.Value)),
        _ => [],
    };

    private static void CheckLimits(MapProject p, UeSchema? schema, bool wholeMap, ConveyorHandoff handoff, List<Finding> f)
    {
        List<Actor> zones = p.Actors.Where(a => schema?.IsA(a.Class, "Engine.ZoneInfo") ?? a.ShortClass.EndsWith("ZoneInfo", StringComparison.Ordinal)).ToList();
        if (wholeMap && zones.Count + 1 > MaxZones)
        {
            f.Add(Finding.Error("roundtrip.zones",
                $"{zones.Count} zone actors: UE2 levels hold at most {MaxZones} zones including the default zone",
                zones.Skip(MaxZones - 1).Select(a => ObjectRef.Actor(a.Id)).ToArray()));
        }

        List<Actor> lights = p.Actors.Where(a => a.ShortClass.Equals("STriggerLight", StringComparison.OrdinalIgnoreCase)).ToList();
        if (wholeMap && lights.Count > Limits.TriggerLightWarning)
        {
            f.Add(Finding.Warning("roundtrip.triggerLights",
                $"{lights.Count} STriggerLights: more than {Limits.TriggerLightWarning} needs the raised cap of {Limits.TriggerLightCap}",
                lights.Skip(Limits.TriggerLightWarning).Select(a => ObjectRef.Actor(a.Id)).ToArray()));
        }

        foreach (Actor a in p.Actors.Where(a => !InBounds(a.Location)))
        {
            f.Add(Finding.Warning("roundtrip.outsideWorld", $"{a.Id} is outside the UE2 world (±{WorldExtent})", ObjectRef.Actor(a.Id)));
        }

        if (schema is not null)
        {
            // Conveyors are lowered to RE+'s belts on export, and the RE+ belts an .sdc import lifted are RE+'s classes as
            // well (whichever map package holds them, T3D names them by class name): CheckConveyors covers both. Classes
            // the map defines itself are CheckMapClasses'.
            Dictionary<string, MapClass> own = MapClasses.Of(p);
            foreach (Actor a in p.Actors.Where(a => !ConveyorDesign.Is(a) && !IsLiftedBelt(a) && schema.ResolveClass(a.Class) is null
                && !IsOwnClass(p, a, own)))
            {
                f.Add(Finding.Error("roundtrip.unknownClass", $"{a.Id}: class {a.Class} does not exist in the original game's packages", ObjectRef.Actor(a.Id)));
            }
        }

        CheckConveyors(p, handoff, f);
    }

    /// <summary>
    /// What RE+'s conveyor belts cannot express, and the classes the map needs before the import. Conveyors an .sdc
    /// import converted from RE+ belts are not lowered while they and their belts are as imported: those belts are
    /// written as imported (<see cref="ConveyorHandoff"/>). A converted group changed since is lowered like any
    /// conveyor, in place of its imported belts.
    /// </summary>
    private static void CheckConveyors(MapProject p, ConveyorHandoff handoff, List<Finding> f)
    {
        var ids = new HashSet<string>(p.Actors.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        foreach (ConveyorHandoff.Group g in handoff.Groups.Where(g => !g.AsImported && g.Conveyors.Concat(g.Belts).Any(ids.Contains)))
        {
            ObjectRef[] refs = g.Conveyors.Concat(g.Belts).Where(id => p.FindActor(id) is not null).Select(ObjectRef.Actor).ToArray();
            f.Add(Finding.Info("roundtrip.convertedConveyors", g.Conveyors.Count == 0
                ? $"the conveyors converted from RE+ belts {string.Join(", ", g.Belts)} were deleted: the export leaves those belts out"
                : $"{string.Join(", ", g.Conveyors)} changed since the .sdc import ({g.Change}): the export writes them as new RE+ belts in "
                  + $"place of the imported {string.Join(", ", g.Belts)}", refs));
        }

        List<Actor> conveyors = p.Actors.Where(a => ConveyorDesign.Is(a) && !handoff.Carried.Contains(a.Id)).ToList();
        var tread = TreadTags(p);
        foreach (Actor a in conveyors)
        {
            var found = new List<Finding>();
            ReConveyors.Lower(p, a, found);
            // AeroD's tread script, written as imported, still pans the meshes it tags.
            bool panned = ConveyorDesign.Read(a).ScrollTargets.All(t => p.FindActor(t.Target) is { } m && m.Props.GetString("Tag") is { } tag
                && tread.Contains(tag));
            f.AddRange(found.Where(x => !(panned && x.Code == $"{ReConveyors.Code}.scroll")));
        }

        // A selection writes the belts of the carried conveyors it holds with them.
        HashSet<string> carriedBelts = handoff.BeltsOf(p.Actors);
        List<string> belts = p.Actors.Where(a => IsLiftedBelt(a) && handoff.Writes(a)).Select(a => a.Id).Union(carriedBelts, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (conveyors.Count + belts.Count > 0)
        {
            var parts = new List<string>();
            if (conveyors.Count > 0)
            {
                parts.Add($"{conveyors.Count} conveyors are exported as RE+ 2.8.0 ConveyorBelt chains");
            }

            if (belts.Count > 0)
            {
                parts.Add($"{belts.Count} RE+ conveyor belts are written as imported");
            }

            f.Add(Finding.Info("roundtrip.conveyorClasses",
                $"{string.Join(" and ", parts)}: the map needs RE+'s conveyor classes before the import (in RE+, Tools > Add Conveyor "
                + "Belt once, then delete that belt)", conveyors.Select(a => a.Id).Concat(belts).Select(ObjectRef.Actor).ToArray()));
        }
    }

    /// <summary>The tags whose meshes a lifted AerTreadScroll pans (its <c>Runs</c>).</summary>
    private static HashSet<string> TreadTags(MapProject p)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors.Where(a => BakedContent.IsLifted(a) && a.ShortClass == "AerTreadScroll"))
        {
            SvJsonNode? runs = a.Props["Runs"] is SvJsonObject o && o["$elements"] is SvJsonObject e ? e : a.Props["Runs"];
            IEnumerable<SvJsonNode?> values = runs switch
            {
                SvJsonObject els => els.Select(kv => kv.Value),
                SvJsonArray arr => arr,
                _ => [],
            };
            tags.UnionWith(values.OfType<SvJsonString>().Select(s => s.Value).Where(s => s.Length > 0));
        }

        return tags;
    }

    /// <summary>An RE+ conveyor actor (belt, motor, item or roller) an .sdc import lifted.</summary>
    public static bool IsLiftedBelt(Actor a) =>
        BakedContent.IsLifted(a) && a.ShortClass.StartsWith("Conveyor", StringComparison.OrdinalIgnoreCase);

    private static void CheckGes(MapProject p, List<Finding> f)
    {
        List<Ge> masked = p.Ges.Where(g => g.Mask != 0 || g.Platform != 0).ToList();
        if (masked.Count > 0)
        {
            f.Add(Finding.Warning("roundtrip.geIds",
                string.Create(CultureInfo.InvariantCulture,
                    $"{masked.Count} GEs have a GE ID or platform: GE ADD lines cannot carry them, so they import as always on (set them again with RE+ GE tools)"),
                masked.Take(50).Select(g => ObjectRef.Ge(g.Id)).ToArray()));
        }

        // GE ADD reads the kind and the points only: the options a T3D import kept and the flag word are not carried.
        List<Ge> optioned = p.Ges.Where(g => HasOptions(g) || (g.Flags & ~0x2) != 0).ToList();
        if (optioned.Count > 0)
        {
            f.Add(Finding.Warning("roundtrip.geOptions",
                string.Create(CultureInfo.InvariantCulture,
                    $"{optioned.Count} GEs have options (NOSLIDE, NOHOIST, NOGETOUT) or flags: the stock GE ADD reads only the kind and the points, so they import without them"),
                optioned.Take(50).Select(g => ObjectRef.Ge(g.Id)).ToArray()));
        }

        foreach (Ge g in p.Ges.Where(g => g.Points.Count != (GeKinds.HasThirdPoint(g.Kind) ? 3 : 2)))
        {
            f.Add(Finding.Error("roundtrip.gePoints", $"GE {g.Id} ({g.Kind}) has {g.Points.Count} points", ObjectRef.Ge(g.Id)));
        }
    }

    private static bool HasOptions(Ge g)
    {
        if (g.Source is not { Length: > 0 } src || src[0] != '{' || !src.Contains("options", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return (SvJson.Parse(src) as SvJsonObject)?["options"] is SvJsonObject o && o.Any(kv => kv.Value is not SvJsonString { Value: "UNUSED" });
        }
        catch (OpenScct.Content.SvMapFormatException)
        {
            return false;
        }
    }

    /// <summary>Properties that name objects the original keeps in the map's package beside its assets (made in the
    /// editor, not imported from files), with what they are.</summary>
    private static readonly Dictionary<string, string> s_levelObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZoneEffect"] = "a zone's reverb settings, an I3DL2Listener",
        ["SoftBody"] = "a cloth patch, an ESBPatch soft body",
    };

    /// <summary>The assets the stock editor resolves on import: a static mesh it cannot find opens a modal box and
    /// the actor loses its mesh; the map's own assets must be imported first (the export for the original editor does
    /// that for those it has files for, a plain T3D for none); stock streamed sounds play only in the map whose stream
    /// bank holds them.</summary>
    private static void CheckAssets(MapProject p, AssetCatalog assets, AssetResolver resolver, List<Finding> f, string? projectFolder, bool carriesAssets)
    {
        var objects = new Dictionary<string, List<ObjectRef>>(StringComparer.OrdinalIgnoreCase);
        var missingMeshes = new Dictionary<string, List<ObjectRef>>(StringComparer.OrdinalIgnoreCase);
        var local = new Dictionary<string, List<ObjectRef>>(StringComparer.OrdinalIgnoreCase);
        var streamed = new Dictionary<string, List<ObjectRef>>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, List<ObjectRef>>(StringComparer.OrdinalIgnoreCase);
        var unimported = new Dictionary<string, List<ObjectRef>>(StringComparer.OrdinalIgnoreCase);
        // The outfit textures come from the project's outfits (the export writes them next to the other textures).
        var outfitTextures = new HashSet<string>(T3d.LiftedWriteBack.Textures(p, projectFolder).Select(t => Original.LocalAssets.LevelPath(t.Path)),
            StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string key, ObjectRef owner) in AssetRefs(p, projectFolder))
        {
            AssetEntry? e = FindAsset(p, assets, path, key);
            if (IsLocalPath(p, path))
            {
                if (key is "SpyModel" or "MercModel")
                {
                    Add(models, path, owner);
                }
                else if (s_levelObjects.TryGetValue(key, out string? what))
                {
                    Add(objects, $"{path} ({what})", owner);
                }
                else if (outfitTextures.Contains(Original.LocalAssets.LevelPath(path))
                    || Original.LocalAssets.Find(p, assets, resolver, path, key) is { HasSource: true })
                {
                    if (!carriesAssets)
                    {
                        Add(unimported, path, owner);
                    }
                }
                else
                {
                    Add(local, path, owner);
                }
            }
            else if (e is null && key.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
            {
                Add(missingMeshes, path, owner);
            }

            if (e is { Kind: AssetKind.Sound, SoundKind: "stream" or "multichannel" } && !e.UsedIn.ContainsKey(p.Id))
            {
                Add(streamed, path, owner);
            }
        }

        foreach ((string path, List<ObjectRef> refs) in missingMeshes)
        {
            f.Add(Finding.Warning("roundtrip.meshMissing",
                $"{path} is not in the original game's packages the editor knows: the stock import stops on a message box (\"contains no static mesh\") for "
                + $"{string.Join(", ", refs.Take(5).Select(r => r.Id))}{(refs.Count > 5 ? ", ..." : "")} and the actor loses its mesh; "
                + "open the package that holds it in the stock editor before the import", refs.ToArray()));
        }

        foreach ((string path, List<ObjectRef> refs) in objects)
        {
            f.Add(Finding.Warning("roundtrip.mapObject",
                $"{path} is an object of the map's own package, not a file the editor can import: the T3D cannot carry it, so "
                + $"{string.Join(", ", refs.Take(5).Select(r => r.Id))}{(refs.Count > 5 ? ", ..." : "")} lose it; make it again in the stock editor",
                refs.ToArray()));
        }

        foreach ((string path, List<ObjectRef> refs) in local)
        {
            f.Add(Finding.Warning("roundtrip.mapLocalAsset",
                $"{path} is the map's own asset and the editor has no file for it: the export cannot import it into the new map, so its users lose it",
                refs.ToArray()));
        }

        if (unimported.Count > 0)
        {
            f.Add(Finding.Warning("roundtrip.mapAssetNotCarried",
                string.Create(CultureInfo.InvariantCulture,
                    $"{unimported.Count} assets of the map's own package ({string.Join(", ", unimported.Keys.Take(5))}{(unimported.Count > 5 ? ", ..." : "")}) are not in a plain T3D, and a new map in the stock editor lacks them: use the export for the original editor, which imports them"),
                unimported.Values.SelectMany(r => r).Distinct(ObjectRefComparer.Instance).Take(50).ToArray()));
        }

        foreach ((string path, List<ObjectRef> refs) in models)
        {
            f.Add(Finding.Warning("roundtrip.outfitModel",
                $"{path} is the map's own skeletal mesh (an outfit model): the export cannot write skeletal meshes, so the new map lacks it until it is "
                + "imported again (a PSK, with RE+ Tools > Character Skins)", refs.ToArray()));
        }

        foreach ((string path, List<ObjectRef> refs) in streamed)
        {
            f.Add(Finding.Warning("roundtrip.streamedSound",
                $"{path} is a stock streamed sound: it lives in its map's stream bank (Sounds\\<Map>.uas) and is silent in a map of another name",
                refs.ToArray()));
        }
    }

    /// <summary>Every asset reference of the map as the export writes it (actor and LevelInfo properties, nested
    /// ones too, a lifted RE+ actor's as written back, and face materials), with the property that holds it. A converted
    /// conveyor written as its imported RE+ belts (and a belt left out for the belts lowered in its place) is not
    /// written itself: the belts written carry its assets (<see cref="ConveyorHandoff"/>).</summary>
    public static IEnumerable<(string Path, string Key, ObjectRef Owner)> AssetRefs(MapProject p, string? projectFolder = null)
    {
        ConveyorHandoff handoff = ConveyorHandoff.Of(p);
        foreach (Actor a in p.Actors.Where(handoff.Writes))
        {
            // An emitter's own sub-objects are written with it. The assets they use still need to be available.
            HashSet<string> own = SubObjectNames(a);
            SvJsonArray? systems = Compile.EmitterBuild.SystemsOf(a, p);
            foreach ((string key, SvJsonNode value) in T3d.LiftedWriteBack.Props(p, a, projectFolder: projectFolder).Where(kv => Original.LocalAssets.IsAssetProperty(kv.Key)))
            {
                if (systems is not null && key.Equals("Emitters", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string path in AssetsIn(value).Where(x => !own.Contains(AssetPaths.Split(x).Name)))
                {
                    yield return (path, key, ObjectRef.Actor(a.Id));
                }
            }

            if (systems is not null)
            {
                foreach (SvJsonObject part in Compile.EmitterBuild.ValidSystems(systems))
                {
                    var props = (SvJsonObject)part["props"]!;
                    foreach ((string key, SvJsonNode value) in props.Where(kv => Original.LocalAssets.IsAssetProperty(kv.Key)))
                    {
                        foreach (string path in AssetsIn(value))
                        {
                            yield return (path, key, ObjectRef.Actor(a.Id));
                        }
                    }
                }
            }

            foreach (string m in a.Shape?.Faces.Select(x => x.Material).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
            {
                yield return (m, "Material", ObjectRef.Actor(a.Id));
            }
        }

        foreach (Brush b in p.Brushes)
        {
            foreach (string m in b.Shape.Faces.Select(x => x.Material).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return (m, "Material", ObjectRef.ForBrush(b));
            }
        }

        foreach ((string key, SvJsonNode value) in p.LevelInfo.Where(kv => Original.LocalAssets.IsAssetProperty(kv.Key)))
        {
            foreach (string path in AssetsIn(value))
            {
                yield return (path, key, ObjectRef.Actor(T3d.T3dExport.LevelInfoName(p)));
            }
        }
    }

    private static HashSet<string> SubObjectNames(Actor a)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in T3d.T3dImport.ObjectsOf(a).SelectMany(block => block))
        {
            string t = line.TrimStart();
            int at = t.IndexOf(" Name=", StringComparison.OrdinalIgnoreCase);
            if (t.StartsWith("Begin Object", StringComparison.OrdinalIgnoreCase) && at > 0)
            {
                string rest = t[(at + 6)..];
                int end = rest.IndexOf(' ', StringComparison.Ordinal);
                names.Add(end < 0 ? rest : rest[..end]);
            }
        }

        return names;
    }

    private static IEnumerable<string> AssetsIn(SvJsonNode node) => node switch
    {
        SvJsonObject o when PropValues.AssetPath(o) is { } path => [path],
        SvJsonObject o => o.SelectMany(kv => AssetsIn(kv.Value)),
        SvJsonArray a => a.SelectMany(AssetsIn),
        _ => [],
    };

    /// <summary>True for a path in the map's own package (MyLevel, or the map's name as an .sdc import keeps it).</summary>
    public static bool IsLocalPath(MapProject p, string path)
    {
        string package = AssetPaths.Split(path).Package;
        return package.Equals(AssetPaths.LocalPackage, StringComparison.OrdinalIgnoreCase) || package.Equals(p.Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An asset by path; a map-local one under either of its package names.</summary>
    /// <param name="key">The property that refers to it: a StaticMesh property looks for a mesh first, a face (Material)
    /// for a material (the catalogue also matches by Package.Name, where a mesh and a material can meet).</param>
    public static AssetEntry? FindAsset(MapProject p, AssetCatalog assets, string path, string? key = null)
    {
        AssetKind? kind = key switch
        {
            "StaticMesh" => AssetKind.Mesh,
            "Material" => AssetKind.Material,
            _ => null,
        };
        AssetEntry? Find(string at) => (kind is { } k ? assets.Find(k, at) : null) ?? assets.Find(at);
        if (Find(path) is { } e)
        {
            return e;
        }

        if (!IsLocalPath(p, path))
        {
            return null;
        }

        (string package, string group, string name) = AssetPaths.Split(path);
        string other = package.Equals(AssetPaths.LocalPackage, StringComparison.OrdinalIgnoreCase) ? p.Id : AssetPaths.LocalPackage;
        return Find(group.Length > 0 ? $"{other}.{group}.{name}" : $"{other}.{name}");
    }

    private static void Add(Dictionary<string, List<ObjectRef>> map, string path, ObjectRef owner)
    {
        if (!map.TryGetValue(path, out List<ObjectRef>? list))
        {
            map[path] = list = [];
        }

        if (!list.Contains(owner))
        {
            list.Add(owner);
        }
    }

    private static void Add(List<Finding> f, int count, Severity severity, string code, string format, ObjectRef owner)
    {
        if (count > 0)
        {
            f.Add(new Finding(severity, code, string.Format(CultureInfo.InvariantCulture, format, count), [owner]));
        }
    }

    private static bool InBounds(Vec3 v) => Math.Abs(v.X) <= WorldExtent && Math.Abs(v.Y) <= WorldExtent && Math.Abs(v.Z) <= WorldExtent;
}
