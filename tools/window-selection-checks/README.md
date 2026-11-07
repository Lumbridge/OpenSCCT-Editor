# Window selection and tutorial UI checks

Generate disposable fixtures:

```powershell
dotnet run --project tools/surface-checks/SurfaceChecks.csproj -p:BuildInParallel=false -- local/window-picking/Door.osmap local/window-picking/Selection.osmap
pwsh tools/Start-BackgroundEditor.ps1 -Map local/window-picking/Selection.osmap -Out local/window-picking/ui -Godot <Godot .NET executable> -NoBuild
python -B tools/window-selection-checks/check_selection.py local/window-picking/ui
```

The test connects only to that background editor, sends synthetic clicks to all six panes and their frames from
both sides, checks surrounding walls and hidden/locked panes, and tests tutorial close, Hide guide, save/reload and
reopen at 100% and 150% UI scale. It saves only the disposable fixture. It captures screenshots for visual review.
Stop the background process returned by the startup script after the checks.
