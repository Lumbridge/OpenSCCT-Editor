param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$Bundle,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_]+$')][string]$MapId,
    [string]$Out = '',
    [string]$EditorDll = '',
    [string]$ExpectedEditorSha256 = '',
    [switch]$Rebuild,
    [string[]]$ExpectedGroups = @(),
    [switch]$PauseBeforeLoad,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$Bundle = (Resolve-Path -LiteralPath $Bundle).Path
if (!$Out) { $Out = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) ('local/native-package-load-' + [Guid]::NewGuid().ToString('N')) }
$Out = [IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $Out) { throw 'Use a new empty test path.' }
$testSystem = Join-Path $Out 'System'
$testPackages = Join-Path $Out 'Packages'
New-Item -ItemType Directory -Path $testSystem,$testPackages | Out-Null
# All files the native editor may load or save are private copies. Preserve the original folder names and extensions.
Get-ChildItem -LiteralPath (Join-Path $GameRoot 'System') -File | Where-Object { $_.Extension -in '.exe','.dll','.ini','.int','.dat','.config','.bmp' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $testSystem }
foreach ($name in 'EditorRes','_PC_') {
    $source = Join-Path $GameRoot "System/$name"
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $testSystem -Recurse }
}
foreach ($name in 'Textures','StaticMeshes','Animations','Sounds','Assets') {
    $source = Join-Path $GameRoot "Packages/$name"
    if (Test-Path -LiteralPath $source) {
        Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { !$_.Name.StartsWith($MapId + '_',[StringComparison]::OrdinalIgnoreCase) -and $_.Name -ne "$MapId-i.utc" } |
            ForEach-Object {
                $destination = Join-Path (Join-Path $testPackages $name) ([IO.Path]::GetRelativePath($source,$_.FullName))
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $destination
            }
    }
}
Copy-Item -LiteralPath (Join-Path $Bundle 'Packages') -Destination $Out -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $Bundle 'System')) { Copy-Item -LiteralPath (Join-Path $Bundle 'System') -Destination $Out -Recurse -Force }
if ($EditorDll) { Copy-Item -LiteralPath (Resolve-Path -LiteralPath $EditorDll).Path -Destination (Join-Path $testSystem 'Reloaded.Editor.dll') -Force }
$actualEditorHash = (Get-FileHash -LiteralPath (Join-Path $testSystem 'Reloaded.Editor.dll') -Algorithm SHA256).Hash
if ($ExpectedEditorSha256 -and $actualEditorHash -ne $ExpectedEditorSha256) { throw 'The test DLL does not match the requested native editor.' }
Write-Output "EditorSha256=$actualEditorHash"
if ($PauseBeforeLoad) { New-Item -ItemType File -Path (Join-Path $testSystem 'package-load-wait.txt') | Out-Null }
if (Test-Path -LiteralPath (Join-Path $GameRoot 'Menus')) { Copy-Item -LiteralPath (Join-Path $GameRoot 'Menus') -Destination $Out -Recurse }
$interface = Join-Path $testPackages "Textures/$MapId-i.utc"
$map = Join-Path $testPackages "MapsEd/$MapId.sdc"
if (!(Test-Path -LiteralPath $interface) -or !(Test-Path -LiteralPath $map)) { throw 'Bundle must contain the interface and editable map.' }
$commands = @("OBJ LOAD PACKAGE=`"$MapId-i`" FILE=`"$interface`"", "MAP LOAD FILE=`"$map`"", 'MAP NEW', "MAP LOAD FILE=`"$map`"")
if ($Rebuild) {
    $rebuilt = Join-Path $testPackages "MapsEd/$MapId-rebuilt.sdc"
    $commands += @('MAP REBUILD', "MAP SAVE FILE=`"$rebuilt`"", 'MAP NEW', "MAP LOAD FILE=`"$rebuilt`"")
}
$nativeText = Join-Path $testSystem 'native-loaded.t3d'
if ($ExpectedGroups.Count) { $commands += "MAP EXPORT FILE=`"$nativeText`"" }
$commands |
    Set-Content -LiteralPath (Join-Path $testSystem 'package-load-commands.txt') -Encoding ascii
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ tools are required.' }
$compile = Join-Path $testSystem 'compile.cmd'
@"
@call "$vs\Common7\Tools\VsDevCmd.bat" -arch=x86 -host_arch=x64 >nul
@if errorlevel 1 exit /b 1
@cl /nologo /W4 /EHsc /std:c++20 /MT /O2 "$PSScriptRoot\native\Driver.cpp" /Fe:"$testSystem\PackageLoadDriver.exe" /Fo:"$testSystem\Driver.obj" /link user32.lib
@if errorlevel 1 exit /b 1
@cl /nologo /W4 /EHsc /std:c++20 /MT /O2 /LD "$PSScriptRoot\native\PackageLoadProbe.cpp" /Fe:"$testSystem\PackageLoadProbe.dll" /Fo:"$testSystem\Probe.obj" /link user32.lib
@exit /b %errorlevel%
"@ | Set-Content -LiteralPath $compile -Encoding ascii
& cmd.exe /d /c $compile
if ($LASTEXITCODE) { throw 'Native test compilation failed.' }
Add-Type -Namespace NativePackageCheck -Name Ini -MemberDefinition @'
[DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
public static extern bool WritePrivateProfileString(string section,string key,string value,string file);
'@
$config = Join-Path $testSystem 'Reloaded_Editor.ini'
[NativePackageCheck.Ini]::WritePrivateProfileString('Updates','CheckOnStartup','0',$config) | Out-Null
[NativePackageCheck.Ini]::WritePrivateProfileString('CrashRecovery','OfferAtStartup','0',$config) | Out-Null
$executable = Join-Path $testSystem 'ChaosTheory_Editor.exe'
$launched = & (Join-Path $testSystem 'PackageLoadDriver.exe') $executable (Join-Path $testSystem 'Reloaded.Editor.dll') (Join-Path $testSystem 'PackageLoadProbe.dll')
if ($LASTEXITCODE) { throw 'Native editor launch failed.' }
$ownedId = [int]($launched | Select-Object -Last 1)
Write-Output "TestRoot=$Out"
Write-Output "EditorPid=$ownedId"
try {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $report = Join-Path $testSystem 'package-load-report.txt'
    $shown = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $report) {
            $lines = @(Get-Content -LiteralPath $report)
            if ($lines.Count -gt $shown) { $lines[$shown..($lines.Count-1)] | Write-Output; $shown = $lines.Count }
            if ($lines -match '^FAIL ') { throw 'Native package load failed.' }
            if ($lines -match '^PASS ') {
                if ($ExpectedGroups.Count) {
                    $text = Get-Content -LiteralPath $nativeText -Raw
                    $visible = [regex]::Match($text,'(?m)^\s*VisibleGroups=(.*)$').Groups[1].Value
                    foreach ($group in $ExpectedGroups) {
                        if (!$visible.Contains($group)) { throw "Native editor disabled group $group." }
                    }
                    if ($text -match 'bHiddenEdGroup=True') { throw 'Native editor hid grouped actors.' }
                    Write-Output 'PASS Native groups remain enabled after load/rebuild/save/reopen.'
                }
                return
            }
        }
        if (!(Get-Process -Id $ownedId -ErrorAction SilentlyContinue)) { throw 'Native editor exited before completing the package test.' }
        Start-Sleep -Milliseconds 500
    }
    throw 'Native package load timed out.'
} finally {
    $owned = Get-Process -Id $ownedId -ErrorAction SilentlyContinue
    if ($owned -and $owned.Path -eq $executable) { Stop-Process -Id $ownedId -Force }
}
