# Reloaded folder export checks

Run `tools/sdc-asset-checks` first to create its custom mesh, texture and WAV fixture, then:

```
dotnet run --project tools/reloaded-export-checks -p:OpenScctRoot=<checkout> -- <content-root> <Reloaded-install> [output-folder]
```

Checks both native map layouts, map-specific texture/mesh/sound packages, native skeletal/animation repacking,
unchanged opaque payloads and lazy-array offsets, interface mode ids/pictures, missing-texture warnings,
unrelated-file protection, repeated exports, and preservation of the previous complete folder after failure.
The installed `MAL_anm.ukx` is read only; outputs stay under `local/` by default.

Native validation also opened the generated fixture in RE+ 2.8.1, rebuilt geometry, saved, reopened and exported
T3D in a disposable install. The test install used private copies of the generated dependencies under its mesh
search path. This establishes native asset loading and map authoring for the fixture, not all map features or
multiplayer gameplay parity.
