using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>A non-blocking outline points at the real editor control the lesson is teaching.</summary>
[ShellPart(Order = 90)]
public partial class GuideSpotlight : Control, IShellPart
{
    private static Func<Control?>? s_target;
    private double _time;
    public static void Clear() => s_target = null;
    public static void PointAt(Control target) => s_target = () => IsInstanceValid(target) ? target : null;
    public static void PointAtAction(string id) => s_target = () => Descendants(MainWindow.Instance).OfType<ActionButton>()
        .FirstOrDefault(b => b.ActionId == id && b.IsVisibleInTree());

    private static IEnumerable<Node> Descendants(Node? root)
    {
        if (root is null) yield break;
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (Node next in Descendants(child)) yield return next;
        }
    }

    public void Attach(MainWindow window, EditorContext context)
    {
        Name = "TutorialHighlight";
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 1000;
        window.AddChild(this);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        context.SessionReplaced += Clear;
    }

    public override void _Process(double delta) { _time += delta; QueueRedraw(); }
    public override void _Draw()
    {
        if (FirstMapPanel.Instance is not { } guide || !guide.IsVisibleInTree() || s_target?.Invoke() is not { } target || !target.IsVisibleInTree()) return;
        Rect2 rect = GetGlobalTransform().AffineInverse() * target.GetGlobalRect();
        float pulse = .65f + .35f * (float)Math.Sin(_time * Math.PI * 2);
        DrawRect(rect.Grow(EditorTheme.SpaceXs), new Color(EditorTheme.Accent, pulse), false, 3);
        DrawRect(rect.Grow(EditorTheme.SpaceXs + EditorTheme.SpaceXxs), new Color(EditorTheme.Accent, pulse * .3f), false, 2);
    }
}
