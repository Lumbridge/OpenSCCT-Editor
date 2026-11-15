# SDC assets and preview checks

Import a real custom map into an isolated `OSE_DATA` folder with the CLI, then run:

```powershell
dotnet run --project tools/sdc-assets-checks/SdcAssetChecks.csproj -- local/sdc-checks local/import/MoonD.osmap 'C:/path/to/Packages/Maps/MoonD.sdc'
```

Checks that the imported materials, mesh images, skeletal meshes, sounds and composite dependencies resolve without
the original installation; that report findings survive reopening; and that first save and Save As carry the assets
and retained source package. Also reproduces the shared-WAV path problem using the installed `AirDoor_Open` clip.
It creates a separate copy of the source map with one damaged map-local asset for testing unsupported reporting.
Import that fixture and check `import.assetUnsupported`; the import should continue and preserve the original bytes.

For the UI, generate the disposable SoundDemo with `tools/audio-checks`, start it with
`tools/Start-BackgroundEditor.ps1`, and run:

```powershell
python -B tools/sdc-assets-checks/check_ui.py local/background-run
```

This checks the sound list height, the real subsecond clip's waveform and playback completion, Rising bubbles framing,
and dragging an emitter into the viewport followed by one undo. Screenshots come from the editor renderer.
Background sessions use a dummy audio driver; audible output quality requires a listening check.
