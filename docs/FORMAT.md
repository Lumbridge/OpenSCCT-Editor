# The `.osmap` project format

An `.osmap` project is the map you edit. It is a folder of JSON files. The editor saves it, the `ose` command line
reads and writes it, and the compiler turns it into an SVMap folder that OpenSCCT plays: SVMap 1.2 when the map has
an identity, player-count rules, outfits or conveyors, otherwise 1.1.

SVMap is not the source format. It is compiled: it has no CSG brushes, decoration actors lose most of their
properties, and its collision, BSP and indexes are derived. The project keeps everything, so nothing is lost
between saves.

This page describes what the code writes today: `src/OpenScct.Editor.Core/Project/ProjectStore.cs`,
`Project/ModelJson.cs`, `Model/*.cs`, `Prefabs/` and `Assets/ProjectAssets.cs`. Parts that are planned are marked with
their milestone.

## Folder layout

```
ShipD.osmap/
  map.json             id, title, source, LevelInfo, map settings per mode, map info, map identity, lighting, baked world, extra
  world/brushes.json   CSG brushes, in CSG order
  world/actors.json    actors, in level order
  world/ges.json       geometric events (ledges, pipes, zip lines...)
  world/prefabs.json   the linked prefab instances placed in the map (only when there are any)
  layers.json          editor layers
  workspace.json       editor-only state: grid, camera, bookmarks, reference images, storeys, movement limits, play options
  outfits.json         what each team wears on this map (only when it sets any)
  assets/              map-local meshes and their materials (assets.json, meshes/, textures/), outfit files (outfits/)
  prefabs/             the map's own prefabs, one <name>.json each
  propsets.json        the map's own property sets (only when it has any)
  pictures/            the map's picture files (identity picture recipes name them)
  .cache/pictures/     camera pictures rendered by ose mapshot, each with the map key it was rendered from (safe to
                       delete; never copied)
  .backups/<time>/     earlier versions of the JSON files above and their .backup-files.json, one folder per save
                       (rotating; see Save backups)
  .backups/files/      one copy of each assets/ and prefabs/ file version a backup lists, named by its SHA-256
  .backup-files.json   the assets/ and prefabs/ files at the last save (see Save backups)
  .saving/             only during a save, or after a crash in one: the new files waiting to go in (see Safe writes)
  .saving.journal      the commit record of a save in progress
  .saving.lock         only while a save or a read runs: keeps a second program out (see Safe writes)
  .gitignore           written by the first save when the folder has none: leaves the four entries above out of git
```

`workspace.json` also stores optional `firstMap` guide state (`hidden`, `explored`, `checkedRevision`,
`playedRevision`, and an optional `constructionBaseline` array of brush IDs present before starting the building
lesson). Existing pieces do not count as new work in that lesson. Older workspaces default to a visible, uncompleted guide. Check and play revisions refer to map
content and play settings, excluding camera and guide state, so edits invalidate those steps. These fields never
enter the game format. Workspace mode and the completion preference are local editor settings.

`ProjectStore` writes the six JSON files, plus `world/prefabs.json` when the map has prefab instances and
`outfits.json` when it has outfits (and deletes each when there is none). Prefab files are written by `prefab.save`,
`assets/` by mesh import (`Core/Assets/MeshImporter`) and by outfit picks (`assets/outfits/`), and `pictures/` by the
Identity panel and `ose identity picture ... file`. All three are copied along when the map is saved under a new
folder or autosaved.

Why split: a version control diff stays small, and a tool (or a script) can read one part without the rest.

## General rules

- **Canonical JSON.** Files are written by OpenSCCT's `CanonicalJson.ToFileBytes`, the same writer SVMap uses:
  - UTF-8, and a newline at the end;
  - keys sorted by byte order, and keys must be ASCII;
  - a one-space indent;
  - an array or object that holds only plain values (no arrays or objects inside it) stays on one line when it fits
    in 100 columns; everything else is spread over lines.

  So the same project always gives the same bytes.
- **Safe writes.** Save first turns every file into bytes. A value that cannot be saved (a non-finite number, say)
  stops the save before any file is touched. Then a journalled swap (`Core/Project/SaveJournal`) puts them in, so a
  crash at any moment leaves either the whole old map or the whole new one, never a mix of files from both saves:
  1. every new file is written into `.saving/` and flushed to disk;
  2. `.saving.journal` (`{"format": "osmap.savejournal/1", "files": [...], "remove": [...], "backup": ".backups/<time>"}`)
     is written to a `.tmp` file and renamed into place. That rename is the commit point;
  3. each listed file is moved from `.saving/` over its target (the old one first moves into the backup folder, when
     there is one), files under `remove` are deleted, then the journal and `.saving/` go.

  Opening a project (and the next save) first finishes the job: a journal is replayed (each step is safe to repeat),
  and a `.saving/` without a journal is a save that never committed, so it is thrown away and the old files stand.

  A save, and every read of a project (the editor opening it, any `ose` command, the API host), holds
  `.saving.lock`, opened with no sharing and deleted on close. A second program waits (up to a minute) for a save in
  progress instead of reading half-moved files or taking its `.saving/` for a crashed one. The operating system closes
  the file when a program dies, so a crash never leaves the project locked. In a folder nobody may write to there is no
  lock.
- **Defaults are left out.** In brushes, actors, GEs and layers a member at its default value is not written (for
  example `rot` when the rotation is zero). Readers fill the default back in. `map.json` and `workspace.json` always
  write their members; only `lighting`, `baked` and `extra` in `map.json` are left out when empty (`lighting` when
  every setting is at its default).
- **Format markers.** `map.json` has `"format": "osmap"` and `"version": 1`. Each section file has
  `"format": "osmap.<section>/1"`, for example `osmap.actors/1`. Loading fails when `map.json` names another format.
  The section markers and `version` are written but not checked yet.
- **Save backups.** A save that replaces existing files keeps them in `.backups/<UTC yyyyMMdd-HHmmss-fff>/` (the
  journal moves them there, so it costs no copy). How many are kept is one per-user number shared by the editor, `ose`
  and the API host (`backups.json` in the user folder, below): five unless set in Preferences or with
  `ose backups --keep <n>`; 0 keeps none. Saves less than a minute apart count as one run and keep only the run's
  first and last backup, so a script's string of edits (or a burst of Ctrl+S) never pushes out older restore points.
  A backup holds the map's JSON files (map, world, layers, workspace) from before the save. `assets/` and `prefabs/`
  change outside saves (an import writes its files at once), so each save that keeps backups also writes
  `.backup-files.json`, `{"format": "osmap.backupfiles/1", "files": [{"path", "size", "time", "sha256"}]}` (time in
  UTC ticks), listing those folders as they are at that save, and first copies every listed file version the store
  lacks into `.backups/files/<sha256>`. The list is one of the save's own files, so the next save's backup gets it with
  the JSON it was saved with. A file whose size and time match the last list is not read again, and the store drops
  versions no kept backup lists. If the folders cannot be listed the save still goes on, without a list, and says so.
  `ose backups <project>` and the `backups` query list the backups, and `ose backups --restore <n>` brings one back; in
  the editor, File > Restore Backup opens one unsaved, and saving puts it back. Either way the backup's listed assets
  and prefabs are checked to be in the store before anything is written, the files it replaces become the newest
  backup (a restore keeps one even when backups are off), and each folder is rebuilt beside the project's and then
  swapped in. A backup with no list leaves the project's assets and prefabs alone.
- **Missing files are fine.** Only `map.json` is required. A missing section file loads as empty, and a missing
  `workspace.json` loads the defaults.
- **Property key order.** Inside `props`, keys are sorted on disk like everything else. The editor does not depend on
  their order.

## Units, axes and rotations

- **Units.** Unreal units: 100 units = 1 metre. Axes: X forward, Y right, Z up.
- **Vectors** are arrays `[x, y, z]`, written as float32 numbers with a decimal point (`[384.0, 0.0, 48.0]`). The
  editor works in doubles and rounds through float32 on save, as the engine stores them. Readers also accept the
  object form `{"X": .., "Y": .., "Z": ..}`.
- **Rotators** are arrays `[pitch, yaw, roll]` of integers. 65536 units make one full turn, so 16384 is 90 degrees.
  Yaw turns about Z, pitch about Y, roll about X. The axes come from UE2's `FRotationMatrix`. Readers also accept
  `{"Pitch": .., "Yaw": .., "Roll": ..}`.
- Inside `props`, values use SVMap's own forms: a vector is `{"X": .., "Y": .., "Z": ..}` and a rotator is
  `{"Pitch": .., "Yaw": .., "Roll": ..}`.

## Ids

- Every brush, actor and GE has an `id`. Ids are unique across all three lists, ignoring ASCII case.
- SVMap only accepts ids made of letters, digits and `_` that do not start with a digit, at most 63 long. The model
  does not enforce this; Map Check reports it (`id.invalid`).
- Imported objects keep their original name (`PlayerStart3`, `Brush12`). New ones get a stem plus the lowest free
  number: `Room0`, `PlayerStart1`, `Light0`. Digits at the end of the stem are dropped first.
- Ids never change on save, so they are safe to use from scripts and change documents.
- In findings and in the editor an object is named as `Kind:Id`, for example `Actor:PlayerStart3`.

## `map.json`

What `ose new DemoD.osmap --title "Demo map"` writes:

```json
{
 "format": "osmap",
 "id": "DemoD",
 "levelInfo": {"GEActivationMask": 0},
 "mapInfo": {"MapName": "DEMO MAP"},
 "mapSettings": {
  "Deathmatch": {"RespawnTime": 10, "SentriesLives": 3, "SpiesLives": 3, "TimeLimit": 10},
  "DiskHunt": {"RespawnTime": 10, "SentriesLives": 3, "SpiesLives": 3, "TimeLimit": 10},
  "MercDeathmatch": {"RespawnTime": 10, "SentriesLives": 3, "SpiesLives": 3, "TimeLimit": 10},
  "Story": {"RespawnTime": 10, "SentriesLives": 3, "SpiesLives": 3, "TimeLimit": 10}
 },
 "source": {"kind": "authored"},
 "title": "Demo map",
 "version": 1
}
```

| Key | Meaning |
|---|---|
| `id` | The map code: folder name and file name of the compiled map. SVMap allows up to 63 letters, digits or `_`. The original game needs exactly 5 letters or digits (four letters and a mode letter, such as `ShipD`). When missing, the folder name is used. |
| `title` | The map's name (menus show it; at most 48 characters). It is also `identity.name` in the compiled map. |
| `source` | How the map was made: `kind` is `authored`, `svmap`, `t3d` or `sdc`; `file` and `sha256` name the imported file. `findings` keeps the import's findings for its import report (`[{sev, code, msg, objects}]`, objects as `Kind:Id`; absent on maps imported before it was kept). An `.sdc` that RE+'s map recovery rebuilt from a compiled map (`*_Recovered*`, or under `MapsEd\Recovery`) gets `import.reRecovered` first. |
| `levelInfo` | The LevelInfo actor's stored properties (the original `Level.Actors[0]`). SVMap requires `GEActivationMask` (0 to 255); new maps store 0. |
| `mapSettings` | One property set per game mode (`Story`, `DiskHunt`, `Deathmatch`, `MercDeathmatch`, ...): `TimeLimit`, `ScoreLimit`, `SpiesLives`, `SentriesLives`, `RespawnTime`, `Handicap`, `NumDisksMax`, `GameModeId`. These are the original `SMapSettings` modes. |
| `mapInfo` | The texts of `ui/mapinfo.json`: `MapName`, `LegendMap`, the briefing texts and so on. `LegendMap` and `LegendMapInBrief` are written from the identity when it has a dossier and places. |
| `identity` | What the map says about itself for the menus. Only written when it has something. See "Map identity" below. |
| `lighting` | How the build bakes the map's light. Left out while every setting is at its default. See "Lighting settings" below. |
| `baked` | Only on maps imported from a compiled form. See "Baked world" below. |
| `extra` | Anything an importer kept that the editor does not model yet. It is copied into the compiled map. |

### Lighting settings

`map.json` `lighting` (`Model/LightingSettings`) sets how the build and the Lit Preview bake lightmaps. Any member can
be left out; a missing member keeps its default. A value out of range stops the map from loading, with the setting
named in the message. The Map panel's Lighting section edits them too.

```json
"lighting": {"atlasSize": 1024, "bounce": 0.0, "bounceSamples": 64, "density": 2.0, "lightSize": 8.0,
 "mode": "quality", "relightImported": false, "samples": 4, "sunSize": 0.5}
```

| Key | Default | Range | Meaning |
|---|---|---|---|
| `mode` | `quality` | `quality`, `faithful` | `faithful` samples as the original editor did: one lightmap per brush face sized by its lightmap scale, one shadow ray per texel centre that lets through occluders within a depth slack fitted to the stock lightmaps (it behaves like a 16-bit depth test; not decoded), its 3x3 filter (24 40 24 / 40 64 40 / 24 40 24 over 320) and per-light byte rounding. It matches the stock lightmaps closely but not exactly: the original took each surface's lights from its BSP leaves, which converted maps do not carry, and charts go into this editor's atlases. `quality` merges touching coplanar faces into seamless charts (never across a narrow gap such as a wall's foot, so no light crosses under a wall), supersamples shadow edges and occluders thinner than a texel, and gives lights a size for soft shadows. |
| `density` | `2` | 0.125 to 16 | Quality mode: texels per face lightmap-scale step (a face with `lmScale` 32 gets a 16 uu texel at density 2). |
| `samples` | `4` | 1 to 16 | Quality mode: shadow rays per texel side on shadow edges (4 = 16 rays), over the whole penumbra each edge can cast (up to 24 texels from it). |
| `lightSize` | `8` | 0 to 256 uu | Quality mode: the radius of point and spot lights for soft shadows (0 = hard). |
| `sunSize` | `0.5` | 0 to 10 degrees | Quality mode: the sun's angular size. |
| `bounce` | `0` | 0 to 2 | Quality mode: one bounce of light off lit world surfaces, times this gain and the surface texture's mean colour (0 = off, as in the original). Static meshes and unlit surfaces block bounce rays but reflect nothing. Pawn lighting never sees it. |
| `bounceSamples` | `64` | 4 to 1024 | Rays per texel for the bounce. |
| `atlasSize` | `1024` | 512, 1024, 2048, 4096 | The largest lightmap atlas side. |
| `relightImported` | `false` | | Baked worlds: relight the source map's own lightmaps with the project's lights, in their original layout (see "Baked world"). |
| `bakeDefaultLightRadius` | absent | 0 to 255 | The LightRadius a light that stores none bakes with (absent: its class default, 64). Written by the light recovery of a cooked `.sdc` import when 32 matches the cooked lightmaps better; it changes the bake only, never the game's lights. |

The API op `settings` takes the same object as `lighting` (any subset), and `ose bake` takes them as flags.

## Property values (`props`)

Actor, brush and LevelInfo properties follow SVMap 1.1's value rules (`spec/svmap/1.1/conventions.md` in OpenSCCT):

| Kind | Form |
|---|---|
| bool | `true` / `false` |
| int, byte, enum | integer |
| float | float32 number with a decimal point |
| string, name | string |
| struct | object of its members, for example `{"X": 1.0, "Y": 2.0, "Z": 3.0}` |
| actor reference | `{"$actor": "PlayerStart3"}` |
| asset reference | `{"$asset": "Package.Group.Name"}` |
| class reference | `{"$class": "SBase.SObjective"}` |
| static array | `{"$elements": {"0": v, "3": v}}` (only the set elements) |
| dynamic array | array |

Like the original map, `props` holds only what differs from the class defaults. The class catalogue
(OpenSCCT's `shared/classes.json`, or the generated metadata without an install) gives the defaults and the
schema.

## `world/actors.json`

All actors, in level order: gameplay actors (what the server simulates) and decoration (static meshes, lights,
projectors, sounds...). Decoration keeps its full properties here, even though SVMap drops most of them.

What `ose new` writes:

```json
{
 "actors": [
  {
   "class": "Engine.PlayerStart",
   "id": "PlayerStart0",
   "loc": [-384.0, 0.0, 48.0],
   "props": {"TeamNumber": 0}
  },
  {
   "class": "Engine.PlayerStart",
   "id": "PlayerStart1",
   "loc": [384.0, 0.0, 48.0],
   "props": {"TeamNumber": 1},
   "rot": [0, 32768, 0]
  },
  {
   "class": "Engine.Light",
   "id": "Light0",
   "loc": [0.0, 0.0, 448.0]
  }
 ],
 "format": "osmap.actors/1"
}
```

| Key | Default | Meaning |
|---|---|---|
| `id` | required | Unique id. |
| `class` | `Engine.Actor` | `Package.Class`, for example `SBase.SObjective`. Always written. |
| `loc` | `[0, 0, 0]` | Location. Always written. |
| `rot` | zero | Rotation `[pitch, yaw, roll]`. |
| `scale` | `1` | DrawScale. |
| `scale3d` | `[1, 1, 1]` | DrawScale3D. |
| `prePivot` | zero | PrePivot. |
| `props` | empty | Every other stored property. Tag, Event, Group and so on live here. |
| `shape` | none | The brush shape of a brush-based actor (volumes, movers, zone and anti-portal brushes), in local space. Same form as a brush `shape`. |
| `match` | none | Player-count settings, see below. |
| `legs` | none | A mover's own timing per key, see below. |
| `editor` | empty | Editor-only state, see below. |
| `src` | none | Import provenance as text (for example the original export index). Kept for round trips. |

**Actor transform (UE2):** `world = rotate((p − prePivot) × scale3d × scale) + loc`.

**Tag rule:** when `Tag` is not stored, the effective Tag is the short class name (UE2 rule). The compiler writes the
effective Tag of every actor, as SVMap requires.

Particle emitters keep their systems in the JSON inside `src`, under `particleSystems`. Each system has a `class`,
an `object` name and typed `props`. A converted sprite can also name its `texture` path and `textureOrigin` map so
the compiler can copy the right texture. The systems travel with the actor when it is copied or saved as a preset.

### Player-count settings (`match`)

Any actor can be kept to matches of some sizes, and a mission can ask for a different number of objectives at each
team size. The player count is everyone in the match at round start, bots included, spectators not. The settings
compile to SVMap 1.2 `sim/match.json` (OpenSCCT's `spec/svmap/1.2/match.md`); members at their default are left out.

```json
"match": {"players": [6, 0], "outside": {"key": 0}, "inside": {"key": 1}}
"match": {"win": [1, 1, 2, 2, 3, 3, 3, 3, 0, 0, 0, 0]}
```

| Key | Default | Meaning |
|---|---|---|
| `players` | every count | `[min, max]`, 1 to 24, 0 for no limit on that side. A minimum above the maximum still loads; Map Check and the build report it. |
| `outside` | movers: `{"key": 0}`; anything else: `"hidden"` | The state in a match outside the range: `"hidden"`, or for a mover `{"key": n}` (it is held at that key and its triggers do nothing). An objective outside its range leaves the match and its mission's count. |
| `inside` | `"normal"` | The state inside the range: `"normal"`, `"hidden"` or, for a mover, `{"key": n}` (0 to 7). The mover goes straight to that key and stays; an open mover uses its last key (where RE+'s OpenInRange ends). |
| `win` | none | A mission's objectives to win for 1v1 to 12v12 (team size is half the players, rounded up). 0 keeps the mission's own `MinimumObjectives`. |

### Mover timing per key (`legs`)

A mover's keys can each have their own leg time and easing (an OpenSCCT addition; the game's mover has one `MoveTime`
and one `MoverGlideType`). `legs[n]` is key `n`, in the form of SVMap 1.2 `sim/movers.json` (OpenSCCT's
`spec/svmap/1.2/movers.md` and `spec/additions/mover-legs.md`): `time` is the seconds of the leg between key `n - 1`
and key `n`, run either way (0.005 to 3600; key 0 has none), `smooth` whether the mover eases at that key. A member
left out uses the mover's own `MoveTime` or glide; trailing empty keys are left out. The build writes the movers it
writes to `sim/movers.json`; a mover with `legs` that is not written is dropped with a warning (`mover.legs-dropped`),
and `--target original` keeps the mover's own timing and warns (`mover.legs-not-exported`). Importing an SVMap folder
reads them back.

```json
"legs": [{"smooth": true}, {"time": 4, "smooth": false}, {"time": 0.75}]
```

**Conveyors** are actors of class `Svm.Conveyor` (OpenSCCT's native package). Their props are the class's own
(OpenSCCT `spec/svmap/1.2/conveyors.md`). A prop that is not stored has the class default; the conveyor tool stores
only what differs from it:

```json
{
 "class": "Svm.Conveyor",
 "id": "Belt",
 "loc": [-200.0, -300.0, 32.0],
 "props": {
  "Path": [{"Loc": {"X": 0.0, "Y": 0.0, "Z": 0.0}}, {"Loc": {"X": 500.0, "Y": 0.0, "Z": 0.0}, "Radius": 128.0},
           {"Loc": {"X": 500.0, "Y": 600.0, "Z": 0.0}}],
  "Speed": 200.0,
  "ItemMeshes": [{"$asset": "Penitentiary_STM.Details.Boxes_09_CHB"}]
 }
}
```

`Path` points are on the riding surface, in the actor's local space (the conveyor tool and the `conveyor` op put the
location on the first point). `Radius` rounds that corner. `Look`, `Walls` and `TriggerAction` are enum numbers.
`Walls` 4 (`auto`, the class default) is a wall on both sides, open where another conveyor's end meets this one's
side; Map Check's `conveyor.join-wall` warns where such an end meets a fixed wall instead. `ScrollTargets` lists the
mesh actors whose textures scroll with a driver conveyor:
`[{"Target": "StaticMeshActor12", "UnitsPerTile": 128.0, "Axis": 1}]` (one texture repeat per `UnitsPerTile` uu of
travel, along U for `Axis` 0 or V for 1; a negative size scrolls the other way). `Material` (`{"$asset": ...}`), when
set, scrolls only that material of the mesh (a converted RE+ tread: AeroD's travelator scrolls its tread, not its
handrails); without it every material of the mesh scrolls. `Next` is the conveyor items go on to from the end they leave by (the
path's end, or its start while the belt runs backwards), an actor reference (`{"$actor": "Belt2"}`); Map Check's
`conveyor.next` warns when that conveyor is not one the end meets, and notes an end that meets another conveyor with
no `Next` set.

## `world/brushes.json`

The CSG brushes of the level geometry (UE2 `Engine.Brush` actors). **The list order is the CSG order.** A UE2 world
starts solid, so a map begins by carving.

What `ose new` writes (the first of six faces):

```json
{
 "brushes": [
  {
   "csg": "subtract",
   "id": "Room0",
   "loc": [0.0, 0.0, 256.0],
   "shape": {
    "builder": {
     "breadth": 1024.0,
     "height": 512.0,
     "hollow": 0.0,
     "kind": "cube",
     "material": "Editor.Grid",
     "tessellated": 0.0,
     "wallThickness": 16.0,
     "width": 1024.0
    },
    "faces": [
     {
      "mat": "Editor.Grid",
      "texBase": [0.0, 0.0, 0.0],
      "texU": [1.0, 0.0, 0.0],
      "texV": [0.0, -1.0, 0.0],
      "verts": [
       [-512.0, -512.0, 256.0],
       [512.0, -512.0, 256.0],
       [512.0, 512.0, 256.0],
       [-512.0, 512.0, 256.0]
      ]
     }
    ]
   }
  }
 ],
 "format": "osmap.brushes/1"
}
```

| Key | Default | Meaning |
|---|---|---|
| `id` | required | Unique id. |
| `csg` | `subtract` | `add` fills space, `subtract` carves it. Always written. |
| `flags` | `0` | Brush-wide poly flags (semi-solid, non-solid, portal...), see the table below. |
| `loc` | `[0, 0, 0]` | Location. Always written. |
| `rot` | zero | Rotation. |
| `prePivot` | zero | PrePivot. |
| `mainScale` | `[1, 1, 1]` | UE2 MainScale, applied before rotation. |
| `postScale` | `[1, 1, 1]` | UE2 PostScale, applied after rotation. |
| `shape` | required | `builder` (optional) and `faces`. Always written. |
| `props` | empty | Other stored Brush actor properties (Group, Tag...), kept for round trips. |
| `editor` | empty | Editor-only state. |
| `src` | none | Import provenance. |

**Brush transform (UE2):** `world = rotate((p − prePivot) × mainScale) × postScale + loc`. When the two scales
together mirror the brush (an odd number of negative axes), the corner order is reversed so faces still point out.
Texture axes take the inverse transpose of the linear part (`U_world = Linear^-T U_local`), so the stored mapping
alone would stretch the texture with the brush when its scale changes. The editor's scale edits (scale tool, typed
values, Inspector, `scale` op) rewrite it so its world form stays put (`U' = Linear'^T U_world`,
`textureBase' = toLocal'(textureBase_world)`), which keeps the texture's size unless Surface > Scale Keeps Texture
Size is off or the op says `"keepTextures": false`. A face whose plane turns (a slope, or a wall of a rotated brush
under PostScale) also turns its world mapping by the smallest turn onto its new plane, about the face's centre, so
its texels keep their size on the face. A scale that changes sign mirrors the texture with the brush. A builder brush
resized through its parameters keeps each face's surface the same way when the face count does not change.

The .sdc and T3D writers fold the pans into the texture origin (UE2's FPoly has no pans). UE2 folds each pan along
its own axis (`base -= U panU / |U|^2`), which is exact only while U and V are square to each other; the writers
solve for both together when they are not (`OpenScct.Editor.Core.Geometry.TextureOps.FoldPans`), and a T3D poly
whose axes are not square is written with its pans already folded into `Origin` and no `Pan` line.

### Faces

Each face is one convex, flat polygon in the brush's local space (UE2 `FPoly`).

| Key | Default | Meaning |
|---|---|---|
| `verts` | required | Corners. Their order makes the face normal point out of the brush. The normal is found with Newell's method. Always written. |
| `mat` | none | Material path `Package.Group.Name`. None means the default material. New faces get `Editor.Grid`. |
| `flags` | `0` | Per-face poly flags. |
| `texBase`, `texU`, `texV` | zero | Texture mapping (UE2 Base, TextureU, TextureV): `u = dot(p − texBase, texU) + panU`, and the same for v. The three are written together, only when `texU` or `texV` is set. New faces get the stock planar mapping, one texel per unit. |
| `pan` | `[0, 0]` | `[panU, panV]`, integers. |
| `lmScale` | `32` | Lightmap texel scale (the PC one, UE2 LightMapScaleXBOX). |
| `lmScalePsx2` | none | The PS2 lightmap scale, only when it differs from `lmScale` (imported from an `.sdc`, kept for round trips). |
| `item` | none | The original poly group name (ItemName), kept for round trips. |
| `link` | none | The original poly link (iLink), kept for round trips. |

### Builder specs

`shape.builder` records how a parametric piece was made: a `kind` plus every parameter, defaults filled in (flags are
`0` or `1`), for example the cube above. The kinds:

- UE2 builders: `cube`, `cylinder`, `cone`, `sheet`, `linearStair`, `curvedStair`, `spiralStair`, `tetrahedron`,
  `volumetric`.
- Blockout pieces: `room`, `corridor`, `vent`, `crawlway`, `doorway`, `window`, `stairs`, `ramp`, `platform`, `ledge`.

`ose shapes <kind>` lists a kind's parameters, defaults and ranges. Changing a number rebuilds the faces, so stairs stay
stairs across saves. An edit that changes the polygons (clip, vertex edits) drops the spec. Imported and hand-made
shapes have no builder.

A piece made of several brushes (stairs, a shelled room) also stores `piece`, the id of its first brush, and `part`,
the brush's index in the piece. When the first brush is deleted or renamed, the other parts take the id of their new
first part (the lowest `part`), in the same undo step.

### Poly flags

The UE2 `EPolyFlags` bits the editor uses by name. Brush `flags` and face `flags` use the same bits.

| Bit | Name |
|---|---|
| `0x00000001` | Invisible |
| `0x00000002` | Masked |
| `0x00000004` | Translucent |
| `0x00000008` | NotSolid |
| `0x00000010` | Environment |
| `0x00000020` | Semisolid |
| `0x00000040` | Modulated |
| `0x00000080` | FakeBackdrop |
| `0x00000100` | TwoSided |
| `0x00000800` | NoSmooth |
| `0x00100000` | SpecialLit |
| `0x00400000` | Unlit |
| `0x02000000` | AntiPortal |
| `0x04000000` | Portal |
| `0x08000000` | Mirrored |

Other bits are kept as they are.

## `world/ges.json`

Geometric events: the lines a player can grab. An example with one ledge:

```json
{
 "format": "osmap.ges/1",
 "ges": [
  {
   "id": "LedgeGrab0",
   "kind": "LG",
   "points": [
    [0.0, 0.0, 128.0],
    [256.0, 0.0, 128.0]
   ]
  }
 ]
}
```

| Key | Default | Meaning |
|---|---|---|
| `id` | required | Unique id. |
| `kind` | `LG` | `LG` ledge grab, `ZL` zip line, `PIPE`, `LADDER`, `HOH` hand over hand, `POLE`, `FENCE`. Always written. |
| `points` | required | Two points, or three for `LADDER` and `PIPE`. Always written. |
| `mask` | `0` | The GE ID bit: 0 is always on, else one of 1, 2, 4 ... 128, matched against LevelInfo `GEActivationMask`. |
| `platform` | `0` | 0 all platforms, 1 PS2 only, 2 Xbox only. |
| `flags` | `0` | The original flag word, kept for round trips (0x2 is the stock editor's "added in the editor" bit). |
| `appearance` | absent | Optional generated ladder, pipe, pole, cable, overhead bar or fence geometry: `width`, `radius`, `spacing` (units) and optional `material` asset path. Absent means GE only. |
| `editor` | empty | Editor-only state. |
| `src` | none | Import provenance. |

The appearance belongs to the GE, so cloning, transforms, point edits, deletion and undo keep the line and its
geometry together. Width is ladder width or fence height; spacing is rung or post separation. Width is 4–512,
radius 0.5–32, spacing 4–256, with at most 256 repeated rungs/posts. Each kind has suitable defaults when the
author enables appearance. Ledge grabs use existing level geometry and do not generate an appearance.
Climbable vertical poles use `PIPE`; the stock `POLE` kind is preserved as a marker and has no grab event.
The compiler and original-game exporters generate ordinary brush geometry from these settings; the saved project
contains no duplicate generated brushes and the runtime traversal format is unchanged. Original-game exports
retain the resulting shapes but do not retain their procedural editing relationship. Fences default to the stock
climbable `drpoc_chantier.SM.moa_grillage` surface material; replacement materials must also be climbable.
When writing back an imported `.sdc`, its existing compiled world is preserved: generated brushes trigger the
normal `sdc.worldNotRebuilt` warning and need a rebuild in the stock editor before they appear in the original game.

## Editor-only state (`editor`)

Brushes, actors and GEs can carry an `editor` object. It is never compiled into the game map.

| Key | Default | Meaning |
|---|---|---|
| `layer` | `""` (the default layer) | Layer name. |
| `folder` | `""` (top level) | Outliner folder path, `/`-separated. |
| `hidden` | `false` | Hidden in the viewports. Kept across builds and saves. Hidden objects still compile. |
| `locked` | `false` | Cannot be selected or moved in the viewports. |
| `prefab` | none | The prefab instance this object belongs to (an id in `world/prefabs.json`). |
| `prefabPart` | none | Its part name inside the prefab, so updating the instance keeps the object's id. |
| `moverKey` | none | A mover drawn at this key while its keys are placed in the views (moving it writes that key). Needed because two keys can share a pose. |
| `note` | none | A free note. |
| `author`, `op` | none | Provenance: who last made or changed the object through a change document, and which op did it (`label#index`). |

## `layers.json`

An example with the default layer and a locked baked-world layer:

```json
{
 "format": "osmap.layers/1",
 "layers": [
  {"name": "Default"},
  {"color": "#E0A030", "locked": true, "name": "Baked world"}
 ]
}
```

| Key | Default | Meaning |
|---|---|---|
| `name` | required | Layer name. Always written. |
| `visible` | `true` | Written only when `false`. |
| `locked` | `false` | Everything on the layer is locked. |
| `color` | none | `#RRGGBB`. |

## `workspace.json`

Editor-only state that travels with the map. Every member is always written. What `ose new` writes:

```json
{
 "bookmarks": [],
 "camera": [
  [-512.0, -512.0, 512.0],
  [-4096, 8192, 0]
 ],
 "grid": 16.0,
 "movement": {
  "mercCrouchHeight": 136.0,
  "mercRunSpeed": 400.0,
  "radius": 35.0,
  "spyCrouchHeight": 100.0,
  "spyRunSpeed": 440.0,
  "standHeight": 175.0,
  "stepHeight": 34.0,
  "walkableSlope": 45.57
 },
 "format": "osmap.workspace/2",
 "play": {"bots": 0, "difficulty": "normal", "mode": "Story", "players": 0, "rules": "playlevel", "side": "spy"},
 "references": [],
 "rotationGrid": 1024,
 "shownStorey": null,
 "storeys": []
}
```

| Key | Default | Meaning |
|---|---|---|
| `format` | `osmap.workspace/2` | The workspace's version. A file without it was saved before version 2 (see `play`). |
| `grid` | `16` | Grid size in units. |
| `rotationGrid` | `1024` | Rotation snap in rotator units (1024 = 5.625 degrees). |
| `camera` | `[[-512, -512, 512], [-4096, 8192, 0]]` | Perspective camera `[location, rotation]`. |
| `bookmarks` | `[]` | Saved views: `name`, `loc`, `rot`, optional `note`. |
| `references` | `[]` | Reference images: `name` (unique; from the file name when missing), `path`, `view` (`top`, `front`, `side`), `origin`, `unitsPerPixel` (1), `opacity` (0.5). |
| `storeys` | `[]` | The map's own storeys for the storey filter: `name`, `bottom`, `top` (units). Empty: found from the floors. |
| `shownStorey` | `null` | The storey the views show, as a height in units: its floor, or its bottom when it has no floor (null: all). When the map opens, the views show the storey that holds this height. |
| `movement` | see above | Movement numbers the design checks use. |
| `play` | see above | Default Play options. |

`movement`: `walkableSlope` is in degrees, the rest in units or units per second. The body numbers (radius 35,
stand 175, crouch spy 100 and merc 136) come from OpenSCCT's `spec/movement`; `stepHeight` 34 is the highest step a
player walks up and `walkableSlope` 45.57 degrees is a floor normal Z of 0.7. Map Check and new blockout pieces take
their clearances from these numbers: each clearance keeps its margin over the body (stand clearance 192 = stand 175 +
17, spy crouch gap 104 = 100 + 4, merc crouch gap 144 = 136 + 8, narrowest door 72 = radius 35 x 2 + 2, vent 125 and
crawlway 105 over the spy gap), so a taller or wider body moves them all together.

`play`: `side` is `spy` or `merc`; `mode` is a game mode (`Story`...); `bots` is the bot count; `rules` is
`playlevel` (the stock editor's Play Level rules) or `map` (the map's own settings); `players` (1 to 24) is the player
count a playtest lays the map out for: Play passes it to OpenSCCT as `--players=N`. `0` (the default) counts the
playtest's own players, the bots and you, which is also what OpenSCCT does without `--players`. The default used
to be 4: a workspace without `format` whose `players` is 4 opens with 0, and the editor log, `ose info` and `ose
build` say so; saving writes the new form. Any other stored count is kept. `difficulty` is how well the bots play (`easy`, `normal`, `hard`,
`expert`: OpenSCCT's bot skill presets, passed to the game as `--difficulty=`).

`references`: `path` is absolute or relative to the project folder; the image is centred on `origin` (the world point
under its centre) and drawn behind the map in its view. In the Top view, `origin`'s height says which storey the image
belongs to: with the storey filter on, only the images whose height lies in the storey shown are drawn.

`storeys`: when empty, the editor finds the storeys from the map's floors, by RE+ Map Design's rules: every walkable
surface (the floor of a carved brush, the top of an added one, and the floors of an imported map's baked collision,
normal Z 0.7 or more) is binned by height with its area; floors within 64 units are one, floors with under 6% of the
largest floor's area are left out, and at most 24 are kept. Each storey runs from 16 units under its floor to 16 units
under the next floor (the highest is open above). `ose storeys <project>` and the `storeys` query list them.

## `outfits.json`

What each team wears on this map, format `osmap.outfits/1`. It has the members of SVMap 1.2 `render/outfits.json`
(OpenSCCT's `spec/svmap/1.2/outfits.md`), with file paths relative to the project folder. `ose import` and every
save copy files that are still outside the project (an SVMap import, files picked before the map had a folder) into
`assets/outfits/`, so a saved project does not depend on the map it came from.

```json
{
 "format": "osmap.outfits/1",
 "merc": {
  "cosmetics": "map",
  "model": {"glb": "assets/outfits/HazmatMerc/HazmatMerc.glb", "goggles": {"bone": "B Head", "offset": [-0.1, -2.8, -0.8]}}
 },
 "spy": {
  "cosmetics": "map",
  "slots": {"body": {"texture": "assets/outfits/SpyBody.png"}, "head": {"texture": "assets/outfits/SpyHead.png"}}
 }
}
```

| Key | Meaning |
|---|---|
| `spy`, `merc` | A team's outfit; a team without one wears its stock look. |
| `slots.body`, `slots.head` | `texture`: a PNG for that skin of the stock model. `source` keeps the original texture's name after an import. |
| `model` | A replacement character: `glb` (a skinned glTF) or `stock` (a shared mesh, `Package.Mesh`, such as `SPerso.ATT_01`). It plays the team's own animations by bone name, so it needs every bone they drive; other bones are free. `goggles` places the goggle glow: `bone` and `offset`. |
| `cosmetics` | Whose look wins on the body: `map` (the outfit, the default), `player` (the player's own cosmetics; texture outfits only) or `none` (no cosmetics). |

Picked files are copied into `assets/outfits/`: a texture as it is, a model into its own folder with the files its
glb names beside it. A different file already there under the same name is never replaced: the copy takes the next
free name (`body-2.png`, a model folder `HazmatMerc-2/`), and the same file picked again reuses its copy. A team with
its own model and slot textures gets a warning (`outfit.slots`): the textures only show where the model draws the
stock materials.

## Editor files outside the project

The editor keeps some files of its own in its user folder (`%APPDATA%/OpenSCCT Editor` on Windows; an automated run,
headless or in the background, uses a folder of its own, see ARCHITECTURE.md), never in a map:

- `settings.cfg`: Preferences (autosave, field of view, grid, colours, UI scale) and the user's own shortcut
  bindings (`[shortcuts]`, `action id = "Ctrl+Shift+K"`; an empty value takes a shortcut away).
- `backups.json`: `{"format": "osmap.backups/1", "keep": 5}`, how many save backups each save keeps. Outside
  `settings.cfg` because `ose` and the API host read it too.
- `autosave/<MapId>.auto<n>.osmap/`: rotating autosave copies, each a whole project folder with an `autosave.json`
  (`{"format": "osmap.autosave/1", "id", "title", "saved", "project"}`) naming the project it was taken of, so a
  recovered copy saves back there. File > Open Latest Autosave matches copies by that project folder (a Save As copy
  keeps the map id), and only offers one newer than the project's last save.
- `sessions/editor-<pid>-<start ticks>.json`: one marker per running editor (`{"format": "osmap.session/1", "pid",
  "started", "title", "autosave"}`), deleted on a normal exit. `autosave` names the copy that holds the open map's
  unsaved work; saving the map or opening another clears it. A marker whose process is gone (or whose id now belongs
  to a program started at another time) means that editor crashed: the next start offers its autosave back, unless the
  project has been saved since, and names any other crashed editor's autosave in the log.

## `world/prefabs.json`

The linked prefab instances, written only when the map has any:

```json
{
 "format": "osmap.instances/1",
 "instances": [
  {"anchor": ["alarm", [0.0, 0.0, 200.0], [0, 0, 0]], "hash": "24028430efd9", "id": "LaserGridAlarm0",
   "loc": [-200.0, 0.0, 0.0], "params": {"beams": 4}, "prefab": "laser_grid_alarm", "rot": [0, 16384, 0]}
 ]
}
```

| Key | Default | Meaning |
|---|---|---|
| `id` | required | Instance id, unique among the instances (ignoring case). Its objects store it in `editor.prefab`. |
| `prefab` | required | The prefab's name (looked up in `prefabs/`, the user library, then the built-in library). |
| `loc`, `rot` | zero | Where the prefab's pivot was put, and its turn. |
| `params` | `{}` | The parameter values given at placement; the rest take the prefab's defaults. |
| `hash` | none | A short hash of the prefab as placed: a different hash means the prefab changed since. |
| `anchor` | none | `[part, loc, rot]`: one part's place in prefab space. Updating finds the instance again from that part if its objects were moved. |

## `prefabs/`

One file per prefab, `<name>.json` (names: letters, digits, `_` and `-`). The same format is used in the user library
folder (`OSE_PREFABS`, else `%APPDATA%/OpenSCCT Editor/prefabs`) and by the starter library built into the editor
(`src/OpenScct.Editor.Core/Prefabs/Library/`).

```json
{
 "format": "osmap.prefab/1",
 "name": "camera_alarm",
 "title": "Security camera on an alarm",
 "category": "security",
 "summary": "A wall camera that sets off its own alarm ...",
 "params": {
  "height": {"default": 256, "min": 64, "max": 2048, "summary": "Height of the camera on the wall"},
  "alarmName": {"default": "Camera"}
 },
 "ops": [
  {"op": "create", "id": "$camera", "class": "SCamera", "at": [8, 0, "${height}"], "props": {"Event": "Alarm"}},
  {"op": "create", "id": "$alarm", "class": "SAlarm", "at": [8, 0, "${height + 64}"],
   "props": {"Tag": "Alarm", "AlarmName": "${alarmName}"}}
 ]
}
```

| Key | Default | Meaning |
|---|---|---|
| `format` | `osmap.prefab/1` | |
| `name` | the file name | The prefab's name. |
| `title`, `summary`, `category` | none | Shown in lists. The summary should say where the pivot is. |
| `params` | `{}` | Parameters: a default value (number, text or `true`/`false`, which also gives the type), or an object with `default`, `min`, `max` (numbers), `options` (allowed texts) and `summary`. `id` is reserved. |
| `objects` | none | Saved objects: `{"brushes":[..],"actors":[..],"ges":[..]}` in the `world/*.json` object format, in prefab space. Their ids are the part names. |
| `ops` | none | A change-document template (`osmap.changes/1` ops) run in an empty map after `objects` are added. The symbols it binds name the parts (`$beam#1` makes part `beam_1`); objects without a symbol keep their id as part name. |

A prefab needs `objects`, `ops` or both. **Prefab space:** the pivot is the origin, usually on the floor, and +X is the
front (it faces out of a wall the prefab is put against).

**Substitution.** Any string in `objects` or `ops` may hold `${expr}`. A string that is one whole `${expr}` becomes
the value (`"${width / 2}"` is a number); inside longer text the value is written as text (`"Terminal: ${name}"`). An
op with `"if": "${expr}"` is left out when the expression is false (`false`, `0` or `""`). Expressions have numbers,
`'text'`, `true`/`false`, `+ - * / %` (`+` joins text), `== != < <= > >=`, `&& || !`, parentheses and `min max abs
floor ceil round sqrt clamp`. Names: the parameters, `id` (the instance id), and the map's body numbers from
`movement` (`standHeight`, `spyCrouchHeight`, `mercCrouchHeight`, `bodyRadius`, `stepHeight`, `standClearance`,
`ventHeight`, `crawlwayHeight`). Template ops cannot place other prefabs.

## Property sets (`propsets.json`)

Named presets of property values for one class (the stock editor's "Use Properties Set", such as `SCamera.blinde`),
applied to actors of that class or a subclass. A map keeps its own in `<map>.osmap/propsets.json` (it travels with the
map, and Save As copies it to the new folder; a map set hides a library set of the same name; saving a set is not an
undo step); the user library is `OSE_PROPSETS`, else
`%APPDATA%/OpenSCCT Editor/propsets.json`. The stock sets come from an original install's `System/PropertiesSets.ini`
(`ose propset import-ini`, Gameplay > Property Sets): they are game data and are never shipped with the editor.

```json
{
 "format": "osmap.propsets/1",
 "sets": [
  {"class": "SBase.SCamera", "name": "blinde", "note": "from PropertiesSets.ini",
   "props": {"EMFIntensity": 0.7, "StaticMesh": {"$asset": "SGameplayObjects.SGPO_SM.camera_blindee"}, "bCanBeChaffed": false}}
 ]
}
```

| Key | Meaning |
|---|---|
| `class` | Full class name. The set's name is `ShortClass.name`. |
| `name` | The set's own name. |
| `note` | Optional: where it came from. |
| `props` | Values in the `props` format above, applied as they are. |

## Copied properties (`osmap.props/1`)

Edit > Copy Properties puts an actor's stored values on the system clipboard as one line of JSON, so they paste into
another map or another editor: `{"format":"osmap.props/1","class":"Engine.Mover","from":"Mover315","props":{...}}`.
`ose props copy --out <file>` writes the same document, and `ose props paste --from-file` or the `props.paste` op's
`props` field reads it. A paste
takes the ticked values a target's class has; identity, placement and links to other actors (`Tag`, `Event`,
`BasePos`, `KeyPos`, `NextCam`, any actor reference...) start unticked.

## SMagicEvent timelines (`scct.smagic-event`)

Gameplay > Event Sequencer > Copy Timeline JSON and `ose magic export` write an SMagicEvent's groups in the RE+
exchange format (RE+ `tests/SMagicEventJson.md`), so timelines move between RE+ and this editor:
`{"format":"scct.smagic-event","version":1,"class":"SBase.SMagicEvent","properties":{...},"context":{...}}`. Every
leaf of `properties` is a string: `Groups` holds `EventGroup` entries (`Delay`, `Event`, `Type` and `ValidOn` by
EEventType name), `Repeat`, `Sequence` and `SequenceIndex`; `StopActor` is an actor id or `None`. `context` maps each
gameplay actor's id to its Tag, for reference only. Paste and `ose magic import` read `properties` back (numbers and
booleans may also be plain JSON values). In the map the groups are the class's own `Groups` property.

## Zone on-complete relays

A mission or objective fires its `Event` when complete. To fire several actors with delays, the editor gives it an
SMagicEvent relay of its own (Tag `<mission>_Done`), so the map stays stock classes. The editor changes only a relay
the mission alone fires that has one plain group (not a sequence, not switched off, no `StopActor`); anything else the
mission fires is kept as the new relay's first action, with the type the mission used. An actor already reached on
completion is not added again, since a second firing would toggle it back.

## Baked world

A map imported from a compiled form (a converted SVMap or a cooked `.sdc`) has no brushes for its world geometry.
Its BSP comes in as a locked "baked world" layer instead. In `map.json`:

```json
"baked": {"keepCollision": true, "keepRender": true, "sourceMap": "C:/.../svmap/SteeE"}
```

| Key | Default | Meaning |
|---|---|---|
| `sourceMap` | required | The SVMap folder the geometry came from, absolute or relative to the project. |
| `keepRender` | `true` | Use that map's `world.glb` BSP meshes in the compiled output. |
| `keepCollision` | `true` | Use that map's `collision.bin` world colliders and zones in the compiled output. |
| `brushes` | `[]` | The ids of the brushes an `.sdc` editor copy brought in with the layer, or that the import recovered from a cooked copy's BSP (below), so Map Check can tell one that was deleted. |

With only one kept, the other layer is built from every brush, the baked ones included (an `.sdc` editor copy's, or the
brushes recovered from a cooked copy), as
with both off: keep the render and the collision and zones are rebuilt; keep the collision and the world drawn is
rebuilt. The kept render keeps its `lighting.json`, which names zones by the source map's numbers (fog, ambient, the
sky zone, the zone of each light and fog volume), so the rebuilt collision numbers its spaces by the source's zones
too: each space takes the number of the source zone that holds most of its volume, and the zones are the source's.
A zone actor that no longer stands in its source zone, or that the source has no zone for, gets a `zones` warning. A map whose baked world holds no brushes (an SVMap import, or a cooked `.sdc` imported with brush recovery off) has nothing to build the other layer from, so it
gets no world collision or no world drawn; the build and Map Check (`brush.baked-one-layer`) warn, and Map Settings
offers such a map only both or neither. Map Settings (Baked world) and the `settings` op (`baked`) set the
two options.

The baked geometry is drawn and compiled through unchanged. Volumes get their brush shapes back from the collision file
on import (from an `.sdc`, their exact brush models instead). With both keep options off, the compile builds the world
from every brush (world solid outside, zones from the zone actors), as for an authored map; the source map then only
supplies the materials and static meshes of its own packages that no library map ships, and the collision sets of its
static meshes.

### Brushes and the baked world (`.sdc` import)

An `.sdc` import reads the file natively as well (`Interop/Sdc`, SDC-FORMAT.md) and lays it over the converted map:

- An editor copy (`Packages\MapsEd`) brings its CSG brushes, in CSG order, each with its exact polygons, materials,
  poly flags, texture mapping, both lightmap scales, item names and links. They are marked as built into the baked world:
  `src` is `{"sdc":{"export":N,"hash":H},"baked":true}`, where `H` is a hash of the brush as imported. A kept layer
  does not build them (they are already in it); a layer that is not kept builds them with the rest. Map Check warns
  (`brush.baked-edited`) on a brush whose hash no longer matches, since a kept layer does not show that edit. A copy of
  a baked brush (duplicate, paste, array, storey duplicate, a prefab saved from it and its placed copies) is a new
  brush: it loses the mark and is built like any added brush, and the editor says so (a subtract copy cannot carve a
  kept layer, which it warns about). A baked brush cut and pasted back into its map (its id is in `baked.brushes` and
  the map no longer has it) is moved, not copied: it keeps the mark and its id, like a baked brush dragged in place, so
  it is not built a second time over the kept layer, and Map Check reports it as edited. The baked world still holds a deleted baked brush too:
  `baked.brushes` lists them, and Map Check (`brush.baked-deleted`) and the `delete` warn. A cooked copy has no brushes, except the few cooked copies
  that keep the brush exports the cook took out of Level.Actors (RED1, 3.6 Auto1): those are read the same way.
- A cooked copy gets its brushes back from its compiled BSP (`Interop/Sdc/Recovery`, SDC-FORMAT.md section 8;
  `ose import --brushes off|cells|merge`, default `merge`; the import dialog's Recover brushes choice; the script's
  `open` with `brushes`; `ose sdc-brushes <project> --save` again later), unless the brushes the file kept rebuild its
  BSP themselves (then those are the map's). They are ordinary brushes in CSG order ahead of any others (a cooked
  copy's own kept brushes come after them), in the layer `Recovered brushes` (structural brushes in the layer itself,
  folders `Sheets` for non-solid sheets and `Zone portals`), with materials, poly flags, texture mapping and lightmap
  scales from the surfaces they came from; a face part between solid and empty space that no surface covered is
  Invisible, as the original drew nothing there. Like an editor copy's they are built into the baked world: `src` is
  `{"recovered":{"mode":"merge"|"cells"},"sdc":{"hash":H},"baked":true}` and `baked.brushes` lists their ids, so the
  kept layers still play the map as the original, and Map Settings > Baked world builds it from them instead. Ids are
  `RecoveredBrush<n>`, `RecoveredCell<n>` (cells mode), `RecoveredSheet<n>` and `RecoveredPortal<n>`, numbered the same
  for the same file. `extra.sdcBrushRecovery` keeps the run's report: a one-line `summary` (shown in Map Settings under
  Baked world), the counts of each step (`cells`, `joined`, `grown`, `absorbed`, `splitBack`, `brushes`, `sheets`,
  `portals`, the cells left out as `unboundedCells`, `degenerateCells` and `sliversDropped`), the probe check
  (`probes`, `firstMisses`, `misses`, `missPoints`), the `thin` brushes and the `unsurfaced` ones (with the largest
  part's centre, `at`); no timings, so importing the same file again writes the same project. A recovery that fails,
  or `--brushes off`, leaves the map with the baked world only (and any brushes the file kept) and says so in the
  report. Recovered brushes are not the original's own: the export lint does not excuse their shape findings as the
  imported map's, and an editor `.sdc` still cannot be written from a cooked import (no lightmap records): T3D or
  RE+'s folder can.
- Volumes, movers and anti-portals get their exact shapes from their brush models.
- Every actor gets every property the file stores (the converted map carries only a few for render actors), typed by the
  class data. The build language's text from the conversion is kept for localized properties. A value the class data
  cannot type is kept as `{"$raw": {"type", "hex"}}` (its tag type and bytes); the compile and Map Check skip it.
  Without the editor's class data, the builder's classes step makes it first and the actors are read with that.
- The objects an actor owns through an export-object property (an Emitter's particle emitters, `Emitters`) come in
  with every stored property as the Begin Object blocks the stock T3D export writes inside the actor, kept in `src` as
  a T3D import keeps them (`t3dObjects`), so the T3D export writes them back and the original editor makes the
  emitters again.
- Actors the cook drops (static lights and other actors with a light type, already in the baked lighting) and PS2-only
  actors come in with `"cookedOut": true` in `src`, in the folder `Render/Cooked out`; the compile leaves them out as the
  cook does. The cook-dropped lights are written nowhere, but the bake still lights with them, as the original
  editor did: a relight of the source lightmaps (`relightImported`), the brushes and meshes added to the map, and a
  rebuild without the baked render get the source map's full lighting in their lightmaps, while the game, as with the
  cooked map, has only the lights the cook kept (PS2-only lights stay out). A cooked copy still stores the dropped
  lights something refers to, outside Level.Actors: they come in with their properties like any other light. Render
  actors the conversion keeps only in its render files (cloth, projectors...) come in under `Render/Other`. Cameras
  and deleted actors are dropped, as the stock editor's T3D export drops them. References to actors are
  `{"$actor"}`, render actors included; a reference to a deleted actor (some cooked copies keep deleted lights in
  Level.Actors) is None, as in the editor copy.
- A cooked copy has lost most of its static lights (only the ones something refers to are still in the file). The
  import brings them back (`Interop/Sdc/Lights`, `ose import --lights off|exact|full`, default `full`; `ose
  sdc-lights` and the `lights.recover` op run it again), best source first:
  1. the editor copy whose lightmap atlases are the cooked copy's byte for byte (found by their hashes among the
     `MapsEd` folders and autosaves of every install near the file's, plus `--twins` and `OSE_TWIN_FOLDERS`; the
     hashes are cached in `twins.json` in the editor's data folder; `*_Recovered*` maps are never used): its lights
     the cook drops, exactly. Two game modes can share one bake (MuseD and MuseE): the copy with the cooked package's
     name wins, else the one holding most of the cooked map's objects by name;
  2. the lights the file keeps outside Level.Actors (above; the conversion keeps most of them as render lights, which
     stay as they are and are only labelled);
  3. in `full` mode only, an editor copy of the same map from another bake, when the cooked lightmaps hold at least
     half of its lights' predicted light: its lights whose light the bake holds (support 0.3 or more) come back,
     those under 0.3 are left out, and those the lightmaps cannot check (too few texels) come back listed for review;
  4. in `full` mode only, stand-in point lights fitted to the cooked lightmaps with the editor's faithful bake (same
     light formula and shadows as a build), when the fit gate passes: the map's remaining lights rebake at least 2
     bytes from the atlases and at least 1% of the samples hold unexplained light above the noise floor (else what
     is left is model error and a fit places noise lights). The fit stops at a noise floor of 10 bytes (raised to the
     map's measured model error when that is higher), after 300 lights at twice that, or after 60 failed seeds; it
     has no time limit unless one is given, so the same file gives the same lights on any machine.

  Each comes in cooked out (it bakes, it is never written; a kept-in-file light keeps what the import made it) with
  `src.recovered` = `{"source": "twin" | "orphan" | "staleTwin" | "fitted", "confidence": 0..1, "support": share of
  its predicted light the bake holds (absent when the lightmaps cannot tell), "bspLit": the editor copy's lightmaps
  list it, "peak": a stand-in's brightest channel in bytes, "review": true when it should be checked}`, in the folder
  `Render/Recovered lights/<Editor copy | Kept in file | Older editor copy | Fitted stand-ins>`. Stand-ins have only
  LightBrightness, LightHue, LightSaturation and LightRadius and the group `RecoveredLights`. `extra.sdcLightRecovery`
  keeps the run's report: a one-line `summary` (shown in the Map panel), the editor copy used, counts per source, the
  faithful rebake's error against the cooked atlases before and after (`before`/`after`: mean absolute error and
  bias in bytes over the covered texels), the fit's noise floor and gate (`noiseFloor`, `measuredNoise`,
  `aboveFloor`, `fitGated`), the default light radius trial, and `relightSafe` with `unsafeReason`. A relight is
  safe only when the exact editor copy gave the lights back and the cooked lightmaps confirm every one of them;
  otherwise `unsafeReason` says why (recovery off, no editor copy, stand-ins, lights to review) and the cooked
  lightmaps should be kept. The recovery sets `lighting.bakeDefaultLightRadius` when 32 matches better than 64. If
  the recovery fails, the import still succeeds with the cooked lightmaps and warns (`import.sdcLightsFailed`).
- RE+ actors of classes the map defines whose settings the conversion makes native SVMap data (player-count rules
  `SMatchSizeRules` and its older names, outfits `SCharacterOutfits` and its older names, the Play Level
  `ReloadedBotController`, conveyor belts `ConveyorBelt` with `ConveyorMotor`, `ConveyorItem` and `ConveyorRoller`,
  and AeroD's tread script `AerTreadScroll`) come in with every property, `"lifted": true` in `src`, in the folder
  `Gameplay/RE+ lifted`. The project's match rules, outfits and converted conveyors come from the conversion and
  build; the compile leaves the lifted actors out, as the conversion does, Map Check does not ask for their classes,
  and the T3D export writes them, so the original editor gets them back. Only actors of the map's own classes
  (`<Map>.ConveyorBelt`, not a class of another package) are lifted. A converted conveyor (`Svm.Conveyor`) names the
  belts it stands for in `src.liftedFrom` (AeroD's tread script is never listed: it stays a lifted actor of its own,
  and its tread meshes stay static meshes). Converted conveyors and lifted belts fall into groups, one RE+ chain each
  with the conveyors it hands items on to (`liftedFrom`, `Next` and `NextBelt`), and `extra.sdcLifted.conveyors`
  keeps each group as imported: `[{"belts": [...], "hash": H}]`, H a hash of its conveyors. The T3D export writes a
  group that is as imported as its belts, as imported. A group with a conveyor edited, added or deleted, a belt
  deleted or added, or joined to another group is written as new RE+ belts lowered from its conveyors, linked by
  `NextBelt` into one chain across `Next`, and its imported belts are left out (`roundtrip.convertedConveyors` says
  which and why); a group whose conveyors were all deleted is left out. Copying a carried conveyor copies its belts.
  `extra.sdcLifted` also keeps a hash of the outfits and match rules as imported: when they are edited after import,
  the T3D export rewrites the lifted actors from them (`roundtrip.liftedData` says so, and reports what RE+'s actors
  cannot hold).
- The classes the map defines itself are kept in `extra.mapClasses` (`{"<Name>": {"parent", "script"}}`, the source
  from the class's `ScriptText`, SDC-FORMAT.md section 12), so the export for the original editor can write them.
- A package name with `-` (`new-island`) is imported under the map id with `_` (`new_island`), and its own objects are
  referred to under the map id (`new_island.Model3296`), as the compile writes them; an editor copy in a
  download laid out like an install (`<download>\Packages\MapsEd`) is converted with the rest of the download, so its
  title, map info and map settings come from the download's `<Map>-i.utc` and texts.

Actors and GEs on top of it are editable, but a build does not yet carry every edit into the render and audio files:

- Gameplay actors, static meshes, volumes and GEs: built from the project. A rebuild keeps the source map's colliders
  and mesh instances for actors that did not change, and rebuilds the ones that did (a moved actor, a reshaped volume,
  a swapped mesh). Deleted actors lose theirs.
- Lights (`render/lighting.json`): deleted lights are dropped and added lights are written. The source map's lightmaps
  are kept as they are, unless `lighting.relightImported` is on: then they are baked again with the project's lights in
  their original layout (`Lightmaps/ConvertedWorld` reads the charts back from the source's lightmap UVs). An atlas
  the original squeezed to 256 (CompressLightmaps: each 2x2 block summed, times 0.25, truncated) is baked at 512 and
  squeezed the same way. A converted map keeps only its in-game lights (SteeE: 138 of the editor copy's 364), not the
  ones that only lit its lightmaps, so a relight with only those comes out darker than the original: the build warns
  (`lighting.source-lights`) until the project bakes with a light the source map did not have (recovered lights do
  not count). An `.sdc` import has those lights (an editor copy holds them; a cooked copy gets them back, above), so
  it relights with the original's lights. A cooked import whose recovery says a relight will not match warns with
  its reason instead (`lighting.recovery-unsafe`); stand-ins in a project without the report warn as
  `lighting.recovered-standins`. An editor-copy import also keeps the original's choice of lights per lightmap: each
  lightmap takes only the lights the editor copy lists for it, so light does not reach a surface's border texels from
  the room behind a wall. A light edited since the import (moved, reshaped, or switched to baking) or added takes, if
  the copy's leaf runs list it (run-time lights only), the lights those runs give its surface; otherwise the build
  floods it through the leaves of the map's world rebuilt from the project's brushes, as for an authored map. The build counts this under `lightmap.leafLists.*`; an import from
  a converted SVMap folder has no such lists and lights every surface with every light in reach. A cooked copy stores
  no per-lightmap lists and has no brushes to rebuild its world from, so the static lights its recovery brings back
  are held to no list: they light every surface in reach, through walls too (the build's `lighting.leaf-lists` note
  says so). Its run-time lights still take the lists worked out from its leaf runs.
- Brushes added on top of a baked world are lit and shadowed by the source map's BSP and meshes too. Their lightmaps
  go in atlases of their own (`<MapId>_added_lightmap<N>.png`, materials `<key>__lmadded<N>`), added to the source's
  `lightmaps` list, and they draw from a `BSP_<MapId>_Added` node. Only the project's lights reach them, so the same
  warning applies. Placed static meshes keep the source map's baked vertex light.
- Emitters (`render/fx.json`): deleted emitters are dropped. Moving or editing an imported emitter is not compiled
  yet.
- Sounds (`audio/audio.json`): deleted sounds are dropped, moved ones move and added ones are written. Other edits to
  an imported sound are not compiled yet.

Import sets `bCollideActors` to `false` on static meshes the source map gives no collider (decals and the like), so a
rebuild does not give them one.

## Map identity

The `identity` section of `map.json` (all fields optional; `Model/ProjectIdentity.cs`, `Project/IdentityJson.cs`):

```json
"identity": {
 "family": "Example Docks", "description": "...",
 "dossier": {"when": "14 June 2009, 11:20 PM", "where": "Lisbon, a harbour warehouse", "summary": "..."},
 "authors": ["Map Author"], "version": "1.0.0", "language": "fr", "players": {"recommended": 6}, "tags": ["3v3", "night"],
 "places": [{"number": 1, "name": "Loading bay", "at": [1200, -340, 64], "floor": 0}, {"number": 2, "name": "Crane"}],
 "pictures": {
  "menu": {"from": "camera", "bookmark": "Overview", "look": "plain", "fov": 80},
  "loading": {"spy": [{"from": "camera", "bookmark": "Spy entry", "look": "nightvision", "fov": 80}],
              "merc": [{"from": "file", "file": "pictures/merc.jpg", "focus": [0.5, 0.4]}]},
  "briefing": {"from": "plan"}
 },
 "texts": {"fr": {"name": "Docks d'exemple", "dossier": {"summary": "..."}, "places": {"1": "Quai de chargement"}}},
 "floors": [-10000, 320]
}
```

| Key | Meaning |
|---|---|
| `family`, `variant` | The name without its variant word, and the word (`Aquarius`, `Enhanced`): lists sort by the family and dim the variant. |
| `description` | One or two sentences for map browsers (300 characters); the dossier summary when absent. |
| `dossier` | When and where the map is set, and one line about it. `LegendMap` is written from it as the stock maps write it: `when|where||summary`; a line break in the summary is written as `|`, and an imported legend's later lines come in as line breaks, so it is written back as it was. |
| `authors`, `version` | Credits, in order, and the author's version. |
| `language` | The language of the map's texts; `en` when absent. |
| `players.recommended` | The lobby size the map plays best at (1 to 24). The range a map supports is gameplay (match-size rules), never identity. |
| `tags` | Lowercase facts (`3v3`, `1v1`, `competitive`, `casual`, `small`, `large`, or any other). A `3v3` tag or exactly six recommended players puts the map in OpenSCCT's 3v3 group. Playlists are never part of a map. |
| `places` | The numbered areas of the briefing plan and the strategic map. `at` is where the number is drawn; a place without `at` (imported from a legend) is only in the text. `floor` picks the strategic map floor; absent, the floor of `at`'s height (a floor the built map does not have is dropped with a warning). `LegendMapInBrief` is written from them: `1. Name||2. Name`. Import takes places only from a legend made of nothing but numbered items; any other legend stays as stored. |
| `pictures` | A recipe per slot: `menu`, `loading.spy` and `loading.merc` (up to 8 each; the game picks one per load), `briefing`, `banner`. Every slot is square but the banner, which is 16:9. `file`: a PNG, JPEG, BMP or WebP image in the project's `pictures/` (or a full path, kept until the project's first save and with `ose identity ... --link`), cropped to the slot's shape around `focus` (0..1 across and down), kept at most 2048 pixels and 4 MiB and stored as PNG (a PNG that passes 4 MiB is halved until it fits, with a warning); a JPEG that already has the slot's shape and fits the limits is kept as it is. The editor's core decodes the four formats itself (`Identity/Codecs`): baseline and progressive JPEG (not CMYK, arithmetic or 12-bit; the EXIF orientation is not applied), BMP of every header and depth, lossy and lossless WebP with alpha (not animated). `camera`: a workspace bookmark rendered by OpenSCCT's own renderer with a `look` (`plain`, `nightvision`, `thermal`) and a horizontal `fov` (10 to 120). `plan`: the generated plan of the map (square, so not for the banner). |
| `texts` | Translations of the texts above, per BCP 47 language code other than `language`: `name`, `description`, `dossier` (`when`, `where`, `summary`) and `places` (by number), each with the same limit as the map's own text. Anything missing falls back to the map's own text. Each is built into `ui/text/<lang>.json`, which OpenSCCT reads for the game content's language: `en`, `fr`, `de`, `es` or `it`. Map Check warns about a translation into the map's own language, a translated place the map does not have and a text over its limit (`identity.translation`), and about a language OpenSCCT does not show (`identity.translation-language`). |
| `floors` | The strategic map's floor heights, rising. Absent: found from the compiled collision (a map whose `levelInfo` already has `MapFloors`, as imported maps have, keeps its own). `[]`: no strategic map; an imported map without `MapFloors` gets `[]`, so its build has none, as the original has none. |

Camera pictures are rendered by `ose mapshot` (or **Render now** in the Identity panel): the editor builds the map and
starts OpenSCCT with `--position -32000,-32000 -- --svm-background --svm-maps=<build> --svm-mapshot=<request.json>`,
which renders each bookmark (1024 x 1024, the banner 1920 x 1080) and quits (OpenSCCT `docs/MAPS.md` "Map pictures").
It starts the game from a copy in its work folder: the exe, the console exe, the `.pck` and the `data_*` folder hard
linked (copied where links cannot be made) beside an `override.cfg` that starts the window minimised and never
focused, since Godot reads that file before any of the game's code runs and an exported game cannot be pointed at
another one (given `OpenSCCT.console.exe`, the copy is still named after `OpenSCCT.exe`). `--svm-background` then moves
the window off-screen; the game finds its content through `--svm-content` (the launcher's, else the `.content` of the
OpenSCCT checkout the exe sits in, else, without `SVM_CONTENT`, the exe's own folder when `shared/` is beside it) and
its built-in maps through `--svm-maps` (the `maps/` beside the exe). On another drive than the temp folder the files
are copied, about 210 MB per render. A game whose script assembly has no
`--svm-mapshot` (0.8.84 and older) is not started, and the game is not started at all when its background copy cannot be
made. A game that fails, takes more than 3 minutes or cannot be copied gives each picture an error. The pictures go to `.cache/pictures/<key>.png`, where the key is made
of the recipe, the bookmark (at the float precision the project stores) and the size, and `<key>.map` holds the key of
the built map the render was made from: the hash of its `sim/` and `render/` files, `sim/classes.json` included (it
decides which devices the game draws), so pictures rendered from a build without it are stale. A build finds the pictures without a GPU:

| Render | Build | Map Check |
|---|---|---|
| made from this version of the map | used | nothing |
| made from an earlier version (the map changed since) | used, with an `identity.picture-render` warning | nothing (only a build knows the map's key) |
| none | left out with an `identity.picture-render` warning; a menu picture falls back to the plan | `identity.picture-render` warning |

The strategic map and the plan are drawn on the CPU from the compiled collision (`Compile/StrategicMapBuild.cs`), so
every build makes them. Walkable surfaces (normal Z at least 0.7) reachable from the player starts frame the picture;
floors start where at least 200 units of height separate two large walkable areas, `FloorZ` sitting 32 under each.
The camera looks straight down (the stock rows, +X across and +Y down the picture) with a narrow 15 degree field of
view, so every floor is drawn at nearly the same scale; OpenSCCT projects through it exactly. Each floor picture is
1024 pixels in the stock palette: lower floors darker, this floor light, walls where they cross chest height. The plan
stacks the floors from the bottom up, each a shade lighter than the one under it and drawn with its own walls, so an
upper storey covers what lies beneath it; when the briefing slot is the plan, `briefingProjection` is `snapshot` and
the game draws the place numbers on it (numbers that land on the same spot, as places on different floors can, stack
downwards).

Import keeps a map's pictures and identity: a converted map's `ui/identity.json`, then the shared catalog's name (the
mixed-case name menus show, split into family and variant word, for a map with no name of its own) and pictures for
its map code (with a downloaded map's `catalog/entry.json`), and the dossier and place names from its legends. The
pictures are named by full path until the project's first save (`ose import`, or the app's first Save), which copies
them into `pictures/`, so the project does not depend on where the content was installed. When the catalog's name
replaces the title the map came with, `extra.catalogTitle` keeps both (`{"name", "imported"}`): until the map is
renamed, the T3D export writes the imported title into the LevelInfo, so the original editor gets the map's own title
back. `extra.originalPictures` keeps a short content hash (SHA-256, first 8 bytes) of each picture the map came with:
Map Check and the build do not ask such a picture to be bigger or another shape (the stock catalogue's 256 x 256
pictures, shown so by the original game); a picture replaced since is checked like any other. A map imported without a
strategic map (no `MapFloors`) gets `floors: []`.

## Map-local assets (`assets/`)

`assets/assets.json` (format `osmap.assets/1`) lists the map's own assets, in the `MyLevel` package as UE2 names a
map's own objects. Paths in it are relative to `assets/`.

| Section | Entry |
|---|---|
| `meshes` | `MyLevel.<Group>.<Name>`: `file` (`meshes/<Group>_<Name>.glb`), `mesh` (the render mesh index), `collisionMesh` (the collision mesh index, absent when the mesh collides per polygon), `collision` (`parts`, `hull`, `box` or `mesh`), `hulls`, `triangles`, `collisionTriangles`, `bounds` (Unreal units), `materials`, `source` |
| `materials` | `MyLevel.<MeshName>.<Material>`: `flags` as in a converted map's `materials.json` (`texture`, `masked`, `translucent`, `two_sided`, `unlit`), `uSize`, `vSize` |
| `textures`, `sounds` | reserved: `file` per path |

Each mesh glb holds a `libraryMesh` node for the render mesh (one primitive per material, glTF metres, Y up) and a
`collision` node of convex hulls. Material and mesh paths resolve as the compiler resolves them
(`Core/Assets/AssetResolver`): the project's own assets first, then the converted maps in name order. The views
also draw gameplay actors' class meshes that no converted map carries (cameras, laser triggers, sensors, mines) from
OpenSCCT's gameplay pack (`shared/render/gameplay`), as the game does, and skeletal meshes (animated props, fish)
from `shared/chars` or a converted map's `render/chars`, in their reference pose. A build takes no render mesh from
either; a device's collision comes from the pack's triangles, the per-polygon set the converter writes for stock devices.

## How a project compiles to SVMap 1.2

The compiler (`Core/Compile`, `ose build`, the app's Build) writes a complete SVMap folder named after the map id.
SVMap itself is described in OpenSCCT's `spec/svmap/1.1/` and `spec/svmap/1.2/`. Every compiled map is SVMap 1.2:
every build writes the map's identity (`ui/identity.json`, a 1.2 file, with at least the name), and the spec asks one
`formatVersion` across `manifest.json`, `sim/gameplay.json` and `sim/traversal.json`, so all three say `1.2.0`
(`ose validate` reports a folder whose files differ). The identity adds `ui/identity.json`, `ui/pictures/` and `ui/text/`.
Player-count settings, mover timing per key and outfits add `sim/match.json`, `sim/movers.json` and
`render/outfits.json`, and conveyors are `Svm.Conveyor` actors in `sim/gameplay.json`. OpenSCCT's
`spec/svmap/1.2/schemas` holds JSON schemas for `sim/match.json`, `sim/movers.json`, `render/outfits.json` and the
conveyor props, and its content validator checks compiled maps against them. The other files are written by the 1.1
rules, which 1.2 keeps. What it writes:

| Project data | SVMap output |
|---|---|
| `map.json` `id`, `title` | `manifest.json` `map` and `title`, `formatVersion` `1.2.0` (as in `sim/gameplay.json` and `sim/traversal.json`), `requires` (the shared class table, unless `--no-classes`). `source` is `{"kind": "authored", "generator": "openscct-editor", "generatorVersion"}`; the schema needs all three. |
| `levelInfo` | `sim/gameplay.json` `levelInfo` (class LevelInfo, `GEActivationMask` always written). |
| `mapSettings` | `sim/gameplay.json` `mapSettings`. |
| `mapInfo` | `ui/mapinfo.json`, with `LegendMap` and `LegendMapInBrief` from the identity's dossier and places when it has them. |
| `title`, `identity` | `ui/identity.json` (`svm.identity/1`: the name is the title, else the map code; a title past 48 characters is cut, with a warning) and `ui/pictures/` (`menu`, `loading-spy-<n>`, `loading-merc-<n>`, `briefing`, `banner`; PNG (RGB when opaque, filtered rows), or a JPEG of the slot's shape kept as it is) and `ui/text/<lang>.json` per translation (`svm.text/1`: `identity` with the translated name, description, dossier and place names, and `mapinfo` with the `LegendMap` and `LegendMapInBrief` written from them, the untranslated lines filled from the map's own). A baked map's own identity and pictures are replaced; of its translations, the `identity` section is the project's and the rest (other `mapinfo` strings, `mapinfo.modes`, `actors`) is kept. |
| Gameplay actors | `sim/gameplay.json` `actors`: `id`, `class`, `props` with the transform, the effective Tag, `Level` and `Region`. Level order is kept. No `src` is written. |
| Emitters (`Engine.Emitter` and friends) | Stay gameplay actors. `render/fx.json` is written empty for now (planned: emitters, projectors, rain). |
| Decoration actors (StaticMeshActor, Light, Spotlight, SunLight, AmbientSound, Projector, DynamicProjector, AntiPortalActor, ERainVolume, soft-body patches and strip doors, SFogVolume) | Static meshes: `render/world.glb` instances (lit ones through a `#vc` variant with baked vertex light), their colliders in `sim/collision.bin` with no actor id: the mesh's per-polygon and simple collision sets as a converted map stores them (the baked source map, else the library map that ships the mesh), each with the traces that use it (`CSRC` traceUse), else one per-polygon collider of the render triangles. A gameplay object (`SGamePlayObject`) also gets the colliders of its `TriggeredMesh` and of each `DamagedMeshes` stage that keeps collision, starting disabled, with `CSRC` variants naming the stages that use each mesh (as the converter writes them). Lights: `render/lighting.json`, the lightmaps and the vertex light. Sounds: `audio/audio.json`. Projectors, rain, soft bodies, fog volumes and anti-portal actors are not written yet. |
| Actors with `bDeleteMe`, or PS2-only `Platform` | Not written. |
| Editor-only actors (`Engine.Camera`, `Engine.Brush`) | Not written. |
| Actor `shape` (volumes, movers) | `sim/collision.bin` brush colliders, with a hull when the shape is convex. |
| Brushes | CSG in list order, then a solid-leaf BSP and zones. A point's state is that of the last CSG plane crossed, as the engine reads it. A face parallel to a node's plane with every corner within 0.25 of it (UE2's coplanar band) is put on that node, so two brushes a hair apart leave no solid sheet between them; a leaf the brushes say is the other state gets one more CSG node, on a plane it lies wholly behind (solid) or in front of (empty); in `sim/collision.bin` it shares the triangle range of the nearest ancestor node that has triangles, since the runtime counts a node as CSG only when it has some. Output: lightmapped `role: "bsp"` meshes in `render/world.glb` (see "Lighting" below), with `render/materials.json` and `render/textures/`; triangles, BSP nodes and zones in `sim/collision.bin`. Non-solid and portal faces do not collide; semi-solid brushes cut no other brush, but their faces go into the BSP as UE2 builds them (with the semisolid material flag), so level traces see them too; portals split zones. |
| GEs | `sim/traversal.json` `ges`, with `src` `{"kind": "authored", "node": <id>}`. |
| Derived indexes | `tagIndex`, `eventEdges` and `missionTree`, computed with OpenSCCT's `GameplayDoc.WithDerived`. |
| Stale engine state in `props` (`Region`: zone, zone number, leaf) | Recomputed from the new BSP, or cleared. |
| `baked` | The source map's BSP meshes, world collision and zones, copied through. See "Baked world" for the actors. |
| Actor `match` | `sim/match.json`: a rule per gameplay actor with a range or state, win targets per mission. A range on a light, static mesh or other render-only actor (or a subclass) is an error (they are baked into the lighting for every count), so is one on a conveyor (its belt is world geometry and carries at every count; gate what switches it), and so is a minimum above the maximum; Map Check also reports a key past the mover's last. One on an actor that is not written is dropped with a warning. |
| Actor `legs` | `sim/movers.json` (`svm.movers/1`): each written mover with its own timing, in level order. |
| `outfits.json` | `render/outfits.json`, with slot textures copied to `render/textures/outfit_<team>_<slot>_<file>` and a model with its files to `render/chars/<map id>/<team>/`. A missing file or a model without the bones the team's animations drive is an error. A baked source map's own `outfits.json` and the outfit files it names are replaced. |
| `extra` | Copied through. |
| Layers, `editor`, `workspace.json` | Never written. |
| Sound actors, zones and portal distances | `audio/audio.json` (`svm.mapaudio/1`). Streams and zone reverb are planned. |
| Strategic map | `levelInfo` `MapFloors` (`FloorZ`) and `SnapshotCamera`, and `ui/strategicmap/floor<n>.png`, drawn from the collision (see "Map identity"). A map whose `levelInfo` has `MapFloors` keeps them and its pictures. |
| Conveyors (`Svm.Conveyor`) | A gameplay actor in `sim/gameplay.json`. The built look (`Core/Gameplay/ConveyorGeometry`): the static part as a library mesh `<MapId>.Conveyor.<id>_Frame` with an instance (actor `<id>.Frame`), the moving belt, steps and drums as instances with `gameplay` true (actors `<id>.Belt`, `<id>.Steps`, `<id>.Rollers`, class `Conveyor`), all vertex-lit, the steps at belt phase 0 (the game moves them); item meshes as library meshes. Its collision is one world collider with no actor id; the belt, drums and escalator slope are `SURFACE_Conveyor` unless their material names a surface type. Scroll targets' mesh instances get `gameplay` true. This is SVMap 1.2 data (OpenSCCT `spec/svmap/1.2/conveyors.md`), so a map with a conveyor is SVMap 1.2; a 1.1 reader draws and collides with the look but nothing moves. |
| Everything | `manifest.json` file digests and group hashes. No timestamps, so the same project gives the same bytes. |

`sim/classes.json` holds the class defaults of every class the map's gameplay actors use (with their super chains), as
OpenSCCT's converter writes it for every converted map. Every build writes it (`ose build --no-classes` leaves it out).
OpenSCCT reads it twice: its renderer takes an actor's `DrawType` and `StaticMesh` from it when the actor stores none,
so without it the gameplay objects that draw their class's mesh (cameras and their wall mounts, laser triggers,
laptops, bomb targets, disks, window panes, extinguishers, first-aid boxes) are invisible in the game, though they
still work; and its map loader applies every class default in it that differs from the game's generated defaults to
the actors it creates, before their own props, so the table also sets gameplay values an actor does not store, as on
a converted map. Without OpenSCCT's `shared/classes.json` the build writes none, with a `classes.missing` warning when
an actor would draw its class's mesh from it (or, without any class data, when the map has gameplay actors), else a
note.

### Lighting

The build bakes light as the original editor did (`Core/Lightmaps`, `Compile/WorldLighting`), into the files converted maps carry, so OpenSCCT draws an authored map
exactly as it draws a converted one:

- **World surfaces** form a `role: "bsp"` node `BSP_<MapId>` in `render/world.glb`, one primitive per material and
  atlas, with the lightmap UV on TEXCOORD_1. The sky zone's surfaces form a second node, `BSP_<MapId>_Sky`, with
  `"sky": true`. A lit surface's glTF material is `<key>__lm<N>`: it names its atlas in `extras.scct_lightmap` (and as
  its occlusion texture on TEXCOORD_1), and `materials.json` has an entry under the same name (the game binds a
  lightmap only through that entry) plus a row in `lightmapped`. Unlit surfaces (PF_Unlit or an unlit material) have
  no TEXCOORD_1 and draw at their texture's colour.
- **Atlases** are `render/textures/<MapId>_lightmap<N>.png`, listed in `lighting.json` `lightmaps`. RGBA8, the
  stored units of the original (byte = half the light: the game shows twice the atlas). A texel is the zone's ambient
  (half of FGetHSV, truncated to bytes) plus, per baked light, `vis x Colour x f`.
- **Baked lights**: `LightType` not 0, `bApplyToStaticLighting`, not `bDynamicLight`, not an STriggerLight (switched
  at run time), not LE_ESpotShadow. Colour is `FGetHSV(hue, saturation) x LightBrightness / 255 x LevelInfo.Brightness`,
  times the light type's value at time 0 (flicker: its mean). Lights and surfaces must agree on `bSpecialLit`.
  `bAffectOwnZoneOnly` lights light their zone only.
- **Shadows** are ray traced against the world's visible surfaces and every placed static mesh with `bShadowCast`
  (the original's shadow pass drew those). Every surface blocks light from both sides (the original's shadow pass
  did not cull back faces), masked materials only where their texture is solid (alpha of half or more). FakeBackdrop and sky
  zone surfaces let sunlight through.
- **Placed static meshes** that are not gameplay objects get baked vertex light: each instance names a
  `<mesh>#vc<hash>` library node (the mesh with its own COLOR_0, `variant` `vc<hash>`), `vertexLit` is `true`, and
  `materials.json` `vertexLitMeshes` lists `{base, name}`. One shadow ray per vertex from 4 uu off the surface, only
  for actors with `bStaticLighting`, times the actor's ScaleGlow; no ambient (the game adds the zone's own).
- **Zones** in `lighting.json` carry `ambient` = the zone's `m_RefAmbientColor` (FGetHSV of AmbientHue,
  AmbientSaturation, AmbientBrightness, truncated), which lights pawns and static meshes at run time.
- `lighting.json` `bake` records how the lightmaps were made (the settings, `fast`, the generator, charts, texels and
  rays). The game does not read it.

`map.json` `lighting` chooses the sampling (see "Lighting settings"). `--fast` bakes at a quarter of the density (a
sixteenth of the texels) without shadows, for a quick look in the game. `ose bake` builds with settings given on the
command line and reports the bake. Quality mode packs charts into atlases of up to `atlasSize` (several when one is
full), with a texel of border around each chart; charts sit on their plane's own texel grid, and a surface wider than
an atlas is cut into tiles whose new corners are mended into the surfaces next to them. Faithful mode keeps the
original's per-face layout (sizes rounded to 8, at most 256 a side).

A light leaves the bake with `bApplyToStaticLighting` off or `LightType` 0. Hiding it in the editor does not change
the build (UE2 dropped lights hidden in its editor, so decluttering the view changed the map). Lights placed in this
editor (the viewport, the class browser, the `create` op) that bake also start with `bApplyToInGameLighting`
on, so they light players as they light the room; the author can turn it off.

The shadow masks of each build are kept in the editor's cache folder (`bake/` under `OSE_CACHE`, by default
`%LOCALAPPDATA%/OpenSCCT-Editor/cache/bake`,
one file per project folder), keyed by each light's shape, the occluders its rays can meet and each lightmap's
geometry. A rebuild after a colour or brightness change traces no rays; moving a light or a brush retraces only the
lightmaps the old and new places touch (a sun retraces only the lightmaps whose rays toward it pass the edit). The
stats `lightmap.shadowJobs` and `lightmap.cachedJobs` show how much came from the cache. Deleting the folder only costs
a full bake.

After writing, the build loads the map back with OpenSCCT's strict loader (`SvMap.Load` with `VerifySimHash`,
`StrictCollision` and `CheckReferences`) and reports what it rejects (`--no-validate` skips this). `ose play` then starts OpenSCCT with
`--svm-maps=<the folder holding the map folder> --svm-play=<id>` and the play options, `--players=<n>` among them.

## Export to the original game

The project exports straight to an original map package (`ose export --format sdc`, the app's File > Export SDC
with a Layout choice in the save dialog, the `export` API method), written by the editor itself with no stock
editor:

- `--layout editor` (the default) writes the editor copy for `Packages\MapsEd`: every brush with its Model and Polys
  in CSG order, the actors, volumes, movers and GEs, and the world. `--layout cooked` writes the copy the game loads
  from `Packages\Maps`, without what the cook leaves out (SDC-FORMAT.md section 13).
- A map imported from an `.sdc` is written back over that file's tables: what the project does not edit (the baked
  world, StaticMeshInstances, map textures and meshes, script classes, emitters) is copied unchanged, and an unedited
  map writes back the same file. Objects keep their source names; one the edit adds is written under a free name
  when the source already holds that name for something the import leaves out (`sdc.renamed`). A copied or new
  brush or volume gets its own model. Its baked world is not rebuilt: brushes added or edited since the import are
  written as brushes, but reach the world BSP, collision and lightmaps only after a rebuild in the stock editor
  (`sdc.worldNotRebuilt` lists them), and lights and meshes moved, added, edited or deleted keep the old lightmaps
  and baked vertex light until then (`sdc.lightingNotRebuilt`; a new mesh has no lighting instance at all,
  `sdc.meshNotLit`). A deleted light leaves the world's light lists. Localized text the import replaced with the
  build language's is written back as the file stored it while it is unedited (the `storedText` key of the actor's
  source).
- A map imported from a cooked copy (`Packages\Maps`) writes only a cooked copy: the cook dropped the CSG brushes
  and the lightmap records an editor copy needs (`sdc.refused`).
- A map made in the editor gets a world built by the compiler: BSP, zones and lightmaps baked in faithful mode
  (SDC-FORMAT.md section 16), and a LevelSummary with its title and author. Faces with the editor's grid placeholder
  are written with no material (`sdc.untextured`).
- The written file is read back with the native reader and checked: the layout, every brush and actor by name, the
  GEs, properties the class data types, and, for a write-back, what differs from the source by kind.
- `--game <install>` is the install that new materials, meshes and sounds are looked up in (default: the install the
  source `.sdc` sits in, else `SVM_GAME_ROOT`). A reference that is not found is written as None with a warning.
- The package is named after its file, and the game finds the map by that name.
- Player-count rules, outfits, the `<Map>-i.utc` pictures and settings and the `.int` texts are not written.

The project also exports to T3D (`ose export`, the app's File > Export T3D), which the stock or RE+ editor imports (`File > Import`), builds
and saves as `.sdc`:

- Brushes become `Begin Brush` blocks in CSG order: their polygons in brush space and their transform (Location,
  Rotation, PrePivot, MainScale, PostScale) as properties. The editor copies show that SCCT's CSG applies the whole
  brush transform, rotation included (SDC-FORMAT.md section 6). Each polygon header also carries `LightMapScale=` and
  the editor's own `LightMapScalePsx2=` (an `.sdc`'s second lightmap scale); the stock polygon header reader takes only Item,
  Texture, Flags and Link and skips them, and the editor's T3D import reads them back.
- Actors keep their full properties.
- Floats are written as UE2 writes them (six decimals), with more decimals where six would not read back as the same
  single-precision value, so a round trip keeps every stored vertex, texture axis and property value exactly.
- GEs go in the T3D's `Begin GE` block as `GE ADD` lines, the form the stock editor reads. The stock `GE ADD` reads
  only the kind and the points, so GE IDs are lost there (the native `.sdc` export keeps them). Each line also carries the editor's
  own `MASK=`, `PLATFORM=` and `FLAGS=` (when not 0), which the stock command skips and the editor's T3D import reads
  back.
- `--only Kind:Id,...` writes just those objects, in the Edit > Copy form (no LevelInfo, no builder brush).
- A conveyor becomes a chain of RE+ 2.8.0 `ConveyorBelt` actors (one converted from RE+ belts and unchanged since
  the .sdc import is written as those belts instead, see the .sdc import above) (`<id>_Belt<n>`) linked by
  `NextBelt`, a loop linking back to its first: one belt per straight run, arcs as chords at most 4 uu off the curve,
  and RE+'s square corner piece at a level right-angle turn whose runs are longer than the width. The last belt of an
  open chain leads on (`NextBelt`) to the first belt written for the conveyor its `Next` names, so RE+ runs them as
  one chain, with the first belt's item settings and at most 64 belts (`conveyor.re.next`). A driver conveyor exports as hidden belts
  without rollers. The map needs RE+'s conveyor classes before the import (in RE+, Tools > Add Conveyor Belt once,
  then delete that belt). `conveyor.re.*` warnings list what is lost: other looks, switching by events, starting
  stopped, scroll targets, carried objects, sharp corners RE+ cannot turn, auto walls (RE+ walls are per belt, so a
  join opens that side of the whole belt), and RE+'s limits (64 belts, 128 items, 8 item meshes).
- The RE+ actors an `.sdc` import kept for the map's outfits and player-count rules (`SCharacterOutfits` and its
  older names, `SMatchSizeRules` and its older names) are written as imported while that data is unchanged, and
  rewritten from it when it was edited (within RE+'s caps: 64 rows, 16 players, 8 team sizes, 16 win missions).
  `roundtrip.matchRows` names each actor whose setting RE+ cannot hold and what the original game does instead (a range
  that starts above 16 players, a gated actor that is not an objective, mission or mover, win targets past 8v8). An
  outfit texture the editor set goes in `MyLevel.CharacterSkins` named by team, slot and content (`SpyBody_<hash>`).
  A plain T3D carries neither the map's own assets nor RE+ actors a map lacks (`roundtrip.mapAssetNotCarried`,
  `roundtrip.outfits`, `roundtrip.matchRules`): the export for the original editor does.
- The `<Map>-i.utc` pictures and settings need the automated route (M8).

**The export for the original editor** (`ose export <map> <folder> --format original`, File > Export for the Original
Editor, the `export` method) writes a folder for RE+ instead of a single file:

| File | What it is |
|---|---|
| `<Map>.t3d` | The map, builder brush and LevelInfo first: for File > Import into a new map. |
| `<Map>-MapClasses.t3d` | The actors of classes the map defines itself (and conveyors), objects only: for `MAP IMPORTADD` once the classes exist. A map with outfits or player counts but no RE+ actor for them (an authored map) gets `SCharacterOutfits0` / `SMatchSizeRules0` here, made from its data. |
| `Classes\<Name>.uc` | The sources of those classes an editor copy keeps (TextBuffer `ScriptText`). |
| `Assets\Textures\`, `Assets\Meshes\`, `Assets\Sounds\`, `ImportAssets.txt` | The map's own textures (DDS: sides a power of two up to 2048, DXT1 when opaque, DXT5 with alpha imported MASKED or ALPHATEXTURE, the whole mip chain box-filtered in linear light), static meshes (ASE: a material slot per material, smoothing groups split at creases, collision hulls as `MCDCX_` objects) and sounds (WAV), in folders by group, and the `EXEC` script that imports them into MyLevel under their groups. |
| `System\_PC_\<Map>.int`, `<Map>-i.int` | The localized texts: per actor, and the map's dossier, places and per-mode texts (`[MapSettings]`). |
| `<Map>.openscct.json` | `osmap.handoff/1`: what RE+ has no place for (identity, map settings, player counts, outfits, the builders of parametric brushes, the classes' sources), with the files it names under `OpenSCCT\` (pictures, outfit textures and models, the project's own asset library); the SHA-256 of the T3D it was written with (`t3d`), a hash per RE+ actor as written (`lifted`), the RE+ actors the export made (`made`) and the brushes and shapes still as an imported original had them (`asImported`). |
| `README.txt` | The steps in RE+ and what the export could not carry. |
| `openscct-export.json` | The files written (also after an export that fails part way). A later export into the folder deletes only these; it refuses a folder that holds other files or a damaged list. |

Brush faces name the map's own materials as `MyLevel.X`, as properties do; the import reads them back as the map's.

The T3D import reads `<Map>-MapClasses.t3d` (unless the T3D already holds its actors, as RE+'s own Export to T3D does
after `MAP IMPORTADD`) and `<Map>.openscct.json` when they sit beside the T3D:

- An RE+ actor still as exported stands for the outfits and player counts again; one the export made is dropped (the
  map never had it). One changed in RE+ stays an RE+ actor (`import.handoffEdited`): its player counts are read back
  per actor as the converter reads them (a row still as exported keeps the map's own rule, which may say more than
  RE+ can hold), while outfits are not (the T3D names textures, not their pixels).
- When the T3D is the one exported (its hash matches), brushes and shapes not listed as imported lose the
  as-imported mark the T3D import gives, and model names the export made up are dropped. When it has changed, the
  T3D's LevelInfo title and shapes are kept.
- A brush gets its builder back when its corners are still the ones the builder made.

Checked by exporting and importing again without RE+ (the editor's own T3D reader), so this says nothing yet about
what RE+ makes of the folder.

A "round-trip compatible" check runs first (`Interop/RoundTripLint`): a map id of 5 letters or digits, ASCII names,
convex and planar brushes and actor shapes (warnings only for faces an imported original already had: an `.sdc`
or a T3D the editor did not write), GE IDs and options that T3D loses, at
most 64 zones, actors of classes the map defines itself, static meshes not in the packages the editor knows, the map's
own assets with no file to import them from, objects of the map's package that are not files (zone reverbs, cloth
patches), stock streamed sounds outside their own map, and outfit models. The file is written even with errors; `ose export`
then exits with code 1. `ose check --target original` runs the original-game limits too. `ose build --target original`
reports an error when the map id is not 5 letters or digits, or the map has more than 128 STriggerLights; above 40
it warns.

## Change documents (scripts)

Edits from scripts come as **change documents**, format `osmap.changes/1`. A change document is a batch of
ops (`create`, `update`, `delete`, `move`, `rotate`, `scale`, `duplicate`, `brush`, `surface`, `ge`, `conveyor`, `link`,
`layer`, `folder`, `settings`, `identity`, `select`, `workspace`, `bookmark`, `reference`, `match`, `outfits`, the `prefab.*` ops and
the gameplay workbench ops `mover`, `magic`, `camnet`, `camera`, `props.paste`, `propset`, `tip` and `zone.action`). It is applied as one undo
step. With `dryRun` it changes nothing and returns a compact diff instead.

- Objects are named by id.
- New objects are placed by reference rather than by a typed height: `on` another object, against a `wall`,
  `at: "Name@anchor+dx,dy,dz"`, or dropped to the floor.
- Vectors, rotators and property values use the same forms as the project files above.

`ose apply` applies a change document to a project. `ose serve` takes them as JSON lines. The running editor hosts the same API, so a script can edit the open map (`--live`).

A Map Check finding that knows a safe fix carries it as a change document (a `move` op today), so the Fix
button, `ose check --fix` and scripts all apply the same edit as one undo step.

The full op and query reference is in [SCRIPTING.md](SCRIPTING.md).

## Screenshot scripts and manifests

The app's screenshot runs (`--shots=`) take a JSON array of steps and write `manifest.json` and `index.html` next to
the pictures. Both are described in [SCREENSHOTS.md](SCREENSHOTS.md). They are tooling files, not part of a project.

The editor's own preferences (`user://settings.cfg`) are not part of a project either. Its `[ui] scale_percent` holds
View > UI Scale: 0 follows the display, otherwise 100 to 250.

## Versioning

- `map.json` `version` and each section's `/1` change only when an old reader would misread the new file.
- New optional keys do not change the version. Readers ignore keys they do not know, but an older editor drops
  them when it saves.


## Native staircase objects (project format 2)

Projects containing editable stairs use `format: "osmap/2"` and `version: 2`; ordinary
projects keep `osmap` version 1. `world/stairs.json` contains a `stairs` array. Each entry
has an `order` (its CSG index among all authored CSG objects), an `object` (identity,
transform and editor metadata), and a `design` (dimensions, profile, materials and
clearance/smoothing settings). The object's generated faces are rebuilt when read.
Stair owners are omitted from `world/brushes.json`, and expose `ObjectKind.Stair`.
See [Editable staircases](STAIRS.md) for the authoring and scripting workflow.
