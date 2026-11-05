using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.Lighting;
using OpenScctEditor.Core;

namespace OpenScctEditor.Viewports.Render;

/// <summary>
/// The lights the game applies to the world at run time, which a bake leaves out (WorldLights.IsDynamicWorldLight:
/// bDynamicLight lights, switchable trigger lights among them), for the Lit Preview: an additive pass over each
/// lightmapped surface brings it from texture x 2 x lightmap to the game's texture x min(2 x lightmap + dynamic, 2),
/// with the BSP falloff 1 - x^2 (game/Shaders/svm_world.gdshaderinc svm_dynamic). A vertex-lit static mesh takes them
/// over its baked vertex light the same way, with the actor falloff 1 - 3x^2 + 2x^3, as the game's D3D vertex lights
/// give it (svm_dyn_falloff; RenderingModeNormal::SetLight). A trigger light takes the event
/// preview's state while one plays, else its state at the round's start (WorldScene decides); light types count at
/// their mean brightness. Like the base pass, it is cut to the storey shown (<see cref="StoreyClip"/>), and it draws the
/// same sides: a two-sided surface takes the lights on its back face too, with the normal turned round as the game's
/// shader turns it; a masked surface only where its texture passes the alpha test, a blended one by its alpha.
/// </summary>
public static class DynamicWorldLights
{
    /// <summary>The most run-time lights, as the game's WorldLights: the first this many in the map's order, switched
    /// off or not; the rest never light the world.</summary>
    public const int MaxLights = 256;

    // Row 0: x the light count. Row 1 + i: position and radius, colour and cos theta, spot direction and cos phi / 2
    // (w -2 for a point light), as the game packs a light (ActorLighting.PackWorld).
    private static readonly Image s_rows = Image.CreateEmpty(3, MaxLights + 1, false, Image.Format.Rgbaf);
    private static ImageTexture? s_texture;
    private static readonly Dictionary<(bool VertexLit, bool TwoSided), Shader> s_shaders = new();
    private static int s_leftOut;

    // {BAKED} is the baked light term in half units, {FALLOFF} the falloff over x = distance / radius, {CULL} the cull mode.
    private const string Template = """
        shader_type spatial;
        render_mode unshaded, blend_add, depth_draw_never, {CULL}, fog_disabled;

        // The storey shown, as the base pass cuts it (StoreyClip): x, y its bottom and top, z 1 while one is shown.
        global uniform vec4 storey_clip;
        uniform sampler2D lights : filter_nearest;
        // Raw sRGB values, as the game multiplies them before the display.
        uniform sampler2D albedo : filter_linear_mipmap, repeat_enable;
        uniform sampler2D lightmap : filter_linear;
        uniform bool has_albedo = false;
        // 0 opaque, 1 masked (alpha test at a half), 2 blended (the pass scaled by the texture's alpha).
        uniform int alpha_mode = 0;

        varying vec3 v_wpos;
        varying vec3 v_wn;

        void vertex() {
            v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            v_wn = normalize(mat3(MODEL_MATRIX) * NORMAL);
        }

        vec3 to_linear(vec3 c) {
            return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
        }

        void fragment() {
            if (storey_clip.z > 0.5 && (v_wpos.y < storey_clip.x || v_wpos.y > storey_clip.y)) {
                discard;
            }

            vec3 wn = FRONT_FACING ? v_wn : -v_wn;
            vec3 dyn = vec3(0.0);
            int count = int(texelFetch(lights, ivec2(0, 0), 0).x);
            for (int i = 1; i <= count; i++) {
                vec4 p = texelFetch(lights, ivec2(0, i), 0);
                vec4 c = texelFetch(lights, ivec2(1, i), 0);
                vec4 s = texelFetch(lights, ivec2(2, i), 0);
                vec3 l;
                float att = 1.0;
                if (p.w < 0.0) {
                    l = normalize(p.xyz);
                } else {
                    vec3 d = p.xyz - v_wpos;
                    float dist = length(d);
                    l = d / max(dist, 1e-5);
                    float x = dist / max(p.w, 1e-5);
                    att = x >= 1.0 ? 0.0 : {FALLOFF};
                    if (s.w > -1.5) {
                        att *= clamp((dot(-l, s.xyz) - s.w) / max(c.a - s.w, 1e-5), 0.0, 1.0);
                    }
                }
                dyn += c.rgb * att * max(0.0, dot(wn, l));
            }
            vec4 t = has_albedo ? texture(albedo, UV) : vec4(1.0);
            if (dot(dyn, dyn) <= 0.0 || (alpha_mode == 1 && t.a < 0.5)) {
                discard;
            }
            vec3 lm = {BAKED} * 2.0;
            // Over the base pass (texture x 2 x lightmap): what the dynamic light adds below the 2x cap.
            vec3 add = max(to_linear(min(t.rgb * min(lm + dyn, vec3(2.0)), vec3(1.0))) - to_linear(min(t.rgb * lm, vec3(1.0))), vec3(0.0));
            ALBEDO = alpha_mode == 2 ? add * t.a : add;
        }
        """;

    private static ImageTexture Rows => s_texture ??= ImageTexture.CreateFromImage(s_rows);

    private static Shader Shader(bool vertexLit, bool twoSided)
    {
        if (!s_shaders.TryGetValue((vertexLit, twoSided), out Shader? shader))
        {
            string code = Template.Replace("{CULL}", twoSided ? "cull_disabled" : "cull_back");
            code = vertexLit
                ? code.Replace("{BAKED}", "COLOR.rgb").Replace("{FALLOFF}", "1.0 - 3.0 * x * x + 2.0 * x * x * x")
                : code.Replace("{BAKED}", "texture(lightmap, UV2).rgb").Replace("{FALLOFF}", "1.0 - x * x");
            s_shaders[(vertexLit, twoSided)] = shader = new Shader { Code = code };
        }

        return shader;
    }

    /// <summary>The additive pass for a surface of <paramref name="base"/>'s sides and alpha, lightmapped by
    /// <paramref name="atlas"/> (its texture set later by <see cref="SetAlbedo"/>), cut to the storey shown as its base
    /// pass is.</summary>
    public static ShaderMaterial Pass(Texture2D atlas, BaseMaterial3D @base)
    {
        ShaderMaterial m = Make(vertexLit: false, @base);
        m.SetShaderParameter("lightmap", atlas);
        return m;
    }

    /// <summary>The additive pass for a static mesh drawn with its baked vertex light (the light in its vertex colours,
    /// half units, as <see cref="ViewMaterials.VertexLit"/> draws it) by <paramref name="base"/>, its texture set later
    /// by <see cref="SetAlbedo"/>.</summary>
    public static ShaderMaterial VertexPass(BaseMaterial3D @base) => Make(vertexLit: true, @base);

    private static ShaderMaterial Make(bool vertexLit, BaseMaterial3D @base)
    {
        StoreyClip.Register();
        var m = new ShaderMaterial { Shader = Shader(vertexLit, @base.CullMode == BaseMaterial3D.CullModeEnum.Disabled) };
        m.SetShaderParameter("lights", Rows);
        m.SetShaderParameter("alpha_mode", @base.Transparency switch
        {
            BaseMaterial3D.TransparencyEnum.Disabled => 0,
            BaseMaterial3D.TransparencyEnum.Alpha or BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass => 2,
            _ => 1,
        });
        return m;
    }

    public static void SetAlbedo(ShaderMaterial pass, Texture2D texture)
    {
        pass.SetShaderParameter("albedo", texture);
        pass.SetShaderParameter("has_albedo", true);
    }

    /// <summary>Writes the map's run-time world lights that are on (<paramref name="isOn"/>: a trigger light as the
    /// round starts it or an event preview switches it).</summary>
    public static void Update(MapProject p, Func<Actor, bool> isOn)
    {
        int row = 0, taken = 0, leftOut = 0;
        foreach (Actor a in p.Actors.Where(IsDynamicWorldLight))
        {
            if (taken == MaxLights)
            {
                leftOut++;
                continue;
            }

            taken++;
            if (isOn(a))
            {
                Write(++row, a);
            }
        }

        s_rows.SetPixel(0, 0, new Color(row, 0, 0, 0));
        Rows.Update(s_rows);
        if (leftOut != s_leftOut)
        {
            s_leftOut = leftOut;
            if (leftOut > 0)
            {
                EditorContext.Instance.Log($"The map has {MaxLights + leftOut} run-time lights (bDynamicLight); the game, and the Lit Preview, " +
                    $"use only the first {MaxLights}, so {leftOut} never light the world", Severity.Warning);
            }
        }
    }

    /// <summary>WorldLights.IsDynamicWorldLight from the actor: a light with a light type, bDynamicLight (or movable),
    /// not an Echelon spot shadow light, with some brightness.</summary>
    private static bool IsDynamicWorldLight(Actor a) =>
        ClassDefaults.IsA(a, "Engine.Light") && ClassDefaults.Number(a, "LightType", 1) != 0 &&
        (ClassDefaults.Flag(a, "bDynamicLight") || (ClassDefaults.Flag(a, "bMovable") && !ClassDefaults.Flag(a, "bStatic"))) &&
        Effect(a) != ActorLightRules.LE_ESpotShadow && ClassDefaults.Number(a, "LightBrightness", 64) > 0;

    private static int Effect(Actor a) =>
        ClassDefaults.IsA(a, "Engine.SunLight") && a.Props["LightEffect"] is null ? ActorLightRules.LE_Sunlight
        : ClassDefaults.IsA(a, "Engine.Spotlight") && a.Props["LightEffect"] is null ? ActorLightRules.LE_Spotlight
        : (int)ClassDefaults.Number(a, "LightEffect", 0);

    private static void Write(int row, Actor a)
    {
        int effect = Effect(a);
        float brightness = (float)ClassDefaults.Number(a, "LightBrightness", 64) *
            ActorLightRules.MeanModulation((int)ClassDefaults.Number(a, "LightType", 1));
        LightRgb c = ActorLightRules.Colour((byte)ClassDefaults.Number(a, "LightHue", 0), (byte)ClassDefaults.Number(a, "LightSaturation", 255), brightness);
        Vector3 facing = Space.DirToGodot(a.Rotation.Axes().Forward);
        if (effect == ActorLightRules.LE_Sunlight)
        {
            float k = ActorLightRules.SunLightGain;
            s_rows.SetPixel(0, row, new Color(-facing.X, -facing.Y, -facing.Z, -1));
            s_rows.SetPixel(1, row, new Color(c.R * k, c.G * k, c.B * k, 1));
            s_rows.SetPixel(2, row, new Color(0, 0, 0, -2));
            return;
        }

        Vector3 at = Space.ToGodot(a.Location);
        float radius = (float)(ActorLightRules.WorldRadius((float)ClassDefaults.Number(a, "LightRadius", 64)) / Space.UnitsPerMetre);
        float g = ActorLightRules.PointLightGain, cosTheta = 1, cosHalfPhi = -2;
        if (ActorLightRules.IsSpot(effect))
        {
            float phi = ActorLightRules.SpotPhi((byte)ClassDefaults.Number(a, "LightCone", 128));
            cosHalfPhi = MathF.Cos(phi * 0.5f);
            cosTheta = MathF.Cos(phi * ActorLightRules.SpotThetaRatio * 0.5f);
        }

        s_rows.SetPixel(0, row, new Color(at.X, at.Y, at.Z, radius));
        s_rows.SetPixel(1, row, new Color(c.R * g, c.G * g, c.B * g, cosTheta));
        s_rows.SetPixel(2, row, new Color(facing.X, facing.Y, facing.Z, cosHalfPhi));
    }
}
