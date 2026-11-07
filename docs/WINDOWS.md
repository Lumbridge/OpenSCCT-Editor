# Windows and breakable glass

Open **Tools > Windows Palette**, or choose the Windows dock in either workspace. The palette has **Framed Windows**
and **Glass Only** tabs. Choose a rectangle, circle, oval, arch, triangle or regular polygon, with clear, tinted or
frosted glass. Search filters the preset names and finishes. Drag a preset into a view, or choose **Place Window**
and click in the view; **R** rotates its preview and **Escape** cancels.

Framed presets dropped on an editable wall cut a matching opening, including its curved outline. Their frame depth
increases to span the wall and leave visible trim on both sides. The frame, glass
and cut remain one authored window. Moving, resizing or deleting it updates the cut too; undo restores the whole
operation. Retained baked world surfaces cannot be cut. New editable walls added to a baked map can be cut.

Select a window to edit its shape, width, height, glass thickness, clearance, depth, finish, tint and materials in
Inspector. Circles keep one diameter. **Scale** (Shift+S by default) also shows orange dimension handles. Existing
stock glass actors keep their original mesh and gameplay properties and still use ordinary scaling.

The tint picker stays open while you adjust or drag the colour. The pane updates live; each pick or drag is one
undo step. Click outside to keep the colour, or press Escape to cancel the current picker session.

Coloured panes filter baked light onto world surfaces and static meshes, using the pane's actual outline. Their
tint multiplies the light's colour; clear glass passes the most light, tinted glass less, and frosted glass less
again. Rebuild lighting, or wait for Lit Preview's final pass, to see the coloured shadow. Fast builds skip shadows.
Imported maps retaining their original lightmaps need **Relight imported** enabled to update those surfaces.

This is baked lighting: the coloured contribution stays static during play, including after glass breaks. Dynamic
and switchable lights do not project coloured glass shadows in this version.

## Fill an existing frame

Right-click **inside the empty opening**, then choose **Add Window** at the top of the menu. This works even when the
pointer ray passes through to a wall behind the frame. The tool uses the frame's actual closed outline, including
holes in imported static meshes and frames assembled from separate brushes. It fills only the opening clicked in
a frame with multiple panes. Hidden or locked frames are ignored, and an existing pane prevents duplicate glass.

One clear opening gets glass immediately. Ambiguous openings show a highlighted preview and an opening selector,
with clearance and depth controls; choose **Add Window** or click the view to accept. An open-sided or invalid frame
must be repaired before it can be fitted. Imported meshes with their frame plane rotated inside the asset are also
detected from their surface normals.

Fitted glass follows its source frame's move, rotation and scale. Resize the frame to resize its glass. **Refit**
detects the current opening again; **Detach** keeps the pane's current shape and stops following the frame. Changing
an individual frame bar or replacing the source mesh flags the pane for Refit in Inspector and Map Check. Deleting
the frame deletes its linked panes in the same undo step. Copying an entire assembly remaps the links; copying only
the pane makes an independent copy.

## Storage and builds

An optional `window` record on each glass actor stores its `WindowDesign`: shape/dimensions or detected outline,
appearance, frame and managed cut settings, source-local fit controls and source references. It lives in the normal
actor JSON and travels through cloning, the clipboard and prefabs. Projects containing it use `osmap/3`; projects
without it retain their existing format. Older project formats remain readable.

The pane is built as an ordinary `SBase.SVitre`, with a generated explicit static mesh and matching collision.
Permanent frames and owned cuts lower to ordinary brushes. Frame meshes used for fitted panes get collision from
their visible triangles, so a coarse imported hull cannot block the opening after glass breaks; this also survives
Detach. The output packages its generated meshes, textures and materials, with no new gameplay class or SVMap
format. Disposable mesh files in the system temporary cache can be regenerated from the saved design.

Default new windows are 160 × 128 units (circles: 160 diameter), with 2-unit glass, 0.5-unit edge clearance and an
8-unit-wide, 16-unit-deep neutral frame. Generated glass finishes use translucent colour; frosted glass adds a cloudy
texture. They do not refract or blur the scene. Original-game SDC, T3D and handoff exports report an explicit error
for these windows; use an OpenSCCT build.

## Verification

Run `dotnet run --project tools/window-checks/WindowChecks.csproj -- local/windows-review --network --render`.
The optional render requires an installed OpenSCCT executable and runs in a background window without taking focus.
The checks cover geometry, persistence, source fitting, movement/damage collision, managed cuts and client replication.
They write a gallery project, imported-frame project and game render under the supplied output folder.

The screenshot scripts `tools/window-checks/ui-shots.json`, `fit-ui-shots.json` and `place-ui-shots.json` run through
`tools/Invoke-EditorShots.ps1` against those projects. They cover the palette/Inspector at 100% and 150% scale and the
actual right-click insertion, wall placement, undo and redo workflows.
