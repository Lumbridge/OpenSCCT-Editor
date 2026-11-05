using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The Match panel's "Grey out absent" view: while it is on, the views draw faded the actors a match of the chosen
/// player count and mode leaves out of play (<see cref="MatchPlan.Absent"/>: left out, hidden, movers held hidden, and
/// with a mode the other modes' objects). They stay pickable. It turns off when another map is opened.
/// </summary>
public static class MatchGreying
{
    private static HashSet<string>? s_absent;

    public static bool On { get; private set; }

    /// <summary>Everyone in the greyed match, bots included.</summary>
    public static int Players { get; private set; }

    /// <summary>The greyed match's game mode, or null for every mode's missions.</summary>
    public static string? Mode { get; private set; }

    /// <summary>How many actors are greyed out now.</summary>
    public static int Count => On ? Absent().Count : 0;

    /// <summary>True when the actor of the open map is drawn greyed out.</summary>
    public static bool Greys(MapProject p, string id) =>
        On && ReferenceEquals(p, EditorContext.Instance.Project) && Absent().Contains(id);

    public static void Show(int players, string? mode)
    {
        if (On && players == Players && string.Equals(mode, Mode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        (On, Players, Mode) = (true, players, mode);
        Refresh();
    }

    public static void Off()
    {
        if (On)
        {
            On = false;
            Refresh();
        }
    }

    /// <summary>Forgets what is greyed and redraws.</summary>
    public static void Refresh()
    {
        s_absent = null;
        ViewportLayout.Instance?.Scene.ApplyVisibility(EditorContext.Instance.Project);
        ViewOverlays.Invalidate();
    }

    /// <summary>After an edit: works out what is greyed again, and redraws only when that changed (the views already
    /// redraw edited actors).</summary>
    public static void Edited()
    {
        HashSet<string>? before = s_absent;
        s_absent = null;
        if (!On || before is null || !Absent().SetEquals(before))
        {
            Refresh();
        }
    }

    private static HashSet<string> Absent()
    {
        MapProject p = EditorContext.Instance.Project;
        return s_absent ??= MatchPlan.Absent(p, MatchPlan.Preview(p, Players, Mode));
    }
}

/// <summary>Keeps the greyed actors up to date with edits and turns greying off for another map.</summary>
[ShellPart(Order = -17)]
public sealed class MatchGreyingPart : IShellPart
{
    public void Attach(MainWindow window, EditorContext context)
    {
        context.ProjectChanged += _ =>
        {
            if (MatchGreying.On)
            {
                MatchGreying.Edited();
            }
        };
        context.SessionReplaced += MatchGreying.Off;
    }
}
