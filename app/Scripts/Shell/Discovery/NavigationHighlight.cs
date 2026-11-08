using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell;

/// <summary>Shared non-intercepting spotlight for search and lessons; follows layout and scrolling.</summary>
[ShellPart(Order = 91)]
public partial class NavigationHighlight : Control, IShellPart
{
    public static NavigationHighlight? Instance { get; private set; }
    private Control? _target;
    private Func<Rect2>? _localRect;
    private bool _dismissOnLeave;
    private bool _entered;
    private double _time;
    public bool Active => _target is not null && IsInstanceValid(_target) && _target.IsInsideTree();
    internal string? TargetPath => Active ? _target!.GetPath().ToString() : null;
    internal Rect2 TargetRect => Active ? Rect() : new Rect2();

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        Name = "NavigationHighlight";
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 1001;
        window.AddChild(this);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        context.SessionReplaced += DiscoveryNavigation.Invalidate;
    }

    public void PointAt(Control control, bool dismissOnLeave = true, Func<Rect2>? localRect = null)
    {
        _target = control;
        _localRect = localRect;
        _dismissOnLeave = dismissOnLeave;
        _entered = false;
        _time = 0;
    }

    public void Clear() { _target = null; _localRect = null; QueueRedraw(); }

    private Rect2 Rect() => _localRect is null ? _target!.GetGlobalRect()
        : _target!.GetGlobalTransform() * _localRect();

    public override void _Process(double delta)
    {
        if (!Active) { if (_target is not null) Clear(); return; }
        _time += delta;
        if (_target!.IsVisibleInTree())
        {
            bool inside = Rect().HasPoint(EditorPointer.Global(this));
            if (_dismissOnLeave && _entered && !inside) Clear();
            else _entered |= inside;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!Active || !_target!.IsVisibleInTree()) return;
        Rect2 rect = GetGlobalTransform().AffineInverse() * Rect();
        // Never outline an off-screen row after its dock was collapsed or scrolled away.
        Rect2 clip = GetGlobalRect();
        for (Node? parent = _target.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is Control { ClipContents: true } c) clip = clip.Intersection(c.GetGlobalRect());
        rect = rect.Intersection(GetGlobalTransform().AffineInverse() * clip);
        if (!rect.HasArea()) return;
        float pulse = .75f + .25f * (float)Math.Sin(_time * Math.PI * 2);
        DrawRect(rect.Grow(3), new Color(EditorTheme.Accent, pulse), false, 3);
        DrawRect(rect.Grow(6), new Color(EditorTheme.Accent, pulse * .3f), false, 2);
    }
}
