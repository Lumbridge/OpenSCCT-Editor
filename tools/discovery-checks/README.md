# Command-palette and tutorial checks

`dotnet run --project tools/discovery-checks` checks task-query parsing, matching, aliases, lesson coverage,
unique IDs and all 130 step routes. It also places real computers, bomb targets and disks to verify that guidance
reuses their generated objectives, recognises automatic mission/drop-zone links and creates no duplicate actors.
Pass `-p:OpenScctRoot=...` if the default pinned dependency checkout is absent.

Build `app/OpenScctEditor.csproj`, then start a fresh disposable editor through `tools/Start-BackgroundEditor.ps1`
with a dedicated `-Out` folder and installed game content. Run:

```
python tools/discovery-checks/check_ui.py <background-run-folder>
```

The live test rejects an ordinary foreground editor. It checks laptop navigation and hover dismissal; tutorial
Back/Next/Cancel/Finish/resume/minimise; favourites; base classes; exact asset, prefab and preset navigation;
command menu location; category filters; direct Identity, texture and GE destinations; existing @ and F1 modes;
and every step of every lesson. It verifies map object counts remain unchanged and writes screenshots under the
run's `shots` folder. The caller owns stopping the disposable editor after the test.

Add `--navigation-only` for focused Simple-workspace, collapsed mission-controls and keyboard-cancel checks.

`ui-shots.json` exercises the palette and lesson cards at normal and 150% UI scale. Run it with
`--shots=<absolute-script-path> --shots-out=<output-folder>` through the background-editor helper. The app enforces
its minimum canvas size when raising UI scale, so the physical window can grow beyond the requested 1280×720.
Inspect the generated `manifest.json`, UI lint and PNGs; a successful build alone does not verify the navigation.

The UI tree includes palette result keys and kinds, active lesson and step, and the current spotlight target and
rectangle. These are read-only diagnostics for automation; navigation still uses the same real input and controls
as an editor user.
