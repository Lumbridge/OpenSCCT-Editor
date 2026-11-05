using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// The eye and lock buttons of an outliner or layers row. Passive at rest: a visible row's eye and an open padlock are
/// dim, so the names stay the brightest thing in the list; a hidden row's crossed eye and a closed padlock (in the
/// warning colour) stand out. Under the pointer both brighten, so it is clear they can be clicked. The icons are drawn
/// white and tinted per state, and the state is kept on the cell, so <see cref="OutlinerTree"/> can re-tint a row on
/// hover without asking its panel.
/// </summary>
internal static class RowStateButtons
{
    public const int Eye = 0;
    public const int Lock = 1;

    private const int HiddenBit = 1;
    private const int LockedBit = 2;

    /// <summary>Adds the two buttons to a new row (shown as visible and unlocked until <see cref="Set"/>).</summary>
    public static void Add(TreeItem item, int column, string eyeTooltip, string lockTooltip)
    {
        item.AddButton(column, Icon("eye"), Eye, false, eyeTooltip);
        item.AddButton(column, Icon("lock-open"), Lock, false, lockTooltip);
        Set(item, column, hidden: false, locked: false);
    }

    /// <summary>Shows a row's state.</summary>
    public static void Set(TreeItem item, int column, bool hidden, bool locked, bool hot = false)
    {
        item.SetMetadata(column, (hidden ? HiddenBit : 0) | (locked ? LockedBit : 0));
        Apply(item, column, hidden, locked, hot);
    }

    /// <summary>Re-tints a row for the pointer coming or going.</summary>
    public static void Refresh(TreeItem item, int column, bool hot)
    {
        if (column >= item.GetTree().Columns || item.GetButtonCount(column) < 2)
        {
            return;
        }

        int bits = item.GetMetadata(column).VariantType == Variant.Type.Int ? item.GetMetadata(column).AsInt32() : 0;
        Apply(item, column, (bits & HiddenBit) != 0, (bits & LockedBit) != 0, hot);
    }

    private static void Apply(TreeItem item, int column, bool hidden, bool locked, bool hot)
    {
        if (item.GetButtonCount(column) < 2)
        {
            return;
        }

        int eye = item.GetButtonById(column, Eye);
        int padlock = item.GetButtonById(column, Lock);
        item.SetButton(column, eye, Icon(hidden ? "eye-off" : "eye"));
        item.SetButtonColor(column, eye, hot ? EditorTheme.Text : hidden ? EditorTheme.TextDim : EditorTheme.GlyphRest);
        item.SetButton(column, padlock, Icon(locked ? "lock" : "lock-open"));
        item.SetButtonColor(column, padlock, locked ? EditorTheme.Warning : hot ? EditorTheme.TextDim : EditorTheme.GlyphFaint);
    }

    private static Texture2D Icon(string name) => Icons.Get(name, Colors.White)!;
}
