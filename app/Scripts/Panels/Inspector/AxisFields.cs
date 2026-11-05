using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>A row of number boxes (X Y Z, Pitch Yaw Roll, R G B A), or one plain number box. A blank box with a dash
/// means the selected objects differ there. <see cref="Edited"/> reports one component at a time, so multi-edit
/// changes only that component, and only once the box was typed in: focusing a box and leaving it never writes the
/// rounded display value back. A value too long for its box drops decimals to fit, in amber and with a leading ≈ when
/// there is room; the tooltip and a focused box show it whole.</summary>
public partial class AxisFields : HBoxContainer
{
    private readonly List<LineEdit> _boxes = new();
    private readonly List<bool> _typed = new();
    private readonly List<double?> _values = new();
    private readonly List<string> _names = new();
    private bool _setting;

    /// <summary>(component, value, final): final on Enter or leaving the box, otherwise while typing.</summary>
    public event Action<int, double, bool>? Edited;

    /// <summary>Boxes with a coloured axis letter in front of each, or with <paramref name="words"/> the whole name in
    /// the label colour ("From", "To").</summary>
    public static AxisFields Create(IReadOnlyList<string> axes, bool words = false) => Make(axes, labels: true, words);

    /// <summary>A single number box without an axis letter.</summary>
    public static AxisFields Number(string tooltip) => Make([tooltip], labels: false, words: false);

    private static AxisFields Make(IReadOnlyList<string> axes, bool labels, bool words)
    {
        var f = new AxisFields { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        f.AddThemeConstantOverride("separation", 2);
        for (int i = 0; i < axes.Count; i++)
        {
            int axis = i;
            if (labels)
            {
                var label = new Label { Text = words ? axes[i] : axes[i][..1], TooltipText = axes[i], MouseFilter = MouseFilterEnum.Pass };
                label.AddThemeColorOverride("font_color", AxisColour(axes[i]));
                f.AddChild(label);
            }

            var box = new LineEdit
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                // No floor: the boxes share the row, so three of them fit a narrow dock.
                CustomMinimumSize = Vector2.Zero,
                SelectAllOnFocus = true,
                TooltipText = axes[i],
            };
            box.TextChanged += t => f.Report(axis, t, final: false);
            box.TextSubmitted += t => f.Report(axis, t, final: true);
            box.FocusEntered += () => f.ShowWhole(axis);
            box.FocusExited += () =>
            {
                f.Report(axis, box.Text, final: true);
                f.Show(axis);
            };
            box.Resized += () => f.Show(axis);
            f._boxes.Add(box);
            f._typed.Add(false);
            f._values.Add(null);
            f._names.Add(axes[i]);
            f.AddChild(box);
        }

        return f;
    }

    public bool HasFocusInside => _boxes.Any(b => b.HasFocus());

    public bool Editable
    {
        set
        {
            foreach (LineEdit b in _boxes)
            {
                b.Editable = value;
            }
        }
    }

    /// <summary>Shows values; null marks a component that differs across the selection. A box being typed in keeps
    /// its text.</summary>
    public void SetValues(IReadOnlyList<double?> values)
    {
        _setting = true;
        for (int i = 0; i < _boxes.Count && i < values.Count; i++)
        {
            LineEdit box = _boxes[i];
            if (box.HasFocus())
            {
                continue;
            }

            _typed[i] = false;
            _values[i] = values[i];
            Show(i);
        }

        _setting = false;
    }

    /// <summary>Shows a component's value in its box, with as many decimals as fit.</summary>
    private void Show(int axis)
    {
        LineEdit box = _boxes[axis];
        if (box.HasFocus())
        {
            return;
        }

        bool setting = _setting;
        _setting = true;
        if (_values[axis] is not { } v)
        {
            box.Text = "";
            box.PlaceholderText = "—";
            box.TooltipText = _names[axis];
        }
        else
        {
            string whole = PanelUi.Num(v);
            string shown = Fitted(box, v, whole);
            bool rounded = shown != whole;
            box.Text = shown;
            box.PlaceholderText = "";
            box.TooltipText = rounded ? $"{_names[axis]}: {whole} (rounded to fit the box)" : $"{_names[axis]}: {whole}";
            // Amber says the number is rounded even when there is no room for the ≈.
            if (rounded)
            {
                box.AddThemeColorOverride("font_color", EditorTheme.Warning);
            }
            else
            {
                box.RemoveThemeColorOverride("font_color");
            }
        }

        _setting = setting;
    }

    /// <summary>Typing starts from the whole value, not the rounded one shown.</summary>
    private void ShowWhole(int axis)
    {
        if (_values[axis] is { } v && !_typed[axis])
        {
            bool setting = _setting;
            _setting = true;
            _boxes[axis].Text = PanelUi.Num(v);
            _boxes[axis].RemoveThemeColorOverride("font_color");
            _boxes[axis].SelectAll();
            _setting = setting;
        }
    }

    /// <summary>The value as it fits the box: whole when it can, else with fewer decimals and a leading ≈, so a rounded
    /// number never passes for the exact one; a whole number with no room for the ≈ goes bare (the caller colours it).</summary>
    private static string Fitted(LineEdit box, double v, string whole)
    {
        float room = box.Size.X - box.GetThemeStylebox("normal").GetMinimumSize().X;
        Font font = box.GetThemeFont("font");
        int size = box.GetThemeFontSize("font_size");
        bool Fits(string text) => font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X <= room;
        if (room <= 0 || Fits(whole))
        {
            return whole;
        }

        string rounded = whole;
        for (int decimals = 3; decimals >= 0; decimals--)
        {
            rounded = v.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), System.Globalization.CultureInfo.InvariantCulture);
            if (rounded != whole && Fits("≈" + rounded))
            {
                return "≈" + rounded;
            }
        }

        return rounded;
    }

    private void Report(int axis, string text, bool final)
    {
        if (_setting)
        {
            return;
        }

        // Only typing makes an edit: the shown text may be a rounded value.
        if (!final)
        {
            _typed[axis] = true;
        }
        else if (!_typed[axis])
        {
            return;
        }
        else
        {
            _typed[axis] = false;
        }

        if (PanelUi.TryNum(text, out double v))
        {
            Edited?.Invoke(axis, v, final);
        }
    }

    /// <summary>An axis letter's colour: X red, Y green, Z blue as in the views (roll turns about X, pitch about Y, yaw
    /// about Z; R G B for colours).</summary>
    private static Color AxisColour(string axis) => axis switch
    {
        "X" or "R" or "Roll" or "Roll°" => EditorTheme.AxisX,
        "Y" or "G" or "Pitch" or "Pitch°" => EditorTheme.AxisY,
        "Z" or "B" or "Yaw" or "Yaw°" => EditorTheme.AxisZ,
        _ => EditorTheme.TextDim,
    };
}
