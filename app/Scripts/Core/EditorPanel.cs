using Godot;

namespace OpenScctEditor.Core;

/// <summary>Where a panel docks by default.</summary>
public enum DockSide
{
    Left,
    Right,
    Bottom,

    /// <summary>The workbench column beside the Inspector (Match, Mover, Camera Network...). Until the shell gives it a
    /// dock of its own it shares the right dock.</summary>
    Workbench,
}

/// <summary>Marks an <see cref="EditorPanel"/> subclass the shell creates and docks at startup.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PanelAttribute : Attribute
{
    public PanelAttribute(string title, DockSide side)
    {
        Title = title;
        Side = side;
    }

    public string Title { get; }

    public DockSide Side { get; }

    /// <summary>Tab order within its dock.</summary>
    public int Order { get; init; }
}

/// <summary>
/// Base of every docked panel (outliner, inspector, browsers, Map Check, log...). The shell finds subclasses with
/// [Panel], creates them with a parameterless constructor and calls <see cref="Bind"/>. Panels build their UI in
/// code and listen to <see cref="EditorContext"/> events; they never reach into other panels.
/// </summary>
public abstract partial class EditorPanel : VBoxContainer
{
    protected EditorContext Context { get; private set; } = EditorContext.Instance;

    /// <summary>Called once after the panel is added to the tree.</summary>
    public void Bind(EditorContext context)
    {
        Context = context;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Build();
        context.SessionReplaced += Refresh;
        Refresh();
    }

    /// <summary>Creates the panel's controls.</summary>
    protected abstract void Build();

    /// <summary>Re-reads everything from the context (new map opened, or a full refresh).</summary>
    public virtual void Refresh()
    {
    }
}
