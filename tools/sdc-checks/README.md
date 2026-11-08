# Direct SDC export checks

Run `dotnet run --project tools/sdc-checks -c Release -- <content-root>` with the editor's pinned OpenSCCT
dependencies (or `-p:OpenScctRoot=<checkout>` before `--`).

Checks both layouts and the schema-free writer: every fresh actor, including LevelInfo itself, must serialize its
Level reference to this map's LevelInfo. Also checks that the export defaults to the playable cooked layout and
refuses an editor copy in Packages/Maps without overwriting an existing file.

Native validation on 2026-11-08 used Enhanced SCCT Versus 4.0 Beta 5 / Reloaded 4.0.4. The original cooked starter
crashed at 0x10A29872 when Actor.Level was null. With the reference written, both teams spawned with 100 health,
walked across the room, and kept ticking; Spy standing and a D3D11 gameplay frame were also checked. The game rebuilt
the starts' Region.Zone references to LevelInfo0. Files went directly from the native SDC writer to the game; RE+
did not import, build or save them. This verifies the starter, not every imported/custom map feature.

For unattended native testing, the game's startup accepts only an Autoplay.sdc URL with GameMode, team, PLATFORM
and HWND arguments. Stage a copy in an isolated game install's Packages/Maps/Autoplay.sdc and use its launcher with
`Autoplay.sdc?GameMode=5?team=0?Editeur=true PLATFORM=XBOX HWND=0` (team 1 for Merc). Keep the launcher, game and
Reloaded.Core together. The 4.0.4 launcher supplies the child's executable token itself. Game-derived artifacts
belong under local/ and are not committed.
