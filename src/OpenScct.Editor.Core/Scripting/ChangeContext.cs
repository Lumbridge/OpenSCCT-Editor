using System.Text.RegularExpressions;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>
/// The state of one change document while it applies: symbols (<c>$door1</c>) bound to real ids, the commands run so
/// far (executed at once, so later ops see earlier results, and rolled back on error or dry run), provenance, warnings,
/// and before-snapshots of every touched object for the diff.
/// </summary>
internal sealed partial class ChangeContext
{
    private readonly List<IEditCommand> _commands = new();
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Tracked> _order = new();
    private WorldGeometry? _geometry;

    public ChangeContext(MapProject project, string? folder, string author, string label)
    {
        Project = project;
        Folder = folder;
        Author = author;
        Label = label;
        Coercion = new PropCoercion(EditorServices.Classes, TryActorId);
    }

    public MapProject Project { get; }

    public string? Folder { get; }

    public string Author { get; }

    public string Label { get; }

    /// <summary>The index of the op being applied.</summary>
    public int OpIndex { get; set; }

    public Dictionary<string, string> Symbols { get; } = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = new();

    public PropCoercion Coercion { get; }

    /// <summary>The map's body and clearance numbers (from its movement limits).</summary>
    public Validation.DesignNumbers Body => Validation.DesignNumbers.Of(Project);

    public IReadOnlyList<IEditCommand> Commands => _commands;

    /// <summary>The session's selection when the document started (ops that take <c>selection:true</c>).</summary>
    public IReadOnlyList<ObjectRef> Selected { get; init; } = [];

    /// <summary>Selection requests, applied after the document commits (selection is not part of undo).</summary>
    public List<(string Mode, List<ObjectRef> Refs)> SelectRequests { get; } = new();

    /// <summary>The project-level JSON (map.json, layers, workspace, prefab instances) before the first op that changes
    /// them, for the diff.</summary>
    public SvJsonObject? ProjectBefore { get; private set; }

    public void Warn(string field, string message) => Warnings.Add($"op {OpIndex} {field}: {message}");

    /// <summary>Prefabs saved by this document: written to disk only after it commits (never on a dry run), but
    /// placeable by later ops at once.</summary>
    public Dictionary<string, (PrefabDefinition Prefab, string Folder)> PendingPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files to copy into the map folder (outfit textures and models, as project paths) once the document
    /// commits.</summary>
    public List<(string From, string To)> PendingCopies { get; } = new();

    /// <summary>The prefab of that name (saved earlier in this document, in the map, the library or built in).</summary>
    public PrefabDefinition FindPrefab(string name, string field)
    {
        if (PendingPrefabs.TryGetValue(name, out var pending))
        {
            return pending.Prefab;
        }

        if (PrefabStore.Find(name, Folder) is { } def)
        {
            return def;
        }

        (List<PrefabDefinition> all, _) = PrefabStore.All(Folder);
        string[] close = all.Select(p => p.Name).Where(n => n.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(n, StringComparison.OrdinalIgnoreCase)).Take(5).ToArray();
        throw new ApiException($"no prefab '{name}'" + (close.Length > 0 ? $"; did you mean {string.Join(", ", close)}?" : $" (prefabs: {string.Join(", ", all.Select(p => p.Name))})"), field);
    }

    // ---- commands -----------------------------------------------------------------------------------------------

    /// <summary>Runs a command now and keeps it for the undo step. Say when it cannot change brushes, so the
    /// geometry used for placement is not rebuilt for nothing.</summary>
    public void Run(IEditCommand command, bool touchesBrushes = true)
    {
        command.Do(Project);
        _commands.Add(command);
        if (touchesBrushes)
        {
            _geometry = null;
        }
    }

    /// <summary>Undoes everything run so far, newest first.</summary>
    public void Rollback()
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
        {
            _commands[i].Undo(Project);
        }

        _geometry = null;
    }

    /// <summary>Edits existing objects through a before/after snapshot command, stamping provenance.</summary>
    public void Edit(IReadOnlyList<ObjectRef> refs, string name, Action<Brush>? brush = null, Action<Actor>? actor = null, Action<Ge>? ge = null)
    {
        if (refs.Count == 0)
        {
            return;
        }

        foreach (ObjectRef r in refs)
        {
            Touch(r);
        }

        ReplaceObjectsCommand cmd = ReplaceObjectsCommand.Edit(Project, refs, name,
            brush: brush is null ? null : b => { brush(b); Stamp(b.Editor); },
            actor: actor is null ? null : a => { actor(a); Stamp(a.Editor); },
            ge: ge is null ? null : g => { ge(g); Stamp(g.Editor); });
        if (!cmd.IsEmpty)
        {
            Run(cmd, refs.Any(r => r.Kind == ObjectKind.Brush));
        }
    }

    /// <summary>Adds new objects (already stamped by <see cref="Stamp"/>).</summary>
    public void Add(string name, IEnumerable<Brush>? brushes = null, IEnumerable<Actor>? actors = null, IEnumerable<Ge>? ges = null)
    {
        var cmd = new AddObjectsCommand(name, brushes, actors, ges);
        foreach (ObjectRef r in cmd.Added)
        {
            Created(r);
        }

        Run(cmd, cmd.Added.Any(r => r.Kind == ObjectKind.Brush));
    }

    public void Delete(IReadOnlyList<ObjectRef> refs)
    {
        foreach (ObjectRef r in refs)
        {
            Touch(r);
        }

        Run(new DeleteObjectsCommand(refs), refs.Any(r => r.Kind == ObjectKind.Brush));
        RekeyPieces(PieceKeys.BrushIds(refs));
    }

    /// <summary>Re-keys the pieces whose first part was one of the gone brush ids (<see cref="PieceKeys"/>).</summary>
    public void RekeyPieces(IReadOnlySet<string> gone)
    {
        foreach ((IReadOnlyList<Brush> parts, string key) in PieceKeys.Orphans(Project, gone))
        {
            Edit(parts.Select(b => ObjectRef.Brush(b.Id)).ToList(), $"Rename piece {key}",
                brush: b => b.Shape.Builder!.Strings[BuilderArgs.PieceKey] = key);
        }
    }

    /// <summary>Stamps provenance: the author and <c>label#op</c>.</summary>
    public void Stamp(EditorMeta meta)
    {
        meta.Author = Author;
        meta.Op = $"{Label}#{OpIndex}";
    }

    public void SnapshotProject() => ProjectBefore ??= ProjectJson(Project);

    public static SvJsonObject ProjectJson(MapProject p)
    {
        SvJsonObject o = ProjectStore.MapToJson(p);
        o.TryAdd("layers", new SvJsonArray(p.Layers.Select(ProjectStore.LayerToJson)));
        o.TryAdd("workspace", ProjectStore.WorkspaceToJson(p.Workspace));
        if (!p.Outfits.IsEmpty)
        {
            o.TryAdd("outfits", OutfitsJson.ToJson(p.Outfits));
        }
        // Instances keyed by id, so the diff names the ones that changed.
        var prefabs = new SvJsonObject();
        foreach (SvJsonObject i in ProjectStore.PrefabsToJson(p.Prefabs).OfType<SvJsonObject>())
        {
            prefabs.TryAdd(ApiJson.GetString(i, "id") ?? "", i);
        }

        o.TryAdd("prefabs", prefabs);
        return o;
    }

    // ---- ids and symbols ----------------------------------------------------------------------------------------

    /// <summary>The real id for a new object: a fresh one for null or a <c>$symbol</c> (then bound), else the given
    /// id, which must be free.</summary>
    public string NewId(string? requested, string stem, string field)
    {
        if (requested is null || requested.StartsWith('$'))
        {
            string id = Project.NewId(stem);
            if (requested is not null)
            {
                Bind(requested, id, field);
            }

            return id;
        }

        if (!IdPattern().IsMatch(requested))
        {
            throw new ApiException($"'{requested}' is not a valid id (letters, digits and _; start with a letter or _)", field);
        }

        if (MapObjects.Find(Project, requested) is not null)
        {
            throw new ApiException($"id '{requested}' is already used", field);
        }

        return requested;
    }

    /// <summary>Whether a text is a valid object or instance id (letters, digits and _; not starting with a digit).</summary>
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public void Bind(string symbol, string id, string field)
    {
        if (!SymbolPattern().IsMatch(symbol))
        {
            throw new ApiException($"'{symbol}' is not a valid symbol ($ then a letter or _, then letters, digits, _, # or .)", field);
        }

        if (!Symbols.TryAdd(symbol, id))
        {
            throw new ApiException($"symbol {symbol} is already bound to {Symbols[symbol]}", field);
        }
    }

    /// <summary>An existing object by id or symbol, or an error naming the field.</summary>
    public ObjectRef Resolve(string idOrSymbol, string field)
    {
        string id = ResolveId(idOrSymbol, field);
        return MapObjects.Find(Project, id) ?? throw new ApiException($"no object '{idOrSymbol}'" + Suggest(idOrSymbol), field);
    }

    /// <summary>The real id of a symbol, or the id itself.</summary>
    public string ResolveId(string idOrSymbol, string field)
    {
        if (!idOrSymbol.StartsWith('$'))
        {
            return idOrSymbol;
        }

        return Symbols.TryGetValue(idOrSymbol, out string? id)
            ? id
            : throw new ApiException($"symbol {idOrSymbol} is not bound by an earlier op", field);
    }

    public string? TryActorId(string text)
    {
        if (text.StartsWith('$'))
        {
            return Symbols.TryGetValue(text, out string? id) && Project.FindActor(id) is not null ? id : null;
        }

        return Project.FindActor(text)?.Id;
    }

    /// <summary>The targets of an op: <c>id</c>, <c>ids</c> or <c>where</c> (a filter). Explicit ids must exist.</summary>
    public List<ObjectRef> Targets(SvJsonObject op, bool required = true)
    {
        var refs = new List<ObjectRef>();
        if (ApiJson.GetString(op, "id") is { } one)
        {
            refs.Add(Resolve(one, "id"));
        }

        if (ApiJson.StringList(op["ids"], "ids") is { } many)
        {
            for (int i = 0; i < many.Count; i++)
            {
                refs.Add(Resolve(many[i], $"ids[{i}]"));
            }
        }

        if (op["where"] is { } where)
        {
            if (where is not SvJsonObject w)
            {
                throw new ApiException("where is a filter object, e.g. {\"class\":\"SCamera\"}", "where");
            }

            List<ObjectRef> found = ObjectFilter.Parse(w, Point, s => ResolveId(s, "where.ids")).Select(Project).ToList();
            if (found.Count == 0)
            {
                Warn("where", "matched nothing");
            }

            refs.AddRange(found);
        }
        else if (required && refs.Count == 0)
        {
            throw new ApiException("give id, ids or where", "id");
        }

        return refs.Distinct().ToList();
    }

    private string Suggest(string id)
    {
        string? close = Project.AllRefs().Select(r => r.Id)
            .FirstOrDefault(x => x.StartsWith(id.TrimStart('$').TrimEnd("0123456789".ToCharArray()), StringComparison.OrdinalIgnoreCase));
        return close is null ? "" : $" (e.g. {close} exists)";
    }

    // ---- points ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A point: <c>[x,y,z]</c>, <c>"x,y,z"</c>, or a reference <c>"Name[@anchor][+dx,dy,dz]"</c>. The anchor is a
    /// point of the object's bounds: <c>center</c> (default for brushes and GEs), <c>origin</c> (Location, default for
    /// actors), or tokens joined by _ such as <c>top</c>, <c>bottom</c>, <c>xmin</c>, <c>xmax</c>, <c>ymin</c>,
    /// <c>ymax</c> (e.g. <c>xmax_bottom</c>).
    /// </summary>
    public Vec3 Point(SvJsonNode spec, string field)
    {
        if (ApiJson.TryVec(spec, out Vec3 v))
        {
            return v;
        }

        if (spec is not SvJsonString s)
        {
            throw new ApiException($"expected a point ([x,y,z] or \"Name@anchor+dx,dy,dz\"), got {ApiJson.Describe(spec)}", field);
        }

        Match m = RefPattern().Match(s.Value.Replace(" ", "", StringComparison.Ordinal));
        if (!m.Success)
        {
            throw new ApiException($"cannot read point \"{s.Value}\" (use [x,y,z], \"x,y,z\" or \"Name@anchor+dx,dy,dz\")", field);
        }

        ObjectRef r = Resolve(m.Groups["name"].Value, field);
        Vec3 p = AnchorPoint(r, m.Groups["anchor"].Success ? m.Groups["anchor"].Value : null, field);
        if (m.Groups["off"].Success)
        {
            string off = m.Groups["off"].Value;
            if (!ApiJson.TryParseTriple(off.StartsWith('+') ? off[1..] : off, out Vec3 d))
            {
                throw new ApiException($"bad offset '{off}' in \"{s.Value}\" (use +dx,dy,dz)", field);
            }

            p += d;
        }

        return p;
    }

    public Vec3 AnchorPoint(ObjectRef r, string? anchor, string field)
    {
        if (anchor is null)
        {
            return r.Kind == ObjectKind.Actor ? MapObjects.Origin(Project, r) : Center(MapObjects.Bounds(Project, r));
        }

        if (anchor.Equals("origin", StringComparison.OrdinalIgnoreCase))
        {
            return MapObjects.Origin(Project, r);
        }

        return Anchor(MapObjects.Bounds(Project, r), anchor, field);
    }

    /// <summary>A point of a box picked by anchor tokens (center, top, bottom, xmin, xmax, ymin, ymax, zmin, zmax).</summary>
    public static Vec3 Anchor((Vec3 Min, Vec3 Max) b, string anchor, string field)
    {
        Vec3 p = Center(b);
        foreach (string token in anchor.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            p = token switch
            {
                "center" or "c" => p,
                "top" or "zmax" => p with { Z = b.Max.Z },
                "bottom" or "zmin" or "floor" => p with { Z = b.Min.Z },
                "xmin" => p with { X = b.Min.X },
                "xmax" => p with { X = b.Max.X },
                "ymin" => p with { Y = b.Min.Y },
                "ymax" => p with { Y = b.Max.Y },
                _ => throw new ApiException($"unknown anchor '{token}' (center, origin, top, bottom, xmin, xmax, ymin, ymax; join with _)", field),
            };
        }

        return p;
    }

    public static Vec3 Center((Vec3 Min, Vec3 Max) b) => (b.Min + b.Max) / 2;

    // ---- geometry -------------------------------------------------------------------------------------------------

    /// <summary>The level geometry as it is now (rebuilt after each command), optionally without one brush.</summary>
    public WorldGeometry Geometry(string? exclude = null)
    {
        if (exclude is not null)
        {
            return new WorldGeometry(Project, Folder, exclude);
        }

        return _geometry ??= new WorldGeometry(Project, Folder);
    }

    // ---- diff tracking --------------------------------------------------------------------------------------------

    private sealed class Tracked
    {
        public required string CurrentId { get; set; }

        public required ObjectKind Kind { get; init; }

        public SvJsonObject? Before { get; init; }
    }

    /// <summary>Remembers an object's state before its first change in this document.</summary>
    public void Touch(ObjectRef r)
    {
        if (_tracked.ContainsKey(r.Id))
        {
            return;
        }

        var t = new Tracked { CurrentId = r.Id, Kind = r.Kind, Before = ObjectJson(Project, r) };
        _tracked[r.Id] = t;
        _order.Add(t);
    }

    public void Created(ObjectRef r)
    {
        if (_tracked.ContainsKey(r.Id))
        {
            return; // deleted and made again under the same id: the diff shows it as updated
        }

        var t = new Tracked { CurrentId = r.Id, Kind = r.Kind, Before = null };
        _tracked[r.Id] = t;
        _order.Add(t);
    }

    public void Renamed(string oldId, string newId)
    {
        if (_tracked.Remove(oldId, out Tracked? t))
        {
            t.CurrentId = newId;
            _tracked[newId] = t;
        }
    }

    /// <summary>Every touched object: its first id, kind, before-JSON (null when created) and after-JSON (null when
    /// deleted), in the order first touched.</summary>
    public IEnumerable<(string Id, ObjectKind Kind, SvJsonObject? Before, SvJsonObject? After)> Changes() =>
        _order.Select(t => (t.CurrentId, t.Kind, t.Before, ObjectJson(Project, new ObjectRef(t.Kind, t.CurrentId))));

    public static SvJsonObject? ObjectJson(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Actor => p.FindActor(r.Id) is { } a ? ModelJson.ActorToJson(a) : null,
        ObjectKind.Brush => p.FindBrush(r.Id) is { } b ? ModelJson.BrushToJson(b) : null,
        _ => p.FindGe(r.Id) is { } g ? ModelJson.GeToJson(g) : null,
    };

    [GeneratedRegex(@"^(?<name>\$?[A-Za-z_][A-Za-z0-9_#.]*)(?:@(?<anchor>[A-Za-z_]+))?(?<off>[+-].+)?$")]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\$[A-Za-z_][A-Za-z0-9_#.]*$")]
    private static partial Regex SymbolPattern();
}
