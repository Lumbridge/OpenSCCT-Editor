# Door and prefab checks

`dotnet run --project tools/door-prefab-checks -- <output-folder>` checks all door styles and movements, pivot
positions, rotated slide directions, authored resizing, floor anchoring, cloning, clipboard serialization,
save/reopen, format gating, undo/redo, fixed frames, every built-in prefab at default/min/max parameters, independent
door-switch events, runtime moving collision, managed wall cuts, and a fully packaged common-prop gallery.

Override `-p:OpenScctRoot=<checkout>` when the pinned dependency checkout is elsewhere. The harness writes the
DoorPlacement, DoorGallery and PrefabGallery sample projects under the output folder. It uses generated class
defaults and does not require converted map asset packs.

Build the app, import it once with Godot `--headless --path app --import`, then use `ui-shots.json` with the Editor's
`--shots=<script> --shots-out=<folder>` arguments. The screenshots cover the library, door palette, Inspector,
resize handles, tutorial and 150% UI scaling.

For interaction checks, launch a disposable `--background` Editor with `OSE_USER_DATA` pointing at a separate run
folder and DoorPlacement.osmap open. Run `python tools/door-prefab-checks/check_ui.py <run-folder>`. It exercises
real click/drag placement, wall cuts, Inspector edits, undo/redo, prefab search/favourites and all new lesson
destinations. It reverts prior edits in that disposable session before starting. Normal user editors reject UI
driving. `tools/discovery-checks` covers tutorial search and the full lesson catalogue; `tools/window-checks`
guards the existing window geometry, fitting, history, runtime collision and breakage workflow.

The assembly checks also cover every front/back team combination, synchronized double leaves, independent copies,
real rectangular side-sensor overlaps, allowed-player occupancy, cancelled/successful hacks, configurable hack
timers, moved panel links, painting both leaves and persisted finishes. `AccessDoorGallery.osmap` and
`DoorMaterialDrop.osmap` are the fixtures for `check_access_ui.py <run-folder> <output-folder>`, which exercises
actual leaf/frame material drags, undo/redo, the four access-assembly shortcuts and the access tutorial.

`check_tutorials_ui.py <run-folder> [hackable_door.osmap]` exercises all nine dedicated door-type and interaction lessons in a disposable
background Editor. It verifies the actual preset and highlighted parameter for every prefab destination, conditional
sliding controls, discovery alongside existing tutorials, contextual entry points and Back/Cancel/Resume/Finish.
The obsolete combined access lesson has been replaced by separate hacking and automatic-access lessons.
