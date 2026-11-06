# Scripting the editor

The editor has one op layer. The app, the `ose` CLI and scripts all edit through it, so a script's edit is an ordinary
undo step and shows up live in the app. Two documents do everything:

- **change documents** (`osmap.changes/1`) edit the map. A whole document is one undo step.
- **queries** read the map. You choose the fields, and the output is compact JSON.

Output is deterministic: keys are sorted, whole numbers print as integers, and vectors are `[x,y,z]`. Ids are stable
and unique across brushes, actors and GEs (case-insensitive).

## Surfaces

| How | Command | Use for |
|---|---|---|
| One shot | `ose apply <map.osmap> <changes.json\|-\|JSON> [--dry-run] [--save] [--author me]` | scripts, CI |
| | `ose query <map.osmap> <query-json\|file\|->` | |
| | `ose summary <map.osmap>` | |
| JSON lines | `ose serve [map.osmap]` | long sessions with no reload cost |
| MCP | `ose mcp [map.osmap]` or `ose mcp --live [--pid N]` | agent clients using local stdio |
| Live | `ose serve --live` [`--pid N`] | edit the map open in the running editor (`ose live` lists editors) |

`ose` is `ose\ose.exe` in a release, or `dotnet <editor>/src/OpenScct.Editor.Cli/bin/Debug/net10.0/ose.dll` in a build
from source. [CLI.md](CLI.md) lists every command.

**JSON lines.** Send one request per line: `{"id":1,"method":"apply","params":{...}}`. You get one line back:
`{"id":1,"result":{...}}` or `{"id":1,"error":{"message":..,"op":..,"field":..}}`.
The methods are `apply`, `query`, `summary`, `open`, `new`, `save`, `undo`, `redo`, `build`, `export`, `playtest`,
`check`, `describe` and `exit`.

`export` writes the open map for the original game: `{"format":"original","out":"<folder>"}` (the default) gives the
folder RE+ needs (the T3D, the map's own classes, assets and texts, a README with the steps), `"format":"t3d"` a single
T3D (`"only":["Id",...]` for a selection), and `{"format":"sdc","layout":"editor"|"cooked","game":..}` an `.sdc`
written natively (the editor copy by default). Without `out` it goes to `export/` beside the map. The reply lists the
files written and the findings of the round-trip lint (and, for an `.sdc`, of the read-back check).

`apply` takes the change document as `params`, or as `params.changes` with `dryRun` and `"save":true` beside it.
`open` and `new` replace the open map. While it has unsaved changes they are refused, unless the call has
`"discard":true`. `open` on a cooked `.sdc` recovers its brushes from the compiled BSP: `"brushes":"merge"` (the
default), `"cells"` or `"off"` (the baked world only).

## MCP for mapping agents

For live editing, open the map and choose **Tools → Agents and Scripting → Copy MCP Configuration**. Paste the entry into
your client's local stdio MCP configuration. For a project on disk, use the same executable with `mcp` and the
project path in place of `--live`. For example:

```json
{
  "mcpServers": {
    "openscct-editor": {
      "command": "C:/OpenSCCT Editor/ose/ose.exe",
      "args": ["mcp", "C:/maps/RooftopD.osmap"]
    }
  }
}
```

From source, use `dotnet` as the command and the full path to `ose.dll` as the first argument. Use
`ose live` to list running editors and add `--pid N` to pin a live session. Without a PID the newest editor is
selected. For an isolated editor, pass the background script's `OSE_LIVE_DIR` to the client in the server entry's
`env` and use `mcp --live`. Only the background editor allows `ui_tree`, `ui_input` and full UI screenshots.
In the user's live editor, `ui_shot` with `views:true` captures only the visible map viewports without moving the
camera, changing selection, focusing the app or reading its surrounding UI. It returns PNG paths, render modes,
camera poses and sky-preview state. `crop` is unavailable and UI lint is skipped. A minimized editor may need to be
restored before it can produce a fresh frame; the request returns an error after five seconds instead of hanging.

After editing, poll `editor_state.preview.litReady` before capturing a baked viewport. The `preview` object reports
`sceneSyncPending`, `geometry`, `lighting`, `sky`, `lightingVersion`, `completedLightingVersion` and any `error`.
States are `empty`, `queued`, `paused`, `building` (geometry/sky), `baking` (lighting), `ready` or `error`.
`paused` means the required render mode is not visible; waiting alone will not start it. A completed lighting
version must match the current version, and all three previews must be ready. This covers geometry, sky and
light baking, not asynchronous texture IO. Do not infer readiness from an old status-bar message or an elapsed
delay. The status bar now distinguishes queued work, baking and completion.

Start with `map_summary` (or `editor_state` for the active selection and camera). Request only the IDs and fields
you need. For example, find a surface and read nearby brushes with one `map_query` call:

```json
{"queries":[{"q":"raycast","from":[0,0,256],"max":512},{"q":"find","kind":"brush","near":[0,0,0],"radius":512,"fields":["id","bounds"],"rows":true,"limit":6}]}
```

Use `map_help` with no arguments for a short index, with `{"kind":"op"}` to list edit operations, or with a kind
and name for a single example. For instance, `{"kind":"op","name":"ge"}` returns the fields and a dry-run
example for a ledge. The kinds are `method`, `op`, and `query`. Substitute the IDs, coordinates and file paths
from your project when an example uses placeholders. Use `map_query` with `q:"schema"` for actor properties.

Group related geometry, actors and traversal into one change document. In a project session, call `map_apply`
with `dryRun:true`, inspect the diff, then apply and save. In a live editor, use `map_propose` for a visual
review, then check `map_proposal` for the user's decision. An accepted proposal is one undo step. Finish with
`map_check` and `map_build`, or `map_playtest` when the game is available. Object replies also include
structured content. Invalid edits return the operation and field where available.

## Live editing in the open editor

The editor app hosts the same API while it runs, so a script can edit the map the user has open. `--live` connects
`ose serve` to it instead of opening a project folder:

```
ose serve --live
```

The editor listens on 127.0.0.1 only,
on a free port, and writes `%LOCALAPPDATA%\OpenSCCT Editor\live\editor-<pid>.json` with the port and a random token
(`OSE_LIVE_DIR` moves it; an automated run, headless or in the background, writes it to the `live` folder of its own
user data folder, so the user's `ose live` never lists it). With no editor running, `--live` stops with a clear error.
`Tools > Agents and Scripting > Allow Live Agent and Script Connections` turns it off.

- Every call runs on the editor's main thread against its open session. An applied document is one step on the
  editor's own undo stack, every view updates, the log names it by its `label`, and what it created is selected.
  The slow reads (the `stealth`, `reach`, `route` and `nav` queries, `check` with `deep:true`, and `playtest`) run
  on a copy of the map taken when the call arrives, off the main thread, so the editor stays responsive meanwhile.
- `open` and `new` are refused while the open map has unsaved changes, even with `discard:true`. Save first (method
  `save`), or have the user save or discard their changes in the editor.
- The status bar shows the connected agents and scripts.
- If the editor closes, the call in flight fails and the next call connects again (to a restarted editor, say), so
  a server started once keeps working. A failed call is never resent.
- If that reconnect reaches a different editor (another pid), the first call is not sent. It returns an error that
  names the new editor and its map. Check with `editor`, then call again; the next call goes through.

Live adds three methods:

| method | does |
|---|---|
| `propose` | Takes a change document and shows it as a translucent ghost in every view (green new, cyan changed, red deleted) with Accept, Reject and Frame over the views. Returns the dry-run diff and a `proposal` id. `wait` (seconds) waits for the user's answer. Accept applies it as one undo step. A new proposal replaces a pending one. |
| `proposal` | `{id?, wait?}`: `pending`, `accepted` (with the applied diff), `rejected`, `superseded` or `failed`. While it is pending the ghost follows the user's edits; when an edit means it no longer applies, `problem` says why and Accept is off until it applies again. |
| `editor` | The open map, unsaved changes, the selection, the camera, `viewCenter` (the surface under the centre of the active view: place things there with `at`), the cursor, connected agents and scripts, the pending proposal, and `matchGrey` (`players`, `mode`, `greyed`) while the Match panel greys out what a match leaves out (the ids: the `match` query's `absent`). |

Three more methods let a script look at the editor and drive an isolated background instance like a user.
The live editor permits read-only map viewport captures. It refuses UI inspection and synthetic input, which
could otherwise click or type while the user works or accept a proposal outside the change-document workflow.

| method | does |
|---|---|
| `ui.shot` | `{name?, dir?, crop?, views?, viewportsOnly?, lint?}`: saves a PNG of the window (from the editor's own image, so it works off-screen) and returns its path and size. Without `dir` the PNGs go to `shots` in the run's own user folder. `crop` (a target) also saves that part; `views` also saves each view as shown and its bare 3D frame. `lint` (default on) returns the automatic UI checks: clipped text, hidden tabs, missing glyphs, controls outside their box. |
| `ui.input` | `{steps:[...]}` or one step: `move`, `click`, `rightClick`, `doubleClick`, `press`, `release`, `drag` (`to`, `hold`), `scroll` (`clicks`), `key` (`"Ctrl+Z"`), `type` (`text`), `hold` (`key`, `down`), `hover` (waits for the tooltip), `escape` (closes popups). Synthetic events: the real mouse and keyboard are never touched. A key a step does not take (a typo such as `ofset`) fails the call. |
| `ui.tree` | `{under?, all?, limit?}`: the visible controls with paths, kinds, rectangles, texts and states, popups included. |

The `ui.shot` table entry describes an isolated background editor. In a live editor `views:true` is required and
only raw viewport PNGs are written, with `scope:"viewports"` in the reply. A background editor can use the same
path explicitly with `viewportsOnly:true`; its ordinary full-window screenshot behavior remains available.

A target (`at`, `to`, `crop`) is `[x, y]` in window coordinates, `action:<id>` (a toolbar button), `tab:<title>` (a
dock tab, or a tab of a panel's own tab bar), `dock:<title>` (the dock holding that panel, wherever the layout put it),
`menu:<title>`, `item:<text>` (in the open popup menu),
`view:<Perspective|Top|Front|Side>`, `text:<label>` or a node path from `ui.tree`. `offset: [fx, fy]` picks a point inside it (default the centre).

Every failure of a `ui.*` call (a folder that cannot be written, an unknown target) comes back as an error reply; the
connection stays open. The calls are logged in the editor's Log like other API calls.

To give a script its own editor that never appears on the user's screen, start it with the script, then connect with
the live folder it prints:

```
pwsh tools/Start-BackgroundEditor.ps1 -Map C:/maps/MyMap.osmap
#   Id   : 46392
#   Live : C:\...\local\background\20261104-120000-MyMap\live
$env:OSE_LIVE_DIR = 'C:\...\local\background\20261104-120000-MyMap\live'
ose serve --live
Stop-Process -Id 46392      # when done
```

Do not start a background editor by hand with `-- --background`: Godot shows and may focus its window before any
editor code runs, and the editor would share the user's settings, autosaves, recent files and caches. The script
starts the window minimised and unfocused (a temporary `app/override.cfg`) and gives the run its own folders, as
described in [SCREENSHOTS.md](SCREENSHOTS.md#background-mode). A background editor writes its discovery file only to
its own folders, so it never shows up in the user's own `ose live` list.

A change document pasted into the command palette (text starting with `{` or `[`) is previewed the same way (Enter)
or applied at once (Ctrl+Enter). The `Tools > Agents and Scripting` menu has the same from the clipboard (Preview / Apply Change
Document from Clipboard), plus Accept Proposal, Reject Proposal and Frame Proposal. Started with
`-- --changes=<file>`, the app's `live.previewFile` action previews that file.

## Typical script

1. Read the map with `summary` (a short overview). It gives counts, bounds, classes, team starts, objectives, GEs,
   layers and the top problems.
2. Use `find` with `rows:true` and only the fields you need.
3. Send one `apply` with every edit, using `$symbols` and placement by reference. `dryRun:true` returns the diff
   without changing the map.
4. Run `check` and fix problems by id. Then `save` (or `--save`, or `"save":true` in `apply`).

## Change documents

```json
{"format":"osmap.changes/1","author":"script","label":"west wing","dryRun":false,"ops":[ ... ]}
```

`format`, `author`, `label` and `dryRun` are optional, and a bare `[ ...ops ]` is accepted too. `dryRun` must be a JSON
`true` or `false` (not `"true"` or `1`). `label` names the undo step. Ops run in order against the live map, so a
later op sees what earlier ops made. If any op fails, nothing changes, and the error names the op and the field:

```json
{"ok":false,"error":{"op":3,"field":"props.TeamNumbr","message":"PlayerStart has no property 'TeamNumbr'; did you mean TeamNumber?"}}
```

A success returns the diff, the same as a dry run:

```json
{"ok":true,"undo":"west wing","ids":{"$hall":"Brush4","$cam":"SCamera0"},
 "created":{"Brush4":{"brush":"subtract","loc":[256,0,80],"min":[0,-96,0],"max":[512,96,160],"builder":"cube"}},
 "updated":{"PlayerStart0":{"loc":[[-384,0,48],[-384,0,80]],"props.TeamNumber":[0,1]}},
 "deleted":["Light3"],"project":{"mapSettings.Story.TimeLimit":[10,15]},"warnings":["op 5 where: matched nothing"]}
```

Every object an op creates or edits is stamped with `editor.author` and `editor.op` (`label#index`). Find your own
work with `{"q":"find","author":"script"}`.

### Ids, symbols and targets

- `"id":"$door"` makes a fresh id and binds the symbol. Later ops (and points, props and filters) can use `"$door"`.
  `count` and `duplicate` bind `$door#1`, `$door#2`, and so on. A builder that makes several brushes binds its first
  brush to `$door` and the others to `$door#p1`, `$door#p2`... (and `$door#1#p1` for the copies of a `count`).
- `prefab.place` with `"id":"$door"` binds `$door` to the instance and `$door.<part>` to each of its objects
  (`$door.switch`, `$door.beam_1`...).
- `"id":"Door1"` asks for that exact id, which must be free. In `update`, `brush` and `ge`, the id names the object
  to edit.
- Ops that edit objects take `id`, `ids` or `where`. `where` is a find filter, for example
  `{"class":"SCamera","layer":"Security"}`.

### Points and placement

A **point** is written `[x,y,z]`, `"x,y,z"` or `"Name@anchor+dx,dy,dz"`. Anchors pick a point on the object's bounds:
`center`, `origin` (the Location), `top`, `bottom`, `xmin`, `xmax`, `ymin` and `ymax` (`c`, `zmax`, `zmin` and `floor`
work too). You can join them with `_`, for example `xmax_bottom` or `top_xmin_ymin`. With no anchor, an actor's point
is its origin and a brush's is its center.

Units are Unreal units: X forward, Y right, Z up. 16 is one grid step, and a player stands 175 tall.

**Placement fields** work in `create`, `move` and `update`. They apply in this order:

| field | does |
|---|---|
| `at` | the target point |
| `offset` | adds `[dx,dy,dz]` to it |
| `near` (+ `dist`, default 96) | picks a free spot that distance from an object or point: in open space, in sight of it, and not on another actor |
| `pivot` | the anchor of the placed object that lands on the point. `"xmin_bottom"` puts a corridor's start at a doorway |
| `wall` | `"nearest"`, `true`, `"+x"`, `"-x"`, `"+y"`, `"-y"` or `[dx,dy,dz]`. Casts from the point (up to `reach`, default 4096) and puts the object's back on the first wall it meets (`gap` off it). An actor turns to face out of the wall (`keepRot` keeps its rotation); brushes and volumes keep their rotation, so give `rot`/`rotDeg` first to turn them |
| `on` | `true` drops from the point to the floor below. An id drops onto that object: into a room brush, onto an add brush's top, or onto an actor's collision top. The object's bottom stands on the floor. An actor's bottom is its CollisionHeight, so starts land at the right height (without the class catalog the height is unknown: you get a warning, and the origin stands on the floor) |
| `lift` | raises the result |

`rot` and `rotDeg` apply before placement, so a turned brush is placed by its turned bounds. `face` applies after.

Rays run against the map's CSG brushes. Imported maps also use their baked collision; the brushes an `.sdc` editor
copy brings in (`"baked": true` in their `src`) are in that collision already, so rays skip them while the baked world
is kept. With the baked world off, the brushes make the whole world, solid outside every brush, as the build makes it.
Placement by reference finds floor heights, so a script never needs a floor z: place things `on` the floor.

### Values

Props follow the class schema whenever the class catalog is loaded. Look a class up with
`{"q":"schema","class":"SCamera"}`.

| type | how to write it |
|---|---|
| number | a number (`1` becomes `1.0` for a float property) |
| string or name | a string |
| bool | `true`/`false` (`0`/`1` also work) |
| enum | the value name or its index |
| vector, rotator | `[x,y,z]` |
| color | `[r,g,b,a]` |
| actor reference | an id or `$symbol` |
| asset | `"Package.Group.Name"` |
| static array element | `"Prop[2]": v` |

`null` removes a stored value, which returns it to the class default. Some props go to the typed fields instead:

- On actors: Location, Rotation, PrePivot, DrawScale and DrawScale3D.
- On brushes: Location, Rotation and PrePivot. A piece rebuild (a `shape` on a multi-brush piece) refuses them: rebuild
  first, then move or rotate all its parts.

### Op reference

| op | fields | notes |
|---|---|---|
| `create` | `class` + `props` (an actor), or `brush`: SHAPE + `csg` (`"subtract"` by default, or `"add"`) + `flags` + `mat`/`mats`, or `ge`: kind + `points` + `mask`/`platform`/`appearance`. Also placement, `rot` (`[pitch,yaw,roll]`, 65536 = one turn), `rotDeg`, `face` (a point to look at), `scale`, `scale3d`, `postScale`, `prePivot`, `layer`, `folder`, `note`, `hidden`, `locked`, and `count` + `step` (a row of copies, for actors, brushes and GEs) | `class` + `brush` makes a volume |
| `update` | targets + `set` / `props` / `unset`, `rename`, `shape`, `csg`, `flags`, `mat`/`mats`, `points`/`mask`/`kind`/`appearance` (GEs), placement, rotation, scale, meta, `keepTextures` | `rename` takes one object and a real id (not a `$symbol`). It rewrites every `{"$actor":old}` reference. A brush given a new `scale` or `postScale` keeps its texture size as `scale` does, and a `shape` with new parameters for the same builder keeps each face's material, flags and texture size and alignment (a builder that makes a different number of faces gets fresh alignment, not stretched by the brush's scales); `"keepTextures": false` stretches the textures and gives a new shape fresh mapping |
| `delete` | targets | warns about references left behind |
| `move` | targets + `by:[dx,dy,dz]` or `to`/`at`: POINT, plus `on`/`wall`/`lift`/`keepRot`, and `stick` (`true` or `{within, axes, targets}`) | several objects move together, then each snaps on its own. `stick` then pulls them, together, onto the nearest neighbour faces as the app's Neighbour Snap does: within `within` units (16), on `axes` (letters from `xyz`; default the axes the move changed, all three for `to`/`at`), against `targets` (`brushes`, `meshes`, `actors`, `baked`; default all but `actors`). A face sticks touching (face to face) or in line (same plane); the objects' own baked copies, where they were before the move, are not neighbours. The result notes what each axis stuck to (`stuck X to Wall3 -X face (touching)`), or that nothing was in reach; a mesh measured by its collision cylinder is noted as an approximate box |
| `rotate` | targets + `by`/`byDeg`/`to`/`toDeg`/`face`, and `around: "center"` or a POINT | `by` is a world-space turn |
| `scale` | targets + `by` or `to` (a number or `[x,y,z]`), `keepTextures` (default `true`) | actors: DrawScale(3D). Brushes: MainScale, and their textures keep their size (a stretched face shows more repeats, a face that stays put looks the same); `"keepTextures": false` stretches the textures with the brush, as UnrealEd does |
| `duplicate` | targets + `by`, `count`, `as` (`"$c"` or a list) | the k-th copy is offset by k×`by` |
| `brush` | like `create`/`update` for brushes | creates the brush, or edits it if `id` exists. `shape:{"height":512}` changes one builder parameter (`ose shapes <kind>` lists them) |
| `surface` | brush targets + `faces` (`[indexes]`, `"all"`, `"walls"`, `"floors"`, `"ceilings"`) and any of `mat`, `flags`/`clearFlags` (names such as `"invisible"`, `"twoSided"`), `lightmap`, `align` (`default`, `floor`, `wall`, `face`, `fit`, `cylinder`), `scale` (units per texel, a number or `[u,v]`), `rotate` (degrees), `pan` (`[u,v]`), `flip` (`u`, `v`, `uv`) | the surface tool's edits, applied in that order. `ose edit` has the vertex, clip, extrude and wall-piece tools |
| `ge` | `id`, `kind`, `points`, `mask`, `platform`, `appearance`, and `layer`, `folder`, `note`, `hidden`, `locked` | creates the GE, or edits it if `id` exists; `appearance: true` enables generated geometry, an object sets `width`, `radius`, `spacing`, `material`, and `false` or `null` removes geometry while keeping the GE |
| `conveyor` | `id`, `points` (two or more; each POINT or `{"at":POINT,"radius":r}` to round that corner), `floor`, `loop`, `look` (`frame`, `driver`, `treadmill`, `escalator`), `walls` (`auto`, the default: open where another conveyor joins; `both`, `left`, `right`, `none`), `width`, `speed` (uu/s, negative runs it backwards), `on`, `trigger` (`toggle`, `on`, `off`, `reverse`: what an event to its Tag does), `items` (mesh names), `scroll` (`[{"target": id, "tile": uu, "axis": "u"\|"v", "material": name}]`, replaces the scroll targets; a negative `tile` scrolls the texture the other way, `material` scrolls only that material of the mesh), `drive` (mesh actor ids) with `tile` (default 128) and `axis` (default `v`), `next` (the conveyor, id or `$symbol`, items go on to from the end they leave by: the end, or the start while it runs backwards; `null` clears it), `props`/`set`, and meta | creates a native conveyor (`Svm.Conveyor`) at its first point, or edits the conveyor `id` names. Points are on the riding surface; `floor:true` drops each to the floor under it and lifts it for the look, as the conveyor tool does. With `drive` and no `points`, a new conveyor is a driver fitted to the top of those meshes (the app's Gameplay > Conveyors > Drive Selected Meshes), scrolling their textures at its speed (each mesh along its own longer side, so meshes turned different ways in an L give an L; the path follows the belt's top and width, bridges the plate between two meshes and stops where the belt ends); on an edit or with points, `drive` sets the scroll targets. Wire a switch to it with `link` |
| `link` | `from` (one id or a list), `to` (one or a list), `tag` | sets `from.Event` = `to.Tag` (default: `to`'s stored Tag, else its id). An `SAlarm` sender links as the wiring tool does: movers go to its `MoversToLock`, objectives to its `ObjectiveTriggersToLock`, anything else to its `Events` |
| | `from`, `prop`, `to` | sets an actor-reference property. An array property is appended to |
| | `clear: true` | unlinks: clears `Event` (and an alarm's `Events`) |
| `layer` | `name` + `color` `#RRGGBB`, `visible`, `locked`, `rename`, `delete`, `add:[ids]`, `where` | creates the layer if it is missing (an op that puts objects on a missing layer adds it too). `Default` is the base layer and cannot be renamed or deleted |
| `folder` | `path` + targets, or `path` + `rename` | sets outliner folders. `rename` moves subfolders too |
| `settings` | `levelInfo:{..}`, `mapSettings:{Mode or "*":{..}}`, `mapInfo:{..}`, `lighting:{..}`, `baked:{..}`, `title`, `mapId` | `mapId` is letters, digits and `_`. The original game needs exactly 5 letters or digits. `lighting` takes any of `mode` (`quality`, `faithful`), `density`, `samples`, `lightSize`, `sunSize`, `bounce`, `bounceSamples`, `atlasSize`, `relightImported`, `bakeDefaultLightRadius` (null clears it) (docs/FORMAT.md, "Lighting settings"); values out of range are refused. `baked` takes `keepRender` and `keepCollision` (an imported map only; a layer that is not kept is built from the brushes, docs/FORMAT.md "Baked world") |
| `identity` | `name`, `family`, `variant`, `description`, `dossier:{when, where, summary}`, `authors`, `version`, `language`, `players` (1..24), `tags`, `floors` (`"auto"` or heights; `[]` for none), `places` (replaces all), `place:{number, name, at, floor, delete}`, `pictures:{menu, loading:{spy:[..], merc:[..]}, briefing, banner}`, `texts:{<lang>:{name, description, dossier:{when, where, summary}, places:{"<n>": name}}}` | the map's name and what the menus show (FORMAT.md "Map identity"). Only the given fields change; null clears one. In `texts` each language given replaces that translation whole (null or `{}` removes it; the map's own language is refused, and each text has the limit of the map's own: 48 for the name and places, 64 for `when` and `where`, 300 for the rest). OpenSCCT shows `en`, `fr`, `de`, `es` and `it` translations. A picture is `{"from":"file","file":"pictures/x.png","focus":[x,y]}`, `{"from":"camera","bookmark":"Overview","look":"plain\|nightvision\|thermal","fov":80}` (the bookmark must exist) or `{"from":"plan"}` |
| `select` | targets + `mode` (`set`, `add`, `remove` or `clear`) | sets the app's selection after the commit |
| `workspace` | `grid` (1..4096), `rotationGrid` (rotator units), `camera:{at, rot\|rotDeg\|look}`, `play:{side, mode, bots, rules, players, difficulty}`, `movement:{stepHeight, walkableSlope, standHeight, spyCrouchHeight, mercCrouchHeight, radius, spyRunSpeed, mercRunSpeed}`, `storeys:[{name, bottom, top}]`, `shown` | editor-only settings saved with the map. `movement` also sizes new blockout pieces and the Map Check clearances. `play.difficulty` is `easy`, `normal`, `hard` or `expert` (the bots' skill). `storeys` names the map's own storeys for the storey filter (`[]` goes back to the ones found from the floors). `shown` picks the storey the views show: a name, a number (1 is the lowest) or `all`; it reads the storeys after this op's own `storeys` |
| `storey.duplicate` | `storey` (a name or number), `below:true` (under the lowest storey) or `by` (units up); default: on top of the highest storey | copies every object whose middle is on that storey. A storey is as tall as its range; the open top of the highest detected storey is as tall as what stands on it, and at least the storey below. When the map has its own storeys a range is added for the copy. Warns when the copy adds player starts, that an imported map's baked world is not copied, and when it copies brushes an `.sdc` import marked baked (their copies are not built while the baked world is kept) |
| `bookmark` | `name` + `at`, `rot`/`rotDeg`/`look` (a point), `note`; or `rename`; or `delete:true` | a saved camera view |
| `reference` | `name` or `path` (picks the image; a path several images use needs the name) + `view` (`top`, `front`, `side`), `origin`, `unitsPerPixel`, `opacity` (0..1), `rename`, `calibrate:{from, to, length}`; or `delete:true` | a reference image behind a 2D view, centred on `origin`; a new one is named from its file. In the Top view the image shows on the storey its `origin` height is in. `calibrate` takes two world points measured on the image and their real length in units, and scales the image about `from` |
| `prefab.place` | `name`, `params`, placement, `rot`/`rotDeg`/`face`, `keepRot`, `id`, meta | see [Prefabs](#prefabs) |
| `prefab.update` | `id`/`ids` (instances, or any of their objects), `name` (every instance of a prefab) or `all:true`; `params` (one instance) | rebuilds instances from their prefab |
| `prefab.break` | `id`/`ids`, `name` or `all:true`; `delete:true` | unlinks instances (and deletes their objects with `delete`) |
| `prefab.save` | `name`, targets or `selection:true` (the app's selection), `pivot`, `rot`/`rotDeg`, `title`, `summary`, `category`, `to` (`project` or `library`), `overwrite`, `link`, `as` | saves objects as a prefab |
| `mover` | `id` + any of `keys` (`[{"at":[x,y,z],"rot":[p,y,r]}]`: offsets from the base, 1 to 8), `addKey` (`true`: one leg further, or a POINT), `deleteKey` (index), `startKey`, `moveTime`, `stayOpenTime`, `delayTime`, `delayTimeLastKey`, `otherTime` (seconds), `eased`, `legs` (`[{"key":1,"time":4,"smooth":true}]`: a key's own leg time from the key before, `null` for the mover's `MoveTime`, and easing, `true`/`false`, `null` for its glide; a member left out is unchanged, and each entry sets `time`, `smooth` or both), `state` (an InitialState name such as `TriggerOpenTimed`), `showKey` (index) | the mover workbench's edits, applied in that order. Key edits keep the base and put the mover at its start key; deleting or reordering keys keeps the start key on the same key, and a close state moves a start key of 0 to 1. `showKey` draws the mover at a key, so a following `move` or `rotate` of it writes that key (`KeyPos`/`KeyRot`) instead of moving the base; showing the start key ends that. More than 8 keys is refused: the game's `KeyPos` holds 8. `legs` is OpenSCCT's own per-key timing (SVMap 1.2 `sim/movers.json`): the leg between key n-1 and n, either way, takes key n's time (0.005 to 3600 s; key 0 has none), and a leg eases where it touches a smooth key; deleting or moving keys carries their timing. The original game runs every leg in `MoveTime`, so `--target original` warns |
| `magic` | `id`, `groups` (`[{"actions":[{"event":"Door1","delay":1.5,"type":"trigger","on":"trigger"}],"repeat":0,"sequence":false}]`), `tag`, `stopActor` (an id, or `null`) | edits an SMagicEvent; with no `id`, or a new `$symbol`, it makes a new one at `at` with Tag `tag` (an `id` that names no actor is an error). Every action needs an event, delays are 0 or more, `repeat` is -1 (off), 0 (no limit) or more. Types: `trigger`, `untrigger`, `triggerUntrigger`, `untriggerTrigger` (or 0-3, or `EVT_...`) |
| `camnet` | `order` (camera ids) + `loop`, or `remove` (ids) | `order` links whole networks in that order (the first gets `bFirstCam`); `remove` takes cameras out and joins their neighbours |
| `camera` | `id` (an SCamNetwork) + `name` (CamName), `pitch`/`yaw` (`[below, above]` rotator units from the placed rotation) | a network camera's HUD name and turn limits. The game applies a limit only when both ends are non-zero; with a 0 at either end the camera turns freely that way (the `camnets` query flags such limits) |
| `props.paste` | `from` (an actor in this map) or `props` (an `osmap.props/1` document, e.g. copied in another map) + targets, `keys` | copies the stored values onto the targets. Default keys: all but identity, placement and actor links. Values a target's class lacks become warnings |
| `propset` | `set` (`SCamera.blinde`) + targets | applies a property set (the map's `propsets.json`, then the user library) |
| `tip` | `id` (an existing tip), or `at` + `spy` for a new one; `merc`, `radius` (150), `height` (100), `marker` (true: a help path marker 100 below), `mesh`, `endsGame` (SEndZoneTip), `onlyWithHelp`, `stay`, `endGameDelay` | zone tips and help path markers. The marker's mesh defaults to the map's own marker mesh, else the stock `help_path_INFO` |
| `zone.action` | `mission` + `add` (an actor) with `type` and `delay`; or `message: {"spy","merc","seconds"}` with `delay`; or `set` (index from the `zone` query) with `delay` and/or `type`; or `remove` (index) | what a mission or objective does when complete, through an SMagicEvent relay of its own (made on first use, Tag `<mission>_Done`; what it fired before becomes its first action). An actor the mission already reaches is refused. Only the own relay's actions can be timed or deleted; removing a receiver the mission fires directly is refused while other actors fire the same Tag |
| `match` | targets (actors); `players` (`[min, max]`, 0 = no limit, `"any"` clears), `outside` (`"hidden"` or `{"key":n}`), `inside` (`"normal"`, `"hidden"` or `{"key":n}`), `win` (objective counts for 1v1..12v12, 0 = the mission's own); `null` resets a member; `clear:true` | which matches an actor is in, by player count (everyone in the match, bots included), and a mission's win targets. Key states are for movers, which are held exactly at that key; an open mover uses its last key, as RE+'s OpenInRange ends there. A range on a light or static mesh is a build error |
| `outfits` | `team` (`spy` or `merc`); `body`, `head` (PNG paths), `model` (`{"glb":path}` or `{"stock":"SPerso.ATT_01"}`), `goggles` (`{"bone", "offset"}`), `cosmetics` (`map`, `player` or `none`); `null` removes a member; `clear:true` | what a team wears on this map. Files outside the map are copied into `assets/outfits/` when the document commits (listed in `files`). The result warns when a model lacks bones the team's animations drive |
| `lights.recover` | `mode` (`off`, `exact` or `full`, the default), `twins` (more folders of editor copies), `fitSeconds` (a time limit on the fit; none by default, so the result does not depend on the machine), `ignoreTwins`, `forceFit` (fit past the fit gate) | runs the light recovery of a map imported from a cooked `.sdc` again (FORMAT.md, "Baked world"): lights an earlier run added are replaced whole (a rerun reuses their ids), the report and `lighting.bakeDefaultLightRadius` updated, one undo step. It runs inside the call: a big map takes minutes, a live editor waits for it, and a dry run or proposal preview runs it too, so for a big map run `ose sdc-lights` on the saved project instead. Warnings say what to check (why a relight will not match, lights to review) |

Rotations an op writes are tidy: yaw in 0..65535, pitch and roll in -32768..32767. `update` with a `shape` on a brush
of a multi-brush blockout piece (a shell room, stairs...) rebuilds the whole piece: its first brush keeps its id, the
other parts are remade and bound to `$sym#p1`, `$sym#p2`... `duplicate` of a whole piece makes a piece of its own.

A piece is keyed by its first brush's id. When that brush goes (deleted by an op, in the app or by a clip, or renamed
by `update`), the other parts take the id of their new first part. So the piece stays whole and never joins a later
piece.

Movers carry their base along: any edit that moves or turns a mover (ops, the gizmo, typed transforms) shifts its
`BasePos`/`BaseRot` the same way, as UE2's editor does, unless the edit sets them itself. This covers duplicates,
pastes, copies along an axis and drop to floor or ceiling too. A mover placed in the app gets a `BasePos`/`BaseRot`.
Mirror mirrors the base and flips the `KeyPos` and `KeyRot` offsets, so every key lands on its mirrored place.

**SHAPE** can be written five ways:

| shape | what you get |
|---|---|
| `"cube"` | a default cube |
| `{"size":[x,y,z]}` | a box of that size |
| `{"min":[..],"max":[..]}` | a box from world corners; this also places it |
| `{"kind":"cylinder",...params}` | any registered builder. A wrong parameter name lists the valid ones |
| `{"faces":[[[x,y,z],...],...]}` | a convex polyhedron in local space; windings are fixed to face outwards |

`mats:{"top":..,"bottom":..,"sides":..,"0":..}` sets materials by face direction or face index.

**Flags** are a number or names: `semisolid`, `notsolid`, `portal`, `antiportal`, `invisible`, `masked`,
`translucent`, `twosided`, `unlit`, `mirrored`, `nosmooth`, `speciallit`, `environment`, `modulated`, `fakebackdrop`.

## Queries

Send `{"q":KIND,...}`. To run several in one call, send `{"queries":[...]}` or an array, and you get an array back.
A query that fails returns `{"error":{...}}` in its slot.

| q | fields | returns |
|---|---|---|
| `find` | filters + `fields`, `rows`, `limit` (100), `offset`, `sort` | `{"items":[..]}`, or with `rows:true` `{"cols":[..],"rows":[[..]]}`; plus `count` and `more`. Sorted by distance when `near` is given |
| `count` | filters + `by` (`class`, `kind`, `layer`, `folder` or `author`) | totals |
| `get` | `ids`, `fields` (default `*`: everything not at its default value) | objects |
| `summary` | `classes` (20) | the map overview |
| `schema` | `class`, `text`, `all`, `own` | the class chain, and rows of [name, type, default, from]. Enum values are listed when the list is short |
| `classes` | `under`, `text`, `all`, `limit` | placeable class names |
| `check` | `target` (`openscct` or `original`), `only`, `limit`, `deep` | finding rows [sev, code, message, ids, at, fix, shapes]. `fix` is null or `{label, changes}`: a change document to `apply`. `shapes` is null or `[{kind, points}]` (`path` or `outline`): what the views draw for it, such as a zone leak's way through and its cut. `deep:true` compiles the map first and adds the deep rules (zone leaks, portals that seal nothing, actors in the void, floating meshes, mesh collision caps, reachability) |
| `edges` | `id`, `depth`, `limit` | wiring rows [from, via, to, event or ref], plus `unheard` events |
| `raycast` (or `trace`) | `from`, then `to` or `dir` (down by default), `max` | hit point, normal, distance, `by` (a brush id or `baked`), face |
| `pointInfo` | `at`, `radius` | solid?, the brushes at the point, floor, ceiling, wall distances, nearby actors |
| `history` | undo position, dirty flag, and the steps |
| `workspace` | | grid, rotation grid, camera, bookmarks, reference images, play options, movement limits and layers |
| `storeys` | | the storeys the storey filter steps through, lowest first (`name`, `bottom`, `top` (null when open), `floor`), and `source`: `map` (the map's own) or `detected` (found from its floors), and `shown`: the shown storey's name, or null for all |
| `backups` | | the project's save backups, newest first (`n`, `name`, `saved`, `folder`), and `keep`: how many each save keeps (the user's setting, shared with the editor). Restore with `ose backups <project> --restore <n>` |
| `identity` | | the map's name, dossier, authors, recommended players, tags, places, floors, picture recipes and translations |
| `importReport` | | for an imported map, its import report in parts (`sections`: `key`, `title`, `lines` with `text`, `severity`, `objects` as `Kind:Id`): what came in, the cooked-out actors by class, the lifted actors, what was recovered or rebuilt (first, for a map RE+ recovered from a compiled map), dropped, the warnings and other notes. The project keeps the import's findings (`source.findings`), so this is the same report `ose import` printed; a map imported before they were kept has only the parts the project itself holds |
| `prefabs` | `name` (optional) | the prefabs the map can place (name, title, category, source, parameter defaults, summary) and its instances (`parts`, `stale` when the prefab changed since, `missing`). With `name`: that prefab in full, with its instances |
| `assets` | `kind` (`material`, `mesh`, `texture`, `sound`), `text` (every word, any case, in the name, package, group, origin, kind or the English gloss of a French name), `origin` (`MyLevel`, `shared` or a map id), `limit` (50) | rows of [kind, path, origin, facts, english], best match first |
| `asset` | `path`, `kind` | one asset: facts, bounds, materials, the converted maps that use it, and its usages in this map |
| `mover` | keys (offset, turn, world point, when one activation reaches it, `legTime` and `smooth` as the game runs them, `ownTime` and `ownSmooth` when the key has its own), times, glide, state, one activation's `timeline` steps (as the game runs it from the start key) with `openAt`, `problems` (where the game moves it differently from the key list), `shownAtKey` (the key it is drawn at while keys are placed), and what triggers it (`triggeredBy`) or locks it (`lockedBy`) |
| `magic` | `id`, `play` (`[{"t":0,"trigger":true,"fromStopActor":false}]`, default one trigger at 0), `at` (seconds; default the end) | the groups (with what each action `reaches`) and the `firings` the game would make, in order; then what they do by `at`, followed through other SMagicEvents (8 relays deep): `deliveries` (`t`, `to`, `event`, `trigger`, `from`), the trigger `lights` they reach (`on` then and at the `start`) and the `movers` a trigger has started (with its time) |
| `camnets` | | every camera network in ring order (names, limits, `first`), `loop`, `problems`, and the `entry` camera CamNet opens on |
| `propsets` | `class` or `id` (optional) | the property sets (all, or those that fit) with their values and source |
| `tips` | | zone tips (texts, cylinder, flags) and help path markers |
| `zone` | `id` (optional) | missions and objectives that fire something when complete, with their `actions` (`index`, `kind`, `event`, `reaches`, `delay`, `type`, the `relay` it goes through and whether it is the mission's `own`) and the mission's own `relay` |
| `modes` | `mode` (optional: a number or a name such as `DiskHunt`) | the game modes the map has missions for, with how many actors belong only to them; with `mode`, those actors' ids |
| `stealth` | `lights` (`start`, `blackout`, `lit` or `switch:ID`: the lights after that actor's first use), `off` and `on` (light ids of the map's lighting), `stance` (`crouch` or `stand`), `spacing` (64), `mercSight` (true), `night`, `near` + `radius`, `cells`, `limit` | how dark the floors spies reach are: counts of hidden, partial and exposed cells, how many a merc's motion tracker sees (`tracked`) or a torch reaches, how many are dark but stand out against a bright background (`silhouette`), the same per objective approach, the switches (with the lights each reaches and whether its first use `sends` a trigger or an untrigger), `vision` and `viewpoints`, `worldOnlyLights` (light the world, baked or drawn live as dynamic lights, but not pawns) and `pawnOnlyLights` (light pawns but not the world), and with `cells:true` rows [x, y, z, stand, crouch, band, merc, torch, back, albedo, silhouette] |
| `reach` | `drops` (`survivable`, `unhurt` or `bot`), `mask` (a number or `all`), `limit` | per side: nodes, nodes reached from its starts, starts on no floor, traversal links, and the doors and lifts it met as rows [id, kind, works, used, by] (`works`: walk, trigger, startsOpen or never; `by`: what opened it); areas only spies reach; findings (objectives spies cannot reach, switches neither side can, with the doors and lifts that stop each side) |
| `route` | `from`, `to` (points or ids), `side` (`spy` or `merc`), `drops`, `mask` | `reachable`, the `length`, the drop `damage` in all (% of a spy's health) and the legs (`how`: walk, crouch, drop, ledge climb, zip line, door, lift...) with the GE or `mover` each uses, what `openedBy` a door or lift, and drop damage, or `problem` and `closest` (how far the side gets). A route whose drops add up to a fatal fall is not reachable |
| `nav` | `side`, `drops` (`bot`), `mask`, `near` + `radius`, `limit` (200) | the bots' graph: nodes, edges by kind, and link rows [how, ge, start, end, height] |
| `match` | `players` (default: the playtest's count), `mode` (a game mode name; default every mode) | the actors with player-count settings, `layouts` (the counts where the layout changes), the map's `modes`, `problems`, `warnings`, and `preview`: the match at that count in that mode as the server lays it out (team size, missions with their `mode`, `objectives` and `needed`, `leftOut` objectives and missions, `hidden` actors, held `movers`), `absent` (every actor that match leaves out of play: what the Match panel greys out, other modes' objects included when `mode` is given) and `sizes` (what each team size plays at full teams in that mode, sizes that play alike joined: `from`, `to`, `missions` with `objectives` and `needed`, counts `leftOut`, `hidden`, `held`) |
| `outfits` | | the teams' outfits as stored, the `assets/outfits` folder, and `problems` (missing files, missing bones) |
| `recoveredLights` | | an `.sdc` import's light recovery: the `report` (`extra.sdcLightRecovery`) and each recovered light's id, folder, location, source, confidence, support and light properties |

A mesh from `assets` is placed with `create` (`"class":"StaticMeshActor","set":{"StaticMesh":"<path>"}`), and a
material goes on brush faces with `mat`. Importing a glTF or OBJ mesh into the map's own assets is
`ose asset-import` (it writes files, so it is not an undo step).

The last four analyse the map as OpenSCCT loads it. The first one compiles the map (about a second for a big map) and
loads it with the server's loader; the compiled map is kept until the next edit. Graphs come from OpenSCCT's
`NavBuilder` (the bots' own navigation) and are kept on disk by map content, so a second run on an unchanged map is
quick. A door blocks a side unless something the side reaches opens it (a switch by the game's use test, a touch
trigger or sensor that detects the side, or walking into it); the game's lift (`SLift`) carries a side between the
floors in its cabin at each key once the side can use one of its call switches, and opens its own doors; a plain mover
is a lift only when it moves 64 or more up or down, is wide enough to stand on and has room on top. Walls an event
breaks and triggers that switch GEs are not followed, so on a map that has them the deep check's unreached findings
are notes. The stealth levels come from the bots' light model
(`MapAiLight`): only lights that light pawns in game count (`bApplyToInGameLighting` or `bDynamicLight`). Hidden is a
level below 40/127.5, exposed from 100/127.5. A cell is `tracked` when a merc eye over floor mercs reach sees it within
the motion tracker's 2600 (every viewpoint in range is tried, the line traced from the merc to the spy as the game
does). `back` is what the mercs who see it see right behind the spy, in the same units, the median over up to 5 of
them: the first surface on the line past the spy, lit by the map's own lightmap where it has a real bake (a source
map's kept lightmaps or a full build; the analysis compile's fast bake has no shadows, so there the light model of the
world lights stands in, as it does for static meshes and movers) plus the run-time (bDynamicLight) lights that are
on, trigger lights among them (one without bDynamicLight lights pawns only, as `pawnOnlyLights` lists), times
`albedo`, the mean of that material's texture (0.5 without one); an unlit surface shows its texture's colour, and a
sky face the sky zone seen from its SkyZoneInfo. A `silhouette` cell is not exposed but its background is at or
above the exposed level. `night:true` adds night vision's ambient lift (2 x 15/255) to the spy and the background,
then the screen's gain (2, capped; the grain's spread and the vision's glow are left out).

**Filters** (in `find`, `count` and `where`) can be combined. A `where` takes filters only: `limit`, `offset`, `sort`,
`fields`, `rows`, `q` and `by` belong to `find` and `count` queries and are refused there.

| filter | matches |
|---|---|
| `kind` | `actor`, `brush` or `ge` |
| `ids` | these ids |
| `class` | this class and its subclasses (`exact:true` for the class only) |
| `tag`, `event` | the Tag or Event value |
| `near` (+ `radius`, default 512) | objects within the radius of a point |
| `box` | `[[min],[max]]` |
| `layer` | this layer |
| `folder` | folders starting with this prefix |
| `text` | text in the id, class, note, folder or string props |
| `has` | objects with these stored props |
| `props` | `{"TeamNumber":1}`: effective values (stored, else the class default; unknown counts as 0/false/"") |
| `csg` | `add` or `subtract` |
| `author` | who made or last edited it |
| `prefab` | the objects of a prefab instance (`"LaserGridAlarm0"` or its symbol), or `true`/`false` for any instance or none |

**Fields**: `id` `kind` `class` `short` `loc` `rot` `scale` `scale3d` `postScale` `prePivot` `tag` `event` `props`
`layer` `folder` `hidden` `locked` `note` `author` `op` `prefab` `src` `bounds` `size` `center` `csg` `flags` `builder`
`faces` `mats` `shape` `points` `mask` `platform` `appearance` `refs`. Any other name is read as a property (its stored value, else the
class default), for example `"CollisionHeight"` or `"props.Event"`. `class` is the full actor class, `Brush` for
brushes and `GE.<kind>` for GEs; class filters take short names too (`PlayerStart`, `Brush`, `LG`).

## Prefabs

A prefab is a saved group of brushes, actors and GEs around a pivot, with parameters. Placing one makes a linked
**instance**: its objects carry the instance id (`prefab` in `fields`) and their part name, and `prefab.update`
rebuilds them when the prefab or the instance's parameters change.

**Where prefabs live.** By name, in this order: the map's own `<map>.osmap/prefabs/<name>.json`, the user library
(`OSE_PREFABS`, else `%APPDATA%/OpenSCCT Editor/prefabs`), then the starter library built into the editor. A map
prefab hides a library one of the same name. `{"q":"prefabs"}` lists them all.

**Prefab space.** The pivot is the origin, usually on the floor, and +X is the front. `prefab.place` puts the pivot on
the point (`at`, `near`, `offset`), on the floor below it (`on`), or on a wall (`wall`; the front then faces out of
the wall). `pivot:"<anchor>"` places by the instance's bounds instead. `rotDeg:[0,yaw,0]` turns it; prefabs turn about
Z only when `face` or a wall sets the facing.

**Parts and links.** Each object gets the id `<instance>_<part>` (`LaserGridAlarm0_beam_1`). Actor references between
parts point at the new ids. A Tag the parts fire at each other (an `Event` or `*Event`/`Events` value that matches a
part's `Tag`) is renamed `<instance>_<tag>`, so two instances never trigger each other. Tags fired from outside keep
their names. A copy made with `duplicate` (or pasted in the app) is a plain object, not part of the instance.

**Updating.** `prefab.update` keeps each instance where it is now: if its objects were moved or turned about Z, the
instance follows its anchor part (a tilted part such as a camera works too). Parts keep their ids, layer, folder,
hidden, locked and note; new parts are added, parts the prefab no longer makes are deleted, and a second object that
still claims a part (a copy made some other way, e.g. by editing the JSON) is unlinked with a warning, not deleted.
Edits made by hand to a part's shape or properties are replaced. A mover's `BasePos`/`BaseRot` follow its place.

**Saving.** `prefab.save` takes objects (`id`/`ids`/`where`) and writes them in prefab space: the pivot defaults to the
bottom centre (of the brushes and GEs, when there are any), and `rotDeg:[0,yaw,0]` says which way the group faces now.
`to:"library"` saves to the user library, and a map that was never saved can only save there. `link:true` makes the
saved objects an instance of the new prefab (`as` names it). The file is written when the document commits (never on a
dry run); the result lists it under `files`. `ose apply` writes it only with `--save`: without it the file is listed
but not written, with a warning. Undo does not delete a written prefab file. Saving a map prefab with the
name of a library or built-in one hides that one (a warning says so): its instances in this map update from the new one.

**Starter library:**

| name | what | parameters |
|---|---|---|
| `laser_grid_alarm` | a stack of `SLaserTrigger` beams across an opening, firing their own `SAlarm`; beams tagged `<instance>_lasers` | `width`, `beams`, `bottom`, `spacing`, `alarmName`, `duration` |
| `camera_alarm` | a wall `SCamera` firing its own `SAlarm` | `height`, `tilt`, `patrol`, `range`, `delay`, `alarmName`, `duration` |
| `door_switch` | a `Mover` door in a carved doorway, toggled by an `SInterrupteur` beside it | `width`, `height`, `depth`, `opening`, `mesh`, `state`, `moveTime`, `stayOpen`, `switchHeight`, `label` |
| `ladder` | a `LADDER` GE up a wall, optional mesh | `height`, `offset`, `mesh` |
| `zipline` | a `ZL` GE from a high start down along +X | `length`, `startHeight`, `drop` |
| `vent_crawl` | a crouch vent carved into a wall at sill height, with an `LG` GE on its lip | `length`, `width`, `sill` |
| `objective_computer` | an `SComputerObjectiveTrigger` and the `SObjective` it completes | `name`, `captureTime` |
| `spawn_room` | a carved room with a row of `PlayerStart`s for one team and a doorway | `team` (0 spies, 1 mercs), `starts`, `spacing`, `length`, `width`, `height`, `door` |

**Writing a prefab by hand** (see FORMAT.md, `prefabs/`): `ops` is a change-document template run in an empty map.
`${expr}` in any string is replaced (`"${width / 2}"` becomes a number); `"if":"${expr}"` on an op drops it when false.
Expressions know the parameters, `id` (the instance id) and the map's body numbers (`standHeight`, `spyCrouchHeight`,
`mercCrouchHeight`, `bodyRadius`, `stepHeight`, `standClearance`, `ventHeight`, `crawlwayHeight`). Symbols name the
parts (`$beam#1` makes part `beam_1`).

**CLI:**

```
ose prefab list [map.osmap]
ose prefab show <name> [map.osmap]
ose prefab place <map.osmap> <name> [--at x,y,z|Id@anchor] [--wall nearest] [--on true] [--rot-deg 90] [--id Door1] [--width 160 ...]
ose prefab save <map.osmap> <name> <ids...> [--pivot x,y,z] [--yaw-deg d] [--title t] [--library] [--overwrite] [--link]
ose prefab update <map.osmap> [instance ids...] [--name prefab] [--all]
ose prefab break <map.osmap> <instance ids...> [--delete]
```

Options a command does not know are prefab parameters (`--width 160`). Edits save the map unless `--dry-run`.

## Recipes

### A two-room map with a corridor and player starts

```json
{"format":"osmap.changes/1","label":"two rooms","ops":[
 {"op":"delete","where":{"class":["Brush","Light"]}},
 {"op":"create","id":"$a","brush":{"min":[-1024,-512,0],"max":[0,512,384]},"mats":{"bottom":"Floors.Concrete"}},
 {"op":"create","id":"$hall","brush":{"size":[512,192,160]},"at":"$a@xmax_bottom","pivot":"xmin_bottom"},
 {"op":"create","id":"$b","brush":{"size":[1024,1024,384]},"at":"$hall@xmax_bottom","pivot":"xmin_bottom"},
 {"op":"move","where":{"class":"PlayerStart","props":{"TeamNumber":0}},"to":"$a@center","on":true},
 {"op":"move","where":{"class":"PlayerStart","props":{"TeamNumber":1}},"to":"$b@center","on":true},
 {"op":"create","id":"$spy2","class":"Engine.PlayerStart","near":"$a@center","dist":160,"on":true,"props":{"TeamNumber":0}},
 {"op":"create","class":"Engine.Light","at":"$a@top+0,0,-64"},
 {"op":"create","class":"Engine.Light","at":"$b@top+0,0,-64"}
]}
```

The corridor's `xmin_bottom` lands on room A's `xmax_bottom`, so their floors line up and the openings meet. Room B
attaches to the corridor the same way.

### A laser grid on this wall

```json
{"ops":[
 {"op":"create","id":"$alarm","class":"SBase.SAlarm","at":"Hall@center","props":{"AlarmName":"Hall","Duration":20}},
 {"op":"create","id":"$las","class":"SBase.SLaserTrigger","at":"Hall@ymin_bottom+0,0,24","wall":"-y",
  "count":5,"step":[0,0,32],"props":{"LaserLength":192},"layer":"Security"},
 {"op":"link","from":["$las","$las#1","$las#2","$las#3","$las#4"],"to":"$alarm"}
]}
```

Each laser's back sits on the -y wall, facing across the hall. They stack 32 apart, and every one fires the alarm.

### The same grid from the starter library

```json
{"ops":[
 {"op":"prefab.place","name":"laser_grid_alarm","id":"$grid","at":"Hall@center","wall":"-y","on":true,
  "params":{"width":192,"beams":5,"spacing":32,"alarmName":"Hall"},"layer":"Security"},
 {"op":"prefab.place","name":"camera_alarm","at":"Hall@center","wall":"+x","on":true,"params":{"alarmName":"Hall"}}
]}
```

The result binds `$grid`, `$grid.alarm` and `$grid.beam` ... `$grid.beam_4`. Later,
`{"op":"prefab.update","id":"$grid","params":{"beams":3}}` (or the instance id) rebuilds it with three beams.

### Move all cameras up 16

```json
{"ops":[{"op":"move","where":{"class":"SCamera"},"by":[0,0,16]}]}
```

### Point the cameras at the vault and put them on their own layer

```json
{"ops":[
 {"op":"rotate","where":{"class":"SCamera","near":"Vault","radius":1500},"face":"Vault"},
 {"op":"layer","name":"Security","color":"#ff4040","where":{"class":"SCamera"}}
]}
```

### A conveyor round a corner, switched by a button

```json
{"ops":[
 {"op":"conveyor","id":"$belt","points":[[-600,-300,0],{"at":[200,-300,0],"radius":128},[200,400,0]],"floor":true,
  "look":"frame","width":120,"speed":200,"items":["Penitentiary_STM.Details.Boxes_09_CHB"],"set":{"Tag":"Belt1"}},
 {"op":"link","from":"Button0","to":"$belt"}
]}
```

An escalator's treads, driven by a conveyor fitted to the placed meshes:

```json
{"ops":[
 {"op":"conveyor","id":"$tread","drive":["StaticMeshActor12","StaticMeshActor13"],"tile":64,"speed":120}
]}
```

Belts hand their items on: give a feeding conveyor `"next":"$belt"` where its end meets that belt. Map Check notes
an end that meets another conveyor with no `next` set, with a fix that sets it.

Map Check's `conveyor.*` codes cover steep or stepped paths, steps up where conveyors meet, conveyors ending at another's
fixed wall, belts that run into walls (on a baked map, in the deep check), riders carried into damage volumes, `Next`
links items cannot follow and speeds a player cannot walk against; `ose check --target original` adds
`conveyor.re.*` for what RE+'s belts cannot do.

### Check, then fix by id

```json
{"queries":[{"q":"check"},{"q":"find","author":"script","fields":["id","short","loc"],"rows":true}]}
```

```json
{"ops":[{"op":"move","id":"PlayerStart4","on":true},{"op":"link","from":"Button0","to":"Door2"}]}
```

### Can a spy reach the vault, and how dark is the way in

```json
{"queries":[
 {"q":"route","from":"PlayerStart0","to":"VaultTerminal","side":"spy"},
 {"q":"stealth","near":"VaultTerminal","radius":768,"lights":"blackout"}
]}
```

### Deep check, then apply the fixes it offers

```json
{"q":"check","deep":true,"only":["place.","zones.leak","limit.mesh-collision"]}
```

Each row's last column is a fix or null. Apply a fix's `changes` as it is, or run `ose check <map> --deep --fix`.

### A headless test round

`playtest` (or `ose play <map> --headless`) builds the map and runs a round in OpenSCCT with no window, with the server
inline and `bots` (the map's Play setting, else 2; at most 15). It lays the map out for `players` (1 to 24); by default
the map's Play setting, else the count the Match panel and the `match` query preview (the Play bots and you), so the
two extra bots do not change the layout. With `bots` given and no `players`, the game counts those bots and you. It uses the OpenSCCT the editor finds (`OSE_GAME_EXE`,
else the launcher's install, as Play does). It answers `loaded`, `loadError`, `errors`, `warnings`, the map's `starts`,
and per bot [name, side, status, at, nearestStart, distance] from the first status line (after 10 s of play), and
`player`: where the local player first spawned (`side`, `at`, `yaw`, `nearestStart`, `distance`). `at` (`camera`, a
bookmark's name or number, or `X,Y,Z[,Yaw[,Pitch]]`) spawns the player once on the floor under it (OpenSCCT's
`--start-at`; the editor finds that floor, or for a spot outside the level the floor under where it looks, and an `at`
with no floor there is an error), and `startAt` reports where it was sent with `spawnedThere` (within 32 uu across,
dropped at most 200 uu). `ose play <map> --at ...` does
the same, windowed or headless. `at` is for an offline round you play, so it is refused with `route` or `client`. It takes up to `seconds` (40) and ends once every bot has reported.

Test rounds run on default settings, signed out, and write nothing (no profile name, account key, demos or settings):
the editor passes OpenSCCT's `--svm-test-round` to every headless round, bots only or from `at` too. A windowed
`ose play` (Play, Play From Camera) is a hand play and uses the player's own settings and account.
`route` (stops: [x,y,z] floor points or object ids) has a route bot of `side` walk them in order with the bots' own
navigation and door handling (no fighting, no gadgets, no lifts), reaching an object where the game's use test lets it
use it, while you stand by on the other side; `from` starts it at that player start of its side. No other bots unless
`bots` is given, and `seconds` defaults to 30 plus 40 a stop. The report adds `route` (the game's lines),
`routeStops`, `routeReached` and `routeStuck` (where it got stuck and why, or that the run ended first).
`client:true` plays the round online instead (no bots, no route): this game hosts it (loopback only, unlisted, with
`mode`) on `side` and a second headless game joins on the other side. The report adds `peers`: per game its `role`,
`loaded`, `errors`, the last `lobby` line and the `pawns` its own world held; the client fails when its world holds a
different number of pawns than the host's, or lacks a host pawn or holds it more than 64 units from where the host
has it. `ok` is false when the round did not load, the game printed errors, the route did not reach its last stop, or
either game of the pair failed.

### Quick reads

```json
{"q":"find","class":"PlayerStart","fields":["id","loc","TeamNumber"],"rows":true}
{"q":"count","by":"class"}
{"q":"pointInfo","at":"Room3@center"}
{"q":"edges","id":"SAlarm0","depth":2}
{"q":"match","players":6}
```

### Doors that only open in big matches

```json
{"ops":[
 {"op":"match","ids":["Mover12","Mover13"],"players":[7,0],"outside":{"key":0},"inside":{"key":1}},
 {"op":"match","id":"SObjective4","players":[7,0]},
 {"op":"match","id":"SMission0","win":[1,1,2,2,3,3,3,3]}
]}
```

`{"q":"match","players":4}` then shows the small layout: the objective left out, the doors held shut and what the
mission needs.

## Other `ose` commands

These run one edit or report on a project folder and save it (unless `--dry-run`). Each edit is one undo step. They
use the same Core code as the app's tools. `ose <command> --help` gives the arguments; [CLI.md](CLI.md) lists them all.

- `ose edit`: the face, vertex, clip and placement tools (faces, material, flags, surface, extrude, move-vertex, weld,
  bevel, clip, drop, mirror, array, replace-class, measure...).
- `ose brush`: add or rebuild pieces, split, clip, flip, mirror, merge faces, hull, bake, snap, UV modes.
- `ose ge`: ledge detection and probing, flush, join, GE IDs, platforms, the activation mask.
- `ose graph`, `ose wire`: the event graph and the mission tree; link, unlink, rename a Tag.
- `ose security`: list devices with what they watch, place one on a wall, snap to walls.
- `ose mover`, `ose magic`, `ose camnet`, `ose props`, `ose propset`, `ose tips`, `ose zone`, `ose modes`: the
  gameplay workbenches (mover keys and timing, SMagicEvent timelines and their RE+ JSON, camera networks, copy/paste
  properties and property sets, zone tips, zone on-complete actions, game mode membership). Reads print the matching
  query; edits run the matching op.
- `ose match`, `ose outfit`: show the layout at a player count and set player ranges and win targets; show and set a
  team's outfit (`ose match show <map> --players 6 --mode Sabotage`, `ose outfit set <map> --team merc --model Hazmat.glb`).
- `ose probe`: solid or open space at points, and the floor below.
- `ose assets`, `ose asset`, `ose asset-import`, `ose asset-place`, `ose asset-apply`: the asset catalogue, mesh
  import, placing meshes and applying materials.
- `ose bake`: build the map with lighting settings given as flags (`--mode`, `--density`, `--samples`, `--light-size`,
  `--sun-size`, `--bounce`, `--bounce-samples`, `--atlas`, `--relight-imported`, `--fast`) and print the settings,
  lightmaps and bake numbers as JSON. `--save` keeps the settings in the map.
- `ose analyze stealth|reach|route|nav <map>`: the analysis queries with their fields as options (`--lights blackout`,
  `--from PlayerStart0 --to Vault`, `--side merc`...).
- `ose check <map> --deep [--fix]` and `ose play <map> --headless`: the deep check and a test round.
- `ose identity`: show the map's identity, or change it: `set name="Example Docks" players=6 tags=3v3,night
  when=... where=... summary=... authors=...`, `place <n> [name] --at x,y,z [--floor n] [--delete]`, `picture <slot>
  file <image> [--focus x,y] [--link]` (the image is copied into `pictures/` unless `--link`), `picture <slot> camera
  <bookmark> [--look nightvision] [--fov 80]`, `picture <slot> plan` or `none`, `text <lang> name=... description=... when=... where=... summary=...
  place.<n>=...` (merged into that translation; an empty value removes one text, `--delete` the whole language).
  Slots are `menu`, `loading-spy`, `loading-merc` (`--replace` replaces the list instead of adding), `briefing` and
  `banner`.
- `ose mapshot`: builds the map and renders its camera pictures with OpenSCCT into `.cache/pictures/`, then builds
  again so the map carries them. The game runs from a linked copy beside a background `override.cfg`, so its window
  starts minimised and never takes the focus (`--svm-background` then keeps it off-screen); a game too old for
  `--svm-mapshot` is not started, nor one whose copy cannot be made (each picture then reports why). `--dry-run` lists the shots and each one's `state` (`missing`, `stale`: rendered from an earlier version of
  the map, `current`); `--only menu,loading-spy-1` picks slots; `--force` renders current ones again; `--exe` picks
  the game; `--fast` and `--no-classes` build as `ose build` does.
- `ose identity picture <slot> file` takes PNG, JPEG, BMP and WebP files of any shape and size: the editor's core
  decodes all four (no image library, no Godot), and the build crops and scales them and stores them as PNG. A JPEG
  that already has the slot's shape (square; 16:9 for the banner) and size is kept as it is. A file the core cannot
  read is refused. The app's Identity panel also converts other formats (TGA) to PNG when they are picked.

## Notes

- `undo` and `redo` step through the history. A whole document is one step. `history` lists the steps.
- Pictures: a build never renders. It uses rendered camera pictures from `.cache/pictures/` (run `ose mapshot`, or
  Render now in the Identity panel) and warns with `identity.picture-render` about missing ones and ones rendered from
  an earlier version of the map; Map Check warns about camera pictures never rendered. The strategic map and the
  `plan` picture are drawn on the CPU on every build.
- `build` needs the map compiler, and `check` uses Map Check. When Map Check is not loaded, a short set of basic
  checks runs instead: duplicate ids, missing references, unheard events, team starts, actors in solid, and floating
  starts.
- Without the class catalog, prop values pass through unchecked, and `create` needs full class names
  (`Engine.Light`).
