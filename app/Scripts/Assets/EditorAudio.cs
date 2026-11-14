using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Audio;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Assets;

/// <summary>One preview transport and an optional map audition, with a shared persistent mute switch.</summary>
[ShellPart(Order = 50)]
public partial class EditorAudio : Node, IShellPart
{
    public static EditorAudio? Instance { get; private set; }
    public static bool Muted => AppSettings.GetBool("audio", "muted", false);
    public static event Action? Changed;
    private AudioStreamPlayer _preview = null!;
    private AudioListener3D? _listener;
    private readonly Dictionary<string, (Actor Actor, AudioStreamPlayer3D Player)> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, bool), AudioStream?> _streams = new();
    private readonly Dictionary<string, double> _gains = new(StringComparer.OrdinalIgnoreCase);
    private EditorContext _ctx = null!;
    private SoundLibrary? _library;
    private double _tick;
    public bool Ambience { get; private set; }
    public bool SoloSelection { get; private set; }
    public bool Loop { get; set; }
    public double Length => _preview.Stream?.GetLength() ?? 0;
    public double Position => _preview.GetPlaybackPosition();
    public bool Playing => _preview.Playing;
    public bool Paused => _preview.StreamPaused;
    public AudioStream? Stream => _preview.Stream;
    public string? CurrentPath { get; private set; }
    private double _previewGain = 1;

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        Name = "EditorAudio";
        window.AddChild(this);
        _preview = new AudioStreamPlayer { Name = "SoundPreview" };
        AddChild(_preview);
        _preview.Finished += () => { if (Loop) _preview.Play(); Changed?.Invoke(); };
        AudioServer.SetBusMute(0, Muted || BackgroundMode.Active);
        context.AddAction(new EditorAction("audio.mute", "Mute Editor Audio", "View/Audio", ToggleMute)
        { IsChecked = () => Muted, Description = "Quickly mute all sound previews and map ambience; remembered between sessions" });
        context.AddAction(new EditorAction("audio.ambience", "Preview Map Sounds", "View/Audio", () =>
        { Ambience = !Ambience; Stop(); Changed?.Invoke(); })
        { IsChecked = () => Ambience, Description = "Listen from the perspective camera while moving through the map (distance and panning preview)" });
        context.AddAction(new EditorAction("audio.solo", "Solo Selected Sound Sources", "View/Audio", () =>
        { SoloSelection = !SoloSelection; Changed?.Invoke(); }) { IsChecked = () => SoloSelection });
        context.AddAction(new EditorAction("audio.stop", "Stop Sound Preview", "View/Audio", Stop));
        context.AddAction(new EditorAction("view.effects", "Particle Effects", "View/Show", () =>
        { if (ViewportLayout.Instance?.Scene is { } scene) scene.SetEffects(!scene.EffectsVisible, scene.EffectsPaused); })
        { IsChecked = () => ViewportLayout.Instance?.Scene.EffectsVisible == true });
        context.AddAction(new EditorAction("view.pauseEffects", "Pause Particle Preview", "View/Show", () =>
        { if (ViewportLayout.Instance?.Scene is { } scene) scene.SetEffects(scene.EffectsVisible, !scene.EffectsPaused); })
        { IsChecked = () => ViewportLayout.Instance?.Scene.EffectsPaused == true });
        context.AddAction(new EditorAction("view.restartEffects", "Replay Map Emitters", "View/Show", () => ViewportLayout.Instance?.Scene.RestartEffects()));
        context.SessionReplaced += Reset;
        AssetService.Instance.CatalogChanged += Reset;
        TreeExiting += () => { AssetService.Instance.CatalogChanged -= Reset; Instance = null; };
    }

    public static void ToggleMute()
    {
        bool muted = !Muted;
        AppSettings.Set("audio", "muted", muted);
        AudioServer.SetBusMute(0, muted || BackgroundMode.Active);
        EditorContext.Instance.SetStatus(muted ? "Editor audio muted" : "Editor audio unmuted");
        Changed?.Invoke();
    }

    public static AudioStream? LoadFile(string file, bool loop = false)
    {
        AudioStream? stream = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".wav" => AudioStreamWav.LoadFromFile(file),
            ".ogg" => AudioStreamOggVorbis.LoadFromFile(file),
            ".mp3" => AudioStreamMP3.LoadFromFile(file),
            _ => null,
        };
        if (stream is AudioStreamWav wav)
        {
            if (loop && wav.LoopEnd <= wav.LoopBegin && wav.Format is AudioStreamWav.FormatEnum.Format8Bits or AudioStreamWav.FormatEnum.Format16Bits)
            {
                wav.LoopBegin = 0;
                wav.LoopEnd = wav.Data.Length / (wav.Stereo ? 2 : 1) / (wav.Format == AudioStreamWav.FormatEnum.Format16Bits ? 2 : 1);
            }
            wav.LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled;
        }
        if (stream is AudioStreamOggVorbis ogg) ogg.Loop = loop;
        if (stream is AudioStreamMP3 mp3) mp3.Loop = loop;
        return stream;
    }

    private AudioStream? Resolve(string path, bool loop)
    {
        if (_streams.TryGetValue((path, loop), out var stream)) return stream;
        _library ??= new SoundLibrary(AssetService.Instance.Catalog, OpenScct.Editor.Core.Services.EditorServices.ContentRoot);
        try
        {
            var leaf = _library.Leaves(path).FirstOrDefault(s => s.File is { } f && File.Exists(f));
            stream = leaf?.File is { } file ? LoadFile(file, loop) : null;
            _gains[path] = leaf?.Gain ?? 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ctx.Log($"Cannot preview sound {path}: {ex.Message}", OpenScct.Editor.Core.Services.Severity.Warning);
            stream = null;
        }
        _streams[(path, loop)] = stream;
        return stream;
    }

    public bool Play(string path, double volume = 128, double pitch = 64)
    {
        Stop();
        _preview.Stream = Resolve(path, false);
        CurrentPath = path;
        _previewGain = _gains.GetValueOrDefault(path, 1);
        if (_preview.Stream is null)
        {
            _ctx.SetStatus("This sound has no playable file. Install its content pack.");
            return false;
        }
        SetPreviewSettings(volume, pitch);
        _preview.StreamPaused = false;
        _preview.Play();
        Changed?.Invoke();
        return true;
    }

    public void SetPreviewSettings(double volume, double pitch)
    {
        _preview.VolumeDb = Mathf.LinearToDb((float)Math.Clamp(volume / 255 * _previewGain, 0.00001, 1));
        _preview.PitchScale = (float)Math.Clamp(pitch / 64, 1f / 64, 255f / 64);
    }

    public void Pause() { _preview.StreamPaused = !_preview.StreamPaused; Changed?.Invoke(); }
    public void Seek(double seconds) { if (_preview.Stream is not null) _preview.Seek((float)Math.Clamp(seconds, 0, Length)); }
    public void Stop() { _preview.Stop(); _preview.StreamPaused = false; Changed?.Invoke(); }

    private void Reset()
    {
        Stop();
        ClearSources();
        _streams.Clear();
        _gains.Clear();
        CurrentPath = null;
        _library = null;
    }

    private void ClearSources()
    {
        foreach (var source in _sources.Values) { source.Player.Stop(); source.Player.QueueFree(); }
        _sources.Clear();
    }

    public override void _Process(double delta)
    {
        _tick -= delta;
        if (_tick > 0) return;
        _tick = 0.1;
        if (!Ambience) { if (_sources.Count > 0) ClearSources(); return; }
        if (ViewportLayout.Instance is not { } views) return;
        if (_listener is null || !IsInstanceValid(_listener))
        {
            _listener = new AudioListener3D();
            views.Scene.AddChild(_listener);
            _listener.MakeCurrent();
        }
        Camera3D? camera = views.Views.FirstOrDefault(v => !v.Cam.IsOrtho)?.Cam.Camera;
        if (camera is null) return;
        _listener.GlobalTransform = camera.GlobalTransform;
        var selected = _ctx.Selection.Items.ToHashSet();
        var wanted = _ctx.Project.Actors.Where(a => SoundAuthoring.Sound(a) is not null && views.Scene.ShowActors &&
            ObjectFilter.IsVisible(_ctx.Project, a.Editor) && (!SoloSelection || selected.Contains(ObjectRef.Actor(a.Id))) &&
            Space.ToGodot(a.Location).DistanceTo(camera.GlobalPosition) < SoundAuthoring.Number(a, "SoundRadius", 64))
            .OrderBy(a => Space.ToGodot(a.Location).DistanceSquaredTo(camera.GlobalPosition)).Take(32)
            .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        foreach (string id in _sources.Keys.ToArray())
            if (!wanted.TryGetValue(id, out var actor) || !ReferenceEquals(_sources[id].Actor, actor))
            { _sources[id].Player.Stop(); _sources[id].Player.QueueFree(); _sources.Remove(id); }
        foreach (var actor in wanted.Values)
        {
            if (_sources.ContainsKey(actor.Id) || Resolve(SoundAuthoring.Sound(actor)!, true) is not { } stream) continue;
            float radius = (float)SoundAuthoring.Number(actor, "SoundRadius", 64);
            if (radius <= 0) continue;
            var player = new AudioStreamPlayer3D
            {
                Stream = stream, Position = Space.ToGodot(actor.Location), MaxDistance = radius,
                UnitSize = Math.Max(0.01f, radius * 0.1f),
                VolumeDb = Mathf.LinearToDb((float)Math.Clamp(SoundAuthoring.Number(actor, "SoundVolume", 128) / 255 * _gains.GetValueOrDefault(SoundAuthoring.Sound(actor)!, 1), 0.00001, 1)),
                PitchScale = (float)Math.Clamp(SoundAuthoring.Number(actor, "SoundPitch", 64) / 64, 1f / 64, 255f / 64),
            };
            views.Scene.AddChild(player);
            player.Play();
            _sources[actor.Id] = (actor, player);
        }
    }
}
