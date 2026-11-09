using Godot;
using OpenScct.Editor.Core.Geometry;
using OpenScctEditor.Assets;

namespace OpenScctEditor.Viewports.Render;

/// <summary>
/// The world's materials, cut to the storey shown in every view. The world is drawn as whole meshes (an imported map's
/// baked world, the CSG preview, the Lit Preview's bake), so the storey filter cannot hide it object by object, and the
/// whole world stays in the CSG and the light bake so the storey shown is lit as in the game. These shaders drop what
/// lies above or below the storey, and ceilings seen from above (back faces that face down), so the floor is visible
/// from the 3D, Front and Side views as well as the Top view. Each material copies the look of the view material it
/// stands for: shading, sides, transparency, texture and its filter, albedo colour, vertex colours (sRGB or linear) and
/// a baked lightmap. The range is one global shader parameter, so a storey change redraws without touching any
/// material or baking again.
/// </summary>
public static class StoreyClip
{
    private const string Parameter = "storey_clip";

    /// <summary>The shader parameter that slides the texture (UV), as a StandardMaterial3D's UV1 offset does.</summary>
    public const string UvOffset = "uv_offset";

    /// <summary>The shader parameter holding the texture.</summary>
    public const string Texture = "albedo_texture";

    /// <summary>A storey's open top, in metres: above anything a map holds.</summary>
    private const float OpenTop = 1e6f;

    private static readonly Dictionary<StandardMaterial3D, ShaderMaterial> s_materials = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, Shader> s_shaders = new(StringComparer.Ordinal);
    private static bool s_registered;

    /// <summary>Cuts the world to <paramref name="storey"/>, or shows it whole for null.</summary>
    public static void Show(Storey? storey)
    {
        Register();
        RenderingServer.GlobalShaderParameterSet(Parameter, storey is null
            ? Vector4.Zero
            : new Vector4((float)(storey.Bottom / Space.UnitsPerMetre), double.IsFinite(storey.Top) ? (float)(storey.Top / Space.UnitsPerMetre) : OpenTop, 1, 0));
    }

    /// <summary>
    /// The cut version of a view material. <paramref name="textureFile"/> is the image it loads in the background (the
    /// copy takes it when it arrives, as the source does), or null.
    /// </summary>
    public static ShaderMaterial Of(StandardMaterial3D source, string? textureFile = null)
    {
        if (s_materials.TryGetValue(source, out ShaderMaterial? made))
        {
            return made;
        }

        ShaderMaterial material = Make(source, textureFile, null);
        s_materials[source] = material;
        return material;
    }

    /// <summary>The cut version of a material made for one bake pass (the Lightmap Density view's). Not kept.</summary>
    public static ShaderMaterial Fresh(StandardMaterial3D source) => Make(source, null, null);

    /// <summary>
    /// The cut version of a Lit Preview surface: <paramref name="unlit"/>'s texture times the lightmap
    /// <paramref name="atlas"/> on UV2, scaled by <paramref name="scale"/> as an albedo colour. Not kept: each bake pass
    /// makes its own.
    /// </summary>
    public static ShaderMaterial Lightmapped(StandardMaterial3D unlit, string? textureFile, Texture2D atlas, Color scale)
    {
        ShaderMaterial material = Make(unlit, textureFile, atlas);
        material.SetShaderParameter("lightmap_scale", scale);
        return material;
    }

    private static ShaderMaterial Make(StandardMaterial3D source, string? textureFile, Texture2D? lightmap)
    {
        Register();
        if (source.HasMeta("authored_json"))
        {
            var definition = OpenScct.Content.Materials.AuthoredMaterial.From(System.Text.Json.Nodes.JsonNode.Parse(source.GetMeta("authored_json").AsString()))!;
            definition.Surface = source.Transparency == BaseMaterial3D.TransparencyEnum.Alpha ? "translucent" : source.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? "masked" : "opaque";
            definition.TwoSided |= source.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
            string root = source.GetMeta("authored_root").AsString();
            var loaded = new Dictionary<string, Texture2D>();
            ShaderMaterial? authoredMaterial = null;
            authoredMaterial = AuthoredPreview.Make(definition, _ => null, lightmap, source.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded, source.VertexColorUseAsAlbedo, authoredTexture: (slot, binding) =>
            {
                if (loaded.TryGetValue(slot, out Texture2D? known)) return known;
                AuthoredTextures.Request(Path.Combine(root, binding.File), slot, binding, definition.Animation, image =>
                {
                    loaded[slot] = image;
                    if (authoredMaterial is not null) OpenScctGame.Render.Materials.AuthoredMaterialBinding.Apply(authoredMaterial, definition, _ => null, (s, _) => loaded.GetValueOrDefault(s));
                });
                return loaded.GetValueOrDefault(slot);
            });
            return authoredMaterial;
        }
        Texture2D? texture = source.AlbedoTexture;
        bool textured = texture is not null || textureFile is not null;
        var material = new ShaderMaterial { Shader = ShaderFor(source, textured, lightmap is not null) };
        material.SetShaderParameter("albedo_color", source.AlbedoColor);
        if (texture is not null)
        {
            material.SetShaderParameter("albedo_texture", texture);
        }

        if (textureFile is not null)
        {
            TextureCache.Request(textureFile, tex => material.SetShaderParameter("albedo_texture", tex));
        }

        if (lightmap is not null)
        {
            material.SetShaderParameter("lightmap", lightmap);
        }

        return material;
    }

    /// <summary>Drops the copies (with <see cref="ViewMaterials.Clear"/>, when the project's assets change).</summary>
    public static void Clear() { s_materials.Clear(); AuthoredTextures.Clear(); }

    /// <summary>Adds the global shader parameter, once per run, before any shader that reads it is made.</summary>
    internal static void Register()
    {
        if (s_registered)
        {
            return;
        }

        // Once per run: the parameter is not in project.godot, and listing the parameters is an editor-only call.
        s_registered = true;
        RenderingServer.GlobalShaderParameterAdd(Parameter, RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero);
    }

    private static Shader ShaderFor(StandardMaterial3D m, bool textured, bool lightmapped)
    {
        bool unshaded = m.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded;
        bool twoSided = m.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
        BaseMaterial3D.TransparencyEnum alpha = m.Transparency;
        bool vertexColour = m.VertexColorUseAsAlbedo;
        bool srgbVertex = vertexColour && m.VertexColorIsSrgb;
        bool nearest = m.TextureFilter is BaseMaterial3D.TextureFilterEnum.Nearest
            or BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
        string key = $"{unshaded}|{twoSided}|{alpha}|{vertexColour}|{srgbVertex}|{nearest}|{textured}|{lightmapped}";
        if (s_shaders.TryGetValue(key, out Shader? shader))
        {
            return shader;
        }

        var modes = new List<string> { twoSided ? "cull_disabled" : "cull_back" };
        if (unshaded)
        {
            modes.Add("unshaded");
        }

        if (alpha == BaseMaterial3D.TransparencyEnum.Alpha)
        {
            modes.Add("blend_mix");
            modes.Add("depth_draw_opaque");
        }

        string colour = (textured ? "texture(albedo_texture, UV + uv_offset)" : "vec4(1.0)") + " * albedo_color" + (vertexColour ? " * COLOR" : "");
        // As a StandardMaterial3D with VertexColorIsSrgb: the vertex colours are decoded to linear.
        string srgbCode = srgbVertex
            ? "if (!OUTPUT_IS_SRGB) {\n        COLOR.rgb = mix(pow((COLOR.rgb + vec3(0.055)) * (1.0 / (1.0 + 0.055)), vec3(2.4)), " +
              "COLOR.rgb * (1.0 / 12.92), lessThan(COLOR.rgb, vec3(0.04045)));\n    }"
            : "";
        string filter = nearest ? "filter_nearest" : "filter_linear_mipmap_anisotropic";
        // As a StandardMaterial3D's albedo colour and multiplied detail texture on UV2 (LitPreviewSurfaces).
        string lightmapCode = lightmapped
            ? "vec4 lm = texture(lightmap, UV2);\n    c.rgb *= lightmap_scale.rgb;\n    c.rgb = mix(c.rgb, c.rgb * lm.rgb, lm.a);"
            : "";
        string alphaCode = alpha switch
        {
            BaseMaterial3D.TransparencyEnum.AlphaScissor => "ALPHA = c.a;\n    ALPHA_SCISSOR_THRESHOLD = 0.5;",
            BaseMaterial3D.TransparencyEnum.Alpha => "ALPHA = c.a;",
            _ => "",
        };
        shader = new Shader
        {
            Code = $$"""
                shader_type spatial;
                render_mode {{string.Join(", ", modes)}};

                // x, y: the storey's bottom and top (Godot Y, metres); z: 1 while a storey is shown.
                global uniform vec4 storey_clip;
                uniform sampler2D albedo_texture : source_color, {{filter}}, repeat_enable;
                uniform vec4 albedo_color : source_color = vec4(1.0);
                uniform sampler2D lightmap : source_color, filter_linear, repeat_disable;
                uniform vec4 lightmap_scale : source_color = vec4(1.0);
                // A conveyor sliding the texture (ConveyorLook), as a StandardMaterial3D's UV1 offset.
                uniform vec2 uv_offset = vec2(0.0);
                varying float world_y;
                varying float world_normal_y;

                void vertex() {
                    world_y = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
                    world_normal_y = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz).y;
                    {{srgbCode}}
                }

                void fragment() {
                    if (storey_clip.z > 0.5 && (world_y < storey_clip.x || world_y > storey_clip.y || (!FRONT_FACING && world_normal_y < -0.7))) {
                        discard;
                    }

                    vec4 c = {{colour}};
                    {{lightmapCode}}
                    ALBEDO = c.rgb;
                    ROUGHNESS = 1.0;
                    SPECULAR = 0.1;
                    {{alphaCode}}
                }
                """,
        };
        s_shaders[key] = shader;
        return shader;
    }
}
