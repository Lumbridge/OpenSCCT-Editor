using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Shell;

/// <summary>
/// The toolbar: file and undo buttons, the tools, snapping (grid, rotation, neighbour), CSG Add/Subtract, the view layout, then
/// Build, Check and Play (the primary action) at the far end; under it the active tool's options and the typed
/// transform values. Every button is an editor
/// action, so its state follows the action and a missing action simply hides its button. Buttons show
/// <see cref="ToolbarIcons"/>; their tooltips give the action's name and shortcut.
/// </summary>
[ShellPart(Order = 70)]
public sealed class ToolbarPart : IShellPart
{
    /// <summary>The left part of a snap control (EditorTheme.Shell.cs).</summary>
    public const string SnapToggleStyle = "SnapToggle";

    /// <summary>The right part of a snap control: the size drop-down.</summary>
    public const string SnapMenuStyle = "SnapMenu";

    private readonly List<ActionButton> _buttons = new();
    private readonly List<ActionMenuButton> _menus = new();

    public void Attach(MainWindow window, EditorContext context)
    {
        HBoxContainer row = window.ToolRow;
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);

        ActionButton Button(string id, string? label = null, string variation = "ToolButton", Texture2D? icon = null, Container? into = null)
        {
            var b = new ActionButton(context, id, icon ?? ToolbarIcons.For(id), label, variation);
            _buttons.Add(b);
            (into ?? row).AddChild(b);
            return b;
        }

        void Menu(string path, string prefix, string variation = "ToolMenu", Container? into = null, string? labelGroup = null,
            Func<string>? fallback = null)
        {
            var m = new ActionMenuButton(context, path, prefix) { ThemeTypeVariation = variation, LabelGroup = labelGroup, Fallback = fallback };
            _menus.Add(m);
            if (into is null)
            {
                // A borderless drop-down hugs its text (the chevron right after it) in a slot as wide as its longest
                // choice, so the toolbar still never shifts.
                var slot = new HBoxContainer { Name = path.Replace('/', '_') + "Slot" };
                row.AddChild(slot);
                slot.AddChild(m);
                m.Reserve = slot;
                m.Refresh();
            }
            else
            {
                into.AddChild(m);
            }
        }

        // A snap toggle and its size as one control: [magnet | Grid 16 v].
        void Snap(string toggle, string sizes, string prefix, string? labelGroup = null, Func<string>? fallback = null)
        {
            var strip = new HBoxContainer { Name = toggle };
            strip.AddThemeConstantOverride("separation", 0);
            row.AddChild(strip);
            Button(toggle, variation: SnapToggleStyle, into: strip);
            Menu(sizes, prefix, SnapMenuStyle, strip, labelGroup, fallback);
        }

        void Gap() => row.AddChild(new VSeparator());

        Button("file.new");
        Button("file.open");
        Button("file.save");
        Gap();
        Button("edit.undo");
        Button("edit.redo");
        Gap();
        foreach (string tool in new[] { "select", "move", "rotate", "scale", "place", "brush", "ge", "conveyor", "face", "vertex", "clip", "measure", "security" })
        {
            Button("tools." + tool);
        }

        Gap();
        Snap("view.grid.snap", "View/Grid Size", "Grid ");
        row.AddChild(new Control { CustomMinimumSize = new Vector2(EditorTheme.SpaceXs, 0) });
        Snap("view.rotsnap", "View/Rotation Snap Size", "");
        row.AddChild(new Control { CustomMinimumSize = new Vector2(EditorTheme.SpaceXs, 0) });
        // Neighbour snap: [touching squares | 8 px v], the drop-down also holding what it applies to.
        Snap("view.neighbourSnap", ToolManager.NeighbourMenu, "", "view.nsnap.dist",
            () => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ToolManager.Instance?.NeighbourSnapPx ?? 8:0.#} px"));
        Gap();
        Button("brush.add");
        Button("brush.subtract");
        Gap();
        Menu("View/Layout", "");
        Gap();
        Button("build.build", "Build");
        Button("build.check", "Check");
        row.AddChild(new Control { CustomMinimumSize = new Vector2(EditorTheme.SpaceXs, 0) });
        Button("build.play", "Play", "PrimaryButton", Icons.Get("play", EditorTheme.TextOnAccent));

        var options = new HBoxContainer { Name = "ToolOptions", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        options.AddThemeConstantOverride("separation", 4);
        window.OptionsRow.AddChild(options);
        if (ToolManager.Instance is { } tools)
        {
            tools.SetOptionsBar(options);
            window.OptionsRow.AddChild(new TransformBar(tools));
            tools.Changed += Refresh;
        }

        context.ProjectChanged += _ => Refresh();
        context.SelectionChanged += Refresh;
        context.SessionReplaced += Refresh;
        BackgroundJobs.Changed += Refresh;

        // Enabled states can also change without an event (clipboard, files on disk): a slow tick catches those.
        var tick = new Godot.Timer { WaitTime = 0.5, Autostart = true };
        tick.Timeout += Refresh;
        window.AddChild(tick);
    }

    private void Refresh()
    {
        foreach (ActionButton b in _buttons)
        {
            b.Refresh();
        }

        foreach (ActionMenuButton m in _menus)
        {
            m.Refresh();
        }
    }
}
