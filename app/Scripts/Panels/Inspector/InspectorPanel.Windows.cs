using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Windows;
using OpenScctEditor.Assets;

namespace OpenScctEditor.Panels;

public partial class InspectorPanel
{
    private void WindowSection(Actor owner)
    {
        PanelUi.Header(_sections, "Window");
        WindowFields.Build(_sections, owner.Window!, edit => {
            if (ReferenceEquals(_builtFor, Context.Project)) Context.Execute(WindowCommands.Edit(Context.Project, owner.Id, edit));
        }, Context.SetStatus);
        if (owner.Window!.Binding is { } binding)
        {
            PanelUi.Button(_sections, "Detach", "Keep the current pane and stop following the frame", () => Context.Execute(WindowCommands.Edit(Context.Project, owner.Id, d => d.Binding = null)));
            PanelUi.Button(_sections, "Refit", "Detect this frame's current opening again", () => {
                var n = owner.Rotation.Axes().Forward;
                var openings = WindowFit.Find(Context.Project, AssetService.Instance.Resolver, owner.Location + n * 1024, -n, ignorePane: owner.Id)
                    .Where(o => o.Sources.Any(s => binding.Sources.Contains(s, StringComparer.OrdinalIgnoreCase))).OrderBy(o => (o.Centre - owner.Location).Length).ToList();
                if (openings.Count == 0) { Context.SetStatus("No closed opening found. Check the source frame, or detach the glass."); return; }
                var fitted = WindowCommands.Fit(Context.Project, openings[0]); var next = owner.Clone();
                next.Location = fitted.Location; next.Rotation = fitted.Rotation; next.DrawScale = 1; next.DrawScale3D = Vec3.One; next.PrePivot = Vec3.Zero;
                next.Window!.Outline = fitted.Window!.Outline; next.Window.Width = fitted.Window.Width; next.Window.Height = fitted.Window.Height; next.Window.Binding = fitted.Window.Binding;
                next.Window.FrameCollisionSources = fitted.Window.FrameCollisionSources;
                WindowAssets.Prepare(next); Context.Execute(new ReplaceObjectsCommand("Refit window").Actor(owner.Clone(), next));
            });
        }
    }
}
