#Requires -Version 7
<#
.SYNOPSIS
Takes screenshots of the editor app without disturbing the person at the machine.

.DESCRIPTION
Runs the app in background mode through Start-BackgroundEditor.ps1: the window starts minimised and unfocused, is
moved off-screen with no taskbar button, never takes focus and never touches the real mouse, clipboard or speakers,
and the run has its own user folder, data, prefab and live discovery folders. A shot script (or a built-in tour)
drives it; the PNGs, manifest.json and a contact sheet (index.html) land in the output folder.

.PARAMETER Map
A .osmap project, or anything the editor imports (an SVMap folder, a .t3d). Default: a new map (the starter room).

.PARAMETER Script
A shot script (JSON array of steps, see docs/SCREENSHOTS.md) or tour:full / tour:quick, optionally pinned to a window
size and UI scale (tour:full@1280x720, tour:full@1920x1080@1.5). Default tour:full.

.PARAMETER Out
Output folder. Default local/shots/<yyyyMMdd-HHmmss>-<map>.

.PARAMETER Cache
The OSE_CACHE folder for the run (see Start-BackgroundEditor.ps1). Default local/background/.cache.

.PARAMETER OpenScctRoot
The OpenSCCT checkout the app builds against. Default: the one Directory.Build.props names.

.EXAMPLE
pwsh tools/Invoke-EditorShots.ps1
pwsh tools/Invoke-EditorShots.ps1 -Map C:/maps/SteeE.osmap -Script tour:quick
#>
param(
    [string]$Map = '',
    [string]$Script = 'tour:full',
    [string]$Out = '',
    [string]$OpenScctRoot = '',
    [string]$Cache = '',
    [string]$Godot = $env:OSE_GODOT,
    [int]$TimeoutSeconds = 900,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$name = if ($Map) { [IO.Path]::GetFileNameWithoutExtension($Map.TrimEnd('\', '/')) } else { 'new' }
if (-not $Out) { $Out = Join-Path $repo ("local/shots/{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $name) }
$Out = [IO.Path]::GetFullPath($Out)
if ($Script -notlike 'tour:*') { $Script = (Resolve-Path -LiteralPath $Script).Path }

$run = & (Join-Path $PSScriptRoot 'Start-BackgroundEditor.ps1') -Map $Map -Out $Out -OpenScctRoot $OpenScctRoot -Cache $Cache -Godot $Godot `
    -NoBuild:$NoBuild -Wait -TimeoutSeconds $TimeoutSeconds -EditorArgs @("--shots=$Script", "--shots-out=$Out")

$manifest = Join-Path $Out 'manifest.json'
if (-not (Test-Path -LiteralPath $manifest)) { throw "No manifest was written (exit code $($run.ExitCode)); see $($run.Log)" }
$m = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$findings = ($m.shots | ForEach-Object { @($_.lint).Count } | Measure-Object -Sum).Sum
Write-Host ("{0} shots, {1} UI findings, {2} failed steps, exit code {3}" -f @($m.shots).Count, $findings, @($m.failures).Count, $run.ExitCode)
foreach ($f in $m.failures) { Write-Host "  failed: $f" }
Write-Host (Join-Path $Out 'index.html')
exit $run.ExitCode
