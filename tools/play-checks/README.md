# Play launch checks

Run `dotnet run --project tools/play-checks/PlayChecks.csproj -p:BuildInParallel=false`.

Checks the saved Original, Volumetric Fast and Volumetric Full play choices, their distinct launch arguments,
workspace save/reopen, older workspaces, scripting and undo/redo. Each choice preserves side, mode, rules, bot
settings, player count, content paths and custom starts. Full requires a game build supporting
`--svm-smoke-mode=full` for scene lighting attenuation.

`ui-shots.json` checks Build > Play Options in both workspaces and at 150% UI scale through
`tools/Invoke-EditorShots.ps1`. The labels explicitly identify all three choices as smoke settings.
