using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// The empty state of a panel or list (see <see cref="PanelUi.EmptyState"/>): a 32 px icon, a title, a hint and an
/// optional primary button, stacked and centred in whatever room it is given. It asks for no room of its own and fits
/// itself to what there is: when the stack is too tall it drops the icon, then moves the hint into the title's
/// tooltip; it stays one centred stack whatever the size, so every panel's empty state looks the same. The button
/// stays to the last. Over a list it hides the list's frame and column titles while it shows, so it sits on the panel
/// rather than in an empty table.
/// </summary>
public sealed partial class EmptyStateView : Container
{
    /// <summary>The hint's widest line, so it reads as a short paragraph even in a wide bottom dock.</summary>
    private const int HintWidth = 320;

    private readonly TextureRect? _hero;
    private readonly Label? _title;
    private Label? _hint;
    private readonly Control? _action;
    private readonly VBoxContainer? _stack;
    private bool _hidListTitles;

    public EmptyStateView(string icon, string title, string hint, Control? action)
    {
        Name = "EmptyState";
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _stack = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _stack.AddThemeConstantOverride("separation", EditorTheme.SpaceM);
        AddChild(_stack);
        _hero = new TextureRect
        {
            Texture = Icons.Get(icon, EditorTheme.TextDim, EditorTheme.IconSizeHero),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _stack.AddChild(_hero);

        _title = new Label
        {
            Text = title,
            ThemeTypeVariation = "Title",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _title.AddThemeColorOverride("font_color", EditorTheme.Text);
        _stack.AddChild(_title);

        if (hint.Length > 0)
        {
            AddHint(hint);
        }

        if (action is not null)
        {
            var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
            row.AddChild(action);
            _stack.AddChild(row);
            _action = row;
        }
    }

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public EmptyStateView()
    {
    }

    /// <summary>Changes the title and the hint, for an empty state that says why (an empty hint shows none).</summary>
    public void SetText(string title, string hint)
    {
        if (_title is null || (_title.Text == title && (_hint?.Text ?? "") == hint))
        {
            return;
        }

        _title.Text = title;
        if (_hint is not null)
        {
            _hint.Text = hint;
        }
        else if (hint.Length > 0)
        {
            AddHint(hint);
        }

        QueueSort();
    }

    /// <summary>The hint, under the title (before the button).</summary>
    private void AddHint(string text)
    {
        _hint = new Label
        {
            Text = text,
            ThemeTypeVariation = "Caption",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(HintWidth, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _stack!.AddChild(_hint);
        _stack.MoveChild(_hint, _title!.GetIndex() + 1);
    }

    /// <summary>No room of its own: it takes what its parent gives and fits itself to it.</summary>
    public override Vector2 _GetMinimumSize() => Vector2.Zero;

    public override void _Notification(int what)
    {
        if (what == NotificationSortChildren || what == NotificationResized)
        {
            Arrange();
        }
        else if (what == NotificationVisibilityChanged)
        {
            FrameList();
        }
        else if (what == NotificationReady)
        {
            // After the panel has built its list (column titles included).
            EditorLifetime.Defer(FrameList);
        }
    }

    private void Arrange()
    {
        if (_stack is null || _title is null || _hero is null)
        {
            return;
        }

        if (_hint is not null)
        {
            float width = Math.Min(HintWidth, Math.Max(0, Size.X - 2 * EditorTheme.SpaceM));
            if (!Mathf.IsEqualApprox(_hint.CustomMinimumSize.X, width))
            {
                _hint.CustomMinimumSize = new Vector2(width, 0);
            }
        }

        int sep = EditorTheme.SpaceM;
        float title = _title.GetCombinedMinimumSize().Y;
        // The hint's height at the width it will have, measured from its text: a wrapping label's own minimum is
        // from its last layout, which while hidden is a sliver one word wide.
        float hint = _hint is not { Text.Length: > 0 } ? 0 : HintHeight(_hint) + sep;
        float action = _action is null ? 0 : _action.GetCombinedMinimumSize().Y + sep;
        float room = Size.Y - 2 * EditorTheme.SpaceM;
        bool hero = EditorTheme.IconSizeHero + sep + title + hint + action <= room;
        bool showHint = hero || title + hint + action <= room;

        SetShown(_hero, hero);
        if (_hint is not null)
        {
            SetShown(_hint, showHint && _hint.Text.Length > 0);
            _title.TooltipText = showHint ? "" : _hint.Text;
            _title.MouseFilter = showHint ? MouseFilterEnum.Ignore : MouseFilterEnum.Pass;
        }

        // Centred; when even the title and button do not fit, from the top, so what shows first is what stays.
        Vector2 size = _stack.GetCombinedMinimumSize();
        size.X = Math.Min(size.X, Size.X);
        float x = (Size.X - size.X) / 2;
        float y = Math.Max(Math.Min(EditorTheme.SpaceM, Size.Y), (Size.Y - size.Y) / 2);
        FitChildInRect(_stack, new Rect2(new Vector2(x, y).Floor(), size));
    }

    private static float HintHeight(Label hint)
    {
        Font font = hint.GetThemeFont("font");
        int size = hint.GetThemeFontSize("font_size");
        float lines = font.GetMultilineStringSize(hint.Text, HorizontalAlignment.Center, Math.Max(1, hint.CustomMinimumSize.X), size).Y;
        return lines + hint.GetThemeStylebox("normal").GetMinimumSize().Y;
    }

    private static void SetShown(Control c, bool shown)
    {
        if (c.Visible != shown)
        {
            c.Visible = shown;
        }
    }

    /// <summary>Over a list: hides its frame and column titles while the empty state shows, restores them after.</summary>
    private void FrameList()
    {
        if (_stack is null || GetParent() is not Control list || list is not (Tree or ItemList))
        {
            return;
        }

        if (Visible)
        {
            list.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            if (list is Tree { ColumnTitlesVisible: true } tree)
            {
                tree.ColumnTitlesVisible = false;
                _hidListTitles = true;
            }
        }
        else
        {
            list.RemoveThemeStyleboxOverride("panel");
            if (list is Tree tree && _hidListTitles)
            {
                tree.ColumnTitlesVisible = true;
                _hidListTitles = false;
            }
        }
    }
}
