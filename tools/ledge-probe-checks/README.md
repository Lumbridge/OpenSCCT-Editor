# Ledge probe checks

Run `dotnet run --project tools/ledge-probe-checks -c Release` against the editor's pinned OpenSCCT dependency.

This builds a small CSG room and stone ledge in memory. It checks short and long floor rays, a nearby miss, lip fitting and hang clearance. The pre-fix collision dependency misses the short rays even though they cross the same surface. Physics commit `8b2700f6` uses a direct front-face line/triangle intersection rather than independently rounded zero-thickness SAT slabs. Box sweeps and static-mesh native line rules are unchanged.

This regression creates no files and does not save a map. It complements the physics tests and an exported-editor check on a real project.
