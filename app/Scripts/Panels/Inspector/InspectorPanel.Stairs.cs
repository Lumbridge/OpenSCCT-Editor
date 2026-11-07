using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Commands;
using Godot;
using OpenScct.Editor.Core.Stairs;

namespace OpenScctEditor.Panels;

public partial class InspectorPanel
{
    private void StairSection(Brush owner)
    {
        PanelUi.Header(_sections, "Editable staircase");
        PanelUi.Caption(_sections, "Drop a texture on a tread or riser to update that surface across the entire staircase.");
        var connection = StairGeometry.Generate(owner).Top;
        foreach (int axis in new[] { 0, 1, 2 })
        {
            int coordinate = axis;
            var row = PanelUi.Row(_sections, "Top connection " + "XYZ"[axis], "Fit this staircase to a new upper-floor connection", 140);
            var field = new SpinBox { MinValue = -262144, MaxValue = 262144, Value = connection[axis], UpdateOnTextChanged = false };
            row.AddChild(field);
            field.ValueChanged += v => {
                try {
                    if (!ReferenceEquals(_builtFor, Context.Project) || Context.Project.FindBrush(owner.Id) is not { Stair: not null } current) return;
                    var next = StairFit.Between(current.Id, current.Stair!, current.Location, connection.With(coordinate, v));
                    next.Editor = current.Editor.Clone();
                    Context.Execute(new ReplaceObjectsCommand("Move top stair connection").Brush(current.Clone(), next));
                } catch (ArgumentException ex) { Context.SetStatus(ex.Message); field.SetValueNoSignal(connection[coordinate]); }
            };
        }
        StairFields.Build(_sections, owner.Stair!, change => {
            if (ReferenceEquals(_builtFor, Context.Project)) Context.Execute(StairCommands.Edit(Context.Project, owner.Id, change));
        }, message => Context.SetStatus(message));
        PanelUi.Button(_sections, "Break Apart", "Convert into ordinary brushes and smoothing volumes (undoable)", () => Context.Execute(StairCommands.BreakApart(Context.Project, owner.Id)));
    }
}
