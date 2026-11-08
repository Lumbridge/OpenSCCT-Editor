# Mission linking checks

Run `dotnet run --project tools/mission-link-checks`, with `-p:OpenScctRoot=...` when the pinned game checkout is
not in the default location. An optional argument after `--` chooses the output directory.

Checks batch linking, case-insensitive duplicates, invalid links, direct and indirect mission cycles, unlinking
without deleting actors, chained ordering, imported single/missing references, atomic creation, undo/redo and
save/reopen. Compiles the resulting map, loads it with the game's own map loader and verifies the first linked
computer becomes usable while the second chained objective stays inactive.

The run also writes `Ui.osmap` with unlinked objectives and computers. Pass it to `tools/Invoke-EditorShots.ps1`
with `-Script tools/mission-link-checks/ui-shots.json` to exercise checkbox linking across search filters,
selection-based linking, creation, undo/redo, completion controls and small/high-DPI layouts. The script uses
checkbox coordinates at its explicitly set 1600×900 size. Afterward run the checker with the same output directory
and `--verify-ui` as its second argument to verify the map saved by the UI.
