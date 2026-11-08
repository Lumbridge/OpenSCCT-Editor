# The `ose` command line

`ose` is the editor's command line. It creates, checks, builds, imports, exports and edits maps without the window,
through the same Core library and the same undoable op layer as the app, so a command's edit is the same edit the
app would make.

- In a release: `ose\ose.exe` in the editor's folder (one self-contained file).
- From source: `dotnet src/OpenScct.Editor.Cli/bin/Debug/net10.0/ose.dll` after `dotnet build OpenScctEditor.slnx`.

`ose help` lists the commands and `ose <command> --help` shows one. Commands that edit a project save it as one
undo step unless they take `--dry-run` and it is given; many print JSON with `--json`. A failed command exits with
code 1. Commands that need OpenSCCT's content (class data, converted maps) find it as the app does: `OSE_CONTENT`,
the OpenSCCT clone next to the editor, or an OpenSCCT installed with its launcher; `--content` overrides it where
offered. Commands that start the game (`play`, `mapshot`) need OpenSCCT 0.8.112 or newer (`--exe`, `OSE_GAME_EXE`, or the
launcher's install).

The scripting API (`apply`, `query`, `summary`, `serve`, `live`) is described in full in [SCRIPTING.md](SCRIPTING.md).

## Animation Studio

`ose animation new <folder.osanim>` creates a standalone animation document. `info` and `check` inspect/validate it;
`import <project> <asset>` reads GLB/glTF, FBX, BVH, PSK or PSA. FBX also takes `--godot <exe>` and
`--editor-project <app-folder>`. `export <project> <output.glb|output.gltf|output.osanimpack>` accepts `--skeleton <id>`
and `--rate <samples-per-second>` (default 60). `install <pack> <library-folder>` validates and installs a pack into
a digest-named subfolder; `recover <recovery.json> <folder.osanim>` restores a document. See the
[studio guide](ANIMATION-STUDIO.md) for editing, first-person setups, map bindings and testing.

This page is generated from `ose help`.

## Projects

For launcher-managed installation/update commands and their structured protocol, see
[Launcher integration](RELEASING.md#launcher-integration-protocol-1).

### new

```
ose new <folder.osmap> [--id NewMD] [--title "My map"]
```

Creates a new map project with one room, a spy and a merc start.

### info

```
ose info <project>
```

Prints a short summary of a project.

### identity

```
ose identity <project.osmap> [set key=value ...] | [place <n> [name] [--at x,y,z] [--floor n] [--delete]] | [text <lang> [name|description|when|where|summary|place.<n>=value ...] [--delete]] | [picture <menu|loading-spy|loading-merc|briefing|banner> file <image> [--focus x,y] [--link] | camera <bookmark> [--look plain|nightvision|thermal] [--fov 80] | plan | none] [--replace]
```

Prints or edits the map's identity and saves it. set keys: name, family, variant, description, when, where, summary, authors, version, language, players, tags, floors (authors and tags are comma lists, floors is auto or comma heights, an empty value clears). text edits the map's translation into a language (OpenSCCT shows en, fr, de, es, it): its name, description, dossier lines and place names, each as long as the map's own may be; an empty value falls back to the map's own text, --delete removes the translation. A picture file (PNG, JPEG, BMP or WebP) is copied into the project's pictures/ unless --link, and the build crops it to the slot's shape around --focus; loading pictures are added to the side's list unless --replace.

### storeys

```
ose storeys <project> [--json] [--show <storey>|all] [--duplicate <storey> [--below|--by <z>]] [--dry-run]
```

Lists the map's storeys, lowest first: its own (workspace storeys) or the ones found from its floors, and the one the views show. --show picks the storey the editor shows (a name or a number, 1 for the lowest; saved with the map). --duplicate copies everything on a storey to a new storey on top of the highest one (--below: under the lowest; --by: that many units up) and saves.

### backups

```
ose backups <project> [--restore <name|n>] [--json] | backups --keep <n>
```

Lists the save backups, newest first; --restore brings one back (n = 1 is the newest; the current files become a backup). --keep sets how many backups each save keeps, for you (the editor's Preferences show the same number).

## Scripting API

### apply

```
ose apply <project.osmap> <changes.json|-> [--dry-run] [--save] [--author name]
```

Applies an osmap.changes/1 document (a file, '-' for stdin, or inline JSON) as one undo step and prints the diff.

### query

```
ose query <project.osmap> <query-json|file|->
```

Runs a query ({"q":"find",...}, or {"queries":[...]}) and prints compact JSON.

### summary

```
ose summary <project.osmap>
```

Prints a short overview of the map (counts, starts, objectives, problems).

### mcp

```
ose mcp [project.osmap] | mcp --live [--pid N]
```

Runs an MCP server over UTF-8 stdio. Exposes map editing, queries, builds and playtests, plus visual proposals
and editor state in live mode. See [MCP for mapping agents](SCRIPTING.md#mcp-for-mapping-agents) for setup and
focused tool discovery.

### serve

```
ose serve [project.osmap] | serve --live [--pid N]
```

Serves the scripting API as JSON lines on stdin/stdout: {"id","method","params"} -> {"id","result"|"error"}. --live edits the map open in the running editor.

### live

```
ose live [--json]
```

Lists the running editors that take live connections (ose serve --live connects to the newest; --pid picks one).

## Build, check and play

### build

```
ose build <project.osmap> [--out <folder>] [--target openscct|original] [--fast] [--no-classes] [--no-validate] [--content <dir>] [--verbose]
```

Compiles the project to an SVMap 1.2 folder (default &lt;project>/../build/&lt;MapId>) and prints a JSON result. --target original also checks the stock engine's limits and writes the original-format export (&lt;MapId>.t3d next to the map folder) when an exporter is installed.

### validate

```
ose validate <svmap-folder>
```

Loads a map folder with OpenSCCT's strict loader, checks its manifest and render files; prints a JSON result.

### check

```
ose check <project> [--target openscct|original] [--only code,prefix...] [--min error|warning|info] [--deep] [--fix] [--json] | check --rules
```

Runs Map Check (stable rule codes; --rules lists them). --deep also compiles the map and runs the deep rules (zone leaks, actors in the void, floating meshes, mesh collision caps, reachability). --fix applies every fix a rule offers and saves the project. Exit code 1 when any error is found.

### bake

```
ose bake <project.osmap> [--mode quality|faithful] [--density N] [--samples N] [--light-size uu] [--sun-size deg] [--bounce G] [--bounce-samples N] [--atlas 512|1024|2048|4096] [--relight-imported true|false] [--fast] [--save] [--out <folder>]
```

Builds the map (default &lt;project>/../build/&lt;MapId>) with its lightmaps baked by the project's lighting settings, or by the ones given here for this bake (--save also stores them in the project). Prints the settings used, the atlas files, the charts, texels and rays, the vertex-lit meshes and the time taken.

### play

```
ose play <project.osmap> [--side spy|merc] [--mode Story] [--bots N] [--players N] [--difficulty easy|normal|hard|expert] [--rules playlevel|map] [--at camera|<bookmark>|X,Y,Z[,Yaw[,Pitch]]] [--out <folder>] [--exe <OpenSCCT.exe>] [--dry-run] [--headless [--seconds N] [--output] [--route x,y,z;id;... [--from <start id>]] [--client]]
```

Builds the project, then starts OpenSCCT on it (--svm-maps, --svm-play). --players lays the map out for that many players (default: the map's Play setting; 0 counts the bots and you). --at spawns you there once instead of at a start: camera (the 3D view's camera saved with the map), a bookmark's name or number, or a spot in Unreal units with yaw and pitch in rotation units; you start on the floor under it, or under where it looks when it is outside the level (offline only: not with --route or --client). --dry-run prints the command instead. --headless runs the round with no window and reports whether it loaded, the errors and warnings the game printed, where you spawned, the map's starts and where each bot stood (bots default to the map's Play setting, else 2, at most 15; the run ends once every bot has reported, or after --seconds, 40). --route has a route bot of --side walk those stops in order (floor points, or objects by id, reached where the game's use test lets it use them) with the side's own navigation and door handling, while you stand by on the other side; the report says each stop reached or where it got stuck (no other bots unless --bots; --from starts it at that player start; --seconds defaults to 30 plus 40 a stop). Lifts are not ridden. --client plays the round online instead (no bots, no route): this game hosts it with the lobby's rules for --mode and a second headless game joins it on the other side; the report says what each loaded and which pawns each one's world held, and the client fails when its world does not match the host's. Exit code 1 when the round did not load, printed errors, the route was not finished or a game of the pair failed.

### mapshot

```
ose mapshot <project.osmap> [--only menu,loading-spy-1] [--force] [--exe <OpenSCCT.exe>] [--out <folder>] [--fast] [--no-classes] [--dry-run]
```

Builds the project, renders its camera pictures with OpenSCCT in a background window (a linked copy of the game started minimised beside a background override.cfg, so it never takes the focus) into the project's .cache/pictures, then builds again so the map carries them. Pictures already rendered from this version of the map are kept unless --force; --dry-run lists each picture's state (missing, stale or current).

### analyze

```
ose analyze stealth|reach|route|nav <project.osmap> [--lights start|blackout|lit|switch:ID] [--off ids] [--on ids] [--stance crouch|stand] [--night] [--spacing N] [--no-merc-sight] [--cells] [--side spy|merc] [--drops survivable|unhurt|bot] [--mask N|all] [--from x,y,z|id] [--to x,y,z|id] [--near x,y,z|id] [--radius N] [--limit N] [--pretty]
```

Analyses the compiled map with OpenSCCT's own loader, light model and bot navigation: stealth (how dark the floors spies reach are, merc tracker and torch reach, the background behind the spy; --night as night vision shows it), reach (each side's reach from its starts, spy-only areas, unreachable objectives, doors and lifts), route (can a side get from --from to --to, through which doors and lifts) and nav (the bots' graph). Prints the JSON of the query of the same name.

## Import and export

### import

```
ose import <source> <out.osmap> [--map Name] [--format t3d|svmap|sdc] [--content <.content>] [--game <install>] [--lights off|exact|full] [--twins <folder;...>] [--fit-seconds N] [--brushes off|cells|merge] [--add] [--json]
```

Imports a map into a new project: a T3D file (stock editor File > Export), a converted SVMap folder, or an .sdc (converted through OpenSCCT's svmbuild first, needs the game install). A cooked .sdc gets the static lights its cook stripped back (--lights, default full: from its editor copy in any install, from the file, else fitted stand-ins; see sdc-lights), and its brushes recovered from the compiled BSP as editable brushes in the Recovered brushes layer (--brushes, default merge: cells joined and grown over their neighbours, as RE+'s map recovery; cells joins only cells that share a whole face; off keeps the baked world only; see sdc-brushes). With --add, a T3D file (a whole map or copied objects) is added to the existing project instead, as one undo step, with clashing names renamed. Prints the import report (what came in, cooked out, lifted, recovered, dropped, warnings); --json prints the result, findings and report as JSON.

### export

```
ose export <project.osmap> <out> [--format t3d|sdc|original] [--layout editor|cooked] [--game <install>] [--only Kind:Id,...] [--content <.content>] [--json]
```

Exports a project for the original game after the round-trip lint: T3D for the stock editor's File > Import, a map package (.sdc) written natively, or a folder for RE+. An .sdc is the editor copy for Packages\MapsEd (--layout editor) or the cooked copy the game loads from Packages\Maps (--layout cooked, the default); it is read back and checked, and a map imported from an .sdc keeps that file's objects it does not edit. --game is the install new materials and meshes are looked up in. --format original writes the folder for RE+ to &lt;out> instead: the T3D, the actors of the map's own classes and their sources, the map's own textures, meshes and sounds with a script that imports them, the map's texts (.int) and a README of the steps in RE+. --only writes just those objects (Brush:Id, Actor:Id, Ge:Id or a bare id) in the T3D Edit > Copy form, without LevelInfo and builder brush. T3D writes even with lint errors; the exit code is 1 then. SDC applies known original-game replacements (including standard breakable glass for editable windows), validates the generated package before saving, and preserves any previous SDC on errors. A readable report of replacements, errors/workarounds and warnings is saved beside the destination as `<map>.sdc.export-report.md`; required user actions are bold.

### export-t3d

```
ose export-t3d <project.osmap> <out.t3d> [--only Kind:Id,...] [--content <.content>] [--json]
```

The same as export --format t3d.

### sdc-info

```
ose sdc-info <file.sdc> [--content <.content>] [--json]
```

Reads an original map package natively and reports its layout (editor copy or cooked copy), version and zlib chunks, actors per class, brushes and faces, whether every Model, Polys and StaticMeshInstance export reads to its last byte, the world model and the GE count.

### sdc-lights

```
ose sdc-lights <cooked.sdc | project.osmap> [--mode off|exact|full] [--twins <folder;...>] [--no-twin] [--force-fit] [--fit-seconds N] [--truth <editor copy.sdc>] [--out <project.osmap>] [--save] [--content <.content>] [--game <install>] [--json]
```

Brings back the static lights the stock cook stripped from a cooked .sdc (an import, or a project imported from one), best source first: the editor copy whose lightmap atlases are the map's byte for byte (MapsEd and autosaves of every install found, cached by fingerprint), lights the file still holds outside the level; in full mode (the default) also an editor copy of another bake checked light by light against the cooked lightmaps, then stand-in lights fitted to the bake when what is left is more than noise (the fit gate; --force-fit fits anyway). Prints what came back, the rebake error against the cooked atlases before and after, and each light's source and confidence. --no-twin ignores editor copies (to measure the fit); --truth compares the result with an editor copy's lost lights (recall, precision, position error, stand-in precision by peak). --fit-seconds caps the fit (none by default: a cap makes the result depend on the machine). --out writes the project; --save writes a project given back. A cooked .sdc is imported without brush recovery (see sdc-brushes).

### sdc-brushes

```
ose sdc-brushes <file.sdc | project.osmap> [--mode cells|merge] [--no-check] [--truth <editor copy.sdc;...>] [--grid N] [--save] [--content <.content>] [--json]
```

Recovers editable brushes from a map package's compiled BSP (the brush recovery a cooked import runs) without importing the rest of the map, and prints what it made: cells, joined and grown brushes, split-backs, sheets and portals, thin brushes, faces no surface covers, and how the brushes built by the editor's compiler compare with the compiled BSP (probes beside every original surface, a grid of points through the map, drawn area per material). --mode cells joins only cells that share a whole face (merge, the default, also grows brushes over their neighbours). --no-check skips the rebuild check during the recovery. --truth builds the brushes of other copies of the map (an editor copy of the same save, or another tool's recovery) and compares them the same way. --grid sets the sample points per axis (48). Given a project imported from a cooked .sdc (its map.json source.file still there), recovers its brushes again from that file, in place of the ones recovered before (for a project imported with brush recovery off or by an older version); --save writes the project.

## Geometry

### shapes

```
ose shapes [kind] [--json]
```

Lists the brush builders and blockout pieces with their parameters, defaults and ranges.

### shape

```
ose shape <kind> [--<param> value ...] [--at x,y,z] [--rot pitch,yaw,roll] [--csg subtract|add] [--pretty]
```

Builds a shape and prints its brushes as project JSON (builder spec on each shape).
Plain builders carve (subtract) unless --csg add (sheets add); blockout pieces choose per brush from their mode.

### brush

```
ose brush <project> <op> [ids...] [options] [--dry-run] [--json]
```

Edits brushes of a project in one undo step and saves it (--dry-run: report only). Ops:

```
  add <kind> [--<param> v] [--at x,y,z] [--rot p,y,r] [--csg add|subtract]   place a builder shape or blockout piece
  rebuild <id> [--<param> v]       regenerate a parametric piece with changed parameters
  info <ids>                       convexity, planarity, closure, volume, bounds
  split|clip <id> --plane nx,ny,nz,d [--cap material]   cut by a world plane (clip keeps the back)
  flip | mirror --axis x|y|z | merge-faces | triangulate | hull | bake | recenter <ids>
  snap <ids> [--grid 16]           snap vertices to the world grid
  uv <mode> <ids> [--faces 0,2] [--scale s]   modes: default, floor, wall, face, cylinder,
     fit --tex 256,256 [--repeat 1,1], pan --u 16 --v 0, rotate --deg 90, scale --u 2 --v 2 [--absolute], flip-u, flip-v
  paste-surface <ids> --from Brush:face [--faces ...] [--mapping]
```

### edit

```
ose edit <project> <op> [args...] [options] [--dry-run] [--json]
```

Runs a geometry editing tool on a project. Faces are Brush:index; points are x,y,z; lists of points use ';'.

```
Surfaces:
  faces <brushes...>                          list faces: material, flags, normal, scale, rotation, pan
  material <faces...> --mat Pkg.Group.Name    set the material (--mat none: the default)
  flags <faces...> [--set a,b] [--clear c]    poly flags: invisible, masked, translucent, twoSided, unlit, portal...
  lightmap <faces...> --scale 16              lightmap scale
  surface <faces...> [--pan u,v] [--rotate deg] [--scale u,v] [--fit u,v]   set the mapping absolutely
  select-faces <material|coplanar|walls|floors|ceilings> [--mat m] [--face B:i] [--brushes ids]   list matching faces
  extrude <Brush:face> --by 64 [--new]        push the face out (negative: in), or grow a new brush from it
Vertices and edges:
  vertices <brush>                            list world vertices and convexity
  move-vertex <brush> --at p;p --by dx,dy,dz  move vertices (faces that bend are split into triangles)
  weld <brush> --at p;p [--to x,y,z]         weld vertices into one point (their centre, or --to)
  delete-vertex <brush> --at p;p              remove vertices (the brush becomes the hull of the rest)
  snap-vertex <brush> [--at p;p] [--grid 16]  snap vertices (all when no --at) to the world grid
  bevel <brush> --edge p;p --size 16          cut an edge off
  clip <brushes...> --points p;p[;p] [--axis z] [--split] [--flip] [--cap mat]   clip or split by a plane
Placement:
  drop <actors...> [--ceiling] [--align] [--half h]   drop to the floor (or ceiling) by brushes and baked collision
  snap <ids...> [--grid 16]                   snap pivots to the grid
  mirror <ids...> --axis x|y|z [--about x,y,z]   mirror across the selection's centre (or a point)
  array <ids...> --count 3 --step x,y,z      copies along a vector (custom event Tags get fresh ones per copy)
  replace-class <actors...> --class Pkg.Class    change the class, keeping properties
  measure <x,y,z> <x,y,z>                     distance, player heights, spy/merc run time
```

### probe

```
ose probe <project> <x,y,z> [x,y,z ...] [--drop 600] [--json]
```

Tells whether world points are in solid or open space by the project's brushes (CSG replay), and how far
down the floor is (within --drop). A baked world's geometry is not seen.

## Gameplay

### ge

```
ose ge draw <project> --kind LADDER|PIPE|POLE|ZL|HOH|FENCE|LG --from x,y,z --to x,y,z [--facing x,y,z] [--name id] [--zone-only] [--width n] [--radius n] [--spacing n] [--material asset] [--dry-run] [--json]
ose ge detect <project> [--apply] [--compare] [--all] [--min-drop 40] [--min-length 32] [--headroom 0] [--step 8] [--box x,y,z,x,y,z] [--meshes] [--limit 200] [--json]
ose ge flush <project> [--ids a,b] [--reach 32] | ge join <project> [--ids a,b] [--dist 24] | ge probe <project> [--ids a,b] | ge mask <project> --ids a,b --id 4 | ge platform <project> --ids a,b --platform common|ps2|xbox | ge activation <project> <0-255>   (edits: [--dry-run] [--json])
```

GE authoring. detect finds grabbable ledges in the geometry (brushes and baked collision) and lists the ones no LG covers yet (--all lists every one, --meshes also looks at a baked map's props, --compare scores them against the map's LGs, --apply adds them as one undo step). probe explains, per LG, whether its edge is grabbable and why not. flush moves LGs onto their edges, join closes corner gaps, mask sets GE ID bits, platform the GE platform and activation sets LevelInfo.GEActivationMask.

`draw` creates the GE and its matching visible geometry as one editable object. Ladder, pipe and pole endpoints
use the first point's XY and the two points' bottom/top heights; `--facing` is the direction toward the climber
(default +X). Use `PIPE` for a climbable vertical pipe or pole; the original `POLE` kind is a marker with no grab
event. Other kinds use the endpoints as given. Dimensions default to the selected kind; width controls
ladder width or fence height, radius controls bar thickness and spacing controls rungs or fence posts. Fence
panels default to the stock climbable `drpoc_chantier.SM.moa_grillage` material; a replacement must also be marked
climbable in the game's material data. `--zone-only` creates only the GE; ledges
always use existing geometry. Later point or appearance edits regenerate the geometry. Builds and original-game
exports lower it to ordinary brushes without adding those brushes to the saved project.

### graph

```
ose graph <project> [--id X] [--depth 1] [--dangling] [--rings] [--missions] [--limit 300] [--json]
```

The event graph with SVMap 1.1 rules (fire, accept and reference links), events nothing receives, event rings, and the missions / objectives / triggers tree.

### wire

```
ose wire link <project> --from A --to B[,C...] | wire unlink <project> --ids a,b | wire unlink <project> --from A --to B | wire tag <project> --old X --new Y   ([--dry-run])
```

Event wiring, one undo step each. link makes A fire B (A.Event = B's Tag, giving B its own Tag when its tag is shared; several receivers share the first one's Tag); an alarm instead adds a mover to MoversToLock, an objective trigger to ObjectiveTriggersToLock, or anything else to its Events. unlink --ids clears Event (and an alarm's Events); unlink --from --to removes just the links from A to B. tag renames a Tag on every actor that carries it and every event that names it.

### security

```
ose security list <project> [--json] | security place <project> --kind camera|laser|motionsensor|presencetrigger|alarm --at x,y,z [--reach 512] | security snap <project> --ids a,b [--reach 512]   (edits: [--dry-run])
```

Security devices. list shows each camera's cone, range and sweep, each laser's beam (stopped by walls), presence and mine radii and the volumes motion sensors watch. place puts a device on the wall nearest a point, facing out; snap moves devices onto their nearest wall.

### mover

```
ose mover <project> <id> [--keys x,y,z[/p,y,r];...] [--add-key true|x,y,z] [--delete-key n] [--start-key n] [--move-time s] [--stay-open-time s] [--delay-time s] [--delay-time-last-key s] [--other-time s] [--eased true|false] [--legs key:time[:ease];...] [--state S] [--show-key n] (edits: [--dry-run] [--author a])
```

A mover's keys (offsets from its base, 1 to 8: the game's arrays hold 8), times, glide, start key and state. --legs gives keys their own timing (OpenSCCT): the leg from the key before in seconds (mover: its MoveTime; empty: unchanged) and the easing at the key (smooth, speed, mover: its glide; empty: unchanged), e.g. 1:4:smooth;2:0.5. With no edit options, prints its keys, one activation's timeline with the time each key is reached, and what triggers it.

### magic

```
ose magic show <project> <id> [--play t[:untrigger],...] [--at s] | magic set <project> <id|new> --groups <json|file> [--at x,y,z --tag T] [--stop-actor id|none] | magic export <project> <id> [--out file] | magic import <project> <id> <file> (edits: [--dry-run] [--author a])
```

SMagicEvent timelines. show lists the groups and plays activations through the game's logic (default one trigger at 0), then what they do to the map by --at seconds (default: the end), through other SMagicEvents too: the events delivered, the trigger lights switched and the movers started; set replaces the groups (or makes a new one with id new); export and import use the RE+ JSON exchange (scct.smagic-event v1).

### camnet

```
ose camnet list <project> | camnet link <project> <id,id,...> [--loop true] | camnet remove <project> <id,...> | camnet camera <project> <id> [--name N] [--pitch below,above] [--yaw below,above] (edits: [--dry-run] [--author a])
```

Security camera networks (SCamNetwork): list the rings with the camera the CamNet gadget opens on and link problems; link whole networks in an order; take cameras out; name a camera and limit how far it turns.

### tips

```
ose tips list <project> | tips add <project> --at x,y,z --spy text [--merc text] [--radius 150] [--height 100] [--marker false] [--mesh path] [--ends-game true] | tips set <project> <id> [--spy] [--merc] [--radius] [--height] [--only-with-help b] [--stay b] [--end-game-delay s] (edits: [--dry-run] [--author a])
```

Zone tips (hint cylinders shown with in-game help) and their help path markers.

### zone

```
ose zone list <project> [mission] | zone add <project> <mission> <actor> [--delay s] [--type trigger|untrigger|...] | zone message <project> <mission> --spy text [--merc text] [--seconds 6] [--delay s] | zone set <project> <mission> <index> [--delay s] [--type t] | zone remove <project> <mission> <index> (edits: [--dry-run] [--author a])
```

What a mission (zone) does when complete: doors, lights, sounds, HUD messages or any actor, with delays, through the mission's own SMagicEvent relay (made when needed; other senders' wiring is never changed).

### modes

```
ose modes <project> [mode]
```

The game modes the map has missions for and how many actors only they use; with a mode (number or name), those ids.

### match

```
ose match show <project> [--players N] [--mode Sabotage] | match set <project> <ids...> [--players min,max|any] [--outside hidden|key:N|default] [--inside normal|hidden|key:N|default] [--win n,n,...|none] | match clear <project> <ids...>  (edits: [--dry-run] [--author a])
```

Shows and sets player-count rules (which actors are in a match of N players) and missions' win targets; prints JSON.

### outfit

```
ose outfit show <project> | outfit set <project> --team spy|merc [--body file.png|none] [--head file.png|none] [--model file.glb|Package.Mesh|none] [--goggle-bone name] [--goggle-offset x,y,z] [--cosmetics map|player|none] | outfit clear <project> --team spy|merc  (edits: [--dry-run] [--author a])
```

Shows and sets a team's outfit on the map: body and head textures, a replacement model and the cosmetics policy; prints JSON.

### props

```
ose props copy <project> <id> [--out file] | props paste <project> (--from A | --from-file copy.json) --to b,c [--keys k1,k2] (edits: [--dry-run] [--author a])
```

Copy properties: copy prints an actor's stored values as osmap.props/1; paste copies them onto other actors, from an actor of the map or from a copy file (so values move between maps; default: everything but identity, placement and links to other actors).

### propset

```
ose propset list [project] [--class C] | propset apply <project> <Class.Name> --to ids | propset save <project> <id> <name> [--keys k1,k2] [--map true] [--note text] | propset delete <Class.Name> [--map project] | propset import-ini <PropertiesSets.ini> [--map project]
```

Property sets (the stock editor's Use Properties Set): the map's propsets.json and the user library. import-ini reads the original game's System/PropertiesSets.ini (into the library, or a map with --map).

## Classes

### classes

```
ose classes [--placeable] [--filter text] [--isa Class] [--json]
```

Lists the actor classes (name, super, placeable/abstract, editable property count).

### schema

```
ose schema <Class> [--all] [--own] [--json]
```

Prints a class's properties with type, category, enum values and defaults (editable unless --all; --own: declared by the class itself).

## Assets and prefabs

### content-packs

```
ose content-packs list | install <id-or-map-code> [--content folder]
```

Lists the OpenSCCT-Content catalogue or installs a pack and its required shared data. Prints JSON.
Use an id such as `shared/core` or `recovered/ClarD`, or a map code such as `ClarD`. Map installs use the full
archive so its assets are available in the editor. `--content` sets the installation folder for the command.
The launcher-managed editor and launcher use the same pack folder. A source build uses its active content folder.
Existing local files that the pack installer does not own are protected.

### assets

```
ose assets [--kind material|mesh|texture|sound] [--filter words] [--origin Map|shared|MyLevel] [--project p.osmap] [--limit 200] [--json]
```

Lists the assets an authored map can use: the project's own (with --project), the converted maps' materials and meshes,
and the shared textures and sounds. --filter matches every word (any case) in the name, package, group, origin or English gloss of French names, or the kind.

### asset

```
ose asset <path> [--kind k] [--project p.osmap] [--json]
```

Details of one asset: file, size or bounds, materials, the converted maps that use it, and its uses in --project.

### asset-import

```
ose asset-import <project> <mesh.obj|.gltf|.glb> [--name N] [--group Meshes] [--scale 1] [--zup] [--collision parts|hull|box|mesh] [--overwrite] [--place x,y,z] [--json]
```

Imports a mesh into the project's assets as MyLevel.&lt;group>.&lt;name>, with a generated collision hull. OBJ is read in
Unreal units (Y up unless --zup), glTF in metres. --place also adds a StaticMeshActor standing on that point.

### asset-place

```
ose asset-place <project> <mesh path> --at x,y,z [--rot p,y,r] [--json]
```

Adds a StaticMeshActor showing the mesh, its bottom on the point (one undo step), and saves the project.

### asset-apply

```
ose asset-apply <project> <material path> (--brushes id,id | --all) [--json]
```

Sets every face of the brushes to the material (one undo step), and saves the project.

### prefab

```
ose prefab list [project] | show <name> [project] | place <project> <name> [--at x,y,z|Id@anchor] [--wall nearest|+x|-y..] [--on true] [--lift z] [--offset x,y,z] [--pivot anchor] [--rot-deg yaw] [--face x,y,z] [--id Name] [--layer L] [--<param> value ...] | save <project> <name> <ids...> [--pivot x,y,z] [--yaw-deg d] [--title t] [--summary s] [--category c] [--library] [--overwrite] [--link] | update <project> [instance ids...] [--name prefab] [--all] | break <project> <instance ids...> [--delete]  (edits: [--dry-run] [--author a])
```

Lists, places, saves, updates and unlinks prefabs (saved groups of brushes, actors and GEs with parameters); prints JSON.

### emitters

```
ose emitters list | show <effect> | place <project> <effect> [--at x,y,z] [--loop-min seconds] [--loop-max seconds] [--dry-run] | save <project> <name> <ids...> [--category name] | import-pack <file.json> [--content folder]
```

Lists, inspects, places and saves Emitter Library effects and imports RE+ effect packs. Effect names and ids are accepted. Prints JSON.
`--content` sets the source of stock presets and particle textures. `place` defaults to the map origin and saves the
project unless `--dry-run` is given. With either loop bound set, each particle system repeats after a wait in that range.
If you give only one bound, it sets a fixed wait. `save` captures the specified emitters around the centre of their positions.

## Updates

### update

```
ose update check [--install <folder>] [--prereleases] [--json] | update stage [--install <folder>] [--prereleases] | update apply --plan <file> [--no-restart] | update recover (--install <folder> | --swap <record>) [--no-restart]
```

Editor updates. check: is a newer release out (for the release folder ose runs from, or --install; from source it only reports the latest release)? stage: download, verify and unpack it into that folder, ready to swap in when the editor next restarts. apply and recover are used by the editor: apply swaps a staged build in after the editor quit, recover puts the previous build back after an interrupted swap (run at sign-in by the entry the swap leaves).
