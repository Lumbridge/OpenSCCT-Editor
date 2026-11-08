# Doors and reusable pieces

Open **Doors** from the building kit, toolbar or Tools menu. Choose Plain, Panelled, Industrial, Glazed, Louvred or
Security, then set the dimensions. **Place Door** previews a closed door and its open position. Click at the bottom
of an editable wall to place it and cut its opening. Dragging a preset into a view uses the same preview and rules.
On a wall, **R** flips facing; away from walls it rotates 90 degrees. **Escape** cancels the preview.

The door includes a three-sided frame without a threshold. Swing left/right pivots around the corresponding jamb;
Slide left/right and Raise move out of the opening. Travel 0 chooses the full opening plus frame width. Reverse
opening swaps the movement direction. Leave space for the entire movement, including the door's thickness.
Retained baked walls need an existing opening; recover editable brushes to cut new doorways.

Select a door to edit its style, size, materials, movement, timing and activation in **Inspector → Door**. Activate
**Scale** to use its Width and Height handles. Height changes through Inspector or these handles retain the floor
height. **Show open / Show closed** inspect the poses; the frame remains at its closed location. Close the preview
before using dimension handles. Each placement, setting change and completed resize can be undone and redone.

**Contact · timed** opens on player contact and closes after Stay open seconds. Other activation modes require a
switch or trigger linked to the door's Tag. The **Door with a switch** prefab supplies the doorway cut and a switch
already linked to its generated door. Each instance owns its event names. The switch model uses shared gameplay
content; the door mesh itself needs no content pack. An optional mesh parameter keeps support for custom meshes.
The **Mover** workbench offers detailed keys and a timeline; changing Door settings rebuilds its two movement keys.

**Help → Tutorials** includes Create and edit doors, Place a door with a switch, Browse and customise prefabs and
Save reusable prefab assemblies. Search `tutorial door` in Ctrl+Shift+P, or click **Learn door tools** in Doors.

## Prefab browser

The 42 built-in pieces include furniture, shelving, crates, pallets, barrels, technology, industrial details, rails,
columns, platforms, lights, doors and gameplay assemblies. Common props have generated finishes and work without
map asset packages. The gameplay devices retain their shared-content requirements.

Search names and descriptions, filter categories and libraries, or use Favourites. Tiles show shaded geometry,
including actual available mesh assets and visible traversal geometry. Settings remain with each prefab while
browsing and resizing the panel; Reset settings restores defaults. Drag to place on a surface or double-click to
place at the view centre. Select a part to update or break an instance; Apply to selected rebuilds it with the shown
settings. Updating replaces hand edits, so break a prefab before keeping individual changes to its parts.

## Double doors, hacking and side access

**Hinged pair** and **Sliding pair** in Doors open their corresponding prefab. Both include two leaves, one frame
without a centre post, and independently configured side sensors. **Automatic access** provides the same rules for
a single leaf; **Hack panel** starts with both automatic sides disabled and a five-second hack requirement.

`frontAccess` controls the +X side and `backAccess` the -X side. Each accepts `spies`, `mercs`, `both` or `none`.
The sensors occupy separate nonblocking brush volumes on their respective sides of the wall and use the game's
class filters. Rotate the whole prefab to orient these sides. Without a hack panel, all leaves stay open while an
allowed player is inside a sensor and close after the last allowed player leaves.

All four assemblies can enable `hackPanel`. `hackTime` sets uninterrupted hacking seconds, `hackState` chooses a
timed or permanent opening, and `stayOpen` controls the timed opening. Set both automatic sides to `none` to require
hacking; an allowed team can otherwise use its automatic side without hacking. Cancelled hacks fire no door event.
The default panel model needs shared gameplay content. Panel side, height and offset set its initial position;
move or rotate the placed panel freely afterward. Its event link stays with the same leaves. Inspector's Door hack
panel section changes its duration without repositioning it. Applying prefab parameters rebuilds the assembly,
including the initial panel position.

Drop a material on a leaf to update its saved finish. A prefab pair receives the finish on both leaves as one
undoable edit. Drops on a single-door frame change Frame material; a pair's shared frame is editable brush
geometry. Custom finishes persist through resizing, save/reopen and builds. Painting preserves movement keys,
timing and event links. The built-in Configure door hacking and team access tutorial covers these workflows.

## Saved design and build output

An actor's optional `door` record stores its design. Projects with doors use `osmap/4`, so older editors reject them
instead of dropping the design. Other maps retain their existing version. Cloning, clipboard, project-local prefabs
and library prefabs preserve the record. At build time the leaf becomes a normal `Engine.Mover` with a generated
static mesh, while its permanent frame and owned opening become brushes. Removing the authored door restores the
wall opening with the rest of that undoable edit. Generated assets rebuild from saved data in a new process.

Editable doors currently target OpenSCCT builds; original-game exports report the unsupported design. No new game
actor class or SVMap format is required. Build and play to check movement clearance and collision from both sides.
