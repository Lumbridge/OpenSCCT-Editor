# Roadmap

Milestones M0 to M8. Each lists what it delivers, where it stands, and what is left. The feature-by-feature list,
with a status per row, is [PARITY.md](PARITY.md).

## Done so far

- Animation Studio: an independent animation workspace with native pose/key/curve editing, layers and rigs,
  import/export, graphs/sequences, map/native-pack integration, and dedicated Spy/Merc first-person setups.
  [Guide](ANIMATION-STUDIO.md); [automated validation and outstanding human checks](ANIMATION-VALIDATION.md).

- Beginner workflow: Simple/Full workspaces with separate layouts, an in-map first-map checklist, the Building
  palette over the eight starter prefabs, dependency explanations and plain-language property tooltips. New profiles
  start simple; existing profiles keep Full Workspace and their shortcuts. The 15-minute learning target still needs
  observation with three first-time users.

M0 to M2 are done and M3 and M4 mostly. Delivered so far:

- [x] Live editing of the open editor (`ose serve --live`) with ghost proposals to accept or reject.
- [x] Prefabs: linked instances, `prefab.place|save|update|break`, `ose prefab`, and a starter library of eight.
- [x] Face, vertex, clip, extrude and surface tools in the app, and the same in `ose edit`.
- [x] GE tools: ledge detection, probe, flush and corner join, GE IDs and the activation mask, the GE draw tool (T).
- [x] The security workbench: place on a wall, snap, link to alarms (Y tool, `ose security`).
- [x] The event graph: Connections panel with a graph view, `ose graph`, `ose wire`, the Missions panel.
- [x] View overlays: team starts, mover paths, volumes, zones and portals, light radius, GE directions, security,
  event links.
- [x] The asset catalogue and the Assets panel (materials, meshes, textures, sounds, thumbnails, favourites, find
  usages), `ose assets` and the `assets` / `asset` queries.
- [x] A textured viewport (Unlit) and a lit preview, for brushes, static meshes and an imported map's baked world.
- [x] glTF and OBJ mesh import with generated collision hulls (`ose asset-import`, Assets > Import Mesh).

On-screen checks:

- [x] Background mode (`tools/Start-BackgroundEditor.ps1`): the full app runs off-screen from its first frame with no
  focus, taskbar button, clipboard, sound or cursor, and with folders of its own.
- [x] Screenshot runs (`--shots=`, `tools/Invoke-EditorShots.ps1`): scripts and a full tour, a manifest, a contact
  sheet and automatic UI checks. Scripts get the same as `ui.shot`, `ui.input` and `ui.tree` in a background editor.
- [x] A finished dark theme with drawn icons, toolbar buttons for every tool with the active one shown, docks that keep
  their width, a draggable split between names and values in the Inspector, maps framed to fill the views on open,
  and UI scaling for high-DPI displays with the views at full resolution.
- [ ] Still to check on a real high-DPI display: the automatic scale, the window fit, popup positions and picking
  above 100%. Still to build: a light theme, saved window and dock sizes, headless `ose render`.

Also delivered (details under each milestone):

- [x] The lightmapper: lightmapped worlds and vertex-lit static meshes, faithful and quality modes, `ose bake`, the
  Lit Preview baked in the background (M6).
- [x] The map maker's workbench: the Prefabs panel, the storey filter, reference images, Preferences with shortcut
  rebinding, journalled saves, save backups and crash recovery (M3, M4, M8).
- [x] The gameplay workbenches: movers, SMagicEvent timelines, camera networks, property sets, zone actions, zone tips
  and the game mode filter (M5).
- [x] Analysis: the stealth light map, reachability, routes and the bots' nav graph, the deep Map Check with fixes, and
  headless test rounds (M6, M7).
- [x] Map identity: name, dossier, places, pictures and the strategic map, in SVMap 1.2 (M5).
- [x] Player-count rules and map outfits, in SVMap 1.2 (M5, M8).
- [x] Native conveyors (`Svm.Conveyor`, SVMap 1.2) with the conveyor tool and export to RE+'s belts (M5).
- [x] Neighbour snap (Shift+G, View > Grid, off by default): moving, resizing, extruding and turning stick faces to the
  nearest brush, mesh or baked surface they touch (faces in line beside them too when asked), with violet feedback, Alt
  during a drag to flip it, and `stick` on the `move` op (M4).

## M0 Foundations

**Delivers:** the repo, solutions, the Core map model, `.osmap` reading and writing, the undo stack, the class
catalogue, brush builders, the `ose` skeleton, the app shell.

**Status:** done.

## M1 Interop

**Delivers:** import of SVMap folders, T3D and `.sdc` (through svmbuild); T3D export; the scripting op layer (change
documents, queries, `ose serve`); the round-trip lint.

**Status:** done. Also `ose import --add` (T3D into an open project), `ose export --only` (the Edit > Copy form),
provenance per object and `ose check --target original`.

## M2 Compiler

**Delivers:** ordered CSG and a solid-leaf BSP for any convex brush; `collision.bin`; `world.glb` (BSP meshes,
mesh library, instances); materials and lighting JSON (lightmapped since M6); traversal; gameplay with derived indexes; the
manifest; validation with OpenSCCT's strict loader; `ose build` and `ose play`.

**Status:** done.

**Left:** build options (BSP balance, portals), incremental build, named build configurations, the BSP cuts and
collision view modes, and writing projectors, rain, soft bodies and fog volumes to `render/fx.json` for brush maps.

## M3 Editor shell

**Delivers:** the Godot app with the dark theme, four views, fly camera, grid, selection, the transform gizmo with
snapping, Outliner (layers, folders, lock, hide, search), Inspector from the class schema (stored vs default, enum
dropdowns, actor pickers, multi-edit), Undo History, command palette, open, save, autosave, Build and Play buttons,
Map Check panel. The app also hosts the API server so scripts can edit the open map.

**Status:** mostly done.

Also built: Edit > Preferences (autosave, backups kept, field of view, grid, view colours, UI scale) with shortcut
rebinding and clash detection saved per user; crash recovery (the newest autosave of a crashed session is offered at
start, File > Open Latest Autosave); journalled saves and rotating save backups (File > Restore Backup,
`ose backups`; backups bring back the assets and prefabs their map was saved with, each file version stored once);
shortcut presets (the stock editor's decoded key table, RE+, Hammer, Blender-like) on the Shortcuts page; Play From
Camera and Play From Here, on the floor under the camera or the point clicked (Alt+P, the perspective view's menu,
`ose play --at`, the playtest's `at`).

**Left:** floating panels and saved dock sizes, lock to actor and set from camera, actor display modes, a local-axis gizmo,
pivot and reset tools, select by brush type, the Lock Selections toggle.

## M4 Geometry tools

**Delivers:** the builder brush and all builders; add, subtract, intersect, deintersect; semi-solid and non-solid;
vertex, edge and face editing; clip; extrude and bevel; UV tools (align, fit, pan, rotate, scale, texture lock,
copy/paste surface); the 2D shape tool; Map Design blockout pieces (room, corridor, vent, crawlway, doorway, stairs,
ramp, platform, with carve or shell); the storey slider; hover "+" wall actions; smooth stairs; placement tools;
measure.

**Status:** mostly done. Built: the builder brush (B) and every builder and blockout piece, CSG add, subtract,
intersect and deintersect, vertex and edge editing (V), face editing and extrude (U), clip (C), bevel, surface
alignment and copy/paste, hover "+" pieces (a doorway can carry a zone portal sheet), drop, mirror and copies along
an axis, measure (M), the storey slider in every view (PgUp, PgDn, View > Storeys, `ose storeys`) and reference images
drawn in the 2D views (References panel). The Storeys panel edits the map's own storey ranges and duplicates a storey
on top or underneath (`storey.duplicate`, `ose storeys --duplicate`); the shown storey is saved with the map (`ose
storeys --show`, the `workspace` op's `shown`) and cuts the world as it is drawn in every view (baked world, CSG
preview, Lit Preview) while the CSG and the light bake keep the whole map. Reference images are named and calibrated
from two measured points.

**Left:** the 2D shape
tool and freehand polygons, smooth stairs, texture lock and an eyedropper, portal and anti-portal tools, Order: Swap,
selection memory, align and distribute, push to wall in the app, fit the builder brush to the selection, scale map,
reference image tiling, cutting actor helpers that span storeys to the storey shown. Neighbour snap next steps: the
vertex tool's dragged vertices sticking to neighbour faces, vertex-to-vertex and edge snap, scale sticking for turned
selections, each member of a multi-selection sticking on its own, in-line matches against the baked world, concave
props by their collision hulls, surface-follow, `ose edit stick` and Placement > Stick to Neighbours, a per-layer
opt-out.

## M5 Gameplay authoring

**Delivers:** GE tools (click-to-detect, the 7 kinds, IDs and mask, auto-flush ledges, corner joins); the security
workbench (lasers, cameras, sensors, snap to wall, cones, drag-to-link alarms); the missions, zones and objectives
tree; player-count rules; the event and Tag graph; the SMagicEvent timeline; the camera network; the mover workbench;
conveyors; property sets; map settings per mode; the map's pictures (menu, loading screens, briefing), level
snapshot and minimap; prefab kinds and set-pieces scripts can place by name; ghost previews for script edits.

**Status:** partly done. Built: GE tools, the security workbench, the event graph and wiring, the Missions panel, map
settings per mode, prefabs with a starter library, ghost previews, native conveyors (the conveyor tool, the `conveyor`
op, the built look and collision with walls that open at joins, point editing in the views, drivers fitted to placed
meshes, export to RE+'s belts), player-count rules (SVMap 1.2: Inspector rows, the Match panel with win targets, a
preview at N players per game mode, what each team size plays, greying out in the views what that match leaves out,
`ose match`, Play at N players), and the gameplay workbenches: the mover workbench (keys, start key, timing, glide,
state, triggers, each key's own leg time and easing as SVMap 1.2 `sim/movers.json` with leg ends dragged on the
timeline, a preview that moves the mover in the views), the SMagicEvent timeline with a preview through the game's
queue logic that follows events through other SMagicEvents, moves movers and switches trigger lights (the lit view and
the Lit Preview, which now draws the run-time world lights over the bake), the camera network with a monitor, property sets and
copy/paste properties, zone on-complete actions, help paths and zone tips, and showing one game mode's objects (each
with its API op or query and an `ose` command). The map's identity: name, dossier, authors, recommended players,
tags and numbered places in the Identity panel, `ose identity` and the `identity` op; pictures from files (with a
focal point), workspace bookmarks rendered by OpenSCCT (`ose mapshot`) or the generated plan, with previews in the
panel and warnings when a camera picture is missing or older than the map; camera pictures rendered end to end by a
linked copy of the game that starts in the background (with a game built after 0.8.84); picture files in PNG, JPEG, BMP or WebP, decoded by the core so
`ose` and the build crop and scale any of them; translations of the identity texts (`ui/text/<lang>.json`) in the
Identity panel, `ose identity text` and the `identity` op; the strategic map (floors, snapshot camera, floor pictures)
drawn from the compiled collision on every build.

**Left:** the rest of conveyors (soft bodies parting round items; riders colliding with the moving escalator steps
rather than the slope under them; seeing the moving steps, items and feet and hearing the hum and footsteps in the
game; the same items for a client that joins late; a driver fit that keeps bumps under 32 uu and gives a level run a
set direction; a route search that stays optimal with belts; for RE+ belts converted from `.sdc`, the rollers of
drawn belts, RE+'s centring push, item offsets and yaw spread, and items turning over a join as RE+'s `FromTurn` does;
checking the RE+ export by importing it), translating the rest of a map's texts
(the briefing explanations `ExpliMapInBrief*`, the per-mode strings and the actor strings, which OpenSCCT reads from
`ui/text` but only the first two of which it applies; a Texts panel with CSV export and import), the EXIF orientation of JPEG photos, more set-pieces (lifts, rotors,
switches...), Add Mover from the CSG menu, the event preview playing receivers other than relays, movers and trigger
lights (gameplay objects, objectives, sounds and emitters are listed, not played) and playing untriggers and repeat
triggers on movers (it draws one activation from the latest trigger), and fading the overlays' labels and
icons of actors a match leaves out (the views fade their drawn shapes).

## M6 Lighting and render

**Delivers:** the faithful lit viewport (OpenSCCT's renderer and shaders), vision modes, a lightmap baker (vertex
lighting first), the light and shadow map, lighting and render budgets, the in-game light checks.

**Status:** started. The compiler bakes lightmaps for the world and vertex light for placed static meshes with the
editor's own ray tracer (`Core/Lightmaps`): faithful and quality modes, masked occluders, `ose bake` and the
`lighting` settings (also in the Map panel), a shadow cache kept between builds and an optional bounce. Relighting
nine stock maps from their editor copies' lights in faithful mode (the original's decoded 3x3 filter, a fitted depth
slack and both-sided occluders) matches the original atlases to 0.4 to 2.5 bytes mean error over all texels. A cooked
`.sdc` gets its lost static lights back at import (from its editor copy, found by its lightmaps in any install, else
fitted stand-ins), so it can be relit too. The Lit Preview
shows the same bake, refined in the background, with static meshes in their baked light, and a Lightmap Density view.
The light and shadow map is built too: the stealth light map (Gameplay > Analysis, `ose analyze stealth`, the
`stealth` query) reads the compiled map with the bots' own light model, with light switch and blackout states and merc
motion tracker sight from every merc viewpoint, the background a merc sees behind the spy (the map's lightmap where
it has a real bake, else the light model, times the material's albedo, the median over up to five mercs; dark spies
that stand out against it), night vision (ambient lift and screen gain), and a flag (and overlay) for lights that light
the world but not pawns, or the reverse.

Each surface now takes only the lights the original's BSP leaf light lists give it (decoded; imported editor copies
use their stored lists, and lights edited since the import, like an authored map's, are flooded through the editor's
own leaves), which fixed the bright strips and wedges at lightmap borders (SteeE's spy start, PolaD's spawn). The Lit
Preview shows the game's dynamic lights on vertex-lit static meshes too.

**Left:** in the light recovery's fitter (maps with no editor copy anywhere): spot lights, suns, clusters of close
lights (they come back as one light too bright), lights that only lit static meshes, and spot-heavy maps (TSP20
finds 8% of its lights); a fit gate that also tells maps like ShadP and CalyD (where most stand-ins fit model error
or a lost sun) from maps like GhosS; non-Light actors with a light type (PanaD's StaticMeshActor3024 baked into the
BSP), which the bake does not treat as lights; a residual heat-map view of what the recovered lights leave
unexplained; rerunning the recovery from the app with a mode choice, off the main thread and cancellable (only the
CLI and the API op can rerun it, and the op blocks a live editor); vertex light for meshes placed on a baked world; whether static mesh vertex light follows the leaf lists
too (not decoded); the flooded lists are close to the original's but not exact (the original widens a surface's lights
to everything under its BSP node's sides, which depends on its own tree); added surfaces of a baked map take every light in reach; masked grates still differ texel by texel; in the stealth map, static meshes'
own vertex light behind the spy and night vision's grain and glow; per-instance mesh lightmaps (needs an OpenSCCT change, SVMap 1.2); the in-game light
checks and budgets; everything else, starting with an edit mode in OpenSCCT's `MapBuilder` (no cell merging, one node
per actor, rebuild one thing at a time) without breaking the game's golden renders.

## M7 Validation and play

**Delivers:** full Map Check (leaks, void, floating, collision caps, hulls, zones), reachability and NavGraph
overlays, the ledge-grab and ladder engine rules, play in editor (spy or merc, mode, bots, from the camera),
host plus client test, playtest pins as bookmarks.

**Status:** partly done. Built: Play in OpenSCCT (side, mode, bots, difficulty, rules; all in Build > Play Options),
Map Check with the GE engine rules (hang clearance, ladder tops, zip line landings), start, placement, reference,
design and limit rules. The deep check compiles the map and adds zone leaks (the minimum cut that seals them, and
the leak path drawn in the views), portals that seal nothing, actors in the void, floating meshes, the mesh collision
triangle, BSP, hull face and 128 hull corner caps, unreachable objectives and switches (by the game's own use test);
rules that know a safe fix offer it. The reachability, route and NavGraph overlays and queries use OpenSCCT's own
`NavBuilder`, with doors and lifts as each side can work them. `ose play --headless` (and the `playtest` API method)
runs a round with no window and reports load, errors and bot positions; it also plays it online with a host and a
connected client (checking that the client's world holds the host's pawns), and has a route bot walk a route.

**Left:** walls that events break and triggers that switch GEs in reachability, background (windowed, off-screen)
test rounds, routes in online rounds, through lifts (the bots' navigation rides none) and with crouch and light steps, playtest pins, saved routes per
team.

## M8 Assets and sharing

**Delivers:** the asset browser (catalogue thumbnails, English names, favourites and tags, find usages), glTF and OBJ
import with collision hulls and splitting, the emitter library, sound import, skins and outfits, scatter, linked
prefabs, compare with backup, brush recovery from compiled maps, release packaging (SVMap pack plus `.sdc` release),
and the automated `.sdc` build through the stock editor.

**Status:** partly done. The native `.sdc` writer is in (`ose export --format sdc`, both layouts; SDC-FORMAT.md
section 16), so an `.sdc` no longer needs the stock editor; still to check that the stock editor and the game load
what it writes. Built: the asset browser and catalogue, glTF and OBJ import with hulls, linked prefabs in
Core and `ose`, the Prefabs panel (pictures, place or drag into a view, parameters, update, break, save the
selection as a prefab), and map outfits (SVMap 1.2: team textures, a replacement model with the bone check, the
cosmetics policy; Map panel, `ose outfit`), and brushes from `.sdc` editor copies (the native reader,
SDC-FORMAT.md) and brushes recovered from a cooked copy's BSP (SDC-FORMAT.md section 8), with an import report (Import Report panel,
`ose import`), one baked layer kept while the other is built from the brushes, and the original editor's zone
numbering, so a rebuilt original gets close to its own zone count. The Emitter Library browses, previews and places
stock, saved and pack effects, with particle editing and loop waits. Placed effects compile to `render/fx.json`.

Also done: the export for the original editor (a folder with the T3D, the map's own classes, its textures, meshes
and sounds for RE+ to import, its `.int` texts, a README with the steps, and a handoff file the T3D import reads back).
It has not been tried in RE+ by hand yet. **Decision:** textures go as DDS (DXT1, DXT5 with alpha,
our own mips); a map with outfits or player counts but no RE+ actor for them
gets new `SCharacterOutfits0` / `SMatchSizeRules0` actors in the classes file; the README answers No to "Import into
existing map?" (the T3D brings its LevelInfo and builder brush), with Yes as the fallback if the MyLevel assets do not
survive it.

**Left:** prefab overrides and nesting, auto-split of big meshes, texture and sound import, an
outfit preview and skin presets, PSK export of outfit models for `.sdc` maps, player-count rules and outfits in the
`.sdc` export, reading outfits changed in RE+ back from a T3D, scatter, compare with backup (`ose diff`), brush
recovery from cooked copies, `ose pack` and `ose publish`, the stock editor and the game loading a written `.sdc`,
rebuilding a written-back map's baked world after brush edits and its lighting after light or mesh edits, moving a
custom map's own assets (they keep their slots in a written `.sdc`).

## Next

Suggested order, smallest useful steps first:

1. Events that change the map in reachability (walls that events break, triggers that switch GEs).
2. The faithful lit viewport and the rest of M6 (the lightmapper is in).
