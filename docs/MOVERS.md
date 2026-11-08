# Doors, platforms and mover paths

Create a working starting point from **Gameplay > Create Mover**: Sliding door, Rising door, Swinging door,
Moving platform or Turntable. These create visible solid geometry and a two-stop motion. Sliding/rising doors
open when bumped; a platform moves when stood on; a turntable toggles when triggered. They remain editable actors.

Select a static mesh or an additive brush and choose **Gameplay > Movers > Make Selection Move** to use your own
appearance. This preserves the object's name, incoming references, surface materials and world placement. A brush
leaves the static world and becomes a moving object. Undo restores the original brush and its CSG order. Locked
objects, parametric stairs, subtractive brushes and brushes still baked into an imported world cannot be converted.
**Brush > Add Mover** uses a single builder-brush shape instead.

## Place the stops

The Mover panel is in the right dock. Stops are numbered from **1** in the panel and path overlay; scripting and
original-game properties keep their zero-based key numbers.

1. Choose a stop with the number field or previous/next arrows.
2. Choose **Edit in view**, then move or rotate the mover using the normal viewport tools. This also works on the
   starting stop. Typed Position is relative to the mover's base; Rotation is in degrees.
3. **Move whole mover** leaves stop editing and returns to the round's starting pose. Moving it now moves its base
   and the entire path together.
4. **Extend path** continues the final segment. **Duplicate stop** inserts after the chosen stop. **Add at view
   centre** inserts a stop at the active view's placement point. Earlier/Later reorder stops; their timing and the
   round's starting stop follow them. Delete and every other edit support Undo.

Travel time belongs to the segment before the selected stop, in either direction. The first stop has no preceding
segment. Reset returns to Default travel. Easing can use the default, smooth arrival/departure, or keep speed.

## Behaviour and preview

Choose how it activates and returns under **How it works**. The description explains each state. Default travel
applies to segments without their own time. Timed states use Wait before return. More options contains opening
and closing delays, repeated-motion hold time, wiring details and motion presets. **Replace motion** explicitly
replaces the existing path and timing; it keeps the appearance and can be undone.

**Add a connected switch** places a switch at the active view's placement point and wires it to this mover as one
undo step. Move/mount the switch using the normal tools. Its default model comes from the gameplay asset pack.
Select existing senders with the mover and use **Selected actors trigger this** under More options to wire them.

Start, Finish and Play/pause preview one activation without editing the map. Click the timeline to scrub, or drag
a travel segment's end to set its duration. Stop returns to the editing pose. This is a motion preview: it does not
simulate player collision, crushing, or a complete event sequence with repeated triggers and releases. Play the map
to check those interactions.

## Longer paths and compatibility

OpenSCCT paths are no longer tied to the original game's eight-element arrays or byte indices. The native data
supports up to **65,536 stops**, a resource guard rather than the original gameplay limit. Only the chosen stop has
editing fields; ghosts show nearby stops and endpoints, and long paths sample their text labels.

The project retains the full path in its normal mover properties. Build writes paths longer than eight stops to
`sim/mover-paths.json`; per-stop timing remains in `sim/movers.json`. Builds normalize geometry and actor poses to the configured starting stop without changing the stop shown in the editor. Server and clients load the same poses before
BeginPlay and replicate integer stop indices. Use a game build containing this native path support on both sides;
previous game releases do not implement it. Importing the SVMap restores the complete path and timing.

The original game still supports eight keys. Original-editor, T3D-file and `.sdc` export **refuse** longer paths
before touching the destination, so a route is never silently truncated. Per-stop timing is also OpenSCCT-specific;
legacy export of an eight-stop path keeps the mover's default timing and reports the timing loss.

## Verification

`dotnet run --project tools/mover-checks/MoverChecks.csproj` covers presets, conversion/undo, independent viewport
editing of the first and later stops, insertion/reorder/timing, save/reopen, legacy export safety, compile/import,
and 300-stop host/client opening and return with zero and 100 ms simulated latency. It writes a workshop map under
`local/mover-checks`. `tools/mover-checks/shots.json` exercises the app through the background screenshot runner.
