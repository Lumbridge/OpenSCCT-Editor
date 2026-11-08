# Animation Studio implementation checks

Checked on Windows with Godot 4.7.2 .NET and .NET 10, 2026-11-08, on the isolated `feature/animation-studio`
worktrees. These are implementation checks; no public release/deployment or human acceptance is implied.

| Check | Result |
|---|---|
| Both repository solutions, warnings treated as errors | Pass, zero warnings/errors |
| Animation Core/import/export/history/recovery checks | 53 pass, including the actual headless FBX helper |
| Existing game animation, equipment, notify and movement selection | 459 pass; 30 golden-recording checks skipped because their local recordings are unavailable |
| Game-side animation/view-model logic | 10 pass |
| Animation/content-contract/combat/weapon network selection | 56 pass, including mismatched pack rejection and late joins at two link profiles |
| Focused studio runtime checks, including authored first-person holster timing and once-only view-model notifies | 8 pass; includes the 6 studio checks in the broader selection above |
| Roll-pose content and mover network checks | 40 pass |
| Map/manifest/gameplay document/content hashing selection | 68 pass |
| Animated-mesh and view-model tween regressions after provider initialization changes | 17 pass |
| First-person rendered layouts: desktop, laptop and 150% UI | 4 shots, zero UI findings, zero failed steps |
| General studio desktop/laptop layout | 2 shots, zero UI findings |
| Source runtime preview | Compiled original sample, skinned ActorX prop and Merc first-person rig load and sample successfully |
| Local playable replacement overlay | Merc spawns in TestRange with the authored pack; no runtime errors after importing resources and providing the existing native audio binaries |
| Exported editor FBX helper | Local Windows export imports the synthetic FBX through `ose animation import --godot <exported-editor.exe>` |

The 53 Core checks cover linear/step/cubic interpolation, preserved tangents and trim/retime/reverse, override and
additive layers, two-bone/chain IK, cross-rig pin baking, retargeting, lossless projects, undo/redo and rollback,
journal/recovery, Spy/Merc action round trips, all supported importers, glTF output, pack/path/digest validation,
case-insensitive binding conflicts, ordered loop events, late-join evaluation, audio dependencies and map compilation.
GLB dependencies include external textures, which stock assets use even when their geometry is binary GLB.

Two stock Spy rigs sampled 240 times through the shared evaluator in approximately 0.6 seconds including import.
This is an evaluation/scrub measurement, not a rendered frame-rate claim. Rendered playback performance with two
stock meshes still needs interactive profiling.

The check tool generates five editable examples and their runtime overlays: a new layered Spy greeting, a Merc
rifle reload replacement, paired hand pinning, a skinned animated prop and an explicitly bound camera/object/event
sequence. Stock-derived output is local-only and is not committed. The generated map fixture also builds and reloads
an animation manifest with an actor graph binding.

Remaining human checks are original-game pose/visual comparison across a broad stock clip set, hands-on authoring
and mirrored/paired alignment review, audible sequence synchronization, interactive playback/scrubbing profiling,
and live multiplayer interruption/late-join acceptance. Existing automated tests cover stock timing, collision,
movement and replicated playback contracts; they do not replace those observations.
