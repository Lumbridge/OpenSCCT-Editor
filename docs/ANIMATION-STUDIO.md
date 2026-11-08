# Animation Studio

Choose **Animation** beside **Map** to open the studio. Animation documents have their own history, recovery files
and splitter layout. A map does not need to be open or saved. **New** creates an empty document; the Stock tab's
**Create example rig** starts with an original three-bone rig without requiring installed game content.

Use **Tutorials** in the studio or **Help > Tutorials** for five guided lessons: first keyframes, stock Spy/Merc
editing, scene items, in-game replacements and first-person weapons/arms. Each uses the existing tutorial card,
highlights the current control and lets you work at your own pace. Search for `tutorials: animation` in the command
palette. Running an animation command opens the studio; **Show me** reveals its actual control without running it.
Palette highlights clear on click, Escape, moving away after entering the control, or after eight seconds.

## Start with a character or imported rig

The **Stock** library searches the installed converted character, weapon and skeletal-prop GLBs. Double-click an
asset to make an editable copy. **Assets > Animation > Import Spy Rig / Import Merc Rig** loads a character directly.
Stock files are sources: saving, reimporting and testing write into the animation project or a temporary overlay.

**Import** accepts GLB, glTF, FBX, BVH, PSK and PSA. The glTF reader retains animation keys and cubic tangents.
FBX uses the editor's packaged headless Godot importer; Blender is not required. BVH assumes centimetres and Y-up;
adjust scene placement or retarget a differing source convention before exporting. ActorX skeletons and keys use
the original Unreal-to-glTF coordinate conversion. PSA and animation-only glTF can join a compatible existing rig;
an incompatible skeleton stays separate and can use a retarget profile. PSK imports skinned mesh geometry as well.

Select a clip in **Project**, or use **+ Clip** to start from the rig's rest pose. Duplicate preserves the source
clip. **Reimport base tracks** in the Clip inspector reloads the source while retaining authored layers, events and
the current duration. Keep the source file available for reimport; a saved project copies render dependencies and
can play without the source. FBX reimport remembers the original FBX, rather than the helper's generated GLB.

## Spy and Merc first-person animation

Use **Assets > Animation > Spy First Person / Merc First Person**, or choose **First Person** in the studio's section
selector. **Load setup** loads the real role-specific rigs. Spy has separate P190/Shocker and arm instances with the
game's shared two-handed grip solver. Merc supports rifle, Uzi, shotgun and Desert Eagle rigs, including their arms.

The **Action** selector organizes Idle, Move, Draw, Holster, Fire, Reload, Aim and Gadget clips. **Edit** switches
between weapon and arms. **New action clip** duplicates the selected motion into that action, allowing each action
to be authored independently. Select a bone and edit its pose or curve, then switch actions to preview the result.
The default holster is a separate reversed draw clip and can be edited without changing the draw.
New action clips are selected and revealed in **Project**, even when starting from **Stock**, and receive unique
names. Without a loaded setup, **New action clip** creates a standalone editable copy of the selected motion;
load a setup to assign and bind first-person actions. Select or import a motion first if the button is disabled.

**Eye view** uses the game's X-forward camera and reference-aspect FOV conversion; **Orbit** inspects the assembly
from outside. FOV is stored per setup. Bone overlays, keys, layers and constraints work in both views. The runtime
slot field is editable; press Enter to commit it. **Bind actions** explicitly enables visual replacement bindings.
Unbound setups remain authoring assets. Existing game action selection still decides which slots play: assigning
an unused slot does not create a new gameplay action. In particular, a newly authored Spy reload/gadget clip can be
previewed or sequenced, but needs a supported game slot to play during normal gameplay.

## Pose and keyframe editing

Space plays/pauses, the transport steps frames and scrubs, and the Clip inspector sets loop start/end, duration and
FPS. Speed supports reverse preview. Preview and scrubbing do not dispatch gameplay events. The stage supports
middle-button orbit, Shift-middle pan, wheel zoom and F to frame. Select bones in the hierarchy or the stage.

Use Move, Rotate or Scale; the active button stays pressed. Drag a coloured arrow, rotation ring or scale square;
red is X, green is Y and blue is Z. **World** chooses world axes, otherwise the gizmo uses the bone's axes.
Shift snaps. Numeric pose fields are available in the inspector. **Auto key** commits edits at the
playhead; turn it off to stage a pose, then **Key pose** or I to record it. Pose presets and mirrored poses are in
the Pose inspector. Rig presets create matching left/right maps for stock Spy/Merc bones.

Move drags use the camera and the bone's full parent transform, so imported rigs with centimetre-scale bones move
with the pointer. Position fields display metres in both local and World modes. Their arrows move 1 cm, rotation
arrows step 1 degree, and scale arrows change the current scale by 5%; precise typed values remain available.
The Pose inspector follows stepping, scrubbing and playback. A completed pose drag or numeric edit supports Undo
and Redo with Auto key off, without inserting clip keys. Undo can return to the edited clip and frame after seeking.

For an imported first-person weapon such as `OpenScctSidearms.desert_eagle_first`, **First person** uses its authored
eye origin and forward direction. **Frame** returns to an orbit view. The standing-character eye height applies to
third-person character rigs.

To replace a magazine motion with a few controlled poses, add an **Override layer**, select it under **Edit layer**,
and set **Mask bones** to the magazine bone (the Desert Eagle uses `mag`; press Enter to commit the mask). In Pose,
scrub to the start, set the pose and press I; do the same at the end. The layer interpolates between the keys,
instead of competing with the imported base keys. Select the layer's keys in Curves and choose Linear for steady
movement or Cubic for easing. Key the layer's Weight to zero outside the edited interval when the original motion
should resume. Check the hand grip and clearance through the magazine well; a third key can shape the approach.

For an existing dense animation, key the two corrected endpoint poses first. Select the bone, open **Curves**, choose
**Position**, and click one endpoint key then Ctrl-click the other. Click **Smooth between** to replace the original
keys inside that interval with an eased path between your endpoint values. Position changes apply to all three
axes. The endpoints, motion outside the interval, other bones and other channels retain their existing poses;
Ctrl+Z restores the entire operation. Use the Rotation or Scale filter for those channels. This also works on an
override layer, root motion, or selected rows in the dope sheet. With more than two selected keys on one track,
the first and last are the endpoints. **Cubic** alone changes interpolation while retaining intermediate keys.

The dope sheet and curve editor support Ctrl/Shift multiselection, dragging, copy/paste, deletion and Step, Linear
or Cubic interpolation. Curves automatically fits the selected bone's keys when changing clips, layers, channels,
components or panel size. **Frame keys** fits selected keys, or all visible keys when none are selected. The ruler
shows frames and seconds. Filter **Position / Rotation / Scale**, then select X/Y/Z/W; the key count helps distinguish
dense imported base tracks from a sparse override layer. Ctrl+A in Curves selects only visible keys.
Ctrl-wheel zooms time, Shift-wheel scrolls time and the wheel scrolls tracks in the dope sheet or pans values in
Curves. Alt-wheel zooms curve values. Incoming/outgoing tangent fields edit selected keys. Rotation values are normalized quaternions.
**Root motion** exposes separate gameplay root tracks; **Key root** adds transform keys there.

The Clip inspector provides trim, split, reverse, retime and loop cleanup. Tangents are adjusted when time changes.
The Layers inspector adds masked override or additive layers, static weights and keyed weights. A mask lists bone
names, comma-separated. Root motion, visual layers and event tracks remain distinct in the project.

## Rigs, retargeting and choreography

The Rig inspector edits two-bone/chain IK, pinning, look-at and parent constraints, poles, weights, offsets and
cross-rig target bones. **Pin at playhead** captures a foot/hand target; **Humanoid controls** creates named controls
for available stock limbs. These are authoring constraints: compiled clips bake their result.

**Scene items** opens the Scene inspector for independently posed instances, offsets and synchronized clips.
**Remove rig** removes a scene instance and its dependent scene links, retaining its reusable clips and model;
Undo restores the instance and links together. **Add rig** reuses the selected skeleton and clip. **Create paired
interaction** creates skeletal sequence tracks for two rigs. Cross-rig constraints need one owning instance per
clip; duplicate a clip when two instances need different constraint partners. Baking resolves dependencies in
target-first order and rejects cyclic dependencies. First-person action pairs participate in this bake too.

The Retarget inspector creates reusable named-bone maps, rest-pose correction and scale correction. Review the map,
then retarget to a new clip. Check hand/foot alignment and any required scene offsets before exporting.

## Graphs and sequences

The Graph tab edits clip players, 1D and 2D blend spaces, masked layers and additive nodes. Wire pose outputs into
blend inputs and edit blend points/masks in the inspector. The **States** toggle shows draggable states and
transition arrows. Connect states to add a transition, then edit its typed parameter, condition, threshold, earliest
exit time and blend duration. Preview exposes parameters, live state and input weights. 2D blending uses normalized
inverse-distance weights. Custom graphs bind to `actor:ActorId`; player and weapon state machines retain their
existing action selection.

The Sequence tab combines skeletal segments, object transforms, explicitly bound cameras, audio and typed events.
Edit segment start, duration, source offset, playback rate and overlap blends. Bind objects to `actor:ActorId`;
`camera:local` or `camera:all` explicitly opts into camera control. Enable **Sequence camera** to preview it. Stopping
or interrupting restores the previous view. Imported WAV/Ogg audio is copied into compiled content. Event edges
include markers, trigger/untrigger, supported script notifies, sound/stop sound and attachment switching.

Bind a sequence to a trigger tag in the sequence inspector. Wire that tag from the map's Connections / Event
Sequencer workbench. Authority runs sequence events, graph transitions and collision-bearing object movement;
clients receive playback state and phase. Late joins receive current state without replaying earlier events.

## Save, export and test

Save into a versioned **`.osanim` folder**. `animation.json` holds stable asset IDs and editable data; `assets/`
contains copied dependencies. Saves flush a pending file, replace the document atomically and retain `.previous`.
Undo/redo is document-local. Dirty documents autosave every minute; **Recover** opens a recovery JSON as an unsaved
document. **Save As** copies dependencies as well as the document.

**Export** writes GLB/glTF or `.osanimpack`. Directly representable tracks retain their keys; layers, constraints and
mixed interpolation bake at 60 samples/sec by default. **Export samples / s** in the Clip inspector, or the CLI's
`--rate`, changes export sampling. A compiled
pack includes native timing, events, root tracks, graphs, sequences, required digests and runtime version 1.

**Add to Map** copies the project to the saved map's `assets/animations/<project-id>.osanim`. Build Map compiles it
into the SVMap manifest. **Place prop** creates an SAnimatedMesh with the selected rig, a custom graph and an explicit
actor binding at the map cursor. Switch to Map to position it; map Undo removes that placement. Use Build/Play Map
for the assembled scene. Reattach after animation edits to update the map's copy.

**Test in OpenSCCT** builds a private overlay and launches the game-side animation preview. **Play replacements**
builds a private practice room and launches it as a local TestRange session with that overlay selected, using the
current first-person role. No installed development TestRange is required. Bind the desired
actions first. Neither test writes installed stock content. Installed game builds must report Animation Studio
runtime capability; source runs use the configured sibling game checkout. For custom props and map triggers, use
the map build/play path. Exported packs are imported and explicitly selected under the game's Content screen;
selection cannot change during a session. The selected pack participates in the connection content hash.

Bindings resolve stock, selected session pack, then map overrides. Duplicate target/slot bindings within one source
are build errors. **Visual** is the default and fits authored motion to stock slot timing, preserving stock events,
root movement and hit volumes. **Gameplay** is explicit and uses authored duration, supported notifies, root tracks
and collision poses through the existing providers. It does not add arbitrary gameplay scripting.

## Development and checks

Source builds use `tools/Sync-OpenScct.ps1` to get the pinned runtime. An explicit
`-p:OpenScctRoot=<animation-runtime-checkout>` also works. `tools/Start-AnimationStudio.ps1` starts the source editor
with an optional runtime/content root and project. Released Animation Studio builds require OpenSCCT 0.8.116 or newer.

`tools/animation-checks/AnimationChecks.csproj` exercises native contracts, history/recovery, every importer,
export/bake, first-person actions, cross-rig constraints, events, content digests and map compilation. Pass Godot,
the editor app folder and a converted content root to also run the real FBX helper, two-rig sampling profile and
generate editable examples/runtime overlays. Generated stock-derived examples stay local and are not distributable
as repository fixtures. `studio-shots.json`, `stock-shots.json` and `first-person-shots.json` exercise rendered
desktop/laptop/scaled layouts through the background screenshot runner.

Automated checks do not establish original-game visual parity or human multiplayer acceptance. Mesh modeling,
skinning/binding, facial tooling, original `.ukx`/`.sdc` animation export, runtime procedural IK and automatic online
pack distribution remain outside this studio.
