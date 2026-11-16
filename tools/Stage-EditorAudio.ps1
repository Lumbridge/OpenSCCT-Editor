param([Parameter(Mandatory)][string]$OpenScctRoot, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$source = Join-Path $OpenScctRoot 'game/addons/scct_audio'
if (!(Test-Path (Join-Path $source 'bin/windows-x86_64/scct_audio.dll'))) {
    & (Join-Path $OpenScctRoot 'tools/audio/Build-NativeAudio.ps1')
    if ($LASTEXITCODE) { throw 'The shared game audio extension did not build.' }
}
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.Name -ne '.gitignore' }) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $target = Join-Path $Destination $relative
    # Godot loads the extension before invoking MSBuild during export. Identical
    # DLLs must be left alone because that process has them mapped into memory.
    if ((Test-Path -LiteralPath $target) -and
        (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { continue }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
