# Animation Studio implementation checks

## Imported weapon authoring regressions (2026-11-08)

The Desert Eagle first-person GLB carries a 0.01 ancestor scale. A 30-pixel local move previously displaced its
magazine about 0.31 pixels. Axis-plane dragging now uses the complete parent transform and moves it 27.5–29.2 pixels
at two zoom levels in both local and World modes. Scale dragging is relative. Position arrows move one physical
centimetre, rotation arrows one degree and scale arrows five percent, independently of precise typed values.

Draft poses with Auto key off now share the document's Undo/Redo ordering without adding keys or changing its dirty
checkpoint. History restores the edited clip/frame and handles keying a draft, keyed gestures and document changes.
Inspector values follow stepping, scrubbing and playback without recreating active spinners. Imported eye-space
assets use their calibrated eye origin rather than a standing character's eye height.

Curves fits time and values to the visible bone/layer/component, includes sampled interpolation extrema, labels
frames and seconds, supports channel filtering and Frame keys, and selects only visible tracks with Ctrl+A.
New action clip creates and reveals an editable duplicate with or without a loaded first-person profile; Merc
setups include Desert Eagle. Rendered checks exercise source preservation, selection, scrolling, action assignment,
unique names and Undo/Redo.

Validation: 71 animation contract/history checks, 506 discovery/lesson checks and 106 rendered interaction checks
pass with no engine errors. Rendered checks use the actual imported Desert Eagle reload and `mag` bone, verify
draft/keyed transforms and inspector updates, fit dense base keys and sparse large-value endpoints, resize and pan
the curve panel, drag keys at the fitted scale and create action clips from Stock. Active spinner lifetime and
layout are checked; native held-arrow autorepeat remains a manual check because the background window does not
receive native pointer hover. Screenshots were visually reviewed. These checks do not establish original-game
visual parity or human gameplay acceptance.

## Project clip selection regression (2026-11-08)

Editor 0.2.30's Project tree handled selection synchronously. Selecting a different row refreshed and rebuilt that
same tree while Godot still blocked tree changes for its selection signal. The rebuild failed and left the UI's
refresh guard enabled; scrub inputs were then ignored until playback refreshed the transport.

The follow-up defers clip selection until the input signal finishes, ignores selections queued for a replaced
document, and clears the refresh guard in a `finally` block. Rendered regression checks click Project rows, switch
between clips with different durations, and scrub both transport controls before playback. They also select and
scrub an imported stock clip and verify its displayed skeleton changes. The new selection/range check fails on
the 0.2.30 implementation with the tree-blocked error and a null reference, before applying the fix.
After the fix, all 38 rendered interaction checks pass without engine errors, including both timeline controls
immediately after Project selection and the first scrub of a scrolled stock clip. The app builds without warnings.

## Interaction fixes and guided lessons (2026-11-08)

The `fix/animation-studio-interaction` branch adds five animation lessons through the existing tutorial system and
fixes stage grounding/framing, hierarchy selection, transform handles, scene removal, scrub dragging, stale New
overlays, animation command navigation and dismissible palette highlights. Replacement playtests compile a private
practice map rather than relying on the development TestRange being installed.

Validation: the full editor solution builds with zero warnings/errors; 57 animation contract/history/compilation
checks and 506 discovery/lesson checks pass. An isolated rendered editor passes 29 interaction checks using the
actual palette, hierarchy, sliders and transform handles, including first-person setup and New. Four final
desktop/laptop/150% first-person screenshots have zero UI findings or failed steps. The installed OpenSCCT 0.8.117
executable loads the generated practice room and selected stock Spy replacement pack, spawns the Spy, and exits
cleanly. That launch check is headless; it verifies loading/spawning, not visual or human gameplay acceptance.
The Tutorials submenu also passes reachability, scrolling and bounded-height checks at 100% and 150% UI scale,
with all 53 lessons present.

Use `tools/Start-BackgroundEditor.ps1 -EditorArgs '--animation-ui-checks'` to run the rendered interaction checks
with installed character content configured through `OSE_CONTENT`. Its private run folder contains screenshots,
the replacement overlay and generated practice map. No public release or installation is implied by these checks.

## Initial implementation

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
