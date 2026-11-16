extern alias GameRuntime;
using System.Reflection;
using System.Text.Json;
using Godot;
using OpenScct.Editor.Core.Audio;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Scripting;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Viewports;
using GameRuntime::OpenScctGame.Audio;

namespace OpenScctEditor.Shell.Shots;

/// <summary>Opt-in real-runtime audio regression checks; runs only in an isolated background editor.</summary>
[ShellPart(Order = 120)]
public partial class AudioChecks : Node, IShellPart
{
    private readonly List<string> _passed = [];
    private string _output = "";
    public void Attach(MainWindow window, EditorContext context)
    {
        string? output = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--audio-checks="))?["--audio-checks=".Length..];
        if (output is null || !UserData.IsSeparate) return;
        _output = Path.GetFullPath(output);
        window.AddChild(this);
        EditorLifetime.Defer(() => { _ = Run(context); });
    }
    private void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        _passed.Add(message);
        GD.Print("AUDIO CHECK: " + message);
    }
    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), Godot.Timer.SignalName.Timeout);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Call(object instance, string name) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
    private async Task Run(EditorContext context)
    {
        string? error = null;
        float peak = -200;
        try
        {
            await Wait(2);
            var actor = context.Project.Actors.First(a => SoundAuthoring.Sound(a) is not null);
            string path = SoundAuthoring.Sound(actor)!;
            var audio = EditorAudio.Instance!;
            for (int i = 0; i < 100 && audio.PreviewStream(path) is null; i++) await Wait(0.1);
            var panel = SoundPanel.Instance!;
            for (int attempt = 0; attempt < 100 && audio.PreviewStream(path) is null; attempt++) await Wait(0.1);
            panel.ShowSound(path);
            await Wait(0.2);
            var slider = Field<HSlider>(panel, "_seek");
            var waveform = Field<SoundWaveform>(panel, "_waveform");
            Check(waveform.Length > 1, "Selected sound loads a waveform before playback");
            slider.Value = 0.6;
            await Wait(0.1);
            Check(audio.CurrentPath == path && audio.Paused && Math.Abs(audio.Position / audio.Length - 0.6) < 0.05, "Scrubbing before Play loads the selected sound and retains its position");
            Call(panel, "Play");
            await Wait(0.15);
            Check(audio.Playing && !audio.Paused && audio.Position > audio.Length * 0.6, "Play resumes from the scrubbed position");
            slider.Value = 0.2;
            await Wait(0.1);
            Check(!audio.Paused && Math.Abs(audio.Position / audio.Length - 0.2) < 0.12, "Scrubbing during playback seeks without pausing");
            audio.Pause();
            waveform._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new(waveform.Size.X * 0.75f, 10) });
            await Wait(0.1);
            Check(audio.Paused && Math.Abs(audio.Position / audio.Length - 0.75) < 0.05, "Waveform seeking preserves pause and moves the playhead");
            audio.Stop();
            slider.Value = 0.35;
            await Wait(0.1);
            Check(audio.Paused && Math.Abs(audio.Position / audio.Length - 0.35) < 0.05, "Scrubbing after Stop retains a new starting position");
            audio.Stop();
            var layout = ViewportLayout.Instance!;
            var view = layout.Views.First(v => !v.Cam.IsOrtho);
            view.Cam.Location = actor.Location + new Vec3(100, 0, 0);
            context.Selection.Set([ObjectRef.Actor(actor.Id)]);
            await Wait(1);
            var mixer = (SoundSystem)audio.WorldSound!;
            Check(mixer.Buses!.NativeAvailable && ClassDB.ClassExists("ScctBinauralEffect"), "Native game reverb and Steam Audio binaural extension are loaded");
            Check(audio.Ambience && view.Sub.AudioListenerEnable3D, "Map listening defaults on in the perspective viewport");
            Check(mixer.GetViewport() == view.Sub, "Game sound engine and listener belong to the camera viewport");
            Check(mixer.Library!.Count > 0 && mixer.Unknown.Count == 0 && mixer.Library.LoadErrors.Count == 0, "Placed sound resolves through the game sound library");
            Check(mixer.CaptureVoices().Any(v => v.AppliedGain > 0.001f), "Nearby placed sound has a live audible game voice");
            int bus = AudioServer.GetBusIndex(ReverbBuses.WorldBus);
            for (int i = 0; i < 20; i++)
            {
                await Wait(0.05);
                peak = Math.Max(peak, Math.Max(AudioServer.GetBusPeakVolumeLeftDb(bus, 0), AudioServer.GetBusPeakVolumeRightDb(bus, 0)));
            }
            Check(peak > -65, "Placed sound produces measurable audio on the game world bus");
            Check(layout.Scene.GetChildren().OfType<SoundPaths>().Any(p => p.Segments > 0), "Selected sound draws live sound paths and reach");
            view.Cam.Location = actor.Location + new Vec3(100000, 0, 0);
            await Wait(0.5);
            Check(!mixer.CaptureVoices().Any(v => v.Ambient && v.AppliedGain > 0.001f), "Placed sound fades outside its game hearing range");
            view.Cam.Location = actor.Location + new Vec3(100, 0, 0);
            await Wait(0.7);
            Check(mixer.CaptureVoices().Any(v => v.AppliedGain > 0.001f), "Returning to the source restores playback");
            Check(layout.Scene.GetChildren().OfType<SoundPaths>().Any(p => p.Segments > 0), "Reach remains visible after returning to the source");
            var room = new Brush("AudioCheckRoom")
            {
                Oper = CsgOper.Subtract,
                Location = actor.Location,
                Shape = new BrushShape { Faces = ConvexSolid.Box(new(-1000, -1000, -500), new(1000, 1000, 1000)).Select(v => new BrushFace { Vertices = v }).ToList() },
            };
            var wall = new Brush("AudioCheckWall")
            {
                Oper = CsgOper.Add,
                Location = actor.Location,
                Shape = new BrushShape { Faces = ConvexSolid.Box(new(40, -100, -500), new(60, 100, 1000)).Select(v => new BrushFace { Vertices = v }).ToList() },
            };
            context.Execute(new AddObjectsCommand("Audio check geometry", brushes: [room, wall]));
            await Wait(1.5);
            Check(audio.AudioWorld!.SupportsReverbGeometry, "Editable room geometry is available to the game reverb engine");
            Check(audio.AudioWorld.OcclusionBetween(EditorAudioWorld.Vector(view.Cam.Location), EditorAudioWorld.Vector(actor.Location), out _), "Editable wall blocks the direct source-to-camera path");
            Check(mixer.OcclusionQueries > 0, "Game mixer queries the editor's acoustic geometry");
            Check(mixer.CaptureVoices().Any(v => (v.Arrival - v.Location).Size() > 1), "Game propagation routes the sound around the wall");
            Check(mixer.Buses!.CaptureGeometry().Any(g => g.Rays > 0), "Adaptive reverb samples the editable room");
            audio.ShowPaths = false;
            await Wait(0.2);
            Check(layout.Scene.GetChildren().OfType<SoundPaths>().All(p => p.Segments == 0), "Sound Paths switch clears the visualization");
            audio.ShowPaths = true;
            view.Cam.Location = actor.Location + new Vec3(550, 550, 300);
            view.Cam.Yaw = -24576;
            view.Cam.Pitch = -3800;
            view.Cam.Apply();
            await Wait(0.5);
            await UiDriver.Instance!.Shot(ApiJson.Obj(("dir", ApiJson.Str(Path.GetDirectoryName(_output)!)), ("name", ApiJson.Str("audio-paths")), ("views", OpenScct.Content.Json.SvJsonNode.Bool(true))));
        }
        catch (Exception ex) { error = ex.ToString(); GD.PrintErr(error); }
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        File.WriteAllText(_output, JsonSerializer.Serialize(new { passed = error is null, checks = _passed, peakDb = peak, error }, new JsonSerializerOptions { WriteIndented = true }));
        EditorLifetime.Quit(GetTree(), error is null ? 0 : 1);
    }
}
