# Screenshots and background runs

The editor can take screenshots of itself, from a script or a built-in tour, without disturbing the person at the
machine. Reviewers use the pictures and their automatic UI checks to see how the app looks; scripts use the
same calls (`ui.shot`, `ui.input`, `ui.tree`, see [SCRIPTING.md](SCRIPTING.md#live-editing-in-the-open-editor)) to see
what they changed.

```
pwsh tools/Invoke-EditorShots.ps1                                   # the full tour on a new map
pwsh tools/Invoke-EditorShots.ps1 -Map C:/maps/SteeE.osmap          # the full tour on a map
pwsh tools/Invoke-EditorShots.ps1 -Map C:/maps/Demo.osmap -Script my-shots.json -Out C:/temp/shots
pwsh tools/Invoke-EditorShots.ps1 -Map C:/maps/SteeE.osmap -Script tour:full@1920x1080@1.5   # the whole tour at one size and scale
```

The script builds the app, runs it in background mode and prints a summary: how many shots, how many UI findings and
which steps failed. Open `index.html` in the output folder for the contact sheet. The exit code is the editor's: 0,
1 when a step failed, 2 when a step hung. `-Map` takes anything the editor opens: a `.osmap`, an SVMap folder or a
`.t3d`. Godot comes from `-Godot`, `OSE_GODOT`, or `tools/godot` in the OpenSCCT checkout, the clone named by
`OSE_OPENSCCT_GIT` or `..\OpenSCCT`.

## Background mode

Start a background editor with `tools/Start-BackgroundEditor.ps1` (screenshot runs use it too). The app flag
`-- --background` (or `OSE_BACKGROUND=1`, or any `--shots=` run) turns the mode on, but on its own it cannot keep the
window off the screen: Godot creates and shows the window before any editor code runs. The script adds what the flag
cannot:

- **The first frame.** It writes a temporary `app/override.cfg` (start minimised, never focused, popups inside the
  window) and removes it as soon as the editor's code is running. A named lock stops two starts from taking away or
  leaving behind each other's copy; a leftover one is undone by a normal start.
- **Folders of its own**, under the run folder: `appdata` (`APPDATA`, so all of `user://`, and `OSE_USER_DATA` its
  `OpenSCCT Editor` folder: settings, autosaves, crash markers, builds, recent files), `editor.log` (Godot's log,
  `--log-file`), `live` (`OSE_LIVE_DIR`), `data` (`OSE_DATA`, imports and the staged map builder) and
  `prefabs` (`OSE_PREFABS`). The asset index and thumbnails (`OSE_CACHE`) go to `-Cache`, default `local/background/.cache`,
  shared by background runs only. Godot's shader cache is copied in from `local/background/.shader-cache`, which the first clean
  run fills, so runs do not compile every shader again.

Inside the app, background mode then:

- keeps the window off-screen at (-32000, -32000), with no taskbar button and never focused. It still draws:
  screenshots read the editor's own image, not the screen;
- draws popups, menus, tooltips and dialogs inside the main window, so they are in the pictures. Dialogs that would
  open a native window (file pickers) are skipped and logged;
- lets nothing reach the desktop: no clipboard (the editor keeps its own), no sound, no Explorer windows, no game or
  process starts, no real cursor reads or moves;
- caps the frame rate at 30;
- keeps its settings, autosaves and live discovery file in a folder of its own (`OSE_USER_DATA`, else a new folder
  under `%TEMP%\OpenSCCT Editor\runs`), so a background editor never appears in the user's `ose live` list, and
  allows the `ui.*` calls (the user's own editor refuses them);
- uses 100% UI scale whatever the display's DPI (or the View > UI Scale choice), so pictures do not depend on the
  machine. A script's `scale` step sets another.

`OSE_FOCUS=1` turns background mode off for a run that has the flag, to watch a script run: the window stays where
Godot put it. Shot runs still use the synthetic pointer and redraw every frame.

## Command line

| Argument | Does |
|---|---|
| `--background` | Background mode (above). Start it through `tools/Start-BackgroundEditor.ps1`. |
| `--shots=<script.json \| tour:full \| tour:quick>` | Runs a shot script, then quits. Implies `--background`. Needs a window: a `--headless` run refuses it. |
| `--shots-out=<folder>` | Where the PNGs, `manifest.json` and `index.html` go. Default `%TEMP%\OpenSCCT Editor\shots\<time>` (kept), or `shots/<time>` in the user data folder `--user-data=` or `OSE_USER_DATA` names. |
| `--user-data=<folder>` | Keeps settings, recent files, autosaves, crash markers, builds and live discovery in this folder. Automated runs (headless, background, `--run=`, `--shots=`) get a new one under `%TEMP%\OpenSCCT Editor\runs` without it. |

| Variable | Does |
|---|---|
| `OSE_BACKGROUND=1` | Background mode. |
| `OSE_FOCUS=1` | No background mode, even with the flag. |
| `OSE_USER_DATA` | As `--user-data=`; `ose` with the same value finds the editor live, unless `OSE_LIVE_DIR` moved its discovery file (`Start-BackgroundEditor.ps1` does: use that folder). |
| `OSE_LIVE_DIR` | The live server's discovery folder (see SCRIPTING.md). Default `live` in the user data folder of an automated run, else `%LOCALAPPDATA%\OpenSCCT Editor\live`. |
| `OSE_CACHE` | The asset index, thumbnails, the lightmapper's shadow cache and the nav graph cache. Default `%LOCALAPPDATA%\OpenSCCT-Editor\cache`; `%TEMP%\OpenSCCT Editor\cache` for an automated run. |
| `OSE_DATA` | Imports and the staged map builder. Default `%LOCALAPPDATA%\OpenSCCT-Editor`; `%TEMP%\OpenSCCT Editor\data` for an automated run. |
| `OSE_PREFABS` | The prefab library. Default `prefabs` in the user data folder (`%APPDATA%\OpenSCCT Editor` for the user's own runs). |

## Shot scripts

A shot script is a JSON array of steps run in order. Each step names its kind in `op`:

```json
[
  {"op": "size", "w": 1600, "h": 900},
  {"op": "select", "class": "SBase.SAlarm", "frame": true},
  {"op": "tab", "panel": "Inspector"},
  {"op": "shot", "name": "alarm", "caption": "An alarm in the inspector", "crop": "tab:Inspector"},
  {"op": "menu", "path": "Brush/Builder Shape"},
  {"op": "shot", "name": "builder-shapes"},
  {"op": "escape"},
  {"op": "quit"}
]
```

| op | Arguments | Does |
|---|---|---|
| `shot` | `name`, `caption`, `crop` (a target), `views` | Saves `NNN-name.png`, and `NNN-name.crop.png` for a crop. `views` also saves each view as shown (`.view-Top.png`) and its bare 3D frame (`.view-Top.raw.png`). |
| `wait` | `frames`, `seconds`, `idle` | Waits; `idle` waits until background jobs (imports, the asset index) finish. |
| `size` | `w`, `h`, `window` | Resizes the main window, or a named child dialog when `window` is supplied. Fails if the requested size cannot be reached. |
| `scale` | `factor` | Sets the UI scale (1.5 draws everything half as big again), as View > UI Scale does. |
| `open` | `path` | Opens a map and waits until it has loaded. |
| `run` | `action` | Runs an editor action by id (the command palette lists them). |
| `select` | `what` (`none`, `all`, `brush`, `ge`), `class`, `ids`, `frame` | Selects objects; `frame` frames them in every view. |
| `tool` | `id` | Makes a tool active (`select`, `place`, `brush`, `face`, `vertex`, `clip`, `measure`, `ge`, `security`...). |
| `tab` | `panel` | Shows a dock panel by its tab title. |
| `render` | `mode` (`wireframe`, `brushColors`, `unlit`, `lit`, `lightmapDensity`), `view` | Sets the render mode of one view or all of them. |
| `menu` | `path` | Opens a menu bar menu and its submenus, as a user would (`"View/Render Mode"`). |
| `palette` | `query` | Opens the command palette and types the query. |
| `quit` | | Ends the run (the steps after it are not run). |

Every other `op` is an input step, the same ones `ui.input` takes: `move`, `click`, `rightClick`, `doubleClick`,
`press`, `release`, `drag`, `scroll`, `key`, `type`, `hold`, `hover` and `escape`. Targets are described in
[SCRIPTING.md](SCRIPTING.md#live-editing-in-the-open-editor). Any step also takes `frames` (frames to wait after it).

A key a step does not take fails that step (`{"op": "size", "width": 1600}` fails with "size does not take 'width'"),
so a typo never turns into a plausible but wrong picture. A failed step is recorded and the run goes on; an exception
anywhere is recorded the same way, and a run that stalls (also before the first step) ends after 120 s with its
manifest written.

`tour:full` walks through the app: every menu and submenu, toolbar tooltips, the command palette and shortcut list,
each view layout and render mode, view context menus, flying in the perspective view (right mouse and W, the camera
in the manifest must move), every tool, every panel, a drag from the asset browser into the top view (the object
count must go up for a mesh), the inspector for each kind of object the map has, Map Check, the unsaved-changes
dialog, and the window at 1280x720, 1920x1080, 2560x1440 and at 150% and 200% scale. It also opens the toolbar
drop-downs and each Preferences page, picks faces (and the surface tool's menus), box-selects vertices, sets clip points,
measures, opens the place tool's class list, shows every panel with nothing selected and the workbench panels with an
object they work on, hovers and selects outliner, Map Check and asset rows, and shows each kind of the asset browser.
`tour:quick` is the startup shot, every panel and the File and View menus.

A tour name can pin the window: `tour:full@1280x720` or `tour:full@1920x1080@1.5` (a UI scale) sets that size and
scale first and runs the whole tour there, without the closing round of sizes.

## The manifest

`manifest.json` has the run (`script`, `map`, `background`, `window` with where the window was and how it was
styled, and `failures`) and one entry per shot:

| Field | |
|---|---|
| `name`, `caption`, `path`, `size`, `scale` | The PNG, its pixel size and the UI scale. |
| `crop`, `views` | The extra pictures, when asked for. |
| `tool`, `docks`, `selection`, `objects`, `modes`, `canvas`, `camera` | The active tool, each dock's visible tab, how many objects are selected and in the map, each view's render mode, the window size in UI units and the perspective camera's location. |
| `lint` | The automatic UI checks: `{code, path, rect, message}`. |
| `log` | Errors and warnings logged (by the editor or Godot) since the previous shot. |

The UI check codes:

| Code | Means |
|---|---|
| `clipped-text` | Text is cut off and cannot be read in full: a label, a button, or a text box's placeholder. A label or button shortened with an ellipsis whose tooltip holds the whole text is not reported. |
| `clipped-cell` | Rows of a list or tree on screen whose text does not fit its column (one finding per column, with a count and an example). Wrapping cells are skipped. |
| `missing-glyph` | The UI font has no glyph for a character; it falls back to another typeface or draws a box. |
| `tabs-overflow` | A tab bar has more tabs than fit; some hide behind the scroll arrows. Docks are skipped: their menu button lists every tab. |
| `empty-button` | A button with neither text nor icon. |
| `outside-window` | A control reaches outside the window (the window is too small for the layout). |
| `overflows-parent` | A control sticks out of its container. |
