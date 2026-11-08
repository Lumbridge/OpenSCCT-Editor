Run `dotnet run --project tools/ge-snap-checks -- local/ge-snap/Handles.osmap` to check GE-only and mixed selection
snap shapes, exact point contact, move/resize/turn solves, undo/redo and baked collision with zero-size bounds.
Pass `-p:OpenScctRoot=<checkout>` when the default dependency checkout is unavailable.

For real viewport input, start that sample with `tools/Start-BackgroundEditor.ps1` in a dedicated run folder, then
run `python tools/ge-snap-checks/check_handles.py <run-folder>`. It checks endpoint and facing dots, grid fallback,
Alt override, cancellation and the ordinary Move/Rotate/Scale gizmos. Use a fresh disposable editor; nothing is saved.
Stop the background process returned by the startup script after the checks.
