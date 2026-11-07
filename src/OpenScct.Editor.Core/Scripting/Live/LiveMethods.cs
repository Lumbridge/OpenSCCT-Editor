namespace OpenScct.Editor.Core.Scripting.Live;

/// <summary>The methods only a running editor answers: ghost previews for the user to accept or reject, the editor's
/// own state (selection, camera, the point under the view centre), and its user interface (<c>ui.*</c>: look at the
/// window, act on it, list its controls; UI input/inspection requires an isolated background editor, while read-only
/// viewport screenshots are also available in the live editor).</summary>
public static class LiveMethods
{
    public static readonly IReadOnlyList<ApiMethod> All =
    [
        new("propose", "Propose changes",
            "Shows a change document to the user as a translucent ghost in the editor's views, with Accept and Reject. Nothing " +
            "changes until the user accepts; accepting applies it as one undo step. Takes the same document as apply. " +
            "Returns the dry-run diff and a proposal id; wait (seconds) waits for the user's answer. A new proposal replaces a " +
            "pending one.",
            """{"type":"object","properties":{"ops":{"type":"array","items":{"type":"object"}},"author":{"type":"string"},"label":{"type":"string","description":"what the user sees on the accept bar"},"wait":{"type":"number","description":"seconds to wait for accept or reject (0 = return at once)"}},"required":["ops"]}"""),
        new("proposal", "Proposal status",
            "The status of a proposal (pending, accepted, rejected, superseded or failed) and, once accepted, the applied diff. " +
            "wait (seconds) waits while it is pending.",
            """{"type":"object","properties":{"id":{"type":"integer","description":"the proposal id (default: the latest)"},"wait":{"type":"number"}}}"""),
        new("editor", "Editor state",
            "What the user has in the editor now: the open map, unsaved changes, the selection, the camera, the point under the " +
            "centre of the view (place things there with \"at\"), connected clients, any pending proposal and, while the Match " +
            "panel greys out a match's absent actors, its players, mode and how many (the ids: the match query's absent). " +
            "preview reports sceneSyncPending, geometry/lighting/sky states, litReady, lightingVersion, completedLightingVersion " +
            "and errors. Wait for litReady after a change before capturing a baked view; queued/paused/baking are not ready. " +
            "This tracks geometry and light baking, not asynchronous texture loading.",
            """{"type":"object","properties":{}}"""),
        new("ui.shot", "Screenshot the editor",
            "In the live editor, views:true captures only visible map viewports and returns PNG paths, modes, camera poses " +
            "and sky-preview state without changing the map or UI. No crop or UI lint in this mode. " +
            "In an isolated background editor (tools/Start-BackgroundEditor.ps1), writes the editor window " +
            "as a PNG (from the editor's own image, so it works off-screen) " +
            "and returns its path, plus automatic UI checks (clipped text, hidden tabs, controls outside their box, missing glyphs). " +
            "crop takes a target as ui.input does and saves that part too; views:true also saves each 3D view as shown and bare. " +
            "viewportsOnly:true also selects the viewport-only path in a background editor. The reply gives the PNG's path.",
            """{"type":"object","properties":{"name":{"type":"string"},"dir":{"type":"string","description":"folder for the PNGs (default: shots in the run's own user folder)"},"crop":{"description":"a target: [x,y] or \"action:<id>\", \"tab:<panel>\", \"menu:<title>\", \"item:<menu item>\", \"view:<Perspective|Top|Front|Side>\", \"text:<label>\" or a control path from ui.tree"},"views":{"type":"boolean"},"viewportsOnly":{"type":"boolean","description":"capture only map viewports, also in a background editor"},"lint":{"type":"boolean","description":"run the UI checks (background full UI only; default true)"}}}"""),
        new("ui.input", "Click and type in the editor",
            "Only in an editor started in the background. Sends synthetic mouse and keyboard input to the editor " +
            "window (never the real pointer or keyboard); unknown keys in a step are an error. steps is a list, " +
            "each {op, ...}: move/click/rightClick/doubleClick/press/release {at, offset:[fx,fy], button, mods:\"ctrl+shift\"}, " +
            "drag {at, to, toOffset, hold}, scroll {at, clicks}, key {key:\"Ctrl+Z\"}, type {text}, hold {key, down}, hover {at, " +
            "seconds} (waits for the tooltip), escape (closes popups). at is a target, see ui.shot. frames (default 1) waits " +
            "after a step. Returns the pointer, the hovered control and the open popup. A ui.shot shows the result.",
            """{"type":"object","properties":{"steps":{"type":"array","items":{"type":"object"}}},"required":["steps"]}"""),
        new("ui.tree", "List the editor's controls",
            "Only in an editor started in the background. The visible controls of the editor window with their " +
            "paths, types, rectangles (canvas coordinates, the ones ui.input " +
            "takes), texts, tooltips, toolbar action ids and states, plus open popups and their items. under limits it to one " +
            "control's subtree; all:true keeps plain layout boxes; limit caps the count (default 400).",
            """{"type":"object","properties":{"under":{"type":"string"},"all":{"type":"boolean"},"limit":{"type":"integer"}}}"""),
    ];

    /// <summary>The scripting API plus the live methods.</summary>
    public static IReadOnlyList<ApiMethod> WithApi { get; } = [.. ApiMethods.All, .. All];
}
