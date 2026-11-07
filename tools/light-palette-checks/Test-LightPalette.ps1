#Requires -Version 7
# Needs installed map content (including Aquarium) via OSE_CONTENT or normal content discovery.
param([string]$Godot = $env:OSE_GODOT, [string]$Out = '', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Out) { $Out = Join-Path $repo 'local/light-palette-checks' }
& (Join-Path $repo 'tools/Invoke-EditorShots.ps1') -Script (Join-Path $PSScriptRoot 'shots.json') -Out $Out -Godot $Godot -NoBuild:$NoBuild
if ($LASTEXITCODE -ne 0) { throw 'Light palette screenshot run failed' }
$manifest = Get-Content -LiteralPath (Join-Path $Out 'manifest.json') -Raw | ConvertFrom-Json
$shots = @($manifest.shots)
if ($shots.Count -ne 6 -or @($manifest.failures).Count -gt 0) { throw 'Light palette run did not finish' }
if ($shots[1].objects -ne ($shots[0].objects + 2) -or $shots[1].selection -ne 2) { throw 'Fixture did not place and select a mesh/light pair' }
if ($shots[2].objects -ne $shots[0].objects) { throw 'Fixture undo did not remove both objects' }
if ($shots[3].objects -ne $shots[1].objects) { throw 'Fixture redo did not restore both objects' }
if ($shots[5].objects -ne ($shots[4].objects + 1) -or $shots[5].selection -ne 1) { throw 'Raw light did not place and select one actor' }
Write-Host 'PASS: fixture placement, selection, undo/redo and raw light placement'
