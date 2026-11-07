# Real-map lighting equivalence and allocations

Runs a cold preview, changes one light to brightness20, then restores it. It never saves the project. Reports each
pass's exact atlas and placed-mesh colour SHA256 plus chart/texel/mesh counts, allocation bytes, GC counts and time.
Choose a visible light whose original brightness is different from20 and which affects the final bake.

```
dotnet run --project tools/light-preview-checks -c Release -- <project.osmap> <report.json> <content> <light-id> [baseline.json]
```

Capture the baseline before changing the lighting implementation. Supplying it later asserts every pass matches,
including a changed light and its restoration. Timing/allocations are diagnostic values rather than portable test
thresholds. This is the real lighting pipeline without Godot or viewport resource allocation; also exercise the
packaged editor to validate process memory and rendering. Keep map assets and measurements in ignored `local/`.
