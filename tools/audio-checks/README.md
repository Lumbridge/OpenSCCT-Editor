# Sound authoring checks

Run `dotnet run --project tools/audio-checks/AudioChecks.csproj -- local/audio-checks` from the editor repository.
The console checks import, unique asset and actor names, undo, persistence, compiled sound payloads, retained-map
edits/removal and composite sound dependencies. It writes a disposable `SoundDemo.osmap` fixture with an emitter
and a two-second WAV for UI checks. It never opens a user's map.

Start the fixture with `tools/Start-BackgroundEditor.ps1`, then run
`python -B tools/audio-checks/check_ui.py <background-run-folder>` to exercise the sound controls, toolbar mute,
placement, source edits, removal, undo and drag-and-drop. Screenshots are captured from the editor's own renderer;
the script uses synthetic input and the background run's isolated settings. Stop the background process afterward.
Audio uses the dummy driver in background mode, so a real listening check is still needed for output quality.
