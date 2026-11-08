# Find anything and learn a task

Open **Ctrl+Shift+P** and type a task, command, placeable, class, asset or control name. Results show their kind,
destination and description. The index follows registered commands, the installed asset catalogue, the class
catalogue and the controls currently built by the editor's panels. Asset packs must be installed before their
assets can appear. Particle effects load in the background on the first palette opening.

- **Laptop** reveals Computer in **Placeables → Objectives**, scrolls it into view and highlights the tile.
- **Textures**, **Meshes**, **Materials** and **Sounds** open the corresponding asset-browser tab.
- **Level name**, **Map thumbnail** and **Loading screen** reveal the corresponding Identity setting.
- **Ladder**, **Zip line** and **Overhead bar** select the corresponding GE drawing type and highlight its options.
- **tutorial** lists the guided lessons; **how do I set up a laptop objective** finds the relevant lesson directly.
- **@** searches existing map objects by id, class or tag. Enter selects and frames the object.
- Paste a change document beginning with `{` or `[` to preview it; Ctrl+Enter applies it as before.

Use the category buttons to narrow the search, or type a prefix: `tutorial:`, `command:` (also `>`), `placeable:`,
`class:`, `asset:`, `panel:`, `control:`, `tool:`, `prefab:`, `preset:`, `effect:` or `object:`.
Search understands aliases including laptop/computer, resize/scale and drop-off/drop zone. Search ranks direct
placeable matches above decorative assets with the same name; use Assets to search specifically for the art.

**Enter** opens the result or runs its command. **Alt+Enter / Show me** finds a command's toolbar button or opens
its actual menu with the command focused, without running it. A class, asset, preset or prefab result reveals its
browser entry so you can inspect it before placing or applying anything. Unavailable commands stay searchable,
show a state hint and can still be located. Abstract and non-placeable classes are shown for reference and cannot
be placed.

The outline follows the target while the layout changes. Move the mouse into it and then out to dismiss a search
outline. Opening the palette clears the previous outline. **Ctrl+D / Favourite** stars a result; Favourites and
recent results are kept between sessions. **F1** continues to list the current shortcut bindings.

## Guided tutorials

The 35 lessons cover ladders; laptop, bomb, carry-disk and chained objectives; naming; thumbnails and loading
pictures; glass; Surface; meshes; textures; transforms; geometry and builder brushes; stairs; lights; team starts;
security; saving and recovery; validation; building and playing; content packs; navigation; events; layers;
bookmarks; emitters; prefabs; sound previews; camera networks; movers; storeys; round rules; other traversal;
import/export; and actor properties.

Each lesson opens a small card alongside the editor. It reveals the relevant panel and highlights the actual
control for each step. **Back**, **Next**, **Show me again**, **Cancel** and the close button remain available as
you work; minimise with **−** and expand with **+**. The buttons support keyboard focus. Escape closes a lesson
when focus is inside its card; in the map view, Escape remains available to cancel a tool gesture.

Lessons navigate the UI and explain the task. You perform the map edits yourself and decide when to advance.
Cancelling keeps edits already made, clears the lesson outline and saves your place. **Help → Resume Last
Tutorial** continues at that step; choosing the tutorial again starts at the beginning. Finishing marks the lesson
completed in search. Opening another map cancels the active lesson. The original **Your first map** checklist
remains available; its separate spotlight is suppressed while a mini tutorial is active.

Map checks and tutorial completion do not replace testing a finished objective, traversal route or exported map
in its target game.
