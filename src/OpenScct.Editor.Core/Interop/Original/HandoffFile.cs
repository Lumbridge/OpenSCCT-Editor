using System.Security.Cryptography;
using System.Text;
using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>
/// <c>&lt;Map&gt;.openscct.json</c>, beside the T3D of an export for the original editor: what the original editor has
/// no place for (the map's identity, its settings per mode and briefing texts as the editor keeps them, the sources of
/// its own classes, its player counts and outfits as the editor keeps them, with the files they name under
/// <see cref="FilesFolder"/>). The T3D import reads it back with the T3D (<see cref="Apply"/>).
/// </summary>
public static class HandoffFile
{
    public const string Format = "osmap.handoff/1";

    public const string Suffix = ".openscct.json";

    /// <summary>The file the actors of the map's own classes go to, beside the map's T3D.</summary>
    public const string ClassesSuffix = "-MapClasses.t3d";

    public static string PathFor(string t3dPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(t3dPath)) ?? ".", Path.GetFileNameWithoutExtension(t3dPath) + Suffix);

    public static string ClassesPathFor(string t3dPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(t3dPath)) ?? ".", Path.GetFileNameWithoutExtension(t3dPath) + ClassesSuffix);

    /// <summary>The folder (under the export folder) of the files the handoff file names: outfit textures and models,
    /// picture files.</summary>
    public const string FilesFolder = "OpenSCCT";

    /// <summary>
    /// The handoff file for <paramref name="p"/>. <paramref name="carry"/> copies a file the map names (an outfit
    /// texture or model, a picture; given by full path, with the folder to put it in) beside the handoff file and
    /// returns its path relative to the handoff file's folder, or null when the file is missing (its path is then kept).
    /// <paramref name="assets"/> is where the project's own asset library was copied (relative), if it has one.
    /// <paramref name="made"/> are the RE+ actors the export made for the map (<see cref="LiftedWriteBack.Made"/>) and
    /// <paramref name="t3d"/> the bytes of the map's T3D, so an import can tell the T3D is the one exported.
    /// </summary>
    public static SvJsonObject ToJson(MapProject p, string? projectFolder, Func<string, string, string?> carry, string? assets,
        IReadOnlyList<Actor> made, byte[] t3d)
    {
        string? Carry(string path, string folder) =>
            Path.IsPathFullyQualified(path) ? carry(path, folder)
            : projectFolder is not null ? carry(Path.GetFullPath(Path.Combine(projectFolder, path)), folder)
            : null;

        var o = new SvJsonObject();
        o.Add("format", SvJsonNode.String(Format));
        o.Add("id", SvJsonNode.String(p.Id));
        o.Add("t3d", SvJsonNode.String(Convert.ToHexStringLower(SHA256.HashData(t3d))));
        o.Add("title", SvJsonNode.String(p.Title));
        o.Add("mapInfo", p.MapInfo.ToJson());
        var settings = new SvJsonObject();
        foreach ((string mode, PropSet props) in p.MapSettings)
        {
            settings.Add(mode, props.ToJson());
        }

        o.Add("mapSettings", settings);
        if (IdentityJson.ToJson(p.Identity) is { Count: > 0 } identity)
        {
            o.Add("identity", Paths(identity, (key, value) => key == "file" ? Carry(value, "pictures") : null));
        }

        if (p.Extra[MapClasses.ExtraKey] is SvJsonObject classes)
        {
            o.Add(MapClasses.ExtraKey, classes);
        }

        if (p.Extra[Svmap.IdentityImport.CatalogTitleKey] is SvJsonObject catalogTitle)
        {
            o.Add(Svmap.IdentityImport.CatalogTitleKey, catalogTitle);
        }

        // The export gives the LevelInfo the map's title when it has none, for the stock editor's map list.
        o.Add("levelInfoTitle", p.LevelInfo.GetString("Title") is not null ? SvJsonNode.True : SvJsonNode.False);

        // The stock editor keeps player counts and outfits only in RE+'s actors, and only what RE+ can hold.
        var match = new SvJsonObject();
        foreach (Actor a in p.Actors.Where(a => a.Match is not null && !BakedContent.IsLifted(a)))
        {
            match.TryAdd(a.Id, MatchJson.ToJson(a.Match!));
        }

        if (match.Count > 0)
        {
            o.Add("match", match);
        }

        if (!p.Outfits.IsEmpty)
        {
            o.Add("outfits", Paths(OutfitsJson.ToJson(p.Outfits), (key, value) => key is "texture" or "glb" ? Carry(value, "outfits") : null));
        }

        // The RE+ actors that stand for them, as written: one still the same at import stands for the data above again.
        var lifted = new SvJsonObject();
        foreach (Actor a in p.Actors.Where(BakedContent.IsLifted))
        {
            lifted.TryAdd(a.Id, SvJsonNode.String(PropsHash(LiftedWriteBack.Props(p, a, projectFolder: projectFolder))));
        }

        foreach (Actor a in made)
        {
            lifted.TryAdd(a.Id, SvJsonNode.String(PropsHash(a.Props)));
        }

        if (lifted.Count > 0)
        {
            o.Add("lifted", lifted);
        }

        if (made.Count > 0)
        {
            o.Add("made", new SvJsonArray(made.Select(a => SvJsonNode.String(a.Id))));
        }

        // The brushes and shapes still as an imported original had them: the lint lets their faces through, and an
        // import of this export marks only these again (the others are the editor's own).
        var asImported = p.Brushes.Where(RoundTripLint.AsImported).Select(b => b.Id)
            .Concat(p.Actors.Where(BakedContent.ShapeAsImported).Select(a => a.Id)).ToList();
        if (asImported.Count > 0)
        {
            o.Add("asImported", new SvJsonArray(asImported.Select(SvJsonNode.String)));
        }

        // The brushes and shapes that have no model name: the T3D gives them one, which an import of it drops again.
        var unnamed = p.Brushes.Where(b => b.Props["Brush"] is null).Select(b => b.Id)
            .Concat(p.Actors.Where(a => a.Shape is not null && a.Props["Brush"] is null).Select(a => a.Id)).ToList();
        if (unnamed.Count > 0)
        {
            o.Add("unnamedModels", new SvJsonArray(unnamed.Select(SvJsonNode.String)));
        }

        if (assets is not null)
        {
            o.Add("assets", SvJsonNode.String(assets));
        }

        // The converted map the map's own assets have their files in (an .sdc import's), so a map imported from this
        // export can export them again on this machine.
        if (LocalAssets.SourceMap(p) is { } sourceMap)
        {
            o.Add(LocalAssets.SourceMapKey, SvJsonNode.String(sourceMap));
        }

        // A T3D holds a brush's polygons only: the builder that made them (cube, stairs...) and its settings come back
        // from here, with the corners they made, so a brush reshaped in the original editor stays as it is.
        var builders = new SvJsonObject();
        foreach ((string id, BrushShape shape) in p.Brushes.Select(b => (b.Id, b.Shape))
                     .Concat(p.Actors.Where(a => a.Shape is not null).Select(a => (a.Id, a.Shape!))))
        {
            if (shape.Builder is { } spec)
            {
                var entry = new SvJsonObject();
                entry.Add("builder", ModelJson.BuilderToJson(spec));
                entry.Add("corners", new SvJsonArray(Corners(shape).Select(v => SvJsonNode.Float((float)v))));
                builders.TryAdd(id, entry);
            }
        }

        if (builders.Count > 0)
        {
            o.Add("builders", builders);
        }

        return o;
    }

    /// <summary>The key of <see cref="MapProject.Extra"/> naming the asset library an import brought (copied into the
    /// project at its first save, <see cref="AdoptAssets"/>).</summary>
    public const string AssetsKey = "handoffAssets";

    /// <summary>First save of a map imported from an export: the project's asset library that came with it moves into
    /// the project (files already there are kept).</summary>
    public static void AdoptAssets(MapProject p, string projectFolder)
    {
        if (p.Extra[AssetsKey] is not SvJsonString { Value: var from })
        {
            return;
        }

        if (Directory.Exists(from))
        {
            string to = Path.Combine(Path.GetFullPath(projectFolder), ProjectAssets.Folder);
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            }
        }

        var extra = new SvJsonObject();
        foreach ((string key, SvJsonNode value) in p.Extra.Where(kv => kv.Key != AssetsKey))
        {
            extra.Add(key, value);
        }

        p.Extra = extra;
    }

    /// <summary>Puts a handoff file's data back into a project imported from the T3D beside it; false when there is
    /// none or it is not one.</summary>
    public static bool Apply(MapProject p, string t3dPath, List<Finding> findings)
    {
        string file = PathFor(t3dPath);
        if (!File.Exists(file))
        {
            return false;
        }

        SvJsonObject o;
        try
        {
            o = SvJson.Parse(File.ReadAllBytes(file), file) as SvJsonObject ?? throw new SvMapFormatException($"{file}: not an object");
        }
        catch (SvMapFormatException e)
        {
            findings.Add(Finding.Warning("import.handoff", $"{Path.GetFileName(file)} was not read: {e.Message}"));
            return false;
        }

        if ((o["format"] as SvJsonString)?.Value != Format)
        {
            return false;
        }

        // Files the handoff file names are relative to its folder: made full, so the first save copies them in.
        string folder = Path.GetDirectoryName(file)!;
        string? Full(string path) => Path.IsPathFullyQualified(path) ? null : Path.GetFullPath(Path.Combine(folder, path));

        // The T3D as exported, or one the original editor wrote over it (its Export to T3D): then what the T3D holds
        // (the LevelInfo's title, the shapes) is the original editor's, and only what it has no place for comes from here.
        bool exported = o["t3d"] is SvJsonString { Value: var t3dHash }
            && t3dHash == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(t3dPath)));
        if (o["title"] is SvJsonString title && (exported || p.LevelInfo.GetString("Title") is not { Length: > 0 }))
        {
            p.Title = title.Value;
        }

        if (exported)
        {
            ForgetImportMarks(p, (o["asImported"] as SvJsonArray ?? []).OfType<SvJsonString>().Select(m => m.Value));
            ForgetModelNames(p, (o["unnamedModels"] as SvJsonArray ?? []).OfType<SvJsonString>().Select(m => m.Value));
        }

        p.MapInfo = PropSet.FromJson(o["mapInfo"]);
        p.MapSettings.Clear();
        if (o["mapSettings"] is SvJsonObject settings)
        {
            foreach ((string mode, SvJsonNode props) in settings)
            {
                p.MapSettings[mode] = PropSet.FromJson(props);
            }
        }

        p.Identity = IdentityJson.FromJson(o["identity"] is SvJsonObject identity
            ? Paths(identity, (key, value) => key == "file" ? Full(value) : null)
            : null);
        if (o[MapClasses.ExtraKey] is SvJsonObject classes)
        {
            p.Extra.TryAdd(MapClasses.ExtraKey, classes);
        }

        if (o[Svmap.IdentityImport.CatalogTitleKey] is SvJsonObject catalogTitle)
        {
            p.Extra.TryAdd(Svmap.IdentityImport.CatalogTitleKey, catalogTitle);
        }

        if (o["levelInfoTitle"]?.Kind == SvJsonKind.False && p.LevelInfo.GetString("Title") == Svmap.IdentityImport.ImportedTitle(p))
        {
            p.LevelInfo.Remove("Title");
        }

        var restored = new List<string>();
        if (o["assets"] is SvJsonString { Value: var assets } && Full(assets) is { } library
            && File.Exists(Path.Combine(library, ProjectAssets.ManifestFile)))
        {
            p.Extra.TryAdd(AssetsKey, SvJsonNode.String(library));
            int relinked = Relink(p, library);
            restored.Add($"the map's own assets ({relinked} references)");
        }

        if (o[LocalAssets.SourceMapKey] is SvJsonString { Value: var sourceMap } && Directory.Exists(sourceMap))
        {
            p.Extra.TryAdd(LocalAssets.SourceMapKey, SvJsonNode.String(sourceMap));
        }

        RestoreMatchAndOutfits(p, o, Full, restored, findings);
        if (o["builders"] is SvJsonObject builders)
        {
            restored.Add($"the builders of {RestoreBuilders(p, builders)} of {builders.Count} brushes");
        }

        findings.Add(Finding.Info("import.handoff",
            $"the map's identity, settings and texts{string.Concat(restored.Select(r => ", " + r))} come from {Path.GetFileName(file)}"
            + (exported ? "" : $" (written for an earlier {Path.GetFileName(t3dPath)}: the T3D has changed since the export, and its title and shapes are kept)")));
        return true;
    }

    /// <summary>Drops the model names the export made up for brushes and shapes that had none: the next export makes
    /// the same names again.</summary>
    private static void ForgetModelNames(MapProject p, IEnumerable<string> unnamed)
    {
        var ids = new HashSet<string>(unnamed, StringComparer.OrdinalIgnoreCase);
        foreach (PropSet props in p.Brushes.Where(b => ids.Contains(b.Id)).Select(b => b.Props)
                     .Concat(p.Actors.Where(a => a.Shape is not null && ids.Contains(a.Id)).Select(a => a.Props)))
        {
            props.Remove("Brush");
        }
    }

    /// <summary>Takes the as-imported marks the T3D import gave every brush and shape off those the export did not
    /// list: their faces are the editor's own, so the lint holds them to the stock import's rules again.</summary>
    private static void ForgetImportMarks(MapProject p, IEnumerable<string> keep)
    {
        var ids = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (Brush b in p.Brushes.Where(b => !ids.Contains(b.Id)))
        {
            b.Source = BakedContent.WithoutImportMarks(b.Source);
        }

        foreach (Actor a in p.Actors.Where(a => a.Shape is not null && !ids.Contains(a.Id)))
        {
            a.Source = BakedContent.WithoutImportMarks(a.Source);
        }
    }

    private static void RestoreMatchAndOutfits(MapProject p, SvJsonObject o, Func<string, string?> full, List<string> restored,
        List<Finding> findings)
    {
        var actors = new Dictionary<string, Actor>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors)
        {
            actors.TryAdd(a.Id, a);
        }

        if (o["match"] is SvJsonObject match)
        {
            int count = 0;
            foreach ((string id, SvJsonNode rule) in match)
            {
                if (actors.TryGetValue(id, out Actor? a) && MatchJson.FromJson(rule) is { } m)
                {
                    a.Match = m;
                    count++;
                }
            }

            restored.Add($"the player counts of {count} actors");
        }

        if (o["outfits"] is SvJsonObject outfits)
        {
            try
            {
                p.Outfits = OutfitsJson.FromJson(Paths(outfits, (key, value) => key is "texture" or "glb" ? full(value) : null));
                restored.Add("the outfits");
            }
            catch (SvMapFormatException e)
            {
                findings.Add(Finding.Warning("import.handoff", $"the outfits were not read: {e.Message}"));
            }
        }

        if (o["lifted"] is not SvJsonObject lifted)
        {
            return;
        }

        // An RE+ actor still as exported stands for the data above again (one the export made for the map is dropped:
        // the map never had it). One changed in the original editor stays an RE+ actor, and what it holds now is read
        // back where the T3D can tell it: player counts, as the converter reads them; outfits name textures, not their
        // pixels, so the map keeps the outfits exported.
        var made = new HashSet<string>((o["made"] as SvJsonArray ?? []).OfType<SvJsonString>().Select(m => m.Value), StringComparer.OrdinalIgnoreCase);
        var edited = new List<Actor>();
        int kept = 0;
        foreach ((string id, SvJsonNode hash) in lifted)
        {
            if (!actors.TryGetValue(id, out Actor? a))
            {
                continue;
            }

            bool same = hash is SvJsonString h && h.Value == PropsHash(a.Props);
            if (same && made.Contains(id))
            {
                p.Actors.Remove(a);
                continue;
            }

            a.Source = MarkLifted(a.Source);
            a.Editor.Folder = SdcOverlay.LiftedFolder;
            kept++;
            if (!same)
            {
                edited.Add(a);
            }
        }

        if (kept == 0)
        {
            return;
        }

        string? matchNote = ReadBackMatch(p, edited);
        var hashes = new SvJsonObject();
        hashes.Add("outfits", SvJsonNode.String(BakedContent.OutfitsHash(p)));
        hashes.Add("match", SvJsonNode.String(BakedContent.MatchHash(p)));
        p.Extra.TryAdd(BakedContent.LiftedKey, hashes);
        if (edited.Count > 0)
        {
            bool outfitsEdited = edited.Any(a => BakedContent.LiftedData(a) == "outfits");
            findings.Add(Finding.Warning("import.handoffEdited",
                $"{string.Join(", ", edited.Select(a => a.Id))} changed in the original editor and stay as they are (an export writes them unchanged)"
                + (matchNote is null ? "" : $"; {matchNote}")
                + (outfitsEdited ? "; the outfits RE+ holds now name textures the T3D has no pixels for, so the map's outfits (and the OpenSCCT build) "
                    + "stay the ones exported: set them again in the editor if they should change" : ""),
                edited.Select(a => ObjectRef.Actor(a.Id)).ToArray()));
        }
    }

    /// <summary>
    /// The player counts an RE+ rules actor changed in the original editor holds now, read back per actor: a row RE+
    /// still has as exported keeps the map's own rule (which may say more than RE+ can hold), a changed one is read as
    /// the converter reads it. Returns what changed, or null.
    /// </summary>
    private static string? ReadBackMatch(MapProject p, List<Actor> edited)
    {
        // The converter reads the newest class's actor (svmbuild mapconv/mapclasses.py _own_actors).
        List<string> order = SdcOverlay.LiftedClasses.ToList();
        Actor? rules = edited.Where(a => BakedContent.LiftedData(a) == "match")
            .OrderBy(a => order.FindIndex(c => c.Equals(a.ShortClass, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
        if (rules is null)
        {
            return null;
        }

        (Dictionary<string, ActorMatch> wasRules, Dictionary<string, int[]> wasWins) = LiftedWriteBack.Written(p, rules);
        (Dictionary<string, ActorMatch> nowRules, Dictionary<string, int[]> nowWins) = LiftedWriteBack.ReadMatch(p, rules.Props);
        var changed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors.Where(a => !BakedContent.IsLifted(a)))
        {
            ActorMatch? was = wasRules.GetValueOrDefault(a.Id), now = nowRules.GetValueOrDefault(a.Id);
            int[]? wasWin = wasWins.GetValueOrDefault(a.Id), nowWin = nowWins.GetValueOrDefault(a.Id);
            bool ruleChanged = Json(was) != Json(now);
            bool winChanged = !(wasWin ?? new int[MatchRuleSet.TeamSizes]).SequenceEqual(nowWin ?? new int[MatchRuleSet.TeamSizes]);
            if (!ruleChanged && !winChanged)
            {
                continue;
            }

            ActorMatch kept = a.Match ?? new ActorMatch(PlayerRange.Any);
            if (ruleChanged)
            {
                kept = kept with { Players = now?.Players ?? PlayerRange.Any, Outside = now?.Outside, Inside = now?.Inside };
            }

            if (winChanged)
            {
                // RE+ holds 1v1 to 8v8; the map's targets for larger teams stay.
                int[] win = kept.WinBySize();
                for (int i = 0; i < LiftedWriteBack.TeamSizes; i++)
                {
                    win[i] = nowWin?[i] ?? 0;
                }

                kept = kept with { Win = win };
            }

            a.Match = kept.OrNull();
            changed.Add(a.Id);
        }

        return changed.Count == 0 ? null : $"the player counts of {string.Join(", ", changed)} are read back from {rules.Id}";

        static string Json(ActorMatch? m) => m is null ? "" : CanonicalJson.ToCanonicalString(MatchJson.ToJson(m));
    }

    /// <summary>
    /// Points the references the T3D import read as the map's package (<c>&lt;Map&gt;.Group.Name</c>, from MyLevel) back
    /// at the project's own assets (<c>MyLevel.Group.Name</c>) when the asset library has them; returns how many.
    /// </summary>
    private static int Relink(MapProject p, string library)
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SvJson.Parse(File.ReadAllBytes(Path.Combine(library, ProjectAssets.ManifestFile))) is SvJsonObject manifest)
        {
            foreach (string section in new[] { "materials", "meshes", "textures", "sounds" })
            {
                foreach ((string path, _) in manifest[section] as SvJsonObject ?? new SvJsonObject())
                {
                    own.Add(path);
                }
            }
        }

        int count = 0;
        string? Mine(string path)
        {
            (string package, _, _) = AssetPaths.Split(path);
            string local = AssetPaths.LocalPackage + path[package.Length..];
            if (!package.Equals(p.Id, StringComparison.OrdinalIgnoreCase) || !own.Contains(local))
            {
                return null;
            }

            count++;
            return local;
        }

        SvJsonNode Walk(SvJsonNode node) => node switch
        {
            SvJsonObject o when PropValues.AssetPath(o) is { } path => Mine(path) is { } local ? PropValues.AssetRef(local) : o,
            SvJsonObject o => Rebuild(o, Walk),
            SvJsonArray a => new SvJsonArray(a.Select(Walk)),
            _ => node,
        };

        void Props(PropSet props)
        {
            foreach ((string key, SvJsonNode value) in props.ToList())
            {
                props.Set(key, Walk(value));
            }
        }

        void Faces(BrushShape? shape)
        {
            foreach (BrushFace f in shape?.Faces ?? [])
            {
                if (f.Material is { } m && Mine(m) is { } local)
                {
                    f.Material = local;
                }
            }
        }

        foreach (Actor a in p.Actors)
        {
            Props(a.Props);
            Faces(a.Shape);
        }

        foreach (Brush b in p.Brushes)
        {
            Props(b.Props);
            Faces(b.Shape);
        }

        Props(p.LevelInfo);
        return count;
    }

    private static SvJsonObject Rebuild(SvJsonObject o, Func<SvJsonNode, SvJsonNode> walk)
    {
        var copy = new SvJsonObject();
        foreach ((string key, SvJsonNode value) in o)
        {
            copy.Add(key, walk(value));
        }

        return copy;
    }

    /// <summary>Gives brushes back the builders that made them when their corners are still the ones made; returns how
    /// many.</summary>
    private static int RestoreBuilders(MapProject p, SvJsonObject builders)
    {
        var shapes = new Dictionary<string, BrushShape>(StringComparer.OrdinalIgnoreCase);
        foreach (Brush b in p.Brushes)
        {
            shapes.TryAdd(b.Id, b.Shape);
        }

        foreach (Actor a in p.Actors.Where(a => a.Shape is not null))
        {
            shapes.TryAdd(a.Id, a.Shape!);
        }

        int count = 0;
        foreach ((string id, SvJsonNode node) in builders)
        {
            if (node is not SvJsonObject { } entry || entry["builder"] is not SvJsonObject spec || entry["corners"] is not SvJsonArray corners
                || !shapes.TryGetValue(id, out BrushShape? shape))
            {
                continue;
            }

            // The T3D keeps six decimals: a corner within a tenth of a unit is the one the builder made.
            List<double> now = Corners(shape);
            if (now.Count == corners.Count && now.Zip(corners, (a, b) => Math.Abs(a - ModelJson.Num(b))).All(d => d < 0.1))
            {
                shape.Builder = ModelJson.BuilderFromJson(spec);
                count++;
            }
        }

        return count;
    }

    private static List<double> Corners(BrushShape shape) =>
        shape.Faces.SelectMany(f => f.Vertices).SelectMany(v => new[] { Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 2) }).ToList();

    /// <summary>An actor's source with the mark of an actor whose settings are native data (as an .sdc import marks it).</summary>
    private static string MarkLifted(string? source)
    {
        var src = new SvJsonObject();
        if (source is { Length: > 0 } s && s[0] == '{' && SvJson.Parse(s) is SvJsonObject old)
        {
            foreach ((string key, SvJsonNode value) in old)
            {
                if (key != "lifted")
                {
                    src.Add(key, value);
                }
            }
        }

        src.Add("lifted", SvJsonNode.True);
        return InteropValues.Compact(src);
    }

    /// <summary>A copy of <paramref name="node"/> with the string members <paramref name="map"/> gives a new value for
    /// (by member name and value; null keeps the value).</summary>
    private static SvJsonObject Paths(SvJsonObject node, Func<string, string, string?> map) => (SvJsonObject)Walk(node, map);

    private static SvJsonNode Walk(SvJsonNode node, Func<string, string, string?> map)
    {
        switch (node)
        {
            case SvJsonObject obj:
                var copy = new SvJsonObject();
                foreach ((string key, SvJsonNode value) in obj)
                {
                    copy.Add(key, value is SvJsonString { Value.Length: > 0 } str && map(key, str.Value) is { } changed
                        ? SvJsonNode.String(changed)
                        : Walk(value, map));
                }

                return copy;
            case SvJsonArray array:
                return new SvJsonArray(array.Select(v => Walk(v, map)));
            default:
                return node;
        }
    }

    /// <summary>A hash of the properties a T3D carries (the original editor works out the zone and the rest again).</summary>
    private static string PropsHash(PropSet props)
    {
        var written = new SvJsonObject();
        foreach ((string key, SvJsonNode value) in props.Where(kv => T3dExport.IsWritten(kv.Key)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            written.TryAdd(key, value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.ToCanonicalString(written))).AsSpan(0, 8));
    }
}
