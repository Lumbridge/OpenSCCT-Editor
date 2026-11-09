using Godot;
using OpenScct.Content.Materials;
using OpenScctGame.Render.Materials;

namespace OpenScctEditor.Assets;

/// <summary>Shared material sampling with Godot lighting for authoring. The scene clock animates all viewports together.</summary>
public partial class AuthoredClock : Node
{
    private double _time;
    public override void _Ready() => RenderingServer.GlobalShaderParameterAdd("authored_time", RenderingServer.GlobalShaderParameterType.Float, 0f);
    public override void _Process(double delta) { _time += delta; RenderingServer.GlobalShaderParameterSet("authored_time", (float)_time); }
}

public static class AuthoredPreview
{
    private static readonly Dictionary<string, Shader> s_shaders = new();
    public static ShaderMaterial Make(AuthoredMaterial definition, Func<string, Texture2D?> texture, Texture2D? lightmap = null, bool unlit = false, bool vertexLit = false, bool depthPreview = false, Func<string, MaterialTexture, Texture2D?>? authoredTexture = null)
    {
        bool flat = unlit || definition.Unlit || lightmap is not null || vertexLit;
        bool refraction = definition.Refraction > 0 && definition.Surface == "translucent";
        string key = $"{flat}|{definition.TwoSided}|{definition.Surface}|{lightmap is not null}|{vertexLit}|{depthPreview}|{refraction}";
        if (!s_shaders.TryGetValue(key, out Shader? shader))
        {
            string modes = "" + (flat ? "unshaded," : "") + (definition.TwoSided ? "cull_disabled" : "cull_back");
            if (definition.Surface == "translucent") modes += ",blend_mix,depth_draw_opaque";
            string alpha = definition.Surface == "opaque" ? "" : "ALPHA = sample_value.color.a;" + (definition.Surface == "masked" ? "ALPHA_SCISSOR_THRESHOLD = am_cutoff;" : "");
            shader = new Shader { Code = "shader_type spatial;\nrender_mode " + modes + ";\n" + (refraction ? "#define AM_REFRACTION\n" : "") + AuthoredMaterialBinding.ShaderCode + "\n" + $$"""
                global uniform float authored_time;
                global uniform vec4 storey_clip;
                uniform bool preview_paused = false;
                uniform float preview_time = 0.0;
                uniform sampler2D lightmap : filter_linear, repeat_disable;
                uniform vec4 lightmap_scale = vec4(1.0);
                uniform vec2 uv_offset = vec2(0.0);
                varying vec3 world_position;
                varying vec3 world_normal;
                varying vec4 authored_color;
                varying vec4 authored_factors;
                varying vec3 authored_tangent;
                varying vec4 baked_vertex_direction;
                void vertex() {
                    {{(depthPreview ? "if (am_has_height) VERTEX += NORMAL * (am_scalar(textureLod(am_tex_height, am_uv(UV, 0.0), 0.0), am_channel_height, am_invert_height) - am_heightMidpoint) * am_displacement / 100.0;" : "")}}
                    world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    world_normal = MODEL_NORMAL_MATRIX * NORMAL;
                    baked_vertex_direction = CUSTOM2;
                }
                void fragment() {
                    if (storey_clip.z > 0.5 && (world_position.y < storey_clip.x || world_position.y > storey_clip.y)) discard;
                    mat3 frame = am_frame(world_position, normalize(world_normal), am_mapping(UV, preview_paused ? preview_time : authored_time));
                    vec3 view_vector = transpose(frame) * normalize(CAMERA_POSITION_WORLD - world_position);
                    AuthoredSample sample_value = am_evaluate(UV + uv_offset, world_position, normalize(world_normal), view_vector, preview_paused ? preview_time : authored_time);
                    vec3 authored_n = normalize(am_triplanar ? sample_value.normal : frame * sample_value.normal);
                    vec3 baked_highlight = vec3(0.0);
                    ALBEDO = sample_value.color.rgb;
                    {{(refraction ? "ALBEDO = mix(ALBEDO, textureLod(am_scene, SCREEN_UV + sample_value.normal.xy * am_refraction, sample_value.roughness * 5.0).rgb, 1.0 - sample_value.color.a);" : "")}}
                    {{(lightmap is not null ? "vec3 irradiance = am_linear(texture(lightmap, UV2).rgb * lightmap_scale.rgb); if (am_hasBakedDirection) { vec4 direction = texture(am_bakedDirection, UV2); vec3 light_direction = normalize(direction.xyz * 2.0 - 1.0); irradiance *= mix(1.0, clamp(max(dot(authored_n, light_direction), 0.0) / max(dot(normalize(world_normal), light_direction), 0.1), 0.0, 4.0), direction.a); } ALBEDO *= irradiance;" : "")}}
                    {{(vertexLit ? "vec3 irradiance = am_linear(COLOR.rgb * 2.0); if (baked_vertex_direction.a > 0.0) { vec3 light_direction = normalize(baked_vertex_direction.xyz * 2.0 - 1.0); irradiance *= mix(1.0, clamp(max(dot(authored_n, light_direction), 0.0) / max(dot(normalize(world_normal), light_direction), 0.1), 0.0, 4.0), baked_vertex_direction.a); baked_highlight = am_highlight(sample_value, authored_n, normalize(CAMERA_POSITION_WORLD - world_position), light_direction, irradiance * baked_vertex_direction.a, frame[0]); } ALBEDO *= irradiance;" : "")}}
                    {{(vertexLit || lightmap is not null ? "ALBEDO *= (1.0 - sample_value.metallic) * sample_value.ao;" : "")}}
                    NORMAL = normalize(mat3(VIEW_MATRIX) * authored_n);
                    METALLIC = sample_value.metallic; ROUGHNESS = sample_value.roughness;
                    SPECULAR = sample_value.specular; AO = sample_value.ao;
                    EMISSION = sample_value.emission + am_environment(sample_value, authored_n, normalize(CAMERA_POSITION_WORLD - world_position)) + baked_highlight;
                    authored_color = sample_value.color; authored_factors = vec4(sample_value.roughness, sample_value.metallic, sample_value.specular, sample_value.ao);
                    authored_tangent = normalize(mat3(VIEW_MATRIX) * frame[0]);
                    CLEARCOAT = am_clearcoat; CLEARCOAT_ROUGHNESS = 1.0 - am_clearcoatGloss; ANISOTROPY = am_anisotropy;
                    {{alpha}}
                }
                void light() {
                    AuthoredSample sample_value; sample_value.color = authored_color; sample_value.normal = NORMAL; sample_value.emission = vec3(0.0);
                    sample_value.roughness = authored_factors.x; sample_value.metallic = authored_factors.y; sample_value.specular = authored_factors.z; sample_value.ao = authored_factors.w;
                    DIFFUSE_LIGHT += am_brdf(sample_value, normalize(NORMAL), normalize(VIEW), normalize(LIGHT), LIGHT_COLOR * ATTENUATION, authored_tangent);
                }
                """ };
            s_shaders[key] = shader;
        }
        var material = new ShaderMaterial { Shader = shader };
        AuthoredMaterialBinding.Apply(material, definition, texture, authoredTexture);
        if (lightmap is not null) material.SetShaderParameter("lightmap", lightmap);
        return material;
    }
}
