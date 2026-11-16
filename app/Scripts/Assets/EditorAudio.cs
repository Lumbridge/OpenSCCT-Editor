extern alias GameRuntime;
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
    // A plain holder keeps Godot serialization away from the aliased game assembly.
    private sealed class WorldTransport { public GameRuntime::OpenScctGame.Audio.SoundSystem? Sound; }
    private readonly WorldTransport _transport = new();
    private EditorAudioWorld? _world;
    private bool _worldDirty = true;
    private bool _worldReady;
    private readonly Dictionary<(string, bool), AudioStream?> _streams = new();
    private readonly Dictionary<string, double> _gains = new(StringComparer.OrdinalIgnoreCase);
    private EditorContext _ctx = null!;
    private SoundLibrary? _library;
    public bool Ambience { get; private set; } = true;
    public bool SoloSelection { get; private set; }
    public bool ShowPaths { get; set; } = true;
    private SoundPaths? _paths;
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
        const string extension = "res://addons/scct_audio/scct_audio.gdextension";
        if (!GDExtensionManager.IsExtensionLoaded(extension)) GDExtensionManager.LoadExtension(extension);
        _preview.Finished += () => { if (Loop) _preview.Play(); Changed?.Invoke(); };
        AudioServer.SetBusMute(0, Muted || BackgroundMode.Active);
        context.AddAction(new EditorAction("audio.mute", "Mute Editor Audio", "View/Audio", ToggleMute)
        { IsChecked = () => Muted, Description = "Quickly mute all sound previews and map ambience; remembered between sessions" });
        context.AddAction(new EditorAction("audio.ambience", "Preview Map Sounds", "View/Audio", () =>
        { Ambience = !Ambience; Stop(); Changed?.Invoke(); })
        { IsChecked = () => Ambience, Description = "Listen through the game sound engine from the perspective camera" });
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
        context.ProjectChanged += _ => _worldDirty = true;
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
        _library ??= new SoundLibrary(AssetService.Instance.Catalog, OpenScct.Editor.Core.Services.EditorServices.ContentRoot, AssetService.ProjectFolder);
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

    /// <summary>Loads a clip for its waveform without starting or interrupting playback.</summary>
    public AudioStream? PreviewStream(string path) => Resolve(path, false);

    public bool Play(string path, double volume = 128, double pitch = 64, double position = 0, bool paused = false)
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
        _preview.Play((float)Math.Clamp(position, 0, Length));
        _preview.StreamPaused = paused;
        Changed?.Invoke();
        return true;
    }

    public void SetPreviewSettings(double volume, double pitch)
    {
        _preview.VolumeDb = Mathf.LinearToDb((float)Math.Clamp(volume / 255 * _previewGain, 0.00001, 1));
        _preview.PitchScale = (float)Math.Clamp(pitch / 64, 1f / 64, 255f / 64);
    }

    public void Pause() { _preview.StreamPaused = !_preview.StreamPaused; Changed?.Invoke(); }
    public void Seek(double seconds)
    {
        if (_preview.Stream is null) return;
        bool paused = _preview.StreamPaused;
        // Seek is ignored by Godot when no playback is active. Keep an idle preview
        // paused at the requested position so Play/Resume starts from there.
        if (!_preview.Playing)
        {
            _preview.Play((float)Math.Clamp(seconds, 0, Length));
            _preview.StreamPaused = true;
        }
        else
        {
            _preview.Seek((float)Math.Clamp(seconds, 0, Length));
            _preview.StreamPaused = paused;
        }
        Changed?.Invoke();
    }
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
        _transport.Sound?.StopAll();
        _worldDirty = true;
    }

    public override void _Process(double delta)
    {
        if (!Ambience) { _transport.Sound?.StopAll(); _paths?.Clear(); return; }
        if (ViewportLayout.Instance is not { } views || views.Views.FirstOrDefault(v => !v.Cam.IsOrtho) is not { } view) return;
        // Audio belongs to the perspective SubViewport, not the shell viewport or
        // one of the orthographic cameras sharing its World3D.
        view.Sub.AudioListenerEnable3D = true;
        if (_transport.Sound is null)
        {
            _transport.Sound = new GameRuntime::OpenScctGame.Audio.SoundSystem { Name = "MapSoundSystem" };
            view.Sub.AddChild(_transport.Sound);
            _transport.Sound.SetProcess(false);
        }
        if (_worldDirty)
        {
            _worldDirty = false;
            _worldReady = false;
            try
            {
                _world = new EditorAudioWorld(_ctx, () => SoloSelection);
                _transport.Sound.Acoustics = _world.Acoustics;
                var library = _world.LoadLibrary();
                var previous = _transport.Sound.Library;
                _transport.Sound.Bind(library, _world);
                _worldReady = true;
                previous?.ReleaseStreams();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
            {
                _transport.Sound.StopAll();
                _ctx.Log($"Map sound preview unavailable: {ex.Message}", OpenScct.Editor.Core.Services.Severity.Warning);
                return;
            }
        }
        if (!_worldReady) return;
        var camera = view.Cam;
        _transport.Sound.SetListener(EditorAudioWorld.Vector(camera.Location), new OpenScct.Core.FRotator((int)camera.Rotation.Pitch, (int)camera.Rotation.Yaw, (int)camera.Rotation.Roll));
        _transport.Sound.Update(delta);
        if (_paths is null)
        {
            _paths = new SoundPaths();
            views.Scene.AddChild(_paths);
        }
        _paths.Update(delta, ShowPaths, _world, _transport.Sound, _ctx.Selection.Items);
    }

    public object? WorldSound => _transport.Sound;
    public EditorAudioWorld? AudioWorld => _world;
}
