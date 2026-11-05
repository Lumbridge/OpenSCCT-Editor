using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;

namespace OpenScctEditor.Shell;

/// <summary>
/// The Shortcuts page of Preferences: every action with its shortcut and default ("—" for none). Double-click a row (or
/// Set) and press the new keys; Escape cancels. A binding the user changed is drawn semibold with a reset button in its
/// cell. A shortcut another action already uses is a clash: the editor asks whether to move it, and rows that still
/// clash are drawn in the error colour. Preset puts in the keys of another editor (<see cref="ShortcutPresets"/>).
/// Bindings are saved per user (<see cref="ShortcutBindings"/>).
/// </summary>
public sealed partial class ShortcutEditor : VBoxContainer
{
    private const string Idle = "Double\u2011click an action to change its keys, or select it and press Set\u2026";
    private const int ResetButton = 1;

    private readonly EditorContext _ctx;
    private readonly Tree _tree;
    private readonly Label _prompt;
    private readonly Button _set;
    private readonly Button _clear;
    private readonly Button _default;
    private string _filter = "";
    private EditorAction? _capturing;

    public ShortcutEditor(EditorContext context)
    {
        _ctx = context;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        AddChild(bar);
        PanelUi.Search(bar, "Filter", t =>
        {
            _filter = t.Trim();
            Rebuild();
        }, "Find actions by name, menu or keys");
        _set = PanelUi.Button(bar, "Set\u2026", "Press new keys for the selected action", () => Begin(Selected()));
        _clear = PanelUi.Button(bar, "Clear", "Take the selected action's shortcut away", () =>
        {
            if (Selected() is { } a)
            {
                ShortcutBindings.Bind(a, null);
            }
        });
        _default = PanelUi.Button(bar, "Default", "Put the selected action's default shortcut back", () =>
        {
            if (Selected() is { } a)
            {
                ShortcutBindings.Reset(a);
            }
        });
        var presets = new MenuButton
        {
            Text = "Preset",
            Flat = false,
            ThemeTypeVariation = "Button",
            Icon = Icons.Get("chevron-down", EditorTheme.TextDim),
            IconAlignment = HorizontalAlignment.Right,
            TooltipText = "Use the keys of another editor (replaces your own bindings)",
        };
        PopupMenu menu = presets.GetPopup();
        PanelUi.CapPopup(menu);
        for (int i = 0; i < ShortcutPresets.All.Count; i++)
        {
            menu.AddItem(ShortcutPresets.All[i].Name, i);
            menu.SetItemTooltip(i, ShortcutPresets.All[i].Description);
        }

        menu.IdPressed += id => AskPreset(ShortcutPresets.All[(int)id]);
        bar.AddChild(presets);

        _prompt = PanelUi.Caption(this, Idle, wrap: false);
        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        PanelUi.Columns(_tree, ("Action", 0, true), ("Shortcut", EditorTheme.ShortcutColumnWidth, false),
            ("Default", EditorTheme.ShortcutColumnWidth, false));
        _tree.ItemActivated += () => Begin(Selected());
        _tree.ItemSelected += UpdateButtons;
        _tree.ButtonClicked += (item, _, id, _) =>
        {
            if (id == ResetButton && item.GetMetadata(0).AsString() is { Length: > 0 } actionId && _ctx.FindAction(actionId) is { } a)
            {
                ShortcutBindings.Reset(a);
            }
        };
        AddChild(_tree);
        UpdateButtons();
        // Another Preferences page was picked: stop waiting for keys, so the shortcuts work again.
        VisibilityChanged += () =>
        {
            if (!IsVisibleInTree())
            {
                Cancel();
            }
        };
    }

    public override void _EnterTree()
    {
        ShortcutBindings.Changed += Rebuild;
        Rebuild();
    }

    public override void _ExitTree()
    {
        ShortcutBindings.Changed -= Rebuild;
        Cancel();
    }

    public override void _Input(InputEvent e)
    {
        if (_capturing is not { } action || e is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        GetViewport().SetInputAsHandled();
        if (key.Keycode == Key.Escape)
        {
            Cancel();
            return;
        }

        // A modifier on its own is the start of a chord: wait for the key.
        if (key.Keycode is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta)
        {
            return;
        }

        var chord = new KeyChord(key.Keycode, key.IsCommandOrControlPressed(), key.ShiftPressed, key.AltPressed);
        Cancel();
        Assign(action, chord);
    }

    private void Begin(EditorAction? action)
    {
        if (action is null)
        {
            return;
        }

        _capturing = action;
        ShortcutRouter.Suspended = true;
        _prompt.Text = $"Press the new keys for {action.Title} \u00b7 Esc cancels";
        _prompt.AddThemeColorOverride("font_color", EditorTheme.Accent);
    }

    /// <summary>Stops waiting for keys (Escape, the page or window closing) and gives the shortcuts back.</summary>
    public void Cancel()
    {
        if (_capturing is null)
        {
            return;
        }

        _capturing = null;
        ShortcutRouter.Suspended = false;
        _prompt.Text = Idle;
        _prompt.RemoveThemeColorOverride("font_color");
    }

    /// <summary>Binds the chord, first asking whether to take it from the actions that already use it.</summary>
    private void Assign(EditorAction action, KeyChord chord)
    {
        IReadOnlyList<EditorAction> clashes = ShortcutBindings.Clashes(_ctx.Actions, action, chord);
        if (clashes.Count == 0)
        {
            ShortcutBindings.Bind(action, chord.ToString());
            return;
        }

        Dialogs.Ask(this, "Shortcut in use", $"Move {chord} to {action.Title}?",
            $"{chord} already runs {string.Join(", ", clashes.Select(c => c.Title))}, which will have no shortcut.", "Move shortcut", () =>
            {
                foreach (EditorAction other in clashes)
                {
                    ShortcutBindings.Bind(other, null);
                }

                ShortcutBindings.Bind(action, chord.ToString());
            }, modal: false);
    }

    private void AskPreset(ShortcutPreset preset) =>
        Dialogs.Ask(this, $"{preset.Name} shortcuts", $"Use the {preset.Name} keys?", $"{preset.Description}\n\nThis replaces every shortcut you changed.",
            "Use these keys", () =>
            {
                ShortcutBindings.ApplyPreset(_ctx.Actions, preset);
                _ctx.SetStatus($"Shortcuts: {preset.Name}");
            }, icon: "warning");

    private EditorAction? Selected() =>
        _tree.GetSelected() is { } item && item.GetMetadata(0).AsString() is { Length: > 0 } id ? _ctx.FindAction(id) : null;

    private void Rebuild()
    {
        string? selected = Selected()?.Id;
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        List<EditorAction> actions = _ctx.Actions
            .Where(a => !a.Id.StartsWith("file.recent.", StringComparison.Ordinal))
            .OrderBy(a => a.Menu.Length == 0 ? "~" : a.Menu, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Order)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byChord = actions
            .Select(a => (Action: a, Chord: KeyChord.Parse(ShortcutBindings.Of(a))))
            .Where(x => x.Chord is not null)
            .GroupBy(x => x.Chord!.Value)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Action.Title).ToList());
        // Actions sit under their menu (Brush, View › Render Mode), as in a keymap, so a row reads as just its title.
        var groups = new Dictionary<string, TreeItem>(StringComparer.Ordinal);
        foreach (EditorAction a in actions)
        {
            string menu = a.Menu.Length > 0 ? a.Menu.Replace("/", " › ", StringComparison.Ordinal) : "Other";
            string title = a.Title.Replace("&&", "&", StringComparison.Ordinal).TrimEnd('…');
            string now = ShortcutBindings.Of(a) ?? "";
            if (_filter.Length > 0 && TextMatch.FuzzyWords(_filter, $"{menu} {title} {a.Id} {now}") is null)
            {
                continue;
            }

            if (!groups.TryGetValue(menu, out TreeItem? group))
            {
                group = groups[menu] = _tree.CreateItem(root);
                group.SetText(0, menu);
                group.SetCustomFont(0, EditorTheme.UiFontSemibold);
                for (int c = 0; c < _tree.Columns; c++)
                {
                    group.SetSelectable(c, false);
                    group.SetCustomColor(c, EditorTheme.TextHeading);
                }
            }

            TreeItem item = _tree.CreateItem(group);
            item.SetText(0, title);
            item.SetText(1, now.Length > 0 ? now : PanelUi.Mixed);
            item.SetText(2, a.Shortcut ?? PanelUi.Mixed);
            item.SetMetadata(0, a.Id);
            item.SetCustomColor(1, now.Length > 0 ? EditorTheme.Text : EditorTheme.TextDisabled);
            item.SetCustomColor(2, a.Shortcut is null ? EditorTheme.TextDisabled : EditorTheme.TextDim);
            string tip = a.Id + (a.Description is { } d ? "\n" + d : "");
            if (KeyChord.Parse(now) is { } chord && byChord.TryGetValue(chord, out List<string>? others))
            {
                item.SetCustomColor(1, EditorTheme.Error);
                tip += $"\n{chord} also runs {string.Join(", ", others.Where(o => o != a.Title))}: the first by id wins.";
            }

            if (ShortcutBindings.IsCustom(a))
            {
                // A binding the user changed reads stronger than the defaults and can be put back from its row.
                item.SetCustomFont(1, EditorTheme.UiFontSemibold);
                item.AddButton(1, Icons.Get("reset", EditorTheme.TextDim)!, ResetButton, tooltipText: "Put the default shortcut back");
            }

            item.SetTooltipText(0, $"{menu} › {title}\n{tip}");
            item.SetTooltipText(1, tip);
            if (a.Id == selected)
            {
                item.Select(0);
            }
        }

        UpdateButtons();
    }

    /// <summary>Set, Clear and Default act on the selected row, so they are off without one (and Clear and Default
    /// when they would change nothing).</summary>
    private void UpdateButtons()
    {
        EditorAction? a = Selected();
        _set.Disabled = a is null;
        _clear.Disabled = a is null || ShortcutBindings.Of(a) is null;
        _default.Disabled = a is null || !ShortcutBindings.IsCustom(a);
    }
}
