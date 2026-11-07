# Play launch checks

Run `dotnet run --project tools/play-checks/PlayChecks.csproj -p:BuildInParallel=false`.

Checks that volumetric smoke enables the existing game match rule while preserving side, mode, rules, bot settings,
player count, content paths and custom starts. A later ordinary launch keeps the ordinary smoke behavior.

`ui-shots.json` checks the Build dropdown in both workspaces and at 150% UI scale through `tools/Invoke-EditorShots.ps1`.
