using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell;

/// <summary>
/// The editor's preferences that change how it looks: the perspective field of view, the grid, the view colours and
/// the UI scale. They are stored per user in settings.cfg (<see cref="AppSettings"/>), applied at startup before the
/// map opens, and applied again at once when the Preferences window changes one. Edit > Preferences opens the window.
/// </summary>
[ShellPart(Order = -100)]
public sealed class Preferences : IShellPart
{
    public const double MinFov = 60, MaxFov = 120;
    public const double MinUiScale = 0.75, MaxUiScale = 2;
    private const string Views = "views", Colours = "colours", Ui = "ui";
    /// <summary>Seconds without a colour change before the map is drawn again.</summary>
    private const double ColourSettle = 0.3;
    private static MainWindow? s_window;
    private static int s_colourVersion;
    private PreferencesWindow? _dialog;

    /// <summary>Horizontal field of view of the perspective view, in degrees (the stock editor's is 90).</summary>
    public static double FieldOfView
    {
        get => Math.Clamp(AppSettings.GetDouble(Views, "fov", 90), MinFov, MaxFov);
        set => AppSettings.Set(Views, "fov", Math.Clamp(value, MinFov, MaxFov));
    }

    /// <summary>The snap grid a new map starts with, in units.</summary>
    public static int DefaultGrid
    {
        get => Math.Clamp(AppSettings.GetInt(Views, "grid_default", 16), 1, 4096);
        set => AppSettings.Set(Views, "grid_default", Math.Clamp(value, 1, 4096));
    }

    /// <summary>A major grid line every this many cells.</summary>
    public static int GridMajorEvery
    {
        get => Math.Clamp(AppSettings.GetInt(Views, "grid_major", 8), 2, 64);
        set => AppSettings.Set(Views, "grid_major", Math.Clamp(value, 2, 64));
    }

    /// <summary>The size of the editor's text and controls (1 = normal).</summary>
    public static double UiScale
    {
        get => Math.Clamp(AppSettings.GetDouble(Ui, "scale", 1), MinUiScale, MaxUiScale);
        set => AppSettings.Set(Ui, "scale", Math.Clamp(value, MinUiScale, MaxUiScale));
    }

    /// <summary>A view colour (<see cref="SceneStyle.Defaults"/> keys): the user's, or the stock one.</summary>
    public static Color Colour(string key)
    {
        Color fallback = SceneStyle.Defaults.First(d => d.Key == key).Default;
        string html = AppSettings.GetString(Colours, key, "");
        return html.Length > 0 && Color.HtmlIsValid(html) ? Color.FromHtml(html) : fallback;
    }

    public static void SetColour(string key, Color c) => AppSettings.Set(Colours, key, c.ToHtml(includeAlpha: false));

    /// <summary>Puts every colour back to the stock ones.</summary>
    public static void ResetColours() => AppSettings.EraseSection(Colours);

    public void Attach(MainWindow window, EditorContext context)
    {
        s_window = window;
        Apply();
        context.AddAction(new EditorAction("edit.preferences", "Preferences…", "Edit", Open)
        {
            Shortcut = "Ctrl+,",
            Order = 900,
            Description = "Autosave, backups, views, colours, UI scale and keyboard shortcuts",
        });
    }

    /// <summary>Applies the stored field of view and grid to the open views (cheap).</summary>
    public static void ApplyViews()
    {
        Apply();
        ViewportLayout.Instance?.Restyle(colours: false);
    }

    /// <summary>Applies the stored colours: the map is drawn again once the colours stop changing for a moment, so
    /// dragging in a colour picker does not rebuild the scene on every step.</summary>
    public static void ApplyColours()
    {
        Apply();
        int version = ++s_colourVersion;
        s_window?.GetTree().CreateTimer(ColourSettle).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() =>
        {
            if (version == s_colourVersion)
            {
                ViewportLayout.Instance?.Restyle(colours: true);
            }
        }));
    }

    /// <summary>Puts the stored preferences into effect for whatever is drawn from now on.</summary>
    public static void Apply()
    {
        ViewCamera.FieldOfView = FieldOfView;
        ViewGrid.MajorEvery = GridMajorEvery;
        foreach ((string key, _, _) in SceneStyle.Defaults)
        {
            SceneStyle.Set(key, Colour(key));
        }

        if (s_window?.GetTree()?.Root is { } root)
        {
            root.ContentScaleFactor = (float)UiScale;
        }
    }

    private void Open()
    {
        if (s_window is null)
        {
            return;
        }

        if (_dialog is null || !GodotObject.IsInstanceValid(_dialog))
        {
            _dialog = new PreferencesWindow();
            s_window.AddChild(_dialog);
        }

        _dialog.Show();
    }
}
