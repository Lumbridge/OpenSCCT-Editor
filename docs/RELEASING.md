# Releases and updates

Releases are published at [github.com/Lumbridge/OpenSCCT-Editor/releases](https://github.com/Lumbridge/OpenSCCT-Editor/releases).
Each is one zip people download and the editor updates itself from. There is no installer.

## What a release holds

The tag is `v<version>`: `v0.2.0`, or `v0.3.0-beta.1` for a pre-release (published with GitHub's pre-release flag).
Its assets:

| Asset | What it is |
| --- | --- |
| `OpenSCCT-Editor-0.2.0-win-x64.zip` | The download. |
| `OpenSCCT-Editor-0.2.0-win-x64.zip.sha256` | Its SHA-256 (`<hex>  <name>`). The updater refuses an archive without it. |
| `SHA256SUMS` | The same lines, for people and scripts. |

The platform token is the .NET runtime identifier (`win-x64`; `linux-x64` later, as a `.tar.gz`). The zip holds one
folder:

```
OpenSCCT Editor\
  OpenSCCT Editor.exe                      double-click to run (Godot export, the .pck inside it)
  data_OpenScctEditor_windows_x86_64\      Godot's .NET folder: the runtime and the editor's assemblies
  ose\ose.exe                              the command line tool, one self-contained file
  README.txt  THIRD-PARTY-NOTICES.txt     what it is, the disclaimer, and the licences of what it includes
  version.json                             version, commit, platform, the names the release owns and every
                                           file under them with its size and SHA-256
```

`version.json` lists in `entries` the top-level names the release owns. An update replaces exactly those and leaves
anything else in the folder alone (Godot's `_sc_` self-contained mode and its `editor_data`, an `override.cfg`, the
user's own files).

## Versions

`VersionPrefix` in `Directory.Build.props` is the only place the version is written. Every build is `<version>-dev`
except a release package, which `tools/Package-Release.ps1` builds with `OseRelease=true` (and `VersionSuffix=beta.1`
for a pre-release). The SDK adds the commit, so About shows `OpenSCCT Editor 0.2.0 (eb6af75)`, and a build from source
shows `0.2.0-dev (eb6af75)`. A `-dev` build never updates itself.

## Making a release

1. Bump `VersionPrefix` in `Directory.Build.props`, commit and push to `main`.
2. Run `pwsh tools/Package-Release.ps1` (add `-Version 0.3.0-beta.1` for a pre-release). It checks that the tree is
   clean and pushed, `app/` holds no ignored files besides Godot's cache, the tag is free, the pinned OpenSCCT checkout matches `openscct.ref` and Godot's
   4.7.2 .NET export templates are installed. Then it builds, exports headless (no window), publishes `ose`, writes the
   documents and `version.json`, checks the folder (only the expected names, no PDBs, a notice for every third-party
   library, no path of the build machine in any file, `ose --help` runs) and writes, in `local/release/<version>/`: the zip, its `.sha256`, `SHA256SUMS`,
   `notes.md` (drafted from the commits since the last tag) and `publish.ps1`.
3. Edit `notes.md`. Try the zip: extract it somewhere and run it. Run `python -B tools/Test-Mcp.py "<extracted folder>/ose/ose.exe"`
   to check MCP discovery and build a test map with the packaged CLI.
4. Run `publish.ps1`. It uploads everything as a **draft** release (`gh release create --draft`), which editors never
   see.
5. Check the draft on GitHub and publish it. Editors find it at their next check.

`-Local` makes a package for trying things out on this machine: it skips the release checks and accepts any
`-Version` (`-Local -Version 0.1.1 -Out C:/temp/ose-0.1.1`). Never upload one.

## How the editor updates

The updater is `src/OpenScct.Editor.Core/Updates` (Godot-free; the app and `ose` share it), on top of OpenSCCT's
launcher library: its GitHub release client (ETag cache, rate limits, offline cache), release picker, resumable
downloader and checksum code.

**Checking.** A release package asks GitHub a few seconds after every start (Preferences > General > Updates turns
it off), on a background thread and silently: a newer release shows as a link in the status bar, never a popup. The
request carries the ETag of the last answer, so an unchanged list costs nothing of GitHub's rate limit; offline, the
cached list answers, so the link is back after every restart. Help > Check for Updates… always answers. The newest
release with this platform's zip *and* its `.sha256` wins; drafts and tags that are not versions are ignored,
pre-releases only when the preference is on (or the running build is one). "Skip this version" hides that version
until a newer one is out.

**Downloading.** Update now downloads the zip to `%LOCALAPPDATA%\OpenSCCT-Editor\updates\downloads` (resuming an
earlier partial; the status-bar link cancels it, and a server that sends nothing for a minute counts as a dropped
connection), checks the SHA-256, and unpacks it into the hidden `.update\<version>` folder inside the install, so
every later move is a rename on one disk. Every entry must sit under the zip's `OpenSCCT Editor/` folder, be a plain
relative path and unpack to exactly its stated size; the archive is capped at 20 000 entries and 2 GB. The unpacked
`version.json` must name the release's version and platform, every entry it lists must be there, and every file in
its `files` list must have its size and SHA-256. The folder is then marked `.staged`, and the status bar offers
"Restart to update". A lock file keeps two editors from doing this at once.

**Swapping.** A running editor keeps its exe and the DLLs of its data folder mapped, so the swap runs after it quits,
in a helper: the new build's `ose`, copied to `updates\helper\<version>` and started hidden as
`ose update apply --plan <file>`. Restart asks to save the map, checks no other program runs from the folder, starts
the helper, waits for it to answer (a helper that does not answer within ten seconds is stopped) and quits. Quitting
with an update ready starts the helper the same way, without a restart.

The helper holds the install's swap lock (`.update\swap.lock`) from start to end, so a second helper only answers
"busy" and leaves. It waits for the editor and anything else running from the folder to exit, then checks:

- the folder's own `version.json` is readable and names the version the editor was;
- the staged build passes the file-by-file check again (an antivirus may have taken a file since);
- every name the new build adds is free: an update never moves a file or folder of the user's.

It writes `.update\journal.json` once, then moves the previous build's entries (only the names its `version.json`
lists) into `.update\old-<previous>`, **exe first**, and the new ones in from staging, **exe last**. So whenever an
exe is in the folder, the build it belongs to is whole; in between there is no exe at all, never one that crashes on a
half-moved data folder. It then starts the new editor once, headless, with `-- --update-probe=<file>`: the editor
writes its version there and quits before opening anything. Only then does the swap count as done: the journal goes,
the editor restarts with the map that was open (unless it was installing on quit) and the previous build stays in
`old-<previous>` until the next start removes it.

If a rename fails (a file still in use after ten seconds of retries), or the new editor does not start, the helper
moves everything back from what it finds on disk (new entries back to staging exe first, previous ones back exe last,
retried for half a minute) and starts the previous editor. A new build that did not start is deleted, so it is
downloaded again rather than retried.

Either way the helper writes `.update\result.json`, and the next start reports it: "Updated to 0.2.0" with a link to
the notes, or a notice with the reason in words (files in use, a damaged download, a name of the user's in the way)
and an Open Log button. Installing on quit is reported the same way at the next start.

**When a swap is cut off.** While a swap runs, the helper keeps a RunOnce entry for the user
(`HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce`, "OpenSCCT Editor update <id>") that runs its own copy of
`ose update recover` at the next sign-in; finishing or rolling back removes it. So when Windows shuts down or the
helper is killed half way, the next sign-in puts the previous build back, and the next start says the update was
interrupted. If an editor is started before that, its build is whole (see above), so it settles the journal itself:
the new build if the swap had got that far, the previous one if nothing had moved. A journal that cannot be read is
set aside, never left to stop every start. If nothing helps, extracting the release zip over the folder repairs it.

The editor's start also cleans up: staged builds that are not newer, `old-*`, downloads of older versions, and helper
copies and plans that are over a day old, not running and not named by a recovery entry (another install may still
need its own).

**What it never does.** A build from source (`Run-Editor.cmd`, a local export without the release stamp, a folder
without `version.json`) never updates: Help > Check for Updates says to use `git pull`. A folder the user cannot write
(Program Files) gets the notice and a Download… button that opens the release page; the editor never asks for
administrator rights. Background runs (`--background`, `OSE_BACKGROUND=1`) never open a browser.

## Trying updates out

### Launcher integration (protocol 1)

The Windows launcher installs an optional editor copy under `<launcher-root>/editor` and writes `.launcher-managed`
outside the package's owned entries. Standalone installs keep their existing behavior. The launcher and the editor
both use the editor's updater; neither replaces a running editor or command-line tool.

Every new package includes `launcherProtocol: 1` and `minimumGameVersion: "0.8.111"` in format-1 `version.json`.
Packaging also emits `OpenSCCT-Editor-<version>-win-x64.launcher.json` with those two fields and includes it in the
generated draft-release upload command. Publish this sidecar with the ZIP and checksum: a managed editor only offers
releases carrying it and validates both the sidecar and downloaded manifest before staging. The marker survives
updates because it is not a package entry. A release without compatible metadata must never replace a managed copy.

The launcher supplies `OSE_LAUNCHER_ROOT`; managed discovery uses that root's current `install.json`, `packs`, and
installed content builder instead of inherited executable/content overrides. It resolves the current game when Play
or a CLI command needs it, so keeping the editor open across a game update does not pin an old executable. Standalone
`OSE_GAME_EXE`/`OSE_CONTENT` overrides remain supported. GUI and headless play use the same effective content root.

The machine interface is:

```
ose update status --install <folder> --json
ose update stage --install <folder> --version <exact-version> --json --cancel-file <path>
ose update install-staged --install <folder> --version <exact-version> --json
```

`status` reports `launcherProtocol`, `current`, `staged`, `minimumGameVersion`, `busy`, and the last update `result`
without consuming the editor's notification. `stage` writes JSON lines to stdout: progress has `type: "progress"`,
`phase`, `received`, `total`, and `bytesPerSecond`; the final line has `type: "result"`, `status`, `version`, `message`,
and optional `folder`. Status is `staged`, `current`, `cancelled`, `busy`, or `failed`. Creating the cancellation file
stops download/extraction cooperatively, leaving the installed editor untouched. Exact selection never downgrades.

Before `install-staged`, copy the entire installed `ose` directory to a unique helper directory outside the editor
installation and run that copy. A CLI waiting inside the installed tree would keep its own files locked on Windows.
The command prepares a unique copy of the **new** build's helper, applies the selected staged version, and waits.
It returns `installed`, `current`, `busy`, or `failed`, never opens a visible editor, and retains the headless startup
probe and rollback. Applying is not cancellable. Exit codes for both operations are 0 success, 1 failed/cancelled,
and 2 busy. Diagnostic logs go to stderr in JSON mode. The launcher reads `version.json` after completion rather
than relying on its previous version display.

Staging, applying, recovery, and cleanup acquire the existing global update lock before the per-install swap lock.
GUI and CLI handoffs use a unique installation/operation ID for helper and plan paths; no version-only plan is
overwritten by another window. Update results remain in `.update/result.json` until the editor consumes them.

Run the isolated regression checks with:

```
dotnet run --project tools/launcher-integration-checks/LauncherIntegrationChecks.csproj
```

They cover root switching, packed content, retained/pruned game versions, standalone overrides, lock contention,
verified staging, cancellation, corrupted staging, successful/failed startup probes, rollback/recovery, preservation
of user files, and structured CLI orchestration. Release QA must additionally use exported Godot packages and verify
both launcher-driven and in-editor updates before distributing the integrated launcher.

### Local release feeds

`OSE_UPDATE_API` replaces `https://api.github.com` in the updater's requests (for example
`http://127.0.0.1:8770`; only a loopback address is accepted, and its answers are cached apart from GitHub's), so a
local server can play the release feed: it answers
`/repos/Lumbridge/OpenSCCT-Editor/releases` with release JSON whose download links point back at it. With two `-Local`
packages (say 0.1.0 and 0.1.1) and `OSE_DATA` set to a scratch folder:

```
ose update check --install "C:/temp/inst/OpenSCCT Editor"       # 0.1.1 available
"C:/temp/inst/OpenSCCT Editor/OpenSCCT Editor.exe" --headless -- --install-update
```

`--install-update` checks at once and installs a newer version without asking, then restarts into it with the same
engine arguments (here `--headless`). The swap's own check starts the new editor headless too, so a run like this never
shows a window. `ose update stage` downloads and unpacks without the editor, and
`ose update apply --plan <file> --no-restart` swaps without restarting the editor. The helper's log is
`updates\update.log`.
