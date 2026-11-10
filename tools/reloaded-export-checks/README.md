# Reloaded folder export checks

Run `tools/sdc-asset-checks` first to create its custom mesh, texture and WAV fixture, then:

```
dotnet run --project tools/reloaded-export-checks -p:OpenScctRoot=<checkout> -- <content-root> <Reloaded-install> [output-folder]
```

Checks both native map layouts, map-specific texture/mesh/sound packages, native skeletal/animation repacking,
unchanged retained payloads and lazy-array offsets, unused asset removal, interface mode ids/pictures,
missing-texture warnings, native-safe BSP materials, group visibility, baked brush transforms, no empty folders,
stable filenames across asset edits, unrelated-file protection, and preservation of the previous complete folder after failure.
The installed `MAL_anm.ukx` is read only; outputs stay under `local/` by default.

Native container validation must preserve the original folders and extensions. Run:

```
pwsh tools/reloaded-export-checks/Check-NativePackages.ps1 -GameRoot <Reloaded-install> -Bundle <export-folder> -MapId <id>
```

This creates a disposable copy of the native editor and its assets, explicitly loads `<Map>-i.utc` as File Open does,
opens the editable map, creates a new map, opens it again, and waits through viewport rendering before passing.
Use `-EditorDll <file> -ExpectedEditorSha256 <sha256>` to lock the exact native or RE+ binary. Use `-Rebuild`
to rebuild/save/reopen, and `-ExpectedGroups Lighting,Architecture` to check the native group's visibility cache.
It runs hidden and closes only its own process. A C++
compiler is required. Diagnostics remain under `local/`; no installed file is changed. Renaming UTC dependencies to
USX is insufficient: the native file manager selects decompression by extension. UTC and SDC use the chunked zlib
container; USX, UKX, UAX and UTX contain raw Unreal packages.

Native and RE+ are different validation targets. A previous test used a swapped-in RE+ DLL and ended before
viewport rendering, so it did not establish native compatibility. The native DLL SHA-256 for Enhanced SCCT Versus
4.0 Beta 5 used here is `A963C82C16CAB95EEF967E37E9FFCB651C25522F6FCDE77E68ACDC79E3C98B2B`.
Its blended BSP cleanup can free frame-owned buffers; folder exports make opaque BSP-only shader variants and
report the visual change. Static-mesh shaders retain their blending. These checks do not establish multiplayer gameplay parity.
