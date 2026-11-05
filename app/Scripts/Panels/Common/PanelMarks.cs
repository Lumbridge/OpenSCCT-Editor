using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>How the panels mark a value that differs across the selection, one way for each kind of field: a dim
/// <see cref="PanelUi.Mixed"/> dash in text and drop-downs, the <c>check-mixed</c> box on a checkbox (clicking it sets
/// the flag on every object). The tooltip says "Multiple values". Also the toolbar status caption.</summary>
internal static class PanelMarks
{
    /// <summary>Shows a checkbox as mixed, or as its plain state again.</summary>
    public static void SetMixed(CheckBox box, bool mixed)
    {
        if (mixed)
        {
            box.AddThemeIconOverride("unchecked", Icons.Get("check-mixed", EditorTheme.Accent));
            box.AddThemeIconOverride("unchecked_disabled", Icons.Get("check-mixed", EditorTheme.TextDisabled));
        }
        else
        {
            box.RemoveThemeIconOverride("unchecked");
            box.RemoveThemeIconOverride("unchecked_disabled");
        }
    }

    /// <summary>A text box's mixed state: empty with a dim dash, or no placeholder.</summary>
    public static void SetMixed(LineEdit edit, bool mixed, string placeholder = "")
    {
        edit.PlaceholderText = mixed ? PanelUi.Mixed : placeholder;
        edit.TooltipText = mixed ? PanelUi.MixedTooltip : "";
    }

    /// <summary>Shows a drop-down's choice, or a dim dash when the objects differ (<paramref name="index"/> -1 with
    /// <paramref name="mixed"/>), or <paramref name="none"/> when nothing is chosen.</summary>
    public static void Choose(OptionButton option, int index, bool mixed, string none = "(not set)")
    {
        PanelUi.Choose(option, index, mixed ? PanelUi.Mixed : none);
        if (index < 0)
        {
            option.AddThemeColorOverride("font_color", EditorTheme.TextDim);
        }
        else
        {
            option.RemoveThemeColorOverride("font_color");
        }

        option.TooltipText = mixed ? PanelUi.MixedTooltip : option.Text;
    }

    /// <summary>Makes a <see cref="PanelUi.Caption"/> the status at the end of a toolbar: right-aligned and as wide as its
    /// text, so a filter that fills the row cannot squeeze it out.</summary>
    public static void Status(Label label)
    {
        label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        label.CustomMinimumSize = Vector2.Zero;
    }
}

/// <summary>The brush and surface poly flags as the panels name and order them, so the Inspector and the Surface panel
/// show the same words in the same places.</summary>
internal static class PolyFlagNames
{
    public static readonly (string Name, int Bit)[] All =
    [
        ("Semisolid", PolyFlags.Semisolid), ("Non-solid", PolyFlags.NotSolid), ("Portal", PolyFlags.Portal),
        ("Invisible", PolyFlags.Invisible), ("Masked", PolyFlags.Masked), ("Translucent", PolyFlags.Translucent),
        ("Modulated", PolyFlags.Modulated), ("Two sided", PolyFlags.TwoSided), ("Unlit", PolyFlags.Unlit),
        ("Special lit", PolyFlags.SpecialLit), ("Environment", PolyFlags.Environment), ("Fake backdrop", PolyFlags.FakeBackdrop),
        ("No smooth", PolyFlags.NoSmooth), ("Mirrored", PolyFlags.Mirrored), ("Anti-portal", PolyFlags.AntiPortal),
    ];
}
