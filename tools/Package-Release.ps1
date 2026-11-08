#Requires -Version 7
<#
.SYNOPSIS
Packages an OpenSCCT Editor release: the zip people download and the editor updates from, its checksums, draft notes
and the command that uploads them as a draft GitHub release.

.DESCRIPTION
Builds the editor, exports it with Godot (headless, no window), publishes ose next to it and writes the release folder:

  OpenSCCT Editor\
    OpenSCCT Editor.exe                    double-click to run (the .pck is inside it)
    data_OpenScctEditor_windows_x86_64\    Godot's .NET folder (runtime and editor assemblies)
    ose\ose.exe                            the command line tool, one self-contained file
    README.txt, THIRD-PARTY-NOTICES.txt   what it is, the disclaimer, and the licences of what it includes
    version.json                           version, commit, the names the release owns and every file's SHA-256
                                           (read by the updater)

then zips it (one top folder, so "Extract All" gives a neat folder) and writes, in -Out:

  OpenSCCT-Editor-<version>-win-x64.zip  and  .zip.sha256   the download and its checksum (the updater needs both)
  SHA256SUMS                                               the same line, for people and scripts
  notes.md                                                 release notes drafted from the commits since the last tag
  publish.ps1                                              gh release create ... --draft (run it after editing notes.md)

Nothing is published: publish.ps1 makes a draft, which editors never see until it is published on GitHub.
docs/RELEASING.md has the whole procedure.

.PARAMETER Version
The version to package. Default: VersionPrefix in Directory.Build.props, which it must match. A pre-release adds a
suffix: 0.3.0-beta.1 (published as a GitHub pre-release).

.PARAMETER Local
A package for trying the editor or its updates out on this machine: skips the release preconditions (clean tree,
pushed to origin/main, tag not taken), allows any -Version, warns instead of failing on private paths, and writes no
publish.ps1. Never upload a local package.

.PARAMETER Out
The output folder. Default local/release/<version>.

.PARAMETER Godot
Godot_v4.7.2-stable_mono_win64.exe. Default: OSE_GODOT, else tools/godot in the OpenSCCT clone (OSE_OPENSCCT_GIT, else
..\OpenSCCT next to this repo).

.EXAMPLE
pwsh tools/Package-Release.ps1
pwsh tools/Package-Release.ps1 -Local -Version 0.1.1 -Out C:/temp/ose-0.1.1
#>
param(
    [string]$Version = '',
    [switch]$Local,
    [string]$Out = '',
    [string]$Godot = $env:OSE_GODOT
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo 'app'
$platform = 'win-x64'
$folderName = 'OpenSCCT Editor'
$exeName = 'OpenSCCT Editor.exe'
$dataName = 'data_OpenScctEditor_windows_x86_64'
$owner = 'Lumbridge/OpenSCCT-Editor'
$templateVersion = '4.7.2.stable.mono'

function Fail([string]$message) { throw "Package-Release: $message" }

function Invoke-Native([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit code $LASTEXITCODE)" }
}

# --- Version -------------------------------------------------------------------------------------------------------
$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$prefix = [regex]::Match($props, '<VersionPrefix>([^<]+)</VersionPrefix>').Groups[1].Value
if (-not $prefix) { Fail 'no VersionPrefix in Directory.Build.props' }
if (-not $Version) { $Version = $prefix }
if ($Version -notmatch '^(\d+\.\d+\.\d+)(?:-([0-9A-Za-z.-]+))?$') { Fail "'$Version' is not a version like 0.2.0 or 0.3.0-beta.1" }
$core = $Matches[1]
$suffix = $Matches[2]
if ($suffix -eq 'dev') { Fail "'dev' marks builds from source; a package cannot use it" }
if (-not $Local -and $core -ne $prefix) { Fail "-Version $Version does not match VersionPrefix $prefix in Directory.Build.props (bump it first)" }
$isPrerelease = [bool]$suffix

if (-not $Out) { $Out = Join-Path $repo "local/release/$Version" }
$Out = [IO.Path]::GetFullPath($Out)
$folder = Join-Path $Out $folderName
$zipName = "OpenSCCT-Editor-$Version-$platform.zip"
$zip = Join-Path $Out $zipName

# --- Preconditions -------------------------------------------------------------------------------------------------
$commit = git -C $repo rev-parse HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or -not $commit) { Fail 'run it from a git clone of the editor' }
$commit = ([string]$commit).Trim()
# The export takes every resource under app/, ignored files too: only Godot's cache and override.cfg (excluded from
# the export) may be there.
$strays = @(git -C $repo status --porcelain --ignored -- app | Where-Object { $_ -like '!! *' } |
    ForEach-Object { $_.Substring(3) } | Where-Object { $_ -notin @('app/.godot/', 'app/override.cfg') })
if ($strays.Count -gt 0) {
    $message = "ignored files under app/ would go into the export: $($strays -join ', ')"
    if ($Local) { Write-Warning $message } else { Fail $message }
}
if (-not $Local) {
    if (git -C $repo status --porcelain) { Fail 'the working tree has changes; commit or stash them' }
    Invoke-Native 'git fetch' { git -C $repo fetch origin main --quiet --tags }
    git -C $repo merge-base --is-ancestor HEAD origin/main
    if ($LASTEXITCODE -ne 0) { Fail 'HEAD is not on origin/main; push it first' }
    if (git -C $repo ls-remote --tags origin "refs/tags/v$Version") { Fail "the tag v$Version already exists on origin" }
}

$pinned = Join-Path (Split-Path -Parent $repo) 'OpenSCCT-Editor-deps'
$ref = (Get-Content -LiteralPath (Join-Path $repo 'openscct.ref') -Raw).Trim()
if (Test-Path -LiteralPath (Join-Path $pinned 'OpenSCCT.slnx')) {
    $pinnedHead = (git -C $pinned rev-parse HEAD).Trim()
    if ($pinnedHead -ne $ref) { Fail "the pinned OpenSCCT checkout is at $pinnedHead, openscct.ref says $ref (run tools/Sync-OpenScct.ps1)" }
} elseif (-not $Local) {
    Fail "no pinned OpenSCCT checkout at $pinned (run tools/Sync-OpenScct.ps1)"
}

$openScctClone = if ($env:OSE_OPENSCCT_GIT) { $env:OSE_OPENSCCT_GIT } else { Join-Path (Split-Path -Parent $repo) 'OpenSCCT' }
if (-not $Godot) {
    $Godot = Join-Path $openScctClone 'tools/godot/4.7.2-stable/Godot_v4.7.2-stable_mono_win64.exe'
}
if (-not (Test-Path -LiteralPath $Godot)) { Fail 'Godot 4.7.2 .NET not found: pass -Godot or set OSE_GODOT' }
$godotConsole = $Godot -replace 'mono_win64\.exe$', 'mono_win64_console.exe'
if (-not (Test-Path -LiteralPath $godotConsole)) { Fail "no console Godot next to $Godot" }
$template = Join-Path $env:APPDATA "Godot/export_templates/$templateVersion/windows_release_x86_64.exe"
if (-not (Test-Path -LiteralPath $template)) {
    Fail "Godot's export templates are not installed ($template). Run pwsh tools/Get-Godot.ps1 -ExportTemplates in your OpenSCCT clone, or in Godot: Editor > Manage Export Templates > Download and Install (4.7.2 .NET)."
}

Write-Host "Packaging OpenSCCT Editor $Version ($commit) into $Out$(if ($Local) { ' (local package)' })"
foreach ($stale in @($folder, $zip, "$zip.sha256", (Join-Path $Out 'SHA256SUMS'))) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $folder | Out-Null

# --- Build and export ----------------------------------------------------------------------------------------------
# MSBuild reads environment variables as properties, also in the build Godot runs for the export: OseRelease drops the
# "-dev" suffix (Directory.Build.props), VersionSuffix marks a pre-release, OseVersionPrefix stamps a local version.
$envKeys = 'OseRelease', 'VersionSuffix', 'OseVersionPrefix', 'ContinuousIntegrationBuild'
$previousEnv = @{}
foreach ($key in $envKeys) { $previousEnv[$key] = [Environment]::GetEnvironmentVariable($key) }
$presets = Join-Path $app 'export_presets.cfg'
$presetsText = [IO.File]::ReadAllText($presets)
try {
    $env:OseRelease = 'true'
    $env:VersionSuffix = if ($suffix) { $suffix } else { $null }
    $env:OseVersionPrefix = if ($core -ne $prefix) { $core } else { $null }
    # Maps source paths in the assemblies to /_/, so no path of this machine ships.
    $env:ContinuousIntegrationBuild = 'true'

    Write-Host 'Building...'
    Invoke-Native 'dotnet build' { dotnet build (Join-Path $repo 'OpenScctEditor.slnx') -c Release -nologo -v:q -clp:ErrorsOnly }
    Invoke-Native 'dotnet build (app)' { dotnet build (Join-Path $app 'OpenScctEditor.csproj') -nologo -v:q -clp:ErrorsOnly }

    # The exe's version resource (Explorer's Details tab).
    $fileVersion = "$core.0"
    $stamped = $presetsText -replace '(?m)^application/file_version=".*"$', "application/file_version=`"$fileVersion`"" `
        -replace '(?m)^application/product_version=".*"$', "application/product_version=`"$fileVersion`""
    [IO.File]::WriteAllText($presets, $stamped)

    Write-Host 'Importing...'
    Invoke-Native 'Godot import' { & $godotConsole --headless --path $app --import 2>&1 | Out-Null }
    Write-Host 'Exporting...'
    $exportLog = Join-Path $Out 'export.log'
    & $godotConsole --headless --path $app --export-release 'Windows Desktop' (Join-Path $folder $exeName) *> $exportLog
    # Godot can exit with 0 after a failed export: check what it wrote.
    foreach ($needed in @($exeName, $dataName, "$dataName/OpenScctEditor.dll", "$dataName/OpenScct.Editor.Core.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $folder $needed))) { Fail "the export did not write $needed (see $exportLog)" }
    }

    Write-Host 'Publishing ose...'
    Invoke-Native 'dotnet publish (ose)' {
        dotnet publish (Join-Path $repo 'src/OpenScct.Editor.Cli/OpenScct.Editor.Cli.csproj') -c Release -r $platform --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -p:GenerateDocumentationFile=false -o (Join-Path $folder 'ose') -nologo -v:q -clp:ErrorsOnly
    }
} finally {
    [IO.File]::WriteAllText($presets, $presetsText)
    foreach ($key in $envKeys) { [Environment]::SetEnvironmentVariable($key, $previousEnv[$key]) }
}

# --- Documents -----------------------------------------------------------------------------------------------------
$utf8 = [Text.UTF8Encoding]::new($false)
# Text files for people get Windows line ends (Notepad), without a byte order mark.
function Write-Text([string]$path, [string]$text) {
    $crlf = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($path, $crlf, $utf8)
}

$title = "OpenSCCT Editor $Version"
$readme = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release/README.txt') -Raw).Replace('{{TITLE}}', $title).Replace('{{UNDERLINE}}', '=' * $title.Length)
Write-Text (Join-Path $folder 'README.txt') $readme

# Godot's own licence and its third-party components, as the engine reports them.
$noticeProject = Join-Path $Out '.notices'
New-Item -ItemType Directory -Force -Path $noticeProject | Out-Null
Set-Content -LiteralPath (Join-Path $noticeProject 'project.godot') -Value "config_version=5`n" -NoNewline
Set-Content -LiteralPath (Join-Path $noticeProject 'notices.gd') -NoNewline -Value @'
extends SceneTree

func _init():
	var f = FileAccess.open(OS.get_cmdline_user_args()[0], FileAccess.WRITE)
	f.store_string(Engine.get_license_text() + "\n\nThird-party components of Godot:\n\n")
	for c in Engine.get_copyright_info():
		f.store_string("- " + c["name"] + "\n")
		for part in c["parts"]:
			for line in part["copyright"]:
				f.store_string("    (c) " + line + "\n")
			f.store_string("    License: " + part["license"] + "\n")
	f.store_string("\nLicences of those components:\n\n")
	var info = Engine.get_license_info()
	for key in info:
		f.store_string("----- " + key + " -----\n\n" + info[key] + "\n\n")
	f.close()
	quit()
'@
$godotNotices = Join-Path $noticeProject 'godot.txt'
& $godotConsole --headless --path $noticeProject --script res://notices.gd -- $godotNotices 2>&1 | Out-Null
if (-not (Test-Path -LiteralPath $godotNotices)) { Fail "Godot did not write its licence notices" }

$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$sections = @(
    "Third-party notices for OpenSCCT Editor $Version",
    '',
    'The editor includes the Godot engine (in OpenSCCT Editor.exe), the .NET runtime (in the',
    "$dataName and ose folders) and the components below.",
    '',
    '===== OpenSCCT Editor =====',
    '',
    (Get-Content -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Raw),
    '',
    '===== Godot Engine =====',
    '',
    (Get-Content -LiteralPath $godotNotices -Raw),
    '',
    '===== .NET runtime =====',
    '',
    (Get-Content -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Raw),
    '',
    (Get-Content -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Raw)
)
Write-Text (Join-Path $folder 'THIRD-PARTY-NOTICES.txt') ($sections -join "`n")
Remove-Item -LiteralPath $noticeProject -Recurse -Force

# --- version.json --------------------------------------------------------------------------------------------------
$entries = @(Get-ChildItem -LiteralPath $folder -Force | ForEach-Object Name) + 'version.json' | Sort-Object -Unique
# Every file but version.json with its size and SHA-256: the updater checks an unpacked build against them before it
# swaps it in (a short write or a file an antivirus took is caught there, not when the editor fails to start).
$files = @(Get-ChildItem -LiteralPath $folder -Recurse -File -Force | Where-Object { $_.FullName -ne (Join-Path $folder 'version.json') } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path   = $_.FullName.Substring($folder.Length + 1).Replace('\', '/')
            size   = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
$info = [ordered]@{
    launcherProtocol = 1
    minimumGameVersion = '0.8.117'
    format   = 1
    product  = 'OpenSCCT Editor'
    version  = $Version
    commit   = $commit
    platform = $platform
    built    = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    entries  = $entries
    files    = $files
}
[IO.File]::WriteAllText((Join-Path $folder 'version.json'), ($info | ConvertTo-Json -Depth 4) + "`n", $utf8)

# --- Checks --------------------------------------------------------------------------------------------------------
$expected = @($exeName, $dataName, 'ose', 'README.txt', 'THIRD-PARTY-NOTICES.txt', 'version.json')
$unexpected = $entries | Where-Object { $_ -notin $expected }
$missing = $expected | Where-Object { $_ -notin $entries }
if ($unexpected) { Fail "unexpected entries in the folder: $($unexpected -join ', ')" }
if ($missing) { Fail "missing from the folder: $($missing -join ', ')" }
$bad = Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -like '*.console.exe' }
if ($bad) { Fail "debug files in the package: $(($bad | ForEach-Object Name) -join ', ')" }

# Every library that is not the editor's, OpenSCCT's, Godot's or the .NET runtime's needs its notice.
$notices = Get-Content -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Raw
$unlisted = Get-ChildItem -LiteralPath (Join-Path $folder $dataName) -Filter '*.dll' -File |
    Where-Object { $_.Name -notmatch '^(System\.|Microsoft\.|OpenScct|GodotSharp|GodotPlugins|mscorlib|netstandard|WindowsBase|api-ms-|clr|coreclr|hostfxr|hostpolicy|mscor|msquic|nethost|ijwhost)' } |
    Where-Object { $notices.IndexOf($_.BaseName, [StringComparison]::OrdinalIgnoreCase) -lt 0 } | ForEach-Object Name
if ($unlisted) { Fail "libraries without a notice in THIRD-PARTY-NOTICES.md: $($unlisted -join ', ')" }

# No path of the machine that built it (a user name, a checkout) may ship inside a file.
$needles = @($env:USERPROFILE, $repo, $pinned, $openScctClone) | Where-Object { $_ } | ForEach-Object {
    $_; $_.Replace('\', '/')
} | Sort-Object -Unique
$latin1 = [Text.Encoding]::Latin1
$leaks = foreach ($file in Get-ChildItem -LiteralPath $folder -Recurse -File) {
    $text = $latin1.GetString([IO.File]::ReadAllBytes($file.FullName))
    foreach ($needle in $needles) {
        $wide = $latin1.GetString([Text.Encoding]::Unicode.GetBytes($needle))
        if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $text.IndexOf($wide, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            "$($file.FullName.Substring($folder.Length + 1)): $needle"
        }
    }
}
if ($leaks) {
    $message = "private paths inside the package:`n  " + ($leaks -join "`n  ")
    if ($Local) { Write-Warning $message } else { Fail $message }
}

& (Join-Path $folder 'ose/ose.exe') --help | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'ose --help did not run from the package' }

# --- Zip and checksums ---------------------------------------------------------------------------------------------
Write-Host 'Zipping...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$line = "$hash  $zipName`n"
[IO.File]::WriteAllText("$zip.sha256", $line, $utf8)
[IO.File]::WriteAllText((Join-Path $Out 'SHA256SUMS'), $line, $utf8)

$launcherName = "OpenSCCT-Editor-$Version-win-x64.launcher.json"
[IO.File]::WriteAllText((Join-Path $Out $launcherName), (@{ launcherProtocol = 1; minimumGameVersion = '0.8.117' } | ConvertTo-Json) + "`n", $utf8)

# --- Notes and the publish command ---------------------------------------------------------------------------------
$previous = git -C $repo describe --tags --abbrev=0 --match 'v*' HEAD 2>$null
$log = if ($LASTEXITCODE -eq 0 -and $previous) { git -C $repo log --no-merges "$previous..HEAD" --format='- %s' } else { @('- First release.') }
$notes = @("OpenSCCT Editor $Version", '', '## Changes', '') + $log + @('',
    '## Download', '',
    "Download ``$zipName``, extract it and double-click ``OpenSCCT Editor.exe``. An editor that is already installed",
    'updates itself (Help > Check for Updates).')
Write-Text (Join-Path $Out 'notes.md') ($notes -join "`n")

if (-not $Local) {
    $pre = if ($isPrerelease) { ' --prerelease' } else { '' }
    $publish = @(
        '# Uploads this package as a DRAFT release. Review it on GitHub, then publish it there.',
        '$ErrorActionPreference = ''Stop''',
        "gh release create v$Version -R $owner --draft --target $commit --title 'OpenSCCT Editor $Version' --notes-file (Join-Path `$PSScriptRoot 'notes.md')$pre ``",
        "    (Join-Path `$PSScriptRoot '$zipName') (Join-Path `$PSScriptRoot '$zipName.sha256') (Join-Path `$PSScriptRoot 'SHA256SUMS') (Join-Path `$PSScriptRoot '$launcherName')"
    )
    Write-Text (Join-Path $Out 'publish.ps1') ($publish -join "`n")
}

$size = '{0:0.0} MB' -f ((Get-Item -LiteralPath $zip).Length / 1MB)
Write-Host ''
Write-Host "Packaged $zipName ($size) in $Out"
if ($Local) {
    Write-Host 'A local package: try it out, never upload it.'
} else {
    Write-Host "Next: edit notes.md, then run $(Join-Path $Out 'publish.ps1') to make the draft release."
}
