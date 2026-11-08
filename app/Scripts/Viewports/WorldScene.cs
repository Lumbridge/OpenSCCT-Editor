using Godot;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports.Render;
using ClassDefaults = OpenScct.Editor.Core.Gameplay.ClassDefaults;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The 3D scene every view renders: one node per brush, actor and GE, built in the object's own frame so a move only
/// changes a transform. <see cref="Sync"/> compares the project with what is drawn and rebuilds only the objects that
/// changed (edit commands replace changed objects, so an unchanged object is the same instance). Each part sits on a
/// <see cref="VisualLayers"/> layer; a view's camera mask picks its render mode.
/// </summary>
public sealed partial class WorldScene : Node3D
{
    private readonly Dictionary<ObjectRef, Entry> _entries = new();
    private readonly HashSet<ObjectRef> _selected = new();
    private readonly List<Brush> _drawnBrushes = new();
    private readonly List<Ge> _drawnGeLooks = new();
    private List<Brush> _geBrushes = new();
    private int _geNamesKey;
    private List<Actor> _drawnWindows = [];
    private readonly HashSet<string> _windowPartIds = new(StringComparer.OrdinalIgnoreCase);
    private Node3D _builder = null!;
    private MeshInstance3D? _previewUnlit;
    private MeshInstance3D? _previewLit;
    private MeshInstance3D? _previewBaked;
    private MeshInstance3D? _previewDensity;
    private BakedWorldNode? _baked;
    private ActorIconBatch _icons = null!;

    /// <summary>The actor icons need writing again (something drawn, moved, shown, hidden or selected changed).</summary>
    private bool _iconsDirty;

    /// <summary>The baked world layers kept when it was last loaded: changing them (Map Settings) reloads it and
    /// redoes the CSG preview, which then builds the brushes the baked world held.</summary>
    private (bool Render, bool Collision)? _bakedKeep;
    private IReadOnlyDictionary<string, bool>? _lightStates;
    private bool _showOffTriggerLights;
    private bool _previewDirty;
    private bool _previewRunning;
    private string? _previewError;
    private double _previewWait;
    private MapProject? _project;
    private float _lightTime;
    private OpenScctGame.Render.WindowLightFilter _windowFilter = null!;
    private string? _folder;

    // The Lit Preview's bake: runs after the CSG preview and whenever lights, meshes, the LevelInfo or the lighting
    // settings change.
    private readonly LightPreview _lightPreview = new();
    private IReadOnlyList<PreviewPolygon>? _previewPolys;
    private readonly SkyPreview _skyPreview = new();
    private int _skyRevision;
    private bool _skyDirty;
    private bool _skyRunning;
    private string? _skyError;
    private MeshInstance3D? _skyUnlit;
    private MeshInstance3D? _skyLit;
    private MeshInstance3D? _skyBaked;
    private LightPreviewPass? _lastLightPass;

    public SkyPreviewScene? Sky { get; private set; }
    private CancellationTokenSource? _lightCancel;
    private bool _lightDirty;
    private bool _lightRunning;
    private string? _lightError;
    private int _lightVersion;
    private int? _completedLightVersion;
    private double _lightWait;
    private List<object> _lightInputs = new();

    /// <summary>The last finished bake's light of placed static meshes, by actor id (empty without one).</summary>
    private IReadOnlyDictionary<string, LitPreviewMesh> _meshLight = new Dictionary<string, LitPreviewMesh>();

    /// <summary>Counts CSG previews, so a bake knows whether the bake on show was of the current brushes (null: nothing
    /// baked yet; a map without brushes has a bake but no baked surfaces).</summary>
    private int _geometryVersion;
    private int? _bakedGeometryVersion;

    /// <summary>Faint copies of an actor at other poses (the mover workbench's key ghosts), or null.</summary>
    private Node3D? _placementGhost;
    private Node3D? _ghosts;

    /// <summary>Actors drawn at another pose than the map's (previews), by id.</summary>
    private readonly Dictionary<string, (Vec3 Location, Rot Rotation)> _posed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the CSG preview runs on: copies of the brushes, in the same instance every run, because
    /// WorldPreview keeps its incremental CSG per project instance. A new one per opened map.</summary>
    private MapProject _previewInput = new();

    /// <summary>Brushes hidden (by themselves or their layer) now, and when the shown preview was started: their
    /// surfaces are left out of it.</summary>
    private HashSet<string> _hiddenBrushes = new(StringComparer.Ordinal);
    private HashSet<string> _previewHidden = new(StringComparer.Ordinal);

    /// <summary>Show flags (the View menu): brushes, actors, GEs and an imported map's baked world.</summary>
    public bool ShowBrushes { get; set; } = true;

    public bool ShowActors { get; set; } = true;

    public bool ShowGes { get; set; } = true;

    public bool ShowBaked { get; set; } = true;

    /// <summary>True while some view draws solid surfaces (unlit or lit), so the CSG preview is worth computing.</summary>
    public bool SurfacesNeeded { get; set; }

    /// <summary>True while some view is in Lit Preview, so the light bake is worth running.</summary>
    public bool LitNeeded { get; set; }

    /// <summary>Actual asynchronous preview state, independent of the last status-bar message. A queued preview
    /// is paused when its render mode is not visible. These describe geometry, sky and baked light, not texture IO.</summary>
    public string GeometryPreviewState => _previewDirty ? SurfacesNeeded ? "queued" : "paused"
        : _previewRunning ? "building" : _previewError is not null ? "error" : _previewPolys is null ? "empty" : "ready";

    public string SkyPreviewState => _skyDirty ? SurfacesNeeded ? "queued" : "paused"
        : _skyRunning ? "building" : _skyError is not null ? "error" : "ready";

    public string LightingPreviewState => _previewError is not null && !_previewDirty && !_previewRunning ? "error"
        : _previewDirty || _previewRunning || _lightDirty
        ? LitNeeded ? "queued" : "paused"
        : _previewError is not null || _lightError is not null ? "error"
        : _lightRunning ? "baking" : _lastLightPass is { Final: true } ? "ready" : "empty";

    public string? PreviewError => _previewError ?? _skyError ?? _lightError;
    public int LightingVersion => _lightVersion;
    public int? CompletedLightingVersion => _completedLightVersion;

    /// <summary>Number of drawn objects (status and diagnostics).</summary>
    public int Count => _entries.Count;

    public override void _Ready()
    {
        _windowFilter = new();
        _builder = new Node3D { Name = "BuilderBrush" };
        AddChild(_builder);
        _baked = new BakedWorldNode { Name = "BakedWorld" };
        AddChild(_baked);
        _icons = new ActorIconBatch();
        AddChild(_icons);
    }

    /// <summary>Writes the actor icons again before the next frame (View > Show > Actor Icons changed).</summary>
    public void RefreshIcons() => _iconsDirty = true;

    /// <summary>Drops everything and draws <paramref name="p"/> from scratch (a new map was opened);
    /// <paramref name="folder"/> is the project folder (relative baked-world paths resolve against it).</summary>
    public void Rebuild(MapProject p, string? folder)
    {
        DropObjects();
        DropLightBake();
        _previewPolys = null;
        _previewError = _skyError = null;
        Sky = null;
        _skyRevision++;
        _skyDirty = true;
        _skyUnlit?.QueueFree();
        _skyLit?.QueueFree();
        _skyUnlit = _skyLit = null;
        _folder = folder;
        _baked?.Load(p, folder);
        _bakedKeep = Keep(p);
        _posed.Clear();
        Sync(p);
    }

    /// <summary>Draws every object again with the current colours (Preferences changed them); the baked world and the
    /// Lit Preview bake, which have no editor colours, stay loaded (the bake stays on show until the next one lands).</summary>
    public void Redraw(MapProject p)
    {
        DropObjects();
        Sync(p);
    }

    private static (bool Render, bool Collision)? Keep(MapProject p) => p.Baked is { } b ? (b.KeepRender, b.KeepCollision) : null;

    private void DropObjects()
    {
        foreach (Entry e in _entries.Values)
        {
            e.Root.QueueFree();
        }

        _entries.Clear();
        _selected.Clear();
        _drawnBrushes.Clear();
        _drawnGeLooks.Clear();
        _geBrushes.Clear();
        _iconsDirty = true;
        _previewDirty = true;
        _previewInput = new MapProject();
        _previewUnlit?.QueueFree();
        _previewLit?.QueueFree();
        _previewUnlit = _previewLit = null;
    }

    /// <summary>Brings the drawn scene up to date with the project, touching only what changed.</summary>
    public void Sync(MapProject p)
    {
        _project = p;
        var seen = new HashSet<ObjectRef>();
        // The CSG preview depends on every brush and their order: any replaced, added, removed or reordered brush
        // (edits replace the objects they change) means it is out of date.
        bool brushesChanged = p.Brushes.Count != _drawnBrushes.Count;
        for (int i = 0; i < p.Brushes.Count; i++)
        {
            Brush b = p.Brushes[i];
            brushesChanged |= i >= _drawnBrushes.Count || !ReferenceEquals(_drawnBrushes[i], b);
            var r = ObjectRef.ForBrush(b);
            seen.Add(r);
            SyncObject(r, b, () => BrushGeometry.ShapeKey(b), b.Location, e => BuildBrush(e, b));
        }

        _drawnBrushes.Clear();
        _drawnBrushes.AddRange(p.Brushes);
        // Generated parts remain owned by their GE. They join the same CSG and light previews as ordinary brushes,
        // but never enter the editable project's brush list or acquire independent selection/undo state.
        List<Ge> geLooks = p.Ges.Where(g => g.Appearance is not null && GeGeometry.Supports(g.Kind)).ToList();
        var names = new HashCode();
        foreach (ObjectRef r in p.AllRefs())
        {
            names.Add(r.Id, StringComparer.OrdinalIgnoreCase);
        }

        int namesKey = names.ToHashCode();
        var windows = p.Actors.Where(a => a.Window is not null).ToList();
        if (!geLooks.SequenceEqual(_drawnGeLooks) || namesKey != _geNamesKey || !windows.SequenceEqual(_drawnWindows))
        {
            bool hadLook = _geBrushes.Count > 0;
            _geBrushes = GeGeometry.ExpandValid(p).Brushes.Skip(p.Brushes.Count).ToList();
            _windowPartIds.Clear();
            var usedPartIds = p.AllRefs().Select(r => r.Id).Concat(_geBrushes.Select(b => b.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (Actor window in windows)
            {
                try
                {
                    foreach (var part in OpenScct.Editor.Core.Windows.WindowGeometry.Parts(window))
                    {
                        string stem = part.Id;
                        for (int suffix = 1; !usedPartIds.Add(part.Id); suffix++) part.Id = stem + "_" + suffix;
                        _geBrushes.Add(part);
                        _windowPartIds.Add(part.Id);
                    }
                }
                catch (ArgumentException ex) { Core.EditorContext.Instance.SetStatus(ex.Message); }
            }
            _drawnWindows = windows;
            _drawnGeLooks.Clear();
            _drawnGeLooks.AddRange(geLooks);
            _geNamesKey = namesKey;
            brushesChanged |= hadLook || _geBrushes.Count > 0;
        }

        if (Keep(p) != _bakedKeep)
        {
            _bakedKeep = Keep(p);
            _baked?.Load(p, _folder);
            brushesChanged = true;
        }

        foreach (Actor a in p.Actors)
        {
            var r = ObjectRef.Actor(a.Id);
            seen.Add(r);
            SyncObject(r, a, () => ActorKey(a, p), a.Location, e => BuildActor(e, a, p));
        }

        foreach (Ge g in p.Ges)
        {
            var r = ObjectRef.Ge(g.Id);
            seen.Add(r);
            SyncObject(r, g, () => GeKey(g), Vec3.Zero, e => BuildGe(e, g));
        }

        foreach (ObjectRef gone in _entries.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _iconsDirty |= _entries[gone].Icon != IconUse.None;
            _entries[gone].Root.QueueFree();
            _entries.Remove(gone);
        }

        if (brushesChanged)
        {
            _previewDirty = true;
            _previewWait = 0.35;
            QueueLightBake(0.35);
        }

        List<object> inputs = LightInputs(p);
        if (brushesChanged)
        {
            var windowsLight = new WindowLightTransmission(p.Actors.Where(a => a.Window is not null && !ClassDefaults.Flag(a, "bHidden")));
            _windowFilter.SetPanes(windowsLight.Sheets.Select(s => new OpenScctGame.Render.WindowLightFilter.Pane(s.Actor,
                new Color((float)s.Colour.X, (float)s.Colour.Y, (float)s.Colour.Z), s.Triangles.Select(Space.ToGodot).ToArray())).ToList());
        }
        if (!inputs.SequenceEqual(_lightInputs))
        {
            Render.DynamicWorldLights.Update(p, LightOn);
            _lightInputs = inputs;
            QueueLightBake(0.35);
            _skyRevision++;
            _skyDirty = true;
        }

        if (brushesChanged)
        {
            _skyRevision++;
            _skyDirty = true;
        }

        foreach (string id in _posed.Keys)
        {
            ApplyPose(id);
        }

        ApplyVisibility(p);
    }

    /// <summary>What the light bake reads besides the brushes: the LevelInfo and the lighting settings by value (they are
    /// edited in place), then lights and static mesh actors (their own mesh or their class's) by instance, since edits
    /// replace the actors they change.</summary>
    private static List<object> LightInputs(MapProject p)
    {
        var inputs = new List<object> { p.LevelInfo.ToJson().ToString(), OpenScct.Editor.Core.Project.ProjectStore.LightingToJson(p.Lighting).ToString() };
        inputs.AddRange(p.Actors.Where(a => ClassDefaults.IsA(a, "Engine.Light") || ClassDefaults.IsA(a, "Engine.ZoneInfo") ||
            (a.Props["StaticMesh"] ?? ClassDefaults.Catalog.Default(a.Class, "StaticMesh")) is not null));
        return inputs;
    }

    /// <summary>Draws in the lit view and the Lit Preview the trigger lights that start the round off as switched on,
    /// so they can be seen and placed (View/Show); an event preview's own states still win.</summary>
    public bool ShowOffTriggerLights
    {
        get => _showOffTriggerLights;
        set
        {
            _showOffTriggerLights = value;
            SetLightStates(_lightStates);
        }
    }

    /// <summary>Switches the trigger lights as an event preview has them (null: each at its state when the round
    /// starts) in the lit view and the Lit Preview.</summary>
    public void SetLightStates(IReadOnlyDictionary<string, bool>? states)
    {
        _lightStates = states;
        foreach (Entry e in _entries.Values)
        {
            if (e.Light is { } light && e.Source is Actor a)
            {
                light.Visible = LightOn(a);
            }
        }

        if (_project is { } p)
        {
            Render.DynamicWorldLights.Update(p, LightOn);
        }
    }

    private bool LightOn(Actor a)
    {
        if (!EventPreview.IsTriggerLight(a))
        {
            return true;
        }

        if (_lightStates is not null && _lightStates.TryGetValue(a.Id, out bool on))
        {
            return on;
        }

        return _showOffTriggerLights ? EventPreview.HasLightType(a) : EventPreview.StartsOn(a);
    }

    /// <summary>Marks the security cameras whose wall mount moved (the traversal world their trace reads was built or
    /// changed) so the next <see cref="Sync"/> redraws them; true when any did.</summary>
    public bool MarkMountsChanged(MapProject p)
    {
        bool any = false;
        foreach (Actor a in p.Actors)
        {
            if (Render.StaticMeshDrawing.HasMount(a) && _entries.TryGetValue(ObjectRef.Actor(a.Id), out Entry? e) && ReferenceEquals(e.Source, a) &&
                ActorKey(a, p) != e.Key)
            {
                // Not the actor any more, so the next sync compares keys again and rebuilds it.
                e.Source = e;
                any = true;
            }
        }

        return any;
    }

    /// <summary>Draws an actor at another place and turn without changing the map (the mover workbench's preview)
    /// until <see cref="ClearPose"/>.</summary>
    public void Pose(string actorId, Vec3 location, Rot rotation)
    {
        _posed[actorId] = (location, rotation);
        ApplyPose(actorId);
    }

    /// <summary>Draws faint copies of <paramref name="a"/> at <paramref name="poses"/> (the keys of a selected mover)
    /// until <see cref="ClearGhosts"/> or the next call; the map is not changed.</summary>
    public void ShowGhosts(Actor a, IEnumerable<(Vec3 Location, Rot Rotation)> poses, Color color)
    {
        ClearGhosts();
        _ghosts = new Node3D { Name = "Ghosts" };
        AddChild(_ghosts);
        foreach ((Vec3 location, Rot rotation) in poses)
        {
            var ghost = new Node3D { Transform = new Transform3D(Space.RotationToGodot(rotation) * Space.RotationToGodot(a.Rotation).Inverse(), Space.ToGodot(location)) };
            _ghosts.AddChild(ghost);
            Render.StaticMeshDrawing.AttachGhost(ghost, a, color);
        }
    }

    /// <summary>Draws a faint copy of <paramref name="a"/>, an actor not in the map, where it is (a placing tool's hover
    /// preview), replacing the last one; null clears it. Separate from <see cref="ShowGhosts"/>.</summary>
    public void ShowPlacementGhost(Actor? a, Color color)
    {
        _placementGhost?.QueueFree();
        _placementGhost = null;
        if (a is null)
        {
            return;
        }

        _placementGhost = new Node3D { Name = "PlacementGhost", Position = Space.ToGodot(a.Location) };
        AddChild(_placementGhost);
        Render.StaticMeshDrawing.AttachGhost(_placementGhost, a, color);
    }

    /// <summary>The node an actor is drawn under (at its location), or null when it is not drawn.</summary>
    public Node3D? ActorRoot(string actorId) => _entries.TryGetValue(ObjectRef.Actor(actorId), out Entry? e) ? e.Root : null;

    public void ClearGhosts()
    {
        _ghosts?.QueueFree();
        _ghosts = null;
    }

    public void ClearPose(string actorId)
    {
        if (_posed.Remove(actorId))
        {
            ApplyPose(actorId);
        }
    }

    private void ApplyPose(string id)
    {
        if (!_entries.TryGetValue(ObjectRef.Actor(id), out Entry? e) || e.Source is not Actor a)
        {
            return;
        }

        // The actor's geometry is built turned by its own rotation around its location: undo that turn, apply the pose's.
        e.Root.Transform = _posed.TryGetValue(id, out (Vec3 Location, Rot Rotation) pose)
            ? new Transform3D(Space.RotationToGodot(pose.Rotation) * Space.RotationToGodot(a.Rotation).Inverse(), Space.ToGodot(pose.Location))
            : new Transform3D(Basis.Identity, Space.ToGodot(a.Location));
        _iconsDirty |= e.Icon != IconUse.None;
    }

    /// <summary>Re-applies hidden flags, layers and show flags (cheap; runs after every sync).</summary>
    public void ApplyVisibility(MapProject p)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach ((ObjectRef r, Entry e) in _entries)
        {
            bool show = r.Kind switch
            {
                ObjectKind.Brush or ObjectKind.Stair => ShowBrushes,
                ObjectKind.Actor => ShowActors,
                _ => ShowGes,
            };
            // The entry's source is the live object (Sync runs first), so its meta is at hand without a lookup.
            EditorMeta? meta = e.Source switch { Brush b => b.Editor, Actor a => a.Editor, Ge g => g.Editor, _ => null };
            bool visible = meta is not null && ObjectFilter.IsVisible(p, meta);
            if (e.Root.Visible != (show && visible))
            {
                e.Root.Visible = show && visible;
                _iconsDirty |= e.Icon != IconUse.None;
            }

            if (r.Kind == ObjectKind.Actor && SetGreyed(e, Gameplay.MatchGreying.Greys(p, r.Id)))
            {
                _iconsDirty |= e.Icon != IconUse.None;
            }

            // A brush off the storey shown stays in the CSG and the light bake: the views cut the world as they draw.
            if (r.Kind is (ObjectKind.Brush or ObjectKind.Stair) && (meta is null || !ObjectFilter.IsInWorld(p, meta)))
            {
                hidden.Add(r.Id);
            }
        }

        foreach (Brush stair in p.Brushes.Where(b => b.Stair is not null && hidden.Contains(b.Id)))
        {
            var generated = OpenScct.Editor.Core.Stairs.StairGeometry.Generate(stair);
            hidden.UnionWith(generated.Cuts.Concat(generated.Solids).Select(b => b.Id));
        }
        _hiddenBrushes = hidden;
        foreach (Brush part in _geBrushes)
        {
            if (!(_windowPartIds.Contains(part.Id) ? ShowActors : ShowGes) || !ObjectFilter.IsInWorld(p, part.Editor))
            {
                hidden.Add(part.Id);
            }
        }
        if (!hidden.SetEquals(_previewHidden))
        {
            _previewDirty = true;
        }

        if (_previewUnlit is not null)
        {
            _previewUnlit.Visible = ShowBrushes;
        }

        if (_previewLit is not null)
        {
            _previewLit.Visible = ShowBrushes && _previewBaked is null;
        }

        if (_previewBaked is not null)
        {
            _previewBaked.Visible = ShowBrushes;
        }

        if (_previewDensity is not null)
        {
            _previewDensity.Visible = ShowBrushes;
        }

        if (_baked is not null)
        {
            _baked.Visible = ShowBaked;
        }

        if (_skyUnlit is not null) _skyUnlit.Visible = ShowBrushes;
        if (_skyLit is not null) _skyLit.Visible = ShowBrushes && _skyBaked is null;
        if (_skyBaked is not null) _skyBaked.Visible = ShowBrushes;
    }

    /// <summary>The baked-world surface under a ray (UE units), when the baked world is shown and drawn textured; null
    /// otherwise or when the ray meets nothing within <paramref name="maxDistance"/>.</summary>
    public Tools.BakedSurfacePick? PickBaked(Vec3 origin, Vec3 direction, double maxDistance) =>
        ShowBaked && _baked?.Sections is { } sections
            ? Render.BakedPicking.Pick(sections, origin, direction, maxDistance, StoreyFilter.Current)
            : null;

    /// <summary>True when the baked world is drawn (shown, and its render kept).</summary>
    public bool BakedShown => ShowBaked && _baked?.GetChildCount() > 0;

    /// <summary>Highlights the selected objects (outlines drawn over everything, in every mode).</summary>
    public void ApplySelection(IEnumerable<ObjectRef> selection)
    {
        var now = selection.ToHashSet();
        foreach (ObjectRef r in _selected.Union(now).ToList())
        {
            if (_entries.TryGetValue(r, out Entry? e))
            {
                SetSelected(e, now.Contains(r));
            }
        }

        _selected.Clear();
        _selected.UnionWith(now);
    }

    /// <summary>Draws the builder brush (red), or hides it when <paramref name="shape"/> is null.</summary>
    public void SetBuilder(BrushShape? shape, Vec3 location, Rot rotation)
    {
        foreach (Node c in _builder.GetChildren())
        {
            c.QueueFree();
        }

        if (shape is null)
        {
            return;
        }

        var lines = new LineBuilder();
        var b = new Brush("builder") { Shape = shape, Rotation = rotation };
        foreach (BrushFace f in b.WorldFaces())
        {
            lines.Polyline(f.Vertices.Select(Space.ToGodot).ToList(), SceneStyle.BuilderBrush, closed: true);
        }

        _builder.Position = Space.ToGodot(location);
        AddMesh(_builder, lines.Build(), SceneStyle.Lines, VisualLayers.Common | VisualLayers.Selected);
    }

    public override void _Process(double delta)
    {
        _lightTime += (float)delta;
        if (_project is { } project)
        {
            Render.DynamicWorldLights.Update(project, LightOn, _lightTime);
            foreach (Entry entry in _entries.Values)
                if (entry.Light is { } light && entry.Source is Actor actor)
                    light.LightEnergy = Math.Max(0, (float)ClassDefaults.Number(actor, "LightBrightness", 64) / 64f) *
                        Render.DynamicWorldLights.Modulation(actor, _lightTime);
        }
        if (_iconsDirty)
        {
            WriteIcons();
        }

        ProcessSky();
        ProcessLightBake(delta);
        if (!_previewDirty || _previewRunning || !SurfacesNeeded || _project is null || EditorServices.Preview is not { } preview)
        {
            return;
        }

        _previewWait -= delta;
        if (_previewWait > 0)
        {
            return;
        }

        _previewDirty = false;
        _previewRunning = true;
        _previewError = null;
        Core.EditorContext.Instance.SetStatus("Updating world preview…");
        // The preview reads only the brushes and whether there is a baked world; the brushes are copied because the
        // user keeps editing while it runs.
        MapProject input = _previewInput;
        input.Brushes = _project.Brushes.Concat(_geBrushes).Select(b => b.Clone()).ToList();
        input.Baked = _project.Baked;
        HashSet<string> hidden = _previewHidden = _hiddenBrushes;
        BackgroundJobs.Start(_ =>
        {
            IReadOnlyList<PreviewPolygon>? polys = null;
            string? error = null;
            try
            {
                polys = preview.Build(input).Where(poly => !hidden.Contains(poly.BrushId)).ToList();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            BackgroundJobs.Post(() => ApplyPreview(polys, error));
        });
    }

    private void ApplyPreview(IReadOnlyList<PreviewPolygon>? polys, string? error)
    {
        _previewRunning = false;
        _previewError = error;
        _previewUnlit?.QueueFree();
        _previewLit?.QueueFree();
        _previewUnlit = _previewLit = null;
        if (polys is null)
        {
            Core.EditorContext.Instance.Log($"World preview failed: {error}", Severity.Warning);
            Core.EditorContext.Instance.SetStatus("World preview failed — see Log");
            return;
        }

        // The old bake stays on show until the new bake's first (coarse, shadowed) pass replaces it, so the view does
        // not drop to a different lighting model for a moment on every brush edit. A map without brushes (an imported
        // baked world) still bakes, for its static meshes.
        _previewPolys = polys;
        QueueLightBake(0);
        _geometryVersion++;

        DrawPreview();
        if (!LitNeeded && !_previewDirty) Core.EditorContext.Instance.SetStatus("World preview ready");
    }

    private void DrawPreview()
    {
        _previewUnlit?.QueueFree();
        _previewLit?.QueueFree();
        _skyUnlit?.QueueFree();
        _skyLit?.QueueFree();
        _previewUnlit = _previewLit = _skyUnlit = _skyLit = null;
        if (_previewPolys is null) return;

        // Textured like a build: each material's texture and the compiler's UVs (Render/TexturedSurfaces).
        var surfaces = new Render.TexturedSurfaces();
        surfaces.Add(_previewPolys.Where(p => Sky?.ContainsPolygon(p) != true));
        if (surfaces.Build() is { } built)
        {
            _previewUnlit = Render.TexturedSurfaces.Instance(built.Mesh, built.Materials, lit: false, VisualLayers.Unlit);
            _previewLit = Render.TexturedSurfaces.Instance(built.Mesh, built.Materials, lit: true, VisualLayers.Lit);
            AddChild(_previewUnlit);
            AddChild(_previewLit);
        }

        var sky = new Render.TexturedSurfaces();
        sky.Add(_previewPolys.Where(p => Sky?.ContainsPolygon(p) == true));
        if (sky.Build() is { } skyMesh)
        {
            _skyUnlit = Render.TexturedSurfaces.Instance(skyMesh.Mesh, skyMesh.Materials, lit: false, VisualLayers.SkyUnlit);
            _skyLit = Render.TexturedSurfaces.Instance(skyMesh.Mesh, skyMesh.Materials, lit: true, VisualLayers.SkyLit);
            AddChild(_skyUnlit);
            AddChild(_skyLit);
        }
        if (_project is not null)
        {
            ApplyVisibility(_project);
        }
    }

    private void ProcessSky()
    {
        if (!_skyDirty || _skyRunning || !SurfacesNeeded || _project is null) return;
        _skyDirty = false;
        _skyRunning = true;
        _skyError = null;
        int revision = _skyRevision;
        MapProject snapshot = OpenScct.Editor.Core.Session.ProjectSnapshot.Take(_project);
        string? folder = _folder;
        BackgroundJobs.Start(_ =>
        {
            SkyPreviewScene? sky = null;
            string? error = null;
            try { sky = _skyPreview.Build(snapshot, folder); }
            catch (Exception ex) { error = ex.Message; }
            BackgroundJobs.Post(() =>
            {
                _skyRunning = false;
                if (revision != _skyRevision) return;
                _skyError = error;
                if (error is not null) Core.EditorContext.Instance.Log($"Sky preview failed: {error}", Severity.Warning);
                Sky = sky;
                foreach (Entry e in _entries.Values)
                {
                    if (e.Source is Actor a) Render.StaticMeshDrawing.SetSky(e.Root, sky?.Contains(a.Location) == true);
                }

                DrawPreview();
                if (_lastLightPass is { } pass) DrawLightPass(pass);
                if (_project is not null) ApplyVisibility(_project);
            });
        });
    }

    // ---- Lit Preview bake ---------------------------------------------------------------------------------------------

    private void QueueLightBake(double wait)
    {
        // A pass from the previous inputs must not report ready while the next bake is debouncing.
        _lightCancel?.Cancel();
        _lightRunning = false;
        _lightError = null;
        _lightDirty = true;
        _lightWait = wait;
        if (LitNeeded) Core.EditorContext.Instance.SetStatus("Lit preview queued…");
    }

    /// <summary>Starts a bake of the current surfaces once edits settle; a new one cancels the one running. Each pass
    /// replaces the view's surfaces as it lands, coarse first.</summary>
    private void ProcessLightBake(double delta)
    {
        if (!_lightDirty || !LitNeeded || _project is null || _previewPolys is null || _previewDirty || _previewRunning || _previewError is not null)
        {
            return;
        }

        _lightWait -= delta;
        if (_lightWait > 0)
        {
            return;
        }

        _lightDirty = false;
        _lightRunning = true;
        _lightVersion++;
        Core.EditorContext.Instance.SetStatus("Baking lit preview…");
        _lightCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _lightCancel = cancel;
        MapProject snapshot = OpenScct.Editor.Core.Session.ProjectSnapshot.Take(_project);
        IReadOnlySet<string> hidden = _previewHidden;
        int geometry = _geometryVersion;
        LightPreviewStart start = _bakedGeometryVersion is null ? LightPreviewStart.Cold
            : _bakedGeometryVersion == geometry ? LightPreviewStart.Lights
            : LightPreviewStart.Geometry;
        string? folder = _folder;
        BackgroundJobs.Start(quit =>
        {
            try
            {
                // A newer bake or the editor quitting stops this one.
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, quit);
                foreach (LightPreviewPass pass in _lightPreview.Passes(snapshot, folder, hidden, start, stop.Token))
                {
                    BackgroundJobs.Post(() => ApplyLightPass(pass, cancel, geometry));
                }
            }
            catch (OperationCanceledException)
            {
                // A newer bake took over, or the editor is closing.
            }
            catch (Exception ex)
            {
                BackgroundJobs.Post(() =>
                {
                    if (cancel != _lightCancel || cancel.IsCancellationRequested) return;
                    _lightRunning = false;
                    _lightError = ex.Message;
                    Core.EditorContext.Instance.Log($"Lit preview bake failed: {ex.Message}", Severity.Warning);
                    Core.EditorContext.Instance.SetStatus("Lit preview failed — see Log");
                });
            }
        });
    }

    private void ApplyLightPass(LightPreviewPass pass, CancellationTokenSource from, int geometry)
    {
        if (from != _lightCancel || from.IsCancellationRequested)
        {
            return;
        }

        _lastLightPass = pass;
        DrawLightPass(pass);
        _bakedGeometryVersion = geometry;

        if (pass.Final)
        {
            _lightRunning = false;
            _completedLightVersion = _lightVersion;
            _previewDensity?.QueueFree();
            _previewDensity = Render.LightmapDensity.Instance(pass, VisualLayers.Density);
            if (_previewDensity is not null)
            {
                AddChild(_previewDensity);
            }

            ShowMeshLight(pass.Meshes);
            // A short line for the status bar; the bake's figures go to the log.
            Core.EditorContext.Instance.Log(
                $"Lit preview baked: {pass.Stats.Charts} charts, {pass.Stats.Texels:N0} texels, {pass.Stats.Rays:N0} rays, {pass.Stats.Milliseconds} ms; " +
                $"{pass.MeshStats.Meshes} static meshes lit ({pass.MeshStats.Baked} baked here) in {pass.MeshStats.Milliseconds} ms");
            Core.EditorContext.Instance.SetStatus($"Lit preview ready · {pass.Stats.Milliseconds + pass.MeshStats.Milliseconds} ms");
        }

        if (_project is not null)
        {
            ApplyVisibility(_project);
        }
    }

    private void DrawLightPass(LightPreviewPass pass)
    {
        _previewBaked?.QueueFree();
        _skyBaked?.QueueFree();
        LightPreviewPass Filter(bool sky) => pass with
        {
            Lit = pass.Lit.Where(p => (Sky?.ContainsPolygon(p.Source) == true) == sky).ToList(),
            Unlit = pass.Unlit.Where(p => (Sky?.ContainsPolygon(p) == true) == sky).ToList(),
        };
        _previewBaked = Render.LitPreviewSurfaces.Instance(Filter(false), VisualLayers.Lit);
        _skyBaked = Sky is null ? null : Render.LitPreviewSurfaces.Instance(Filter(true), VisualLayers.SkyLit);
        if (_previewBaked is not null) AddChild(_previewBaked);
        if (_skyBaked is not null) AddChild(_skyBaked);
    }

    /// <summary>Cancels the running bake and drops its surfaces.</summary>
    private void DropLightBake()
    {
        _lightCancel?.Cancel();
        _lightCancel = null;
        _lightRunning = false;
        _lightError = null;
        _completedLightVersion = null;
        _previewBaked?.QueueFree();
        _skyBaked?.QueueFree();
        _previewDensity?.QueueFree();
        _previewBaked = _previewDensity = null;
        _skyBaked = null;
        _lastLightPass = null;
        _bakedGeometryVersion = null;
        ShowMeshLight(new Dictionary<string, LitPreviewMesh>());
    }

    /// <summary>Gives every drawn static mesh its light from the bake (or the view's lights when the bake has none).</summary>
    private void ShowMeshLight(IReadOnlyDictionary<string, LitPreviewMesh> light)
    {
        _meshLight = light;
        foreach (Entry e in _entries.Values)
        {
            if (e.Source is Actor a)
            {
                Render.StaticMeshDrawing.ShowBakedLight(e.Root, a, light.GetValueOrDefault(a.Id));
            }
        }
    }

    /// <summary>Brings one object's node up to date: a moved object only moves its node; a reshaped one is rebuilt.</summary>
    private void SyncObject(ObjectRef r, object source, Func<int> key, Vec3 location, Action<Entry> build)
    {
        if (_entries.TryGetValue(r, out Entry? e))
        {
            if (ReferenceEquals(e.Source, source))
            {
                return;
            }

            e.Source = source;
            int k = key();
            if (k == e.Key)
            {
                e.Root.Position = Space.ToGodot(location);
                _iconsDirty |= e.Icon != IconUse.None;
                return;
            }

            e.Key = k;
            _iconsDirty |= e.Icon != IconUse.None;
            foreach (Node c in e.Root.GetChildren())
            {
                e.Root.RemoveChild(c);
                c.QueueFree();
            }

            e.Wire = null;
            e.Light = null;
            e.Icon = IconUse.None;
            e.IconArrow = null;
        }
        else
        {
            e = new Entry(source, new Node3D { Name = r.Kind + "_" + r.Id }) { Key = key() };
            AddChild(e.Root);
            _entries[r] = e;
        }

        e.Root.Position = Space.ToGodot(location);
        build(e);
        _iconsDirty |= e.Icon != IconUse.None;
        SetSelected(e, _selected.Contains(r));
    }

    private void BuildBrush(Entry e, Brush b)
    {
        Color color = SceneStyle.BrushColor(b);
        var lines = new LineBuilder();
        var surfaces = new SurfaceBuilder();
        bool inward = b.Oper == CsgOper.Subtract;
        foreach (WorldFace f in BrushGeometry.Of(b).Faces)
        {
            List<Vector3> pts = f.Vertices.Select(v => Space.ToGodot(v - b.Location)).ToList();
            if (b.Stair is null) lines.Polyline(pts, color, closed: true);
            if ((f.Flags & PolyFlags.Invisible) == 0 && pts.Count >= 3)
            {
                Vector3 n = Space.DirToGodot(f.Normal);
                surfaces.Polygon(pts, inward ? -n : n, SceneStyle.MaterialColor(f.Material));
            }
        }

        if (b.Stair is not null)
        {
            var bounds = b.Shape.Bounds();
            lines.Box(ObjectGeometry.Corners(bounds.Min, bounds.Max).Select(b.LocalToWorld).Select(v => Space.ToGodot(v - b.Location)).ToArray(), color);
        }
        e.Wire = AddMesh(e.Root, lines.Build(), SceneStyle.Lines, VisualLayers.BrushWire);
        ArrayMesh? solid = surfaces.Build();
        if (solid is not null)
        {
            AddMesh(e.Root, solid, SceneStyle.Volume(color), VisualLayers.CsgVolumes);
            if (EditorServices.Preview is null && !b.IsPortal)
            {
                // Without a CSG preview, brushes stand in for the world: additive ones show their outside,
                // subtractive ones their inside, which reads like the carved rooms.
                bool twoSided = b.IsNotSolid || b.IsPortal;
                AddMesh(e.Root, solid, SceneStyle.Surface(lit: false, twoSided), VisualLayers.Unlit);
                AddMesh(e.Root, solid, SceneStyle.Surface(lit: true, twoSided), VisualLayers.Lit);
            }
        }
    }

    private static void BuildActorLines(LineBuilder lines, Actor a, ActorLook look)
    {
        if (a.Shape is { Faces.Count: > 0 } shape)
        {
            foreach (BrushFace f in shape.Faces)
            {
                lines.Polyline(f.Vertices.Select(v => Space.ToGodot(a.LocalToWorld(v) - a.Location)).ToList(), look.Color, closed: true);
            }
        }
        else if (look.Diamond)
        {
            Vec3 h = look.HalfExtent;
            Vector3[] tips =
            [
                Space.ToGodot(new Vec3(h.X, 0, 0)), Space.ToGodot(new Vec3(-h.X, 0, 0)),
                Space.ToGodot(new Vec3(0, h.Y, 0)), Space.ToGodot(new Vec3(0, -h.Y, 0)),
                Space.ToGodot(new Vec3(0, 0, h.Z)), Space.ToGodot(new Vec3(0, 0, -h.Z)),
            ];
            for (int i = 0; i < 4; i++)
            {
                lines.Line(tips[i], tips[4], look.Color);
                lines.Line(tips[i], tips[5], look.Color);
            }

            lines.Polyline([tips[0], tips[2], tips[1], tips[3]], look.Color, closed: true);
        }
        else
        {
            Vec3 h = ObjectGeometry.ActorHalfExtent(a, look);
            lines.Box(ObjectGeometry.Corners(-h, h).Select(Space.ToGodot).ToList(), look.Color);
        }

        // Direction arrow along the actor's facing (UE forward = rotation X axis).
        BuildArrow(lines, a, look.Color, Math.Max(look.HalfExtent.X * 2, 48));
    }

    /// <summary>An arrow of <paramref name="len"/> units from the actor's location along its facing.</summary>
    private static void BuildArrow(LineBuilder lines, Actor a, Color color, double len)
    {
        (Vec3 fwd, Vec3 right, Vec3 _) = a.Rotation.Axes();
        Vec3 tip = fwd * len;
        double head = Math.Min(16, len * 0.3);
        lines.Line(Vector3.Zero, Space.ToGodot(tip), color);
        lines.Line(Space.ToGodot(tip), Space.ToGodot(tip - fwd * head + right * head * 0.6), color);
        lines.Line(Space.ToGodot(tip), Space.ToGodot(tip - fwd * head - right * head * 0.6), color);
    }

    private void BuildActor(Entry e, Actor a, MapProject p)
    {
        ActorLook look = ActorLook.For(a);
        var lines = new LineBuilder();
        BuildActorLines(lines, a, look);
        // An actor drawn as an icon in the perspective view keeps its box in the 2D views (and when selected).
        IconUse use = ActorIconBatch.Use(a);
        e.Wire = AddMesh(e.Root, lines.Build(), SceneStyle.Lines, use == IconUse.Always ? VisualLayers.ActorBoxes : VisualLayers.Common);
        SetIcon(e, a, look, use);
        if (e.Wire is not null)
        {
            // A device whose mesh turns out to ship nowhere (not even in a baked map's source map or the gameplay pack)
            // is drawn without one, so it shows its icon instead of a bare box.
            MeshInstance3D wire = e.Wire;
            Render.StaticMeshDrawing.Attach(e.Root, a, wire, look.Color, () => _meshLight.GetValueOrDefault(a.Id), () =>
            {
                if (ReferenceEquals(e.Wire, wire) && e.Icon != IconUse.Always && ActorIconBatch.Use(a) == IconUse.Always)
                {
                    wire.Layers = VisualLayers.ActorBoxes;
                    e.Wire = wire;
                    SetIcon(e, a, look, IconUse.Always);
                    SetSelected(e, e.Selected);
                    _iconsDirty = true;
                }
            }, () => e.Source is Actor current && Sky?.Contains(current.Location) == true);
        }

        if (MoverBase.IsMover(a) && a.Shape is not null && PropValues.AssetPath(a.Props["StaticMesh"]) is null)
        {
            var brush = new Brush(a.Id) { Shape = a.Shape, Oper = CsgOper.Add, Rotation = a.Rotation,
                PrePivot = a.PrePivot, MainScale = a.DrawScale3D * a.DrawScale };
            var textured = new TexturedSurfaces();
            textured.Add(CsgBrush.Prepare(brush, 0).Faces.Select(WorldPreview.ToPreview));
            if (textured.Build() is { } built)
            {
                e.Root.AddChild(TexturedSurfaces.Instance(built.Mesh, built.Materials, false, VisualLayers.Unlit));
                e.Root.AddChild(TexturedSurfaces.Instance(built.Mesh, built.Materials, true, VisualLayers.Lit));
            }
        }

        if (ConveyorDesign.Is(a))
        {
            ConveyorLook.Attach(e.Root, a, p);
        }

        if (look.Diamond && a.ShortClass.Contains("Light", StringComparison.OrdinalIgnoreCase))
        {
            // A trigger light shines as the round starts it, or as the event preview has switched it.
            e.Light = PreviewLight(a);
            e.Light.Visible = LightOn(a);
            e.Root.AddChild(e.Light);
        }
    }

    /// <summary>Gives <paramref name="e"/> the icon of <paramref name="use"/> (its atlas cell and colour, and for an actor
    /// always drawn as an icon its direction arrow).</summary>
    private static void SetIcon(Entry e, Actor a, ActorLook look, IconUse use)
    {
        if (use == IconUse.None)
        {
            return;
        }

        ActorKind kind = ActorIcons.KindOf(a);
        e.Icon = use;
        e.IconCell = ActorIcons.Cell(kind);
        e.IconTint = look.Color;
        if (use == IconUse.Always && e.IconArrow is null && ActorKinds.ShowsDirection(kind, a, ClassDefaults.Catalog))
        {
            // Longer than the icon is wide in the world, so it reaches out from under it. Selected, the actor's box
            // and its own arrow show instead.
            var arrow = new LineBuilder();
            BuildArrow(arrow, a, look.Color, Math.Max(look.HalfExtent.X * 2, ActorIconBatch.WorldSize * 1.25));
            e.IconArrow = AddMesh(e.Root, arrow.Build(), SceneStyle.Lines, VisualLayers.ActorIcons);
        }
    }

    /// <summary>A light for the lit view, from the UE2 light properties (stored, else the class defaults, as the
    /// Light Radius overlay reads them): radius 25 * (LightRadius + 1) units, colour from hue/saturation as UE2 makes it, energy from
    /// brightness.</summary>
    private static OmniLight3D PreviewLight(Actor a)
    {
        float radius = (float)ClassDefaults.Number(a, "LightRadius", 64);
        float brightness = (float)ClassDefaults.Number(a, "LightBrightness", 64);
        // UE2's hue triangle and inverted saturation (FGetHSV), as the bake and the inspector's colour swatch read them.
        Vec3 colour = LightColour.Chroma((int)ClassDefaults.Number(a, "LightHue", 0), (int)ClassDefaults.Number(a, "LightSaturation", 255));
        return new OmniLight3D
        {
            OmniRange = (float)(25 * (radius + 1) / Space.UnitsPerMetre),
            LightColor = new Color((float)colour.X, (float)colour.Y, (float)colour.Z),
            LightEnergy = Math.Max(0.05f, brightness / 64f),
            OmniAttenuation = 1,
            LightCullMask = VisualLayers.Lit,
            ShadowEnabled = false,
        };
    }

    private void BuildGe(Entry e, Ge g)
    {
        Color c = GeColor(g.Kind);
        var lines = new LineBuilder();
        List<Vector3> pts = g.Points.Select(Space.ToGodot).ToList();
        if (pts.Count >= 2)
        {
            lines.Line(pts[0], pts[1], c);
        }

        if (pts.Count >= 3)
        {
            // The third point (a ladder's or pipe's top/depth) hangs off the line.
            lines.Line(pts[1], pts[2], c.Darkened(0.35f));
        }

        float tick = 0.06f;
        foreach (Vector3 p in pts)
        {
            lines.Line(p - new Vector3(tick, 0, 0), p + new Vector3(tick, 0, 0), c);
            lines.Line(p - new Vector3(0, tick, 0), p + new Vector3(0, tick, 0), c);
            lines.Line(p - new Vector3(0, 0, tick), p + new Vector3(0, 0, tick), c);
        }

        foreach (WorldBrush part in GeLookGeometry.Of(g))
        {
            foreach (WorldFace face in part.Faces.Where(f => (f.Flags & PolyFlags.Invisible) == 0))
            {
                lines.Polyline(face.Vertices.Select(Space.ToGodot).ToList(), c, closed: true);
            }
        }

        e.Wire = AddMesh(e.Root, lines.Build(), SceneStyle.Lines, VisualLayers.Common);
    }

    public static Color GeColor(string kind) => kind.ToUpperInvariant() switch
    {
        GeKinds.LedgeGrab => new Color(0.3f, 1f, 0.5f),
        GeKinds.ZipLine => new Color(1f, 0.85f, 0.2f),
        GeKinds.Pipe => new Color(0.5f, 0.8f, 1f),
        GeKinds.Ladder => new Color(0.95f, 0.6f, 0.25f),
        GeKinds.HandOverHand => new Color(0.95f, 0.45f, 0.75f),
        GeKinds.Pole => new Color(0.7f, 0.6f, 1f),
        GeKinds.Fence => new Color(0.6f, 0.95f, 0.9f),
        _ => new Color(1, 1, 1),
    };

    /// <summary>Draws an object faded (the Match panel's actors left out of a match) or as before; true when that
    /// changed.</summary>
    private static bool SetGreyed(Entry e, bool greyed)
    {
        if (e.Greyed == greyed)
        {
            return false;
        }

        e.Greyed = greyed;
        if (!e.GreyHooked)
        {
            // Meshes that load later (static meshes) and rebuilt parts join the fade as they arrive.
            e.GreyHooked = true;
            e.Root.ChildEnteredTree += child =>
            {
                if (e.Greyed)
                {
                    Fade(child, true);
                }
            };
        }

        Fade(e.Root, greyed);
        KeepSelectionLit(e);
        return true;
    }

    // The selection outline of a greyed actor stays at full strength, so a selected greyed actor still shows as selected.
    private static void KeepSelectionLit(Entry e)
    {
        if (e.Wire is not null)
        {
            Fade(e.Wire, e.Greyed && !e.Selected);
        }
    }

    // A faded part keeps a quarter of its opacity; its own transparency (a ghost's) is kept to restore.
    private static void Fade(Node node, bool faded)
    {
        const string own = "own_transparency";
        if (node is GeometryInstance3D g)
        {
            if (faded && !g.HasMeta(own))
            {
                g.SetMeta(own, g.Transparency);
                g.Transparency = 1 - (1 - g.Transparency) * 0.25f;
            }
            else if (!faded && g.HasMeta(own))
            {
                g.Transparency = g.GetMeta(own).AsSingle();
                g.RemoveMeta(own);
            }
        }

        foreach (Node child in node.GetChildren())
        {
            Fade(child, faded);
        }
    }

    private void SetSelected(Entry e, bool selected)
    {
        if (e.Selected != selected && e.Icon != IconUse.None)
        {
            _iconsDirty = true;
        }

        e.Selected = selected;
        if (e.IconArrow is not null)
        {
            e.IconArrow.Visible = !selected;
        }

        if (e.Wire is null)
        {
            return;
        }

        e.Wire.MaterialOverride = selected ? SceneStyle.SelectedLines : null;
        e.Wire.Layers = selected ? e.WireLayers | VisualLayers.Selected : e.WireLayers;
        KeepSelectionLit(e);
    }

    /// <summary>Writes every shown actor icon into the batch: the actors drawn as icons, and selected mesh actors (a
    /// smaller icon). A selected icon takes the selection colour, a greyed one (left out of the match) is faint.</summary>
    private void WriteIcons()
    {
        _iconsDirty = false;
        _icons.Begin();
        if (ActorIconBatch.Shown && ShowActors)
        {
            foreach (Entry e in _entries.Values)
            {
                if (e.Icon == IconUse.None || !e.Root.Visible || (e.Icon == IconUse.WhenSelected && !e.Selected))
                {
                    continue;
                }

                Color tint = e.Selected ? SceneStyle.Selection : e.Greyed ? e.IconTint with { A = ActorIconBatch.FaintAlpha } : e.IconTint;
                float scale = e.Icon == IconUse.WhenSelected ? ActorIconBatch.SelectedMeshScale : 1;
                _icons.Add(e.Root.Position, tint, e.IconCell, scale, e.Selected);
            }
        }

        _icons.Commit();
    }

    /// <summary>Adds an instance of <paramref name="mesh"/> with its own material (one mesh can back several instances,
    /// e.g. the CSG volume and the unlit and lit surfaces of a brush), so the material goes on the instance.</summary>
    internal static MeshInstance3D? AddMesh(Node3D parent, ArrayMesh? mesh, Material material, uint layers)
    {
        if (mesh is null)
        {
            return null;
        }

        var mi = new MeshInstance3D
        {
            Mesh = mesh,
            Layers = layers,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        mi.SetSurfaceOverrideMaterial(0, material);
        parent.AddChild(mi);
        return mi;
    }

    private static int ActorKey(Actor a, MapProject p)
    {
        var h = new HashCode();
        h.Add(a.Class.ToUpperInvariant());
        h.Add(a.Rotation);
        h.Add(a.DrawScale);
        h.Add(a.DrawScale3D);
        h.Add(a.PrePivot);
        h.Add(a.Props.GetInt("TeamNumber"));
        h.Add(a.Props.GetFloat("CollisionRadius"));
        h.Add(a.Props.GetFloat("CollisionHeight"));
        h.Add(a.Props.GetFloat("LightRadius"));
        h.Add(a.Props.GetFloat("LightBrightness"));
        h.Add(a.Props.GetInt("LightHue"));
        h.Add(a.Props.GetInt("LightSaturation"));
        h.Add(Render.StaticMeshDrawing.Key(a));
        // The icon and its arrow (a light's LightEffect makes it a spot or sun light, and gives it a facing).
        ActorKind kind = ActorIcons.KindOf(a);
        h.Add(kind);
        h.Add(ActorKinds.ShowsDirection(kind, a, ClassDefaults.Catalog));
        if (ConveyorDesign.Is(a))
        {
            h.Add(ConveyorLook.Key(a, p));
        }

        if (a.Shape is not null)
        {
            BrushGeometry.AddShape(ref h, a.Shape);
        }

        return h.ToHashCode();
    }

    private static int GeKey(Ge g) => GeLookGeometry.Key(g);

    private sealed class Entry(object source, Node3D root)
    {
        private MeshInstance3D? _wire;

        public object Source { get; set; } = source;

        public Node3D Root { get; } = root;

        public int Key { get; set; }

        /// <summary>The outline the selection highlights (brush edges, actor and GE lines); it remembers its own
        /// layers so selecting can add and remove the Selected layer.</summary>
        public MeshInstance3D? Wire
        {
            get => _wire;
            set
            {
                _wire = value;
                WireLayers = value?.Layers ?? 0;
            }
        }

        public uint WireLayers { get; private set; }

        public bool Selected { get; set; }

        /// <summary>The light the lit view draws for a light actor.</summary>
        public OmniLight3D? Light { get; set; }

        /// <summary>When the actor shows an icon in the perspective view, which (its atlas cell) and in what colour.</summary>
        public IconUse Icon { get; set; }

        /// <summary>The direction arrow drawn with the icon (hidden while selected, when the box's own arrow shows).</summary>
        public MeshInstance3D? IconArrow { get; set; }

        public Vector2I IconCell { get; set; }

        public Color IconTint { get; set; }

        public bool Greyed { get; set; }

        public bool GreyHooked { get; set; }
    }
}
