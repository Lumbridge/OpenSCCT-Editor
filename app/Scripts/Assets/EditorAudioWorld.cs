extern alias GameRuntime;
using System.Text.Json.Nodes;
using OpenScct.Core;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Audio;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using GameRuntime::OpenScctGame.Audio;
using GameRuntime::OpenScctGame.Render.Data;
using Actor = OpenScct.Editor.Core.Model.Actor;

namespace OpenScctEditor.Assets;

/// <summary>The editable map supplied to the game's unmodified sound engine, in Unreal units.</summary>
public sealed class EditorAudioWorld : IAudioWorld
{
    private readonly EditorContext _context;
    private readonly Func<bool> _solo;
    private readonly WorldGeometry _geometry;
    private readonly Dictionary<int, Actor> _actors = [];
    private readonly Dictionary<int, string> _zones = [];
    private readonly string? _bakedFolder;
    public AcousticScene? Acoustics { get; }
    public WorldGeometry Geometry => _geometry;

    public EditorAudioWorld(EditorContext context, Func<bool> solo)
    {
        _context = context;
        _solo = solo;
        _geometry = new WorldGeometry(context.Project, context.Session.Folder);
        int id = -1;
        foreach (Actor actor in context.Project.Actors) _actors[id--] = actor;
        if (context.Project.Baked is { } baked)
        {
            _bakedFolder = Path.GetFullPath(baked.SourceMap, context.Session.Folder ?? Environment.CurrentDirectory);
            string lighting = Path.Combine(_bakedFolder, "render", "lighting.json");
            if (File.Exists(lighting))
            {
                var doc = LightingDoc.Load(lighting);
                Acoustics = MapAcoustics.FromLighting(doc);
                foreach (var zone in doc.Zones) _zones[zone.ZoneIndex] = zone.Name;
            }
        }
    }

    public static FVector Vector(Vec3 value) => new((float)value.X, (float)value.Y, (float)value.Z);
    public static Vec3 Vector(FVector value) => new(value.X, value.Y, value.Z);
    public string? ActorName(int id) => _actors.GetValueOrDefault(id)?.Id;

    public AudioLibrary LoadLibrary()
    {
        // Keep definitions intact (random/switch/sequence, flags and loop points).
        // Absolute file references let the game load project and content-pack audio
        // directly; this transient table never copies the sound payloads.
        var source = new SoundLibrary(AssetService.Instance.Catalog, EditorServices.ContentRoot, AssetService.ProjectFolder);
        var sounds = new JsonObject();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (!seen.Add(path) || source.Resolve(path) is not { } sound) return;
            var definition = sound.Definition.DeepClone().AsObject();
            if (sound.File is { } file) definition["file"] = Path.GetFullPath(file);
            sounds[sound.Path] = definition;
            if (definition["entries"] is JsonArray entries)
                foreach (var entry in entries.OfType<JsonObject>())
                    if (entry["sound"]?.GetValue<string>() is { } next) Add(next);
        }
        foreach (Actor actor in _actors.Values)
            if (SoundAuthoring.Sound(actor) is { } path) Add(path);
        string folder = UserData.PathOf("audio-preview");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "audio.json"), new JsonObject { ["format"] = "svm.audio/1", ["sounds"] = sounds }.ToJsonString());
        string mapFolder = Path.Combine(folder, "map");
        Directory.CreateDirectory(Path.Combine(mapFolder, "audio"));
        var map = _bakedFolder is { } baked && File.Exists(Path.Combine(baked, "audio", "audio.json"))
            ? JsonNode.Parse(File.ReadAllText(Path.Combine(baked, "audio", "audio.json")))!.AsObject()
            : new JsonObject { ["format"] = "svm.mapaudio/1", ["zones"] = new JsonArray(), ["zoneDist"] = new JsonArray() };
        map["actors"] = new JsonArray();
        map["sounds"] = new JsonObject();
        File.WriteAllText(Path.Combine(mapFolder, "audio", "audio.json"), map.ToJsonString());
        return AudioLibrary.Load(folder, mapFolder);
    }

    public bool TryGetActorLocation(int actorId, out FVector location)
    {
        location = _actors.TryGetValue(actorId, out var actor) ? Vector(actor.Location) : default;
        return actor is not null;
    }

    public void CollectAmbient(List<AmbientSource> into, FVector listener)
    {
        foreach (var (id, actor) in _actors)
        {
            if (SoundAuthoring.Sound(actor) is not { } sound ||
                _solo() && !_context.Selection.Contains(ObjectRef.Actor(actor.Id))) continue;
            into.Add(new AmbientSource(id, actor.Id, Vector(actor.Location), sound,
                (byte)Math.Clamp(SoundAuthoring.Number(actor, "SoundVolume", 128), 0, 255),
                (float)SoundAuthoring.Number(actor, "SoundRadius", 64),
                (byte)Math.Clamp(SoundAuthoring.Number(actor, "SoundPitch", 64), 0, 255),
                actor.Class.EndsWith("ZoneInfo", StringComparison.Ordinal) || actor.Class.EndsWith("LevelInfo", StringComparison.Ordinal),
                (int)SoundAuthoring.Number(actor, "LightType", 0),
                (float)SoundAuthoring.Number(actor, "LightBrightness", 64), SoundOcclusionOf(id)));
        }
    }

    public string? ZoneIdAt(FVector location) => _zones.GetValueOrDefault(ZoneIndexAt(location));
    public int ZoneIndexAt(FVector location)
    {
        lock (WorldGeometry.BakedQueryLock) return _geometry.BakedWorld?.GetZoneIndex(location) ?? 0;
    }
    public int SurfaceOf(FVector start, FVector end) => OcclusionBetween(start, end, out _) ? 0 : -1;
    public byte SoundOcclusionOf(int actorId) => (byte)Math.Clamp(_actors.TryGetValue(actorId, out var actor) ? SoundAuthoring.Number(actor, "SoundOcclusion", 0) : 0, 0, 255);
    public bool SupportsReverbGeometry => _geometry.HasGeometry;
    public uint AcousticRevision => _geometry.BakedWorld?.GeometryRevision ?? 0;
    public AcousticRayHit TraceReverb(FVector origin, FVector direction, float range)
    {
        if (_geometry.BrushCount == 0 && _geometry.BakedWorld is { } baked)
        {
            lock (WorldGeometry.BakedQueryLock) return LevelAudioWorld.TraceReverbGeometry(baked, origin, direction, range);
        }
        var hit = _geometry.Raycast(Vector(origin), Vector(direction), range);
        return hit is null ? new(false, range, default) : new(true, (float)hit.Distance, Vector(hit.Normal));
    }
    public bool OcclusionBetween(FVector listener, FVector source, out OcclusionHits hits)
    {
        if (_geometry.BrushCount == 0 && _geometry.BakedWorld is { } baked)
        {
            lock (WorldGeometry.BakedQueryLock) return LevelAudioWorld.TraceOcclusion(baked, listener, source, out hits);
        }
        hits = default;
        FVector delta = source - listener;
        float distance = delta.Size();
        if (distance < 0.1f) return false;
        var near = _geometry.Raycast(Vector(listener), Vector(delta / distance), distance);
        if (near is null || near.Distance >= distance - 0.1) return false;
        var far = _geometry.Raycast(Vector(source), Vector(-delta / distance), distance);
        hits = new(Vector(near.Point), far is null ? source : Vector(far.Point), 0, 0,
            ZoneIndexAt(listener), ZoneIndexAt(source), Vector(near.Normal), far is null ? default : Vector(far.Normal));
        return true;
    }
}
