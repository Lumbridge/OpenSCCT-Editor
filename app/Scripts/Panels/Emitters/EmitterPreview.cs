using Godot;
using OpenScct.Editor.Core.Emitters;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Panels;

/// <summary>Orbitable particle library preview, sharing the placed map effect renderer.</summary>
public partial class EmitterPreview : SubViewportContainer
{
    private ParticleEffect _scene = null!;
    private Camera3D _camera = null!;
    private string? _effect;
    private Vector3 _target;
    private float _distance = 4;
    private float _yaw = 0.8f;
    private float _pitch = 0.25f;

    public override void _Ready()
    {
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Drag to orbit. Scroll to zoom.";
        CustomMinimumSize = new Vector2(0, 166);
        var viewport = new SubViewport
        {
            OwnWorld3D = true,
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
        };
        AddChild(viewport);
        _camera = new Camera3D
        {
            Current = true, Near = 0.02f, Far = 600, Fov = 60,
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.08f, 0.09f, 0.11f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White,
                AmbientLightEnergy = 1,
            },
        };
        viewport.AddChild(_camera);
        _scene = new ParticleEffect();
        viewport.AddChild(_scene);
        VisibilityChanged += UpdateProcessing;
        UpdateProcessing();
        Aim();
    }

    private void UpdateProcessing() => _scene.SetProcess(_effect is not null && IsVisibleInTree());

    public void Play(EmitterPreset? preset)
    {
        if (!IsNodeReady()) return;
        bool frame = _effect != preset?.Id;
        _effect = preset?.Id;
        _scene.Configure(preset?.Actors ?? [], preset?.Mode.Equals("triggered", StringComparison.OrdinalIgnoreCase) == true, () => _camera, floor: true);
        if (frame && preset is not null)
        {
            var (min, max) = _scene.Bounds;
            _target = (min + max) * 0.4f;
            _distance = Math.Clamp((max - min).Length() * 0.5f, 1.2f, 150);
            _yaw = 0.8f;
            _pitch = 0.25f;
            Aim();
        }
        UpdateProcessing();
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseMotion move && (move.ButtonMask & MouseButtonMask.Left) != 0)
        {
            _yaw -= move.Relative.X * 0.01f;
            _pitch = Math.Clamp(_pitch + move.Relative.Y * 0.01f, -1.45f, 1.45f);
        }
        else if (input is InputEventMouseButton { Pressed: true } wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            _distance = Math.Clamp(_distance * (wheel.ButtonIndex == MouseButton.WheelUp ? 0.85f : 1.18f), 0.25f, 300);
        }
        else return;

        Aim();
        AcceptEvent();
    }

    private void Aim()
    {
        Vector3 offset = new(MathF.Cos(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Sin(_yaw) * MathF.Cos(_pitch));
        _camera.Position = _target + offset * _distance;
        _camera.LookAt(_target);
    }

}
