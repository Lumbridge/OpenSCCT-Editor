using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Panels;

/// <summary>Small builders for the controls every panel uses, so the panels look alike and stay short. Styling comes
/// from <see cref="EditorTheme"/> variations; docs/UI-DESIGN.md says which to use when.</summary>
internal static class PanelUi
{
    /// <summary>The mark of a value that differs across the selection; show it in <see cref="EditorTheme.TextDim"/>
    /// with <see cref="MixedTooltip"/>.</summary>
    public const string Mixed = "\u2014";

    /// <summary>The tooltip of a <see cref="Mixed"/> value.</summary>
    public const string MixedTooltip = "Multiple values";

    /// <summary>A panel's row of buttons and filters. It wraps onto a second line in a narrow dock instead of
    /// pushing the dock wider.</summary>
    public static HFlowContainer Toolbar(Control parent)
    {
        var row = new HFlowContainer();
        row.AddThemeConstantOverride("h_separation", EditorTheme.SpaceXs);
        row.AddThemeConstantOverride("v_separation", EditorTheme.SpaceXs);
        parent.AddChild(row);
        return row;
    }

    /// <summary>A command button (raised). <paramref name="flat"/> gives the <c>FlatButton</c> look, for an inline
    /// text action in a dense row: nothing drawn until hovered.</summary>
    public static Button Button(Control parent, string text, string tooltip, Action pressed, bool flat = false)
    {
        var b = new Button { Text = text, TooltipText = tooltip, FocusMode = Control.FocusModeEnum.None };
        if (flat)
        {
            b.ThemeTypeVariation = "FlatButton";
        }

        b.Pressed += pressed;
        parent.AddChild(b);
        return b;
    }

    /// <summary>The one primary action of a panel or dialog (accent filled).</summary>
    public static Button Primary(Control parent, string text, string tooltip, Action pressed)
    {
        Button b = Button(parent, text, tooltip, pressed);
        b.ThemeTypeVariation = "PrimaryButton";
        return b;
    }

    /// <summary>A 24 px square row action showing one of the <see cref="Icons"/>: dim at rest, bright on hover, red on
    /// hover when <paramref name="danger"/> (delete, remove, clear).</summary>
    public static Button IconButton(Control parent, string icon, string tooltip, Action pressed, bool danger = false)
    {
        Button b = Button(parent, "", tooltip, pressed);
        b.ThemeTypeVariation = danger ? "DangerIconButton" : "IconButton";
        // Drawn white: the variation tints it for each state.
        b.Icon = Icons.Get(icon, Colors.White);
        b.IconAlignment = HorizontalAlignment.Center;
        b.CustomMinimumSize = new Vector2(EditorTheme.IconButtonSize, EditorTheme.IconButtonSize);
        return b;
    }

    /// <summary>A filter or view toggle: a chip, outlined when off and accent-washed when on.</summary>
    public static Button Toggle(Control parent, string text, string tooltip, bool on, Action<bool> toggled)
    {
        var b = new Button
        {
            Text = text,
            TooltipText = tooltip,
            ToggleMode = true,
            ButtonPressed = on,
            FocusMode = Control.FocusModeEnum.None,
            ThemeTypeVariation = "Chip",
        };
        b.Toggled += v => toggled(v);
        parent.AddChild(b);
        return b;
    }

    /// <summary>A severity filter chip: the severity's icon, plural noun and count ("Errors 3"). Call
    /// <see cref="SeverityChip.Refresh"/> after the counts change.</summary>
    public static SeverityChip SeverityToggle(Control parent, Severity s, Func<int> count, bool on, Action<bool> toggled)
    {
        var b = new SeverityChip(s, count)
        {
            TooltipText = $"Show {SeverityNoun(s).ToLowerInvariant()}",
            ToggleMode = true,
            ButtonPressed = on,
            FocusMode = Control.FocusModeEnum.None,
            ThemeTypeVariation = "Chip",
            Icon = PanelIcons.Severity(s),
        };
        b.Toggled += v => toggled(v);
        parent.AddChild(b);
        b.Refresh();
        return b;
    }

    /// <summary>The plural noun of a severity: "Errors", "Warnings", "Info".</summary>
    public static string SeverityNoun(Severity s) => s switch
    {
        Severity.Error => "Errors",
        Severity.Warning => "Warnings",
        _ => "Info",
    };

    /// <summary>A segmented control: buttons joined into one strip. With <paramref name="selected"/> 0 or more the
    /// parts are exclusive toggles (one is on); with -1 they are plain commands (All / Usual / None).</summary>
    public static HBoxContainer Segmented(Control parent, (string Text, string Tooltip)[] items, int selected, Action<int> chosen)
    {
        var strip = new HBoxContainer();
        strip.AddThemeConstantOverride("separation", 0);
        ButtonGroup? group = selected >= 0 ? new ButtonGroup() : null;
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var b = new Button
            {
                Text = items[i].Text,
                TooltipText = items[i].Tooltip,
                FocusMode = Control.FocusModeEnum.None,
                ThemeTypeVariation = items.Length == 1 ? "" : i == 0 ? "SegmentFirst" : i == items.Length - 1 ? "SegmentLast" : "Segment",
            };
            if (group is not null)
            {
                b.ToggleMode = true;
                b.ButtonGroup = group;
                b.ButtonPressed = i == selected;
                b.Toggled += on =>
                {
                    if (on)
                    {
                        chosen(index);
                    }
                };
            }
            else
            {
                b.Pressed += () => chosen(index);
            }

            strip.AddChild(b);
        }

        parent.AddChild(strip);
        return strip;
    }

    /// <summary>A spin box: one field with its stepper inside, <paramref name="width"/> wide (in a form row, set
    /// <c>SizeFlagsHorizontal</c> to fill). <paramref name="scrub"/> hides the stepper for dense grids; the value still
    /// changes with the wheel, the arrow keys or typing. Units go in <c>Suffix</c> (" s", " u").</summary>
    public static SpinBox Spin(Control parent, double min, double max, double step, double value, string tooltip = "", int width = EditorTheme.SpinMinWidth, bool scrub = false)
    {
        var spin = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            TooltipText = tooltip,
            SelectAllOnFocus = true,
            CustomMinimumSize = new Vector2(width, 0),
        };
        spin.Value = value;
        if (scrub)
        {
            spin.ThemeTypeVariation = "ScrubSpin";
        }

        parent.AddChild(spin);
        return spin;
    }

    /// <summary>A drop-down that never widens its dock: it keeps the width the layout gives it, long text ends in
    /// "…", and its list is capped in height (<see cref="CapPopup"/>).</summary>
    public static OptionButton Options(Control parent, string tooltip = "")
    {
        var o = new OptionButton
        {
            TooltipText = tooltip,
            FitToLongestItem = false,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            FocusMode = Control.FocusModeEnum.None,
        };
        CapPopup(o.GetPopup());
        parent.AddChild(o);
        return o;
    }

    /// <summary>Caps a popup menu at <see cref="EditorTheme.PopupMaxHeight"/> UI pixels; a longer list scrolls.</summary>
    public static void CapPopup(PopupMenu menu)
    {
        menu.AboutToPopup += () =>
        {
            float scale = menu.IsInsideTree() ? menu.GetTree().Root.ContentScaleFactor : 1f;
            // Window.MaxSize clamps both axes, so the width gets a cap too: one no menu reaches.
            menu.MaxSize = new Vector2I(16384, (int)(EditorTheme.PopupMaxHeight * scale));
        };
    }

    /// <summary>Sets a tree's columns: left-aligned titles (Title Case), a minimum width each, and cells that clip
    /// instead of widening the tree.</summary>
    public static void Columns(Tree tree, params (string Title, int MinWidth, bool Expand)[] columns)
    {
        tree.Columns = columns.Length;
        tree.ColumnTitlesVisible = true;
        for (int i = 0; i < columns.Length; i++)
        {
            (string title, int minWidth, bool expand) = columns[i];
            tree.SetColumnTitle(i, title);
            tree.SetColumnTitleAlignment(i, HorizontalAlignment.Left);
            tree.SetColumnCustomMinimumWidth(i, minWidth);
            tree.SetColumnExpand(i, expand);
            tree.SetColumnClipContent(i, true);
        }
    }

    /// <summary>Keeps column <paramref name="column"/> of a tree dim (<see cref="EditorTheme.TextDim"/>, set by the
    /// caller as each row is made) except on selected rows, whose cells all take the tree's selected text colour, so a
    /// dim identifier never sits faint on the selection fill.</summary>
    public static void DimColumn(Tree tree, int column)
    {
        var bright = new List<TreeItem>();
        void Refresh()
        {
            foreach (TreeItem item in bright.Where(i => GodotObject.IsInstanceValid(i) && !i.IsSelected(column)))
            {
                item.SetCustomColor(column, EditorTheme.TextDim);
            }

            bright.Clear();
            for (TreeItem? item = tree.GetNextSelected(null); item is not null; item = tree.GetNextSelected(item))
            {
                item.ClearCustomColor(column);
                bright.Add(item);
            }
        }

        void Queue() => EditorLifetime.Defer(Refresh);
        tree.ItemSelected += Queue;
        tree.CellSelected += Queue;
        tree.NothingSelected += Queue;
        tree.MultiSelected += (_, _, _) => Queue();
    }

    /// <summary>A centred empty state: a 32 px icon, a title, a hint and an optional primary action. Added to a
    /// container it fills it; added to a plain control (a list) it covers it, lets clicks through and hides the list's
    /// frame while it shows. In a short dock it folds itself up (see <see cref="EmptyStateView"/>).</summary>
    public static Control EmptyState(Control parent, string icon, string title, string hint, string? action = null, Action? run = null)
    {
        Button? button = null;
        if (action is not null && run is not null)
        {
            button = new Button { Text = action, ThemeTypeVariation = "PrimaryButton" };
            button.Pressed += run;
        }

        var view = new EmptyStateView(icon, title, hint, button);
        view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        parent.AddChild(view);
        return view;
    }

    /// <summary>A callout: an inline warning, note or error in a form, on a wash of its colour, with its icon.</summary>
    public static PanelContainer Callout(Control parent, Severity s, string text)
    {
        var box = new PanelContainer
        {
            ThemeTypeVariation = s switch { Severity.Error => "CalloutError", Severity.Warning => "Callout", _ => "CalloutInfo" },
        };
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        box.AddChild(row);
        row.AddChild(new TextureRect
        {
            Texture = PanelIcons.Severity(s),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        });
        row.AddChild(new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(80, 0),
        });
        parent.AddChild(box);
        return box;
    }

    /// <summary>A search box. Keep <paramref name="placeholder"/> short enough for a narrow dock;
    /// the tooltip says what is searched. <paramref name="changed"/> runs on every keystroke.</summary>
    public static LineEdit Search(Control parent, string placeholder, Action<string> changed, string? tooltip = null)
    {
        var e = new LineEdit
        {
            PlaceholderText = placeholder,
            TooltipText = tooltip ?? "",
            // Room for the placeholder: a crowded toolbar wraps instead of squeezing the box.
            CustomMinimumSize = new Vector2(EditorTheme.SearchMinWidth, 0),
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        e.TextChanged += t => changed(t);
        parent.AddChild(e);
        return e;
    }

    public static Label Label(Control parent, string text, Color? color = null)
    {
        var l = new Label { Text = text };
        if (color is { } c)
        {
            l.AddThemeColorOverride("font_color", c);
        }

        parent.AddChild(l);
        return l;
    }

    /// <summary>A vertical scroll area whose content fills the width; returns the content box (its scroll area is
    /// <see cref="ScrollOf"/>). The scroll bar's room is always kept, so the bar coming and going never changes the
    /// area's width (in a dock sized by its content that would make the layout flip between two widths every frame),
    /// and a <see cref="EditorTheme.ScrollGutter"/> keeps the content off the bar.</summary>
    public static VBoxContainer Scroll(Control parent)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Reserve,
        };
        parent.AddChild(scroll);
        var gutter = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        gutter.AddThemeConstantOverride("margin_right", EditorTheme.ScrollGutter);
        scroll.AddChild(gutter);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        gutter.AddChild(box);
        return box;
    }

    /// <summary>The scroll area holding a box made by <see cref="Scroll"/> (to show, hide or size it).</summary>
    public static ScrollContainer ScrollOf(Control box) => box.GetParent().GetParent<ScrollContainer>();

    /// <summary>A section header: semibold, 12 px of space above and a hairline across below (the Header variation).</summary>
    public static Label Header(Control parent, string text)
    {
        var l = new Label { Text = text, ThemeTypeVariation = "Header" };
        parent.AddChild(l);
        return l;
    }

    /// <summary>The name of what a panel shows (the selection in the Inspector): semibold, a size up, ending in "…"
    /// with the whole name in its tooltip.</summary>
    public static Label Title(Control parent, string text)
    {
        var l = new Label
        {
            Text = text,
            ThemeTypeVariation = "Title",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = text,
            MouseFilter = Control.MouseFilterEnum.Pass,
            CustomMinimumSize = new Vector2(1, 0),
        };
        parent.AddChild(l);
        return l;
    }

    /// <summary>A hint, status line or info row: small and dim. It wraps at word boundaries; with
    /// <paramref name="wrap"/> false (a status at the end of a toolbar row) it ends in "…" with the whole text in its
    /// tooltip.</summary>
    public static Label Caption(Control parent, string text, bool wrap = true)
    {
        var l = new Label { Text = text, ThemeTypeVariation = "Caption", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        if (wrap)
        {
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
        else
        {
            l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            l.TooltipText = text;
            l.MouseFilter = Control.MouseFilterEnum.Pass;
            l.CustomMinimumSize = new Vector2(1, 0);
        }

        parent.AddChild(l);
        return l;
    }

    /// <summary>A labelled form row: a dim name in the panel's label column, the editor filling the rest. A name too
    /// long for the column ends in "…"; the label's tooltip starts with the whole name, then <paramref name="tooltip"/>.</summary>
    public static HBoxContainer Row(Control parent, string name, string? tooltip = null, int labelWidth = EditorTheme.LabelColumn)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        var label = new Label
        {
            Text = name,
            ThemeTypeVariation = "Dim",
            CustomMinimumSize = new Vector2(labelWidth, 0),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = tooltip is null ? name : tooltip.Contains(name, StringComparison.Ordinal) ? tooltip : $"{name}\n{tooltip}",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        row.AddChild(label);
        parent.AddChild(row);
        return row;
    }

    /// <summary>A field for prose (a summary, a description): its dim name on a line of its own, then a full-width box
    /// that wraps at word boundaries and grows with its text from three lines, instead of one line cut mid-word in the
    /// label column.</summary>
    public static TextEdit Prose(Control parent, string name, string? tooltip = null)
    {
        var block = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        block.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        parent.AddChild(block);
        block.AddChild(new Label
        {
            Text = name,
            ThemeTypeVariation = "Dim",
            TooltipText = tooltip ?? name,
            MouseFilter = Control.MouseFilterEnum.Pass,
        });
        var box = new TextEdit
        {
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            ScrollFitContentHeight = true,
            CustomMinimumSize = new Vector2(0, EditorTheme.ProseMinHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = tooltip ?? "",
        };
        block.AddChild(box);
        return box;
    }

    public static Color SeverityColor(Severity s) => s switch
    {
        Severity.Error => EditorTheme.Error,
        Severity.Warning => EditorTheme.Warning,
        _ => EditorTheme.Info,
    };

    /// <summary>Text as a sentence: its first letter in capitals ("event ring: ..." reads "Event ring: ...").</summary>
    public static string Sentence(string text) =>
        text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    public static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A camelCase key as words for a label: wallThickness becomes "Wall thickness".</summary>
    public static string Words(string key)
    {
        var text = new System.Text.StringBuilder(key.Length + 4);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (i == 0)
            {
                text.Append(char.ToUpperInvariant(c));
            }
            else if (char.IsUpper(c) && !char.IsUpper(key[i - 1]))
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>A whole number with thousands grouped ("3,571"), the one way counts are written.</summary>
    public static string Int(int n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>A count with its noun: "1 actor", "2 actors", "1,204 brushes", "3 GEs". Pass <paramref name="many"/>
    /// when adding "s" or "es" gives the wrong plural.</summary>
    public static string Count(int n, string one, string? many = null) =>
        n.ToString("#,0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? Plural(one));

    private static string Plural(string one)
    {
        if (one.Length == 0 || char.IsUpper(one[^1]))
        {
            return one + "s"; // GE → GEs
        }

        if (one.EndsWith('s') || one.EndsWith('x') || one.EndsWith("ch", StringComparison.Ordinal) || one.EndsWith("sh", StringComparison.Ordinal))
        {
            return one + "es";
        }

        return one.Length > 1 && one[^1] == 'y' && !"aeiou".Contains(one[^2], StringComparison.Ordinal) ? one[..^1] + "ies" : one + "s";
    }

    /// <summary>
    /// An Unreal property or class name as words for a label: "bCollideActors" becomes "Collide actors",
    /// "LightBrightness" "Light brightness", "EchelonEMF" "Echelon EMF". A package prefix is dropped
    /// ("Engine.Light" → "Light"). Class names in the S/E/U/A/F prefix style ("SAlarm", "SMagicEvent") stay as they
    /// are: map makers know them by those names. Keep the raw name in the tooltip and in search.
    /// </summary>
    public static string DisplayName(string unrealName)
    {
        string name = unrealName[(unrealName.LastIndexOf('.') + 1)..];
        if (name.Length >= 3 && "SEUAF".Contains(name[0], StringComparison.Ordinal) && char.IsUpper(name[1]) && char.IsLower(name[2]))
        {
            return name;
        }

        if (name.Length >= 2 && name[0] == 'b' && char.IsUpper(name[1]))
        {
            name = name[1..];
        }

        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is '_' or ' ')
            {
                Flush();
                continue;
            }

            // A word starts at a capital after a small letter (lightBrightness), or at the last capital of a run
            // that a small letter follows (EMFLight → EMF Light).
            bool boundary = word.Length > 0 && char.IsUpper(c) &&
                (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (boundary)
            {
                Flush();
            }

            word.Append(c);
        }

        Flush();
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            bool acronym = w.Length > 1 && w.All(ch => !char.IsLetter(ch) || char.IsUpper(ch));
            words[i] = acronym ? w : i == 0 ? char.ToUpperInvariant(w[0]) + w[1..] : char.ToLowerInvariant(w[0]) + w[1..];
        }

        return words.Count == 0 ? unrealName : string.Join(' ', words);

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
    }

    /// <summary>An identifier that may wrap onto a second line at its natural breaks: a zero-width space after each
    /// "_" and "." ("Editor.Grid_Floor" breaks after "Editor." or "Grid_"), never inside a word.</summary>
    public static string WrapIdentifier(string name) =>
        name.Replace("_", "_\u200B", StringComparison.Ordinal).Replace(".", ".\u200B", StringComparison.Ordinal);

    public static bool TryNum(string text, out double v) =>
        double.TryParse(text.Trim().Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    /// <summary>Selects an item, or with <paramref name="index"/> -1 shows <paramref name="none"/>: an OptionButton with
    /// nothing selected is otherwise a blank button.</summary>
    public static void Choose(OptionButton option, int index, string none)
    {
        option.Select(index);
        if (index < 0)
        {
            option.Text = none;
        }
    }

    /// <summary>Opens a popup menu at the mouse.</summary>
    public static void PopupAtMouse(Control from, PopupMenu menu)
    {
        menu.Position = ScreenPoint(from, EditorPointer.Local(from));
        menu.Popup();
    }

    /// <summary>A point in <paramref name="control"/>'s local coordinates where a popup goes: screen pixels when popups
    /// are their own windows (the UI scale applied), the main window's canvas when they are embedded in it.</summary>
    public static Vector2I ScreenPoint(Control control, Vector2 local) => (Vector2I)(control.GetScreenTransform() * local).Round();

    /// <summary>Copies text to the clipboard.</summary>
    public static void Copy(string text) => EditorClipboard.Set(text);
}

/// <summary>Runs an action once at the end of the frame however often it is queued before then (rebuilds after a burst
/// of edits, such as an undo-history jump, happen once).</summary>
internal sealed class DeferredAction
{
    private readonly Action _action;
    private bool _queued;

    public DeferredAction(Action action) => _action = action;

    public void Queue()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;
        Callable.From(Run).CallDeferred();
    }

    private void Run()
    {
        _queued = false;
        // Nothing to rebuild once the editor is quitting: the panels are going away.
        if (!EditorLifetime.IsQuitting)
        {
            _action();
        }
    }
}
