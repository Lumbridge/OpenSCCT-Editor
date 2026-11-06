using Godot;
using OpenScct.Editor.Core.Compile;

namespace OpenScctEditor.Viewports;

/// <summary>The game's sky-camera view behind a textured perspective viewport. Geometry stays in the shared world;
/// a second camera draws only the sky layers, at the SkyZoneInfo, with the editing camera's direction and projection.</summary>
public sealed partial class SkyPreviewView : Node
{
    private SubViewport? _viewport;
    private Camera3D? _camera;
    private MeshInstance3D? _backdrop;
    private RenderMode? _mode;

    public void Update(EditorViewport owner, SkyPreviewScene? sky)
    {
        uint layer = VisualLayers.SkyForMode(owner.Mode);
        bool inside = sky?.Contains(owner.Cam.Location) == true;
        uint mask = VisualLayers.ForView(owner.Kind, owner.Mode, Render.ActorIconBatch.Shown);
        if (inside || owner.Kind != ViewKind.Perspective) mask |= layer;
        bool show = sky is not null && layer != 0 && owner.Kind == ViewKind.Perspective && !inside &&
            owner.IsVisibleInTree() && StoreyFilter.Current is null;
        if (show)
        {
            Ensure(owner);
            mask |= VisualLayers.SkyBackdrop;
            _viewport!.Size = owner.Sub.Size;
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            _camera!.CullMask = layer;
            Camera3D main = owner.Cam.Camera;
            _camera.GlobalTransform = new Transform3D(main.GlobalBasis, Space.ToGodot(sky!.Location));
            _camera.KeepAspect = main.KeepAspect;
            _camera.Fov = main.Fov;
            _camera.Near = main.Near;
            _camera.Far = main.Far;
            if (_mode != owner.Mode)
            {
                _mode = owner.Mode;
                _camera.Environment = EditorViewport.MakeEnvironment(owner.Mode);
            }
        }
        else if (_viewport is not null)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }

        if (_backdrop is not null) _backdrop.Visible = show;
        owner.Cam.Camera.CullMask = mask;
    }

    private void Ensure(EditorViewport owner)
    {
        if (_viewport is not null) return;
        _viewport = new SubViewport
        {
            Name = "SkyZonePreview", Size = new Vector2I(16, 16), UseHdr2D = true,
            World3D = owner.Sub.FindWorld3D(), GuiDisableInput = true, HandleInputLocally = false,
        };
        _camera = new Camera3D { Name = "SkyCamera", Current = true };
        _viewport.AddChild(_camera);
        AddChild(_viewport);
        var shader = new Shader { Code = """
            shader_type spatial;
            render_mode unshaded, fog_disabled, cull_disabled, depth_draw_never, skip_vertex_transform;
            uniform sampler2D sky_view : filter_linear, repeat_disable;
            void vertex() {
                #if CURRENT_RENDERER == RENDERER_COMPATIBILITY
                POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
                #else
                POSITION = vec4(VERTEX.xy * 2.0, 0.0, 1.0);
                #endif
            }
            void fragment() { ALBEDO = texture(sky_view, SCREEN_UV).rgb; }
            """ };
        var material = new ShaderMaterial { Shader = shader, RenderPriority = (int)Material.RenderPriorityMin };
        material.SetShaderParameter("sky_view", _viewport.GetTexture());
        _backdrop = new MeshInstance3D
        {
            Name = "SkyBackdrop", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = material,
            Layers = VisualLayers.SkyBackdrop, ExtraCullMargin = 16384,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        owner.Cam.Camera.AddChild(_backdrop);
    }
}
