using System.Diagnostics;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell;

/// <summary>
/// Finds an OpenSCCT game and starts it on a built map (<c>--svm-maps</c> + <c>--svm-play</c>, the game's offline
/// play path). <see cref="GameExe"/> finds it, as for <c>ose play</c>.
/// </summary>
public static class GameLauncher
{
    public static string? FindGame() => GameExe.Find();

    public static IReadOnlyList<string> UnrealArguments(string output, PlayOptions play, string? content, string? unrealProject = null)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        if (!File.Exists(Path.Combine(folder, "manifest.json")) || !File.Exists(Path.Combine(folder, "render", "world.glb")))
            throw new InvalidOperationException($"The compiled map at {folder} is missing its manifest or render/world.glb. Rebuild it before playing in Unreal.");
        string id = Path.GetFileName(folder);
        if (id.Length is 0 or > 31 || id.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
            throw new InvalidOperationException($"Unreal needs a map ID of 1–31 letters, digits, underscores or hyphens. This map's ID is '{id}'.");

        var args = new List<string>
        {
            "--scct-editor-map=" + folder,
            "-ScctEditorSide=" + play.Side,
            "-ScctEditorMode=" + play.Mode,
            FormattableString.Invariant($"-ScctEditorBots={play.Bots}"),
            "-ScctEditorDifficulty=" + play.Difficulty,
            FormattableString.Invariant($"-ScctEditorMatchPlayers={play.MatchPlayers}"),
            "-ScctEditorPlayLevel=" + (play.Rules == "playlevel" ? "1" : "0"),
        };
        if (unrealProject is not null)
        {
            if (!File.Exists(unrealProject) || !Path.GetExtension(unrealProject).Equals(".uproject", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Set OSE_UNREAL_PROJECT to the built OpenSCCTUnreal.uproject when launching through UnrealEditor.exe.");
            args.InsertRange(0, [Path.GetFullPath(unrealProject), "-game"]);
        }
        content ??= EditorSteamContent.ActiveRoot;
        if (!string.IsNullOrEmpty(content))
            args.Add("--svm-content=" + Path.GetFullPath(content));
        if (play.Smoke is "volumetric-fast" or "volumetric-full")
        {
            args.Add("-ScctEditorResponsiveSmoke=1");
            args.Add("--svm-setting=Graphics.ResponsiveSmokeQuality=" + (play.Smoke == "volumetric-full" ? "2" : "0"));
        }
        return args;
    }

    /// <summary>
    /// The arguments that play <paramref name="mapId"/> from <paramref name="mapsRoot"/> with the map's play options,
    /// spawning the player once at <paramref name="startAt"/> when given, with the selected smoke mode.
    /// </summary>
    public static IReadOnlyList<string> PlayArguments(string mapsRoot, string mapId, PlayOptions play, PlayStart? startAt = null)
    {
        var args = new List<string>
        {
            "--",
            "--svm-maps=" + mapsRoot,
            "--svm-play=" + mapId,
            "--side=" + play.Side,
            "--mode=" + play.Mode,
            "--rules=" + play.Rules,
            "--bots=" + play.Bots.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--difficulty=" + play.Difficulty,
        };
        if (play.Players > 0)
        {
            // Without it the game counts the playtest's own players (its bots and you).
            args.Add("--players=" + play.Players.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (EditorServices.ContentRoot is { } content)
        {
            args.Add("--svm-content=" + content);
        }

        if (startAt is { } at)
        {
            args.Add(at.Argument);
        }

        if (play.Smoke is "volumetric-fast" or "volumetric-full")
        {
            args.Add("--svm-responsive-smoke");
            args.Add("--svm-smoke-mode=" + (play.Smoke == "volumetric-full" ? "full" : "fast"));
        }

        return args;
    }

    /// <summary>Starts the game; returns the process id, or throws when it cannot start.</summary>
    public static int Play(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        return ExternalPrograms.Start(info);
    }
}
