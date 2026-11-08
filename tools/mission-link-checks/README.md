# Mission workflow checks

Generate a disposable map and run the 37 relationship, undo, persistence, compilation and game activation checks:

```powershell
dotnet run --project tools/mission-link-checks -- local/mission-workflow
```

Use the editor's pinned OpenSCCT dependencies (`openscct.ref`). When building against a separate checkout, pass
`-p:OpenScctRoot=<checkout>/` to `dotnet` and `-OpenScctRoot <checkout>/` to the background editor script.

Run `workflow-ui-shots.json` against the generated `local/mission-workflow/Ui.osmap` using
`tools/Start-BackgroundEditor.ps1 -Map <map> -EditorArgs @('--shots=<absolute-script-path>') -Wait`.
It exercises all four guided link directions, search-preserved batch checks, review/back, undo/redo, repeated device
placement, dragging a device from Missions, dragging translated and generic Classes entries, cancelling a drag, and saving.
Then verify the saved result:

```powershell
dotnet run --project tools/mission-link-checks -- local/mission-workflow --verify-workflow
```

The ten saved-result checks require a fresh generated map for each workflow run. `workflow-context-shots.json`
runs afterward against the edited map: mission, objective and device context menus, Outliner linking, viewport linking,
an empty compatible-target list, the small-window layout, completion controls and 150% text size. These scripts use
1600×1000 and 1280×720 windows and write screenshot manifests with any failed steps and layout findings.
