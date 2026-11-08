# Windows and breakable glass

Click **Windows** on the toolbar to open its palette and placement tool, or choose **Tools > Windows Palette**.
The palette is available in either workspace, with **Framed Windows** and **Glass Only** tabs.
Choose a rectangle, circle, oval, arch, triangle or regular polygon, with clear, tinted or
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

OpenSCCT rebuilds the affected direct and bounce lighting when a pane breaks, removing that pane's colour and
attenuation while keeping the frame's shadow and any other panes in the light path. Connected clients and late
joiners use the replicated break state. Rebuild maps with this Editor and use OpenSCCT 0.8.112 or newer for this
response; older builds retain their static lighting. Dynamic and switchable lights do not project coloured glass
shadows in this version.

In the game, planar glass breaks into irregular fragments clipped to its actual outline, with smaller chips near
the impact and larger pieces farther away. Fragments fall, bounce and fade; the frame stays intact. The ordinary
`SVitre` damage, sound, events, collision and replication rules still apply.

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
The optional `render/window-lighting.json.gz` stores sparse light-path, padding and bounce contributions for panes;
the runtime recombines these cached values when glass breaks or resets, without tracing light rays during play.

Default new windows are 160 × 128 units (circles: 160 diameter), with 2-unit glass, 0.5-unit edge clearance and an
8-unit-wide, 16-unit-deep neutral frame. Generated glass finishes use translucent colour; frosted glass adds a cloudy
texture. They do not refract or blur the scene.

SDC exports automatically replace editable panes with the original game's standard rectangular breakable glass
(`SBase.SVitre`, `SGameplayObjects.Objects.F_vitreSolo01`), scaled to the pane bounds. The export keeps the pane's
world position, rotation, tags/events and permanent frame/cut brushes. Custom outlines, glass thickness, tint and
frosting become standard glass; **check the fit in non-rectangular openings**. Generated frames use the original
default material. The project keeps its original editable window design. The original install and converted
shared gameplay mesh must be available; missing dependencies identify the affected windows and how to restore them.
T3D and handoff exports still require manually replacing editable windows.

An SDC result dialog separates automatic replacements, errors and warnings. Required user actions, including
rebuilding in RE+, are bold in the dialog, Map Check and the saved Markdown report. Copy the report or open Map
Check to select the affected objects. The report is saved beside the destination as `<map>.sdc.export-report.md`,
even when export fails. Validation and native read-back finish before an existing SDC is replaced. Errors preserve
the previous SDC and include workarounds where available.

## Verification

Run `dotnet run --project tools/window-checks/WindowChecks.csproj -- local/windows-review --network --render`.
The optional render requires an installed OpenSCCT executable and runs in a background window without taking focus.
The checks cover geometry, persistence, source fitting, movement/damage collision, managed cuts and client replication.
They write a gallery project, imported-frame project and game render under the supplied output folder.

The screenshot scripts `tools/window-checks/ui-shots.json`, `fit-ui-shots.json` and `place-ui-shots.json` run through
`tools/Invoke-EditorShots.ps1` against those projects. They cover the palette/Inspector at 100% and 150% scale and the
actual right-click insertion, wall placement, undo and redo workflows.
