<img align="right" src="app/branding/icon.svg" width="88" alt="OpenSCCT Editor logo">

# OpenSCCT Editor<br clear="right">

**A modern map editor for Splinter Cell: Chaos Theory Versus.** OpenSCCT Editor makes and edits spies-vs-mercs maps
for [OpenSCCT](https://github.com/Lumbridge/OpenSCCT), the community rewrite of the game, and for the original game.

[![Latest release](https://img.shields.io/github/v/release/Lumbridge/OpenSCCT-Editor)](https://github.com/Lumbridge/OpenSCCT-Editor/releases/latest)
[![Last commit](https://img.shields.io/github/last-commit/Lumbridge/OpenSCCT-Editor/main)](https://github.com/Lumbridge/OpenSCCT-Editor/commits/main/)

[Download](https://github.com/Lumbridge/OpenSCCT-Editor/releases/latest) ·
[Features](docs/PARITY.md) ·
[Command line](docs/CLI.md) ·
[Scripting](docs/SCRIPTING.md) ·
[OpenSCCT](https://github.com/Lumbridge/OpenSCCT) ·
[Report a bug](https://github.com/Lumbridge/OpenSCCT-Editor/issues)

## Get it

The Windows OpenSCCT launcher includes an optional **Editor** tab. Choose **Set up Editor** there to install the
editor and any missing game/content prerequisites, then **Open Editor**. The launcher keeps the editor in its own
`editor` folder and can update it; Help > Check for Updates inside the editor continues to work too. Installing the
editor is optional for players. This requires launcher and editor releases that include launcher integration.

For a standalone installation:

1. Install [OpenSCCT](https://github.com/Lumbridge/OpenSCCT#play) with its launcher (version 0.8.93 or newer). The
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

The editor is usable for blockout and gameplay work, with gaps. [docs/PARITY.md](docs/PARITY.md) has every feature of
the stock ChaosTheory_Editor and of [RE+](https://github.com/Lumbridge/SCCT_Versus_Reloaded_Editor) with where it
lives here and whether it is built; [docs/ROADMAP.md](docs/ROADMAP.md) has what comes next.

- **Projects.** `.osmap` project folders made for version control, undo and redo, autosave, crash recovery and
  rotating save backups.
- **Import and export.** OpenSCCT's converted maps, T3D from the stock editor, and `.sdc` map packages (static lights
  and editable brushes recovered from cooked copies). Export to T3D, to `.sdc` written natively, or to a folder with
  the steps for RE+.
- **Build and play.** A compiler to OpenSCCT's SVMap format (CSG, BSP, zones, collision, lightmaps, vertex-lit
  meshes), checked with OpenSCCT's own loader. Play the map in OpenSCCT from the editor, or run a headless test
  round with bots, a scripted route or a host and a joining client.
- **The app.** Four views with wireframe, brush colour, textured, lit preview and lightmap density modes; Outliner,
  Layers, Inspector, Map, Surface, History, Log, Map Check, Classes, Bookmarks, Assets, Prefabs and References panels;
  select, move, rotate, scale, place, builder brush, face, vertex, clip, measure, GE and security tools; neighbour
  snap; a storey slider; rebindable shortcuts with stock, RE+, Hammer and Blender-like presets.
- **Gameplay.** Ledge detection, flush and corner joins, GE IDs; event wiring and the event graph; missions; the
  mover, SMagicEvent, camera network, property set, zone action and zone tip workbenches; player-count rules, map
  outfits and native conveyors; the map's name, dossier, places, pictures and translations.
- **Analysis.** Map Check with stable rule codes and fixes, a deep check of the compiled map (leaks, void, floating
  meshes, collision caps), the stealth light map, reachability and routes for each side, and the bots' nav graph.
- **Assets and prefabs.** An asset catalogue of the materials, meshes, textures and sounds a map can use, glTF and OBJ
  mesh import with generated collision, and prefabs with a starter library. Use **Get Packs…** in Assets to download
  shared data and map asset libraries from [OpenSCCT-Content](https://github.com/Lumbridge/OpenSCCT-Content). The editor
  indexes the new assets as soon as installation finishes. Open **Tools > Emitter Library** to browse,
  preview and place the 37 RE+ stock effects from installed OpenSCCT maps, save your own effects or import RE+ effect packs.
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
