#Requires -Version 7
<#
.SYNOPSIS
Starts the editor app in the background: off-screen, never focused, with a user folder of its own.

.DESCRIPTION
The safe way to run the editor for a script or a screenshot run on a machine someone is using. Godot creates and
shows the window before any editor code runs, so this script writes a temporary app/override.cfg that starts it
minimised, unfocused and with popups inside it, and takes it away again once the editor has started (a named lock
keeps two starts from mixing up each other's copy). The editor then moves itself off-screen with no taskbar button.

The run gets its own folders under -Out, set through the environment for the editor only:
  appdata  APPDATA: Godot's user folder (shader cache); its "OpenSCCT Editor" folder is also OSE_USER_DATA, the
           editor's settings, autosaves, crash markers and builds. The shader cache starts as a copy of
           local/background/.shader-cache, which the first run that ends cleanly fills
  live     OSE_LIVE_DIR: live editing discovery, so the user's own `ose live` never sees this editor
  data     OSE_DATA: imports and the staged map build tools
  prefabs  OSE_PREFABS: the prefab library
and OSE_CACHE (asset index and thumbnails) goes to -Cache, default local/background/.cache, shared by background
runs only. Godot's own log goes to editor.log in the run folder (--log-file), never the user's log folder.

Without -Wait the script returns as soon as the editor is up and prints its process id and live folder; connect with
OSE_LIVE_DIR set to that folder (`ose serve --live`) and stop it with Stop-Process. With -Wait it waits for the editor
to exit (a screenshot run quits by itself) and returns its exit code.

.PARAMETER Map
A .osmap project, or anything the editor imports (an SVMap folder, a .t3d). Default: a new map.

.PARAMETER Out
The run folder. Default local/background/<yyyyMMdd-HHmmss>-<map>.

.PARAMETER EditorArgs
More arguments for the editor (after --), such as --shots=tour:quick.

.PARAMETER OpenScctRoot
The OpenSCCT checkout the app builds against (as -p:OpenScctRoot). Default: the one Directory.Build.props names.

.PARAMETER Cache
The OSE_CACHE folder (asset index, thumbnails, shadow and nav caches). Default local/background/.cache.

.PARAMETER Godot
Godot_v4.7.2-stable_mono_win64.exe. Default: OSE_GODOT, else tools/godot in the OpenSCCT checkout, in the OpenSCCT
clone (OSE_OPENSCCT_GIT) or in ..\OpenSCCT next to this repo.

.EXAMPLE
pwsh tools/Start-BackgroundEditor.ps1 -Map C:/maps/MyMap.osmap
#>
param(
    [string]$Map = '',
    [string]$Out = '',
    [string[]]$EditorArgs = @(),
    [string]$OpenScctRoot = '',
    [string]$Cache = '',
    [string]$Godot = $env:OSE_GODOT,
    [switch]$NoBuild,
    [switch]$Wait,
    [int]$TimeoutSeconds = 900,
    [int]$StartSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo 'app'
if ($OpenScctRoot) { $OpenScctRoot = $OpenScctRoot.TrimEnd('\', '/') + '/' }

if (-not $Godot) {
    $exe = 'tools/godot/4.7.2-stable/Godot_v4.7.2-stable_mono_win64.exe'
    $roots = @($OpenScctRoot, $env:OSE_OPENSCCT_GIT, (Join-Path (Split-Path -Parent $repo) 'OpenSCCT')) | Where-Object { $_ }
    $Godot = $roots | ForEach-Object { Join-Path $_ $exe } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Godot -or -not (Test-Path -LiteralPath $Godot)) { throw 'Godot 4.7.2 .NET not found: pass -Godot or set OSE_GODOT' }
# The console wrapper imports; the GUI exe runs (it opens no console window).
$godotConsole = $Godot -replace 'mono_win64\.exe$', 'mono_win64_console.exe'

if (-not $NoBuild) {
    $build = @((Join-Path $app 'OpenScctEditor.csproj'), '-v:quiet', '-nologo')
    if ($OpenScctRoot) { $build += "-p:OpenScctRoot=$OpenScctRoot" }
    dotnet build @build | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The app did not build' }
}
if (-not (Test-Path -LiteralPath (Join-Path $app '.godot/imported'))) {
    & $godotConsole --headless --path $app --import | Out-Null
}

$name = if ($Map) { [IO.Path]::GetFileNameWithoutExtension($Map.TrimEnd('\', '/')) } else { 'new' }
if (-not $Out) { $Out = Join-Path $repo ("local/background/{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $name) }
$Out = [IO.Path]::GetFullPath($Out)
$folders = @{ appdata = 'APPDATA'; live = 'OSE_LIVE_DIR'; data = 'OSE_DATA'; prefabs = 'OSE_PREFABS' }
foreach ($folder in $folders.Keys) { New-Item -ItemType Directory -Force -Path (Join-Path $Out $folder) | Out-Null }
$log = Join-Path $Out 'editor.log'
# A fresh user folder would compile every shader again; start from the shared copy.
$userDir = Join-Path $Out 'appdata/OpenSCCT Editor'
$shaderSeed = Join-Path $repo 'local/background/.shader-cache'
$shaderDirs = 'shader_cache', 'vulkan'
New-Item -ItemType Directory -Force -Path $userDir | Out-Null
if (Test-Path -LiteralPath $shaderSeed) { Copy-Item -Path (Join-Path $shaderSeed '*') -Destination $userDir -Recurse -Force }

function Quote([string]$s) { if ($s -match '[\s"]') { '"' + ($s -replace '"', '\"') + '"' } else { $s } }

$arguments = @('--path', (Quote $app), '--audio-driver', 'Dummy', '--log-file', (Quote $log), '--', '--background')
if ($Map) { $arguments += Quote ('--open=' + [IO.Path]::GetFullPath($Map)) }
$arguments += $EditorArgs | ForEach-Object { Quote $_ }

$override = Join-Path $app 'override.cfg'
$envKeys = @($folders.Values) + 'OSE_CACHE', 'OSE_BACKGROUND', 'OSE_FOCUS', 'OSE_USER_DATA'
$previousEnv = @{}
foreach ($key in $envKeys) { $previousEnv[$key] = [Environment]::GetEnvironmentVariable($key) }
# One lock per app folder: the override.cfg of one start must not be taken away (or left behind) by another.
$lockName = 'Local\OpenScctEditor-override-' + [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($app.ToLowerInvariant())))
$lock = [Threading.Mutex]::new($false, $lockName)
$owned = $false
try {
    # An abandoned lock (a start that was killed) still passes to this one.
    try { $owned = $lock.WaitOne(($StartSeconds + 30) * 1000) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another background start of this app did not finish' }
    $previousOverride = if (Test-Path -LiteralPath $override) { [IO.File]::ReadAllBytes($override) } else { $null }
    try {
        # Godot shows the window before any editor code runs: start it minimised, unfocused and with popups inside it.
        [IO.File]::WriteAllText($override, "[display]`n`nwindow/size/no_focus=true`nwindow/size/mode=1`nwindow/subwindows/embed_subwindows=true`n")
        foreach ($folder in $folders.Keys) { [Environment]::SetEnvironmentVariable($folders[$folder], (Join-Path $Out $folder)) }
        $env:OSE_CACHE = if ($Cache) { [IO.Path]::GetFullPath($Cache) } else { Join-Path $repo 'local/background/.cache' }
        $env:OSE_BACKGROUND = '1'
        $env:OSE_USER_DATA = $userDir
        $env:OSE_FOCUS = $null
        $process = Start-Process -FilePath $Godot -ArgumentList $arguments -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $Out 'stdout.log') -RedirectStandardError (Join-Path $Out 'stderr.log')
        # Holding the handle keeps the exit code readable after the process ends.
        $null = $process.Handle

        # The window exists once the editor's first code has run (it logs "Background mode:"); the override is not
        # needed after that.
        $started = [Diagnostics.Stopwatch]::StartNew()
        $ready = $false
        while (-not $ready -and -not $process.HasExited) {
            if ($started.Elapsed.TotalSeconds -gt $StartSeconds) {
                # Never leave a window that may come up without the override's protection.
                taskkill.exe /T /F /PID $process.Id | Out-Null
                throw "The editor did not start within $StartSeconds s; see $log"
            }
            Start-Sleep -Milliseconds 200
            $ready = (Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -Pattern '^Background mode:' -Quiet)
        }
    }
    finally {
        if ($null -eq $previousOverride) { Remove-Item -LiteralPath $override -ErrorAction SilentlyContinue }
        else { [IO.File]::WriteAllBytes($override, $previousOverride) }
        foreach ($key in $envKeys) { [Environment]::SetEnvironmentVariable($key, $previousEnv[$key]) }
    }
}
finally {
    if ($owned) { $lock.ReleaseMutex() }
    $lock.Dispose()
}

if (-not $Wait) {
    if ($process.HasExited) { throw "The editor exited at start (exit code $($process.ExitCode)); see $log" }
    [pscustomobject]@{ Id = $process.Id; Live = (Join-Path $Out 'live'); Run = $Out; Log = $log }
    return
}

if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    taskkill.exe /T /F /PID $process.Id | Out-Null
    throw "The editor did not finish within $TimeoutSeconds s; see $log"
}
if ($process.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $shaderSeed)) {
    New-Item -ItemType Directory -Force -Path $shaderSeed | Out-Null
    foreach ($dir in $shaderDirs) {
        $from = Join-Path $userDir $dir
        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination $shaderSeed -Recurse -Force }
    }
}
[pscustomobject]@{ Id = $process.Id; ExitCode = $process.ExitCode; Run = $Out; Log = $log }
