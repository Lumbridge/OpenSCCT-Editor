using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

public partial class InspectorPanel
{
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
