using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Shell;

/// <summary>Reveals existing UI without invoking the operation it describes.</summary>
public static class DiscoveryNavigation
{
    public static int Revision { get; private set; }
    public static void Invalidate() { Revision++; NavigationHighlight.Instance?.Clear(); }
    public static IEnumerable<Node> Descendants(Node? root)
    {
        if (root is null) yield break;
        foreach (Node child in root.GetChildren())
        {
            if (child is SubViewport) continue;
            yield return child;
            foreach (Node next in Descendants(child)) yield return next;
        }
    }

    public static EditorPanel? Panel(string name) => Descendants(MainWindow.Instance).OfType<EditorPanel>()
        .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Reveal(Control target, bool lesson = false)
    {
        if (!GodotObject.IsInstanceValid(target) || !target.IsInsideTree()) return;
        int revision = Revision;
        for (Node? owner = target; owner is not null; owner = owner.GetParent())
            if (owner is EditorPanel panel) { DockTabs.ShowPanel(panel.Name); break; }
        DockTabs.Reveal(target);
        for (Node? parent = target.GetParent(); parent is not null; parent = parent.GetParent())
        {
            if (parent is FoldableContainer fold) fold.Folded = false;
            if (parent is MissionsPanel missions) missions.RevealCompletionControls(target);
        }
        EditorLifetime.Defer(() =>
        {
            if (revision != Revision) return;
            if (!GodotObject.IsInstanceValid(target) || !target.IsInsideTree()) return;
            for (Node? parent = target.GetParent(); parent is not null; parent = parent.GetParent())
                if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(target);
            NavigationHighlight.Instance?.PointAt(target, !lesson);
        });
    }

    public static void ShowPanel(string name, string? text = null, bool lesson = false)
    {
        int revision = Revision;
        if (!DockTabs.ShowPanel(name)) return;
        EditorLifetime.Defer(() =>
        {
            if (revision != Revision) return;
            if (Panel(name) is not { } panel) return;
            Control? target = text is null ? panel : FindControl(panel, text);
            Reveal(target ?? panel, lesson);
        });
    }

    public static Control? FindControl(Node root, string text)
    {
        Control? found = Descendants(root).OfType<Control>()
        .FirstOrDefault(c => c.IsVisibleInTree() && Caption(c).Equals(text, StringComparison.OrdinalIgnoreCase))
        ?? Descendants(root).OfType<Control>().FirstOrDefault(c => c.IsVisibleInTree()
            && (Caption(c).Contains(text, StringComparison.OrdinalIgnoreCase) || c.TooltipText.Contains(text, StringComparison.OrdinalIgnoreCase)));
        if (found is Label && found.GetParent() is HBoxContainer row)
            return Descendants(row).OfType<Control>().FirstOrDefault(c => c is BaseButton or LineEdit or SpinBox) ?? found;
        return found;
    }

    public static string Caption(Control c) => c switch
    {
        Button b => b.Text,
        Label l => l.Text,
        LineEdit e => e.PlaceholderText,
        _ => "",
    };

    public static string? ActionId(Control control) => control is ActionButton button ? button.ActionId
        : control.HasMeta("editor_action") ? control.GetMeta("editor_action").AsString() : null;

    public static void ShowAction(EditorAction action, bool lesson = false)
    {
        Control? target = Descendants(MainWindow.Instance).OfType<Control>().FirstOrDefault(b => ActionId(b) == action.Id && b.IsVisibleInTree());
        target ??= Descendants(MainWindow.Instance).OfType<Control>().FirstOrDefault(b => ActionId(b) == action.Id);
        if (target is not null)
        {
            if (!target.IsVisibleInTree() && WorkspaceMode.Simple) WorkspaceMode.Set(false);
            Reveal(target, lesson); return;
        }
        // A lesson keeps its card usable. Search's Show me opens and focuses the real menu command.
        if (!lesson && MenuBarPart.Instance?.RevealAction(action) == true) return;
        if (Descendants(MainWindow.Instance).OfType<MenuBar>().FirstOrDefault() is { } bar) Reveal(bar, lesson);
        EditorContext.Instance.SetStatus($"{action.Title}: {action.Menu.Replace("/", " › ", StringComparison.Ordinal)}"
            + (ShortcutBindings.Text(action) is { } shortcut ? $" · {shortcut}" : "") + ". Open the menu to run this command.");
    }

    public static void ShowGe(string kind, bool lesson = false)
    {
        int revision = Revision;
        if (ToolManager.Instance is not { } tools) return;
        if (WorkspaceMode.Simple) WorkspaceMode.Set(false);
        tools.GeKind = kind.ToUpperInvariant();
        tools.Activate("ge");
        tools.RefreshOptions();
        EditorLifetime.Defer(() =>
        {
            if (revision != Revision) return;
            if (Descendants(MainWindow.Instance).OfType<OptionButton>().FirstOrDefault(b => b.IsVisibleInTree() && b.TooltipText == "Kind of GE to draw") is { } picker)
                Reveal(picker, lesson);
        });
    }
}
