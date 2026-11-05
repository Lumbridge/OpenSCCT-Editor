using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Updates;

namespace OpenScctEditor.Shell.Updates;

/// <summary>
/// "OpenSCCT Editor 0.2.0 is available": the version, its date and download size, the start of its release notes with
/// a link to the full ones, and Update now (Download… when the folder cannot be written) / Later / Skip this version.
/// Laid out like the editor's other dialogs (<see cref="Dialogs.Ask"/>); not modal.
/// </summary>
public static class UpdateDialog
{
    public static void Show(Node parent, UpdateOffer offer, bool canInstall, bool skippedBefore, Action updateNow, Action skip)
    {
        var d = new ConfirmationDialog
        {
            Title = "Update Available",
            OkButtonText = canInstall ? "Update now" : "Download…",
            CancelButtonText = "Later",
            Exclusive = false,
        };
        int width = EditorTheme.DialogMinWidth + 120;
        // Wrapped text needs a width of its own, or the dialog grows taller than the window (as in Dialogs.Message).
        int textWidth = width - 2 * EditorTheme.SpaceXl;
        var body = new VBoxContainer { CustomMinimumSize = new Vector2(textWidth, 0) };
        body.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        d.AddChild(body);

        body.AddChild(new Label
        {
            Text = $"{EditorRelease.Product} {EditorRelease.Describe(offer.Version)} is available",
            ThemeTypeVariation = "Title",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(textWidth, 0),
        });
        string released = offer.Published is { } p ? $" Released {p.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)}." : "";
        string detail = $"You have {UpdateEnvironment.Version}.{released} Download {UpdateChecker.Size(offer.Archive.Size)}.";
        if (skippedBefore)
        {
            detail += " You chose to skip this version earlier.";
        }

        if (!canInstall)
        {
            detail += "\n" + UpdateStager.ReadOnlyMessage;
        }

        body.AddChild(new Label
        {
            Text = detail,
            ThemeTypeVariation = "Caption",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(textWidth, 0),
        });

        IReadOnlyList<string> notes = offer.Notes;
        if (notes.Count > 0)
        {
            // The preview is a few lines at most: as tall as its text, with the full notes a link away.
            body.AddChild(new RichTextLabel
            {
                BbcodeEnabled = false,
                Text = string.Join("\n", notes),
                FitContent = true,
                ScrollActive = false,
                SelectionEnabled = true,
                CustomMinimumSize = new Vector2(textWidth, 0),
            });
        }

        var full = new LinkButton
        {
            Text = "Full release notes",
            Underline = LinkButton.UnderlineMode.OnHover,
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        full.Pressed += () => UpdatePart.OpenPage(offer.Page);
        body.AddChild(full);

        d.GetOkButton().ThemeTypeVariation = "PrimaryButton";
        Button skipButton = d.AddButton("Skip this version", right: false, action: "skip");
        skipButton.TooltipText = "No reminders for this version; a newer one shows again";
        Dialogs.ArrangeButtons(d, skipButton);
        d.Confirmed += () =>
        {
            d.QueueFree();
            updateNow();
        };
        d.Canceled += d.QueueFree;
        d.CustomAction += action =>
        {
            if (action == "skip")
            {
                d.Hide();
                d.QueueFree();
                skip();
            }
        };
        parent.AddChild(d);
        d.PopupCentered(new Vector2I(width, 0));
    }
}
