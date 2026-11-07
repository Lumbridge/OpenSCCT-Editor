using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Model;

/// <summary>What a reference in the editor points at.</summary>
public enum ObjectKind
{
    Brush,
    Actor,
    Ge,
}

/// <summary>A reference to one placed object by kind and id.</summary>
public readonly record struct ObjectRef(ObjectKind Kind, string Id)
{
    public static ObjectRef Brush(string id) => new(ObjectKind.Brush, id);

    public static ObjectRef Actor(string id) => new(ObjectKind.Actor, id);

    public static ObjectRef Ge(string id) => new(ObjectKind.Ge, id);

    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>A saved camera position (Working View / bookmark).</summary>
public sealed class Bookmark
{
    public string Name { get; set; } = "";

    public Vec3 Location { get; set; }

    public Rot Rotation { get; set; }

    /// <summary>Optional note.</summary>
    public string? Note { get; set; }

    public Bookmark Clone() => (Bookmark)MemberwiseClone();
}

/// <summary>A reference image laid behind a 2D view (floor plans, concept art).</summary>
public sealed class ReferenceImage
{
    /// <summary>
    /// The image's name, unique in the map: how the <c>reference</c> op and the References panel tell images apart, so
    /// one file can be laid more than once (the same plan on two floors).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The image file: absolute, or relative to the project folder.</summary>
    public string Path { get; set; } = "";

    /// <summary>top, front or side.</summary>
    public string View { get; set; } = "top";

    /// <summary>The world point under the image's centre. In the top view its height also says which storey the image
    /// belongs to: with a storey filter on, only the images whose height lies in that storey show.</summary>
    public Vec3 Origin { get; set; }

    public double UnitsPerPixel { get; set; } = 1;

    public double Opacity { get; set; } = 0.5;

    /// <summary>The axis the image's view looks along (top: Z, front: Y, side: X); the image lies across the other two.</summary>
    public int DepthAxis => View switch
    {
        "front" => 1,
        "side" => 0,
        _ => 2,
    };

    public ReferenceImage Clone() => (ReferenceImage)MemberwiseClone();

    /// <summary>
    /// Two-point calibration: <paramref name="from"/> and <paramref name="to"/> are two marks on the image where it is
    /// drawn now, and <paramref name="length"/> is how far apart they really are. The image scales about
    /// <paramref name="from"/> (which stays where it is) until the marks are that far apart across the view. Throws
    /// <see cref="ArgumentException"/> when the marks coincide across the view or the length is not above 0.
    /// </summary>
    public (Vec3 Origin, double UnitsPerPixel) Calibrate(Vec3 from, Vec3 to, double length)
    {
        double measured = (to - from).With(DepthAxis, 0).Length;
        if (measured < 1e-6 || !(length > 0) || !double.IsFinite(length))
        {
            throw new ArgumentException(measured < 1e-6
                ? $"the two marks are at the same place in the {View} view"
                : "the real length must be above 0");
        }

        double scale = length / measured;
        Vec3 origin = (from + (Origin - from) * scale).With(DepthAxis, Origin[DepthAxis]);
        return (origin, UnitsPerPixel * scale);
    }

    /// <summary>A name for an image of <paramref name="path"/> that no image in <paramref name="existing"/> has: the
    /// file's name without its extension, then "name 2", "name 3"...</summary>
    public static string NewName(IEnumerable<ReferenceImage> existing, string path)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
        stem = stem.Length == 0 ? "Image" : stem;
        var taken = existing.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = stem;
        for (int n = 2; taken.Contains(name); n++)
        {
            name = $"{stem} {n}";
        }

        return name;
    }
}

/// <summary>A storey the map maker named: a height range the storey filter shows (Workspace.Storeys).</summary>
public sealed class StoreyRange
{
    public string Name { get; set; } = "";

    public double Bottom { get; set; }

    public double Top { get; set; }

    public StoreyRange Clone() => (StoreyRange)MemberwiseClone();
}

/// <summary>The body and movement numbers Map Check and the blockout pieces use (defaults decoded from the game:
/// spec/movement in OpenSCCT). Change them to design for a different body; <c>Validation.DesignNumbers</c> derives the
/// clearances from them.</summary>
public sealed class MovementLimits
{
    /// <summary>Highest step a player walks up (decoded 34: 32 walked and 36 refused in tests).</summary>
    public double StepHeight { get; set; } = 34;

    /// <summary>Steepest walkable floor (the game walks floors whose normal Z is at least 0.7).</summary>
    public double WalkableSlopeDegrees { get; set; } = 45.57;

    public double StandHeight { get; set; } = 175;

    public double SpyCrouchHeight { get; set; } = 100;

    public double MercCrouchHeight { get; set; } = 136;

    public double Radius { get; set; } = 35;

    public double SpyRunSpeed { get; set; } = 440;

    public double MercRunSpeed { get; set; } = 400;

    public MovementLimits Clone() => (MovementLimits)MemberwiseClone();
}

/// <summary>The default Play options (side, mode, bots).</summary>
public sealed class PlayOptions
{
    public string Side { get; set; } = "spy";

    public string Mode { get; set; } = "Story";

    public int Bots { get; set; }

    /// <summary>playlevel (the editor's Play Level rules) or map (the map's own settings).</summary>
    public string Rules { get; set; } = "playlevel";

    /// <summary>
    /// The player count a playtest lays the map out for (its match-size rules), 1 to 24; 0 (the default) counts the
    /// playtest's own players: the bots and you.
    /// </summary>
    public int Players { get; set; }

    /// <summary>The count a playtest is laid out for: <see cref="Players"/>, or the bots and you.</summary>
    public int MatchPlayers => Players > 0 ? Players : Bots + 1;

    /// <summary>How well the bots play: easy, normal, hard or expert (OpenSCCT's AiSkill presets).</summary>
    public string Difficulty { get; set; } = "normal";

    public PlayOptions Clone() => (PlayOptions)MemberwiseClone();
}

/// <summary>Editor-only workspace state saved with the project (never compiled into the game map).</summary>
public sealed class Workspace
{
    public FirstMapGuide FirstMap { get; set; } = new();
    public List<Bookmark> Bookmarks { get; set; } = new();

    public List<ReferenceImage> References { get; set; } = new();

    /// <summary>The map's own storeys. Empty means the storeys are detected from its floors (Geometry.Storeys).</summary>
    public List<StoreyRange> Storeys { get; set; } = new();

    public MovementLimits Movement { get; set; } = new();

    public PlayOptions Play { get; set; } = new();

    public double GridSize { get; set; } = 16;

    public int RotationGrid { get; set; } = 4551;

    public Vec3 CameraLocation { get; set; } = new(-512, -512, 512);

    public Rot CameraRotation { get; set; } = new(-4096, 8192, 0);

    /// <summary>
    /// The storey the views show, as a height inside it (the storey filter keeps a storey by its height, since an edit
    /// can renumber them), or null for every storey. Saved with the map like the camera.
    /// </summary>
    public double? ShownStorey { get; set; }

    /// <summary>A deep copy (commands swap whole copies in and out, so snapshots taken for background work stay valid).</summary>
    public Workspace Clone() => new()
    {
        FirstMap = FirstMap.Clone(),
        Bookmarks = Bookmarks.Select(b => b.Clone()).ToList(),
        References = References.Select(r => r.Clone()).ToList(),
        Storeys = Storeys.Select(s => s.Clone()).ToList(),
        Movement = Movement.Clone(),
        Play = Play.Clone(),
        GridSize = GridSize,
        RotationGrid = RotationGrid,
        CameraLocation = CameraLocation,
        CameraRotation = CameraRotation,
        ShownStorey = ShownStorey,
    };
}

/// <summary>A geometry layer imported from a compiled map (a converted SVMap or a cooked .sdc), kept as-is: the world
/// BSP render mesh, its collision and zones. It is drawn and compiled through unchanged, and can later be recovered
/// into brushes.</summary>
public sealed class BakedWorld
{
    /// <summary>The SVMap folder the layer came from (absolute or relative to the project).</summary>
    public string SourceMap { get; set; } = "";

    /// <summary>Use the source map's world.glb BSP meshes in the compiled render output.</summary>
    public bool KeepRender { get; set; } = true;

    /// <summary>Use the source map's collision.bin world/brush colliders and zones in the compiled output.</summary>
    public bool KeepCollision { get; set; } = true;

    /// <summary>The ids of the brushes an .sdc editor copy brought in with the layer (marked baked): the layer holds
    /// them, so Map Check can tell one that was deleted while the layer is kept.</summary>
    public IReadOnlyList<string> Brushes { get; set; } = [];

    /// <summary>True when only one of the render and the collision is kept: the other has no world at all, since the
    /// brushes the layer holds are built for neither.</summary>
    public bool OneLayer => KeepRender != KeepCollision;

    public BakedWorld Clone() => (BakedWorld)MemberwiseClone();
}

/// <summary>How the map was made or imported.</summary>
public sealed class ProjectSource
{
    /// <summary>authored, svmap, t3d or sdc.</summary>
    public string Kind { get; set; } = "authored";

    public string? File { get; set; }

    public string? Sha256 { get; set; }

    /// <summary>The import's findings, kept for its report (<c>Interop.ImportReport</c>): <c>[{sev, code, msg,
    /// objects}]</c>, objects as <c>Kind:Id</c>. Null for a map that was not imported, or imported before they were kept.</summary>
    public SvJsonArray? Findings { get; set; }

    public ProjectSource Clone() => (ProjectSource)MemberwiseClone();
}

/// <summary>
/// An editable map project: the source of truth the editor saves (an <c>.osmap</c> folder) and compiles to an SVMap
/// folder for OpenSCCT, or exports to T3D for the original editor.
/// </summary>
public sealed class MapProject
{
    public const string FormatId = "osmap";
    public const int FormatVersion = 1;

    /// <summary>The map code (folder and file name). The original game needs exactly 5 characters (e.g. ShipD).</summary>
    public string Id { get; set; } = "NewMD";

    public string Title { get; set; } = "New map";

    public ProjectSource Source { get; set; } = new();

    /// <summary>The LevelInfo actor's stored properties (Level.Actors[0]).</summary>
    public PropSet LevelInfo { get; set; } = new();

    /// <summary>Per game mode (Story, DiskHunt, Deathmatch, MercDeathmatch, ...): SMapSettings values.</summary>
    public SortedDictionary<string, PropSet> MapSettings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>ui/mapinfo.json strings (MapName, LegendMap, briefing texts...) as stored.</summary>
    public PropSet MapInfo { get; set; } = new();

    /// <summary>What the map says about itself for the menus (ui/identity.json): dossier, authors, places, pictures...</summary>
    public ProjectIdentity Identity { get; set; } = new();

    /// <summary>CSG brushes in CSG order.</summary>
    public List<Brush> Brushes { get; set; } = new();

    /// <summary>Actors in level order.</summary>
    public List<Actor> Actors { get; set; } = new();

    public List<Ge> Ges { get; set; } = new();

    public List<Layer> Layers { get; set; } = new();

    /// <summary>The linked prefab instances placed in the map.</summary>
    public List<PrefabInstance> Prefabs { get; set; } = new();

    public Workspace Workspace { get; set; } = new();

    public BakedWorld? Baked { get; set; }

    /// <summary>How the compiler bakes lightmaps.</summary>
    public LightingSettings Lighting { get; set; } = new();

    /// <summary>
    /// What the teams wear on this map (compiled to SVMap 1.2 <c>render/outfits.json</c>), with texture and model paths
    /// relative to the project folder (<c>assets/outfits/</c>).
    /// </summary>
    public OpenScct.Content.MapOutfits Outfits { get; set; } = OpenScct.Content.MapOutfits.None;

    /// <summary>Anything else an importer kept that the editor does not model yet (copied into compiled output).</summary>
    public SvJsonObject Extra { get; set; } = new();

    /// <summary>What loading brought up to date in old project files, for the log (never saved).</summary>
    public List<string> LoadNotes { get; } = new();

    private readonly IdIndex<Brush> _brushIndex = new(b => b.Id);
    private readonly IdIndex<Actor> _actorIndex = new(a => a.Id);
    private readonly IdIndex<Ge> _geIndex = new(g => g.Id);

    public Brush? FindBrush(string id) => IndexOfBrush(id) is int i and >= 0 ? Brushes[i] : null;

    public Actor? FindActor(string id) => IndexOfActor(id) is int i and >= 0 ? Actors[i] : null;

    public Ge? FindGe(string id) => IndexOfGe(id) is int i and >= 0 ? Ges[i] : null;

    /// <summary>Where the brush with this id is in <see cref="Brushes"/> (the CSG order), or -1. Constant time.</summary>
    public int IndexOfBrush(string id) => _brushIndex.IndexOf(Brushes, id);

    public int IndexOfActor(string id) => _actorIndex.IndexOf(Actors, id);

    public int IndexOfGe(string id) => _geIndex.IndexOf(Ges, id);

    public int IndexOf(ObjectRef r) => r.Kind switch
    {
        ObjectKind.Brush => IndexOfBrush(r.Id),
        ObjectKind.Actor => IndexOfActor(r.Id),
        _ => IndexOfGe(r.Id),
    };

    /// <summary>The object with this id, whatever its kind (actors first, then brushes, then GEs), or null.</summary>
    public ObjectRef? Find(string id)
    {
        // Answer from the indexes first, so looking up a brush id does not have to prove no actor has it.
        if (_actorIndex.Hit(Actors, id) is int a and >= 0)
        {
            return ObjectRef.Actor(Actors[a].Id);
        }

        if (_brushIndex.Hit(Brushes, id) is int b and >= 0)
        {
            return ObjectRef.Brush(Brushes[b].Id);
        }

        if (_geIndex.Hit(Ges, id) is int g and >= 0)
        {
            return ObjectRef.Ge(Ges[g].Id);
        }

        if (FindActor(id) is { } actor)
        {
            return ObjectRef.Actor(actor.Id);
        }

        if (FindBrush(id) is { } brush)
        {
            return ObjectRef.Brush(brush.Id);
        }

        return FindGe(id) is { } ge ? ObjectRef.Ge(ge.Id) : null;
    }

    public bool Exists(ObjectRef r) => IndexOf(r) >= 0;

    public EditorMeta? MetaOf(ObjectRef r) => r.Kind switch
    {
        ObjectKind.Brush => FindBrush(r.Id)?.Editor,
        ObjectKind.Actor => FindActor(r.Id)?.Editor,
        ObjectKind.Ge => FindGe(r.Id)?.Editor,
        _ => null,
    };

    /// <summary>A new id from <paramref name="stem"/> that no object uses (ignoring case): Stem0, Stem1...</summary>
    public string NewId(string stem)
    {
        string baseStem = stem.TrimEnd("0123456789".ToCharArray());
        if (baseStem.Length == 0)
        {
            baseStem = "Object";
        }

        for (int i = 0; ; i++)
        {
            string id = baseStem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Find(id) is null)
            {
                return id;
            }
        }
    }

    public IEnumerable<ObjectRef> AllRefs() =>
        Brushes.Select(b => ObjectRef.Brush(b.Id)).Concat(Actors.Select(a => ObjectRef.Actor(a.Id))).Concat(Ges.Select(g => ObjectRef.Ge(g.Id)));

    /// <summary>A new empty map: one subtractive 1024 cube room, a player start per team, and default settings.</summary>
    public static MapProject CreateDefault(string id, string title)
    {
        var p = new MapProject { Id = id, Title = title };
        p.LevelInfo.Set("GEActivationMask", SvJsonNode.Integer(0));
        p.Layers.Add(new Layer("Default"));
        foreach (string mode in new[] { "Story", "DiskHunt", "Deathmatch", "MercDeathmatch" })
        {
            var s = new PropSet();
            s.Set("TimeLimit", SvJsonNode.Integer(10));
            s.Set("SpiesLives", SvJsonNode.Integer(3));
            s.Set("SentriesLives", SvJsonNode.Integer(3));
            s.Set("RespawnTime", SvJsonNode.Integer(10));
            p.MapSettings[mode] = s;
        }

        p.MapInfo.Set("MapName", SvJsonNode.String(title.ToUpperInvariant()));
        return p;
    }
}
