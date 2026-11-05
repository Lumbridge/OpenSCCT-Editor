using Godot;
using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>A toolbar drop-down listing the actions of one menu path (grid sizes, rotation steps, layouts); its label
/// shows the checked one, e.g. "Grid 16", with a chevron after it. The width of its longest choice is kept, so the
/// toolbar never shifts when the choice changes: by the button itself (a boxed field, its chevron at the right edge),
/// or with <see cref="Reserve"/> by a slot around it, so a borderless button hugs its text and its chevron follows the
/// text. Its list opens just under it.</summary>
public sealed partial class ActionMenuButton : MenuButton
{
    /// <summary>Gap between the button and its list.</summary>
    private const int PopupGap = EditorTheme.SpaceXxs;

    private readonly EditorContext _ctx;
    private readonly string _menu;
    private readonly string _prefix;
    private readonly List<string> _ids = new();

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public ActionMenuButton()
        : this(EditorContext.Instance, "", "")
    {
    }

    public ActionMenuButton(EditorContext ctx, string menu, string prefix, string? tooltip = null)
    {
        _ctx = ctx;
        _menu = menu;
        _prefix = prefix;
        ThemeTypeVariation = "ToolMenu";
        // A MenuButton is flat by default, which skips its styles: the ToolMenu boxes show hover and the open state.
        Flat = false;
        FocusMode = FocusModeEnum.None;
        Alignment = HorizontalAlignment.Left;
        Icon = Icons.Get("chevron-down", EditorTheme.TextDim);
        IconAlignment = HorizontalAlignment.Right;
        TooltipText = tooltip ?? menu[(menu.LastIndexOf('/') + 1)..];
        PopupMenu popup = GetPopup();
        popup.AboutToPopup += Fill;
        popup.IdPressed += id =>
        {
            if (id >= 0 && id < _ids.Count && _ids[(int)id].Length > 0)
            {
                _ctx.Run(_ids[(int)id]);
            }

            Refresh();
        };
        MenuToggles.KeepOpen(popup, () => MenuToggles.SyncActions(popup, _ids, _ctx));
        Refresh();
    }

    /// <summary>The slot that keeps the longest choice's width, the button hugging its own text inside it; null keeps
    /// the width on the button.</summary>
    public Control? Reserve { get; set; }

    /// <summary>For a drop-down that mixes one choice with independent toggles (neighbour snap: its distance, then what
    /// it applies to): the radio group the button's text shows. The other items are check boxes, a separator between
    /// order bands as in the menu bar. Null: every checkable item is one of the choices.</summary>
    public string? LabelGroup { get; init; }

    /// <summary>The button's text when no item of <see cref="LabelGroup"/> is checked (a value typed in Preferences).</summary>
    public Func<string>? Fallback { get; init; }

    public override void _Ready()
    {
        // Godot puts the list at the button's lower left as it opens; move it down a hair so the button's hover box
        // stays visible above it.
        PopupMenu popup = GetPopup();
        popup.AboutToPopup += () => EditorLifetime.Defer(() => popup.Position += new Vector2I(0, (int)(PopupGap * GetTree().Root.ContentScaleFactor)));
    }

    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        List<EditorAction> items = Items().Where(a => LabelGroup is null || a.RadioGroup == LabelGroup).ToList();
        EditorAction? on = items.FirstOrDefault(a => a.IsChecked?.Invoke() == true);
        Text = on is null && Fallback is not null ? _prefix + Fallback() : Label(on);
        Font font = GetThemeFont("font");
        int size = GetThemeFontSize("font_size");
        float widest = items.Select(a => font.GetStringSize(Label(a), HorizontalAlignment.Left, -1, size).X)
            .Append(font.GetStringSize(Text, HorizontalAlignment.Left, -1, size).X).Max();
        var width = new Vector2(Mathf.Ceil(widest) + EditorTheme.IconSize + GetThemeConstant("h_separation") + 2 * EditorTheme.SpaceS, 0);
        if (Reserve is not null)
        {
            // The slot keeps the width; the button hugs its text (a width set before it had a slot is dropped).
            Reserve.CustomMinimumSize = width;
            CustomMinimumSize = Vector2.Zero;
        }
        else
        {
            CustomMinimumSize = width;
        }
    }

    /// <summary>The button's text for a choice: the prefix and the choice's name without its note in brackets
    /// ("5.625° (1024)" shows as "5.625°"; the menu keeps the note).</summary>
    private string Label(EditorAction? a)
    {
        if (a is null)
        {
            return _prefix + "—";
        }

        int note = a.Title.IndexOf(" (", StringComparison.Ordinal);
        return _prefix + (note > 0 ? a.Title[..note] : a.Title);
    }

    private IEnumerable<EditorAction> Items() =>
        _ctx.Actions.Where(a => a.Menu == _menu).OrderBy(a => a.Order).ThenBy(a => a.Title, StringComparer.Ordinal);

    private void Fill()
    {
        PopupMenu popup = GetPopup();
        popup.Clear();
        _ids.Clear();
        int? band = null;
        foreach (EditorAction a in Items())
        {
            if (LabelGroup is not null && band is { } b && b != a.Order / 100)
            {
                popup.AddSeparator();
                _ids.Add("");
            }

            band = a.Order / 100;
            int id = _ids.Count;
            if (a.IsChecked is not null && LabelGroup is not null && a.RadioGroup != LabelGroup)
            {
                popup.AddCheckItem(a.Title, id);
                popup.SetItemChecked(popup.GetItemIndex(id), a.IsChecked());
            }
            else if (a.IsChecked is not null)
            {
                popup.AddRadioCheckItem(a.Title, id);
                popup.SetItemChecked(popup.GetItemIndex(id), a.IsChecked());
            }
            else
            {
                popup.AddItem(a.Title, id);
            }

            int index = popup.GetItemIndex(id);
            bool enabled = a.IsEnabled();
            popup.SetItemDisabled(index, !enabled);
            if (enabled && KeyChord.Parse(ShortcutBindings.Of(a)) is { } chord)
            {
                // Shown for reference: the shortcut router runs shortcuts, so the menu's own copy stays off.
                popup.SetItemShortcut(index, new Godot.Shortcut { Events = [chord.ToEvent()] }, global: false);
                popup.SetItemShortcutDisabled(index, true);
            }

            if (a.Description is { } help)
            {
                popup.SetItemTooltip(index, help);
            }

            _ids.Add(a.Id);
        }
    }
}
