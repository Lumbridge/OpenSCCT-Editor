# Editable staircases

Click **Stairs** on the toolbar to open the palette and placement tool, or use the left dock or Building panel. The palette
contains straight, L, U, curved and spiral starters, each with square or rounded nosings.
Thumbnails show the stair fronts, with a corner detail distinguishing the two nosing profiles.
New spiral starters have connected treads and risers rather than floating slabs.

## Place and connect

Drag a starter into a view. The preview shows the staircase, its clearance cuts, walking
direction and invisible smoothing envelope. **R** rotates a dragged preset by 45 degrees;
**F** reverses its turn. Escape cancels without adding anything. The palette's settings
apply to the captured preset, so set dimensions before dragging.

**Place stairs** starts a click-to-place preview. **Connect floors** instead takes a bottom
connection followed by an upper connection. It fits the selected layout to those points;
an impossible fit stays pending and explains which dimension needs changing. A U flight's
connections are separated by its width; a full spiral turn ends over its starting point.

Standing clearance and blocked approaches are checked against the proposed brush cuts.
Clearance cutting cannot modify a retained baked world: disable it for those maps and
provide an opening yourself. Brush cuts do not cut imported static mesh assets.

## Edit one staircase

Click any tread to select the whole staircase. It has one Outliner entry, transform and
undo history. Move, rotate, mirror, duplicate, hide and lock work on the complete object.
The bottom connection is its default transform pivot.

With **Scale** active, the orange dimension handles snap to the world grid and nearby geometry using the same
neighbour-snap settings as the transform tools. Hold **Alt** to invert neighbour snapping during a drag. Run, rise,
width, landing and radius edits update the authored dimensions; one drag is one undo step. Scaling always starts
from the geometry at the beginning of the drag, and snapping accounts for fixed nosing and landing dimensions.

Clearance cuts follow a smooth sloping ceiling along each flight. Purple handles independently adjust headroom,
side margin, and the clearance before and after the stairs; the same values are in Advanced settings. They resize
the opening without changing the stair dimensions. Clearance handles also use grid and neighbour snapping.

Use the Inspector for layout, width, rise, run, target step height, turn, smoothing and
clearance. The Scale tool also exposes width, rise and run handles, plus landing/radius
handles for the layouts that use them. Scaling regenerates dimensions instead of stretching
individual treads. Top connection X/Y/Z fields fit the staircase to a new upper connection.

Automatic step count distributes the requested rise evenly. **Advanced** includes an exact step count (0 means automatic), landing length, inner radius,
total turn and headroom. Invalid dimensions are rejected and the last valid value is kept.

Tread thickness, front overhang and edge radius apply to the whole staircase. Radius 0 gives
square edges. Rounding covers the upper and lower nosing transitions; its allowed radius
depends on tread thickness and step rise. Overhang and rounding must also fit the narrow
inside edge of a curved tread. Rounded geometry uses deterministic tessellation.

## Texture the treads and risers separately

Drag a material or texture from Assets onto a tread to change every tread, nosing and
landing top. Drop onto a riser to change every riser. The hover highlights the whole target
surface family and says what will change. Sides/undersides have their own material slot.
Each drop is one undo step, and assignments survive all dimension and layout changes.

The Advanced section exposes each role's material, texture scale, angle and U/V offsets.
Ordinary brush texture drops retain their usual per-face behavior.

## Smoothing and conversion

New stairs default to smooth pawn movement. Hidden convex blocking volumes follow the
flights and landings; rounded flights use nosing tangents so their edges do not catch a
pawn. The entrance extension is included in the preview. These volumes affect players and
bots, while shots, projectiles, camera obstruction, sight and shadows use visible geometry.

Legacy builder stairs stay unchanged until **Convert to Editable Stairs** is chosen in the
Inspector or Brush menu. Conversion validates the complete assembly, including unlinked
builder parts that can be identified unambiguously. Modified geometry, flags, UVs, per-step
material overrides, raised bases and intentionally sloped floors are refused rather than
silently lost. Ordinary blockout straight/L/U/spiral and UE linear/curved/spiral builders are
supported. Spiral columns and floating/sloped undersides are retained.

**Break Apart** converts a staircase into ordinary brushes and smoothing volumes. It is
undoable. Use it before individual face/vertex edits or clipping.

## Files, builds and scripting

Projects containing native stairs use `format: "osmap/2"`, `version: 2`, so older editors
that ignored the numeric version still reject the new format safely. Projects without
native stairs retain the existing format. `world/stairs.json` records each authored object's
transform, metadata, CSG index and design. Generated preview polygons are not saved there;
ordinary brushes remain in `world/brushes.json`.

The public reference kind is `stair`. Internally its CSG owner shares the brush command and
transform infrastructure, keeping one source of identity/order and the existing transactional
undo behavior. The generator supplies the viewport, SVMap compiler and original-game exports.
SVMap output uses ordinary geometry and blocking volumes; no runtime stair class is required.
The original-editor handoff restores editable stairs when its T3D is unchanged. If the T3D
was edited externally, its ordinary geometry is retained instead.

Create or edit dimensions/materials with the `stair` change operation:

```json
{
  "format": "osmap.changes/1",
  "ops": [{
    "op": "stair",
    "id": "MainStair",
    "at": [0, 0, 0],
    "set": {
      "layout": "straight",
      "width": 192,
      "rise": 256,
      "run": 512,
      "overhang": 2,
      "edgeRadius": 2
    }
  }]
}
```

An existing stair ID updates its `set` properties. For new stairs, optional `connectTo`
fits the upper connection; `rot` sets the initial rotator for ordinary placement. Use the
existing move/rotate/scale operations for transforms. `{"op":"stair","id":"Brush0",
"convert":true}` upgrades a legacy assembly; `breakApart:true` lowers a native stair.
Query with `kind:"stair"` or request the `stair` field through `get`.

## Verification

- `dotnet run --project tools/stair-checks/StairChecks.csproj` checks geometry, persistence,
  materials, conversion, undo, floor fitting, collision filters, bot routes, original handoff
  and both teams walking every layout standing and crouched.
- `dotnet run --project tools/stair-network-checks/StairNetworkChecks.csproj` checks actual
  client/server stair traversal for both teams at zero and simulated 100 ms latency.
- `tools/stair-checks/ui-shots.json` exercises palette dragging/rotation, selection, Inspector,
  dimension handles, cancellation and a short window at 150% scale through the editor's
  background screenshot runner.
