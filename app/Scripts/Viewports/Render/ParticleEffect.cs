using System.Text.Json;
using Godot;
using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Assets;
using OpenScctGame;
using OpenScctGame.Render.Particles;

namespace OpenScctEditor.Viewports.Render;

/// <summary>Shared game particle simulation and geometry for the library and the placed map effects.</summary>
public partial class ParticleEffect : Node3D
{
    private const float StepSeconds = 1f / 60;
    private static readonly Dictionary<(int, bool, bool), Shader> s_shaders = [];
    private readonly List<Simulation> _systems = [];
    private readonly List<SpriteQuad> _quads = [];
    private readonly List<SparkSegment> _lines = [];
    private readonly List<MeshInstance> _meshes = [];
    private Node3D _scene => this;
    private Func<Camera3D?>? _cameraSource;
    private uint _layers;
    private int _generation;
    public bool Paused { get; set; }
    public (Vector3 Min, Vector3 Max) Bounds { get; private set; }
    private sealed record Simulation(ParticleSystemSim Sim, FVector At, FRotator Facing, Layer[] Layers);
    private sealed record Layer(ParticleEmitterSim Emitter, ImmediateMesh? Surface, MultiMesh? Instances);

    public void Configure(IEnumerable<Actor> actors, bool trigger, Func<Camera3D?> camera, uint renderLayers = 1, bool floor = false)
    {
        _cameraSource = camera;
        _layers = renderLayers;
        int generation = ++_generation;
        _systems.Clear();
        foreach (Node child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        Vector3 min = Vector3.Zero;
        Vector3 max = Vector3.Zero;
        string? content = EditorServices.ContentRoot;
        UeSchema? schema = UeSchema.Load(content);
        AssetResolver assets = AssetService.Instance.Resolver;
        foreach (Actor actor in actors)
        {
            if (EmitterBuild.SystemsOf(actor) is not { } systems) continue;
            var layers = new List<(ParticleParams Params, string? Texture)>();
            foreach (SvJsonObject part in systems.OfType<SvJsonObject>())
            {
                if (part["class"] is not SvJsonString cls || part["props"] is not SvJsonObject props) continue;
                string name = (part["object"] as SvJsonString)?.Value ?? $"{actor.Id}.{layers.Count}";
                using JsonDocument stored = JsonDocument.Parse(InteropValues.Compact(props));
                if (schema is not null)
                {
                    // The loader asks for effective class defaults only for properties it needs.
                    using var owner = new ListOfJsonDocuments();
                    ParticleParams parameters = ParticleParams.From(cls.Value, name, key =>
                        stored.RootElement.TryGetProperty(key, out JsonElement value) ? value : owner.Parse(schema.DefaultOf(cls.Value, key)), null);
                    Add(parameters, part);
                }
                else
                {
                    Add(ParticleParams.From(cls.Value, name, stored.RootElement, null, null), part);
                }

                void Add(ParticleParams parameters, SvJsonObject system)
                {
                    string? texture = EmitterBuild.TextureOf(assets, content, system) is { } file
                        ? Path.Combine(file.RenderDir, file.Relative) : null;
                    layers.Add((parameters, texture));
                    Expand(actor, parameters, ref min, ref max);
                }
            }

            if (layers.Count == 0) continue;
            var sim = new ParticleSystemSim(layers.Select(l => l.Params).ToArray(), Settings(actor), FxRandom.Seed(actor.Id));
            if (floor) sim.Trace = TraceFloor;
            if (trigger) sim.Trigger();
            Layer[] views = sim.Emitters.Select((e, index) => MakeLayer(e, layers[index].Texture, index, generation)).ToArray();
            _systems.Add(new Simulation(sim, AsVector(actor.Location),
                new FRotator(actor.Rotation.Pitch, actor.Rotation.Yaw, actor.Rotation.Roll), views));
        }
        Bounds = (min, max);
    }

    private Layer MakeLayer(ParticleEmitterSim emitter, string? texture, int order, int generation)
    {
        ParticleParams p = emitter.Params;
        ShaderMaterial Material()
        {
            var material = new ShaderMaterial { Shader = ShaderFor(p), RenderPriority = Math.Min(order, 100) };
            material.SetShaderParameter("blend_cells", p.BlendBetweenSubdivisions && !p.UseRandomSubdivision);
            material.SetShaderParameter("alpha_ref", p.AlphaTest ? p.AlphaRef / 255f : -1f);
            if (texture is not null)
            {
                TextureCache.Request(texture, t =>
                {
                    if (generation == _generation && IsInstanceValid(material)) material.SetShaderParameter("sprite", t);
                });
            }

            return material;
        }

        if (p.Kind == ParticleKind.Mesh)
        {
            var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true,
                InstanceCount = p.MaxParticles, VisibleInstanceCount = 0 };
            var node = new MultiMeshInstance3D { Multimesh = multi, Layers = _layers, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _scene.AddChild(node);
            if (!p.UseMeshBlendMode) node.MaterialOverride = Material();
            if (p.StaticMesh is { } mesh)
            {
                StaticMeshes.Request(mesh, m => { if (generation == _generation && IsInstanceValid(multi)) multi.Mesh = m.Mesh; });
            }

            return new Layer(emitter, null, multi);
        }

        var surface = new ImmediateMesh();
        _scene.AddChild(new MeshInstance3D { Mesh = surface, MaterialOverride = Material(),
            Layers = _layers, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        return new Layer(emitter, surface, null);
    }

    public override void _Process(double delta)
    {
        float remaining = Paused ? 0 : (float)Math.Min(delta, 0.08);
        Camera3D? view = _cameraSource?.Invoke();
        if (!IsVisibleInTree() || view is null || !IsInstanceValid(view)) return;
        Basis inverse = GlobalBasis.Inverse();
        Basis basis = inverse * view.GlobalBasis;
        var camera = new ParticleCamera(UeSpace.ToUe(ToLocal(view.GlobalPosition)), UeSpace.DirectionToUe(basis.X),
            UeSpace.DirectionToUe(basis.Y), UeSpace.DirectionToUe(-basis.Z));
        foreach (Simulation s in _systems)
        {
            for (float left = remaining; left > 1e-5f; left -= StepSeconds)
            {
                s.Sim.Step(Math.Min(StepSeconds, left), s.At, s.Facing, default);
            }

            var owner = new ParticleOwner(s.At, s.Facing, default, s.Sim.GlobalOffset);
            foreach (Layer layer in s.Layers) DrawLayer(layer, owner, camera);
        }
    }

    private void DrawLayer(Layer layer, in ParticleOwner owner, in ParticleCamera camera)
    {
        ParticleEmitterSim emitter = layer.Emitter;
        if (layer.Instances is { } instances)
        {
            _meshes.Clear();
            if (!emitter.Disabled) ParticleGeometry.Meshes(emitter, owner, _meshes);
            int count = Math.Min(_meshes.Count, instances.InstanceCount);
            for (int i = 0; i < count; i++)
            {
                MeshInstance mesh = _meshes[i];
                instances.SetInstanceTransform(i, UeSpace.TransformToGodot(mesh.Location, mesh.Rotation, mesh.Scale, default));
                instances.SetInstanceColor(i, AsColor(mesh.Color));
            }

            instances.VisibleInstanceCount = count;
            return;
        }

        ImmediateMesh surface = layer.Surface!;
        surface.ClearSurfaces();
        if (emitter.Disabled) return;
        if (emitter.Params.Kind == ParticleKind.Spark)
        {
            _lines.Clear();
            ParticleGeometry.Sparks(emitter, owner, _lines);
            if (_lines.Count == 0) return;
            surface.SurfaceBegin(Mesh.PrimitiveType.Lines);
            foreach (SparkSegment line in _lines)
            {
                surface.SurfaceSetColor(AsColor(line.Color));
                Vertex(surface, line.From, 0, line.V0, 0, line.V0);
                Vertex(surface, line.To, 1, line.V1, 1, line.V1);
            }
        }
        else
        {
            _quads.Clear();
            if (emitter.Params.Kind == ParticleKind.Beam) ParticleGeometry.Beams(emitter, camera, owner, _quads);
            else ParticleGeometry.Sprites(emitter, camera, owner, _quads);
            if (_quads.Count == 0) return;
            surface.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            foreach (SpriteQuad q in _quads)
            {
                surface.SurfaceSetColor(AsColor(q.Color));
                Vertex(surface, q.C0, q.U0, q.V1, q.BU0, q.BV1);
                Vertex(surface, q.C1, q.U1, q.V1, q.BU1, q.BV1);
                Vertex(surface, q.C2, q.U0, q.V0, q.BU0, q.BV0);
                Vertex(surface, q.C2, q.U0, q.V0, q.BU0, q.BV0);
                Vertex(surface, q.C1, q.U1, q.V1, q.BU1, q.BV1);
                Vertex(surface, q.C3, q.U1, q.V0, q.BU1, q.BV0);
            }
        }

        surface.SurfaceEnd();
    }

    private static void Vertex(ImmediateMesh surface, FVector p, float u, float v, float u2, float v2)
    {
        surface.SurfaceSetUV(new Vector2(u, v));
        surface.SurfaceSetUV2(new Vector2(u2, v2));
        surface.SurfaceAddVertex(UeSpace.ToGodot(p));
    }

    public static void Expand(Actor actor, ParticleParams p, ref Vector3 min, ref Vector3 max)
    {
        float time = Math.Clamp(p.LifetimeRange.Average, 0, 10);
        Vector3 at = UeSpace.ToGodot(AsVector(actor.Location) + p.StartLocationOffset);
        float size = MathF.Max(p.StartSizeRange.X.Larger, p.StartSizeRange.Y.Larger) / 100;
        static (float Min, float Max) Bounds(FxRange start, FxRange velocity, float acceleration, float duration) =>
            (start.Min + MathF.Min(0, velocity.Min * duration + acceleration * duration * duration * 0.5f),
             start.Max + MathF.Max(0, velocity.Max * duration + acceleration * duration * duration * 0.5f));
        var x = Bounds(p.StartLocationRange.X, p.StartVelocityRange.X, p.Acceleration.X, time);
        var y = Bounds(p.StartLocationRange.Y, p.StartVelocityRange.Y, p.Acceleration.Y, time);
        var z = Bounds(p.StartLocationRange.Z, p.StartVelocityRange.Z, p.Acceleration.Z, time);
        min = min.Min(at + new Vector3(x.Min, z.Min, y.Min) / 100 - Vector3.One * size);
        max = max.Max(at + new Vector3(x.Max, z.Max, y.Max) / 100 + Vector3.One * size);
    }

    private static bool TraceFloor(FVector start, FVector end, out FVector hit, out FVector normal)
    {
        normal = new FVector(0, 0, 1);
        hit = default;
        if (start.Z < 0 || end.Z >= 0) return false;
        hit = start + (end - start) * (start.Z / (start.Z - end.Z));
        return true;
    }

    private static FVector AsVector(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static ParticleSystemParams Settings(Actor actor)
    {
        using JsonDocument properties = JsonDocument.Parse(InteropValues.Compact(actor.Props.ToJson()));
        JsonElement Property(string key) => properties.RootElement.TryGetProperty(key, out JsonElement value) ? value : default;
        return new ParticleSystemParams(actor.Props.GetBool("AutoDestroy"), actor.Props.GetBool("AutoReset"),
            FxRangeVector.From(Property("GlobalOffsetRange"), default), FxRange.From(Property("TimeTillResetRange"), default));
    }

    private static Color AsColor(FxColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    private static Shader ShaderFor(ParticleParams p)
    {
        var key = (p.DrawStyle, p.ZWrite, p.ZTest);
        if (s_shaders.TryGetValue(key, out Shader? cached)) return cached;
        (string blend, string output) = p.DrawStyle switch
        {
            0 => ("blend_mix", "ALBEDO = c.rgb;"),
            1 => ("blend_mix", "ALBEDO = c.rgb; ALPHA = c.a;"),
            2 => ("blend_mul", "ALBEDO = mix(v.rgb, t.rgb, v.a) * 2.0; ALPHA = 1.0;"),
            4 => ("blend_premul_alpha", "ALBEDO = c.rgb; ALPHA = c.a;"),
            5 => ("blend_mul", "ALBEDO = vec3(1.0) - c.rgb; ALPHA = 1.0;"),
            6 => ("blend_premul_alpha", "ALBEDO = c.rgb; ALPHA = max(c.r, max(c.g, c.b));"),
            _ => ("blend_add", "ALBEDO = c.rgb; ALPHA = 1.0;"),
        };
        string depth = p.ZWrite || p.DrawStyle == 0 ? "depth_draw_opaque" : "depth_draw_never";
        string zTest = p.ZTest ? "" : ", depth_test_disabled";
        return s_shaders[key] = new Shader
        {
            Code = $$"""
                shader_type spatial;
                render_mode unshaded, fog_disabled, cull_disabled, {{blend}}, {{depth}}{{zTest}};
                uniform sampler2D sprite : source_color, filter_linear_mipmap, repeat_disable, hint_default_white;
                uniform bool blend_cells = false;
                uniform float alpha_ref = -1.0;
                void fragment() {
                    vec4 t = texture(sprite, UV);
                    vec4 v = COLOR;
                    if (blend_cells) {
                        t = mix(t, texture(sprite, UV2), v.a);
                        v.a = 1.0;
                    }
                    vec4 c = t * v;
                    if (alpha_ref >= 0.0 && c.a <= alpha_ref) discard;
                    {{output}}
                }
                """,
        };
    }

    private sealed class ListOfJsonDocuments : IDisposable
    {
        private readonly List<JsonDocument> _docs = [];

        public JsonElement? Parse(SvJsonNode? value)
        {
            if (value is null) return null;
            JsonDocument doc = JsonDocument.Parse(InteropValues.Compact(value));
            _docs.Add(doc);
            return doc.RootElement;
        }

        public void Dispose()
        {
            foreach (JsonDocument doc in _docs) doc.Dispose();
        }
    }
}
