using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

public partial class InspectorPanel
{
    private void DoorHackSection(Actor owner)
    {
        PanelUi.Header(_sections, "Door hack panel");
        var row = PanelUi.Row(_sections, "Hack time", "Seconds of uninterrupted hacking before this panel opens its linked doors", 100);
        var time = new Godot.SpinBox { MinValue = 1, MaxValue = 600, Step = 1, Value = owner.Props.GetInt("iHackingTime"), UpdateOnTextChanged = false, SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill };
        row.AddChild(time); time.ValueChanged += value =>
        {
            if (ReferenceEquals(_builtFor, Context.Project)) Context.Execute(DoorAccess.HackTime(Context.Project, owner.Id, (int)value));
        };
        var targets = DoorAccess.Targets(Context.Project, owner);
        PanelUi.Caption(_sections, "Linked to " + string.Join(", ", targets.Select(a => a.Id)) + ". Move or rotate this panel freely; its door link is retained.");
        PanelUi.Button(_sections, "Select linked doors", "Select the door leaves controlled by this hack panel", () => Context.Selection.Set(targets.Select(a => ObjectRef.Actor(a.Id))));
    }
    private void DoorSection(Actor owner)
    {
        PanelUi.Header(_sections, "Door");
        DoorFields.Build(_sections, owner.Door!, edit =>
        {
            if (ReferenceEquals(_builtFor, Context.Project)) Context.Execute(DoorCommands.Edit(Context.Project, owner.Id, edit));
        }, Context.SetStatus);
        var row = PanelUi.Toolbar(_sections);
        PanelUi.Button(row, "Show closed", "Show this door at its closed key", () => Context.Execute(MoverEdits.ShowKey(Context.Project, owner.Id, 0)));
        PanelUi.Button(row, "Show open", "Inspect the opening direction and clearance", () => Context.Execute(MoverEdits.ShowKey(Context.Project, owner.Id, 1)));
        PanelUi.Button(row, "Mover", "Preview the movement and inspect event links", () => DockTabs.ShowPanel("Mover"));
        PanelUi.Caption(_sections, "Door settings rebuild its two movement keys. Use Mover for advanced key edits. The frame stays at the closed position.");
    }
}
