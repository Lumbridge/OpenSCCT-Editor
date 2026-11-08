using Godot;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Shell;

/// <summary>A persistent, scrollable result instead of an export failure hidden in the log.</summary>
public static class SdcExportDialog
{
    public static void Show(ExportResult result, bool currentMap)
    {
        if (Dialogs.Host is not { } host) return;
        var dialog = new AcceptDialog
        {
            Name = "SdcExportResult", Title = result.Ok ? "SDC export complete" : "SDC export needs attention",
            OkButtonText = "Close", Exclusive = false, Unresizable = false,
        };
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", EditorTheme.SpaceM);
        var summary = new Label { Text = SdcExportReport.Summary(result), AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(600, 0) };
        summary.AddThemeColorOverride("font_color", !result.Ok ? EditorTheme.Error
            : result.Findings.Any(f => f.Severity == Severity.Warning) ? EditorTheme.Warning : EditorTheme.Text);
        body.AddChild(summary);
        string report = SdcExportReport.Text(result);
        var reportView = new RichTextLabel
        {
            Name = "Report", SelectionEnabled = true, FitContent = false, ScrollActive = true,
            CustomMinimumSize = new Vector2(600, 300), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        reportView.AddThemeFontOverride("bold_font", EditorTheme.UiFontSemibold);
        reportView.PushBold();
        reportView.AddText(SdcExportReport.Summary(result) + "\n\n");
        reportView.Pop();
        reportView.AddText(result.Ok ? "Any replacements affect only the exported copy. Your project is unchanged.\n\n"
            : "No SDC was written. Any existing SDC and your project are unchanged.\n\n");
        foreach (string file in result.Files) reportView.AddText("File: " + file + "\n");
        Section("Errors requiring attention", f => f.Severity == Severity.Error);
        Section("Automatic compatible replacements", SdcExportReport.IsReplacement);
        Section("Warnings", f => f.Severity == Severity.Warning && !SdcExportReport.IsReplacement(f));
        Section("Notes", f => f.Severity == Severity.Info);
        void Section(string title, Func<Finding, bool> keep)
        {
            var entries = result.Findings.Where(keep).ToArray();
            if (entries.Length == 0) return;
            reportView.PushBold(); reportView.AddText("\n" + title + "\n\n"); reportView.Pop();
            foreach (Finding finding in entries)
            {
                bool bold = SdcExportReport.RequiresAction(finding);
                if (bold) reportView.PushBold();
                reportView.AddText(finding.ToString() + "\n\n");
                if (bold) reportView.Pop();
            }
        }
        body.AddChild(reportView);
        dialog.AddChild(body);
        Button copy = dialog.AddButton("Copy report", right: false, action: "copy");
        Button? objects = currentMap ? dialog.AddButton("Show affected objects", right: false, action: "objects") : null;
        dialog.GetOkButton().ThemeTypeVariation = "PrimaryButton";
        Dialogs.ArrangeButtons(dialog, objects is null ? [copy] : [copy, objects]);
        dialog.CustomAction += action =>
        {
            if (action == "copy") Background.EditorClipboard.Set(report);
            if (action == "objects") { DockTabs.ShowPanel("Map Check"); dialog.Hide(); dialog.QueueFree(); }
        };
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        host.AddChild(dialog);
        Vector2 available = dialog.GetTree().Root.GetVisibleRect().Size * .85f;
        dialog.PopupCentered(new Vector2I((int)Math.Min(900, available.X), (int)Math.Min(600, available.Y)));
    }
}
