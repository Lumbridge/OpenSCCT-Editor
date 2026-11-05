using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// A property's colour as a swatch bar; clicking it (or Enter or Space when it has the focus) opens a colour picker.
/// A dragged colour is reported while it moves (<see cref="Picked"/> with final false) and once more as final when the
/// mouse or a key is let go or the picker closes, so a drag is one undo step. A swatch for objects whose colours differ
/// shows a dash and opens on the first object's colour. The picker edits red, green and blue only. The swatch keeps the
/// focus after the picker closes, so Ctrl+Z undoes the pick rather than typing in another box.
/// </summary>
public partial class ColourSwatch : ColorPickerButton
{
    private bool _mixed;
    private bool _changed;
    private Color _value;
    private bool _valueMixed;
    private string _tooltip = "";
    private string _mixedTooltip = "";

    /// <summary>(colour, final).</summary>
    public event Action<Color, bool>? Picked;

    /// <summary>The picker is about to open (read the values a drag starts from).</summary>
    public event Action? Opening;

    /// <summary>The picker closed, or the swatch left the tree while it was open (the grid was rebuilt).</summary>
    public event Action? Ended;

    /// <summary>A swatch bar that fills its column. <paramref name="mixedTooltip"/> says what a pick does to objects
    /// whose colours differ.</summary>
    public static ColourSwatch Create(string tooltip, string mixedTooltip)
    {
        var s = new ColourSwatch
        {
            EditAlpha = false,
            EditIntensity = false,
            ThemeTypeVariation = "Swatch",
            CustomMinimumSize = new Vector2(EditorTheme.SwatchSize.Y, EditorTheme.SwatchSize.Y),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = FocusModeEnum.All,
            _tooltip = tooltip,
            _mixedTooltip = $"{PanelUi.MixedTooltip}: {mixedTooltip}",
            TooltipText = tooltip,
        };
        s.PickerCreated += s.SetUpPicker;
        s.ColorChanged += c =>
        {
            s._changed = true;
            s.Picked?.Invoke(c, false);
        };
        s.PopupClosed += s.Close;
        s.TreeExiting += () =>
        {
            if (s.Picking)
            {
                s.Picking = false;
                s.Ended?.Invoke();
            }
        };
        return s;
    }

    /// <summary>True while the picker is open (the grid then leaves the colour alone, so a drag is not pulled back to
    /// the stored value).</summary>
    public bool Picking { get; private set; }

    /// <summary>Shows a colour, or a dash for objects that differ. Held back while the picker is open and shown when it
    /// closes.</summary>
    public void ShowColour(Color colour, bool mixed)
    {
        _value = new Color(colour, 1);
        _valueMixed = mixed;
        if (!Picking)
        {
            Apply();
        }
    }

    private void Close()
    {
        if (!Picking)
        {
            return;
        }

        Picking = false;
        Finish();
        // The values that came in while the picker was open (the stored bytes nearest the colour dragged to).
        Apply();
        Ended?.Invoke();
        if (IsVisibleInTree())
        {
            GrabFocus();
        }
    }

    private void Apply()
    {
        Color = _value;
        _mixed = _valueMixed;
        TooltipText = _mixed ? _mixedTooltip : _tooltip;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_mixed)
        {
            return;
        }

        // Over the colour (the first object's): a field-coloured face with the mixed dash, as the number boxes show it.
        StyleBox box = GetThemeStylebox("normal");
        var inner = new Rect2(box.GetOffset(), Size - box.GetMinimumSize());
        DrawRect(inner, EditorTheme.Field);
        Font font = GetThemeFont("font");
        int size = GetThemeFontSize("font_size");
        Vector2 text = font.GetStringSize(PanelUi.Mixed, HorizontalAlignment.Left, -1, size);
        var at = new Vector2((Size.X - text.X) / 2, ((Size.Y - text.Y) / 2) + font.GetAscent(size));
        DrawString(font, at, PanelUi.Mixed, HorizontalAlignment.Left, -1, size, EditorTheme.TextDim);
    }

    /// <summary>Sets up the picker and its popup, made on the first click (so building a row stays cheap).</summary>
    private void SetUpPicker()
    {
        PopupPanel popup = GetPopup();
        popup.AboutToPopup += () =>
        {
            _changed = false;
            Opening?.Invoke();
            Picking = true;
        };
        // The end of a drag in the picker, or of typing a hex code.
        popup.WindowInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: false } or InputEventKey { Pressed: false })
            {
                Finish();
            }
        };
        ColorPicker picker = GetPicker();
        // No screen sampling (it reads the desktop) and no saved presets: the picker edits one property.
        picker.SamplerVisible = false;
        picker.PresetsVisible = false;
        picker.CanAddSwatches = false;
        picker.EditAlpha = false;
        picker.EditIntensity = false;
    }

    private void Finish()
    {
        if (!_changed)
        {
            return;
        }

        _changed = false;
        Picked?.Invoke(Color, true);
    }
}
