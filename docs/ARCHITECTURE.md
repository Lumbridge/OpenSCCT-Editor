# Architecture

## In one paragraph

All editor logic lives in a plain .NET library, `OpenScct.Editor.Core`, with no Godot in it. Two front ends sit on
top: the `ose` command line and the Godot editor app. Every change to a map is a command on one undo stack, whether
it comes from a mouse drag, a CLI call or a script. The library reuses OpenSCCT's own C# code for the SVMap format,
collision and class data, so the editor writes maps exactly the way the game reads them.

## Projects

```
OpenSCCT-Editor/
  OpenScctEditor.slnx            Core + CLI (the app builds on its own)
  Directory.Build.props          net10.0, C# 14, nullable, warnings as errors, OpenScctRoot
  global.json                    .NET SDK 10.0.302
  .gitattributes                 LF line endings (CRLF for .ps1/.cmd/.bat), binary assets
  src/OpenScct.Editor.Core/      the editor itself, Godot-free
  src/OpenScct.Editor.Cli/       ose, the command line (assembly ose.dll)
  app/                           the Godot 4.7.2 .NET editor app (OpenScctEditor.csproj, project.godot)
  docs/                          these docs
```

### `OpenScct.Editor.Core`

| Folder | What it holds |
|---|---|
| `Model/` | The map in memory: `MapProject`, `Brush`, `BrushShape`, `BrushFace`, `Actor`, `Ge`, `Layer`, `EditorMeta`, `Workspace`, `PrefabInstance`, `PropSet`, `Vec3`, `Rot`, and the mover base rule (`MoverBase`). |
| `Project/` | Reading and writing `.osmap` folders (`ProjectStore`), the journalled save (`SaveJournal`), rotating save backups (`ProjectBackups`) and the shared JSON form of the model (`ModelJson`). |
| `Commands/` | `IEditCommand`, `CommandStack` (undo, redo, merge, named checkpoints, dirty flag) and the basic edits (add, delete, replace, reorder brushes, project settings, layers, workspace, prefab instances). |
| `Session/` | `EditorSession`: one open map with its folder, undo stack, selection and autosave; autosave copies (`Autosaves`) and the crash markers of running editors (`SessionMarkers`). |
| `Services/` | The service interfaces and their discovery (below), plus `ContentLocator` and `GameExe` (finds the game for Play). |
| `Geometry/` | Brush builders and the blockout kit (`Builders/`), brush operations (`BrushOps`), texture mapping (`TextureOps`), the solid/open query (`SolidQuery`), storeys for the storey filter (`Storeys`), and the editing tools (`Edit/`: faces, vertices, clip, extrude, wall pieces, placement, selection edits). |
| `Classes/` | The class catalogue over `shared/classes.json`, merged over OpenSCCT's generated metadata. |
| `Interop/` | SVMap importer (`Svmap/`, with `IdentityImport`, which keeps a map's identity and pictures), T3D reader, writer and merge (`T3d/`), `.sdc` import through svmbuild plus the native `.sdc` reader (`Sdc/`, `Sdc/Package/`, see SDC-FORMAT.md), the round-trip lint. |
| `Compile/` | CSG, solid-leaf BSP and zones (`Geometry/`), collision, `world.glb` and render JSON, the light bake's inputs and outputs (`WorldLighting`: lights, occluders, zone ambient, mesh vertex light; `LeafLightLists`: each light's BSP leaves, flooded through the leaf portals as the stock editor does, which decide the lights a surface takes), gameplay with derived indexes, traversal, audio and ui files, the strategic map drawn from the collision (`StrategicMapBuild`), the map identity and its pictures (`IdentityBuild`), the manifest (`Output/`: glb and png writers), and what an `.sdc` import's baked brushes and cooked-out actors leave out of a build (`BakedContent`). Also the viewport's CSG preview (`WorldPreview`). |
| `Lightmaps/` | The lightmapper: charts (`ChartBuilder`), atlas packing, the triangle BVH and alpha masks for shadows, the parallel baker with its per-light shadow cache (`LightmapBaker`: one set of entries per pass, saved between builds in the editor's cache folder), one optional bounce (`Bounce`), the original's light formula, relighting a baked world in its own layout (`ConvertedWorld`) with an imported editor copy's own light lists (`SourceLightLists`; lights edited since the import are flooded through the world rebuilt from the project's brushes) and the Lit Preview's progressive passes (`LightPreview`, which builds the same world, zones and lights a compile does). |
| `Identity/` | Map pictures: an RGBA image with PNG read and write, crops to a shape and scaling (`RgbaImage`), the project's `pictures/` folder and the copying of imported pictures into it (`ProjectPictures`), and camera pictures rendered by OpenSCCT's `--svm-mapshot` into `.cache/pictures/`, each stamped with the map it was rendered from (`MapShots`). |
| `Validation/` | Map Check (`MapChecker`), its rules (`Rules/`), the limits table and the design numbers. The deep rules (`Rules/DeepRules`) run only on a compiled map: zone leaks, actors in the void, floating meshes, mesh collision caps, reachability. A finding can carry a fix: a change document the panel, `ose check --fix` and scripts apply. |
| `Analysis/` | Analyses of the map as OpenSCCT loads it (`AnalysisMap`: a compile loaded by the server's loader into a `World`, kept per session in `AnalysisCache` until the next edit): the stealth light map (`StealthMap`, on the bots' `AiLightField`), reachability and routes (`Reachability`, `ReachProfile`, on the bots' `NavBuilder` graph), their JSON answers (`AnalysisJson`), and the headless test round (`HeadlessPlay`). Nav graphs are cached in `nav/` under the editor's cache folder (`OSE_CACHE`, by default `%LOCALAPPDATA%\OpenSCCT-Editor\cache`), keyed by map id, content hash, activation mask and profile. |
| `Gameplay/` | GE authoring: the ledge detector and probe, flush and corner join, GE rules, the traversal world; wiring and the event graph; the mission tree; security devices. |
| `Prefabs/` | Prefab files, lookup (map, user library, built-in), the `${expr}` template language, prefab space transforms, previews (`PrefabPreview`, the Prefabs panel's pictures), and the starter library (`Library/*.json`, embedded). |
| `Assets/` | The asset catalogue (project, converted maps, shared content) with its disk cache, the resolver the compiler and views share, English glosses, asset queries and edits, and mesh import (`Import/`: OBJ and glTF readers, convex hulls). |
| `Scripting/` | The op processor: change documents (`ChangeApplier*`), queries (`MapQuery`), dry-run diffs, placement, the summary, the JSON-lines server (`JsonLinesServer`) and the method table (`ApiMethods`). |
| `Scripting/Live/` | Live editing: the server the app hosts on 127.0.0.1 (`LiveServer`), discovery files (`LiveDiscovery`), the client `--live` uses (`LiveClient`), the live methods (`LiveMethods`), proposals and ghost previews (`Proposals`, `ChangePreview`). |

### `OpenScct.Editor.Cli` (`ose`)

A thin front end. Each command parses its arguments, opens an `EditorSession`, calls Core, and prints plain text or
JSON with `--json`. It never holds editing logic of its own. Commands live in `Commands/`, one file per feature
(`ApiCommands`, `BuildCommands`, `GeometryEditCommands`, `PrefabCommands`...).

### `app/` (the editor app)

A Godot 4.7.2 .NET project (`Godot.NET.Sdk/4.7.2`, net10.0, Forward+). It references Core directly.

| Folder | What it holds |
|---|---|
| `Scripts/Core/` | `EditorContext` (the app's view of the open map: session, actions, log, status, findings, focus requests) and `EditorPanel` (base class of docked panels). |
| `Scripts/Shell/` | `MainWindow` (menu row, tool row, left/right/bottom docks, viewport area, status bar), the shell parts (documents, edit and build actions, menus, toolbar, status bar, shortcuts, command palette, `--run=` startup actions, UI scale), `EditorTheme`, `Icons` (SVG drawings as DPI-aware textures), `AppSettings`, `GameLauncher`. `Background/`: background mode (`BackgroundWindow`, an autoload that keeps the window off-screen and unfocused, and the guards for clipboard, cursor, dialogs and process starts). `Shots/`: `UiDriver` (`ui.shot`, `ui.input`, `ui.tree`), `UiTree`, `UiInput`, `UiLint` and the screenshot runner with its tour and contact sheet. |
| `Scripts/Viewports/` | The four views over one shared scene (`ViewportLayout`, `EditorViewport`, `WorldScene`), cameras and grids, the textured and lit drawing (`Render/`), and the overlays (`Overlays/`: team starts, movers, volumes, zones, lights, GEs, security, links). |
| `Scripts/Tools/` | The viewport tools (select, move, rotate, scale, place actor, builder brush, face, vertex, clip, measure) and the Brush, Placement, Surface and Vertex menus. |
| `Scripts/Panels/` | The docked panels: Outliner, Layers, Classes, Inspector, Map, Identity, Match, Surface, Bookmarks, References, Prefabs, Missions, Traversal, Connections, Mover, Event Sequencer, Camera Network, Properties, Tips, Map Check, History, Log, Assets. |
| `Scripts/Gameplay/` | The GE tool, the security tool, the Gameplay menu and the shared gameplay state (traversal world, event graph). The Analysis menu (`AnalysisPart`) and its results (`AnalysisState`), drawn by the stealth, reachability, nav graph and route overlays. |
| `Scripts/Assets/` | The app's asset service: the catalogue of the open map, favourites, thumbnails and the texture cache. |
| `Scripts/Scripting/` | Live editing: hosts the live server (`LiveEditing`), draws proposals as ghosts (`GhostLayer`) and shows the Accept / Reject bar (`ProposalBar`). |

## Layers

```
            Godot app (app/)                     ose (src/OpenScct.Editor.Cli)           scripts
  panels, tools, viewports, shell parts             one-shot commands               ose serve / ose apply
                 │                                          │                               │
                 └──────────────── EditorContext ───────────┴──────── Scripting op layer ──┘
                                        │
                                  EditorSession  (project, CommandStack, Selection, autosave)
                                        │
   Model ── Commands ── Project ── Geometry ── Classes ── Interop ── Compile ── Validation ── Gameplay ── Prefabs ── Assets   (Core)
                                        │
            OpenSCCT libraries: OpenScct.Core, OpenScct.Content, OpenScct.Physics, OpenScct.Game, OpenScct.Maps
```

Rules:

- Core never references Godot. Anything that can run headless lives in Core.
- The app never edits the model directly. It builds a command and runs it through `EditorContext.Execute`.
- Panels never talk to each other. They listen to `EditorContext` events (`ProjectChanged`, `SelectionChanged`,
  `SessionReplaced`, `FindingsChanged`, `FocusRequested`, `JumpRequested`).
- Scripts in the running editor go through the same session: `LiveEditing` runs each call on the main thread against
  the open `EditorSession`, so an applied change document is one step on the editor's own undo stack.

## Every edit is a command

```
mouse drag / inspector edit / ose apply / API op
        │
        ▼
  IEditCommand  ──►  EditorSession.Execute  ──►  CommandStack
                                                   │  Do(), push on the undo list, clear redo
                                                   │  merge with the last step if it allows (a drag is one step)
                                                   ▼
                                       Changed event ──► Selection.Prune ──► ProjectChanged ──► views refresh
```

- `IEditCommand` has `Name`, `Do`, `Undo` and an optional `TryMerge`.
- `CompositeCommand` makes many commands one undo step. A change document is applied this way.
- `ReplaceObjectsCommand` swaps whole before/after copies of objects. It is simple and always correct, and with a
  merge key a whole drag becomes one step.
- `CommandStack` keeps up to 1000 steps, named checkpoints, a jump-to-step, and a dirty flag set from the last save.
- Because the UI, the CLI and scripts share this one layer, anything one of them can do, the others can too, and
  every change can be undone in the editor.

## Service discovery

Feature modules plug in without editing a shared list.

| Mechanism | Where | How it works |
|---|---|---|
| `[EditorService]` | Core | A class with a public parameterless constructor that implements one or more of `IMapCompiler`, `IWorldPreview`, `IMapChecker`, `IClassCatalog`, `IMapImporter`, `IMapExporter`, `IShapeBuilder`. `EditorServices.Discover()` creates every such class once, ordered by `Priority` then full type name, and registers it under each interface it implements. Single services (compiler, preview, checker, class catalogue): the higher `Priority` wins. Lists (importers, exporters, shape builders) collect all. |
| `[CliProvider]` | CLI | A static method returning `IEnumerable<CliCommand>`. `CliCommands.All()` finds them by reflection, ordered by type name. Each feature file adds its own `ose` commands. |
| `[Panel(title, side)]` | app | An `EditorPanel` subclass. `MainWindow` creates each one, docks it left, right or bottom by `Order` then title, and calls `Bind`. |
| `IShellPart` + `[ShellPart(Order)]` | app | A piece of the shell (menus, toolbar, viewport layout, shortcuts, autosave timer). `MainWindow` creates each one at startup and calls `Attach(window, context)`. |
| `EditorAction` | app | A named action with a stable id (`edit.undo`, `build.play`), a menu path and a shortcut, added with `EditorContext.AddAction`. Menus, the toolbar, the command palette, the shortcut router and `--run=` all use the registered actions. |

Services can be missing while a module is still being written, so callers check for null (for example
`EditorServices.Compiler?`).

`Discover` also finds OpenSCCT's content folder (`ContentLocator`): the `OSE_CONTENT` environment variable first,
then a `.content` folder in the OpenSCCT clone the build tools use (`OSE_OPENSCCT_GIT`, or the clone behind the
pinned `..\OpenSCCT-Editor-deps` worktree), then one in an `OpenSCCT` clone found by walking up from the tool and from
the working folder, then `%LOCALAPPDATA%\OpenSCCT\packs`. A folder counts when it has `shared/classes.json`. The OpenSCCT checkout
(for starting the game) is found the same way, through `OSE_OPENSCCT` first; it counts when it has `OpenSCCT.slnx`.

`GameExe` finds `OpenSCCT.exe` for Play, the same way for `ose play` and the app:

1. The path given (`ose play --exe`).
2. `OSE_GAME_EXE`, then `OPENSCCT_EXE`.
3. The launcher's current install (`%LOCALAPPDATA%\OpenSCCT\install.json`, `Current`).
4. The OpenSCCT checkout's `local/testbuild`, then its `local/build`, then its root.
5. The newest other installed version.

A game older than `GameExe.MinimumVersion` (its `openscct-build.json`, else the file version of its
`OpenScctGame.dll`) is not started: Play, test rounds and map pictures say which version was found and ask for an
update instead.

## OpenSCCT library reuse

Core references seven of OpenSCCT's Godot-free libraries by project reference:

| Library | What the editor uses |
|---|---|
| `OpenScct.Core` | Generated UE class metadata (`UcMeta`), vectors, rotators, rotation matrices. |
| `OpenScct.Content` | SVMap reading and validation (`SvMap.Load`), `GameplayDoc` and its derived indexes (tag index, event edges, mission tree), `TraversalDoc`, canonical JSON (`CanonicalJson`, `SvJson`), hashing (`SvMapHash`), `ClassTable`, `CollisionWriter`, `MapLoader`. |
| `OpenScct.Physics` | `CollisionDataBuilder`, `CollisionWorld` for traces, point checks and drop-to-floor, GE queries. |
| `OpenScct.Game` | Generated SBase metadata, the bots' `NavBuilder` and `NavGraph` (reachability, routes, the nav graph overlay), `AiLightField` (the stealth light map). |
| `OpenScct.Maps` | `BuilderTool`, which runs OpenSCCT's svmbuild converter for `.sdc` import. |
| `OpenScct.Server` | `MapCatalog.LoadSvMap` (a compiled map loaded into a `World` the way the server does it) and `MapAiLight` (the map's lights for the bots' light model). |

Later the app will also compile in OpenSCCT's renderer (`game/Scripts/Render` and the shaders) for the faithful lit
viewport (M6).

### Building against OpenSCCT

`Directory.Build.props` sets `OpenScctRoot`. Its default is the pinned worktree `..\OpenSCCT-Editor-deps\` that
`tools\Sync-OpenScct.ps1` keeps at `openscct.ref`, else the clone named by `OSE_OPENSCCT_GIT`, else `..\OpenSCCT\` next
to this repo. Without any of them the build stops with a message saying so. Override it when your OpenSCCT checkout
lives elsewhere:

```
dotnet build OpenScctEditor.slnx -p:OpenScctRoot=C:/path/to/OpenSCCT/
dotnet build app/OpenScctEditor.csproj -p:OpenScctRoot=C:/path/to/OpenSCCT/
```

The editor never edits OpenSCCT; a clean worktree of OpenSCCT `main` is the safest
thing to point at. The app maps Godot's `ExportRelease` to `Release` and everything else to `Debug` for the
referenced libraries.

### Content

Class data, converted maps and stock assets come from OpenSCCT's `.content` folder (`shared/classes.json`,
`svmap/<Map>/`). They are game data and never go into this repo.

## Determinism

- Project files and compiled JSON go through `CanonicalJson`: sorted keys, float32 text form, a fixed layout.
- Ids are stable and unique ignoring case.
- Service discovery and CLI command order are sorted, so output never depends on reflection order.
- The compiled manifest has no timestamps.

So two runs on the same project give the same bytes, which keeps diffs small and lets scripts compare results.

## Import and export with the original game

### Import

| From | Route | Status |
|---|---|---|
| SVMap folder (the 85 converted maps) | `Interop` SVMap importer: gameplay actors, decoration actors from the render files, GEs, map settings, volume shapes from their colliders. The world BSP stays a locked baked layer. | built |
| T3D (stock or RE+ `File > Export`) | `Interop` T3D reader: brushes with polys, every actor with full properties, the GE list. Best fidelity for maps you have the MapsEd copy of. `ose import --add` adds a T3D to an open project. | built |
| `.sdc` | OpenSCCT's svmbuild converter (Python, run through `OpenScct.Maps.BuilderTool`) makes an SVMap folder for the baked world, then the SVMap importer runs. The native reader (`Interop/Sdc`, SDC-FORMAT.md) reads the same file and lays it over: an editor copy's CSG brushes with exact polygons (built into the baked world, so not rebuilt while it is kept), exact volume, mover and anti-portal shapes, every stored actor property, and the cooked-out lights and PS2-only actors. Editor copies, cooked copies, autosaves with an unwritten summary and version-175 maps all read. `ose sdc-info` shows how the reader sees a file. | built |
| `.sdc` brushes of a cooked copy | `Interop/Sdc/Recovery`: the BSP's convex cells (`CookedBsp`, `CellGeometry`), joined and grown as RE+'s Merge Recovered Geometry, faces split by the surfaces on them (`FacePartition`), built with the editor's compiler and probed against the compiled BSP, grown brushes that differ split back (`BrushRecovery`); `BrushRecoveryImport` adds them as baked brushes in the Recovered brushes layer. `ose sdc-brushes` runs it alone with the rebuild comparison. | built |

### Export

| To | Route | Status |
|---|---|---|
| OpenSCCT | `ose build`: the project compiles to an SVMap 1.2 folder. | built |
| Original game, by hand | `ose export` (or File > Export T3D): a T3D, with the GEs as `GE ADD` lines in its `Begin GE` block, that the stock or RE+ editor imports with `File > Import`, then Build All and Save. No injection, so it works with any install. Brush transforms are baked into the polygons, because RE+ found that SCCT's CSG ignores a brush's Rotation. | built |
| Original game, automated | A headless stock editor driven through RE+ automation imports the T3D, builds and saves `.sdc` (as OpenSCCT's `tools/testmap/twin.py` already does). Then svmbuild converts it back and a twin diff checks the round trip. | planned (M8) |
| Original game, native writer | `ose export --format sdc` (File > Export SDC, the `export` method): `Interop/Sdc/Write` writes the package itself, the editor copy for `Packages\MapsEd` or the cooked copy for `Packages\Maps`. A map imported from an `.sdc` is written back over that file's tables; a new map gets its world from the compiler (SDC-FORMAT.md section 16). The file is read back and checked. | built; not yet loaded by the stock editor or the game |

Before any original export a "round-trip compatible" check runs (`Interop/RoundTripLint`): a map id of 5 letters or
digits, ASCII names, convex and planar brushes, GE IDs that T3D loses, at most 64 zones. `ose check --target
original` adds the other original-game limits (32767 BSP polygons, 8 mover keys). The deep check (`--deep`) adds
the mesh collision caps (5512 triangles and 32767 BSP nodes per mesh); the 15000 per-poly cap and the hull caps are
not checked yet. More than 40 STriggerLights is a warning (40 was the most any
stock map had, not an engine limit); more than 128 is an error for the original game.

T3D and the stock `GE ADD` command lose the GE ID bits and LevelInfo `GEActivationMask` changes. The native `.sdc`
writer keeps them.

The original game also needs files that live outside the map: `<Map>-i.utc` (pictures and map settings) and
`<Map>.int` / `<Map>-i.int` (texts). The automated export writes them through the stock editor (M8).

## Running headless

Everything in Core and `ose` runs without a window. The app can be checked headless too:

```
Godot_v4.7.2-stable_mono_win64_console.exe --headless --path app --import
Godot_v4.7.2-stable_mono_win64_console.exe --headless --path app --log-file <run.log> --quit-after 400 -- --open=<map.osmap> --run=<action>,<action>
```

`--run=` runs editor actions by id (the ids the command palette shows) and logs each result.

An automated run never uses the user's own editor data. A headless run, a background run (`--background`,
`--shots=`) and any run with `--run=` keeps its settings, recent files, autosaves, crash markers, builds, prefab
library, property sets and live discovery file in a folder of its own under `%TEMP%\OpenSCCT Editor\runs` (kept a
week; the run logs which); `-- --user-data=<folder>` or `OSE_USER_DATA` names the folder. Such a run also keeps the
asset cache and imports (`OSE_CACHE`, `OSE_DATA`) in `%TEMP%\OpenSCCT Editor\cache` and `data`, shared by automated
runs, unless the launch sets them, and unnamed screenshots go to `%TEMP%\OpenSCCT Editor\shots`, which is not tidied
away. Godot opens its own log before any editor code runs, so an automated launch passes `--log-file <path>` too, or
it writes `logs/godot.log` in the user's folder (the editor says so in its log). The user's own editor never logs
there: `Run-Editor.cmd` gives each session `logs/editor-<time>.log` (the editor keeps the newest ten). `ose` with the
same `OSE_USER_DATA` (and no `OSE_LIVE_DIR`) finds that editor live. The user's own runs (`Run-Editor.cmd`, or the
app started by hand without those flags) keep `%APPDATA%\OpenSCCT Editor` and `%LOCALAPPDATA%\OpenSCCT Editor\live`.

Quitting goes through `EditorLifetime`: background jobs (texture and thumbnail loads, previews, bakes, builds) run
through `BackgroundJobs`, which hands them its quit token, and worker threads call the engine only inside
`EditorLifetime.TryEngine`. Closing the window or File > Quit asks first while a build, export, import or picture
render is running (quitting stops it, and a content builder run is killed with its processes), then about unsaved
changes. On quit the token is cancelled, posted results and deferred calls are dropped, no worker may start an engine
call, the window is hidden and takes no input, and frames run for up to 2 s while named jobs finish (loads and
previews are not waited for: their results are dropped and their cache files are written whole or not at all); then
the tree quits, with no worker inside the engine. A quit that skips this (Godot's own `--quit-after`) closes the same
fence when the main window leaves the tree, before the engine shuts down.

The build order is collision, the strategic map plan (it needs the collision), gameplay (LevelInfo takes the floors and
snapshot camera), traversal, render, the side files (mapinfo legends from the identity), the strategic map pictures,
then the identity and its pictures. The build never starts the game: camera pictures come from `ose mapshot`, which
runs OpenSCCT in a window started off-screen (`--svm-background`, never `--headless`, which has no renderer) and fills
a cache keyed by the recipe and bookmark; each render records the map it was made from, so a build can say which
pictures are out of date.

`ose play --headless` (and the `playtest` API method) runs the game itself with no window: `OpenSCCT.console.exe
--headless -- --svm-maps <build> --svm-play <map> --svm-host=inline --svm-test-round` with the side, mode, rules and
bots (the last keeps the round on default settings, signed out, writing nothing of the player's). It reads the game's `SVM_PLAY` lines (render warnings, then one status line per bot after 10 s of play) and its `ERROR:` lines, and
stops once every bot has reported or the time is up.

Never start the app or the game windowed on someone else's screen for a test.
