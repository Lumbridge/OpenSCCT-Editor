using Godot;
using OpenScctEditor.Core;

namespace OpenScctEditor.Panels;

/// <summary>
/// Applies field edits: while typing, a short pause after the last keystroke applies the value (the command merges
/// into the previous one, so a typed value is one undo step); Enter or leaving the field applies at once and ends the
/// merge.
/// </summary>
internal sealed class LiveEdit
{
    private const double Delay = 0.25;

    private readonly Node _owner;
    private readonly EditorContext _context;
    private Action? _pending;
    private int _version;

    public LiveEdit(Node owner, EditorContext context)
    {
        _owner = owner;
        _context = context;
    }

    /// <param name="apply">Executes the edit (it should do nothing when the value is unchanged).</param>
    /// <param name="final">Enter or focus left the field.</param>
    /// <param name="live">False for edits that only apply when final (commands that cannot merge).</param>
    public void Change(Action apply, bool final, bool live = true)
    {
        if (final)
        {
            _pending = null;
            _version++;
            apply();
            _context.Session.Commands.EndMerge();
            return;
        }

        if (!live || !_owner.IsInsideTree())
        {
            return;
        }

        _pending = apply;
        int version = ++_version;
        _owner.GetTree().CreateTimer(Delay).Timeout += () =>
        {
            if (version == _version && _pending is { } a)
            {
                _pending = null;
                a();
            }
        };
    }

    /// <summary>Drops a pending edit (another map was opened: it belongs to the old one).</summary>
    public void Cancel()
    {
        _pending = null;
        _version++;
    }

    /// <summary>Applies a pending edit now (before the fields are rebuilt for another selection).</summary>
    public void Flush()
    {
        Action? pending = _pending;
        _pending = null;
        _version++;
        pending?.Invoke();
        _context.Session.Commands.EndMerge();
    }
}
