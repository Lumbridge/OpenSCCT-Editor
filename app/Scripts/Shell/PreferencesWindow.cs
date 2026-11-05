using Godot;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Updates;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Tools;
using OpenScctEditor.Shell.Updates;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell;

/// <summary>
/// Edit > Preferences: a category list on the left (General, Views, Shortcuts) and the chosen page on the right.
/// General has autosave, backups, the UI scale and updates; Views the field of view, the grid and the view colours; Shortcuts
/// is the <see cref="ShortcutEditor"/>. Every change is saved and applied at once; there is nothing to confirm.
/// </summary>
public sealed partial class PreferencesWindow : AcceptDialog
{
    private static readonly Vector2I SmallestSize = new(520, 360);

    private readonly List<(ColorPickerButton Pick, Label Hex)> _colours = new();
    private readonly List<(Button Nav, Control Page)> _pages = new();
    private readonly Button _stockColours;
    private readonly Button _resetShortcuts;
    private CheckBox? _scaleKeepsTextures;

    public PreferencesWindow()
    {
        Title = "Preferences";
        OkButtonText = "Close";
        Exclusive = false;
        Unresizable = false;
        MinSize = SmallestSize;

        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", EditorTheme.SpaceL);
        AddChild(body);
        var nav = new VBoxContainer { Name = "Categories", CustomMinimumSize = new Vector2(EditorTheme.PreferencesNavWidth, 0) };
        nav.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
        body.AddChild(nav);
        body.AddChild(new VSeparator { ThemeTypeVariation = "PaneDivider" });
        var pages = new MarginContainer { Name = "Pages", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddChild(pages);

        var shortcuts = new ShortcutEditor(EditorContext.Instance) { Name = "Shortcuts" };
        var group = new ButtonGroup();
        foreach (Control page in new[] { General(), ViewsPage(), shortcuts })
        {
            var button = new Button
            {
                Text = page.Name,
                ThemeTypeVariation = "NavButton",
                Alignment = HorizontalAlignment.Left,
                ToggleMode = true,
                ButtonGroup = group,
                FocusMode = Control.FocusModeEnum.None,
            };
            Control shown = page;
            button.Toggled += on =>
            {
                if (on)
                {
                    ShowPage(shown);
                }
            };
            nav.AddChild(button);
            pages.AddChild(page);
            _pages.Add((button, page));
        }

        // Page actions sit at the left of the footer, apart from Close.
        _stockColours = AddButton("Stock colours", right: false, action: "stockColours");
        _stockColours.TooltipText = "Put every view colour back to the stock ones";
        _resetShortcuts = AddButton("Reset all shortcuts", right: false, action: "resetShortcuts");
        _resetShortcuts.TooltipText = "Put every default shortcut back";
        CustomAction += action =>
        {
            if (action == "stockColours")
            {
                StockColours();
            }
            else if (action == "resetShortcuts")
            {
                ShortcutBindings.ResetAll();
            }
        };
        _pages[0].Nav.ButtonPressed = true;
        ShowPage(_pages[0].Page);

        // Close only hides the window: a key capture left running would keep every shortcut switched off.
        VisibilityChanged += () =>
        {
            if (!Visible)
            {
                shortcuts.Cancel();
            }
            else
            {
                // Surface > Scale Keeps Texture Size sets the same value.
                _scaleKeepsTextures?.SetPressedNoSignal(SurfaceActions.ScaleKeepsTextures);
            }
        };
    }

    /// <summary>Opens the window centred, at a size taken from the editor window (see
    /// <see cref="EditorTheme.PreferencesSize"/>).</summary>
    public new void Show() => PopupCentered(EditorTheme.PreferencesSize(GetParent<Control>().GetViewportRect().Size));

    private void ShowPage(Control page)
    {
        foreach ((Button _, Control p) in _pages)
        {
            p.Visible = p == page;
        }

        _stockColours.Visible = page.Name == "Views";
        _resetShortcuts.Visible = page.Name == "Shortcuts";
        Dialogs.ArrangeButtons(this, _stockColours, _resetShortcuts);
    }

    private Control General()
    {
        VBoxContainer page = Page("General");
        PanelUi.Header(page, "Saving");
        Number(page, "Autosave every", "How often the open map is autosaved. 0 turns autosave off. Autosaves go to the editor's own folder, never over your map.",
            0, 120, 1, AppSettings.AutosaveMinutes, v => AppSettings.AutosaveMinutes = (int)v, "min");
        Number(page, "Autosaves per map", "How many autosaves of each map are kept before the oldest is reused.",
            1, 50, 1, AppSettings.AutosaveSlots, v => AppSettings.AutosaveSlots = (int)v);
        Number(page, "Backups per map", "Each save moves the files it replaces into the map's .backups folder; this many are kept " +
            "(ose and script saves use the same number). Saves less than a minute apart keep only the first and last. 0 keeps none.",
            0, ProjectBackups.MaxKeep, 1, ProjectBackups.Kept, v =>
            {
                try
                {
                    ProjectBackups.Kept = (int)v;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    EditorContext.Instance.Log($"Could not store the backups setting: {ex.Message}", Severity.Error);
                }
            });
        PanelUi.Header(page, "Editing");
        _scaleKeepsTextures = Check(page, "Scaling a brush keeps its texture size",
            "Scaling or resizing a brush keeps its textures the same size, so a stretched face shows more repeats. Off, " +
            "textures stretch with the brush as in UnrealEd. Also in the Surface menu.",
            SurfaceActions.ScaleKeepsTextures, v => SurfaceActions.ScaleKeepsTextures = v);
        PanelUi.Header(page, "Display");
        Number(page, "UI scale", "Size of the editor's text and controls (1 = normal).",
            Preferences.MinUiScale, Preferences.MaxUiScale, 0.05, Preferences.UiScale, v =>
            {
                Preferences.UiScale = v;
                Preferences.Apply();
            }, "×");
        PanelUi.Header(page, "Updates");
        UpdateEnvironment updates = UpdateEnvironment.Current;
        if (updates.IsRelease)
        {
            Check(page, "Check for updates when the editor starts",
                "A few seconds after each start the editor asks GitHub for a newer version. A new one shows in the " +
                "status bar; nothing installs without you. Help > Check for Updates asks at any time.",
                UpdateSettings.CheckOnStart, v => UpdateSettings.CheckOnStart = v);
            Check(page, "Offer previews (pre-releases)", "Also offer test versions published before a release.",
                UpdateSettings.Prereleases, v => UpdateSettings.Prereleases = v);
        }
        else
        {
            PanelUi.Caption(page, $"Updates are off when running from source ({EditorRelease.Display}): update with git pull.");
        }

        return page.GetParent<Control>();
    }

    /// <summary>A yes/no setting: a check box with its name, the help as its tooltip.</summary>
    private static CheckBox Check(Control page, string title, string help, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Text = title, TooltipText = help, ButtonPressed = value, FocusMode = Control.FocusModeEnum.None };
        box.Toggled += v => changed(v);
        page.AddChild(box);
        return box;
    }

    private Control ViewsPage()
    {
        VBoxContainer page = Page("Views");
        PanelUi.Header(page, "Perspective view");
        Number(page, "Field of view (°)", "Horizontal field of view of the perspective view (the stock editor's is 90).",
            Preferences.MinFov, Preferences.MaxFov, 1, Preferences.FieldOfView, v =>
            {
                Preferences.FieldOfView = v;
                Preferences.ApplyViews();
            });
        Number(page, "Actor icon size", "How big the icons of lights, starts, triggers and other actors without a mesh are " +
            "drawn when near; far ones shrink to about half. View > Show > Actor Icons turns them off.",
            Preferences.MinActorIconSize, Preferences.MaxActorIconSize, 1, Preferences.ActorIconSize, v =>
            {
                Preferences.ActorIconSize = (int)v;
                Preferences.ApplyViews();
            }, "px");
        PanelUi.Header(page, "Grid");
        Number(page, "New map grid", "The snap grid a new map starts with (each map keeps its own).",
            1, 4096, 1, Preferences.DefaultGrid, v => Preferences.DefaultGrid = (int)v, "u");
        Number(page, "Major line every", "Cells between the brighter grid lines.",
            2, 64, 1, Preferences.GridMajorEvery, v =>
            {
                Preferences.GridMajorEvery = (int)v;
                Preferences.ApplyViews();
            });

        // Neighbour snap (View > Grid > Neighbour Snap, Shift+G); the toolbar's drop-down sets the same values, so the rows
        // follow it.
        if (ToolManager.Instance is { } tools)
        {
            PanelUi.Header(page, "Snapping");
            SpinBox distance = Number(page, "Neighbour snap distance", "How close, on screen, a face must come to a neighbour's face to stick to it.",
                ToolManager.MinNeighbourPx, ToolManager.MaxNeighbourPx, 1, tools.NeighbourSnapPx, v => tools.NeighbourSnapPx = v, "px");
            const string uses = "Which drags stick to neighbours when Neighbour Snap is on.";
            CheckBox move = Check(page, "Neighbour snap: move", uses, tools.NeighbourSnapMove, v => tools.NeighbourSnapMove = v);
            CheckBox resize = Check(page, "Neighbour snap: resize (scale, extrude, builder box)", uses, tools.NeighbourSnapResize,
                v => tools.NeighbourSnapResize = v);
            CheckBox rotate = Check(page, "Neighbour snap: rotate", uses, tools.NeighbourSnapRotate, v => tools.NeighbourSnapRotate = v);
            CheckBox inLine = Check(page, "Stick in line too",
                "Faces on the same plane beside the moving face stick too (tops level with a nearby box), not only faces it touches.",
                tools.NeighbourSnapInLine, v => tools.NeighbourSnapInLine = v);
            CheckBox meshes = Check(page, "Stick to static meshes and movers", "Mesh actors count as neighbours (their mesh box).",
                tools.NeighbourSnapMeshes, v => tools.NeighbourSnapMeshes = v);
            CheckBox actors = Check(page, "Stick to other actors",
                "Point actors (lights, path nodes, sounds…) count as neighbours, by their collision size.",
                tools.NeighbourSnapActors, v => tools.NeighbourSnapActors = v);
            CheckBox volumes = Check(page, "Stick to volumes", "Volumes and other brush-shaped actors count as neighbours, by their own faces.",
                tools.NeighbourSnapVolumes, v => tools.NeighbourSnapVolumes = v);
            CheckBox baked = Check(page, "Stick to the baked world", "An imported map's baked collision counts as a neighbour while it is shown.",
                tools.NeighbourSnapBaked, v => tools.NeighbourSnapBaked = v);
            void Sync()
            {
                distance.SetValueNoSignal(tools.NeighbourSnapPx);
                move.SetPressedNoSignal(tools.NeighbourSnapMove);
                resize.SetPressedNoSignal(tools.NeighbourSnapResize);
                rotate.SetPressedNoSignal(tools.NeighbourSnapRotate);
                inLine.SetPressedNoSignal(tools.NeighbourSnapInLine);
                volumes.SetPressedNoSignal(tools.NeighbourSnapVolumes);
                meshes.SetPressedNoSignal(tools.NeighbourSnapMeshes);
                actors.SetPressedNoSignal(tools.NeighbourSnapActors);
                baked.SetPressedNoSignal(tools.NeighbourSnapBaked);
            }

            tools.Changed += Sync;
            page.TreeExiting += () => tools.Changed -= Sync;
        }

        PanelUi.Header(page, "Colours");
        foreach ((string key, string title, _) in SceneStyle.Defaults)
        {
            HBoxContainer row = PanelUi.Row(page, title, labelWidth: EditorTheme.LabelColumnWide);
            row.AddThemeConstantOverride("separation", EditorTheme.SpaceM);
            var pick = new ColorPickerButton
            {
                Color = Preferences.Colour(key),
                EditAlpha = false,
                CustomMinimumSize = EditorTheme.SwatchSize,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                ThemeTypeVariation = "Swatch",
                TooltipText = title,
                FocusMode = Control.FocusModeEnum.None,
            };
            var hex = new Label { ThemeTypeVariation = "Mono", Text = Hex(pick.Color), VerticalAlignment = VerticalAlignment.Center };
            pick.ColorChanged += c =>
            {
                hex.Text = Hex(c);
                Preferences.SetColour(key, c);
                Preferences.ApplyColours();
            };
            pick.SetMeta("key", key);
            row.AddChild(pick);
            row.AddChild(hex);
            _colours.Add((pick, hex));
        }

        return page.GetParent<Control>();
    }

    private void StockColours()
    {
        Preferences.ResetColours();
        Preferences.ApplyColours();
        foreach ((ColorPickerButton pick, Label hex) in _colours)
        {
            pick.Color = Preferences.Colour(pick.GetMeta("key").AsString());
            hex.Text = Hex(pick.Color);
        }
    }

    private static string Hex(Color c) => "#" + c.ToHtml(includeAlpha: false).ToUpperInvariant();

    /// <summary>A page: a scroll area named for its category, holding a column of form rows.</summary>
    private static VBoxContainer Page(string name)
    {
        var scroll = new ScrollContainer
        {
            Name = name,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        var page = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        scroll.AddChild(page);
        return page;
    }

    /// <summary>A number setting: its name in the wide label column and a fixed-width spin box (units in its suffix).</summary>
    private static SpinBox Number(Control page, string title, string help, double min, double max, double step, double value, Action<double> changed,
        string suffix = "")
    {
        HBoxContainer row = PanelUi.Row(page, title, help, labelWidth: EditorTheme.LabelColumnWide);
        SpinBox spin = PanelUi.Spin(row, min, max, step, value, help, EditorTheme.NumberFieldWidth);
        spin.Suffix = suffix;
        spin.ValueChanged += v => changed(v);
        return spin;
    }
}
