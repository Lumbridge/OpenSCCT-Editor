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
    private float _zoom = 1;

    public override void _Ready()
    {
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Drag to orbit. Scroll to zoom. Double-click to frame the complete effect.";
        CustomMinimumSize = new Vector2(0, 120);
        var viewport = new SubViewport
        {
            OwnWorld3D = true,
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
        };
        AddChild(viewport);
        _camera = new Camera3D
        {
            Current = true, Near = 0.001f, Far = 3000, Fov = 60,
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
        Resized += () => { if (IsNodeReady()) Fit(); };
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
            _yaw = 0.8f;
            _pitch = 0.25f;
            Frame();
        }
        else Fit();
        UpdateProcessing();
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true })
        {
            Frame(); AcceptEvent(); return;
        }
        if (input is InputEventMouseMotion move && (move.ButtonMask & MouseButtonMask.Left) != 0)
        {
            _yaw -= move.Relative.X * 0.01f;
            _pitch = Math.Clamp(_pitch + move.Relative.Y * 0.01f, -1.45f, 1.45f);
        }
        else if (input is InputEventMouseButton { Pressed: true } wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            _zoom = Math.Clamp(_zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 0.85f : 1.18f), 0.05f, 20);
        }
        else return;

        Fit();
        AcceptEvent();
    }

    public void Frame()
    {
        _zoom = 1;
        Fit();
    }

    private void Fit()
    {
        if (_scene is null || _camera is null) return;
        var (min, max) = _scene.Bounds;
        _target = (min + max) * 0.5f;
        _distance = 1;
        Aim();
        float tanV = MathF.Tan(Mathf.DegToRad(_camera.Fov) / 2);
        float tanH = tanV * Math.Max(0.05f, Size.X / Math.Max(1, Size.Y));
        Basis basis = _camera.Basis;
        float distance = 0.05f;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            Vector3 p = corner - _target;
            float fit = Math.Max(Math.Abs(p.Dot(basis.X)) / tanH, Math.Abs(p.Dot(basis.Y)) / tanV);
            distance = Math.Max(distance, p.Dot(basis.Z) + fit * 1.18f);
        }
        _distance = distance * _zoom;
        Aim();
    }

    private void Aim()
    {
        Vector3 offset = new(MathF.Cos(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Sin(_yaw) * MathF.Cos(_pitch));
        _camera.Position = _target + offset * _distance;
        _camera.LookAt(_target);
    }

}
