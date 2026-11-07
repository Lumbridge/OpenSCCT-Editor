using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Windows;
using OpenScctEditor.Assets;

namespace OpenScctEditor.Panels;

public partial class InspectorPanel
{
    private ColourSwatch? _windowTint;
    private string _windowTintSignature = "";

    private void WindowSection(Actor owner)
    {
        PanelUi.Header(_sections, "Window");
        Color start = default;
        int pickedFrom = 0;
        var tint = WindowFields.Build(_sections, owner.Window!, edit => {
            if (ReferenceEquals(_builtFor, Context.Project)) Context.Execute(WindowCommands.Edit(Context.Project, owner.Id, edit));
        }, Context.SetStatus, tintEdit: (colour, final) =>
        {
            if (!ReferenceEquals(_builtFor, Context.Project) || Context.Project.FindActor(owner.Id)?.Window is not { } design) return;
            if (colour == start)
            {
                while (Context.Session.Commands.Position > pickedFrom && Context.Session.Commands.Undo()) { }
            }
            else if (!final && new Color((float)design.Tint[0], (float)design.Tint[1], (float)design.Tint[2]) != colour)
            {
                Context.Execute(WindowCommands.Edit(Context.Project, owner.Id, d => d.Tint = [colour.R, colour.G, colour.B], "window.tint:" + owner.Id));
            }
            if (final) Context.Session.Commands.EndMerge();
        });
        _windowTint = tint;
        tint.Opening += () =>
        {
            _live.Flush();
            Context.Session.Commands.EndMerge();
            pickedFrom = Context.Session.Commands.Position;
            start = tint.Color;
            _windowTintSignature = Context.Project.FindActor(owner.Id)!.Window!.ToJson();
        };
        tint.Ended += () => { Context.Session.Commands.EndMerge(); _sync.Queue(); };
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
