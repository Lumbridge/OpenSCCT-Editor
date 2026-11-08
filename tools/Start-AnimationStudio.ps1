param(
    [string]$OpenScctRoot = (Join-Path $PSScriptRoot '../../SVM-animation-studio'),
    [string]$Godot = $env:OSE_GODOT,
    [string]$Content = $env:OSE_CONTENT,
    [string]$Project = '',
    [ValidateSet('', 'Spy', 'Merc')][string]$FirstPerson = '',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$editorRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = (Resolve-Path -LiteralPath $OpenScctRoot).Path
if (!(Test-Path -LiteralPath (Join-Path $runtimeRoot 'src/OpenScct.Animation/OpenScct.Animation.csproj'))) {
    throw 'Choose an OpenSCCT checkout containing the Animation Studio runtime.'
}
$commonGit = (& git -C $runtimeRoot rev-parse --path-format=absolute --git-common-dir).Trim()
$sourceRoot = Split-Path -Parent $commonGit
if (!$Godot) {
    $Godot = (Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'tools/godot') -Recurse -Filter '*mono_win64.exe' | Select-Object -First 1).FullName
}
if (!$Godot -or !(Test-Path -LiteralPath $Godot)) { throw 'Pass -Godot <Godot 4.7.2 .NET executable> or set OSE_GODOT.' }
if (!$Content -and (Test-Path -LiteralPath (Join-Path $sourceRoot '.content'))) { $Content = Join-Path $sourceRoot '.content' }
if ($Content) { $env:OSE_CONTENT = (Resolve-Path -LiteralPath $Content).Path }
$env:OSE_OPENSCCT = $runtimeRoot
if (!$NoBuild) {
    & dotnet build (Join-Path $runtimeRoot 'game/OpenScctGame.csproj') --nologo -v:q
    if ($LASTEXITCODE) { throw 'OpenSCCT build failed.' }
    & dotnet build (Join-Path $editorRoot 'app/OpenScctEditor.csproj') "-p:OpenScctRoot=$runtimeRoot" --nologo -v:q
    if ($LASTEXITCODE) { throw 'Editor build failed.' }
    foreach ($projectFolder in @((Join-Path $runtimeRoot 'game'), (Join-Path $editorRoot 'app'))) {
        & $Godot --headless --editor --path $projectFolder --import --quit
        if ($LASTEXITCODE) { throw "Godot import failed in $projectFolder." }
    }
}
$studioArgs = @('--path', (Join-Path $editorRoot 'app'), '--')
if ($Project) { $studioArgs += '--animation=' + (Resolve-Path -LiteralPath $Project).Path }
elseif (!$FirstPerson) { $studioArgs += '--animation-demo' }
if ($FirstPerson) { $studioArgs += '--animation-first-person=' + $FirstPerson.ToLowerInvariant() }
& $Godot @studioArgs
