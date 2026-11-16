using Godot;
using OpenScct.Editor.Core.Audio;
using OpenScct.Editor.Core.Emitters;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Viewports;

public sealed partial class WorldScene
{
    public bool EffectsVisible { get; private set; } = true;
    public bool EffectsPaused { get; private set; }

    private void AttachEmitter(Node3D root, Actor actor, MapProject project)
    {
        if (!EmitterPlacement.CanCapture(actor, project)) return;
        try
        {
            // Capture makes the actor relative to its own pivot; the entry root supplies the map transform.
            EmitterPreset preset = EmitterPlacement.Capture(actor.Id, "Map", [actor], project);
            var effect = new ParticleEffect { Name = "ParticlePreview", Visible = EffectsVisible, Paused = EffectsPaused };
            root.AddChild(effect);
            effect.Configure(preset.Actors, preset.Mode.Equals("triggered", StringComparison.OrdinalIgnoreCase), () => ViewportLayout.Instance?.Views.FirstOrDefault(v => !v.Cam.IsOrtho)?.Cam.Camera,
                VisualLayers.Effects);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or InvalidOperationException)
        {
            EditorContext.Instance.Log($"{actor.Id}: particle preview unavailable: {ex.Message}", Severity.Warning);
        }
    }

    public void SetEffects(bool visible, bool paused)
    {
        EffectsVisible = visible;
        EffectsPaused = paused;
        foreach (Entry entry in _entries.Values)
            foreach (var effect in entry.Root.GetChildren().OfType<ParticleEffect>())
            {
                effect.Visible = visible;
                effect.Paused = paused;
            }
    }

    public void RestartEffects()
    {
        if (_project is not { } project) return;
        foreach (Entry entry in _entries.Values)
        {
            foreach (var effect in entry.Root.GetChildren().OfType<ParticleEffect>()) { entry.Root.RemoveChild(effect); effect.QueueFree(); }
            if (entry.Source is Actor actor) AttachEmitter(entry.Root, actor, project);
        }
    }

    private static MeshInstance3D? AttachSoundRange(Node3D root, Actor actor)
    {
        if (SoundAuthoring.Sound(actor) is null) return null;
        float radius = (float)SoundAuthoring.Number(actor, "SoundRadius", 64); // 100 Unreal units = 1 metre.
        var lines = new LineBuilder();
        for (int plane = 0; plane < 3; plane++)
        {
            var points = new List<Vector3>();
            for (int i = 0; i < 96; i++)
            {
                float angle = i * MathF.Tau / 96;
                float x = MathF.Cos(angle) * radius, y = MathF.Sin(angle) * radius;
                points.Add(plane switch { 0 => new(x, 0, y), 1 => new(x, y, 0), _ => new(0, x, y) });
            }
            lines.Polyline(points, SceneStyle.OverlayGuide, true);
        }
        var mesh = AddMesh(root, lines.Build(), SceneStyle.Lines, VisualLayers.Common);
        if (mesh is not null)
        {
            mesh.Name = "SoundRadius";
            mesh.Visible = false;
        }
        return mesh;
    }
}
