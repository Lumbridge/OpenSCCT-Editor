# Contributing to OpenSCCT Editor

You can help by reporting bugs, testing a release, improving a guide or sending a code change.

## Report a problem

Search [existing issues](https://github.com/Lumbridge/OpenSCCT-Editor/issues) first, then
[open a report](https://github.com/Lumbridge/OpenSCCT-Editor/issues/new) with:

- The editor and OpenSCCT versions, and your Windows version.
- The map, and steps another person can follow: what you expected and what happened.
- The editor's log, and a screenshot if it helps.

Remove tokens, passwords and personal information from logs. Do not attach original game files, converted maps,
converted asset packs or decompiled output. A map made from scratch that shows the problem is the best attachment.
Problems with the game itself go to [OpenSCCT](https://github.com/Lumbridge/OpenSCCT/issues).

## Make a change

1. Build from source as the [README](README.md#build-from-source) describes.
2. Create a focused branch, such as `fix/snap-release` or `feature/prefab-search`.
3. Read the [architecture](docs/ARCHITECTURE.md), and the [project format](docs/FORMAT.md) when you touch the files
   the editor writes.
4. Make the smallest complete change, and update affected docs (a new command goes in [docs/CLI.md](docs/CLI.md), a
   new scripting method in [docs/SCRIPTING.md](docs/SCRIPTING.md), a feature's status in
   [docs/PARITY.md](docs/PARITY.md)).
5. Build both solutions with no warnings, and check the change in the editor. Record anything you could not test.
6. Open a pull request explaining the problem, the resulting behaviour and how you checked it.

Keep unrelated fixes in separate pull requests. A screenshot helps reviewers assess visible changes.

## Code and provenance

Keep the Core library free of Godot: everything the app does must also work from `ose`. Do not edit OpenSCCT's code
from here; changes to its libraries go to OpenSCCT first, then `openscct.ref` moves to the new commit.

Follow OpenSCCT's [provenance rules](https://github.com/Lumbridge/OpenSCCT/blob/main/re/PROVENANCE.md). Original game
data, converted maps and assets, traces and decompiled material stay outside Git. Code that follows another project
names it in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
