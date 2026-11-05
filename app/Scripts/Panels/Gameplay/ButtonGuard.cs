using Godot;

namespace OpenScctEditor.Panels;

/// <summary>Disables its parent button while a test fails (see <see cref="WorkbenchUi.Guard"/>).</summary>
public sealed partial class ButtonGuard : Node
{
    public BaseButton? Button { get; init; }

    public Func<bool> Enabled { get; init; } = () => true;

    public void Check()
    {
        if (Button is not null)
        {
            Button.Disabled = !Enabled();
        }
    }

    public override void _Process(double delta)
    {
        if (Button?.IsVisibleInTree() == true)
        {
            Check();
        }
    }
}
