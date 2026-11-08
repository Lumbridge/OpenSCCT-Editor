using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>Numeric spinners for scalar and vector properties. Mixed selections show a dash; typing and stepping
/// edit one component at a time. Programmatic refreshes and untouched focus changes never commit an edit.</summary>
public partial class AxisFields : HBoxContainer
{
    private readonly List<LineEdit> _boxes = new();
    private readonly List<SpinBox> _spinners = new();
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

            var spin = new SpinBox
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = Vector2.Zero,
                AllowGreater = true,
                AllowLesser = true,
                Step = 0.000000001,
                CustomArrowStep = 1,
                SelectAllOnFocus = true,
                TooltipText = axes[i],
            };
            LineEdit box = spin.GetLineEdit();
            PanelUi.UseArrowStepForWheel(spin);
            box.CustomMinimumSize = Vector2.Zero;
            box.AddThemeConstantOverride("minimum_character_width", 1);
            spin.ValueChanged += value =>
            {
                if (f._setting) { return; }
                f._values[axis] = value;
                f._typed[axis] = false;
                f.Edited?.Invoke(axis, value, true);
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
            f._spinners.Add(spin);
            f.AddChild(spin);
        }

        return f;
    }

    public bool HasFocusInside => _boxes.Any(b => b.HasFocus());

    public bool Editable
    {
        set
        {
            foreach (SpinBox b in _spinners)
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
            _spinners[axis].SetValueNoSignal(0);
            box.Text = "";
            box.PlaceholderText = "—";
            box.TooltipText = _names[axis];
        }
        else
        {
            string whole = PanelUi.Num(v);
            _spinners[axis].SetValueNoSignal(v);
            box.Text = whole;
            box.PlaceholderText = "";
            box.TooltipText = $"{_names[axis]}: {whole}";
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
            // The inspector refresh deliberately leaves the focused box alone. Keep its backing value current so
            // focus loss (and SpinBox's deferred submission) cannot restore the value from before typing.
            _values[axis] = v;
            _spinners[axis].SetValueNoSignal(v);
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
