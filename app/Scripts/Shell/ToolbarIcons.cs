using Godot;

namespace OpenScctEditor.Shell;

/// <summary>
/// The toolbar's icons, drawn with <see cref="Icons"/> at <see cref="EditorTheme.IconSize"/> (16 px, so strokes sit on
/// whole pixels). Most take the text colour; CSG Add and Subtract and Play have colours of their own.
/// </summary>
public static class ToolbarIcons
{
    /// <summary>The badge behind the + and − of the CSG icons is cut out in the toolbar's colour.</summary>
    private const string BarColour = "#191a1e";

    private static readonly string s_faceFill = "#" + EditorTheme.Accent.ToHtml(includeAlpha: false);

    private static readonly Dictionary<string, (string Svg, Color? Color)> s_drawings = new(StringComparer.Ordinal)
    {
        ["file.new"] = ("""<path d="M3.5 1.5h5.5l3.5 3.5v9.5h-9z"/><path d="M9 1.5v3.5h3.5"/>""", null),
        ["file.open"] = ("""<path d="M1.5 3.5h4.5l1.5 1.5h7v8.5h-13z"/><path d="M1.5 7h13"/>""", null),
        ["file.save"] = ("""<path d="M2 2h9.5l2.5 2.5v9.5h-12z"/><path d="M5 2v3.5h5.5v-3.5"/><path d="M4.5 14v-4.5h7v4.5"/>""", null),
        ["edit.undo"] = ("""<path d="M5.5 2.5l-3.5 3.5 3.5 3.5"/><path d="M2 6h7.5a3.75 3.75 0 0 1 0 7.5h-3"/>""", null),
        ["edit.redo"] = ("""<path d="M10.5 2.5l3.5 3.5-3.5 3.5"/><path d="M14 6h-7.5a3.75 3.75 0 0 0 0 7.5h3"/>""", null),
        ["tools.select"] = ("""<path d="M3.5 1.5l8.5 7-4 .5 2.5 4.5-1.8 1-2.5-4.6-2.7 2.6z" fill="C"/>""", null),
        ["tools.move"] = ("""<path d="M8 1.5v13M1.5 8h13"/><path d="M6 3.5l2-2 2 2M6 12.5l2 2 2-2M3.5 6l-2 2 2 2M12.5 6l2 2-2 2"/>""", null),
        ["tools.rotate"] = ("""<path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9"/><path d="M12.5 1v3.5h-3.5"/>""", null),
        ["tools.scale"] = ("""<path d="M1.5 6.5h8v8h-8z"/><path d="M8 8l6.5-6.5M10.5 1.5h4v4"/>""", null),
        ["tools.place"] = ("""<circle cx="8" cy="4.25" r="2.5"/><path d="M3.5 14.5v-2.5a4.5 4.5 0 0 1 9 0v2.5"/>""", null),
        ["tools.brush"] = ("""<path d="M8 1.5l6 3v7l-6 3-6-3v-7z"/><path d="M2 4.5l6 3 6-3M8 7.5v7"/>""", null),
        ["tools.ge"] = ("""<path d="M4.5 1.5v13M11.5 1.5v13M4.5 4.5h7M4.5 8h7M4.5 11.5h7"/>""", null),
        ["tools.face"] = ($"""<path d="M2 5l6 3 6-3-6-3z" fill="{s_faceFill}" fill-opacity="0.6" stroke="none"/><path d="M2 5l6-3 6 3v6l-6 3-6-3z"/><path d="M2 5l6 3 6-3M8 8v6"/>""", null),
        ["tools.vertex"] = ("""<path d="M3.5 12.5l2-9 7 2.5-1.5 6.5z"/><circle cx="3.5" cy="12.5" r="1.6" fill="C"/><circle cx="5.5" cy="3.5" r="1.6" fill="C"/><circle cx="12.5" cy="6" r="1.6" fill="C"/><circle cx="11" cy="12.5" r="1.6" fill="C"/>""", null),
        ["tools.clip"] = ("""<circle cx="4" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><path d="M5.5 10.5l7-9M10.5 10.5l-7-9"/>""", null),
        ["tools.measure"] = ("""<path d="M1.5 10.5l9-9 4 4-9 9z"/><path d="M4.5 7.5l1.5 1.5M6.5 5.5l2 2M8.5 3.5l1.5 1.5"/>""", null),
        ["tools.security"] = ("""<path d="M8 1.5l5.5 2v4c0 3.5-2.5 6-5.5 7-3-1-5.5-3.5-5.5-7v-4z"/><path d="M8 5v3.5M8 10.5v.5"/>""", null),
        ["view.grid.snap"] = ("""<path d="M3.5 2v6.5a4.5 4.5 0 0 0 9 0v-6.5"/><path d="M6.5 2v6.5a1.5 1.5 0 0 0 3 0v-6.5"/><path d="M3.5 4.5h3M9.5 4.5h3"/>""", null),
        ["view.neighbourSnap"] = ("""<path d="M1.5 5.5h5v5h-5z"/><path d="M13.5 1.5v13"/><path d="M8 8h3.5M10 6l2 2-2 2"/>""", null),
        ["view.rotsnap"] = ("""<path d="M2 13.5h12.5M2 13.5l9-9"/><path d="M7.5 13.5a5.5 5.5 0 0 0-1.6-3.9"/>""", null),
        ["brush.add"] = (Csg("""<path d="M11.75 10v3.5M10 11.75h3.5"/>"""), EditorTheme.CsgAdd),
        ["brush.subtract"] = (Csg("""<path d="M10 11.75h3.5"/>"""), EditorTheme.CsgSubtract),
        ["build.build"] = ("""<path d="M2 14l6.5-6.5"/><path d="M7 4l3-2.5 4.5 4.5-2.5 3z"/>""", null),
        ["build.play"] = ("""<path d="M4 2.5l9.5 5.5-9.5 5.5z" fill="C"/>""", EditorTheme.Ok),
        ["build.check"] = ("""<circle cx="8" cy="8" r="6.5"/><path d="M5 8.25l2 2 4-4.25"/>""", null),
        ["tools.conveyor"] = ("""<circle cx="4" cy="10.5" r="2.5"/><circle cx="12" cy="10.5" r="2.5"/><path d="M4 8h8M4 13h8"/><path d="M5.5 3.5h5M9 2l1.5 1.5-1.5 1.5"/>""", null),
    };

    /// <summary>The icon of an action, or null when it has none.</summary>
    public static Texture2D? For(string actionId) =>
        s_drawings.TryGetValue(actionId, out (string Svg, Color? Color) d) ? Icons.Draw(actionId, d.Svg, d.Color ?? EditorTheme.Text) : null;

    /// <summary>A CSG icon: a small cube with a filled badge at its lower right holding the + or −.</summary>
    private static string Csg(string mark) =>
        """<path d="M6.5 1.5l5 2.5v4M6.5 1.5l-5 2.5v5.5l5 2.5M1.5 4l5 2.5 5-2.5M6.5 6.5v2.5"/>""" +
        $"""<circle cx="11.75" cy="11.75" r="4" fill="{BarColour}" stroke="none"/><circle cx="11.75" cy="11.75" r="3.25" fill="C" stroke="none"/>""" +
        mark.Replace("<path ", $"""<path stroke="{BarColour}" stroke-width="1.3" """, StringComparison.Ordinal);
}
