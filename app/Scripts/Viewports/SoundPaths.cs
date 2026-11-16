extern alias GameRuntime;
using Godot;
using OpenScct.Core;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Assets;
using GameRuntime::OpenScctGame.Audio;
using UeSpace = OpenScctGame.UeSpace;

namespace OpenScctEditor.Viewports;

/// <summary>Live mixer arrivals, sampled reach and explicitly geometric reflection guides.</summary>
public partial class SoundPaths : MeshInstance3D
{
    private double _untilUpdate;
    public int Segments { get; private set; }
    public SoundPaths()
    {
        Name = "SoundPaths";
        Layers = VisualLayers.Common;
        MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
        };
        CastShadow = ShadowCastingSetting.Off;
    }
    public void Clear()
    {
        Mesh?.Dispose();
        Mesh = null;
        Segments = 0;
    }
    public void Update(double delta, bool enabled, EditorAudioWorld? world, SoundSystem sound, IReadOnlyCollection<ObjectRef> selected)
    {
        if (!enabled || world is null || selected.Count == 0) { if (Mesh is not null) Clear(); return; }
        if ((_untilUpdate -= delta) > 0) return;
        _untilUpdate = 0.15;
        var lines = new LineBuilder();
        var ids = selected.Where(r => r.Kind == ObjectKind.Actor).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var playing = sound.CaptureVoices().GroupBy(v => v.ActorId).ToDictionary(g => g.Key, g => g.First());
        var placed = new List<AmbientSource>();
        world.CollectAmbient(placed, sound.ListenerLocation);
        foreach (var emitter in placed.Where(s => ids.Contains(s.Id)).Take(4))
        {
            bool active = playing.TryGetValue(emitter.ActorId, out var voice);
            if (!active) voice = new SoundVoice(emitter.Sound, emitter.Sound, emitter.ActorId, true, true, false,
                emitter.Location, sound.ListenerLocation, 0, emitter.SoundRadius, 0, 0, emitter.SoundPitch / 64f, "", 0, emitter.Location);
            Vector3 source = UeSpace.ToGodot(voice.Location), listener = UeSpace.ToGodot(voice.Listener);
            Vector3 arrival = UeSpace.ToGodot(voice.Arrival);
            var audible = new Color(0.25f, 1, 0.5f, Math.Clamp(voice.AppliedGain * 4, 0.25f, 1));
            if (active)
            {
                lines.Line(source, arrival, audible);
                lines.Line(arrival, listener, audible);
            }
            // The range ring is an outer bound. Clip each radial sample against
            // the very same world geometry supplied to the audio engine.
            const int rays = 48;
            Vector3? previous = null;
            Vector3 first = default;
            float range = Math.Max(0, emitter.SoundRadius * 100);
            for (int i = 0; i < rays; i++)
            {
                float angle = i * MathF.Tau / rays;
                var direction = new FVector(MathF.Cos(angle), MathF.Sin(angle), 0);
                var hit = world.TraceReverb(voice.Location, direction, range);
                FVector end = voice.Location + direction * (hit.Hit ? hit.Distance : range);
                Vector3 endpoint = UeSpace.ToGodot(end);
                if (previous is { } last) lines.Line(last, endpoint, new Color(0.25f, 0.65f, 1, 0.65f));
                else first = endpoint;
                previous = endpoint;
                if (i % 6 != 0) continue;
                lines.Line(source, endpoint, new Color(0.25f, 0.65f, 1, 0.3f));
                if (!hit.Hit || hit.Normal.SizeSquared() < 0.5f) continue;
                FVector reflected = direction - hit.Normal * (2 * (direction | hit.Normal));
                float remaining = Math.Max(0, range - hit.Distance);
                var bounce = world.TraceReverb(end + hit.Normal * 2, reflected, remaining);
                lines.Line(endpoint, UeSpace.ToGodot(end + reflected * (bounce.Hit ? bounce.Distance : remaining)), new Color(1, 0.65f, 0.15f, 0.6f));
            }
            if (previous is { } final) lines.Line(final, first, new Color(0.25f, 0.65f, 1, 0.65f));
        }
        var old = Mesh;
        Mesh = lines.Build();
        old?.Dispose();
        Segments = lines.Count;
    }
}
