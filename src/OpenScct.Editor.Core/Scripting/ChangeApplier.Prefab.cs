using System.Text;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// Prefab ops. <c>prefab.place</c> expands a prefab with its parameters and puts the parts in the map as one linked
/// instance; <c>prefab.update</c> re-applies the prefab to instances (keeping their place and part ids);
/// <c>prefab.break</c> unlinks them; <c>prefab.save</c> saves objects as a new prefab.
/// </summary>
public static partial class ChangeApplier
{
    // Properties, not fields: the op table in another part of this class reads them during static initialisation.
    private static string[] PrefabPlaceKeys => ["name", "id", "params", "rot", "rotDeg", "face", "keepRot", .. Placement.Keys, .. MetaKeys];

    private static string[] PrefabSaveKeys => ["name", "id", "ids", "where", "selection", "pivot", "rot", "rotDeg", "title", "summary", "category", "to", "overwrite", "link", "as"];

    private static string[] PrefabUpdateKeys => ["id", "ids", "name", "all", "params"];

    private static string[] PrefabBreakKeys => ["id", "ids", "name", "all", "delete"];

    /// <summary>A prefab expanded in prefab space: the scratch map holding its parts, each object's part name, and
    /// the parts' bounds.</summary>
    private sealed record Expansion(MapProject Parts, Dictionary<string, string> PartOf, (Vec3 Min, Vec3 Max) Bounds);

    /// <summary>An instance's objects, ready to add or swap in, and the id each part got.</summary>
    private sealed record Built(List<Brush> Brushes, List<Actor> Actors, List<Ge> Ges, Dictionary<string, string> IdOf, PrefabInstance Record);

    // ---- prefab.place ---------------------------------------------------------------------------------------------

    /// <summary>prefab.place: <c>name</c>, <c>params</c>, placement (at, near, wall, on, pivot...), <c>rot</c>/
    /// <c>rotDeg</c>/<c>face</c>, meta. <c>id</c> names the instance; <c>"$door"</c> also binds each part as
    /// <c>$door.part</c>.</summary>
    private static void PrefabPlace(ChangeContext ctx, SvJsonObject op)
    {
        string name = ApiJson.GetString(op, "name") ?? throw new ApiException("name is the prefab to place", "name");
        PrefabDefinition def = ctx.FindPrefab(name, "name");
        string? requested = ApiJson.GetString(op, "id");
        string instanceId = NewInstanceId(ctx, requested, def.Name);
        SortedDictionary<string, SvJsonNode> given = ReadParams(op["params"]);
        Expansion e = Expand(ctx, def, def.Values(given, instanceId, "params"));

        // The pivot is what lands on the point, the floor (on) or the wall (its front, +X, then faces out). With
        // pivot:<anchor> the turned bounds are placed instead, so a wall placement runs again once the facing is known.
        Rot rot = ExplicitRotation(op) ?? Rot.Zero;
        bool byBounds = op["pivot"] is not null;
        (Vec3, Vec3) Extent(Rot r) => byBounds ? Turned(e.Bounds, r) : (Vec3.Zero, Vec3.Zero);
        Placed placed = Placement.Resolve(ctx, op, Vec3.Zero, Extent(rot), null);
        Rot faced = PrefabFacing(ctx, op, rot, placed);
        if (byBounds && faced != rot && placed.WallNormal is not null)
        {
            placed = Placement.Resolve(ctx, op, Vec3.Zero, Extent(faced), null);
        }

        var record = new PrefabInstance(instanceId, def.Name) { Params = given, Hash = def.Hash };
        Built built = Instantiate(ctx, e, record, placed.Location, faced, (_, _) => null);
        foreach (EditorMeta m in Metas(built))
        {
            SetMeta(op, m);
        }

        EnsureLayers(ctx, built);
        ctx.Add($"Place prefab {def.Name}", built.Brushes, built.Actors, built.Ges);
        ctx.SnapshotProject();
        ctx.Run(PrefabInstancesCommand.Put(built.Record), touchesBrushes: false);
        if (requested is not null && requested.StartsWith('$'))
        {
            ctx.Bind(requested, instanceId, "id");
            foreach ((string part, string id) in built.IdOf)
            {
                ctx.Bind($"{requested}.{part}", id, "id");
            }
        }
    }

    /// <summary>A prefab turns about Z only: <c>face</c> and a wall set its yaw.</summary>
    private static Rot PrefabFacing(ChangeContext ctx, SvJsonObject op, Rot rot, Placed placed)
    {
        if (op["rot"] is not null || op["rotDeg"] is not null)
        {
            return rot;
        }

        if (op["face"] is { } face)
        {
            return rot with { Yaw = RotatorMath.YawOf(ctx.Point(face, "face") - placed.Location) };
        }

        return placed.WallNormal is Vec3 n && !ApiJson.GetBool(op, "keepRot", false) ? rot with { Yaw = RotatorMath.YawOf(n) } : rot;
    }

    // ---- prefab.update / prefab.break ------------------------------------------------------------------------------

    /// <summary>prefab.update: re-applies the prefab to instances (<c>id</c>/<c>ids</c>, every instance of
    /// <c>name</c>, or <c>all</c>). Each keeps its place (recovered from its anchor part if its objects were moved),
    /// its part ids and the layer, folder and flags of its objects; <c>params</c> changes one instance's values (null
    /// puts a parameter back to its default). Edits made to the parts by hand are replaced.</summary>
    private static void PrefabUpdate(ChangeContext ctx, SvJsonObject op)
    {
        List<PrefabInstance> targets = InstanceTargets(ctx, op);
        if (op["params"] is not null && targets.Count != 1)
        {
            throw new ApiException("params changes one instance: give its id", "params");
        }

        foreach (PrefabInstance inst in targets)
        {
            PrefabDefinition def = ctx.FindPrefab(inst.Prefab, "name");
            var given = new SortedDictionary<string, SvJsonNode>(inst.Params, StringComparer.Ordinal);
            if (op["params"] is SvJsonObject changes)
            {
                foreach (KeyValuePair<string, SvJsonNode> kv in changes)
                {
                    if (kv.Value.Kind == SvJsonKind.Null)
                    {
                        given.Remove(kv.Key);
                    }
                    else
                    {
                        given[kv.Key] = kv.Value;
                    }
                }
            }

            UpdateInstance(ctx, inst, def, given);
        }
    }

    private static void UpdateInstance(ChangeContext ctx, PrefabInstance inst, PrefabDefinition def, SortedDictionary<string, SvJsonNode> given)
    {
        List<ObjectRef> parts = InstanceParts(ctx.Project, inst.Id);
        (Vec3 loc, Rot rot) = Recover(ctx.Project, inst, parts);
        Expansion e = Expand(ctx, def, def.Values(given, inst.Id, "params"));

        // The first object of each part name and kind keeps its id; the rest are left over and go.
        var existing = new Dictionary<(string, ObjectKind), ObjectRef>();
        foreach (ObjectRef r in parts)
        {
            if (PartOf(ctx.Project, r) is { } part)
            {
                existing.TryAdd((part, r.Kind), r);
            }
        }

        var record = new PrefabInstance(inst.Id, def.Name) { Params = given, Hash = def.Hash };
        Built built = Instantiate(ctx, e, record, loc, rot, (part, kind) => existing.TryGetValue((part, kind), out ObjectRef r) ? r.Id : null);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replace = new ReplaceObjectsCommand($"Update prefab instance {inst.Id}");
        var added = (Brushes: new List<Brush>(), Actors: new List<Actor>(), Ges: new List<Ge>());
        foreach (Brush b in built.Brushes)
        {
            if (ctx.Project.FindBrush(b.Id) is { } old && kept.Add(b.Id))
            {
                KeepMeta(old.Editor, b.Editor);
                if (Changed(ModelJson.BrushToJson(old), ModelJson.BrushToJson(b)))
                {
                    replace.Brush(old.Clone(), b);
                }
            }
            else
            {
                added.Brushes.Add(b);
            }
        }

        foreach (Actor a in built.Actors)
        {
            if (ctx.Project.FindActor(a.Id) is { } old && kept.Add(a.Id))
            {
                KeepMeta(old.Editor, a.Editor);
                if (Changed(ModelJson.ActorToJson(old), ModelJson.ActorToJson(a)))
                {
                    replace.Actor(old.Clone(), a);
                }
            }
            else
            {
                added.Actors.Add(a);
            }
        }

        foreach (Ge g in built.Ges)
        {
            if (ctx.Project.FindGe(g.Id) is { } old && kept.Add(g.Id))
            {
                KeepMeta(old.Editor, g.Editor);
                if (Changed(ModelJson.GeToJson(old), ModelJson.GeToJson(g)))
                {
                    replace.Ge(old.Clone(), g);
                }
            }
            else
            {
                added.Ges.Add(g);
            }
        }

        if (!replace.IsEmpty)
        {
            foreach (ObjectRef r in parts.Where(r => kept.Contains(r.Id)))
            {
                ctx.Touch(r);
            }

            ctx.Run(replace);
        }

        // Left over: a second object claiming a part is a copy made outside the copy tools (which unlink), so it stays
        // as a plain object; the rest are parts the prefab no longer makes, and go.
        List<ObjectRef> left = parts.Where(r => !kept.Contains(r.Id)).ToList();
        List<ObjectRef> copies = left.Where(r => PartOf(ctx.Project, r) is { } part && existing.TryGetValue((part, r.Kind), out ObjectRef first)
            && first != r).ToList();
        if (copies.Count > 0)
        {
            ctx.Warn("id", $"{string.Join(", ", copies.Select(r => r.Id))} copied a part of {inst.Id}: unlinked, not updated");
            ctx.Edit(copies, $"Unlink copies of {inst.Id}", brush: b => b.Editor.UnlinkPrefab(), actor: a => a.Editor.UnlinkPrefab(),
                ge: g => g.Editor.UnlinkPrefab());
        }

        List<ObjectRef> gone = left.Except(copies).ToList();
        if (gone.Count > 0)
        {
            ctx.Delete(gone);
        }

        if (added.Brushes.Count + added.Actors.Count + added.Ges.Count > 0)
        {
            EnsureLayers(ctx, built);
            ctx.Add($"Update prefab instance {inst.Id}", added.Brushes, added.Actors, added.Ges);
        }

        ctx.SnapshotProject();
        ctx.Run(PrefabInstancesCommand.Put(built.Record), touchesBrushes: false);

        // The provenance stamp alone is not a change worth a diff entry.
        static bool Changed(SvJsonObject before, SvJsonObject after) =>
            ApiJson.Write(Unstamped(before)) != ApiJson.Write(Unstamped(after));
    }

    /// <summary>prefab.break: unlinks instances (their objects stay, as plain objects); <c>delete</c> removes the
    /// objects too.</summary>
    private static void PrefabBreak(ChangeContext ctx, SvJsonObject op)
    {
        List<PrefabInstance> targets = InstanceTargets(ctx, op);
        var ids = targets.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ObjectRef> parts = ctx.Project.AllRefs().Where(r => Meta(ctx.Project, r)?.PrefabInstance is { } i && ids.Contains(i)).ToList();
        if (ApiJson.GetBool(op, "delete", false))
        {
            if (parts.Count > 0)
            {
                ctx.Delete(parts);
            }
        }
        else
        {
            ctx.Edit(parts, "Break prefab", brush: b => b.Editor.UnlinkPrefab(), actor: a => a.Editor.UnlinkPrefab(), ge: g => g.Editor.UnlinkPrefab());
        }

        ctx.SnapshotProject();
        ctx.Run(PrefabInstancesCommand.Remove(ids), touchesBrushes: false);
    }

    // ---- prefab.save ----------------------------------------------------------------------------------------------

    /// <summary>prefab.save: saves objects (<c>id</c>/<c>ids</c>/<c>where</c>) as prefab <c>name</c> around
    /// <c>pivot</c> (default: the bottom centre of their bounds); <c>rot</c>/<c>rotDeg</c> is the way the group faces
    /// now (its yaw becomes the prefab's front, +X). <c>to</c>: project (the map's prefabs folder, default) or library.
    /// <c>link</c> makes the objects an instance of the new prefab (<c>as</c> names it). The file is written when the
    /// document commits.</summary>
    private static void PrefabSave(ChangeContext ctx, SvJsonObject op)
    {
        string name = ApiJson.GetString(op, "name") ?? throw new ApiException("name is the new prefab's name", "name");
        if (!PrefabDefinition.IsValidName(name))
        {
            throw new ApiException($"'{name}' is not a prefab name (letters, digits, _ and -; up to 64)", "name");
        }

        bool fromSelection = ApiJson.GetBool(op, "selection", false);
        List<ObjectRef> refs = ctx.Targets(op, required: !fromSelection);
        if (fromSelection)
        {
            refs = [.. ctx.Selected.Where(ctx.Project.Exists).Concat(refs).Distinct()];
        }

        if (refs.Count == 0)
        {
            throw new ApiException(fromSelection ? "nothing is selected" : "nothing to save", fromSelection ? "selection" : "where");
        }

        // The default pivot is the bottom centre. An actor's collision cylinder may reach under the floor, so the
        // bottom is the brushes' and GEs' when there are any; a mover's (mesh-shaped) bottom is its origin.
        (Vec3 Min, Vec3 Max) box = Union(ctx.Project, refs);
        List<ObjectRef> solid = refs.Where(r => r.Kind != ObjectKind.Actor).ToList();
        double bottom = solid.Count > 0
            ? Union(ctx.Project, solid).Min.Z
            : refs.Select(r => ctx.Project.FindActor(r.Id)!).Min(a => MoverBase.IsMover(a) ? a.Location.Z : MapObjects.ActorBounds(a).Min.Z);
        Vec3 pivot = op["pivot"] is { } pv ? ctx.Point(pv, "pivot") : ChangeContext.Center(box) with { Z = bottom };
        Rot facing = ExplicitRotation(op) ?? Rot.Zero;
        if (facing.Pitch != 0 || facing.Roll != 0)
        {
            throw new ApiException("a prefab's facing is a yaw: [0,yaw,0]", op["rot"] is null ? "rotDeg" : "rot");
        }

        string target = ApiJson.GetString(op, "to") ?? "project";
        string folder = target switch
        {
            "project" => PrefabStore.ProjectFolder(ctx.Folder)
                ?? throw new ApiException("the map is not saved yet: save it first, or save the prefab to the library (to:\"library\")", "to"),
            "library" => PrefabStore.LibraryFolder,
            _ => throw new ApiException("to is project or library", "to"),
        };

        // Overwriting keeps the old title, summary and category unless new ones are given.
        PrefabDefinition? old = ctx.PendingPrefabs.TryGetValue(name, out var pending) ? pending.Prefab
            : PrefabStore.FileIn(folder, name) is { } file ? PrefabStore.Read(file, PrefabSource.Project) : null;
        if (old is not null && !ApiJson.GetBool(op, "overwrite", false))
        {
            throw new ApiException($"prefab {name} exists in the {target} prefabs; overwrite:true replaces it", "name");
        }

        if (old is null && Hidden(ctx, name, target) is { } hidden)
        {
            ctx.Warn("name", $"the new {target} prefab {name} hides the {hidden} one of that name: its instances update from the new one");
        }

        PrefabTransform local = PrefabTransform.Local(pivot, facing.Yaw);
        var saved = new HashSet<string>(refs.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        var brushes = new List<SvJsonNode>();
        var actors = new List<SvJsonNode>();
        var ges = new List<SvJsonNode>();
        int baked = 0, carving = 0;
        foreach (ObjectRef r in refs)
        {
            switch (r.Kind)
            {
                case ObjectKind.Brush:
                case ObjectKind.Stair:
                    Brush b = ctx.Project.FindBrush(r.Id)!.Clone();
                    local.Apply(b);
                    // A prefab holds copies: a brush imported with the baked world is a new brush in it.
                    (int bk, int cv) = BakedContent.Unmark([b]);
                    baked += bk;
                    carving += cv;
                    brushes.Add(ModelJson.BrushToJson(b, includeEditor: false));
                    break;
                case ObjectKind.Actor:
                    Actor a = ctx.Project.FindActor(r.Id)!.Clone();
                    local.Apply(a);
                    foreach (string other in References(a.Props).Where(x => !saved.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        ctx.Warn("ids", $"{a.Id} references {other}, which is not saved with the prefab: placed copies keep pointing at {other}");
                    }

                    actors.Add(ModelJson.ActorToJson(a, includeEditor: false));
                    break;
                default:
                    Ge g = ctx.Project.FindGe(r.Id)!.Clone();
                    local.Apply(g);
                    ges.Add(ModelJson.GeToJson(g, includeEditor: false));
                    break;
            }
        }

        var objects = ApiJson.Obj(
            ("brushes", brushes.Count > 0 ? ApiJson.Arr(brushes) : null),
            ("actors", actors.Count > 0 ? ApiJson.Arr(actors) : null),
            ("ges", ges.Count > 0 ? ApiJson.Arr(ges) : null));
        var def = new PrefabDefinition
        {
            Name = name,
            Title = ApiJson.GetString(op, "title") ?? old?.Title,
            Summary = ApiJson.GetString(op, "summary") ?? old?.Summary,
            Category = ApiJson.GetString(op, "category") ?? old?.Category,
            Objects = objects,
            Source = target == "library" ? PrefabSource.Library : PrefabSource.Project,
        };
        ctx.PendingPrefabs[name] = (def, folder);
        if (BakedContent.CopyNote(ctx.Project, baked, carving) is { } note)
        {
            ctx.Warn("ids", note.Message + (ApiJson.GetBool(op, "link", false) && BakedContent.BakedWorldKept(ctx.Project)
                ? "; the linked originals become such copies too once their instance is updated, built over the kept baked world that still has them"
                : ""));
        }

        if (ApiJson.GetBool(op, "link", false))
        {
            LinkSaved(ctx, op, def, refs, pivot, facing);
        }
    }

    /// <summary>The source of a prefab of that name a new one saved to <paramref name="target"/> would hide, or null.</summary>
    private static string? Hidden(ChangeContext ctx, string name, string target)
    {
        PrefabDefinition? other;
        try
        {
            other = PrefabStore.Find(name, ctx.Folder);
        }
        catch (ApiException)
        {
            return null; // a broken file of that name: the new one replaces nothing that works
        }

        return other?.Source switch
        {
            PrefabSource.Builtin => "built-in",
            PrefabSource.Library when target == "project" => "library",
            _ => null,
        };
    }

    /// <summary>Makes saved objects an instance of their new prefab, so a later update of the prefab reaches them.</summary>
    private static void LinkSaved(ChangeContext ctx, SvJsonObject op, PrefabDefinition def, List<ObjectRef> refs, Vec3 pivot, Rot facing)
    {
        string? requested = ApiJson.GetString(op, "as");
        string instanceId = NewInstanceId(ctx, requested, def.Name);
        foreach (string other in refs.Select(r => Meta(ctx.Project, r)?.PrefabInstance).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ctx.Warn("link", $"objects of instance {other} now belong to {instanceId}");
        }

        ObjectRef anchor = refs.FirstOrDefault(r => r.Kind == ObjectKind.Actor, refs[0]);
        PrefabTransform local = PrefabTransform.Local(pivot, facing.Yaw);
        (Vec3 at, Rot turn) = AnchorPose(ctx.Project, anchor);
        var record = new PrefabInstance(instanceId, def.Name)
        {
            Location = pivot,
            Rotation = facing,
            Hash = def.Hash,
            AnchorPart = anchor.Id,
            AnchorLocal = local.Point(at),
            AnchorRotation = RotatorMath.Tidy(local.Turn(turn)),
        };
        ctx.Edit(refs, $"Link to prefab {def.Name}",
            brush: b => Link(b.Editor, b.Id),
            actor: a => Link(a.Editor, a.Id),
            ge: g => Link(g.Editor, g.Id));
        ctx.SnapshotProject();
        ctx.Run(PrefabInstancesCommand.Put(record), touchesBrushes: false);
        if (requested is not null && requested.StartsWith('$'))
        {
            ctx.Bind(requested, instanceId, "as");
        }

        void Link(EditorMeta m, string part)
        {
            m.PrefabInstance = instanceId;
            m.PrefabPart = part;
        }
    }

    // ---- expansion and instantiation ------------------------------------------------------------------------------

    /// <summary>Builds a prefab's parts in an empty scratch map: its saved objects, then its ops (a change document
    /// run there), with parameters substituted. Part names: object ids, or the symbols ops bound
    /// (<c>$beam#1</c> → <c>beam_1</c>).</summary>
    private static Expansion Expand(ChangeContext ctx, PrefabDefinition def, Dictionary<string, object> values)
    {
        // Templates may size things by the map's body numbers (a parameter of the same name wins).
        values = new Dictionary<string, object>(values, StringComparer.Ordinal);
        Validation.DesignNumbers body = ctx.Body;
        foreach ((string key, double v) in BodyValues(body))
        {
            values.TryAdd(key, v);
        }

        var scratch = new MapProject { Id = "Prefab", Title = def.Name, Workspace = ctx.Project.Workspace.Clone() };
        var partOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (def.Objects is { } objects)
        {
            var o = (SvJsonObject)PrefabTemplate.Substitute(objects, values, "objects");
            scratch.Brushes.AddRange(Items(o, "brushes").Select(ModelJson.BrushFromJson));
            // Prefabs saved before copies lost the baked mark may still carry it.
            BakedContent.Unmark(scratch.Brushes);
            scratch.Actors.AddRange(Items(o, "actors").Select(ModelJson.ActorFromJson));
            scratch.Ges.AddRange(Items(o, "ges").Select(ModelJson.GeFromJson));
            foreach (ObjectRef r in scratch.AllRefs())
            {
                if (!ChangeContext.IsValidId(r.Id) || !partOf.TryAdd(r.Id, r.Id))
                {
                    throw new ApiException($"prefab {def.Name}: object id '{r.Id}' is missing, not an id or used twice", "name");
                }
            }
        }

        if (def.Ops is { } template)
        {
            var ops = new SvJsonArray();
            var index = new List<int>();
            for (int i = 0; i < template.Count; i++)
            {
                if (PrefabTemplate.Substitute(template[i], values, $"ops[{i}]") is SvJsonObject op && PrefabTemplate.Keep(op) is { } kept)
                {
                    if (ApiJson.GetString(kept, "op") is { } name && name.StartsWith("prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ApiException($"prefab {def.Name} ops[{i}]: prefabs do not place other prefabs", "name");
                    }

                    ops.Add(kept);
                    index.Add(i);
                }
            }

            ChangeResult r = Apply(new EditorSession(scratch), ApiJson.Obj(("format", ApiJson.Str(Format)), ("ops", ops),
                ("author", ApiJson.Str(ctx.Author)), ("label", ApiJson.Str("prefab " + def.Name))));
            if (!r.Ok)
            {
                SvJsonObject err = r.Json["error"] as SvJsonObject ?? new SvJsonObject();
                int at = err["op"] is { } n && ApiJson.TryNumber(n, out double d) && (int)d < index.Count ? index[(int)d] : -1;
                throw new ApiException($"prefab {def.Name}{(at >= 0 ? $" ops[{at}]" : "")}"
                    + $"{(ApiJson.GetString(err, "field") is { } f ? $" {f}" : "")}: {ApiJson.GetString(err, "message")}", "params");
            }

            if (r.Json["ids"] is SvJsonObject ids)
            {
                foreach (KeyValuePair<string, SvJsonNode> kv in ids)
                {
                    if (kv.Value is SvJsonString id && scratch.Find(id.Value) is not null)
                    {
                        partOf.TryAdd(id.Value, PartName(kv.Key));
                    }
                }
            }

            if (r.Json["warnings"] is SvJsonArray warnings)
            {
                // Templates set engine-only properties (mover keys) on purpose: that note is noise here.
                foreach (string w in warnings.OfType<SvJsonString>().Select(x => x.Value).Where(x => !x.EndsWith("is not editable in the original editor", StringComparison.Ordinal)))
                {
                    ctx.Warn("name", $"prefab {def.Name}: {w}");
                }
            }
        }

        List<ObjectRef> all = scratch.AllRefs().ToList();
        if (all.Count == 0)
        {
            throw new ApiException($"prefab {def.Name} makes no objects with these parameters", "params");
        }

        foreach (ObjectRef r in all)
        {
            partOf.TryAdd(r.Id, r.Id);
        }

        // Two parts may not share a name (a symbol made "a_1" and an object is called a_1): number the later one.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectRef r in all)
        {
            string part = partOf[r.Id];
            for (int k = 1; !used.Add(part); k++)
            {
                part = $"{partOf[r.Id]}_{k}";
            }

            partOf[r.Id] = part;
        }

        return new Expansion(scratch, partOf, Union(scratch, all));
    }

    /// <summary>Copies the expanded parts into the map's space for one instance: ids (<c>&lt;instance&gt;_&lt;part&gt;</c>, or
    /// the id <paramref name="existingId"/> gives a part), actor references between parts, internal Event/Tag names
    /// made unique to the instance, the transform, and the prefab link.</summary>
    private static Built Instantiate(ChangeContext ctx, Expansion e, PrefabInstance record, Vec3 location, Rot rotation,
        Func<string, ObjectKind, string?> existingId)
    {
        rotation = RotatorMath.Tidy(rotation);
        var idOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var newId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectRef r in e.Parts.AllRefs())
        {
            string part = e.PartOf[r.Id];
            string id = existingId(part, r.Kind) ?? FreshId(ctx.Project, $"{record.Id}_{part}", taken);
            taken.Add(id);
            idOf[part] = id;
            newId[r.Id] = id;
        }

        // Tags the parts fire at each other belong to this instance; tags fired from outside keep their names.
        var tags = e.Parts.Actors.Select(a => a.Props.GetString("Tag")).OfType<string>().Where(t => t.Length > 0 && t != "None").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var heard = e.Parts.Actors.SelectMany(a => a.Props.SelectMany(kv => EventNames(kv.Value, IsEventKey(kv.Key)))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var renamed = tags.Where(heard.Contains).ToDictionary(t => t, t => $"{record.Id}_{t}", StringComparer.OrdinalIgnoreCase);

        PrefabTransform place = PrefabTransform.Place(location, rotation);
        var brushes = e.Parts.Brushes.Select(b =>
        {
            Brush c = b.Clone(newId[b.Id]);
            place.Apply(c);
            c.Rotation = RotatorMath.Tidy(c.Rotation);
            if (c.Shape.Builder is { } spec && spec.Strings.TryGetValue(Geometry.Builders.BuilderArgs.PieceKey, out string? piece) && newId.TryGetValue(piece, out string? pieceId))
            {
                spec.Strings[Geometry.Builders.BuilderArgs.PieceKey] = pieceId;
            }

            return c;
        }).ToList();
        var actors = e.Parts.Actors.Select(a =>
        {
            Actor c = a.Clone(newId[a.Id]);
            place.Apply(c);
            c.Rotation = RotatorMath.Tidy(c.Rotation);
            foreach (string key in c.Props.Keys.ToList())
            {
                c.Props[key] = Relink(c.Props[key]!, key.Equals("Tag", StringComparison.OrdinalIgnoreCase) || IsEventKey(key), newId, renamed);
            }

            foreach (string other in References(c.Props).Where(x => !newId.Values.Contains(x, StringComparer.OrdinalIgnoreCase) && ctx.Project.Find(x) is null))
            {
                ctx.Warn("name", $"{c.Id} references {other}, which is not in the map");
            }

            return c;
        }).ToList();
        var ges = e.Parts.Ges.Select(g =>
        {
            Ge c = g.Clone(newId[g.Id]);
            place.Apply(c);
            return c;
        }).ToList();

        var built = new Built(brushes, actors, ges, idOf, record);
        var partOfId = idOf.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        foreach ((EditorMeta m, string id) in brushes.Select(b => (b.Editor, b.Id)).Concat(actors.Select(a => (a.Editor, a.Id))).Concat(ges.Select(g => (g.Editor, g.Id))))
        {
            m.PrefabInstance = record.Id;
            m.PrefabPart = partOfId[id];
            ctx.Stamp(m);
        }

        // The anchor is where updates find the instance again after its objects were moved: an actor if it has one.
        ObjectRef anchor = e.Parts.AllRefs().FirstOrDefault(r => r.Kind == ObjectKind.Actor, e.Parts.AllRefs().First());
        (Vec3 at, Rot turn) = AnchorPose(e.Parts, anchor);
        record.Location = location;
        record.Rotation = rotation;
        record.AnchorPart = e.PartOf[anchor.Id];
        record.AnchorLocal = at;
        record.AnchorRotation = RotatorMath.Tidy(turn);
        return built;
    }

    /// <summary>Where an instance is now: from its anchor part when that still exists (the instance's objects may have
    /// been moved or turned about Z), else as recorded.</summary>
    private static (Vec3 Location, Rot Rotation) Recover(MapProject p, PrefabInstance inst, List<ObjectRef> parts)
    {
        ObjectRef? anchor = inst.AnchorPart is { } name ? parts.Cast<ObjectRef?>().FirstOrDefault(r => PartOf(p, r!.Value) == name) : null;
        if (anchor is not ObjectRef r)
        {
            return (inst.Location, inst.Rotation);
        }

        (Vec3 at, Rot now) = AnchorPose(p, r);
        Rot rot = inst.Rotation;
        if (r.Kind != ObjectKind.Ge && rot.Pitch == 0 && rot.Roll == 0)
        {
            // The anchor stood at its prefab rotation turned by the instance's; any turn since is the user's.
            int turn = YawTurn(RotatorMath.Compose(inst.AnchorRotation, inst.Rotation), now);
            rot = new Rot(0, inst.Rotation.Yaw + turn, 0).Tidy();
        }

        // An instance nobody moved keeps its recorded place exactly (no rounding noise in the diff).
        Vec3 loc = at - rot.Rotate(inst.AnchorLocal);
        return rot == inst.Rotation && (loc - inst.Location).Length < 1e-3
            ? (inst.Location, inst.Rotation)
            : (new Vec3(Math.Round(loc.X, 4), Math.Round(loc.Y, 4), Math.Round(loc.Z, 4)), rot);
    }

    /// <summary>How far <paramref name="now"/> is turned about Z from <paramref name="was"/>, read off the forward axis
    /// (the right axis when the part looks straight up or down), so a tilted anchor (a camera) works too.</summary>
    private static int YawTurn(Rot was, Rot now)
    {
        (Vec3 f0, Vec3 r0, _) = was.Axes();
        (Vec3 f1, Vec3 r1, _) = now.Axes();
        bool forwardHasHeading = f0.X * f0.X + f0.Y * f0.Y > 0.01;
        return forwardHasHeading ? RotatorMath.YawOf(f1) - RotatorMath.YawOf(f0) : RotatorMath.YawOf(r1) - RotatorMath.YawOf(r0);
    }

    private static (Vec3 At, Rot Rotation) AnchorPose(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Actor => (p.FindActor(r.Id)!.Location, p.FindActor(r.Id)!.Rotation),
        ObjectKind.Brush or ObjectKind.Stair => (p.FindBrush(r.Id)!.Location, p.FindBrush(r.Id)!.Rotation),
        _ => (MapObjects.Origin(p, r), Rot.Zero),
    };

    // ---- helpers --------------------------------------------------------------------------------------------------

    /// <summary>The body numbers prefab templates can use by name.</summary>
    internal static IEnumerable<(string Name, double Value)> BodyValues(Validation.DesignNumbers body) =>
    [
        ("standHeight", body.StandHeight),
        ("spyCrouchHeight", body.SpyCrouchHeight),
        ("mercCrouchHeight", body.MercCrouchHeight),
        ("bodyRadius", body.BodyRadius),
        ("stepHeight", body.MaxStepHeight),
        ("standClearance", body.StandClearance),
        ("ventHeight", body.VentHeight),
        ("crawlwayHeight", body.CrawlwayHeight),
    ];

    private static List<PrefabInstance> InstanceTargets(ChangeContext ctx, SvJsonObject op)
    {
        var found = new List<PrefabInstance>();
        var ids = new List<(string Id, string Field)>();
        if (ApiJson.GetString(op, "id") is { } one)
        {
            ids.Add((one, "id"));
        }

        if (ApiJson.StringList(op["ids"], "ids") is { } many)
        {
            ids.AddRange(many.Select((x, i) => (x, $"ids[{i}]")));
        }

        foreach ((string raw, string field) in ids)
        {
            string id = ctx.ResolveId(raw, field);
            PrefabInstance? inst = FindInstance(ctx.Project, id);
            // A part's id stands for its instance.
            if (inst is null && ctx.Project.Find(id) is ObjectRef r && Meta(ctx.Project, r)?.PrefabInstance is { } owner)
            {
                inst = FindInstance(ctx.Project, owner);
            }

            found.Add(inst ?? throw new ApiException($"no prefab instance '{raw}'"
                + (ctx.Project.Prefabs.Count > 0 ? $" (instances: {string.Join(", ", ctx.Project.Prefabs.Take(10).Select(i => i.Id))})" : " (the map has none)"), field));
        }

        if (ApiJson.GetString(op, "name") is { } prefab)
        {
            List<PrefabInstance> of = ctx.Project.Prefabs.Where(i => string.Equals(i.Prefab, prefab, StringComparison.OrdinalIgnoreCase)).ToList();
            if (of.Count == 0)
            {
                ctx.Warn("name", $"no instances of prefab {prefab}");
            }

            found.AddRange(of);
        }

        if (ApiJson.GetBool(op, "all", false))
        {
            found.AddRange(ctx.Project.Prefabs);
        }

        if (ids.Count == 0 && op["name"] is null && op["all"] is null)
        {
            throw new ApiException("give id, ids (instance ids), name (every instance of a prefab) or all:true", "id");
        }

        return found.DistinctBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Select(i => i.Clone()).ToList();
    }

    private static PrefabInstance? FindInstance(MapProject p, string id) =>
        p.Prefabs.Find(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    internal static List<ObjectRef> InstanceParts(MapProject p, string instanceId) =>
        p.AllRefs().Where(r => string.Equals(Meta(p, r)?.PrefabInstance, instanceId, StringComparison.OrdinalIgnoreCase)).ToList();

    private static string? PartOf(MapProject p, ObjectRef r) => Meta(p, r)?.PrefabPart;

    private static EditorMeta? Meta(MapProject p, ObjectRef r) => p.MetaOf(r);

    private static IEnumerable<EditorMeta> Metas(Built b) =>
        b.Brushes.Select(x => x.Editor).Concat(b.Actors.Select(x => x.Editor)).Concat(b.Ges.Select(x => x.Editor));

    private static void EnsureLayers(ChangeContext ctx, Built b)
    {
        foreach (string layer in Metas(b).Select(m => m.Layer).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            EnsureLayer(ctx, layer);
        }
    }

    /// <summary>An updated part keeps where the user filed it and how it is shown.</summary>
    private static void KeepMeta(EditorMeta from, EditorMeta to)
    {
        to.Layer = from.Layer;
        to.Folder = from.Folder;
        to.Hidden = from.Hidden;
        to.Locked = from.Locked;
        to.Note = from.Note;
    }

    private static SvJsonObject Unstamped(SvJsonObject o)
    {
        var copy = new SvJsonObject();
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            if (kv.Key == "editor" && kv.Value is SvJsonObject meta)
            {
                var m = new SvJsonObject();
                foreach (KeyValuePair<string, SvJsonNode> mk in meta)
                {
                    if (mk.Key is not ("author" or "op"))
                    {
                        m.Add(mk.Key, mk.Value);
                    }
                }

                copy.Add(kv.Key, m);
            }
            else
            {
                copy.Add(kv.Key, kv.Value);
            }
        }

        return copy;
    }

    private static string NewInstanceId(ChangeContext ctx, string? requested, string prefab)
    {
        if (requested is not null && !requested.StartsWith('$'))
        {
            if (!ChangeContext.IsValidId(requested))
            {
                throw new ApiException($"'{requested}' is not a valid instance id (letters, digits and _)", "id");
            }

            return FindInstance(ctx.Project, requested) is null ? requested : throw new ApiException($"prefab instance {requested} exists", "id");
        }

        // laser_grid_alarm -> LaserGridAlarm0, LaserGridAlarm1...
        var stem = new StringBuilder();
        foreach (string word in prefab.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            stem.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        }

        string s = stem.Length > 0 && !char.IsDigit(stem[0]) ? stem.ToString() : "Prefab" + stem;
        for (int i = 0; ; i++)
        {
            string id = s + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (FindInstance(ctx.Project, id) is null && ctx.Project.Find(id) is null)
            {
                return id;
            }
        }
    }

    private static string FreshId(MapProject p, string stem, HashSet<string> taken)
    {
        for (int i = 0; ; i++)
        {
            string id = i == 0 ? stem : $"{stem}_{i}";
            if (!taken.Contains(id) && p.Find(id) is null)
            {
                return id;
            }
        }
    }

    private static string PartName(string symbol)
    {
        var sb = new StringBuilder();
        foreach (char c in symbol.TrimStart('$'))
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        return sb.ToString();
    }

    private static SortedDictionary<string, SvJsonNode> ReadParams(SvJsonNode? node)
    {
        var given = new SortedDictionary<string, SvJsonNode>(StringComparer.Ordinal);
        if (node is null)
        {
            return given;
        }

        if (node is not SvJsonObject o)
        {
            throw new ApiException("params is an object of parameter values", "params");
        }

        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            given[kv.Key] = kv.Value;
        }

        return given;
    }

    private static (Vec3 Min, Vec3 Max) Union(MapProject p, IEnumerable<ObjectRef> refs) =>
        refs.Select(r => MapObjects.Bounds(p, r)).Aggregate((x, y) => (Vec3.Min(x.Min, y.Min), Vec3.Max(x.Max, y.Max)));

    private static IEnumerable<SvJsonObject> Items(SvJsonObject o, string key) =>
        o[key] is SvJsonArray a ? a.OfType<SvJsonObject>() : [];

    /// <summary>The bounds of a box turned about the origin.</summary>
    private static (Vec3 Min, Vec3 Max) Turned((Vec3 Min, Vec3 Max) b, Rot rot)
    {
        Vec3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        for (int i = 0; i < 8; i++)
        {
            Vec3 c = rot.Rotate(new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
            min = Vec3.Min(min, c);
            max = Vec3.Max(max, c);
        }

        return (min, max);
    }

    /// <summary>Event-name properties: Event, Events, and any *Event / *Events (OpeningEvent, PlayerBumpEvent...).</summary>
    private static bool IsEventKey(string key) =>
        key.EndsWith("Event", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Events", StringComparison.OrdinalIgnoreCase);

    /// <summary>The event names inside a property value (struct members and array items included).</summary>
    private static IEnumerable<string> EventNames(SvJsonNode node, bool isEvent) => node switch
    {
        SvJsonString s when isEvent && s.Value.Length > 0 && s.Value != "None" => [s.Value],
        SvJsonObject o => o.SelectMany(kv => EventNames(kv.Value, IsEventKey(kv.Key) || (isEvent && IsElementKey(kv.Key)))),
        SvJsonArray a => a.SelectMany(x => EventNames(x, isEvent)),
        _ => [],
    };

    /// <summary>A property value with actor references moved to the new part ids and instance tags renamed.</summary>
    private static SvJsonNode Relink(SvJsonNode node, bool isName, Dictionary<string, string> ids, Dictionary<string, string> tags)
    {
        switch (node)
        {
            case SvJsonString s when isName && tags.TryGetValue(s.Value, out string? renamed):
                return ApiJson.Str(renamed);
            case SvJsonObject o when PropValues.ActorId(o) is { } id:
                return ids.TryGetValue(id, out string? now) ? PropValues.ActorRef(now) : o;
            case SvJsonObject o:
                var copy = new SvJsonObject();
                foreach (KeyValuePair<string, SvJsonNode> kv in o)
                {
                    copy.Add(kv.Key, Relink(kv.Value, IsEventKey(kv.Key) || (isName && IsElementKey(kv.Key)), ids, tags));
                }

                return copy;
            case SvJsonArray a:
                return new SvJsonArray(a.Select(x => Relink(x, isName, ids, tags)));
            default:
                return node;
        }
    }

    /// <summary>The members of a static array (<c>{"$elements":{"0":..}}</c>) carry their property's meaning.</summary>
    private static bool IsElementKey(string key) => key == "$elements" || key.All(char.IsAsciiDigit);
}
