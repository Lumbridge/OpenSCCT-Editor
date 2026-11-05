using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The storey slider at the top right of a view: a slim slider on a view tag, its caption the storey shown ("All" at the
/// top notch). The bottom notch is the lowest storey. All views share one <see cref="StoreyFilter"/>, so moving any
/// slider moves them all. Hidden when the map has fewer than two storeys, and in every view but the active one while
/// all storeys show.
/// </summary>
public sealed partial class StoreySlider : PanelContainer
{
    /// <summary>The slider's width, and its height per notch and at most.</summary>
    private const int SliderWidth = 10, NotchHeight = 12, MaxHeight = 160;

    /// <summary>Storey names longer than this many characters end in "…" (the tooltip has the whole name).</summary>
    private const int CaptionMaxChars = 9;

    private readonly Label _name = new()
    {
        ThemeTypeVariation = "Caption",
        HorizontalAlignment = HorizontalAlignment.Center,
        MouseFilter = MouseFilterEnum.Pass,
    };

    private readonly VSlider _slider = new()
    {
        Step = 1,
        TickCount = 0,
        TicksOnBorders = true,
        FocusMode = FocusModeEnum.None,
        SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
    };

    public StoreySlider()
    {
        ThemeTypeVariation = "ViewTag";
        MouseFilter = MouseFilterEnum.Stop;
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        stack.AddChild(_name);
        stack.AddChild(_slider);
        AddChild(stack);
        _slider.ValueChanged += v => StoreyFilter.Show((int)v == StoreyFilter.List.Count ? -1 : (int)v);
        _slider.TooltipText = "Storey filter: views show only what reaches into the storey (top = every storey) · PgUp and PgDn step through the storeys";
    }

    /// <summary>Whether the slider's view is the active one: the others show it only while a storey is picked.</summary>
    public bool InActiveView
    {
        get => _inActiveView;
        set
        {
            _inActiveView = value;
            Sync();
        }
    }

    private bool _inActiveView;

    public override void _EnterTree()
    {
        StoreyFilter.Changed += Sync;
        Sync();
    }

    public override void _ExitTree() => StoreyFilter.Changed -= Sync;

    private void Sync()
    {
        int n = StoreyFilter.List.Count;
        Visible = n >= 2 && (_inActiveView || StoreyFilter.Active >= 0);
        _slider.MaxValue = n;
        _slider.TickCount = n + 1;
        _slider.CustomMinimumSize = new Vector2(SliderWidth, Math.Min(NotchHeight * (n + 1), MaxHeight));
        _slider.SetValueNoSignal(StoreyFilter.Active < 0 ? n : StoreyFilter.Active);
        string name = StoreyFilter.Current is { } s ? s.Name : "All";
        _name.Text = name.Length <= CaptionMaxChars ? name : name[..(CaptionMaxChars - 1)].TrimEnd() + "…";
        _name.TooltipText = StoreyFilter.Describe();
    }
}
