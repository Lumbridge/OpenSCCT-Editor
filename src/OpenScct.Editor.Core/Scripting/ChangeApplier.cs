using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>The outcome of a change document: the compact result JSON, and whether it applied.</summary>
public sealed record ChangeResult(bool Ok, SvJsonObject Json)
{
    /// <summary>What the change did that the caller may not expect (each "op N field: message").</summary>
    public IEnumerable<string> Warnings => Json["warnings"] is SvJsonArray a ? a.OfType<SvJsonString>().Select(w => w.Value) : [];
}

/// <summary>
/// Applies <c>osmap.changes/1</c> documents: <c>{"format":"osmap.changes/1","ops":[...],"dryRun":false,
/// "author":"name","label":"rooms"}</c>. Ops run in order against the live map (so later ops see earlier results and
/// <c>$symbols</c>), then everything is rolled back and, unless it is a dry run, re-applied as ONE undoable step on the
/// session's command stack. Any error rolls the whole document back and names the op index and field.
/// </summary>
public static partial class ChangeApplier
{
    public const string Format = "osmap.changes/1";

    private delegate void OpHandler(ChangeContext ctx, SvJsonObject op);

    private static readonly string[] MetaKeys = ["layer", "folder", "note", "hidden", "locked"];

    private static readonly string[] TransformKeys = ["rot", "rotDeg", "face", "keepRot", "scale", "scale3d", "prePivot", "postScale"];

    private static IEnumerable<string> CreateKeys =>
        new[] { "id", "class", "props", "set", "brush", "shape", "csg", "flags", "mat", "mats", "ge", "kind", "points", "mask", "platform", "count", "step" }
            .Concat(Placement.Keys).Concat(TransformKeys).Concat(MetaKeys);

    private static IEnumerable<string> UpdateKeys =>
        new[] { "id", "ids", "where", "set", "props", "unset", "rename", "shape", "brush", "csg", "flags", "mat", "mats", "points", "mask", "platform", "kind", "keepTextures" }
            .Concat(Placement.Keys).Concat(TransformKeys).Concat(MetaKeys);

    private static readonly Dictionary<string, (OpHandler Run, string[] Keys)> s_ops = new(StringComparer.OrdinalIgnoreCase)
    {
        ["create"] = (Create, [.. CreateKeys]),
        ["update"] = (Update, [.. UpdateKeys]),
        ["delete"] = (Delete, ["id", "ids", "where"]),
        ["move"] = (Move, ["id", "ids", "where", "by", "to", "keepRot", "stick", .. Placement.Keys]),
        ["rotate"] = (Rotate, ["id", "ids", "where", "by", "byDeg", "to", "toDeg", "face", "around"]),
        ["scale"] = (Scale, ["id", "ids", "where", "by", "to", "keepTextures"]),
        ["duplicate"] = (Duplicate, ["id", "ids", "where", "by", "count", "as"]),
        ["brush"] = (BrushOp, [.. CreateKeys.Concat(UpdateKeys).Distinct()]),
        ["surface"] = (Surface, SurfaceKeys),
        ["ge"] = (GeOp, ["id", "kind", "ge", "points", "mask", "platform", .. MetaKeys]),
        ["conveyor"] = (ConveyorOp, ConveyorKeys),
        ["link"] = (Link, ["from", "to", "tag", "prop", "clear"]),
        ["layer"] = (LayerOp, ["name", "color", "visible", "locked", "rename", "delete", "add", "where"]),
        ["folder"] = (FolderOp, ["path", "id", "ids", "where", "rename"]),
        ["settings"] = (Settings, ["levelInfo", "mapSettings", "mapInfo", "title", "mapId", "lighting", "baked"]),
        ["identity"] = (IdentityOp, IdentityKeys),
        ["select"] = (Select, ["id", "ids", "where", "mode"]),
        ["workspace"] = (WorkspaceOp, ["grid", "rotationGrid", "camera", "play", "movement", "storeys", "shown"]),
        ["storey.duplicate"] = (DuplicateStorey, StoreyDuplicateKeys),
        ["bookmark"] = (BookmarkOp, ["name", "at", "rot", "rotDeg", "look", "note", "rename", "delete"]),
        ["reference"] = (ReferenceOp, ["name", "path", "view", "origin", "unitsPerPixel", "opacity", "rename", "calibrate", "delete"]),
        ["prefab.place"] = (PrefabPlace, PrefabPlaceKeys),
        ["prefab.save"] = (PrefabSave, PrefabSaveKeys),
        ["prefab.update"] = (PrefabUpdate, PrefabUpdateKeys),
        ["prefab.break"] = (PrefabBreak, PrefabBreakKeys),
        ["mover"] = (MoverOp, MoverKeys),
        ["magic"] = (MagicOp, MagicKeys),
        ["camnet"] = (CamNetOp, ["order", "loop", "remove"]),
        ["camera"] = (CameraOp, ["id", "name", "pitch", "yaw"]),
        ["props.paste"] = (PastePropsOp, ["from", "props", "keys", "id", "ids", "where"]),
        ["propset"] = (PropSetOp, ["set", "id", "ids", "where"]),
        ["tip"] = (TipOp, TipKeys),
        ["zone.action"] = (ZoneActionOp, ["mission", "add", "type", "delay", "message", "set", "remove"]),
        ["match"] = (MatchOp, MatchKeys),
        ["outfits"] = (OutfitsOp, OutfitKeys),
        ["lights.recover"] = (LightRecoverOp, LightRecoverKeys),
    };

    /// <summary>The op names, for help and schemas.</summary>
    public static IEnumerable<string> OpNames => s_ops.Keys;

    /// <summary>Applies a change document to a session.</summary>
    /// <param name="dryRun">Overrides the document's dryRun when not null.</param>
    /// <param name="author">Overrides the document's author when not null.</param>
    /// <param name="inspect">Sees the map with every op applied, just before the document is rolled back (dry-run
    /// previews copy the touched objects here).</param>
    /// <param name="writeFiles">False when the caller will not keep the map (a one-shot apply without save): prefab
    /// files are then listed but not written.</param>
    public static ChangeResult Apply(EditorSession session, SvJsonNode document, bool? dryRun = null, string? author = null,
        Action<MapProject, IReadOnlyList<Live.ChangedObject>>? inspect = null, bool writeFiles = true)
    {
        SvJsonArray ops;
        bool dry;
        string label;
        try
        {
            (ops, dry, author, label) = ReadHeader(document, dryRun, author);
        }
        catch (ApiException ex)
        {
            return Failed(ex);
        }

        var ctx = new ChangeContext(session.Project, session.Folder, author, label) { Selected = session.Selection.Items.ToList() };
        for (int i = 0; i < ops.Count; i++)
        {
            ctx.OpIndex = i;
            try
            {
                RunOp(ctx, ops[i]);
            }
            catch (Exception ex)
            {
                // Any failure, expected or not, must leave the map as it was: nothing of the document is kept.
                ctx.Rollback();
                ApiException err = ex as ApiException ?? ApiException.Internal(ex);
                err.Op = i;
                return Failed(err);
            }
        }

        SvJsonObject result;
        try
        {
            result = ChangeDiff.Build(ctx);
            inspect?.Invoke(session.Project, ctx.Changes()
                .Select(c => new Live.ChangedObject(c.Id, ApiJson.GetString(c.Before, "id") ?? c.Id, c.Kind, c.Before is not null, c.After is not null))
                .ToList());
        }
        finally
        {
            ctx.Rollback();
        }

        int count = ops.Count;
        string stepName = label == DefaultLabel ? $"Apply changes ({count} op{(count == 1 ? "" : "s")})" : label;
        if (!dry)
        {
            if (ctx.Commands.Count > 0)
            {
                session.Execute(new CompositeCommand(stepName, ctx.Commands));
            }

            ApplySelection(session, ctx);
        }

        var files = new List<string>();
        foreach ((Prefabs.PrefabDefinition prefab, string folder) in ctx.PendingPrefabs.Values)
        {
            if ((dry || !writeFiles ? Path.Combine(folder, prefab.Name + ".json") : WritePrefab(ctx, prefab, folder)) is { } path)
            {
                files.Add(path);
            }
        }

        if (!dry && !writeFiles && files.Count > 0)
        {
            ctx.Warnings.Add("prefab files were not written (the changes are not saved)");
        }

        files.AddRange(CopyFiles(ctx, write: !dry && writeFiles));

        result.Add("ok", SvJsonNode.True);
        if (dry)
        {
            result.Add("dryRun", SvJsonNode.True);
        }
        else if (ctx.Commands.Count > 0)
        {
            result.Add("undo", ApiJson.Str(stepName));
        }

        if (ctx.Symbols.Count > 0)
        {
            var ids = new SvJsonObject();
            foreach (KeyValuePair<string, string> kv in ctx.Symbols)
            {
                ids.Add(kv.Key, ApiJson.Str(kv.Value));
            }

            result.Add("ids", ids);
        }

        if (files.Count > 0)
        {
            // On a dry run (or without writeFiles) these are the files the document would write.
            result.Add("files", ApiJson.Strings(files));
        }

        if (ctx.Warnings.Count > 0)
        {
            result.Add("warnings", ApiJson.Strings(ctx.Warnings));
        }

        return new ChangeResult(true, result);
    }

    /// <summary>Copies the files the document brought into the map (outfits) after it committed; a failed copy becomes
    /// a warning. Returns the project paths.</summary>
    private static IEnumerable<string> CopyFiles(ChangeContext ctx, bool write)
    {
        if (ctx.PendingCopies.Count == 0 || ctx.Folder is null)
        {
            return [];
        }

        if (write)
        {
            try
            {
                Gameplay.ProjectOutfits.Copy(ctx.Folder, ctx.PendingCopies);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.Warnings.Add($"files were not copied into {ctx.Folder}: {ex.Message}");
            }
        }

        return ctx.PendingCopies.Select(c => Path.Combine(ctx.Folder, c.To.Replace('/', Path.DirectorySeparatorChar))).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Writes a prefab saved by the document after it committed. A failed write cannot undo the commit, so
    /// it becomes a warning (and null).</summary>
    private static string? WritePrefab(ChangeContext ctx, Prefabs.PrefabDefinition prefab, string folder)
    {
        try
        {
            return Prefabs.PrefabStore.Write(prefab, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Warnings.Add($"prefab {prefab.Name} was not written to {folder}: {ex.Message}");
            return null;
        }
    }

    private const string DefaultLabel = "changes";

    private static (SvJsonArray Ops, bool DryRun, string Author, string Label) ReadHeader(SvJsonNode document, bool? dryRun, string? author)
    {
        if (document is SvJsonArray bare)
        {
            return (bare, dryRun ?? false, author ?? "script", DefaultLabel);
        }

        if (document is not SvJsonObject doc)
        {
            throw new ApiException("a change document is {\"format\":\"osmap.changes/1\",\"ops\":[...]}", "format");
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in doc)
        {
            if (kv.Key is not ("format" or "ops" or "dryRun" or "author" or "label" or "comment"))
            {
                throw new ApiException($"unknown key '{kv.Key}' (format, ops, dryRun, author, label)", kv.Key);
            }
        }

        if (ApiJson.GetString(doc, "format") is { } format && format != Format)
        {
            throw new ApiException($"unsupported format '{format}' (this editor reads {Format})", "format");
        }

        if (doc["ops"] is not SvJsonArray ops)
        {
            throw new ApiException("missing ops (a list of operations)", "ops");
        }

        return (ops, dryRun ?? ApiJson.RequireBool(doc, "dryRun") ?? false, author ?? ApiJson.GetString(doc, "author") ?? "script",
            ApiJson.GetString(doc, "label") ?? DefaultLabel);
    }

    private static void RunOp(ChangeContext ctx, SvJsonNode node)
    {
        if (node is not SvJsonObject op)
        {
            throw new ApiException("an op is an object such as {\"op\":\"create\",...}", "op");
        }

        string name = ApiJson.GetString(op, "op") ?? throw new ApiException("missing \"op\"", "op");
        if (!s_ops.TryGetValue(name, out (OpHandler Run, string[] Keys) entry))
        {
            throw new ApiException($"unknown op '{name}' (ops: {string.Join(", ", s_ops.Keys)})", "op");
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in op)
        {
            if (kv.Key is not ("op" or "comment") && Array.IndexOf(entry.Keys, kv.Key) < 0)
            {
                throw new ApiException($"'{name}' has no field '{kv.Key}' (fields: {string.Join(", ", entry.Keys)})", kv.Key);
            }
        }

        if (name is not "layer" && ApiJson.GetString(op, "layer") is { } layer)
        {
            EnsureLayer(ctx, layer);
        }

        entry.Run(ctx, op);
    }

    /// <summary>Adds a layer that objects are being put on, so the layer list always names every layer in use.</summary>
    private static void EnsureLayer(ChangeContext ctx, string name)
    {
        if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) || name.Length == 0
            || ctx.Project.Layers.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ctx.SnapshotProject();
        ctx.Run(new LayerListCommand($"Add layer {name}", list => list.Add(new Model.Layer(name))), touchesBrushes: false);
    }

    private static ChangeResult Failed(ApiException ex) =>
        new(false, ApiJson.Obj(("ok", SvJsonNode.False), ("error", ex.ToJson())));

    private static void ApplySelection(EditorSession session, ChangeContext ctx)
    {
        foreach ((string mode, List<ObjectRef> refs) in ctx.SelectRequests)
        {
            switch (mode)
            {
                case "add":
                    session.Selection.Add(refs);
                    break;
                case "remove":
                    session.Selection.Remove(refs);
                    break;
                case "clear":
                    session.Selection.Clear();
                    break;
                default:
                    session.Selection.Set(refs);
                    break;
            }
        }
    }
}
