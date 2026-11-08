using System.Text;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>The same readable result for the export dialog and saved diagnostic report.</summary>
public static class SdcExportReport
{
    private static readonly string[] s_actionWords =
        ["rebuild", "choose ", "recover ", "restore ", "check the fit", "give it with", "import it again", "set them in", "requires replacing"];
    public static bool IsReplacement(Finding f) => f.Code.StartsWith("sdc.fallback.", StringComparison.Ordinal);

    /// <summary>Findings that ask the author to take a step, including prerequisites and post-export rebuilds.</summary>
    public static bool RequiresAction(Finding f) => f.Severity == Severity.Error || f.Fix is not null
        || f.Code is "sdc.meshNotLit" or "sdc.worldNotRebuilt" or "sdc.lightingNotRebuilt" or "sdc.emitterComponents"
            or "sdc.noInstall" or "sdc.unresolved" or "sdc.sourceMissing" or "sdc.sourceChanged" or "sdc.recoveredEditor"
            or "sdc.report" or "sdc.conveyorsLeftOut" or "sdc.conveyorSettings" or "sdc.conveyorChain"
            or "sdc.property" or "sdc.untyped" or SdcCompatibility.WindowFallback
        || s_actionWords.Any(action => f.Message.Contains(action, StringComparison.OrdinalIgnoreCase));

    public static string Summary(ExportResult result)
    {
        int errors = result.Findings.Count(f => f.Severity == Severity.Error);
        int replaced = result.Findings.Count(IsReplacement);
        int warnings = result.Findings.Count(f => f.Severity == Severity.Warning && !IsReplacement(f));
        return $"{(result.Ok ? "SDC exported" : "SDC export failed")}: {Count(errors, "error")}, {Count(replaced, "automatic replacement")}, {Count(warnings, "warning")}";

        static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
    }

    public static Finding WithWorkaround(Finding f)
    {
        if (f.Severity == Severity.Info) return f;
        string? advice = f.Code switch
        {
            "roundtrip.name" => "Rename the object using ASCII letters, digits and underscores, starting with a letter, with at most 63 characters.",
            "roundtrip.duplicateName" => "Rename one of the listed objects so every object has a different name (ignoring case).",
            "roundtrip.faceVertices" => "Split the affected faces into convex faces with at most 16 corners.",
            "roundtrip.nonConvexFace" or "roundtrip.concaveBrush" => "Split the affected geometry into convex brushes and faces.",
            "roundtrip.nonPlanarFace" => "Flatten the affected face, or split it into planar triangles.",
            "roundtrip.degenerateFace" => "Remove collapsed faces or rebuild the affected brush with at least three distinct corners per face.",
            "roundtrip.outsideWorld" => "Move or resize the listed objects inside the original game's world bounds.",
            "roundtrip.materialName" => "Choose an original-game material with an ASCII asset path without quotes.",
            "roundtrip.text" => "Shorten or replace the reported text with characters supported by the original game.",
            "roundtrip.zones" => "Reduce the number of zones to at most 64, including the outside zone.",
            "roundtrip.triggerLights" => "Reduce the number of triggered lights to the limit stated above.",
            "roundtrip.unknownClass" => "Replace the listed actor with a class supplied by the original game or restore its original package.",
            "roundtrip.geOptions" or "roundtrip.gePoints" => "Correct the listed traversal event's points or options to match the original-game limits stated above.",
            "sdc.conveyorsLeftOut" => "Use Export for Original Editor, import the generated RE+ belt classes and actors, then build and save the map in RE+.",
            "sdc.conveyorSettings" or "sdc.conveyorChain" => "Use a current RE+ belt class, or split the affected conveyor into chains within the stated limit before exporting again.",
            "sdc.property" => "Correct the reported property's value or type to match the original game's class, or set it in RE+ after export.",
            "sdc.readBack" or "sdc.actorLevel" or "sdc.missing" or "sdc.ges" => "Keep the current map and this report, then report the export failure. The previous SDC has been preserved.",
            _ => null,
        };
        return advice is null ? f : f with { Message = f.Message + " Workaround: " + advice };
    }

    public static string Text(ExportResult result)
    {
        var text = new StringBuilder("# ").Append(Summary(result)).AppendLine().AppendLine();
        text.AppendLine(result.Ok ? "Any compatible replacements affect only the exported copy. Your project is unchanged."
            : "No SDC was written. Any existing SDC and your project are unchanged. Fix the errors below and export again.");
        foreach (string file in result.Files) text.AppendLine("File: " + file);
        Section("Errors requiring attention", f => f.Severity == Severity.Error);
        Section("Automatic compatible replacements", IsReplacement);
        Section("Warnings", f => f.Severity == Severity.Warning && !IsReplacement(f));
        Section("Notes", f => f.Severity == Severity.Info && !IsReplacement(f));
        return text.ToString();

        void Section(string title, Func<Finding, bool> keep)
        {
            Finding[] entries = result.Findings.Where(keep).ToArray();
            if (entries.Length == 0) return;
            text.AppendLine().Append("## ").AppendLine(title).AppendLine();
            foreach (Finding f in entries)
            {
                string line = EscapeMarkdown(f.ToString());
                text.AppendLine(RequiresAction(f) ? $"**{line}**" : line).AppendLine();
            }
        }
    }

    private static string EscapeMarkdown(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
