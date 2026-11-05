using Godot;

namespace OpenScctEditor.Shell;

/// <summary>
/// The tab a dock shows for one panel. It never hands the dock the panel's minimum size, so a dock keeps the size its
/// splitter gives it whichever tab is open: switching tabs never moves the views, and a tall panel (two stacked in the
/// right column) can never push the status bar out of the window. A panel larger than its dock is clipped; panels
/// scroll, shrink, wrap or end their text in "…" to fit. The window's bars hold their rows the same way (keeping
/// their height), so a long tool options row is clipped instead of widening the window past the screen.
/// </summary>
public sealed partial class PanelHost : Container
{
    private readonly bool _keepHeight;

    /// <param name="panel">The panel (or bar row) to hold.</param>
    /// <param name="keepHeight">Hand on the child's minimum height (a bar row: the bar stays as tall as its
    /// controls) while still never its width.</param>
    public PanelHost(Control panel, bool keepHeight = false)
    {
        Name = panel.Name;
        _keepHeight = keepHeight;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddChild(panel);
    }

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public PanelHost()
    {
    }

    /// <summary>No width: the dock's own minimum (and its splitter) sizes it, never the panel inside. A bar row's
    /// height only when asked to keep it.</summary>
    public override Vector2 _GetMinimumSize()
    {
        float height = 0;
        if (_keepHeight)
        {
            foreach (Node child in GetChildren())
            {
                if (child is Control { Visible: true } c)
                {
                    height = Math.Max(height, c.GetCombinedMinimumSize().Y);
                }
            }
        }

        return new Vector2(0, height);
    }

    public override void _Notification(int what)
    {
        if (what != NotificationSortChildren)
        {
            return;
        }

        foreach (Node child in GetChildren())
        {
            if (child is Control { Visible: true } c)
            {
                FitChildInRect(c, new Rect2(Vector2.Zero, Size));
            }
        }
    }
}
