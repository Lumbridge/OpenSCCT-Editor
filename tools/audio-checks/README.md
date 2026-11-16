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

## Mixer, scrubbing and visualization regression

`-- --audio-checks=<absolute-result.json>` runs the opt-in checks inside an isolated
background editor opened on the SoundDemo fixture. The checks use the actual game
mixer and native extension, measure its output bus, exercise the real slider and
waveform, move the listener in/out of reach and add disposable room/wall geometry
to verify occlusion, corner propagation and adaptive reverb. They save a screenshot
and a JSON result and exit. They never save their edits to the fixture.

For example, after building the editor:

```powershell
./tools/Start-BackgroundEditor.ps1 -Map local/audio-checks/SoundDemo.osmap `
  -Out local/audio-runtime -NoBuild -Godot <Godot.exe> -Wait `
  -EditorArgs @('--audio-checks=C:/absolute/path/result.json')
```

The Sound Paths switch shows selected sources even when the camera is outside
hearing range. Green lines come from live mixer arrival positions; blue outlines
sample direct reach against the same geometry; amber lines trace up to four reflections in 3D, fading with the game
material absorption model and distance gain. High-frequency loss also changes
their colour. They are not captured Steam Audio reflection rays or a full
acoustic pressure field. Imported maps use their BSP zones, portals and material
collision data. Editable CSG reads the authored surface material where available, using the generic
material as fallback. Placed mesh collision joins the queries once the background
traversal cache is ready. Actors are evaluated in their authored state, without
running gameplay.
