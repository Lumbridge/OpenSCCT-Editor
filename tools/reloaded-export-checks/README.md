# Reloaded folder export checks

Run `tools/sdc-asset-checks` first to create its custom mesh, texture and WAV fixture, then:

```
dotnet run --project tools/reloaded-export-checks -p:OpenScctRoot=<checkout> -- <content-root> <Reloaded-install> [output-folder]
```

Checks both native map layouts, map-specific texture/mesh/sound packages, native skeletal/animation repacking,
unchanged opaque payloads and lazy-array offsets, interface mode ids/pictures, missing-texture warnings,
unrelated-file protection, repeated exports, and preservation of the previous complete folder after failure.
The installed `MAL_anm.ukx` is read only; outputs stay under `local/` by default.

Native container validation must preserve the original folders and extensions. Run:

```
pwsh tools/reloaded-export-checks/Check-NativePackages.ps1 -GameRoot <Reloaded-install> -Bundle <export-folder> -MapId <id>
```

This creates a disposable copy of the native editor and its assets, explicitly loads `<Map>-i.utc` as File Open does,
opens the editable map, creates a new map, and opens it again. It runs hidden and closes only its own process. A C++
compiler is required. Diagnostics remain under `local/`; no installed file is changed. Renaming UTC dependencies to
USX is insufficient: the native file manager selects decompression by extension. UTC and SDC use the chunked zlib
container; USX, UKX, UAX and UTX contain raw Unreal packages.

EscherAlpha's native interface/map loading and reopening passed with the original package extensions using the
Enhanced SCCT Versus 4.0 Beta 5 installation's editor. The earlier RE+ 2.8.1 open/rebuild/save/reopen test used dependencies renamed to USX; it verified their payloads
but missed UTC container requirements. These checks do not establish multiplayer gameplay parity.
