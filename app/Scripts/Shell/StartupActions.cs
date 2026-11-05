using Godot;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>
/// Runs editor actions named on the command line once the window is up: <c>-- --run=edit.selectAll,edit.duplicate</c>.
/// Each action's result goes to the log, so scripts can drive the editor headless the same way the menus do.
/// </summary>
[ShellPart(Order = 100)]
public sealed class StartupActions : IShellPart
{
    public void Attach(MainWindow window, EditorContext context)
    {
        string? list = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--run=", StringComparison.Ordinal))?["--run=".Length..];
        if (string.IsNullOrWhiteSpace(list))
        {
            return;
        }

        string[] ids = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Wait a moment so the start document has loaded and the views have their sizes.
        var timer = new Godot.Timer { WaitTime = 0.5, OneShot = true, Autostart = true };
        timer.Timeout += () =>
        {
            // The status line is how most actions report what they did; it goes into each result line.
            string status = "";
            context.StatusChanged += text => status = text;
            foreach (string id in ids)
            {
                EditorAction? action = context.FindAction(id);
                if (action is null)
                {
                    context.Log($"--run: no action '{id}'", Severity.Error);
                }
                else if (!action.IsEnabled())
                {
                    context.Log($"--run: {id} is disabled", Severity.Warning);
                }
                else
                {
                    status = "";
                    action.Run();
                    context.Log($"--run: {id} done ({context.Project.Brushes.Count} brushes, {context.Project.Actors.Count} actors, " +
                        $"{context.Project.Ges.Count} GEs, {context.Selection.Count} selected){(status.Length > 0 ? ": " + status : "")}");
                }
            }

            timer.QueueFree();
        };
        window.AddChild(timer);
    }
}
