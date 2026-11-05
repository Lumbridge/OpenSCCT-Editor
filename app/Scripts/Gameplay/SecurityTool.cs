using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Panels;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The security workbench (Y). Place mode: click a wall to put the chosen device on it, its back to the wall and
/// facing out (a faint copy of the device, and a camera's cone, show before you click). Link mode: drag from a sender (trigger, alarm, laser...) to
/// a receiver; the sender's Event gets the receiver's Tag, or an alarm adds the mover or objective to lock.
/// </summary>
public sealed class SecurityTool : IViewportTool
{
    private readonly ToolManager _tools;
    private readonly GameplayState _state;
    private EditorViewport? _view;
    private Vector2 _mouse;
    private (Vec3 Location, Rot Rotation)? _ghost;
    private string? _linkFrom;
    private string? _linkTo;

    public SecurityTool(ToolManager tools, GameplayState state)
    {
        _tools = tools;
        _state = state;
    }

    public string Id => "security";

    public string Title => "Security Devices";

    public string Shortcut => "Y";

    /// <summary>The device the place mode puts down.</summary>
    public static DeviceSpec Device
    {
        get
        {
            string cls = Shell.AppSettings.GetString("gameplay", "device", SecurityDevices.Placeable[0].Class);
            return SecurityDevices.Placeable.FirstOrDefault(d => d.Class == cls) ?? SecurityDevices.Placeable[0];
        }
        set => Shell.AppSettings.Set("gameplay", "device", value.Class);
    }

    public static bool LinkMode
    {
        get => Shell.AppSettings.GetBool("gameplay", "link_mode", false);
        set => Shell.AppSettings.Set("gameplay", "link_mode", value);
    }

    /// <summary>Builds the collision world in the background, so the first hover does not wait for it.</summary>
    public void Activate() => _state.Prewarm();

    public void Deactivate() => Reset();

    public bool Input(EditorViewport view, InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _view = view;
                _mouse = press.Position;
                if (LinkMode)
                {
                    _linkFrom = ActorAt(view, press.Position);
                    _tools.Context.SetStatus(_linkFrom is null ? "Start the drag on an actor" : $"Linking from {_linkFrom}: drop on the receiver");
                    return true;
                }

                Place(view);
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _linkFrom is not null:
                FinishLink();
                ViewportLayout.Instance?.RedrawAll();
                return true;
            case InputEventMouseMotion motion:
                _view = view;
                _mouse = motion.Position;
                if (_linkFrom is not null)
                {
                    _linkTo = ActorAt(view, motion.Position) is { } to && !string.Equals(to, _linkFrom, StringComparison.OrdinalIgnoreCase) ? to : null;
                }
                else if (!LinkMode)
                {
                    SetGhost(WallSpot(view, motion.Position));
                }

                view.Redraw();
                return _linkFrom is not null;
        }

        return false;
    }

    public void Draw(EditorViewport view, Control canvas)
    {
        if (view != _view)
        {
            return;
        }

        var batch = new OverlayBatch();
        string text = "";
        MapProject p = _tools.Context.Project;
        if (_linkFrom is not null && p.FindActor(_linkFrom) is { } from)
        {
            Vec3 end = _linkTo is not null && p.FindActor(_linkTo) is { } to ? to.Location : view.Cam.PlanePoint(_mouse, from.Location[Math.Max(ViewAxes.DepthAxis(view.Cam.Kind), 0)]);
            batch.Arrow(from.Location, end, LinkOverlay.Fire, 2);
            text = _linkTo is null ? $"{from.Id} → (drop on an actor)" : Wiring.Describe(p, from.Id, _linkTo);
        }
        else if (!LinkMode && _ghost is { } g)
        {
            DeviceSpec spec = Device;
            batch.Dot(g.Location, SecurityOverlay.CameraColour, 5);
            if (spec.Kind == DeviceKind.Camera)
            {
                // The class defaults' cone, range and sweep, as the device will have when placed.
                SecurityOverlay.Camera(batch, SecurityDevices.Camera(new Actor("ghost", spec.Class) { Location = g.Location, Rotation = g.Rotation }), true);
            }
            else
            {
                batch.Arrow(g.Location, g.Location + g.Rotation.Axes().Forward * 48, SecurityOverlay.CameraColour, 2);
            }

            text = $"Click to place: {spec.Title}";
        }

        batch.Draw(view.Cam, canvas);
        if (text.Length > 0)
        {
            ViewTags.Beside(canvas, _mouse, [new TagPart(text, EditorTheme.Text)]);
        }
    }

    public bool Cancel()
    {
        // Only a link drag is pending work; the hover ghost alone lets Escape go on to Select None.
        bool linking = _linkFrom is not null;
        if (linking || _ghost is not null)
        {
            Reset();
            ViewportLayout.Instance?.RedrawAll();
        }

        return linking;
    }

    public void BuildOptions(HBoxContainer bar)
    {
        PanelUi.Segmented(bar, [("Place", "Click a wall to put the device on it"), ("Link", "Drag from a sender to a receiver")], LinkMode ? 1 : 0, i =>
        {
            LinkMode = i == 1;
            Reset();
        });
        OptionButton devices = ToolOptions.Options(bar, "Device to place", 140);
        for (int i = 0; i < SecurityDevices.Placeable.Count; i++)
        {
            devices.AddItem(SecurityDevices.Placeable[i].Title, i);
            if (SecurityDevices.Placeable[i] == Device)
            {
                devices.Select(i);
            }
        }

        devices.ItemSelected += i =>
        {
            Device = SecurityDevices.Placeable[(int)i];
            Reset();
        };
        ToolOptions.Hint(bar, "Place: click a wall", "Link: drag from a sender to a receiver", "Esc cancels");
    }

    /// <summary>Runs a device placement (<see cref="SecurityDevices.Place"/>) and selects the new device.</summary>
    public static void Add(Core.EditorContext ctx, AddObjectsCommand place)
    {
        ctx.Execute(place);
        ctx.Selection.Set(place.Added);
        ctx.SetStatus($"{place.Name}: {place.Added.First().Id}");
    }

    private void Place(EditorViewport view)
    {
        if (WallSpot(view, _mouse, wait: true) is not { } spot)
        {
            _tools.Context.SetStatus("No wall under the cursor");
            return;
        }

        Add(_tools.Context, SecurityDevices.Place(_tools.Context.Project, Device, spot.Location, spot.Rotation));
    }

    /// <summary>Where the device goes for the surface under the cursor. The hover preview never waits for the
    /// collision world (it starts building it); a click does.</summary>
    private (Vec3 Location, Rot Rotation)? WallSpot(EditorViewport view, Vector2 mouse, bool wait = false)
    {
        if ((wait ? _state.World : _state.WorldIfBuilt) is not { } world)
        {
            _state.Prewarm();
            return null;
        }

        return GameplayPicking.Surface(world, view.Cam, mouse) is { } hit ? SecurityDevices.OnWall(Device, hit.Point, hit.Normal) : null;
    }

    private string? ActorAt(EditorViewport view, Vector2 mouse) =>
        Picking.Pick(view.Cam, _tools.Context.Project, mouse) is { Kind: ObjectKind.Actor } r ? r.Id : null;

    private void FinishLink()
    {
        string? from = _linkFrom, to = _linkTo;
        Reset();
        if (from is null || to is null)
        {
            _tools.Context.SetStatus("Link cancelled: drop on another actor");
            return;
        }

        string what = Wiring.Describe(_tools.Context.Project, from, to);
        if (Wiring.Link(_tools.Context.Project, from, to) is { IsEmpty: false } cmd)
        {
            _tools.Context.Execute(cmd);
            _tools.Context.Selection.Set([ObjectRef.Actor(from)]);
            _tools.Context.SetStatus(what);
        }
    }

    /// <summary>Moves the hover preview: the device's spot, and a faint copy of its mesh there in the views.</summary>
    private void SetGhost((Vec3 Location, Rot Rotation)? spot)
    {
        if (spot == _ghost)
        {
            return;
        }

        _ghost = spot;
        ViewportLayout.Instance?.Scene.ShowPlacementGhost(
            spot is { } g ? new Actor("ghost", Device.Class) { Location = g.Location, Rotation = g.Rotation } : null, SecurityOverlay.CameraColour);
    }

    private void Reset()
    {
        SetGhost(null);
        _linkFrom = null;
        _linkTo = null;
    }
}
