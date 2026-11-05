using Godot;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell;

/// <summary>
/// Native OS file dialogs and the editor's questions (unsaved changes, recovering an autosave, moving a shortcut),
/// all laid out one way: a title line and a caption, the primary answer rightmost. Only the questions asked before
/// closing (unsaved changes, a build or export still running) are modal; everything else in the editor is modeless, so
/// no dialog blocks the viewports.
/// </summary>
public static class Dialogs
{
    /// <summary>The node dialogs attach to (the main window).</summary>
    public static Node? Host { get; set; }

    /// <summary>The unsaved-changes question while it is open.</summary>
    private static ConfirmationDialog? s_saveQuestion;

    /// <summary>The quit-while-busy question while it is open.</summary>
    private static ConfirmationDialog? s_busyQuestion;

    /// <summary>The modal (exclusive) question while it is open: Godot lets a window have only one.</summary>
    private static Window? s_modal;

    public static void OpenFile(string title, string[] filters, Action<string> done, string? startDir = null) =>
        Show(FileDialog.FileModeEnum.OpenFile, title, filters, done, startDir);

    /// <summary>An open dialog with choices beside the file name (an option list each, shown in the native dialog, worded
    /// by its labels); <paramref name="done"/> gets the path and each choice's picked value.</summary>
    public static void OpenFile(string title, string[] filters,
        IReadOnlyList<(string Name, string Label, string[] Values, string[] Labels, int Default)> choices,
        Action<string, IReadOnlyDictionary<string, string>> done, string? startDir = null)
    {
        FileDialog? dialog = null;
        dialog = Show(FileDialog.FileModeEnum.OpenFile, title, filters, path => done(path, Picked(dialog!, choices)), startDir,
            setUp: d => AddChoices(d, choices));
    }

    public static void OpenFolder(string title, Action<string> done, string? startDir = null) =>
        Show(FileDialog.FileModeEnum.OpenDir, title, Array.Empty<string>(), done, startDir);

    public static void SaveFile(string title, string[] filters, string suggestedName, Action<string> done, string? startDir = null) =>
        Show(FileDialog.FileModeEnum.SaveFile, title, filters, done, startDir, suggestedName);

    /// <summary>A save dialog with choices beside the file name (an option list each, shown in the native dialog, worded
    /// by its labels); <paramref name="done"/> gets the path and each choice's picked value.</summary>
    public static void SaveFile(string title, string[] filters, string suggestedName,
        IReadOnlyList<(string Name, string Label, string[] Values, string[] Labels, int Default)> choices,
        Action<string, IReadOnlyDictionary<string, string>> done, string? startDir = null)
    {
        FileDialog? dialog = null;
        dialog = Show(FileDialog.FileModeEnum.SaveFile, title, filters, path => done(path, Picked(dialog!, choices)), startDir, suggestedName,
            d => AddChoices(d, choices));
    }

    private static void AddChoices(FileDialog d, IReadOnlyList<(string Name, string Label, string[] Values, string[] Labels, int Default)> choices)
    {
        foreach ((string _, string label, string[] _, string[] labels, int def) in choices)
        {
            d.AddOption(label, labels, def);
        }
    }

    /// <summary>Each choice's picked value (its default when the dialog did not say).</summary>
    private static Dictionary<string, string> Picked(FileDialog dialog,
        IReadOnlyList<(string Name, string Label, string[] Values, string[] Labels, int Default)> choices)
    {
        Godot.Collections.Dictionary picked = dialog.GetSelectedOptions();
        var values = new Dictionary<string, string>();
        foreach ((string name, string label, string[] options, string[] _, int def) in choices)
        {
            int i = picked.TryGetValue(label, out Variant v) ? v.AsInt32() : def;
            values[name] = options[Math.Clamp(i, 0, options.Length - 1)];
        }

        return values;
    }

    /// <summary>Asks to save, discard or cancel. Runs <paramref name="save"/> or <paramref name="discard"/>; cancel does
    /// nothing. While the question is already open (the window closed twice) it is brought forward instead of asked
    /// again.</summary>
    public static void AskSave(string mapTitle, Action save, Action discard)
    {
        if (Host is null)
        {
            discard();
            return;
        }

        if (s_saveQuestion is { } open && GodotObject.IsInstanceValid(open) && open.Visible)
        {
            open.GrabFocus();
            return;
        }

        s_saveQuestion = Ask(Host, "Unsaved Changes", $"Save the changes to {mapTitle}?", "Your changes are lost if you don\u2019t save them.", "Save", save,
            alternative: "Don\u2019t save", alternativeChosen: discard, icon: "warning");
    }

    /// <summary>Asks whether to quit while <paramref name="running"/> (a build, an export...) is still going: quitting
    /// stops it. Runs <paramref name="quit"/> on yes. While the question is open it is brought forward instead of asked
    /// again.</summary>
    public static void AskQuitWhileBusy(string running, Action quit)
    {
        if (Host is null)
        {
            quit();
            return;
        }

        if (s_busyQuestion is { } open && GodotObject.IsInstanceValid(open) && open.Visible)
        {
            open.GrabFocus();
            return;
        }

        s_busyQuestion = Ask(Host, "Still Working", $"{running} is still running. Quit anyway?",
            "Quitting stops it, and what it was writing may be left incomplete.", "Quit", quit, icon: "warning");
    }

    /// <summary>
    /// A question in a dialog: a title line, an optional explanation and icon, and the answers right-aligned with the
    /// primary one (<paramref name="confirm"/>) last. A destructive <paramref name="alternative"/> ("Don't save") sits
    /// apart on the left, so it is never where the default is. A <paramref name="modal"/> dialog dims the window
    /// behind it. Only one question is modal at a time (Godot allows one exclusive dialog per window): one asked while
    /// another modal question is open is not. The dialog frees itself once answered.
    /// </summary>
    public static ConfirmationDialog Ask(Node parent, string windowTitle, string question, string detail, string confirm, Action confirmed,
        string cancel = "Cancel", Action? canceled = null, string? alternative = null, Action? alternativeChosen = null, string? icon = null,
        bool modal = true)
    {
        if (modal && s_modal is { } other && GodotObject.IsInstanceValid(other) && other.Visible)
        {
            modal = false;
        }

        var d = new ConfirmationDialog
        {
            Title = windowTitle,
            OkButtonText = confirm,
            CancelButtonText = cancel,
            Exclusive = modal,
        };
        d.AddChild(Message(question, detail, icon));
        d.GetOkButton().ThemeTypeVariation = "PrimaryButton";
        if (alternative is null)
        {
            ArrangeButtons(d);
        }
        else
        {
            ArrangeButtons(d, d.AddButton(alternative, right: false, action: "alternative"));
        }

        d.Confirmed += () =>
        {
            d.QueueFree();
            confirmed();
        };
        d.Canceled += () =>
        {
            d.QueueFree();
            canceled?.Invoke();
        };
        d.CustomAction += action =>
        {
            if (action == "alternative")
            {
                d.Hide();
                d.QueueFree();
                alternativeChosen?.Invoke();
            }
        };
        parent.AddChild(d);
        Action? lift = modal ? Dim(d) : null;
        if (modal)
        {
            s_modal = d;
        }

        d.PopupCentered(new Vector2I(EditorTheme.DialogMinWidth, 0));
        if (lift is not null)
        {
            // A modal dialog Godot refused (the window already has another exclusive child, such as a file dialog)
            // must not leave its scrim over the window, and is still asked: as a modeless question.
            if (!d.Visible)
            {
                lift();
                if (ReferenceEquals(s_modal, d))
                {
                    s_modal = null;
                }

                d.Exclusive = false;
                d.PopupCentered(new Vector2I(EditorTheme.DialogMinWidth, 0));
            }
            else
            {
                EditorLifetime.Defer(() =>
                {
                    if (!GodotObject.IsInstanceValid(d) || !d.Visible)
                    {
                        lift();
                    }
                });
            }
        }

        return d;
    }

    /// <summary>A message with one answer (OK), laid out like <see cref="Ask"/>; not modal. An optional
    /// <paramref name="action"/> ("Open Log") sits on the left and closes the message too. Without a window (no
    /// <see cref="Host"/>) it does nothing.</summary>
    public static void Inform(string windowTitle, string title, string detail, string icon = "info", string? action = null,
        Action? actionChosen = null)
    {
        if (Host is null)
        {
            return;
        }

        var d = new AcceptDialog { Title = windowTitle, OkButtonText = "OK", Exclusive = false };
        d.AddChild(Message(title, detail, icon));
        d.GetOkButton().ThemeTypeVariation = "PrimaryButton";
        if (action is null)
        {
            ArrangeButtons(d);
        }
        else
        {
            ArrangeButtons(d, d.AddButton(action, right: false, action: "action"));
            d.CustomAction += chosen =>
            {
                if (chosen == "action")
                {
                    d.Hide();
                    d.QueueFree();
                    actionChosen?.Invoke();
                }
            };
        }

        d.Confirmed += d.QueueFree;
        d.Canceled += d.QueueFree;
        Host.AddChild(d);
        d.PopupCentered(new Vector2I(EditorTheme.DialogMinWidth, 0));
    }

    /// <summary>A dialog's message: a title line and a caption under it, with an icon on the left when given.</summary>
    private static Control Message(string title, string detail, string? icon)
    {
        int textWidth = EditorTheme.DialogMinWidth - 2 * EditorTheme.SpaceXl - (icon is null ? 0 : EditorTheme.IconSizeHero + EditorTheme.SpaceL);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceL);
        if (icon is not null)
        {
            row.AddChild(new TextureRect
            {
                Texture = Icons.Get(icon, icon == "warning" ? EditorTheme.Warning : EditorTheme.Info, EditorTheme.IconSizeHero),
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            });
        }

        var text = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        row.AddChild(text);
        text.AddChild(new Label
        {
            Text = title,
            ThemeTypeVariation = "Title",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(textWidth, 0),
        });
        if (detail.Length > 0)
        {
            text.AddChild(new Label
            {
                Text = detail,
                ThemeTypeVariation = "Caption",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(textWidth, 0),
            });
        }

        return row;
    }

    /// <summary>Lays a dialog's buttons out the editor's way: right-aligned, Cancel before the primary answer, and
    /// destructive answers or page actions (<paramref name="left"/>) alone at the far left. Call it again after showing
    /// or hiding one of the buttons.</summary>
    public static void ArrangeButtons(AcceptDialog d, params Button[] left)
    {
        const string gapName = "LeftGap";
        Button ok = d.GetOkButton();
        HBoxContainer row = ok.GetParent<HBoxContainer>();
        // Godot centres the buttons between spacers (plain Controls) and shows a button's spacer again with it;
        // without them the row packs to its end.
        foreach (Node child in row.GetChildren())
        {
            if (child.GetType() == typeof(Control) && child.Name != gapName)
            {
                ((Control)child).Visible = false;
            }
        }

        row.Alignment = BoxContainer.AlignmentMode.End;
        if (d is ConfirmationDialog c)
        {
            row.MoveChild(c.GetCancelButton(), -1);
        }

        row.MoveChild(ok, -1);
        for (int i = 0; i < left.Length; i++)
        {
            row.MoveChild(left[i], i);
        }

        if (left.Length > 0)
        {
            Control gap = row.GetNodeOrNull<Control>(gapName) ?? new Control
            {
                Name = gapName,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            if (gap.GetParent() is null)
            {
                row.AddChild(gap);
            }

            row.MoveChild(gap, left.Length);
        }
    }

    /// <summary>Dims the main window behind a modal dialog until the dialog closes; returns what lifts the dim.</summary>
    private static Action? Dim(Window dialog)
    {
        if (Host is not Control host)
        {
            return null;
        }

        var scrim = new ColorRect { Name = "Scrim", Color = EditorTheme.Scrim, MouseFilter = Control.MouseFilterEnum.Stop };
        scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        host.AddChild(scrim);
        void Lift()
        {
            if (GodotObject.IsInstanceValid(scrim))
            {
                scrim.QueueFree();
            }
        }

        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible)
            {
                Lift();
            }
        };
        dialog.TreeExiting += Lift;
        return Lift;
    }

    private static FileDialog? Show(FileDialog.FileModeEnum mode, string title, string[] filters, Action<string> done, string? startDir,
        string? fileName = null, Action<FileDialog>? setUp = null)
    {
        // A native dialog is a Windows window of its own, with focus, on the user's screen.
        if (Host is null || BackgroundMode.Refuse($"the file dialog '{title}'"))
        {
            return null;
        }

        var d = new FileDialog
        {
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Title = title,
            Filters = filters,
        };
        if (startDir is not null && Directory.Exists(startDir))
        {
            d.CurrentDir = startDir;
        }

        if (fileName is not null)
        {
            d.CurrentFile = fileName;
        }

        setUp?.Invoke(d);
        void Finish(string path)
        {
            done(path);
            d.QueueFree();
        }

        d.FileSelected += Finish;
        d.DirSelected += Finish;
        d.Canceled += d.QueueFree;
        Host.AddChild(d);
        d.PopupCentered(new Vector2I(960, 600));
        return d;
    }
}
