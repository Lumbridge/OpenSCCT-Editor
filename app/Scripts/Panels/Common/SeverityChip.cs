using Godot;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Panels;

/// <summary>A severity filter chip (Map Check, Log): the severity's icon, its plural noun and how many there are
/// ("Warnings 3"), the count dim. Made by <see cref="PanelUi.SeverityToggle"/>.</summary>
public sealed partial class SeverityChip : Button
{
    private readonly Func<int> _count;

    public SeverityChip()
        : this(Severity.Info, () => 0)
    {
    }

    public SeverityChip(Severity severity, Func<int> count)
    {
        Severity = severity;
        _count = count;
    }

    public Severity Severity { get; }

    /// <summary>Reads the count again.</summary>
    public void Refresh() => Text = $"{PanelUi.SeverityNoun(Severity)} {_count()}";
}
