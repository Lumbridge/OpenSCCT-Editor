using Godot;
using OpenScct.Editor.Core.Updates;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Shell.Updates;

namespace OpenScctEditor.Shell;

/// <summary>
/// Help > About: the editor's name and version, the commit it was built from, the Godot and .NET versions it runs on,
/// links to the project pages and the third-party notices, and the fan-project disclaimer. Laid out like the editor's
/// other dialogs (<see cref="Dialogs.Inform"/>); not modal. Copy Details puts the version lines on the clipboard for a
/// bug report.
/// </summary>
public static class AboutDialog
{
    public const string ProjectPage = "https://github.com/" + EditorRelease.Owner + "/" + EditorRelease.Repo;
    public const string OpenScctPage = "https://github.com/Lumbridge/OpenSCCT";

    /// <summary>The disclaimer, worded as OpenSCCT's README words it.</summary>
    public const string Disclaimer = "OpenSCCT Editor is a fan project, not affiliated with or endorsed by Ubisoft. "
        + "Splinter Cell and Chaos Theory are trademarks of Ubisoft Entertainment.";

    private static AcceptDialog? s_open;

    /// <summary>Opens About, or brings it forward when it is already open. Without a window it does nothing.</summary>
    public static void Show()
    {
        if (Dialogs.Host is not { } host)
        {
            return;
        }

        if (s_open is { } open && GodotObject.IsInstanceValid(open) && open.Visible)
        {
            open.GrabFocus();
            return;
        }

        var d = new AcceptDialog { Title = "About " + EditorRelease.Product, OkButtonText = "Close", Exclusive = false };
        int width = EditorTheme.DialogMinWidth + 60;
        // Wrapped text needs a width of its own, or the dialog grows taller than the window (as in Dialogs.Message).
        int textWidth = width - 2 * EditorTheme.SpaceXl - EditorTheme.IconSizeHero * 2 - EditorTheme.SpaceL;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceL);
        d.AddChild(row);
        row.AddChild(new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://branding/icon.svg"),
            CustomMinimumSize = new Vector2(EditorTheme.IconSizeHero * 2, EditorTheme.IconSizeHero * 2),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        });

        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        row.AddChild(body);
        body.AddChild(new Label { Text = EditorRelease.Product, ThemeTypeVariation = "Title" });
        body.AddChild(Wrapped("A map editor for Splinter Cell: Chaos Theory Versus maps, made for OpenSCCT.", textWidth));

        var facts = new GridContainer { Columns = 2 };
        body.AddChild(facts);
        foreach ((string name, string value) in Facts())
        {
            facts.AddChild(new Label { Text = name, ThemeTypeVariation = "Caption" });
            facts.AddChild(new Label { Text = value });
        }

        var links = new HBoxContainer();
        links.AddThemeConstantOverride("separation", EditorTheme.SpaceL);
        body.AddChild(links);
        links.AddChild(Link("Project page", ProjectPage, ProjectPage));
        links.AddChild(Link("OpenSCCT", OpenScctPage, OpenScctPage));
        string notices = NoticesPath();
        links.AddChild(Link("Third-party notices", notices, "The licences of the components the editor includes"));

        body.AddChild(Wrapped(Disclaimer, textWidth));

        d.GetOkButton().ThemeTypeVariation = "PrimaryButton";
        Button copy = d.AddButton("Copy Details", right: false, action: "copy");
        copy.TooltipText = "Copies the version, commit, Godot and .NET lines, for a bug report";
        Dialogs.ArrangeButtons(d, copy);
        d.CustomAction += action =>
        {
            if (action == "copy")
            {
                EditorClipboard.Set(DetailsText());
                EditorContext.Instance.SetStatus("Copied the editor's version details");
            }
        };
        d.Confirmed += d.QueueFree;
        d.Canceled += d.QueueFree;
        host.AddChild(d);
        s_open = d;
        d.PopupCentered(new Vector2I(width, 0));
    }

    /// <summary>The version lines About shows, one "name: value" per line.</summary>
    public static string DetailsText() =>
        string.Join("\n", Facts().Select(f => $"{f.Name}: {f.Value}").Prepend(EditorRelease.Product));

    private static IEnumerable<(string Name, string Value)> Facts()
    {
        string kind = EditorRelease.IsDevBuild ? "development build" : UpdateEnvironment.Current.IsRelease ? "release" : "local build";
        yield return ("Version", $"{EditorRelease.Version} ({kind})");
        yield return ("Commit", EditorRelease.ShortCommit ?? "not recorded");
        yield return ("Godot", Engine.GetVersionInfo()["string"].AsString());
        yield return (".NET", System.Environment.Version.ToString());
        yield return ("Platform", $"{EditorRelease.Platform}, {OS.GetName()} {OS.GetVersion()}");
    }

    /// <summary>The third-party notices: THIRD-PARTY-NOTICES.txt beside a release's exe (a .txt opens in Notepad on
    /// any Windows), else the copy on GitHub.</summary>
    private static string NoticesPath()
    {
        string release = Path.Combine(UpdateEnvironment.Current.Install, "THIRD-PARTY-NOTICES.txt");
        return File.Exists(release) ? release : ProjectPage + "/blob/main/THIRD-PARTY-NOTICES.md";
    }

    private static Label Wrapped(string text, int width) => new()
    {
        Text = text,
        ThemeTypeVariation = "Caption",
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        CustomMinimumSize = new Vector2(width, 0),
    };

    private static LinkButton Link(string text, string target, string tooltip)
    {
        var link = new LinkButton
        {
            Text = text,
            Underline = LinkButton.UnderlineMode.OnHover,
            FocusMode = Control.FocusModeEnum.All,
            TooltipText = tooltip,
        };
        link.Pressed += () => ExternalPrograms.Open(target);
        return link;
    }
}
