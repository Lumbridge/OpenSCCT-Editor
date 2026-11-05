<#
.SYNOPSIS
Checks out the OpenSCCT commit this editor is built against (openscct.ref) in ..\OpenSCCT-Editor-deps.

.DESCRIPTION
The editor uses OpenSCCT's libraries and its map converter. openscct.ref names the exact commit, always one on
OpenSCCT's public main. This script keeps a detached worktree of your OpenSCCT clone at that commit beside this repo,
so the editor never depends on which branch the clone happens to be on. The build (Directory.Build.props) and the
editor at run time both use that worktree when it exists.

It never touches the clone's working tree. A deps worktree with local changes is left alone and reported.

.PARAMETER OpenScctGit
The OpenSCCT clone whose repository holds the commit. Default: OSE_OPENSCCT_GIT, else ..\OpenSCCT next to this repo,
else the repository an existing ..\OpenSCCT-Editor-deps worktree belongs to.
#>
param(
    [string]$OpenScctGit = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$parent = Split-Path -Parent $repo
$deps = Join-Path $parent 'OpenSCCT-Editor-deps'
$ref = (Get-Content -Raw (Join-Path $repo 'openscct.ref')).Trim()

if (-not $OpenScctGit) { $OpenScctGit = $env:OSE_OPENSCCT_GIT }
if (-not $OpenScctGit) {
    $OpenScctGit = Join-Path $parent 'OpenSCCT'
    # A deps worktree made from a clone kept elsewhere still knows that clone.
    if (-not (Test-Path (Join-Path $OpenScctGit '.git')) -and (Test-Path (Join-Path $deps '.git'))) {
        $common = & git -C $deps rev-parse --path-format=absolute --git-common-dir 2>$null
        if ($LASTEXITCODE -eq 0 -and $common) { $OpenScctGit = Split-Path -Parent ([string]$common).Trim() }
    }
}
if (-not (Test-Path (Join-Path $OpenScctGit '.git'))) {
    throw "No OpenSCCT clone at $OpenScctGit. Clone https://github.com/Lumbridge/OpenSCCT there, or set OSE_OPENSCCT_GIT."
}

function Invoke-Git([string]$dir) { & git.exe -C $dir @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed in $dir" } }

# Make sure the commit is known locally; fetch once if not.
& git -C $OpenScctGit cat-file -e "$ref^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Fetching OpenSCCT to find $ref..."
    Invoke-Git $OpenScctGit fetch --quiet origin
    & git -C $OpenScctGit cat-file -e "$ref^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        # A shallow or single-branch clone may only get it by name.
        & git -C $OpenScctGit fetch --quiet origin $ref 2>$null
        & git -C $OpenScctGit cat-file -e "$ref^{commit}" 2>$null
    }
    if ($LASTEXITCODE -ne 0) { throw "OpenSCCT commit $ref (openscct.ref) is not in $OpenScctGit or its origin." }
}

if (-not (Test-Path (Join-Path $deps 'OpenSCCT.slnx'))) {
    Write-Host "Creating $deps at $($ref.Substring(0, 10))..."
    Invoke-Git $OpenScctGit worktree add --detach $deps $ref
    return
}

$head = (& git -C $deps rev-parse HEAD).Trim()
if ($head -eq $ref) { return }

if (& git -C $deps status --porcelain --untracked-files=no) {
    Write-Warning "$deps has local changes; leaving it at $($head.Substring(0, 10)) instead of $($ref.Substring(0, 10))."
    return
}

Write-Host "Moving $deps to $($ref.Substring(0, 10))..."
Invoke-Git $deps checkout --quiet --detach $ref
