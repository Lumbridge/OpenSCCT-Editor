# Surface selection regression checks

Run `dotnet run --project tools/surface-checks/SurfaceChecks.csproj -p:BuildInParallel=false`.

Exercises the app's actual surface picker against the CSG preview of two rooms connected by a doorway:
independent jamb and soffit selection from both rooms, clicks through the opening, clipped wall highlights,
per-face material edits and undo/redo, hidden and locked brushes, cut removal/restoration and transformed layouts.

An optional output argument writes the test layout as an `.osmap` for visual and input checks in a separate editor.
