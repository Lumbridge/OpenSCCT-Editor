using Godot;
using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>
/// The one place keyboard shortcuts run: every <see cref="EditorAction"/> with a shortcut (its default, or the user's
/// own binding from <see cref="ShortcutBindings"/>) is matched here, after text fields and viewports had their chance at the key. While a text field has focus only
/// Ctrl/Alt chords and the function keys fire, so typing a digit, a letter or Delete in a panel never edits the map.
/// Clashing shortcuts are logged once every part has registered its actions.
/// </summary>
[ShellPart(Order = 50)]
public sealed partial class ShortcutRouter : Node, IShellPart
{
    private static readonly HashSet<string> s_repeatable = new(StringComparer.OrdinalIgnoreCase) { "edit.undo", "edit.redo", "edit.redoAlt" };
    private readonly Dictionary<string, KeyChord?> _parsed = new(StringComparer.Ordinal);
    private EditorContext _ctx = null!;

    /// <summary>When true (a viewport is flying the camera), no shortcut runs.</summary>
    public static bool Suspended { get; set; }

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        Name = "ShortcutRouter";
        window.AddChild(this);
        // Parts after this one and the panels add actions too: report once everything is attached.
        EditorLifetime.Defer(ReportConflicts);
    }

    public override void _ShortcutInput(InputEvent @event)
    {
        if (Suspended || OpenScctEditor.Panels.MissionActorPicker.IsOpen || @event is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        if (IsTyping() && !key.IsCommandOrControlPressed() && !key.AltPressed && key.Keycode is < Key.F1 or > Key.F12)
        {
            return;
        }

        if (Animation.AnimationWorkspacePart.Active)
        {
            foreach (EditorAction global in _ctx.Actions.Where(a => a.Id is "view.commandPalette" or "help.shortcuts"))
                if (Chord(global) is { } chord && chord.Matches(key) && global.IsEnabled())
                { global.Run(); GetViewport().SetInputAsHandled(); return; }
            Animation.AnimationWorkspacePart.Studio!.HandleShortcut(key);
            GetViewport().SetInputAsHandled();
            return;
        }

        foreach (EditorAction action in _ctx.Actions.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            if (Chord(action) is not { } chord || !chord.Matches(key))
            {
                continue;
            }

            if (key.Echo && !s_repeatable.Contains(action.Id))
            {
                GetViewport().SetInputAsHandled();
                return;
            }

            if (action.IsEnabled())
            {
                action.Run();
            }

            GetViewport().SetInputAsHandled();
            return;
        }
    }

    /// <summary>True while an editable text field has the keyboard (a LineEdit, a TextEdit or a SpinBox).</summary>
    private bool IsTyping() => GetViewport().GuiGetFocusOwner() switch
    {
        LineEdit line => line.Editable,
        TextEdit text => text.Editable,
        SpinBox spin => spin.Editable,
        _ => false,
    };

    /// <summary>The parsed shortcut of an action (cached by its text).</summary>
    public KeyChord? Chord(EditorAction action)
    {
        if (ShortcutBindings.Of(action) is not { } text)
        {
            return null;
        }

        if (!_parsed.TryGetValue(text, out KeyChord? chord))
        {
            chord = KeyChord.Parse(text);
            _parsed[text] = chord;
        }

        return chord;
    }

    /// <summary>Logs actions that share a shortcut (the first by id wins), so clashes show up at startup.</summary>
    private void ReportConflicts()
    {
        foreach (IGrouping<KeyChord, EditorAction> group in _ctx.Actions
                     .Where(a => Chord(a) is not null)
                     .GroupBy(a => Chord(a)!.Value)
                     .Where(g => g.Count() > 1))
        {
            _ctx.Log($"Shortcut {group.Key} is used by {string.Join(", ", group.Select(a => a.Id).Order(StringComparer.Ordinal))}");
        }
    }
}
