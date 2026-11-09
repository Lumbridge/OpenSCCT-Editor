<img align="right" src="app/branding/icon.svg" width="88" alt="OpenSCCT Editor logo">

# OpenSCCT Editor<br clear="right">

**A map and animation editor for Splinter Cell: Chaos Theory Versus.** OpenSCCT Editor makes and edits spies-vs-mercs maps
for [OpenSCCT](https://github.com/Lumbridge/OpenSCCT), the community rewrite of the game, and for the original game.

[![Latest release](https://img.shields.io/github/v/release/Lumbridge/OpenSCCT-Editor)](https://github.com/Lumbridge/OpenSCCT-Editor/releases/latest)
[![Last commit](https://img.shields.io/github/last-commit/Lumbridge/OpenSCCT-Editor/main)](https://github.com/Lumbridge/OpenSCCT-Editor/commits/main/)

[Download](https://github.com/Lumbridge/OpenSCCT-Editor/releases/latest) ·
[Features](docs/PARITY.md) ·
[Command line](docs/CLI.md) ·
[Scripting](docs/SCRIPTING.md) ·
[Animation Studio](docs/ANIMATION-STUDIO.md) ·
[OpenSCCT](https://github.com/Lumbridge/OpenSCCT) ·
[Report a bug](https://github.com/Lumbridge/OpenSCCT-Editor/issues)

## Get it

The Windows OpenSCCT launcher includes an optional **Editor** tab. Choose **Set up Editor** there to install the
editor and any missing game/content prerequisites, then **Open Editor**. The launcher keeps the editor in its own
`editor` folder and can update it; Help > Check for Updates inside the editor continues to work too. Installing the
editor is optional for players. This requires launcher and editor releases that include launcher integration.

For a standalone installation:

1. Install [OpenSCCT](https://github.com/Lumbridge/OpenSCCT#play) with its launcher (game version 0.8.116 or newer). The
   editor uses the content the launcher installs (class data, the converted maps, their materials and meshes) and
   starts the game for Play and test rounds.
2. Download `OpenSCCT-Editor-<version>-win-x64.zip` from the
   [latest release](https://github.com/Lumbridge/OpenSCCT-Editor/releases/latest) and extract it to a folder your
   account can write to (Documents, a games folder). There is no installer.
3. Double-click **OpenSCCT Editor.exe**. Keep the folder together: it also holds the editor's data folder and `ose`,
   the command line tool.

Windows 10/11, 64-bit. The editor is not signed yet, so Windows may show "Windows protected your PC" the first time:
click More info, then Run anyway.

Importing a map from the original game (`.sdc`) and exporting one for it also need your own copy of Splinter Cell:
Chaos Theory Versus. The editor contains no game files: it reads yours, and OpenSCCT's content, at run time.

The editor updates itself. A few seconds after it starts it asks GitHub whether a newer version is out (Edit >
Preferences > General turns this off) and shows a link in the status bar; Update now downloads it in the background
and restarts into it with your map. Help > Check for Updates asks at any time.

## What it does

New users start in a **Simple workspace** with one large 3D view and a **Building** palette. Open the optional
**Your first map** guide from **Help > Your First Map**. It starts closed; middle-click its tab or right-click >
Close to dismiss it. Name and save your map, explore the starter room, build a doorway, corridor and room with the
highlighted Surface tool and wall '+' menu, position the teams and drag in an objective computer, then check and
play. The checklist follows the map as you edit, undo and reopen it. Gameplay pieces include their connections and explain any missing
content before placement. Objective computers include an activating Story mission so their laptops appear during
play. Hover over common settings for plain-language explanations.

The double-chevron button at the right of the bottom status bar expands or collapses the workspace. Up chevrons show
all tools and panels; down chevrons return to the simpler layout. These choices are also in **View > Workspace**. Each workspace
remembers its view, dock sizes and tabs separately. Existing settings keep the full workspace. Default tool keys
are **S** Select, **M** Move, **R** Rotate, **Shift+S** Scale and **Shift+M** Measure; custom bindings and explicit
preset assignments take precedence. Shared initials use Shift for the secondary tool.

The editor is usable for blockout and gameplay work, with gaps. [docs/PARITY.md](docs/PARITY.md) has every feature of
the stock ChaosTheory_Editor and of [RE+](https://github.com/Lumbridge/SCCT_Versus_Reloaded_Editor) with where it
lives here and whether it is built; [docs/ROADMAP.md](docs/ROADMAP.md) has what comes next.

- **Projects.** `.osmap` project folders made for version control, undo and redo, autosave, crash recovery and
  rotating save backups.
- **Download and open.** Use **Download & Open** on the toolbar or in **File** to browse maps from OpenSCCT-Content,
  search by name or code, and filter by category or download status. The editor downloads the map and required
  shared packs, then opens it as an unsaved project. Choose **Save As** to keep your edits; installed maps can be opened again.
- **Import and export.** OpenSCCT's converted maps, T3D from the stock editor, and `.sdc` map packages (static lights
  and editable brushes recovered from cooked copies). Export to T3D, to `.sdc` written natively, or to a folder with
  the steps for RE+.
- **Build and play.** A compiler to OpenSCCT's SVMap format (CSG, BSP, zones, collision, lightmaps, vertex-lit
  meshes), checked with OpenSCCT's own loader. Play the map in OpenSCCT from the editor, or run a headless test
  round with bots, a scripted route or a host and a joining client.
- **Doors.** The [Doors palette](docs/DOORS.md) creates six styles of framed doors with hinged, sliding or raised
  movement. Preview wall cuts, edit dimensions and opening behaviour in Inspector, and resize with viewport handles.
  Built-in tutorials cover placement, editing and switches. Editable doors build for OpenSCCT.
- **Windows and glass.** The [Windows palette](docs/WINDOWS.md) places scalable glass or complete framed windows
  in six shapes and three finishes. Framed windows cut editable walls; right-click inside an existing frame to fit
  linked breakable glass. These windows build for OpenSCCT and require Editor 0.2.5 or newer to reopen.
- **Light palette.** Drag ceiling, wall and portable fixtures from the Lights dock to place a mesh and editable
  light together, with one undo step. Raw Lights offers point, spot, local ambient fill, sunlight, colored and
  animated presets. Fixture choices use the installed map content; edit each light in Inspector and rebuild static
  lighting after changes.
- **The app.** Four views with wireframe, brush colour, textured, lit preview and lightmap density modes; Outliner,
  Layers, Inspector, Map, Surface, History, Log, Map Check, Classes, Bookmarks, Assets, Prefabs and References panels;
  select, move, rotate, scale, place, builder brush, face, vertex, clip, measure, GE and security tools; neighbour
  snap; a storey slider; rebindable shortcuts with stock, RE+, Hammer and Blender-like presets.
- **Geometry palettes and builder preview.** Open **Geometry** from Building or the builder toolbar for common
  floor slabs, walls, rooms, passages and other shapes. Drag a preset into a view or use **Use builder preview**.
  In **Builder Brush** (B), arrows move, square handles resize a dimension with the opposite face fixed, and circles
  adjust radii. **Place Here** previews surface contact; **Draw Box** draws a cube in a 2D view. **Add/Subtract** commits
  the preview; **Escape** restores the state before a gesture. The separate **Stairs** palette creates editable stair assemblies.
- **Surface connectors.** In Surface mode (U), hover an editable wall, floor or ceiling and click its '+' badge.
  Floors and ceilings offer hatch openings, shafts, rooms above/below and stair connections. Openings cut through
  the actual editable slab thickness. Connect Stairs seeds the first floor connection; choose another above or below.
- **Gameplay.** Ledge detection, flush and corner joins, GE IDs; event wiring and the event graph; missions; the
  mover, SMagicEvent, camera network, property set, zone action and zone tip workbenches; player-count rules, map
  outfits and native conveyors; the map's name, dossier, places, pictures and translations.
  **Link missions & objectives** in Building or Inspector opens the Missions panel in either workspace. Choose a
  mission while an objective or its device is selected to link that objective; choose an objective while devices
  are selected to link them. Without a compatible selection, the choosers just change the viewed target. Existing
  links are preserved and each edit can be undone.
  Use **Add objectives…** to check several existing objectives for a mission, then choose an objective and
  **Add devices…** to connect its computers, bomb targets or disks. Search keeps checked items when filtered.
  The chain-link icons add selected actors to the named target. **New mission**, **New objective** and **New device…**
  build a chain; the integrated Objectives palette also lets you drag devices into a view, linked to the chosen objective.
  Right-click a mission, objective or device in the map, Outliner or Missions tree and choose **Link to…** to search
  compatible actors, review the proposed links and create them together. Tree × buttons unlink without deleting actors; arrows change objective order for chained
  missions. Every batch is one undo step. **Completion actions** reveals doors, messages and timing.
  The Classes panel also supports dragging actors into any view. Class names display in English; search accepts
  both the English name and original class path, which remains available in tooltips.
  GE dots use grid and neighbour snapping when moved or resized; ladder and pipe facing dots use rotation and
  neighbour angle snapping. The Move, Rotate and Scale tools snap GE selections too. Hold Alt to invert neighbour snapping.
- **Analysis.** Map Check with stable rule codes and fixes, a deep check of the compiled map (leaks, void, floating
  meshes, collision caps), the stealth light map, reachability and routes for each side, and the bots' nav graph.
- **Assets and prefabs.** An asset catalogue of the materials, meshes, textures and sounds a map can use, glTF and OBJ
  mesh import with generated collision, and 42 built-in prefabs with shaded previews, category/library filters,
  favourites and remembered settings. The common furniture, storage and industrial kit includes its own finishes.
  Use **Get Packs…** in Assets to download
  shared data and map asset libraries from [OpenSCCT-Content](https://github.com/Lumbridge/OpenSCCT-Content). The editor
  indexes the new assets as soon as installation finishes. Open **Tools > Emitter Library** to browse,
  preview and place the 37 RE+ stock effects from installed OpenSCCT maps, save your own effects or import RE+ effect packs.
- **Find anything and guided lessons.** Ctrl+Shift+P searches commands, controls, placeables, classes, installed
  assets, prefabs and presets. Search **Laptop** to reveal its tile, or **tutorial** for 39 cancellable lessons with
  highlighted controls. Favourites, saved lesson progress and **Show me** help you find the next step.
  See [the command-palette guide](docs/COMMAND-PALETTE.md).
- **Command line, scripting and MCP.** Everything the app does is also an `ose` command
  ([docs/CLI.md](docs/CLI.md)). Scripts edit maps through change documents and queries, one shot or over a JSON-lines
  server, and can edit the map open in the editor as ghost proposals to accept or reject
  ([docs/SCRIPTING.md](docs/SCRIPTING.md)). MCP clients can use the same tools through `ose mcp`.
  The editor can copy its [MCP configuration](docs/SCRIPTING.md#mcp-for-mapping-agents).

## Build from source

You need the .NET SDK 10.0.302 or newer (`global.json`), Git, PowerShell 7 (`pwsh`), and Windows for the app. The
editor builds against the Godot-free libraries of OpenSCCT at the commit in `openscct.ref`, always one on OpenSCCT's
public `main`. Clone both repositories side by side:

```
git clone https://github.com/Lumbridge/OpenSCCT-Editor
git clone https://github.com/Lumbridge/OpenSCCT
pwsh OpenSCCT/tools/Get-Godot.ps1
```

`Get-Godot.ps1` downloads the Godot .NET version OpenSCCT names in its `godot-version.txt` into
`OpenSCCT/tools/godot/`. The editor needs Godot 4.7.2 exactly (the version of the app's `Godot.NET.Sdk`): if your
clone has moved on to a newer Godot, install 4.7.2 and set `OSE_GODOT` to its `Godot_v4.7.2-stable_mono_win64.exe`.
Then double-click `OpenSCCT-Editor/Run-Editor.cmd`, or drop a `.osmap` folder on it. Each start runs
`tools\Sync-OpenScct.ps1`, which keeps a detached worktree of your OpenSCCT clone at `openscct.ref` in
`..\OpenSCCT-Editor-deps` (it never changes the clone's own working tree), builds the editor and opens it.

To build by hand (warnings are errors):

```
pwsh tools/Sync-OpenScct.ps1
dotnet build OpenScctEditor.slnx
dotnet build app/OpenScctEditor.csproj
```

If your OpenSCCT clone lives somewhere else, set `OSE_OPENSCCT_GIT` to it (Sync, the build, `Run-Editor.cmd`'s
Godot lookup and the editor's content lookup use it), or pass `-p:OpenScctRoot=C:/path/to/OpenSCCT/` to
`dotnet build`. `OSE_GODOT` names the Godot exe, `OSE_CONTENT` a content folder and `OSE_GAME_EXE` the game to play
in. A build from source never updates itself.

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains the layout, [docs/FORMAT.md](docs/FORMAT.md) the project
format, [docs/SCREENSHOTS.md](docs/SCREENSHOTS.md) the background mode and screenshot runs, and
[docs/RELEASING.md](docs/RELEASING.md) how releases are packaged and how the updater works.

Bug reports, testing, clearer docs and code changes all help. Read [CONTRIBUTING.md](CONTRIBUTING.md) before sending a
pull request. Original game files, converted maps and assets, and decompiled code stay outside Git (see OpenSCCT's
[provenance rules](https://github.com/Lumbridge/OpenSCCT/blob/main/re/PROVENANCE.md)).

### App command line

Arguments after `--`:

| Argument | Does |
|---|---|
| `--open=<map.osmap>` | Opens a project (or a file an importer reads). |
| `--run=<id>,<id>...` | Runs editor actions by id once the window is up. Each result goes to the log. |
| `--changes=<file>` | A change document for `live.previewFile` to show as a ghost proposal. |
| `--asset=<path>` | Selects an asset in the Assets panel. |
| `--asset-import=<file>` | The mesh `assets.importMesh` imports, instead of asking. |
| `--background` | Background mode: off-screen, never focused. Start it with `tools/Start-BackgroundEditor.ps1` ([docs/SCREENSHOTS.md](docs/SCREENSHOTS.md#background-mode)). |
| `--shots=<script.json\|tour:full\|tour:quick>` | Takes screenshots from a script, writes a manifest and a contact sheet, then quits. |
| `--shots-out=<folder>` | Where the screenshots go. |

The command palette (Ctrl+Shift+P) lists every action with its id; F1 lists every shortcut. A check without a window:

```
Godot_v4.7.2-stable_mono_win64_console.exe --headless --path app --log-file C:/temp/check.log --quit-after 400 -- --open=MyMapD.osmap --run=edit.selectAll,build.check
```

## A first map from the command line

`ose` is `ose\ose.exe` in a release, or `dotnet src/OpenScct.Editor.Cli/bin/Debug/net10.0/ose.dll` from source.
`camera.json` holds a change document:

```json
{"label":"camera","ops":[{"op":"create","class":"SCamera","at":"Room0@center","wall":"+x","lift":128}]}
```

```
ose new MyMapD.osmap --title "My map"
ose apply MyMapD.osmap camera.json --save
ose check MyMapD.osmap
ose build MyMapD.osmap
ose play MyMapD.osmap --side spy
```

To edit the map open in the running editor from a script, use `ose serve --live` ([docs/SCRIPTING.md](docs/SCRIPTING.md)).

## Project information

OpenSCCT Editor is a fan project, not affiliated with or endorsed by Ubisoft. Splinter Cell and Chaos Theory are
trademarks of Ubisoft Entertainment. This repository contains project code, tools, original art and attributed
third-party components ([THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)). It contains no game files: the editor
reads your own game files and OpenSCCT's content at run time.


## Texture and material authoring

See [Advanced materials](docs/ADVANCED-MATERIALS.md) for texture-set import, animated textures, PBR/depth controls, optional displaced collision and the actual original-game compatibility matrix.
