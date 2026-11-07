# Floor and ceiling connectors

`dotnet run --project tools/surface-connector-checks/SurfaceConnectorChecks.csproj -- local/surface-connectors/Connect.osmap`
checks floor/ceiling anchors, yaw rotation, additive slabs, actual slab depth, connected rooms, hidden/locked
surfaces and whole-operation undo/redo.

Start the generated fixture with `tools/Start-BackgroundEditor.ps1`, then run
`python tools/surface-connector-checks/check_ui.py <background-run-folder>`. The UI checks exercise the contextual
menus, stable hover badge, hatch placement and undo/redo. They also check icon-only palette buttons and the tutorial
tab's default-hidden state, middle/right close and reopening at 100% and 150% scale. Use only a disposable background
editor: the script saves and reopens its fixture and changes its workspace settings.
