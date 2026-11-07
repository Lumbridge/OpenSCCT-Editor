using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Project;

/// <summary>
/// Reads and writes an <c>.osmap</c> project folder: canonical, sorted JSON split by section so version control diffs
/// stay small and a tool can read one part without the rest (docs/FORMAT.md).
/// <code>
/// map.json             id, title, source, LevelInfo, map settings per mode, map info, map identity, baked world, lighting, extra
/// world/brushes.json   CSG brushes in CSG order
/// world/actors.json    actors in level order
/// world/ges.json       geometric events
/// world/prefabs.json   linked prefab instances (written when the map has any)
/// layers.json          editor layers
/// workspace.json       editor-only state (bookmarks, references, movement limits, play options, grid, camera)
/// outfits.json         the teams' outfits (written when the map has any; files under assets/outfits/)
/// prefabs/             the map's own prefabs (Prefabs.PrefabStore)
/// assets/              map-local meshes, textures and sounds
/// pictures/            the map's picture files (identity picture recipes name them)
/// .cache/              rendered camera pictures (ose mapshot), safe to delete
/// .backups/            the project files from before each of the last few saves, and a store of the assets/ and
///                      prefabs/ files they list (ProjectBackups)
/// .backup-files.json   the assets/ and prefabs/ files at the last save, for the backup the next save makes
/// .saving/, .saving.journal   only while a save runs, or after a crash interrupted one (SaveJournal)
/// .saving.lock         only while a save or a read runs (SaveJournal.Lock)
/// .gitignore           written once, keeps the five entries above out of git
/// </code>
/// </summary>
public static class ProjectStore
{
    public const string Extension = ".osmap";
    public const string MapFile = "map.json";
    public const string BrushesFile = "world/brushes.json";
    public const string StairsFile = "world/stairs.json";
    public const string ActorsFile = "world/actors.json";
    public const string GesFile = "world/ges.json";
    public const string PrefabInstancesFile = "world/prefabs.json";
    public const string PrefabsFolder = "prefabs";
    public const string LayersFile = "layers.json";
    public const string WorkspaceFile = "workspace.json";
    public const string OutfitsFile = "outfits.json";

    /// <summary>The format of <see cref="WorkspaceFile"/>. Files from before it was written stored the old default of
    /// 4 play players, which loading turns into 0 (the bots and you).</summary>
    public const string WorkspaceFormat = MapProject.FormatId + ".workspace/2";

    /// <summary>The play player count every project stored before <see cref="WorkspaceFormat"/> (the old default).</summary>
    private const int OldDefaultPlayers = 4;

    /// <summary>Where outfit textures and models are kept, relative to the project folder.</summary>
    public const string OutfitsFolder = "assets/outfits";

    /// <summary>True when <paramref name="folder"/> holds a project (or a project's first save that a crash
    /// interrupted after it was committed).</summary>
    public static bool IsProject(string folder) => File.Exists(Path.Combine(folder, MapFile)) || SaveJournal.IsPending(folder);

    /// <summary>Writes the project as one unit through <see cref="SaveJournal"/>: every file is made first, so a value
    /// that cannot be saved (a non-finite number) throws <see cref="InvalidOperationException"/> before anything is
    /// touched, and a crash part way never leaves files from two saves mixed. When the folder already holds a project
    /// and <paramref name="keepBackups"/> is above 0, the replaced files become a backup (<see cref="ProjectBackups"/>)
    /// and only the newest <paramref name="keepBackups"/> backups are kept. <paramref name="restoreFrom"/> names the
    /// backup this save puts back: its assets and prefabs replace the project's once the replaced ones are kept (a
    /// restore keeps a backup even when <paramref name="keepBackups"/> is 0, so it can be undone). Returns what went
    /// wrong with the backups without stopping the save (the assets could not be listed, an old backup could not be
    /// deleted).</summary>
    public static IReadOnlyList<string> Save(MapProject p, string folder, int keepBackups = 0, string? restoreFrom = null)
    {
        var files = new List<(string Rel, byte[] Bytes)>();
        try
        {
            files.Add((MapFile, CanonicalJson.ToFileBytes(MapToJson(p))));
            files.Add((BrushesFile, CanonicalJson.ToFileBytes(Wrap("brushes", p.Brushes.Where(b => b.Stair is null).Select(b => ModelJson.BrushToJson(b))))));
            files.Add((StairsFile, CanonicalJson.ToFileBytes(Stairs.StairStore.Write(p))));
            files.Add((ActorsFile, CanonicalJson.ToFileBytes(Wrap("actors", p.Actors.Select(a => ModelJson.ActorToJson(a))))));
            files.Add((GesFile, CanonicalJson.ToFileBytes(Wrap("ges", p.Ges.Select(g => ModelJson.GeToJson(g))))));
            if (p.Prefabs.Count > 0)
            {
                files.Add((PrefabInstancesFile, CanonicalJson.ToFileBytes(Wrap("instances", PrefabsToJson(p.Prefabs)))));
            }

            files.Add((LayersFile, CanonicalJson.ToFileBytes(Wrap("layers", p.Layers.Select(LayerToJson)))));
            SvJsonObject workspace = WorkspaceToJson(p.Workspace);
            workspace.Add("format", SvJsonNode.String(WorkspaceFormat));
            files.Add((WorkspaceFile, CanonicalJson.ToFileBytes(workspace)));
            if (!p.Outfits.IsEmpty)
            {
                files.Add((OutfitsFile, CanonicalJson.ToFileBytes(OutfitsJson.ToJson(p.Outfits))));
            }
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"The project cannot be saved: {ex.Message}", ex);
        }

        var warnings = new List<string>();
        using (SaveJournal.Lock(folder))
        {
            var remove = new List<string>();
            if (p.Prefabs.Count == 0 && File.Exists(Path.Combine(folder, PrefabInstancesFile)))
            {
                remove.Add(PrefabInstancesFile);
            }

            if (p.Outfits.IsEmpty && File.Exists(Path.Combine(folder, OutfitsFile)))
            {
                remove.Add(OutfitsFile);
            }

            // Checked before anything is written, so a backup that cannot be put back whole changes nothing.
            bool restoreFolders = restoreFrom is not null && ProjectBackups.CanRestoreFolders(restoreFrom, folder);
            if (restoreFrom is not null)
            {
                keepBackups = Math.Max(keepBackups, 1);
            }

            // The assets and prefabs this save's files go with, for the backup the next save makes of them.
            byte[]? listed = null;
            if (restoreFolders)
            {
                listed = File.ReadAllBytes(Path.Combine(restoreFrom!, ProjectBackups.FilesList));
            }
            else if (keepBackups > 0)
            {
                try
                {
                    listed = ProjectBackups.ListFiles(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"The assets and prefabs could not be kept for a backup ({ex.Message}); restoring the next backup leaves them as they are.");
                }
            }

            if (listed is not null)
            {
                files.Add((ProjectBackups.FilesList, listed));
            }
            else if (File.Exists(Path.Combine(folder, ProjectBackups.FilesList)))
            {
                // An old list would pair the next backup with the wrong assets.
                remove.Add(ProjectBackups.FilesList);
            }

            string? backup = keepBackups > 0 && File.Exists(Path.Combine(folder, MapFile)) ? ProjectBackups.NewFolder(folder) : null;
            SaveJournal.Commit(folder, files, remove, backup);

            // Before the pruning, which may drop the backup being restored.
            if (restoreFolders)
            {
                ProjectBackups.RestoreFolders(restoreFrom!, folder);
            }

            if (backup is not null)
            {
                try
                {
                    ProjectBackups.Prune(folder, keepBackups);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Old backups could not all be deleted: {ex.Message}");
                }
            }

            WriteGitIgnore(folder);
        }

        return warnings;
    }

    /// <summary>Reads a project. Waits for a save another program is running, and finishes (or throws away) a save a
    /// crash interrupted, so the files read all come from one save.</summary>
    public static MapProject Load(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Not a map project (no folder {folder}).");
        }

        using (SaveJournal.Lock(folder))
        {
            SaveJournal.Recover(folder);
            return LoadHeld(folder);
        }
    }

    /// <summary>Keeps the editor's own working files (backups, a running save) out of version control. Written once;
    /// a .gitignore the user already has is left alone.</summary>
    private static void WriteGitIgnore(string folder)
    {
        string path = Path.Combine(folder, ".gitignore");
        if (!File.Exists(path))
        {
            string[] lines = [$"{ProjectBackups.FolderName}/", ProjectBackups.FilesList, $"{SaveJournal.StagingFolder}/", $"{SaveJournal.JournalFile}*", SaveJournal.LockFile];
            File.WriteAllText(path, string.Join('\n', lines) + "\n");
        }
    }

    private static MapProject LoadHeld(string folder)
    {
        string mapPath = Path.Combine(folder, MapFile);
        if (!File.Exists(mapPath))
        {
            throw new FileNotFoundException("Not a map project (no map.json).", mapPath);
        }

        SvJsonObject map = Read(folder, MapFile) ?? new SvJsonObject();
        if (ModelJson.Str(map, "format") is { } fmt && fmt != MapProject.FormatId && fmt != "osmap/2")
        {
            throw new InvalidDataException($"{mapPath}: format is '{fmt}', expected '{MapProject.FormatId}'.");
        }

        if ((int)ModelJson.Num(map["version"]) > 2) throw new InvalidDataException("This project needs a newer editor");
        var p = new MapProject
        {
            Id = ModelJson.Str(map, "id") ?? Path.GetFileNameWithoutExtension(folder),
            Title = ModelJson.Str(map, "title") ?? "",
            LevelInfo = PropSet.FromJson(map["levelInfo"]),
            MapInfo = PropSet.FromJson(map["mapInfo"]),
            Identity = IdentityJson.FromJson(map["identity"]),
            Extra = map["extra"] as SvJsonObject ?? new SvJsonObject(),
        };
        if (map["source"] is SvJsonObject src)
        {
            p.Source = new ProjectSource
            {
                Kind = ModelJson.Str(src, "kind") ?? "authored",
                File = ModelJson.Str(src, "file"),
                Sha256 = ModelJson.Str(src, "sha256"),
                Findings = src["findings"] as SvJsonArray,
            };
        }

        if (map["mapSettings"] is SvJsonObject settings)
        {
            foreach (KeyValuePair<string, SvJsonNode> kv in settings)
            {
                p.MapSettings[kv.Key] = PropSet.FromJson(kv.Value);
            }
        }

        if (map["baked"] is SvJsonObject baked)
        {
            p.Baked = new BakedWorld
            {
                SourceMap = ModelJson.Str(baked, "sourceMap") ?? "",
                KeepRender = baked["keepRender"]?.Kind != SvJsonKind.False,
                KeepCollision = baked["keepCollision"]?.Kind != SvJsonKind.False,
                Brushes = baked["brushes"] is SvJsonArray ids ? ids.OfType<SvJsonString>().Select(id => id.Value).ToList() : [],
            };
        }

        if (map["lighting"] is SvJsonObject lighting)
        {
            try
            {
                p.Lighting = LightingFromJson(lighting);
            }
            catch (FormatException e)
            {
                throw new InvalidDataException($"{mapPath}: {e.Message}.", e);
            }

            if (p.Lighting.Problem() is { } problem)
            {
                throw new InvalidDataException($"{mapPath}: lighting {problem}.");
            }
        }

        p.Brushes.AddRange(Items(Read(folder, BrushesFile), "brushes").Select(ModelJson.BrushFromJson));
        Stairs.StairStore.Read(p, Read(folder, StairsFile));
        p.Actors.AddRange(Items(Read(folder, ActorsFile), "actors").Select(ModelJson.ActorFromJson));
        p.Ges.AddRange(Items(Read(folder, GesFile), "ges").Select(ModelJson.GeFromJson));
        p.Layers.AddRange(Items(Read(folder, LayersFile), "layers").Select(LayerFromJson));
        p.Prefabs.AddRange(Items(Read(folder, PrefabInstancesFile), "instances").Select(PrefabInstanceFromJson));
        if (Read(folder, WorkspaceFile) is { } ws)
        {
            p.Workspace = WorkspaceFromJson(ws);
            if (ModelJson.Str(ws, "format") is null && p.Workspace.Play.Players == OldDefaultPlayers)
            {
                // Saved when 4 was the default, so the 4 was almost never chosen; 0 lays playtests out for who plays.
                p.Workspace.Play.Players = 0;
                p.LoadNotes.Add($"Play options: the old default of {OldDefaultPlayers} players became 0 (the bots and you); " +
                    "set Players in Map Settings to lay playtests out for a fixed count.");
            }
        }

        if (Read(folder, OutfitsFile) is { } outfits)
        {
            p.Outfits = OutfitsJson.FromJson(outfits);
        }

        return p;
    }

    public static SvJsonObject MapToJson(MapProject p)
    {
        var o = new SvJsonObject();
        int version = p.Brushes.Any(b => b.Stair is not null) ? 2 : MapProject.FormatVersion;
        o.Add("format", SvJsonNode.String(version > 1 ? $"osmap/{version}" : MapProject.FormatId));
        o.Add("version", SvJsonNode.Integer(version));
        o.Add("id", SvJsonNode.String(p.Id));
        o.Add("title", SvJsonNode.String(p.Title));
        var src = new SvJsonObject();
        src.Add("kind", SvJsonNode.String(p.Source.Kind));
        if (p.Source.File is not null)
        {
            src.Add("file", SvJsonNode.String(p.Source.File));
        }

        if (p.Source.Sha256 is not null)
        {
            src.Add("sha256", SvJsonNode.String(p.Source.Sha256));
        }

        if (p.Source.Findings is not null)
        {
            src.Add("findings", p.Source.Findings);
        }

        o.Add("source", src);
        o.Add("levelInfo", p.LevelInfo.ToJson());
        var settings = new SvJsonObject();
        foreach (KeyValuePair<string, PropSet> kv in p.MapSettings)
        {
            settings.Add(kv.Key, kv.Value.ToJson());
        }

        o.Add("mapSettings", settings);
        o.Add("mapInfo", p.MapInfo.ToJson());
        if (IdentityJson.ToJson(p.Identity) is { Count: > 0 } identity)
        {
            o.Add("identity", identity);
        }

        if (p.Baked is not null)
        {
            var b = new SvJsonObject();
            b.Add("sourceMap", SvJsonNode.String(p.Baked.SourceMap));
            b.Add("keepRender", SvJsonNode.Bool(p.Baked.KeepRender));
            b.Add("keepCollision", SvJsonNode.Bool(p.Baked.KeepCollision));
            if (p.Baked.Brushes.Count > 0)
            {
                b.Add("brushes", new SvJsonArray(p.Baked.Brushes.Select(id => (SvJsonNode)SvJsonNode.String(id))));
            }
            o.Add("baked", b);
        }

        if (!p.Lighting.IsDefault)
        {
            o.Add("lighting", LightingToJson(p.Lighting));
        }

        if (p.Extra.Count > 0)
        {
            o.Add("extra", p.Extra);
        }

        return o;
    }

    /// <summary>The lightmap settings as map.json stores them (also the API op layer's <c>lighting</c> object).</summary>
    public static SvJsonObject LightingToJson(LightingSettings l)
    {
        var o = new SvJsonObject();
        o.Add("mode", SvJsonNode.String(l.Mode == LightmapMode.Faithful ? "faithful" : "quality"));
        o.Add("density", SvJsonNode.Float((float)l.Density));
        o.Add("samples", SvJsonNode.Integer(l.Samples));
        o.Add("lightSize", SvJsonNode.Float((float)l.LightSize));
        o.Add("sunSize", SvJsonNode.Float((float)l.SunSize));
        o.Add("bounce", SvJsonNode.Float((float)l.Bounce));
        o.Add("bounceSamples", SvJsonNode.Integer(l.BounceSamples));
        o.Add("atlasSize", SvJsonNode.Integer(l.AtlasSize));
        o.Add("relightImported", SvJsonNode.Bool(l.RelightImported));
        if (l.BakeDefaultLightRadius is { } radius)
        {
            o.Add("bakeDefaultLightRadius", SvJsonNode.Float((float)radius));
        }

        return o;
    }

    /// <summary>Settings from map.json (or an op's partial object over <paramref name="over"/>); missing members keep
    /// their value. Throws <see cref="FormatException"/> on a bad mode name.</summary>
    public static LightingSettings LightingFromJson(SvJsonObject o, LightingSettings? over = null)
    {
        LightingSettings l = over?.Clone() ?? new LightingSettings();
        if (ModelJson.Str(o, "mode") is { } mode)
        {
            l.Mode = mode.ToLowerInvariant() switch
            {
                "quality" => LightmapMode.Quality,
                "faithful" => LightmapMode.Faithful,
                _ => throw new FormatException($"lighting mode '{mode}' is quality or faithful"),
            };
        }

        l.Density = o["density"] is null ? l.Density : ModelJson.Num(o["density"]);
        l.Samples = o["samples"] is null ? l.Samples : (int)ModelJson.Num(o["samples"]);
        l.LightSize = o["lightSize"] is null ? l.LightSize : ModelJson.Num(o["lightSize"]);
        l.SunSize = o["sunSize"] is null ? l.SunSize : ModelJson.Num(o["sunSize"]);
        l.Bounce = o["bounce"] is null ? l.Bounce : ModelJson.Num(o["bounce"]);
        l.BounceSamples = o["bounceSamples"] is null ? l.BounceSamples : (int)ModelJson.Num(o["bounceSamples"]);
        l.AtlasSize = o["atlasSize"] is null ? l.AtlasSize : (int)ModelJson.Num(o["atlasSize"]);
        l.RelightImported = o["relightImported"] is null ? l.RelightImported : o["relightImported"]!.Kind == SvJsonKind.True;
        l.BakeDefaultLightRadius = o["bakeDefaultLightRadius"] switch
        {
            null => l.BakeDefaultLightRadius,
            { Kind: SvJsonKind.Null } => null,
            var v => ModelJson.Num(v),
        };
        return l;
    }

    public static SvJsonObject LayerToJson(Layer l)
    {
        var o = new SvJsonObject();
        o.Add("name", SvJsonNode.String(l.Name));
        if (!l.Visible)
        {
            o.Add("visible", SvJsonNode.False);
        }

        if (l.Locked)
        {
            o.Add("locked", SvJsonNode.True);
        }

        if (l.Color is not null)
        {
            o.Add("color", SvJsonNode.String(l.Color));
        }

        return o;
    }

    public static Layer LayerFromJson(SvJsonObject o) => new(ModelJson.Str(o, "name") ?? "Layer")
    {
        Visible = o["visible"]?.Kind != SvJsonKind.False,
        Locked = o["locked"]?.Kind == SvJsonKind.True,
        Color = ModelJson.Str(o, "color"),
    };

    public static SvJsonArray PrefabsToJson(IEnumerable<PrefabInstance> instances) => new(instances.Select(i =>
    {
        var o = new SvJsonObject();
        o.Add("id", SvJsonNode.String(i.Id));
        o.Add("prefab", SvJsonNode.String(i.Prefab));
        o.Add("loc", ModelJson.Vec(i.Location));
        if (i.Rotation != Rot.Zero)
        {
            o.Add("rot", ModelJson.RotJ(i.Rotation));
        }

        if (i.Params.Count > 0)
        {
            var po = new SvJsonObject();
            foreach (KeyValuePair<string, SvJsonNode> kv in i.Params)
            {
                po.Add(kv.Key, kv.Value);
            }

            o.Add("params", po);
        }

        if (i.Hash is not null)
        {
            o.Add("hash", SvJsonNode.String(i.Hash));
        }

        if (i.AnchorPart is not null)
        {
            o.Add("anchor", new SvJsonArray([SvJsonNode.String(i.AnchorPart), ModelJson.Vec(i.AnchorLocal), ModelJson.RotJ(i.AnchorRotation)]));
        }

        return (SvJsonNode)o;
    }));

    public static PrefabInstance PrefabInstanceFromJson(SvJsonObject o)
    {
        var i = new PrefabInstance(ModelJson.Str(o, "id") ?? "Prefab", ModelJson.Str(o, "prefab") ?? "")
        {
            Location = ModelJson.ReadVec(o["loc"]),
            Rotation = ModelJson.ReadRot(o["rot"]),
            Hash = ModelJson.Str(o, "hash"),
        };
        if (o["params"] is SvJsonObject po)
        {
            foreach (KeyValuePair<string, SvJsonNode> kv in po)
            {
                i.Params[kv.Key] = kv.Value;
            }
        }

        if (o["anchor"] is SvJsonArray { Count: 3 } a && a[0] is SvJsonString part)
        {
            i.AnchorPart = part.Value;
            i.AnchorLocal = ModelJson.ReadVec(a[1]);
            i.AnchorRotation = ModelJson.ReadRot(a[2]);
        }

        return i;
    }

    public static SvJsonObject WorkspaceToJson(Workspace w)
    {
        var o = new SvJsonObject();
        o.Add("grid", SvJsonNode.Float((float)w.GridSize));
        var guide = new SvJsonObject();
        guide.Add("hidden", SvJsonNode.Bool(w.FirstMap.Hidden));
        guide.Add("explored", SvJsonNode.Bool(w.FirstMap.Explored));
        if (w.FirstMap.CheckedRevision is { } check) guide.Add("checkedRevision", SvJsonNode.String(check));
        if (w.FirstMap.PlayedRevision is { } play) guide.Add("playedRevision", SvJsonNode.String(play));
        if (w.FirstMap.ConstructionBaseline is { } baseline) guide.Add("constructionBaseline", new SvJsonArray(baseline.Select(SvJsonNode.String)));
        o.Add("firstMap", guide);
        o.Add("rotationGrid", SvJsonNode.Integer(w.RotationGrid));
        o.Add("camera", new SvJsonArray([ModelJson.Vec(w.CameraLocation), ModelJson.RotJ(w.CameraRotation)]));
        o.Add("shownStorey", w.ShownStorey is double shown ? SvJsonNode.Float((float)shown) : SvJsonNode.Null);
        o.Add("bookmarks", new SvJsonArray(w.Bookmarks.Select(b =>
        {
            var bo = new SvJsonObject();
            bo.Add("name", SvJsonNode.String(b.Name));
            bo.Add("loc", ModelJson.Vec(b.Location));
            bo.Add("rot", ModelJson.RotJ(b.Rotation));
            if (b.Note is not null)
            {
                bo.Add("note", SvJsonNode.String(b.Note));
            }

            return (SvJsonNode)bo;
        })));
        o.Add("references", new SvJsonArray(w.References.Select(r =>
        {
            var ro = new SvJsonObject();
            ro.Add("name", SvJsonNode.String(r.Name));
            ro.Add("path", SvJsonNode.String(r.Path));
            ro.Add("view", SvJsonNode.String(r.View));
            ro.Add("origin", ModelJson.Vec(r.Origin));
            ro.Add("unitsPerPixel", SvJsonNode.Float((float)r.UnitsPerPixel));
            ro.Add("opacity", SvJsonNode.Float((float)r.Opacity));
            return (SvJsonNode)ro;
        })));
        o.Add("storeys", new SvJsonArray(w.Storeys.Select(s =>
        {
            var so = new SvJsonObject();
            so.Add("name", SvJsonNode.String(s.Name));
            so.Add("bottom", SvJsonNode.Float((float)s.Bottom));
            so.Add("top", SvJsonNode.Float((float)s.Top));
            return (SvJsonNode)so;
        })));
        MovementLimits m = w.Movement;
        var mo = new SvJsonObject();
        mo.Add("stepHeight", SvJsonNode.Float((float)m.StepHeight));
        mo.Add("walkableSlope", SvJsonNode.Float((float)m.WalkableSlopeDegrees));
        mo.Add("standHeight", SvJsonNode.Float((float)m.StandHeight));
        mo.Add("spyCrouchHeight", SvJsonNode.Float((float)m.SpyCrouchHeight));
        mo.Add("mercCrouchHeight", SvJsonNode.Float((float)m.MercCrouchHeight));
        mo.Add("radius", SvJsonNode.Float((float)m.Radius));
        mo.Add("spyRunSpeed", SvJsonNode.Float((float)m.SpyRunSpeed));
        mo.Add("mercRunSpeed", SvJsonNode.Float((float)m.MercRunSpeed));
        o.Add("movement", mo);
        var po = new SvJsonObject();
        po.Add("side", SvJsonNode.String(w.Play.Side));
        po.Add("mode", SvJsonNode.String(w.Play.Mode));
        po.Add("bots", SvJsonNode.Integer(w.Play.Bots));
        po.Add("rules", SvJsonNode.String(w.Play.Rules));
        po.Add("players", SvJsonNode.Integer(w.Play.Players));
        po.Add("difficulty", SvJsonNode.String(w.Play.Difficulty));
        o.Add("play", po);
        return o;
    }

    public static Workspace WorkspaceFromJson(SvJsonObject o)
    {
        var w = new Workspace();
        if (o["firstMap"] is SvJsonObject guide)
        {
            w.FirstMap.Hidden = guide["hidden"]?.Kind == SvJsonKind.True;
            w.FirstMap.Explored = guide["explored"]?.Kind == SvJsonKind.True;
            w.FirstMap.CheckedRevision = ModelJson.Str(guide, "checkedRevision");
            w.FirstMap.PlayedRevision = ModelJson.Str(guide, "playedRevision");
            if (guide["constructionBaseline"] is SvJsonArray baseline) w.FirstMap.ConstructionBaseline = baseline.OfType<SvJsonString>().Select(s => s.Value).ToArray();
        }
        if (o["grid"] is SvJsonNumber)
        {
            w.GridSize = ModelJson.Num(o["grid"]);
        }

        if (o["rotationGrid"] is SvJsonNumber)
        {
            w.RotationGrid = (int)ModelJson.Num(o["rotationGrid"]);
        }

        if (o["camera"] is SvJsonArray { Count: 2 } cam)
        {
            w.CameraLocation = ModelJson.ReadVec(cam[0]);
            w.CameraRotation = ModelJson.ReadRot(cam[1]);
        }

        if (o["shownStorey"] is SvJsonNumber)
        {
            w.ShownStorey = ModelJson.Num(o["shownStorey"]);
        }

        foreach (SvJsonObject b in Items(o, "bookmarks"))
        {
            w.Bookmarks.Add(new Bookmark { Name = ModelJson.Str(b, "name") ?? "", Location = ModelJson.ReadVec(b["loc"]), Rotation = ModelJson.ReadRot(b["rot"]), Note = ModelJson.Str(b, "note") });
        }

        foreach (SvJsonObject r in Items(o, "references"))
        {
            string path = ModelJson.Str(r, "path") ?? "";
            string? name = ModelJson.Str(r, "name");
            w.References.Add(new ReferenceImage
            {
                // Maps saved before images had names get one from the file.
                Name = string.IsNullOrWhiteSpace(name) || w.References.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ? ReferenceImage.NewName(w.References, path)
                    : name,
                Path = path,
                View = ModelJson.Str(r, "view") ?? "top",
                Origin = ModelJson.ReadVec(r["origin"]),
                UnitsPerPixel = r["unitsPerPixel"] is null ? 1 : ModelJson.Num(r["unitsPerPixel"]),
                Opacity = r["opacity"] is null ? 0.5 : ModelJson.Num(r["opacity"]),
            });
        }

        foreach (SvJsonObject s in Items(o, "storeys"))
        {
            w.Storeys.Add(new StoreyRange { Name = ModelJson.Str(s, "name") ?? "", Bottom = Or(s["bottom"], 0), Top = Or(s["top"], 0) });
        }

        if (o["movement"] is SvJsonObject m)
        {
            MovementLimits d = w.Movement;
            d.StepHeight = Or(m["stepHeight"], d.StepHeight);
            d.WalkableSlopeDegrees = Or(m["walkableSlope"], d.WalkableSlopeDegrees);
            d.StandHeight = Or(m["standHeight"], d.StandHeight);
            d.SpyCrouchHeight = Or(m["spyCrouchHeight"], d.SpyCrouchHeight);
            d.MercCrouchHeight = Or(m["mercCrouchHeight"], d.MercCrouchHeight);
            d.Radius = Or(m["radius"], d.Radius);
            d.SpyRunSpeed = Or(m["spyRunSpeed"], d.SpyRunSpeed);
            d.MercRunSpeed = Or(m["mercRunSpeed"], d.MercRunSpeed);
        }

        if (o["play"] is SvJsonObject p)
        {
            w.Play.Side = ModelJson.Str(p, "side") ?? w.Play.Side;
            w.Play.Mode = ModelJson.Str(p, "mode") ?? w.Play.Mode;
            w.Play.Bots = (int)Or(p["bots"], w.Play.Bots);
            w.Play.Rules = ModelJson.Str(p, "rules") ?? w.Play.Rules;
            w.Play.Players = (int)Or(p["players"], w.Play.Players);
            w.Play.Difficulty = ModelJson.Str(p, "difficulty") ?? w.Play.Difficulty;
        }

        return w;
    }

    private static double Or(SvJsonNode? node, double fallback) => node is SvJsonNumber ? ModelJson.Num(node) : fallback;

    private static SvJsonObject Wrap(string key, IEnumerable<SvJsonNode> items)
    {
        var o = new SvJsonObject();
        o.Add("format", SvJsonNode.String(MapProject.FormatId + "." + key + "/1"));
        o.Add(key, new SvJsonArray(items));
        return o;
    }

    private static IEnumerable<SvJsonObject> Items(SvJsonObject? doc, string key) =>
        doc?[key] is SvJsonArray a ? a.OfType<SvJsonObject>() : Enumerable.Empty<SvJsonObject>();

    private static SvJsonObject? Read(string folder, string rel)
    {
        string path = Path.Combine(folder, rel);
        return File.Exists(path) ? SvJson.Parse(File.ReadAllBytes(path), rel) as SvJsonObject : null;
    }
}
