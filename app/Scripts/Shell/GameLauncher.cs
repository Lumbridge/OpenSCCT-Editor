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

    /// <summary>
    /// The arguments that play <paramref name="mapId"/> from <paramref name="mapsRoot"/> with the map's play options,
    /// spawning the player once at <paramref name="startAt"/> when given.
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
