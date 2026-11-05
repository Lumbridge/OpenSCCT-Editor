using Godot;

namespace OpenScctEditor.Shell;

/// <summary>
/// The editor's icons: line drawings on a 16-unit grid, kept here as SVG text so the app ships no image files for
/// them and each can be drawn in any theme colour. They become <see cref="DpiTexture"/>s, which Godot rasterises
/// again for the UI scale, so they stay sharp on a high-DPI screen.
/// <para>Drawing rules: drawn inside 1.5…14.5, stroke 1.4 with round caps and joins, no fill; solid fill only for
/// small marks (dots, play, check fill, pupils). "C" in a drawing stands for its colour. Passive glyphs are drawn in
/// <see cref="EditorTheme.TextDim"/>, actions in <see cref="EditorTheme.Text"/>; icons on the <c>IconButton</c>
/// variations are drawn white and tinted by the theme.</para>
/// <para>Object type icons are named <c>type-*</c> (see <c>PanelIcons.Type</c>).</para>
/// </summary>
public static class Icons
{
    /// <summary>The marks cut out of filled icons (the tick in a checked box): the field colour.</summary>
    private const string Dark = "#16171b";

    /// <summary>The ring around the slider knob: the window background.</summary>
    private const string Backdrop = "#131417";

    private static readonly Dictionary<string, string> s_drawings = new(StringComparer.Ordinal)
    {
        // Arrows and chevrons.
        ["chevron-down"] = """<path d="M4.5 6.5l3.5 3.5 3.5-3.5"/>""",
        ["chevron-up"] = """<path d="M4.5 9.5l3.5-3.5 3.5 3.5"/>""",
        ["chevron-right"] = """<path d="M6.5 4.5l3.5 3.5-3.5 3.5"/>""",
        ["chevron-left"] = """<path d="M9.5 4.5l-3.5 3.5 3.5 3.5"/>""",
        ["spin-up"] = """<path d="M4 10l4-4 4 4" stroke-width="1.6"/>""",
        ["spin-down"] = """<path d="M4 6l4 4 4-4" stroke-width="1.6"/>""",
        ["updown"] = """<path d="M5 6.5l3-3 3 3M5 9.5l3 3 3-3"/>""",
        ["arrow-right"] = """<path d="M2.5 8h11M9.5 4l4 4-4 4"/>""",
        ["arrow-left"] = """<path d="M13.5 8h-11M6.5 4l-4 4 4 4"/>""",
        ["arrow-up"] = """<path d="M8 13.5v-11M4 6.5l4-4 4 4"/>""",
        ["arrow-down"] = """<path d="M8 2.5v11M4 9.5l4 4 4-4"/>""",

        // Checkboxes and radios.
        ["check-on"] = $"""<rect x="2.5" y="2.5" width="11" height="11" rx="2.5" fill="C"/><path d="M5 8.2l2 2 4-4.2" stroke="{Dark}" stroke-width="1.8"/>""",
        ["check-off"] = """<rect x="2.5" y="2.5" width="11" height="11" rx="2.5"/>""",
        ["check-mixed"] = $"""<rect x="2.5" y="2.5" width="11" height="11" rx="2.5" fill="C"/><path d="M5.25 8h5.5" stroke="{Dark}" stroke-width="1.8"/>""",
        ["radio-on"] = """<circle cx="8" cy="8" r="5.5"/><circle cx="8" cy="8" r="2.75" fill="C" stroke="none"/>""",
        ["radio-off"] = """<circle cx="8" cy="8" r="5.5"/>""",

        // Actions.
        ["close"] = """<path d="M4.5 4.5l7 7M11.5 4.5l-7 7"/>""",
        ["plus"] = """<path d="M8 4v8M4 8h8"/>""",
        ["minus"] = """<path d="M4 8h8"/>""",
        ["more"] = """<g fill="C" stroke="none"><circle cx="3.5" cy="8" r="1.25"/><circle cx="8" cy="8" r="1.25"/><circle cx="12.5" cy="8" r="1.25"/></g>""",
        ["menu"] = """<path d="M2.5 4h11M2.5 8h11M2.5 12h11"/>""",
        ["filter"] = """<path d="M2 3h12l-4.5 5.5v4.5l-3 1.5v-6z"/>""",
        ["search"] = """<circle cx="7" cy="7" r="4.5"/><path d="M10.5 10.5l3.5 3.5"/>""",
        ["copy"] = """<rect x="5.5" y="5.5" width="8.5" height="8.5" rx="1.5"/><path d="M10.5 5.5v-2a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2"/>""",
        ["paste"] = """<path d="M5.5 3h-1.5a1 1 0 0 0-1 1v9.5a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1v-9.5a1 1 0 0 0-1-1h-1.5"/><rect x="5.5" y="1.75" width="5" height="2.5" rx="0.75"/><path d="M5.5 8h5M5.5 11h3"/>""",
        ["trash"] = """<path d="M2.5 4.5h11M6 4.5v-1.5a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1v1.5M4 4.5l.7 9a1 1 0 0 0 1 .9h4.6a1 1 0 0 0 1-.9l.7-9M6.75 7v4.5M9.25 7v4.5"/>""",
        ["reset"] = """<path d="M3 8a5 5 0 1 0 1.5-3.6"/><path d="M4.2 1.5v3h3"/>""",
        ["refresh"] = """<path d="M13.5 6.5a5.5 5.5 0 0 0-10.3-1.5"/><path d="M2.5 9.5a5.5 5.5 0 0 0 10.3 1.5"/><path d="M3 2v3h3M13 14v-3h-3"/>""",
        ["goto"] = """<path d="M9 3h4v4M13 3l-6 6"/><path d="M11.5 9.5v3.5h-9v-9h3.5"/>""",
        ["import"] = """<path d="M8 1.5v8M5 6.5l3 3 3-3"/><path d="M2.5 10.5v3h11v-3"/>""",
        ["export"] = """<path d="M8 9.5v-8M5 4.5l3-3 3 3"/><path d="M2.5 10.5v3h11v-3"/>""",
        ["folder"] = """<path d="M1.5 4a1 1 0 0 1 1-1h3.5l1.5 1.5h5.5a1 1 0 0 1 1 1v6.5a1 1 0 0 1-1 1h-10.5a1 1 0 0 1-1-1z"/>""",
        ["link"] = """<path d="M6.5 9.5l3-3"/><path d="M7.4 4.6l1-1a2.6 2.6 0 0 1 3.7 3.7l-1 1"/><path d="M8.6 11.4l-1 1a2.6 2.6 0 0 1-3.7-3.7l1-1"/>""",
        ["unlink"] = """<path d="M7.4 4.6l1-1a2.6 2.6 0 0 1 3.7 3.7l-1 1"/><path d="M8.6 11.4l-1 1a2.6 2.6 0 0 1-3.7-3.7l1-1"/><path d="M2 2l2 2M14 14l-2-2M5.5 1.5v1.5M1.5 5.5h1.5M10.5 14.5v-1.5M14.5 10.5h-1.5"/>""",
        ["pin"] = """<path d="M8 14.5s4.5-4.3 4.5-8a4.5 4.5 0 0 0-9 0c0 3.7 4.5 8 4.5 8z"/><circle cx="8" cy="6.5" r="1.6"/>""",
        ["camera-bookmark"] = """<path d="M1.5 7.5h2l1-1.5h3.5l1 1.5h2v6.5h-9.5z"/><circle cx="6.25" cy="10.5" r="1.75"/><path d="M12.5 1.5l.75 1.5 1.65.25-1.2 1.15.3 1.6-1.5-.8-1.5.8.3-1.6-1.2-1.15 1.65-.25z" fill="C" stroke-width="0.8"/>""",
        ["drag-handle"] = """<g fill="C" stroke="none"><circle cx="6" cy="4" r="1.1"/><circle cx="10" cy="4" r="1.1"/><circle cx="6" cy="8" r="1.1"/><circle cx="10" cy="8" r="1.1"/><circle cx="6" cy="12" r="1.1"/><circle cx="10" cy="12" r="1.1"/></g>""",
        ["settings"] = """<path d="M12.59 6.27l1.56.36v2.74l-1.56.36-.13.29.86 1.36-1.94 1.94-1.36-.86-.29.13-.36 1.56h-2.74l-.36-1.56-.29-.13-1.36.86-1.94-1.94.86-1.36-.13-.29-1.56-.36v-2.74l1.56-.36.13-.29-.86-1.36 1.94-1.94 1.36.86.29-.13.36-1.56h2.74l.36 1.56.29.13 1.36-.86 1.94 1.94-.86 1.36z"/><circle cx="8" cy="8" r="2.1"/>""",
        ["help"] = """<circle cx="8" cy="8" r="6.5"/><path d="M6.2 6.3a1.9 1.9 0 1 1 2.6 1.75c-.5.25-.8.6-.8 1.15v.3"/><circle cx="8" cy="11.4" r=".8" fill="C" stroke="none"/>""",
        ["keyboard"] = """<rect x="1.5" y="4" width="13" height="8.5" rx="1.5"/><path d="M4 6.75h.01M6.67 6.75h.01M9.33 6.75h.01M12 6.75h.01M4.75 9.75h6.5"/>""",

        // Media and state.
        ["star"] = """<path d="M8 1.8l1.9 3.9 4.3.6-3.1 3 .7 4.3L8 11.6l-3.8 2 .7-4.3-3.1-3 4.3-.6z" fill="C"/>""",
        ["star-outline"] = """<path d="M8 1.8l1.9 3.9 4.3.6-3.1 3 .7 4.3L8 11.6l-3.8 2 .7-4.3-3.1-3 4.3-.6z"/>""",
        ["play"] = """<path d="M4.5 2.5l9 5.5-9 5.5z" fill="C"/>""",
        ["stop"] = """<rect x="3.5" y="3.5" width="9" height="9" rx="1.5" fill="C"/>""",
        ["dot"] = """<circle cx="8" cy="8" r="3.5" fill="C" stroke="none"/>""",
        ["eye"] = """<path d="M1.5 8c1.7-3 3.9-4.5 6.5-4.5s4.8 1.5 6.5 4.5c-1.7 3-3.9 4.5-6.5 4.5s-4.8-1.5-6.5-4.5z"/><circle cx="8" cy="8" r="2" fill="C" stroke="none"/>""",
        ["eye-off"] = """<path d="M1.5 8c1.7-3 3.9-4.5 6.5-4.5s4.8 1.5 6.5 4.5c-1.7 3-3.9 4.5-6.5 4.5s-4.8-1.5-6.5-4.5z"/><path d="M3 13l10-10"/>""",
        ["lock"] = """<rect x="3.5" y="7" width="9" height="6.5" rx="1.5"/><path d="M5.5 7v-2a2.5 2.5 0 0 1 5 0v2"/>""",
        ["lock-open"] = """<rect x="3.5" y="7" width="9" height="6.5" rx="1.5"/><path d="M5.5 7v-2.5a2.5 2.5 0 0 1 4.9-.7"/>""",
        ["timeline-key"] = """<path d="M8 2.5l5.5 5.5-5.5 5.5-5.5-5.5z" fill="C"/>""",
        ["slider-knob"] = $"""<circle cx="8" cy="8" r="7" fill="{Backdrop}" stroke="none"/><circle cx="8" cy="8" r="5.5" fill="C" stroke="none"/>""",

        // Severity marks (Map Check, Log, callouts).
        ["error"] = """<circle cx="8" cy="8" r="6"/><path d="M6 6l4 4M10 6l-4 4"/>""",
        ["warning"] = """<path d="M8 2.2l6.2 11h-12.4z"/><path d="M8 6.6v3"/><circle cx="8" cy="11.4" r=".8" fill="C" stroke="none"/>""",
        ["info"] = """<circle cx="8" cy="8" r="6"/><path d="M8 7.3v4"/><circle cx="8" cy="5" r=".85" fill="C" stroke="none"/>""",
        ["success"] = """<circle cx="8" cy="8" r="6"/><path d="M5.3 8.2l1.8 1.8 3.6-3.8"/>""",

        // Object types (outliner, classes, inspector title, prefab placeholders, the place picker).
        ["type-brush"] = """<path d="M8 1.5l6 3v7l-6 3-6-3v-7z"/><path d="M2 4.5l6 3 6-3M8 7.5v7"/>""",
        ["type-brush-subtract"] = """<path d="M8 1.5l6 3v7l-6 3-6-3v-7z" stroke-dasharray="2 1.6"/><path d="M2 4.5l6 3 6-3M8 7.5v7" stroke-dasharray="2 1.6"/>""",
        ["type-volume"] = """<rect x="2.5" y="2.5" width="11" height="11" rx="1" stroke-dasharray="2.2 1.8"/>""",
        ["type-light"] = """<path d="M5.5 10.5c-1.3-1-2-2.3-2-3.9a4.5 4.5 0 0 1 9 0c0 1.6-.7 2.9-2 3.9v1.5h-5z"/><path d="M6 14.5h4"/>""",
        ["type-sound"] = """<path d="M2 6h2.5l3.5-3v10l-3.5-3h-2.5z"/><path d="M10.5 4.5a5 5 0 0 1 0 7"/>""",
        ["type-mesh"] = """<path d="M8 1.5l5.5 3.2v6.6l-5.5 3.2-5.5-3.2v-6.6z"/><path d="M2.5 4.7h11l-5.5 9.8z"/>""",
        ["type-emitter"] = """<circle cx="3.5" cy="12.5" r="1.5" fill="C" stroke="none"/><g fill="C" stroke="none"><circle cx="7" cy="4" r="1.3"/><circle cx="11.5" cy="5.5" r="1.3"/><circle cx="12.5" cy="10" r="1.3"/></g><path d="M4.8 10.6l1.2-3.4M5.3 11.2l4.2-3.6M5.5 12.2l4.6-1.4" stroke-dasharray="1 1.4"/>""",
        ["type-mover"] = """<rect x="1.5" y="4.5" width="7" height="7" rx="1"/><path d="M10.5 8h4M12.5 6l2 2-2 2"/>""",
        ["type-start"] = """<circle cx="6" cy="4" r="2"/><path d="M2.5 14.5v-2.5a3.5 3.5 0 0 1 7 0v2.5"/><path d="M11.5 14.5v-12.5l3 1.5-3 1.5"/>""",
        ["type-camera"] = """<rect x="4.5" y="3.5" width="8" height="4.5" rx="1"/><path d="M12.5 4.75h1.5v2h-1.5"/><path d="M7.5 8l-1.25 3.5h-3.75M2.5 9.5v4"/>""",
        ["type-alarm"] = """<path d="M3.5 12h9l-1.3-1.8v-3.2a3.2 3.2 0 0 0-6.4 0v3.2z"/><path d="M6.8 14h2.4M8 2.2v1.6"/>""",
        ["type-laser"] = """<path d="M2.5 2.5v11M13.5 2.5v11"/><path d="M2.5 5l11 2-11 2 11 2" stroke-width="1.1"/>""",
        ["type-trigger"] = """<rect x="1.5" y="1.5" width="13" height="13" rx="1" stroke-dasharray="2 1.8"/><path d="M9 4l-3.5 4.5h3l-1.5 3.5 3.5-4.5h-3z"/>""",
        ["type-event"] = """<path d="M9.5 1.5l-6 7.5h4.5l-1.5 5.5 6-7.5h-4.5z"/>""",
        ["type-objective"] = """<circle cx="8" cy="8" r="6"/><circle cx="8" cy="8" r="3.2"/><circle cx="8" cy="8" r=".9" fill="C" stroke="none"/>""",
        ["type-computer"] = """<rect x="1.5" y="2.5" width="13" height="8.5" rx="1"/><path d="M8 11v2.5M5 13.5h6"/>""",
        ["type-door"] = """<path d="M3.5 14.5v-12a1 1 0 0 1 1-1h7a1 1 0 0 1 1 1v12M2 14.5h12"/><circle cx="10" cy="8.5" r=".85" fill="C" stroke="none"/>""",
        ["type-tip"] = """<path d="M2.5 3.5a1 1 0 0 1 1-1h9a1 1 0 0 1 1 1v6.5a1 1 0 0 1-1 1h-5.5l-3 2.5v-2.5h-.5a1 1 0 0 1-1-1z"/>""",
        ["type-path"] = """<circle cx="4.5" cy="12.5" r="1.5"/><circle cx="11.5" cy="3.5" r="1.5"/><path d="M6 12.5h2.5a3 3 0 0 0 3-3v-4.5"/>""",
        ["type-ge"] = """<path d="M4.5 1.5v13M11.5 1.5v13M4.5 4.5h7M4.5 8h7M4.5 11.5h7"/>""",
        ["type-zipline"] = """<path d="M1.5 2.5l13 5"/><circle cx="8" cy="5" r="1.2"/><path d="M8 6.2v3.3M5.5 13l2.5-3.5 2.5 3.5z"/>""",
        ["traversal"] = """<path d="M1.5 14.5h6v-6h7"/><path d="M4.5 11.5v-9M2.5 4.5l2-2 2 2"/>""",
        ["type-zone"] = """<path d="M3.5 14.5v-9a4.5 4.5 0 0 1 9 0v9M1.5 14.5h13"/><path d="M6 14.5v-8a2 2 0 0 1 4 0v8" stroke-dasharray="1.6 1.4"/>""",
        ["type-pawn"] = """<circle cx="8" cy="4" r="2.25"/><path d="M3.5 14.5v-3a4.5 4.5 0 0 1 9 0v3"/>""",
        ["type-conveyor"] = """<circle cx="4" cy="10.5" r="2.5"/><circle cx="12" cy="10.5" r="2.5"/><path d="M4 8h8M4 13h8"/><path d="M5.5 3.5h5M9 2l1.5 1.5-1.5 1.5"/>""",
        ["type-note"] = """<path d="M3.5 1.5h6l3 3v10h-9z"/><path d="M9.5 1.5v3h3M5.5 8h5M5.5 11h3.5"/>""",
        ["type-projector"] = """<path d="M5.5 5.5l9-3v11l-9-3z"/><rect x="1.5" y="5" width="4" height="6" rx="1"/>""",
        ["type-actor"] = """<circle cx="8" cy="8" r="5.5"/><circle cx="8" cy="8" r="1.75" fill="C" stroke="none"/>""",
        ["type-group"] = """<path d="M1.5 4a1 1 0 0 1 1-1h3.5l1.5 1.5h5.5a1 1 0 0 1 1 1v6.5a1 1 0 0 1-1 1h-10.5a1 1 0 0 1-1-1z"/>""",
        ["type-layer"] = """<path d="M8 2l6.5 3.25-6.5 3.25-6.5-3.25z"/><path d="M1.5 8.25l6.5 3.25 6.5-3.25"/><path d="M1.5 11.25l6.5 3.25 6.5-3.25"/>""",

        // Actor kinds (ActorIcons: the views' actor icons, and the class lists'). Kinds without an entry here use the
        // type icons above.
        ["type-light-spot"] = """<path d="M8 1.5v1.75"/><path d="M3.5 7.75a4.5 4.5 0 0 1 9 0z" fill="C"/><path d="M5.5 10.25l-1.75 4.25M8 10.25v4.25M10.5 10.25l1.75 4.25"/>""",
        ["type-light-sun"] = """<circle cx="8" cy="8" r="3"/><path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4"/>""",
        ["type-light-trigger"] = """<path d="M3.5 10c-1-.8-1.5-1.9-1.5-3.2a3.75 3.75 0 0 1 7.5 0c0 1.3-.5 2.4-1.5 3.2v1.5h-4.5z"/><path d="M4 14h3.5"/><path d="M13.5 4.5l-2.5 4h3l-2.5 4"/>""",
        ["type-start-spy"] = """<circle cx="6.5" cy="4.75" r="2.75" fill="C" stroke="none"/><path d="M3 4.5h7"/><path d="M1.5 15v-1.5a5 5 0 0 1 10 0v1.5z" fill="C"/><path d="M14 15v-13"/><path d="M14 2.25l-2.5 1.5 2.5 1.5z" fill="C"/>""",
        ["type-start-merc"] = """<path d="M3.25 5.5a3.25 3.25 0 0 1 6.5 0z" fill="C"/><path d="M2.25 5.75h8.5"/><path d="M4.75 6.25v.75a1.75 1.75 0 0 0 3.5 0v-.75"/><path d="M1.5 15v-1.5a5 5 0 0 1 10 0v1.5z" fill="C"/><path d="M14 15v-13"/><path d="M14 2.25l-2.5 1.5 2.5 1.5z" fill="C"/>""",
        ["type-camnet"] = """<rect x="2.5" y="2" width="7" height="5" rx="1"/><path d="M9.5 3.75l3-1.25v4l-3-1.25"/><path d="M6 7v5.5M3.5 12.5h7"/><circle cx="2.75" cy="12.5" r="1.25"/><circle cx="12" cy="12.5" r="1.25"/>""",
        ["type-sensor-motion"] = """<path d="M3 14h10"/><path d="M5.25 14a2.75 2.75 0 0 1 5.5 0"/><path d="M4.25 9.5a5 5 0 0 1 7.5 0M2 6.75a8.25 8.25 0 0 1 12 0"/>""",
        ["type-sensor-presence"] = """<circle cx="8" cy="8" r="6.5" stroke-dasharray="2 1.6"/><circle cx="8" cy="6" r="1.75"/><path d="M4.75 11.75a3.25 3.25 0 0 1 6.5 0"/>""",
        ["type-mine"] = """<circle cx="8" cy="9" r="3.5"/><path d="M8 2v2.5M8 13.5v1M1.75 9h2.75M11.5 9h2.75M3.6 4.6l1.9 1.9M12.4 4.6l-1.9 1.9"/>""",
        ["type-laptop"] = """<path d="M3.5 3h9v7h-9z"/><path d="M1.5 13h13l-1.5-3h-10z"/>""",
        ["type-bomb"] = """<circle cx="7" cy="9.5" r="4.5"/><path d="M10.25 6.25l1.5-1.5"/><path d="M11.75 4.75c.25-1.5 1.25-2.5 2.75-2.25"/>""",
        ["type-disk"] = """<circle cx="8" cy="8" r="6.5"/><circle cx="8" cy="8" r="1.75"/><path d="M8 3.75a4.25 4.25 0 0 1 4.25 4.25"/>""",
        ["type-flag"] = """<path d="M4 14.5v-13"/><path d="M4 2h8.5l-2 3 2 3h-8.5"/>""",
        ["type-flag-zone"] = """<ellipse cx="8" cy="12.5" rx="6.5" ry="2" stroke-dasharray="2 1.6"/><path d="M6 12.5v-11"/><path d="M6 1.5h6.5l-1.5 2.25 1.5 2.25h-6.5"/>""",
        ["type-mission"] = """<path d="M5.5 3h-1.5a1 1 0 0 0-1 1v9.5a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1v-9.5a1 1 0 0 0-1-1h-1.5"/><rect x="5.5" y="1.75" width="5" height="2.5" rx="0.75"/><path d="M5.5 9.25l1.75 1.75 3.25-3.5"/>""",
        ["type-ammo"] = """<path d="M2 14v-6.5c0-1.8.6-3.3 1.5-4.5.9 1.2 1.5 2.7 1.5 4.5v6.5z"/><path d="M6.5 14v-6.5c0-1.8.6-3.3 1.5-4.5.9 1.2 1.5 2.7 1.5 4.5v6.5z"/><path d="M11 14v-6.5c0-1.8.6-3.3 1.5-4.5.9 1.2 1.5 2.7 1.5 4.5v6.5z"/>""",
        ["type-health"] = """<rect x="1.5" y="1.5" width="13" height="13" rx="2.5"/><path d="M8 4.5v7M4.5 8h7"/>""",
        ["type-switch"] = """<rect x="4" y="1.5" width="8" height="13" rx="1.5"/><path d="M8 9l2-4.5"/><circle cx="8" cy="9.5" r="1.25" fill="C" stroke="none"/>""",
        ["type-lift"] = """<rect x="2.5" y="1.5" width="11" height="13" rx="1"/><path d="M5.5 6.5l2.5-2.5 2.5 2.5M5.5 9.5l2.5 2.5 2.5-2.5"/>""",
        ["type-message"] = """<path d="M2.5 3.5a1 1 0 0 1 1-1h9a1 1 0 0 1 1 1v6.5a1 1 0 0 1-1 1h-5.5l-3 2.5v-2.5h-.5a1 1 0 0 1-1-1z"/><path d="M5 5.5h6M5 8h4"/>""",
        ["type-glass"] = """<rect x="2.5" y="1.5" width="11" height="13" rx="1"/><path d="M5 7.5l3.5-3.5M5 11.5l6-6"/>""",
        ["type-gameplay"] = """<path d="M8 1.5l6 3.25v6.5l-6 3.25-6-3.25v-6.5z"/><circle cx="8" cy="8" r="2" fill="C" stroke="none"/>""",

        // Panels (dock tabs).
        ["outliner"] = """<path d="M2 3.5h2.5M3.25 3.5v8.75M3.25 8h2.5M3.25 12.25h2.5"/><path d="M7 3.5h7M8 8h6M8 12.25h6"/>""",
        ["inspector"] = """<path d="M2 4h12M2 8h12M2 12h12"/><g fill="C" stroke="none"><circle cx="10.5" cy="4" r="1.9"/><circle cx="5.5" cy="8" r="1.9"/><circle cx="9" cy="12" r="1.9"/></g>""",
        ["map"] = """<path d="M1.5 4l4-1.5 5 1.5 4-1.5v9.5l-4 1.5-5-1.5-4 1.5z"/><path d="M5.5 2.5v9.5M10.5 4v9.5"/>""",
        ["surface"] = """<rect x="2.5" y="2.5" width="11" height="11" rx="1.5"/><path d="M2.5 9l6.5-6.5M5.5 13.5l8-8M10 13.5l3.5-3.5"/>""",
        ["log"] = """<path d="M4 1.5h5.5l3 3v10h-8.5z"/><path d="M9.5 1.5v3h3M6 8h4.5M6 11h4.5"/>""",
        ["history"] = """<path d="M2.5 8a5.5 5.5 0 1 0 1.6-3.9"/><path d="M3.5 1.75v2.75h2.75"/><path d="M8 5v3l2 1.5"/>""",
        ["image"] = """<rect x="1.5" y="2.5" width="13" height="11" rx="1.5"/><circle cx="5.5" cy="6" r="1.25"/><path d="M1.5 11.5l3.5-3.5 3 3 2-2 4.5 4"/>""",
        ["classes"] = """<rect x="5.5" y="1.5" width="5" height="3.5" rx="1"/><rect x="1.5" y="11" width="5" height="3.5" rx="1"/><rect x="9.5" y="11" width="5" height="3.5" rx="1"/><path d="M8 5v3M4 11v-3h8v3"/>""",
        ["id-card"] = """<rect x="1.5" y="3" width="13" height="10" rx="1.5"/><circle cx="5.5" cy="7" r="1.5"/><path d="M3.25 11c.4-1 1.2-1.5 2.25-1.5s1.85.5 2.25 1.5M9.75 6.5h2.75M9.75 9.5h2.75"/>""",
        ["check-list"] = """<path d="M5.5 3h-1.5a1 1 0 0 0-1 1v9.5a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1v-9.5a1 1 0 0 0-1-1h-1.5"/><rect x="5.5" y="1.75" width="5" height="2.5" rx="0.75"/><path d="M5.5 9.25l1.75 1.75 3.25-3.5"/>""",
        ["property-sets"] = """<rect x="1.5" y="4.5" width="9.5" height="10" rx="1.25"/><path d="M4.5 4.5v-2a1 1 0 0 1 1-1h8a1 1 0 0 1 1 1v8.5a1 1 0 0 1-1 1h-2.5"/><path d="M4 8h4.5M4 11h4.5"/>""",
        ["storeys"] = """<path d="M3 14.5v-10l5-3 5 3v10z"/><path d="M3 8h10M3 11.25h10"/><path d="M1.5 14.5h13"/>""",
        ["grid"] = """<rect x="1.5" y="1.5" width="5.5" height="5.5" rx="1"/><rect x="9" y="1.5" width="5.5" height="5.5" rx="1"/><rect x="1.5" y="9" width="5.5" height="5.5" rx="1"/><rect x="9" y="9" width="5.5" height="5.5" rx="1"/>""",
        // Three tiles and a pointer dragging the fourth away: the placeables palette.
        ["palette"] = """<rect x="1.5" y="1.5" width="5.5" height="5.5" rx="1"/><rect x="9" y="1.5" width="5.5" height="5.5" rx="1"/><rect x="1.5" y="9" width="5.5" height="5.5" rx="1"/><path d="M9.25 9.25l5.25 2-2.25.9-.9 2.35z"/>""",
    };

    /// <summary>Names that draw another icon.</summary>
    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.Ordinal)
    {
        ["external"] = "goto",
    };

    private static readonly Dictionary<string, Texture2D?> s_cache = new(StringComparer.Ordinal);

    /// <summary>Every icon name, for galleries and checks.</summary>
    public static IEnumerable<string> Names => s_drawings.Keys.Concat(s_aliases.Keys);

    /// <summary>Whether <paramref name="name"/> is a known icon.</summary>
    public static bool Has(string name) => s_drawings.ContainsKey(name) || s_aliases.ContainsKey(name);

    /// <summary>The SVG drawing of a named icon (the elements inside the 16-unit <c>svg</c>, "C" for its colour), or
    /// null; for pictures made from the same drawings (the views' actor icon atlas).</summary>
    public static string? Body(string name) => s_drawings.GetValueOrDefault(s_aliases.GetValueOrDefault(name, name));

    /// <summary>A named icon in a colour, <paramref name="size"/> UI pixels square; null for an unknown name.</summary>
    public static Texture2D? Get(string name, Color color, int size = EditorTheme.IconSize)
    {
        string key = s_aliases.GetValueOrDefault(name, name);
        return s_drawings.TryGetValue(key, out string? body) ? Draw(key, body, color, size) : null;
    }

    /// <summary>The same as <see cref="Get"/>, but for a drawing kept elsewhere (the toolbar's own icons, or an area's
    /// icon before it moves here).</summary>
    public static Texture2D? Draw(string key, string body, Color color, int size = EditorTheme.IconSize)
    {
        string c = "#" + color.ToHtml(includeAlpha: false);
        string opacity = color.A.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string cacheKey = $"{key}|{c}|{opacity}|{size}";
        if (s_cache.TryGetValue(cacheKey, out Texture2D? cached))
        {
            return cached;
        }

        string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 16 16\" opacity=\"{opacity}\" fill=\"none\" stroke=\"{c}\" " +
            "stroke-width=\"1.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" + body.Replace("\"C\"", "\"" + c + "\"", StringComparison.Ordinal) + "</svg>";
        Texture2D? icon = DpiTexture.CreateFromString(svg);
        if (icon is not null && icon.GetWidth() == 0)
        {
            icon = null;
        }

        s_cache[cacheKey] = icon;
        return icon;
    }
}
