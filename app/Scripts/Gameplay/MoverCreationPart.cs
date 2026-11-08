using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>Discoverable mover creation, with working geometry and motion from the first click.</summary>
[ShellPart(Order = 12)]
public sealed class MoverCreationPart : IShellPart
{
    public void Attach(MainWindow window, EditorContext ctx)
    {
        foreach (var template in Enum.GetValues<MoverTemplate>())
        {
            var t = template;
            ctx.AddAction(new EditorAction("gameplay.mover.create." + t.ToString().ToLowerInvariant(), MoverAuthoring.Title(t), "Gameplay/Create Mover", () =>
            {
                Actor a = MoverAuthoring.Create(ctx.Project, t, ctx.PlacementPoint?.Invoke() ?? Vec3.Zero);
                ctx.Execute(new AddObjectsCommand("Create " + MoverAuthoring.Title(t).ToLowerInvariant(), actors: [a]));
                Select(a.Id);
            }) { Description = "Create a visible, solid mover with two stops and sensible behaviour. Edit its motion in the Mover panel." });
        }
        ctx.AddAction(new EditorAction("gameplay.mover.fromSelection", "Make Selection Move", "Gameplay/Movers", () =>
        {
            if (ctx.Selection.Primary is not { } r) return;
            ctx.Execute(MoverAuthoring.Convert(ctx.Project, r, MoverTemplate.Slide)); Select(r.Id);
        }) { Description = "Turn a selected static mesh or additive brush into a mover, keeping its appearance and references",
            IsEnabled = () => ctx.Selection.Count == 1 && ctx.Selection.Primary is { } r && MoverAuthoring.CanConvert(ctx.Project, r) });
        ctx.AddAction(new EditorAction("brush.addMover", "Add Mover", "Brush", () =>
        {
            var brushes = ToolManager.Instance!.Builder.WorldPieces().ToArray();
            if (brushes.Length != 1) { ctx.SetStatus("Choose a single solid builder shape for a mover"); return; }
            brushes[0].Id = ctx.Project.NewId("Mover");
            Actor a = MoverAuthoring.FromBrush(brushes[0], MoverTemplate.Slide);
            ctx.Execute(new AddObjectsCommand("Add mover from builder brush", actors: [a])); Select(a.Id);
        }) { Order = 45, Description = "Make the builder brush into a visible mover, with its own editable motion",
            IsEnabled = () => ToolManager.Instance?.Builder.GestureActive == false });

        void Select(string id)
        {
            ctx.Selection.Set([ObjectRef.Actor(id)]);
            if (ViewOverlays.All.FirstOrDefault(o => o.Id == "movers") is { } overlay) ViewOverlays.Set(overlay, true);
            DockTabs.ShowPanel("Mover");
            ctx.SetStatus("Mover ready. Choose a stop in the Mover panel, then move or rotate it in a view.");
        }
    }
}
