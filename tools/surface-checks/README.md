# Surface selection regression checks

Run `dotnet run --project tools/surface-checks/SurfaceChecks.csproj -p:BuildInParallel=false`.

Exercises the app's actual surface picker against the CSG preview of two rooms connected by a doorway:
independent jamb and soffit selection from both rooms, clicks through the opening, clipped wall highlights,
per-face material edits and undo/redo, hidden and locked brushes, cut removal/restoration and transformed layouts.

Also checks all six generated window shapes against the actual CSG wall openings: pane/frame selection from both
sides before assets load, empty shape corners, transformed panes with offset pivots, and real foreground occlusion.

The first optional output argument writes the doorway layout as an `.osmap`; a second writes the window layout for
`tools/window-selection-checks/check_selection.py` in a separate background editor.
