# Beginner workflow checks

Run `dotnet run --project tools/beginner-checks/BeginnerChecks.csproj` for guide persistence and backward compatibility,
undo/redo, content-derived completion, missing dependencies and independent actor/event connections in all eight
featured prefabs. Add `-- --compile` to build a two-room tutorial with an objective and validate it through
OpenSCCT's strict loader, using installed content. The compiled terminal is also loaded into the real gameplay
simulation: its Story mission must reveal it, activate it and enable its interaction. The wall lesson checks a
manually assembled doorway/corridor/room chain, undo and persisted progress.

Run `pwsh tools/Invoke-EditorShots.ps1 -Script tools/beginner-checks/ui-shots.json -Out local/beginner-review` for a
fresh-profile visual pass at normal and 150% scale. It checks the new workspace, R selecting Rotate, guide actions,
prefab settings, objective placement and restoring the two workspaces. Review the PNGs and manifest, including the
recorded active `tool` for the R step. Use a new output folder for each fresh-profile run.

To check migration, seed `<out>/appdata/OpenSCCT Editor/settings.cfg` with `[views] single="Top"` and custom
`[shortcuts] tools.rotate="E"`, `tools.scale="R"`, then run into that output folder. Its first shot should retain
Top, Outliner and Inspector with Advanced enabled; the R step should record `scale`.

Gameplay validation also needs a real game launch. A headless `ose play <tutorial.osmap> --headless --bots 1`
checks loading and spawning. A route to its computer checks accessibility. These do not establish hacking,
door/switch operation, camera detection, laser alarms, traversal usability or human learning time. Test those in-game
and observe three first-time users before claiming the 15-minute usability target.
